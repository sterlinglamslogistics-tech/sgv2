using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Controllers;

// No class-level [AllowAnonymous] — each action declares its own policy
public class PosController : Controller
{
    private const string RegisterCookie = "till_register";

    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;
    private readonly SignInManager<ApplicationUser> _signIn;
    private readonly IPasswordHasher<ApplicationUser> _hasher;
    private readonly UserManager<ApplicationUser> _userManager;

    private readonly SterlingLams.Web.Services.IStoreAccessService _access;
    private readonly SterlingLams.Web.Services.ILoyaltyService _loyalty;

    private readonly SterlingLams.Web.Services.IAuditService _audit;
    private readonly SterlingLams.Web.Services.ISettingsService _settings;
    private readonly SterlingLams.Web.Services.IEmailService _email;
    private readonly SterlingLams.Web.Services.IOrderNumberService _orderNumbers;

    public PosController(ApplicationDbContext db, IStockService stock,
        SignInManager<ApplicationUser> signIn, IPasswordHasher<ApplicationUser> hasher,
        UserManager<ApplicationUser> userManager,
        SterlingLams.Web.Services.IStoreAccessService access,
        SterlingLams.Web.Services.ILoyaltyService loyalty,
        SterlingLams.Web.Services.IAuditService audit,
        SterlingLams.Web.Services.ISettingsService settings,
        SterlingLams.Web.Services.IEmailService email,
        SterlingLams.Web.Services.IOrderNumberService orderNumbers)
    {
        _db = db;
        _stock = stock;
        _signIn = signIn;
        _hasher = hasher;
        _userManager = userManager;
        _access = access;
        _loyalty = loyalty;
        _audit = audit;
        _settings = settings;
        _email = email;
        _orderNumbers = orderNumbers;
    }

    // POS card/receipt thumbnail: rewrite a Cloudinary upload URL to a small, cacheable variant so
    // offline image-caching stays light. Non-Cloudinary URLs are returned unchanged.
    private static string? PosThumb(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        const string marker = "/image/upload/";
        var i = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return url;
        var at = i + marker.Length;
        return url[..at] + "f_auto,q_auto,w_240,h_240,c_fill/" + url[at..];
    }

    // Per-(product, variant) available stock at a store, for the POS grid + variant picker.
    private sealed record InvRow(int ProductId, int? VariantId, int Avail);
    private async Task<List<InvRow>> StoreInvAsync(int storeId, List<int> productIds) =>
        (await _db.StoreInventories
            .Where(si => si.StoreId == storeId && productIds.Contains(si.ProductId))
            .Select(si => new { si.ProductId, si.ProductVariantId, Avail = si.QuantityOnHand - si.QuantityReserved })
            .ToListAsync())
        .Select(r => new InvRow(r.ProductId, r.ProductVariantId, r.Avail)).ToList();

    // Product total = sum of every row (shared pool + each variant's own row), each clamped at 0.
    private static int ProdAvail(List<InvRow> inv, int pid) =>
        inv.Where(i => i.ProductId == pid).Sum(i => Math.Max(0, i.Avail));

    // Variant available = its own row if it has one, else the shared product-pool row (the same
    // fallback StockService uses to sell), so the picker number matches what checkout will allow.
    private static int VarAvail(List<InvRow> inv, int pid, int vid)
    {
        var own = inv.FirstOrDefault(i => i.ProductId == pid && i.VariantId == vid);
        if (own != null) return Math.Max(0, own.Avail);
        var pool = inv.FirstOrDefault(i => i.ProductId == pid && i.VariantId == null);
        return Math.Max(0, pool?.Avail ?? 0);
    }

    private async Task<Register?> BoundRegisterAsync()
    {
        if (int.TryParse(Request.Cookies[RegisterCookie], out var id))
            return await _db.Registers.Include(r => r.Store)
                .FirstOrDefaultAsync(r => r.Id == id && r.IsActive);
        return null;
    }

    private Task<TillSession?> OpenSessionAsync(int registerId) =>
        _db.TillSessions.FirstOrDefaultAsync(s => s.RegisterId == registerId && s.ClosedAt == null);

