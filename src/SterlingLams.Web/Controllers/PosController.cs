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

    public PosController(ApplicationDbContext db, IStockService stock,
        SignInManager<ApplicationUser> signIn, IPasswordHasher<ApplicationUser> hasher,
        UserManager<ApplicationUser> userManager,
        SterlingLams.Web.Services.IStoreAccessService access,
        SterlingLams.Web.Services.ILoyaltyService loyalty,
        SterlingLams.Web.Services.IAuditService audit)
    {
        _db = db;
        _stock = stock;
        _signIn = signIn;
        _hasher = hasher;
        _userManager = userManager;
        _access = access;
        _loyalty = loyalty;
        _audit = audit;
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
        public decimal ExpectedCash { get; set; }
    }

    [Authorize]
    public async Task<IActionResult> Zreport(int id)
    {
        var session = await _db.TillSessions
            .Include(s => s.Register).ThenInclude(r => r.Store)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (session == null) return NotFound();

        var sales = await _db.Orders.Where(o => o.TillSessionId == id).ToListAsync();
        decimal SumOf(string m) => sales.Where(o => o.PaymentProvider == m).Sum(o => o.Total);
        var cash = SumOf("Cash");

        var refunds = await _db.Refunds.Where(r => r.TillSessionId == id).ToListAsync();
        var cashRefunds = refunds.Where(r => r.RefundMethod == "Cash").Sum(r => r.Amount);

        return View(new ZreportVm
        {
            Session = session,
            SaleCount = sales.Count,
            TotalSales = sales.Sum(o => o.Total),
            CashSales = cash,
            CardSales = SumOf("Card"),
            TransferSales = SumOf("Transfer"),
            RefundsTotal = refunds.Sum(r => r.Amount),
            CashRefunds = cashRefunds,
            ExpectedCash = session.OpeningFloat + cash - cashRefunds
        });
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
    [Authorize, HttpPost, ValidateAntiForgeryToken]
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

    [AllowAnonymous, HttpPost, ValidateAntiForgeryToken]
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

        var query = _db.Products.Where(p => p.IsActive);
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
                // Show AVAILABLE (on-hand − reserved) so the till number matches what can be sold.
                stock = p.StoreInventories.Where(si => si.StoreId == storeId)
                                          .Select(si => si.AvailableQuantity).FirstOrDefault(),
                variants = p.ProductType == "variable"
                    ? p.Variants.Where(v => v.IsActive)
                        .Select(v => new { id = v.Id, name = v.Name, priceAdjustment = v.PriceAdjustment }).ToList()
                    : null
            })
            .ToListAsync();
        return Json(products);
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
    [Authorize, HttpGet]
    public async Task<IActionResult> StockLookup(string? q)
    {
        q = (q ?? "").Trim();
        if (q.Length < 2) return Json(Array.Empty<object>());

        var products = await _db.Products.Where(p => p.IsActive)
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
                price = p.SalePrice != null && p.SalePrice > 0 && p.SalePrice < p.Price ? p.SalePrice.Value : p.Price,
                category = p.Category.Name,
                stores = p.StoreInventories.OrderBy(si => si.Store.Name)
                    .Select(si => new { name = si.Store.Name, onHand = si.QuantityOnHand, reserved = si.QuantityReserved, available = si.AvailableQuantity }).ToList(),
                variants = p.ProductType == "variable"
                    ? p.Variants.Where(v => v.IsActive).OrderBy(v => v.Name)
                        .Select(v => new { id = v.Id, name = v.Name, sku = v.Sku, barcode = v.Barcode }).ToList()
                    : null
            })
            .ToListAsync();
        return Json(products);
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
    }
    public class TillCheckout
    {
        public string PaymentMethod { get; set; } = "Cash";
        public decimal AmountTendered { get; set; }
        public string? CustomerUserId { get; set; }
        public List<TillLine> Items { get; set; } = new();
    }
    public class TillCashier { public string Id { get; set; } = ""; public string Name { get; set; } = ""; }

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
        var productIds = req.Items.Select(i => i.ProductId).Distinct().ToList();
        var products = await _db.Products.Include(p => p.Variants)
            .Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        foreach (var grp in req.Items.GroupBy(i => new { i.ProductId, i.VariantId }))
        {
            if (!products.TryGetValue(grp.Key.ProductId, out var prod))
                return Json(new { success = false, message = "A product in the cart no longer exists." });
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
        var orderNumber = $"POS-{now:yyMMdd}-{now:HHmmssfff}{Random.Shared.Next(100):D2}";

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
            var variant = line.VariantId.HasValue ? prod.Variants.FirstOrDefault(v => v.Id == line.VariantId) : null;
            var unitPrice = prod.EffectivePrice + (variant?.PriceAdjustment ?? 0);
            var lineDiscount = Math.Max(0, Math.Min(line.DiscountAmount, unitPrice * qty));
            subtotal += unitPrice * qty;
            totalDiscount += lineDiscount;

            order.Items.Add(new OrderItem
            {
                ProductId = prod.Id,
                ProductVariantId = variant?.Id,
                ProductName = prod.Name,
                VariantName = variant?.Name,
                ProductSku = prod.Sku,
                Quantity = qty,
                UnitPrice = unitPrice,
                DiscountAmount = lineDiscount,
                DiscountReason = string.IsNullOrWhiteSpace(line.DiscountReason) ? null : line.DiscountReason.Trim(),
                DiscountType = string.IsNullOrWhiteSpace(line.DiscountType) ? null : line.DiscountType.Trim(),
                ItemNote = string.IsNullOrWhiteSpace(line.Note) ? null : line.Note.Trim()
            });
            await _stock.ApplyAsync(prod.Id, variant?.Id, storeId, -qty, StockMovementType.Sale, orderNumber, userId: userId);
        }

        order.Subtotal = subtotal;
        order.DiscountAmount = totalDiscount;
        order.Total = subtotal - totalDiscount;
        order.AmountTendered = req.AmountTendered > 0 ? req.AmountTendered : order.Total;
        order.ChangeGiven = Math.Max(0, (order.AmountTendered ?? order.Total) - order.Total);

        _db.Orders.Add(order);
        try
        {
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

    [Authorize]
    public async Task<IActionResult> Receipt(int id)
    {
        var order = await _db.Orders.Include(o => o.Items).Include(o => o.PickupStore)
            .Include(o => o.Customer).Include(o => o.User)
            .FirstOrDefaultAsync(o => o.Id == id && o.Channel == OrderChannel.Pos);
        if (order == null) return NotFound();
        return View(order);
    }
}