    [AllowAnonymous]
    public async Task<IActionResult> Index()
    {
        var register = await BoundRegisterAsync();
        if (register == null)
        {
            var registers = await _db.Registers.Where(r => r.IsActive)
                .Include(r => r.Store).OrderBy(r => r.Name).ToListAsync();
            return View("PickRegister", registers);
        }

        ViewData["Register"] = register;

        if (!(User.Identity?.IsAuthenticated ?? false))
        {
            var cashiers = await _db.Users.Where(u => u.PinHash != null)
                .OrderBy(u => u.FirstName)
                .Select(u => new TillCashier { Id = u.Id, Name = (u.FirstName + " " + u.LastName).Trim() })
                .ToListAsync();
            return View("Login", cashiers);
        }

        var session = await OpenSessionAsync(register.Id);
        if (session == null) return View("OpenTill", register);

        // Pass display name so Sell view shows first name rather than email
        var uid = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var cashier = uid != null
            ? await _db.Users.Where(u => u.Id == uid)
                .Select(u => (u.FirstName + " " + u.LastName).Trim())
                .FirstOrDefaultAsync()
            : null;
        ViewData["CashierName"] = string.IsNullOrWhiteSpace(cashier) ? User.Identity?.Name : cashier;
        ViewData["Session"] = session;
        return View("Sell", register);
    }

    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> OpenSession(decimal openingFloat)
    {
        var register = await BoundRegisterAsync();
        if (register == null) return RedirectToAction(nameof(Index));
        if (!await _access.CanWriteAsync(User, register.StoreId))
        {
            TempData["Error"] = "You're not assigned to this branch's POS.";
            return RedirectToAction(nameof(Index));
        }
        if (await OpenSessionAsync(register.Id) == null)
        {
            _db.TillSessions.Add(new TillSession
            {
                RegisterId = register.Id,
                OpenedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "",
                OpenedAt = DateTime.UtcNow,
                OpeningFloat = Math.Max(0, openingFloat)
            });
            await _db.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Index));
    }

    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CloseSession(decimal countedCash, string? note)
    {
        var register = await BoundRegisterAsync();
        if (register == null) return Json(new { success = false, message = "POS not set up." });
        var session = await OpenSessionAsync(register.Id);
        if (session == null) return Json(new { success = false, message = "No open POS session." });

        session.ClosedAt = DateTime.UtcNow;
        session.ClosedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        session.CountedCash = countedCash;
        session.ClosingNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        await _db.SaveChangesAsync();
        return Json(new { success = true, sessionId = session.Id });
    }

    // ── Cash in / out (pay-in, pay-out, float top-up during a shift) ──────────
    public class CashMovementRequest
    {
        public decimal Amount { get; set; }
        public string Direction { get; set; } = "out"; // "in" | "out"
        public string? Reason { get; set; }
    }

    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CashInOut([FromBody] CashMovementRequest req)
    {
        var register = await BoundRegisterAsync();
        if (register == null) return Json(new { success = false, message = "This POS isn't set up. Pick a register." });
        var session = await OpenSessionAsync(register.Id);
        if (session == null) return Json(new { success = false, message = "Open the POS before recording cash." });

        var amount = Math.Abs(req.Amount);
        if (amount <= 0) return Json(new { success = false, message = "Enter an amount." });
        var isIn = string.Equals(req.Direction, "in", StringComparison.OrdinalIgnoreCase);
        var signed = isIn ? amount : -amount;

        _db.CashMovements.Add(new CashMovement
        {
            TillSessionId = session.Id,
            RegisterId = register.Id,
            UserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "",
            Amount = signed,
            Reason = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim(),
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        try { await _audit.LogAsync("CashMovement", "TillSession", session.Id.ToString(), $"Cash {(isIn ? "in" : "out")} ₦{amount:N0} at {register.Name}{(string.IsNullOrWhiteSpace(req.Reason) ? "" : $" — {req.Reason!.Trim()}")}"); } catch { }
        return Json(new { success = true });
    }

    [Authorize, HttpGet]
    public async Task<IActionResult> CashMovements()
    {
        var register = await BoundRegisterAsync();
        if (register == null) return Json(Array.Empty<object>());
        var session = await OpenSessionAsync(register.Id);
        if (session == null) return Json(Array.Empty<object>());
        var rows = await _db.CashMovements.Where(m => m.TillSessionId == session.Id)
            .OrderByDescending(m => m.Id)
            .Select(m => new { amount = m.Amount, reason = m.Reason, createdAt = m.CreatedAt })
            .ToListAsync();
        return Json(rows);
    }

    public class ZreportVm
    {
        public TillSession Session { get; set; } = null!;
        public int SaleCount { get; set; }
        public decimal TotalSales { get; set; }
        public decimal CashSales { get; set; }
        public decimal CardSales { get; set; }
        public decimal TransferSales { get; set; }
        public decimal RefundsTotal { get; set; }
        public decimal CashRefunds { get; set; }
        public decimal CashIn { get; set; }
        public decimal CashOut { get; set; }
        public decimal ExpectedCash { get; set; }
    }

    [Authorize]
    public async Task<IActionResult> Zreport(int id)
    {
        var session = await _db.TillSessions
            .Include(s => s.Register).ThenInclude(r => r.Store)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (session == null) return NotFound();
        return View(await BuildZreportAsync(session));
    }

    // Mid-shift "X-report": the same cash/sales read for the CURRENT open session on this register,
    // without closing it. Counted-cash / variance are hidden (nothing's been counted yet).
    [Authorize]
    public async Task<IActionResult> Xreport()
    {
        var register = await BoundRegisterAsync();
        if (register == null) return RedirectToAction(nameof(Index));
        var session = await _db.TillSessions
            .Include(s => s.Register).ThenInclude(r => r.Store)
            .FirstOrDefaultAsync(s => s.RegisterId == register.Id && s.ClosedAt == null);
        if (session == null) return RedirectToAction(nameof(Index));
        ViewBag.Interim = true;
        return View("Zreport", await BuildZreportAsync(session));
    }

    private async Task<ZreportVm> BuildZreportAsync(TillSession session)
    {
        var sales = await _db.Orders.Where(o => o.TillSessionId == session.Id)
            .Select(o => new { o.Id, o.Total, o.PaymentProvider }).ToListAsync();
        var saleIds = sales.Select(s => s.Id).ToList();
        var payments = await _db.OrderPayments.Where(p => saleIds.Contains(p.OrderId))
            .Select(p => new { p.OrderId, p.Method, p.Amount }).ToListAsync();
        var withRows = payments.Select(p => p.OrderId).ToHashSet();

        // Per-method total: split-aware via OrderPayment rows; legacy orders (no rows) fall back to
        // PaymentProvider × Total.
        decimal SumOf(string m) =>
            payments.Where(p => p.Method == m).Sum(p => p.Amount)
            + sales.Where(o => !withRows.Contains(o.Id) && o.PaymentProvider == m).Sum(o => o.Total);
        var cash = SumOf("Cash");

        var refunds = await _db.Refunds.Where(r => r.TillSessionId == session.Id).ToListAsync();
        var cashRefunds = refunds.Where(r => r.RefundMethod == "Cash").Sum(r => r.Amount);

        // Cash drops/top-ups during the shift (pay-in positive, pay-out negative) move the drawer.
        var cashMovements = await _db.CashMovements.Where(m => m.TillSessionId == session.Id).ToListAsync();
        var cashIn = cashMovements.Where(m => m.Amount > 0).Sum(m => m.Amount);
        var cashOut = cashMovements.Where(m => m.Amount < 0).Sum(m => -m.Amount);

        return new ZreportVm
        {
            Session = session,
            SaleCount = sales.Count,
            TotalSales = sales.Sum(o => o.Total),
            CashSales = cash,
            CardSales = SumOf("Card"),
            TransferSales = SumOf("Transfer"),
            RefundsTotal = refunds.Sum(r => r.Amount),
            CashRefunds = cashRefunds,
            CashIn = cashIn,
            CashOut = cashOut,
            ExpectedCash = session.OpeningFloat + cash + cashIn - cashOut - cashRefunds
        };
    }

    // ── Refunds / returns ─────────────────────────────────────────────────────
    [Authorize, HttpGet]
    public async Task<IActionResult> RefundLookup(string orderNumber)
    {
        orderNumber = (orderNumber ?? "").Trim();
        var order = await _db.Orders.Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber && o.Channel == OrderChannel.Pos);
        if (order == null) return Json(new { found = false });

        var refundIds = _db.Refunds.Where(r => r.OriginalOrderId == order.Id).Select(r => r.Id);
        var refunded = await _db.RefundItems.Where(ri => refundIds.Contains(ri.RefundId))
            .GroupBy(ri => new { ri.ProductId, ri.ProductVariantId })
            .Select(g => new { g.Key.ProductId, g.Key.ProductVariantId, Qty = g.Sum(x => x.Quantity) })
            .ToListAsync();
        int Done(int pid, int? vid) => refunded.FirstOrDefault(r => r.ProductId == pid && r.ProductVariantId == vid)?.Qty ?? 0;

        var items = order.Items.Select(i => new
        {
            productId = i.ProductId,
            variantId = i.ProductVariantId,
            name = i.ProductName,
            variantName = i.VariantName,
            unitPrice = i.UnitPrice,
            sold = i.Quantity,
            refundable = i.Quantity - Done(i.ProductId, i.ProductVariantId)
        }).ToList();

        return Json(new { found = true, orderId = order.Id, orderNumber = order.OrderNumber, total = order.Total, items });
    }

    public class RefundLine { public int ProductId { get; set; } public int? VariantId { get; set; } public int Quantity { get; set; } }
    public class RefundRequest
    {
        public int OrderId { get; set; }
        public string Method { get; set; } = "Cash";
        public string? Reason { get; set; }
        public List<RefundLine> Items { get; set; } = new();
    }

    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RefundProcess([FromBody] RefundRequest req)
    {
        var register = await BoundRegisterAsync();
        var order = await _db.Orders.Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == req.OrderId && o.Channel == OrderChannel.Pos);
        if (order == null) return Json(new { success = false, message = "Sale not found." });

        var lines = (req.Items ?? new()).Where(l => l.Quantity > 0).ToList();
        if (lines.Count == 0) return Json(new { success = false, message = "Choose at least one item to return." });

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var now = DateTime.UtcNow;
        var session = register != null ? await OpenSessionAsync(register.Id) : null;
        var storeId = order.PickupStoreId ?? register?.StoreId ?? 0;
        var refundNumber = $"REF-{now:yyMMdd}-{now:HHmmssfff}";

        await using var tx = await _db.Database.BeginTransactionAsync();

        // Lock this order's row so two concurrent refund requests for the same sale
        // serialize: the second sees the first's refund rows before computing "already
        // refunded" quantities below, instead of both reading zero and double-refunding.
        if (_db.Database.IsNpgsql()) // FOR UPDATE is Postgres-only (SQLite test harness no-ops)
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"Orders\" WHERE \"Id\" = {order.Id} FOR UPDATE");

        var refundIds = _db.Refunds.Where(r => r.OriginalOrderId == order.Id).Select(r => r.Id);
        var refunded = await _db.RefundItems.Where(ri => refundIds.Contains(ri.RefundId))
            .GroupBy(ri => new { ri.ProductId, ri.ProductVariantId })
            .Select(g => new { g.Key.ProductId, g.Key.ProductVariantId, Qty = g.Sum(x => x.Quantity) })
            .ToListAsync();
        int Done(int pid, int? vid) => refunded.FirstOrDefault(r => r.ProductId == pid && r.ProductVariantId == vid)?.Qty ?? 0;

        var refund = new Refund
        {
            RefundNumber = refundNumber,
            OriginalOrderId = order.Id,
            RegisterId = register?.Id,
            TillSessionId = session?.Id,
            CashierUserId = userId,
            RefundMethod = req.Method,
            Reason = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim(),
            CreatedAt = now
        };

        decimal amount = 0;
        foreach (var l in lines)
        {
            var oi = order.Items.FirstOrDefault(i => i.ProductId == l.ProductId && i.ProductVariantId == l.VariantId);
            if (oi == null) continue;
            var qty = Math.Min(l.Quantity, oi.Quantity - Done(l.ProductId, l.VariantId));
            if (qty <= 0) continue;

            amount += oi.UnitPrice * qty;
            refund.Items.Add(new RefundItem
            {
                ProductId = oi.ProductId, ProductVariantId = oi.ProductVariantId,
                ProductName = oi.ProductName, VariantName = oi.VariantName,
                Quantity = qty, UnitPrice = oi.UnitPrice
            });
            if (storeId > 0)
                await _stock.ApplyAsync(oi.ProductId, oi.ProductVariantId, storeId, qty,
                    StockMovementType.Return, refundNumber, userId: userId);
        }

        if (refund.Items.Count == 0) return Json(new { success = false, message = "Nothing left to refund on this sale." });

        refund.Amount = amount;
        _db.Refunds.Add(refund);

        // Whole sale refunded (every line's prior + this refund covers the qty sold)?
        bool fullyRefunded = order.Items.All(oi =>
            Done(oi.ProductId, oi.ProductVariantId)
                + refund.Items.Where(ri => ri.ProductId == oi.ProductId && ri.ProductVariantId == oi.ProductVariantId)
                              .Sum(ri => ri.Quantity)
            >= oi.Quantity);

        try
        {
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Json(new { success = false, message = "Stock levels changed while processing this refund. Please try again." });
        }

        // On a full refund, claw back any loyalty points the attached customer earned on this sale.
        if (fullyRefunded)
            await _loyalty.ReverseForOrderAsync(order.Id);

        try { await _audit.LogAsync("Refund", "Order", order.Id.ToString(), $"POS refund {refundNumber} — ₦{amount:N0} on {order.OrderNumber}{(fullyRefunded ? " (full)" : " (partial)")}"); } catch { }

        return Json(new { success = true, refundNumber, amount });
    }

    [AllowAnonymous, HttpPost, ValidateAntiForgeryToken]
    public IActionResult SetRegister(int registerId)
    {
        Response.Cookies.Append(RegisterCookie, registerId.ToString(),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), HttpOnly = true, IsEssential = true });
        return RedirectToAction(nameof(Index));
    }

    // Moving the till to another register/branch is gated behind an admin PIN, so a cashier can't
    // re-point the till on their own. The PIN entered must match an Admin who has a POS PIN set.
    // Rate-limited (per-IP) so the admin PIN can't be brute-forced.
    [Authorize, HttpPost, ValidateAntiForgeryToken]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth")]
    public async Task<IActionResult> ChangeRegister(string? pin)
    {
        pin = (pin ?? "").Trim();
        if (pin.Length == 0)
            return Json(new { success = false, message = "Enter an admin PIN." });

        var admins = await _userManager.GetUsersInRoleAsync("Admin");
        var authorized = admins.Any(u => u.PinHash != null &&
            _hasher.VerifyHashedPassword(u, u.PinHash, pin) != PasswordVerificationResult.Failed);
        if (!authorized)
            return Json(new { success = false, message = "Invalid admin PIN." });

        Response.Cookies.Delete(RegisterCookie);
        return Json(new { success = true });
    }

    // PIN sign-in bypasses Identity's lockout (manual hash verify), so rate-limit it per-IP to stop
    // brute-forcing a 4–8 digit cashier PIN (the login page exposes cashier user ids).
    [AllowAnonymous, HttpPost, ValidateAntiForgeryToken]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth")]
    public async Task<IActionResult> Login(string userId, string pin)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.PinHash != null);
        if (user != null && !string.IsNullOrEmpty(pin) &&
            _hasher.VerifyHashedPassword(user, user.PinHash!, pin) != PasswordVerificationResult.Failed)
        {
            await _signIn.SignInAsync(user, isPersistent: false);
            var reg = await BoundRegisterAsync();
            try { await _audit.LogAsync("Login", "POS", user.Id, $"{user.FullName} signed in to POS{(reg != null ? $" ({reg.Name})" : "")}"); } catch { }
            return Json(new { success = true });
        }
        return Json(new { success = false, message = "Wrong PIN." });
    }

    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _signIn.SignOutAsync();
        return RedirectToAction(nameof(Index));
    }

    // ── Discount reasons (configurable from admin) ────────────────────────────
    [Authorize, HttpGet]
    public async Task<IActionResult> DiscountReasons()
    {
        var reasons = await _db.PosDiscountReasons
            .Where(r => r.IsActive)
            .Include(r => r.Presets.OrderBy(p => p.SortOrder))
            .OrderBy(r => r.SortOrder)
            .Select(r => new
            {
                id = r.Id,
                name = r.Name,
                presets = r.Presets.Select(p => new { id = p.Id, label = p.Label, type = p.Type, value = p.Value })
            })
            .ToListAsync();
        return Json(reasons);
    }

    // ── Recent orders for this register (Orders tab) ──────────────────────────
    [Authorize, HttpGet]
    public async Task<IActionResult> RecentOrders()
    {
        var register = await BoundRegisterAsync();
        if (register == null) return Json(Array.Empty<object>());

        var orders = await _db.Orders
            .Where(o => o.Channel == OrderChannel.Pos && o.RegisterId == register.Id)
            .OrderByDescending(o => o.CreatedAt)
            .Take(25)
            .Select(o => new
            {
                id = o.Id,
                orderNumber = o.OrderNumber,
                total = o.Total,
                method = o.PaymentProvider,
                createdAt = o.CreatedAt,
                itemCount = o.Items.Sum(i => i.Quantity),
                customerName = o.Customer != null
                    ? (o.Customer.FirstName + " " + o.Customer.LastName).Trim()
                    : null,
                // On POS, Order.UserId is the cashier who rang up the sale.
                cashierName = o.User != null
                    ? (o.User.FirstName + " " + o.User.LastName).Trim()
                    : null
            })
            .ToListAsync();
        return Json(orders);
    }

    // ── Customers (attach a buyer to a POS sale) ──────────────────────────────
    [Authorize, HttpGet]
    public async Task<IActionResult> CustomerSearch(string? q)
    {
        q = (q ?? "").Trim();

        // Empty query → list existing customers (POS-created buyers + anyone attached to an order),
        // newest first, so the Customers tab shows saved customers without having to search.
        if (q.Length < 1)
        {
            var recent = await _db.Users
                .Where(u => u.UserName!.StartsWith("pos-")
                         || _db.Orders.Any(o => o.CustomerUserId == u.Id))
                .OrderByDescending(u => u.CreatedAt)
                .Take(20)
                .Select(u => new { id = u.Id, name = (u.FirstName + " " + u.LastName).Trim(), phone = u.PhoneNumber })
                .ToListAsync();
            return Json(recent);
        }

        var matches = await _db.Users
            .Where(u => EF.Functions.ILike(u.FirstName + " " + u.LastName, $"%{q}%")
                     || EF.Functions.ILike(u.PhoneNumber ?? "", $"%{q}%")
                     || EF.Functions.ILike(u.Email ?? "", $"%{q}%"))
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
            .Take(15)
            .Select(u => new { id = u.Id, name = (u.FirstName + " " + u.LastName).Trim(), phone = u.PhoneNumber })
            .ToListAsync();
        return Json(matches);
    }

    public class QuickAddCustomer
    {
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public string Phone { get; set; } = "";
        public string? Email { get; set; }
    }

    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CustomerQuickAdd([FromBody] QuickAddCustomer req)
    {
        var first = (req.FirstName ?? "").Trim();
        var last = (req.LastName ?? "").Trim();
        var phone = (req.Phone ?? "").Trim();
        var email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim();

        if (first.Length == 0 && last.Length == 0)
            return Json(new { success = false, message = "Enter a name." });
        if (phone.Length == 0 && email == null)
            return Json(new { success = false, message = "Enter a phone number." });

        // If a user with this email already exists, reuse it instead of creating a second account.
        // (Creating a new shell here is what produced duplicate-email accounts that broke login.)
        if (email != null)
        {
            var existing = await _db.Users
                .FirstOrDefaultAsync(u => u.NormalizedEmail == _userManager.NormalizeEmail(email));
            if (existing != null)
                return Json(new { success = true, id = existing.Id, name = existing.FullName, phone = existing.PhoneNumber, reused = true });
        }

        // POS customers are phone-first and don't log in: no password. UserName must be unique.
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        var userName = email ?? (digits.Length > 0 ? $"pos-{digits}" : $"pos-{Guid.NewGuid():N}");
        if (await _userManager.FindByNameAsync(userName) != null)
            userName = $"pos-{Guid.NewGuid():N}";

        var user = new ApplicationUser
        {
            UserName = userName,
            Email = email,
            EmailConfirmed = email != null,
            FirstName = first,
            LastName = last,
            PhoneNumber = phone.Length > 0 ? phone : null,
            CreatedAt = DateTime.UtcNow
        };
        var result = await _userManager.CreateAsync(user);
        if (!result.Succeeded)
            return Json(new { success = false, message = string.Join(" ", result.Errors.Select(e => e.Description)) });

        return Json(new { success = true, id = user.Id, name = user.FullName, phone = user.PhoneNumber });
    }

    private async Task<string?> ResolveCustomerIdAsync(string? customerUserId)
    {
        if (string.IsNullOrWhiteSpace(customerUserId)) return null;
        return await _db.Users.AnyAsync(u => u.Id == customerUserId) ? customerUserId : null;
    }

    // ── Hold / recall parked sales ────────────────────────────────────────────
    public class HoldRequest
    {
        public string? CustomerUserId { get; set; }
        public string? Label { get; set; }
        public List<HoldLine> Items { get; set; } = new();
    }
    public class HoldLine
    {
        public string? Key { get; set; }
        public int ProductId { get; set; }
        public int? VariantId { get; set; }
        public string? Sku { get; set; }
        public string Name { get; set; } = "";
        public string? VariantName { get; set; }
        public decimal UnitPrice { get; set; }
        public int Qty { get; set; }
        public int Stock { get; set; }
        public string? Note { get; set; }
        public decimal DiscountAmount { get; set; }
        public string? DiscountReason { get; set; }
        public string? DiscountType { get; set; }
    }

    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Hold([FromBody] HoldRequest req)
    {
        var register = await BoundRegisterAsync();
        if (register == null) return Json(new { success = false, message = "This POS isn't set up. Pick a register." });
        if (req.Items == null || req.Items.Count == 0) return Json(new { success = false, message = "Nothing to hold." });

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var cashierName = (await _db.Users.Where(u => u.Id == userId)
            .Select(u => (u.FirstName + " " + u.LastName).Trim()).FirstOrDefaultAsync()) ?? "";

        var customerId = await ResolveCustomerIdAsync(req.CustomerUserId);
        string? customerName = customerId == null ? null : await _db.Users
            .Where(u => u.Id == customerId).Select(u => (u.FirstName + " " + u.LastName).Trim()).FirstOrDefaultAsync();

        var parked = new ParkedSale
        {
            RegisterId = register.Id,
            StoreId = register.StoreId,
            CashierUserId = userId,
            CashierName = cashierName,
            CustomerUserId = customerId,
            CustomerName = customerName,
            Label = string.IsNullOrWhiteSpace(req.Label) ? null : req.Label.Trim(),
            ItemCount = req.Items.Sum(i => Math.Max(1, i.Qty)),
            Total = req.Items.Sum(i => i.UnitPrice * Math.Max(1, i.Qty) - Math.Max(0, i.DiscountAmount)),
            // camelCase so the JSON round-trips back into the till's JS cart shape (qty, unitPrice, …).
            CartJson = JsonSerializer.Serialize(req.Items,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            CreatedAt = DateTime.UtcNow
        };
        _db.ParkedSales.Add(parked);
        await _db.SaveChangesAsync();
        return Json(new { success = true, id = parked.Id });
    }

    [Authorize, HttpGet]
    public async Task<IActionResult> ParkedSales()
    {
        var register = await BoundRegisterAsync();
        if (register == null) return Json(Array.Empty<object>());

        var held = await _db.ParkedSales
            .Where(p => p.StoreId == register.StoreId)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new
            {
                id = p.Id,
                label = p.Label,
                customerName = p.CustomerName,
                cashierName = p.CashierName,
                itemCount = p.ItemCount,
                total = p.Total,
                createdAt = p.CreatedAt
            })
            .ToListAsync();
        return Json(held);
    }

    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RecallParkedSale(int id)
    {
        var register = await BoundRegisterAsync();
        if (register == null) return Json(new { success = false });

        var parked = await _db.ParkedSales
            .FirstOrDefaultAsync(p => p.Id == id && p.StoreId == register.StoreId);
        if (parked == null) return Json(new { success = false, message = "That held sale is no longer available." });

        var payload = new
        {
            success = true,
            cart = parked.CartJson,
            customerUserId = parked.CustomerUserId,
            customerName = parked.CustomerName
        };
        // Recall moves the sale back into the active cart, so it leaves the held list.
        _db.ParkedSales.Remove(parked);
        await _db.SaveChangesAsync();
        return Json(payload);
    }

    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteParkedSale(int id)
    {
        var register = await BoundRegisterAsync();
        if (register == null) return Json(new { success = false });

        var parked = await _db.ParkedSales
            .FirstOrDefaultAsync(p => p.Id == id && p.StoreId == register.StoreId);
        if (parked != null)
        {
            _db.ParkedSales.Remove(parked);
            await _db.SaveChangesAsync();
        }
        return Json(new { success = true });
    }

    // ── Offline snapshot ──────────────────────────────────────────────────────
    // One bulk payload the PWA caches in IndexedDB so the till can search/scan/build a cart with no
    // network. Shapes mirror Search/Categories/DiscountReasons/CustomerSearch exactly, so the
    // client-side fetch shim can serve identical responses offline. Stock is the bound store's
    // AVAILABLE quantity (on-hand − reserved), same as Search.
    [Authorize, HttpGet]
    public async Task<IActionResult> Snapshot()
    {
        var register = await BoundRegisterAsync();
        if (register == null) return Json(new { ok = false, message = "No register bound to this POS." });
        var storeId = register.StoreId;
        var storeName = (await _db.Stores.Where(s => s.Id == storeId).Select(s => s.Name).FirstOrDefaultAsync());

        var categories = await _db.Categories.Where(c => c.IsActive)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new { id = c.Id, name = c.Name, imageUrl = c.ImageUrl })
            .ToListAsync();

        var discountReasons = await _db.PosDiscountReasons.Where(r => r.IsActive)
            .Include(r => r.Presets.OrderBy(p => p.SortOrder)).OrderBy(r => r.SortOrder)
            .Select(r => new
            {
                id = r.Id, name = r.Name,
                presets = r.Presets.Select(p => new { id = p.Id, label = p.Label, type = p.Type, value = p.Value })
            })
            .ToListAsync();

        var products = await _db.Products.Where(p => p.IsActive && !p.HiddenFromPos)
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                id = p.Id,
                name = p.Name,
                sku = p.Sku,
                barcode = p.Barcode,
                categoryId = p.CategoryId,
                price = p.SalePrice != null && p.SalePrice > 0 && p.SalePrice < p.Price ? p.SalePrice.Value : p.Price,
                image = p.Images.Where(i => i.IsPrimary).Select(i => i.Url).FirstOrDefault()
                        ?? p.Images.Select(i => i.Url).FirstOrDefault(),
                variants = p.ProductType == "variable"
                    ? p.Variants.Where(v => v.IsActive)
                        .Select(v => new { id = v.Id, name = v.Name, priceAdjustment = v.PriceAdjustment }).ToList()
                    : null
            })
            .ToListAsync();
        var snapInv = await StoreInvAsync(storeId, products.Select(p => p.id).ToList());

        // Same population rule as CustomerSearch's empty-query list (POS buyers + anyone on an order).
        var customers = await _db.Users
            .Where(u => u.UserName!.StartsWith("pos-") || _db.Orders.Any(o => o.CustomerUserId == u.Id))
            .OrderByDescending(u => u.CreatedAt)
            .Select(u => new { id = u.Id, name = (u.FirstName + " " + u.LastName).Trim(), phone = u.PhoneNumber, email = u.Email })
            .ToListAsync();

        // Receipt branding (so an offline receipt matches the server-rendered one).
        var siteName = await _settings.GetAsync("general.site_name", "Sterlin Glams");
        var logoUrl = await _settings.GetAsync("general.logo_url", "");
        if (string.IsNullOrWhiteSpace(logoUrl)) logoUrl = "/images/sg-logo.png";
        var receiptHeader = await _settings.GetAsync("pos.receipt_header", "");
        var receiptFooter = await _settings.GetAsync("pos.receipt_footer", "Thank you for shopping with us!");
        var uid = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var cashierName = uid != null
            ? await _db.Users.Where(u => u.Id == uid).Select(u => (u.FirstName + " " + u.LastName).Trim()).FirstOrDefaultAsync()
            : null;

        return Json(new
        {
            ok = true,
            storeId,
            storeName,
            registerName = register.Name,
            cashierName,
            siteName,
            logoUrl,
            receiptHeader,
            receiptFooter,
            syncedAt = DateTime.UtcNow,
            categories,
            discountReasons,
            products = products.Select(p => new
            {
                p.id, p.name, p.sku, p.barcode, p.categoryId, p.price, image = PosThumb(p.image),
                stock = ProdAvail(snapInv, p.id),
                variants = p.variants?.Select(v => new { v.id, v.name, v.priceAdjustment, stock = VarAvail(snapInv, p.id, v.id) })
            }),
            customers
        });
    }

    // ── Categories ────────────────────────────────────────────────────────────
    [Authorize, HttpGet]
    public async Task<IActionResult> Categories()
    {
        var cats = await _db.Categories.Where(c => c.IsActive)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new { id = c.Id, name = c.Name, imageUrl = c.ImageUrl })
            .ToListAsync();
        return Json(cats);
    }

    // ── Product search for the bound register's store ─────────────────────────
    [Authorize, HttpGet]
    public async Task<IActionResult> Search(string? q, int? categoryId)
    {
        var register = await BoundRegisterAsync();
        if (register == null) return Json(Array.Empty<object>());
        var storeId = register.StoreId;
        q = (q ?? "").Trim();

        var query = _db.Products.Where(p => p.IsActive && !p.HiddenFromPos);
        if (categoryId.HasValue)
            query = query.Where(p => p.CategoryId == categoryId.Value);
        if (q.Length > 0)
            query = query.Where(p => EF.Functions.ILike(p.Name, $"%{q}%")
                                  || EF.Functions.ILike(p.Sku ?? "", $"%{q}%")
                                  || EF.Functions.ILike(p.Barcode ?? "", $"%{q}%"));

        var products = await query.OrderBy(p => p.Name).Take(40)
            .Select(p => new
            {
                id = p.Id,
                name = p.Name,
                sku = p.Sku,
                barcode = p.Barcode,
                price = p.SalePrice != null && p.SalePrice > 0 && p.SalePrice < p.Price ? p.SalePrice.Value : p.Price,
                image = p.Images.Where(i => i.IsPrimary).Select(i => i.Url).FirstOrDefault()
                        ?? p.Images.Select(i => i.Url).FirstOrDefault(),
                variants = p.ProductType == "variable"
                    ? p.Variants.Where(v => v.IsActive)
                        .Select(v => new { id = v.Id, name = v.Name, priceAdjustment = v.PriceAdjustment }).ToList()
                    : null
            })
            .ToListAsync();
        // AVAILABLE (on-hand − reserved) per product and per variant, so the till + variant picker
        // numbers match what checkout will actually allow.
        var inv = await StoreInvAsync(storeId, products.Select(p => p.id).ToList());
        return Json(products.Select(p => new
        {
            p.id, p.name, p.sku, p.barcode, p.price, image = PosThumb(p.image),
            stock = ProdAvail(inv, p.id),
            variants = p.variants?.Select(v => new { v.id, v.name, v.priceAdjustment, stock = VarAvail(inv, p.id, v.id) })
        }));
    }

    // ── Stock lookup: list of stores for the location filter ──────────────────
    [Authorize, HttpGet]
    public async Task<IActionResult> StockLookupStores()
    {
        var stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name)
            .Select(s => new { id = s.Id, name = s.Name }).ToListAsync();
        return Json(stores);
    }

    // ── Stock lookup: search any product/variant and see stock across stores ──
    // Returns ONE row per (branch) for simple products and per (branch, active variant) for variable
    // products — never the raw per-inventory-row list (which duplicated branches). `period` drives the
    // Qty-sold column (today / week / month / all).
    [Authorize, HttpGet]
    public async Task<IActionResult> StockLookup(string? q, string? period)
    {
        q = (q ?? "").Trim();
        if (q.Length < 2) return Json(Array.Empty<object>());

        DateTime? since = (period ?? "today").ToLowerInvariant() switch
        {
            "all" => null,
            "week" => DateTime.UtcNow.Date.AddDays(-7),
            "month" => DateTime.UtcNow.Date.AddDays(-30),
            _ => DateTime.UtcNow.Date,
        };
        if (since.HasValue) since = DateTime.SpecifyKind(since.Value, DateTimeKind.Utc);

        var products = await _db.Products.Where(p => p.IsActive && !p.HiddenFromPos)
            .Where(p => EF.Functions.ILike(p.Name, $"%{q}%")
                     || EF.Functions.ILike(p.Sku ?? "", $"%{q}%")
                     || EF.Functions.ILike(p.Barcode ?? "", $"%{q}%")
                     || p.Variants.Any(v => EF.Functions.ILike(v.Sku ?? "", $"%{q}%") || EF.Functions.ILike(v.Barcode ?? "", $"%{q}%")))
            .OrderBy(p => p.Name).Take(25)
            .Select(p => new
            {
                id = p.Id,
                name = p.Name,
                sku = p.Sku,
                barcode = p.Barcode,
                description = p.ShortDescription,
                category = p.Category.Name,
                price = p.SalePrice != null && p.SalePrice > 0 && p.SalePrice < p.Price ? p.SalePrice.Value : p.Price,
                inv = p.StoreInventories.Select(si => new { si.StoreId, store = si.Store.Name, si.ProductVariantId, si.QuantityOnHand, si.QuantityReserved }).ToList(),
                variants = p.Variants.Where(v => v.IsActive).OrderBy(v => v.Name).Select(v => new { id = v.Id, name = v.Name }).ToList()
            })
            .ToListAsync();

        // Units sold per (product, variant, branch) over the chosen period — POS sales only.
        var ids = products.Select(p => p.id).ToList();
        var soldQ = _db.OrderItems.Where(oi => ids.Contains(oi.ProductId)
            && oi.Order.Channel == OrderChannel.Pos
            && oi.Order.Status != OrderStatus.Cancelled && oi.Order.Status != OrderStatus.Refunded);
        if (since.HasValue) soldQ = soldQ.Where(oi => oi.Order.CreatedAt >= since.Value);
        var soldRows = await soldQ
            .Select(oi => new { oi.ProductId, oi.ProductVariantId, oi.Order.PickupStoreId, oi.Quantity })
            .ToListAsync();
        int Sold(int pid, int? vid, int sid) => soldRows
            .Where(s => s.ProductId == pid && s.PickupStoreId == sid && (vid == null || s.ProductVariantId == vid))
            .Sum(s => s.Quantity);

        var result = products.Select(p =>
        {
            var stores = p.inv.Select(i => new { i.StoreId, i.store }).Distinct().OrderBy(s => s.store).ToList();
            var rows = new List<object>();
            if (p.variants.Count > 0)
            {
                foreach (var s in stores)
                    foreach (var v in p.variants)
                    {
                        var cells = p.inv.Where(i => i.StoreId == s.StoreId && i.ProductVariantId == v.id).ToList();
                        rows.Add(new
                        {
                            location = s.store,
                            variant = v.name,
                            inStock = cells.Sum(c => c.QuantityOnHand),
                            onHold = cells.Sum(c => c.QuantityReserved),
                            qtySold = Sold(p.id, v.id, s.StoreId)
                        });
                    }
            }
            else
            {
                foreach (var s in stores)
                {
                    var cells = p.inv.Where(i => i.StoreId == s.StoreId).ToList();
                    rows.Add(new
                    {
                        location = s.store,
                        variant = (string?)null,
                        inStock = cells.Sum(c => c.QuantityOnHand),
                        onHold = cells.Sum(c => c.QuantityReserved),
                        qtySold = Sold(p.id, null, s.StoreId)
                    });
                }
            }
            return new { p.id, p.name, p.sku, p.barcode, p.description, p.category, p.price, rows };
        }).ToList();

        return Json(result);
    }

    public class PosVisibilityRequest { public int ProductId { get; set; } public bool Hidden { get; set; } }

    // Staff "Mark unavailable" from the product popup — hides a product from the POS only (the
    // storefront listing is unaffected). Reversible.
    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPosVisibility([FromBody] PosVisibilityRequest req)
    {
        var register = await BoundRegisterAsync();
        if (register == null) return Json(new { success = false, message = "This POS isn't set up. Pick a register." });
        if (!await _access.CanWriteAsync(User, register.StoreId))
            return Json(new { success = false, message = "You're not assigned to this branch's POS." });

        var product = await _db.Products.FirstOrDefaultAsync(p => p.Id == req.ProductId);
        if (product == null) return Json(new { success = false, message = "Product not found." });

        product.HiddenFromPos = req.Hidden;
        product.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        try { await _audit.LogAsync("Update", "Product", product.Id.ToString(), req.Hidden ? $"Marked '{product.Name}' unavailable on POS" : $"Marked '{product.Name}' available on POS"); } catch { }
        return Json(new { success = true, hidden = product.HiddenFromPos });
    }

    // ── Store-pickup QR verification (scan the customer's pass at the till) ──────
    [Authorize, HttpGet]
    public async Task<IActionResult> PickupVerify(string? code)
    {
        var register = await BoundRegisterAsync();
        if (register == null) return Json(new { success = false, message = "This POS isn't set up. Pick a register." });
        code = (code ?? "").Trim();
        if (code.StartsWith("SGPICK-", StringComparison.OrdinalIgnoreCase)) code = code[7..];
        if (code.Length == 0) return Json(new { success = false, message = "No code scanned." });

        var order = await _db.Orders
            .Include(o => o.Items).Include(o => o.PickupStore).Include(o => o.User).Include(o => o.Customer)
            .FirstOrDefaultAsync(o => o.PickupToken == code);
        if (order == null) return Json(new { success = false, message = "No pickup order found for this code." });

        return Json(new
        {
            success = true,
            token = order.PickupToken,
            orderNumber = order.OrderNumber,
            status = order.Status.ToString(),
            ready = order.Status == OrderStatus.ReadyForPickup,
            collected = order.Status == OrderStatus.Collected,
            total = order.Total,
            customer = order.User != null ? order.User.FullName : (order.Customer != null ? order.Customer.FullName : null),
            pickupStore = order.PickupStore != null ? order.PickupStore.Name : null,
            sameStore = order.PickupStoreId == register.StoreId,
            items = order.Items.Select(i => new { name = i.ProductName, variant = i.VariantName, qty = i.Quantity, lineTotal = i.LineTotal })
        });
    }

    public class PickupCompleteRequest { public string Token { get; set; } = ""; }

    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> PickupComplete([FromBody] PickupCompleteRequest req)
    {
        var register = await BoundRegisterAsync();
        if (register == null) return Json(new { success = false, message = "This POS isn't set up. Pick a register." });
        if (!await _access.CanWriteAsync(User, register.StoreId))
            return Json(new { success = false, message = "You're not assigned to this branch's POS." });

        var token = (req.Token ?? "").Trim();
        if (token.StartsWith("SGPICK-", StringComparison.OrdinalIgnoreCase)) token = token[7..];
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.PickupToken == token);
        if (order == null) return Json(new { success = false, message = "Pickup order not found." });
        if (order.Status == OrderStatus.Collected) return Json(new { success = false, message = "This order was already collected." });
        if (order.Status != OrderStatus.ReadyForPickup) return Json(new { success = false, message = $"Order is {order.Status} — not ready for pickup." });

        order.Status = OrderStatus.Collected;
        order.UpdatedAt = DateTime.UtcNow;
        OrderNotes.AddSystem(_db, order.Id, $"Collected at {register.Name} (QR verified).");
        await _db.SaveChangesAsync();
        try { await _audit.LogAsync("Update", "Order", order.Id.ToString(), $"Pickup collected {order.OrderNumber} at {register.Name}"); } catch { }
        return Json(new { success = true, orderNumber = order.OrderNumber });
    }

    public class TillLine
    {
        public int ProductId { get; set; }
        public int? VariantId { get; set; }
        public int Quantity { get; set; }
        public string? Note { get; set; }
        public decimal DiscountAmount { get; set; }
        public string? DiscountReason { get; set; }
        public string? DiscountType { get; set; }
        /// <summary>One-off / un-barcoded line: carries its own Name + UnitPrice and skips stock.</summary>
        public bool Custom { get; set; }
        public string? Name { get; set; }
        public decimal? UnitPrice { get; set; }
    }

    // The single hidden product POS custom (one-off) lines hang off, created on first use.
    private async Task<Product> GetCustomItemProductAsync()
    {
        var p = await _db.Products.FirstOrDefaultAsync(x => x.IsCustomItem);
        if (p != null) return p;
        var catId = await _db.Categories.OrderBy(c => c.Id).Select(c => c.Id).FirstAsync();
        p = new Product
        {
            Name = "Custom item", Slug = "__pos-custom-item", ExternalCode = "POS-CUSTOM",
            ProductType = "simple", Price = 0, IsActive = false, HiddenFromPos = true, IsCustomItem = true,
            CategoryId = catId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        _db.Products.Add(p);
        await _db.SaveChangesAsync();
        return p;
    }
    public class PaymentPart { public string Method { get; set; } = "Cash"; public decimal Amount { get; set; } }
    public class TillCheckout
    {
        public string PaymentMethod { get; set; } = "Cash";
        public decimal AmountTendered { get; set; }
        public string? CustomerUserId { get; set; }
        public List<TillLine> Items { get; set; } = new();
        /// <summary>Optional split/mixed tenders. When present, each part's Amount is the money applied
        /// to the sale via that method; the parts must cover the total. Single-method sales omit this.</summary>
        public List<PaymentPart>? Payments { get; set; }
    }
    public class TillCashier { public string Id { get; set; } = ""; public string Name { get; set; } = ""; }

    // Builds the per-tender rows for a POS order. Cash change is deducted from the cash row so the
    // recorded cash equals what stays in the drawer (keeps cash-up accurate). Returns the rows plus
    // the resolved PaymentProvider label, amount tendered and change.
    private static (List<OrderPayment> rows, string provider, decimal tendered, decimal change)
        BuildPayments(List<PaymentPart>? parts, string fallbackMethod, decimal fallbackTendered, decimal total)
    {
        var clean = (parts ?? new()).Where(p => p.Amount > 0 && !string.IsNullOrWhiteSpace(p.Method))
            .Select(p => new PaymentPart { Method = p.Method.Trim(), Amount = p.Amount }).ToList();

        if (clean.Count == 0)
        {
            // Legacy single-method path: one row for the whole total.
            var tendered = fallbackTendered > 0 ? fallbackTendered : total;
            var change = Math.Max(0, tendered - total);
            return (new List<OrderPayment> { new() { Method = fallbackMethod, Amount = total } },
                    fallbackMethod, tendered, change);
        }

        var paid = clean.Sum(p => p.Amount);
        var chg = Math.Max(0, paid - total);
        // Change comes out of cash: reduce the cash row so summed amounts == total.
        if (chg > 0)
        {
            var cash = clean.FirstOrDefault(p => string.Equals(p.Method, "Cash", StringComparison.OrdinalIgnoreCase));
            if (cash != null) cash.Amount = Math.Max(0, cash.Amount - chg);
        }
        var rows = clean.Where(p => p.Amount > 0).Select(p => new OrderPayment { Method = p.Method, Amount = p.Amount }).ToList();
        var provider = rows.Count == 1 ? rows[0].Method : "Split";
        return (rows, provider, paid, chg);
    }

    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Checkout([FromBody] TillCheckout req)
    {
        var register = await BoundRegisterAsync();
        if (register == null) return Json(new { success = false, message = "This POS isn't set up. Pick a register." });
        if (!await _access.CanWriteAsync(User, register.StoreId))
            return Json(new { success = false, message = "You're not assigned to this branch's POS." });
        if (req.Items == null || req.Items.Count == 0) return Json(new { success = false, message = "Cart is empty." });

        var session = await OpenSessionAsync(register.Id);
        if (session == null) return Json(new { success = false, message = "Open the POS before selling." });

        // Customer is mandatory on POS sales — attach the buyer before taking payment.
        var customerId = await ResolveCustomerIdAsync(req.CustomerUserId);
        if (customerId == null)
            return Json(new { success = false, message = "Add the customer's details before taking payment." });

        var storeId = register.StoreId;

        // Resolve one-off "quick item" lines onto the hidden custom product (stock isn't tracked).
        if (req.Items.Any(i => i.Custom))
        {
            var customProduct = await GetCustomItemProductAsync();
            foreach (var i in req.Items.Where(i => i.Custom)) { i.ProductId = customProduct.Id; i.VariantId = null; }
        }

        var productIds = req.Items.Select(i => i.ProductId).Distinct().ToList();
        var products = await _db.Products.Include(p => p.Variants)
            .Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        foreach (var grp in req.Items.GroupBy(i => new { i.ProductId, i.VariantId }))
        {
            if (!products.TryGetValue(grp.Key.ProductId, out var prod))
                return Json(new { success = false, message = "A product in the cart no longer exists." });
            if (prod.IsCustomItem) continue; // one-off line — no stock to check
            var requested = grp.Sum(i => Math.Max(1, i.Quantity));
            // Sell against AVAILABLE (on-hand − reserved), not raw on-hand — otherwise the till can
            // sell units already held for a pending online order, which then can't be fulfilled.
            if (requested > await _stock.GetAvailableAsync(grp.Key.ProductId, grp.Key.VariantId, storeId))
                return Json(new { success = false, message = $"Not enough available stock for '{prod.Name}'." });
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var now = DateTime.UtcNow;
        // Random suffix (in addition to millisecond precision) so two checkouts landing in the
        // same millisecond — plausible with multiple registers under load — don't collide on
        // the unique OrderNumber index.
        var orderNumber = await _orderNumbers.NextAsync(OrderChannel.Pos);

        await using var tx = await _db.Database.BeginTransactionAsync();

        // Lock the relevant StoreInventory rows (fixed ascending ProductId order, to avoid
        // deadlocking against a concurrent checkout/transfer touching an overlapping set of
        // products), then re-check availability against the now-locked, up-to-date balances
        // before mutating anything — closes the check-then-act window from the pre-check above.
        if (_db.Database.IsNpgsql()) // FOR UPDATE is Postgres-only (SQLite test harness no-ops)
            foreach (var pid in productIds.OrderBy(id => id))
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"StoreInventories\" WHERE \"ProductId\" = {pid} AND \"StoreId\" = {storeId} FOR UPDATE");

        foreach (var grp in req.Items.GroupBy(i => new { i.ProductId, i.VariantId }))
        {
            var prod = products[grp.Key.ProductId];
            if (prod.IsCustomItem) continue; // one-off line — no stock to check
            var requested = grp.Sum(i => Math.Max(1, i.Quantity));
            // Re-check against available under the row lock (closes the check-then-act window).
            if (requested > await _stock.GetAvailableAsync(grp.Key.ProductId, grp.Key.VariantId, storeId))
                return Json(new { success = false, message = $"Not enough available stock for '{prod.Name}'." });
        }

        var order = new Order
        {
            OrderNumber = orderNumber,
            Channel = OrderChannel.Pos,
            FulfillmentType = FulfillmentType.StorePickup,
            PickupStoreId = storeId,
            RegisterId = register.Id,
            TillSessionId = session.Id,
            UserId = userId,
            CustomerUserId = customerId,
            Status = OrderStatus.Delivered,
            IsPaid = true,
            PaidAt = now,
            PaymentProvider = req.PaymentMethod,
            CreatedAt = now,
            UpdatedAt = now
        };

        decimal subtotal = 0;
        decimal totalDiscount = 0;
        foreach (var line in req.Items)
        {
            var prod = products[line.ProductId];
            var qty = Math.Max(1, line.Quantity);
            var isCustom = prod.IsCustomItem;
            var variant = (!isCustom && line.VariantId.HasValue) ? prod.Variants.FirstOrDefault(v => v.Id == line.VariantId) : null;
            // Custom (one-off) lines carry their own name + price; everything else uses the catalog.
            var unitPrice = isCustom ? Math.Max(0, line.UnitPrice ?? 0) : prod.EffectivePrice + (variant?.PriceAdjustment ?? 0);
            var lineName = isCustom ? (string.IsNullOrWhiteSpace(line.Name) ? "Custom item" : line.Name.Trim()) : prod.Name;
            var lineDiscount = Math.Max(0, Math.Min(line.DiscountAmount, unitPrice * qty));
            subtotal += unitPrice * qty;
            totalDiscount += lineDiscount;

            order.Items.Add(new OrderItem
            {
                ProductId = prod.Id,
                ProductVariantId = variant?.Id,
                ProductName = lineName,
                VariantName = variant?.Name,
                ProductSku = prod.Sku,
                Quantity = qty,
                UnitPrice = unitPrice,
                DiscountAmount = lineDiscount,
                DiscountReason = string.IsNullOrWhiteSpace(line.DiscountReason) ? null : line.DiscountReason.Trim(),
                DiscountType = string.IsNullOrWhiteSpace(line.DiscountType) ? null : line.DiscountType.Trim(),
                ItemNote = string.IsNullOrWhiteSpace(line.Note) ? null : line.Note.Trim()
            });
            if (!isCustom)
                await _stock.ApplyAsync(prod.Id, variant?.Id, storeId, -qty, StockMovementType.Sale, orderNumber, userId: userId);
        }

        order.Subtotal = subtotal;
        order.DiscountAmount = totalDiscount;
        order.Total = subtotal - totalDiscount;

        // Resolve tenders (single or split). A split must cover the total — returning here rolls
        // back the uncommitted stock deductions above (tx is disposed without a commit).
        if (req.Payments != null && req.Payments.Any(p => p.Amount > 0)
            && req.Payments.Where(p => p.Amount > 0).Sum(p => p.Amount) + 0.01m < order.Total)
            return Json(new { success = false, message = "Payments don't cover the total." });

        var (payRows, provider, tendered, change) = BuildPayments(req.Payments, req.PaymentMethod, req.AmountTendered, order.Total);
        order.PaymentProvider = provider;
        order.AmountTendered = tendered;
        order.ChangeGiven = change;

        _db.Orders.Add(order);
        try
        {
            await _db.SaveChangesAsync();
            foreach (var pr in payRows) { pr.OrderId = order.Id; _db.OrderPayments.Add(pr); }
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (InsufficientStockException ex)
        {
            var prodName = products.TryGetValue(ex.ProductId, out var p) ? p.Name : $"product {ex.ProductId}";
            return Json(new { success = false, message = $"Not enough stock for '{prodName}'." });
        }
        catch (DbUpdateConcurrencyException)
        {
            return Json(new { success = false, message = "Stock levels changed while processing this sale. Please try again." });
        }
        catch (DbUpdateException)
        {
            return Json(new { success = false, message = "Could not complete this sale. Please try again." });
        }

        // Award loyalty points to the attached customer (no-op for walk-ins with no customer).
        await _loyalty.AccrueForOrderAsync(order.Id);

        try { await _audit.LogAsync("Sale", "Order", order.Id.ToString(), $"POS sale {orderNumber} — ₦{order.Total:N0} ({req.PaymentMethod}) at {register.Name}"); } catch { }

        return Json(new { success = true, orderId = order.Id, orderNumber, total = order.Total, change = order.ChangeGiven });
    }

    // ── Offline sale sync ─────────────────────────────────────────────────────
    public class OfflineSale
    {
        public string ClientId { get; set; } = "";       // GUID generated on the device (idempotency key)
        public string PaymentMethod { get; set; } = "Cash";
        public decimal AmountTendered { get; set; }
        public string? CustomerUserId { get; set; }
        public DateTime? CreatedAt { get; set; }          // when the sale was rung up offline (client UTC)
        public List<TillLine> Items { get; set; } = new();
    }
    public class SyncSalesRequest { public List<OfflineSale> Sales { get; set; } = new(); }

    /// <summary>Ingests POS sales that were completed OFFLINE. Idempotent by OfflineClientId, so the
    /// device can safely re-send. Stock is committed first-come-first-served under a row lock; if a
    /// line can't be fully covered (sold elsewhere while offline) the deduction is clamped to zero
    /// and the order is flagged for staff to reconcile (the locked "best-effort" policy).</summary>
    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncSales([FromBody] SyncSalesRequest req)
    {
        var register = await BoundRegisterAsync();
        if (register == null) return Json(new { success = false, message = "This POS isn't set up. Pick a register." });
        if (!await _access.CanWriteAsync(User, register.StoreId))
            return Json(new { success = false, message = "You're not assigned to this branch's POS." });

        var results = new List<object>();
        if (req?.Sales == null || req.Sales.Count == 0) return Json(new { success = true, results });

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        foreach (var sale in req.Sales)
        {
            if (string.IsNullOrWhiteSpace(sale.ClientId))
            { results.Add(new { clientId = sale.ClientId, success = false, message = "Missing client id." }); continue; }

            // Idempotency: this offline sale was already synced — return its order, do nothing else.
            var existing = await _db.Orders.Where(o => o.OfflineClientId == sale.ClientId)
                .Select(o => new { o.Id, o.OrderNumber }).FirstOrDefaultAsync();
            if (existing != null)
            { results.Add(new { clientId = sale.ClientId, success = true, duplicate = true, orderId = existing.Id, orderNumber = existing.OrderNumber }); continue; }

            if (sale.Items == null || sale.Items.Count == 0)
            { results.Add(new { clientId = sale.ClientId, success = false, message = "Empty sale." }); continue; }

            try { results.Add(await IngestOfflineSaleAsync(sale, register, userId)); }
            catch (Exception ex)
            {
                _db.ChangeTracker.Clear();
                results.Add(new { clientId = sale.ClientId, success = false, message = ex.Message });
            }
        }
        return Json(new { success = true, results });
    }

    private async Task<object> IngestOfflineSaleAsync(OfflineSale sale, Register register, string userId)
    {
        var storeId = register.StoreId;
        var soldAt = sale.CreatedAt ?? DateTime.UtcNow;
        if (soldAt.Kind != DateTimeKind.Utc) soldAt = DateTime.SpecifyKind(soldAt, DateTimeKind.Utc);
        var now = DateTime.UtcNow;
        var session = await OpenSessionAsync(register.Id);
        var customerId = await ResolveCustomerIdAsync(sale.CustomerUserId);

        var productIds = sale.Items.Select(i => i.ProductId).Distinct().ToList();
        var products = await _db.Products.Include(p => p.Variants)
            .Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        var orderNumber = await _orderNumbers.NextAsync(OrderChannel.Pos);
        var shortfalls = new List<string>();

        await using var tx = await _db.Database.BeginTransactionAsync();
        if (_db.Database.IsNpgsql())
            foreach (var pid in productIds.OrderBy(id => id))
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"StoreInventories\" WHERE \"ProductId\" = {pid} AND \"StoreId\" = {storeId} FOR UPDATE");

        var order = new Order
        {
            OrderNumber = orderNumber,
            OfflineClientId = sale.ClientId,
            Channel = OrderChannel.Pos,
            FulfillmentType = FulfillmentType.StorePickup,
            PickupStoreId = storeId,
            RegisterId = register.Id,
            TillSessionId = session?.Id,
            UserId = userId,
            CustomerUserId = customerId,
            Status = OrderStatus.Delivered,
            IsPaid = true,
            PaidAt = soldAt,
            PaymentProvider = sale.PaymentMethod,
            CreatedAt = soldAt,
            UpdatedAt = now
        };

        decimal subtotal = 0, totalDiscount = 0;
        foreach (var line in sale.Items)
        {
            if (!products.TryGetValue(line.ProductId, out var prod)) { shortfalls.Add($"#{line.ProductId} (removed)"); continue; }
            var qty = Math.Max(1, line.Quantity);
            var variant = line.VariantId.HasValue ? prod.Variants.FirstOrDefault(v => v.Id == line.VariantId) : null;
            var unitPrice = prod.EffectivePrice + (variant?.PriceAdjustment ?? 0);
            var lineDiscount = Math.Max(0, Math.Min(line.DiscountAmount, unitPrice * qty));
            subtotal += unitPrice * qty;
            totalDiscount += lineDiscount;

            order.Items.Add(new OrderItem
            {
                ProductId = prod.Id, ProductVariantId = variant?.Id,
                ProductName = prod.Name, VariantName = variant?.Name, ProductSku = prod.Sku,
                Quantity = qty, UnitPrice = unitPrice, DiscountAmount = lineDiscount,
                DiscountReason = string.IsNullOrWhiteSpace(line.DiscountReason) ? null : line.DiscountReason.Trim(),
                DiscountType = string.IsNullOrWhiteSpace(line.DiscountType) ? null : line.DiscountType.Trim(),
                ItemNote = string.IsNullOrWhiteSpace(line.Note) ? null : line.Note.Trim()
            });

            // Best-effort stock: deduct what's available; clamp the rest and flag (don't go negative).
            var available = await _stock.GetAvailableAsync(prod.Id, variant?.Id, storeId);
            var deduct = Math.Min(qty, Math.Max(0, available));
            if (deduct > 0)
                await _stock.ApplyAsync(prod.Id, variant?.Id, storeId, -deduct, StockMovementType.Sale, orderNumber, userId: userId);
            if (deduct < qty)
                shortfalls.Add($"{prod.Name}{(variant != null ? " – " + variant.Name : "")} (short {qty - deduct})");
        }

        order.Subtotal = subtotal;
        order.DiscountAmount = totalDiscount;
        order.Total = subtotal - totalDiscount;
        order.AmountTendered = sale.AmountTendered > 0 ? sale.AmountTendered : order.Total;
        order.ChangeGiven = Math.Max(0, (order.AmountTendered ?? order.Total) - order.Total);
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        // Record the tender (offline sales are single-method) so the Z-report sums it like any other.
        _db.OrderPayments.Add(new OrderPayment { OrderId = order.Id, Method = sale.PaymentMethod, Amount = order.Total });

        OrderNotes.AddSystem(_db, order.Id,
            $"Recorded offline at {soldAt:yyyy-MM-dd HH:mm} (cashier's till), synced {now:yyyy-MM-dd HH:mm}.");
        if (shortfalls.Count > 0)
        {
            var msg = "Oversold while offline — not enough stock for: " + string.Join(", ", shortfalls) + ". Reconcile manually.";
            order.AdminNotes = msg;
            OrderNotes.AddSystem(_db, order.Id, msg);
        }
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        await _loyalty.AccrueForOrderAsync(order.Id);
        try { await _audit.LogAsync("Sale", "Order", order.Id.ToString(), $"Offline POS sale synced {orderNumber} — ₦{order.Total:N0} ({sale.PaymentMethod}) at {register.Name}"); } catch { }

        return new { clientId = sale.ClientId, success = true, orderId = order.Id, orderNumber, total = order.Total, change = order.ChangeGiven, oversold = shortfalls.Count > 0 };
    }

    [Authorize]
    public async Task<IActionResult> Receipt(int id)
    {
        var order = await _db.Orders.Include(o => o.Items).Include(o => o.PickupStore)
            .Include(o => o.Customer).Include(o => o.User)
            .FirstOrDefaultAsync(o => o.Id == id && o.Channel == OrderChannel.Pos);
        if (order == null) return NotFound();
        // Split-payment breakdown for the receipt (empty for single-tender sales).
        ViewBag.Payments = await _db.OrderPayments.Where(p => p.OrderId == id)
            .OrderBy(p => p.Id).Select(p => new KeyValuePair<string, decimal>(p.Method, p.Amount)).ToListAsync();
        return View(order);
    }

    public class EmailReceiptRequest { public int OrderId { get; set; } public string? Email { get; set; } }

    // Email a POS receipt to the buyer. Uses the typed address if given, else the attached
    // customer's email. Branded HTML is built here and wrapped by the email service shell.
    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EmailReceipt([FromBody] EmailReceiptRequest req)
    {
        var order = await _db.Orders.Include(o => o.Items).Include(o => o.PickupStore).Include(o => o.Customer)
            .FirstOrDefaultAsync(o => o.Id == req.OrderId && o.Channel == OrderChannel.Pos);
        if (order == null) return Json(new { success = false, message = "Sale not found." });

        var to = (req.Email ?? "").Trim();
        if (to.Length == 0) to = order.Customer?.Email ?? "";
        if (to.Length == 0) return Json(new { success = false, message = "No email on file — type one." });

        // Persist a typed-in email onto a customer that doesn't have one, so future receipts/marketing reach them.
        if (!string.IsNullOrWhiteSpace(req.Email) && order.Customer != null && string.IsNullOrWhiteSpace(order.Customer.Email))
        {
            var normalized = _userManager.NormalizeEmail(to);
            var clash = await _db.Users.AnyAsync(u => u.Id != order.Customer.Id && u.NormalizedEmail == normalized);
            if (!clash)
            {
                order.Customer.Email = to;
                order.Customer.NormalizedEmail = normalized;
                await _db.SaveChangesAsync();
            }
        }

        var siteName = await _settings.GetAsync("general.site_name", "Sterlin Glams");
        var footer = await _settings.GetAsync("pos.receipt_footer", "Thank you for shopping with us!");
        string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        var rows = string.Join("", order.Items.Select(i =>
            $"<tr><td style=\"padding:4px 0;border-bottom:1px solid #f0efed;\">{i.Quantity}× {Enc(i.ProductName)}{(i.VariantName != null ? " (" + Enc(i.VariantName) + ")" : "")}</td>" +
            $"<td align=\"right\" style=\"padding:4px 0;border-bottom:1px solid #f0efed;\">₦{i.LineTotal:N0}</td></tr>"));
        var inner = $@"
            <h2 style=""font-size:18px;margin:0 0 4px;"">Receipt {Enc(order.OrderNumber)}</h2>
            <p style=""color:#78716c;margin:0 0 16px;font-size:13px;"">{Enc(order.PickupStore?.Name ?? siteName)} · {order.CreatedAt.ToLocalTime():dd MMM yyyy HH:mm} · {Enc(order.PaymentProvider)}</p>
            <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""font-size:14px;"">{rows}
                <tr><td style=""padding:10px 0 0;font-weight:700;"">TOTAL</td><td align=""right"" style=""padding:10px 0 0;font-weight:700;"">₦{order.Total:N0}</td></tr>
            </table>
            <p style=""color:#78716c;font-size:12px;margin:18px 0 0;white-space:pre-line;"">{Enc(footer)}</p>";

        var sent = await _email.SendAsync(to, $"Your {siteName} receipt — {order.OrderNumber}", inner,
            toName: order.Customer?.FullName);
        if (!sent) return Json(new { success = false, message = "Couldn't send the email. Check the address and try again." });

        try { await _audit.LogAsync("EmailReceipt", "Order", order.Id.ToString(), $"Emailed receipt {order.OrderNumber} to {to}"); } catch { }
        return Json(new { success = true, email = to });
    }
}
