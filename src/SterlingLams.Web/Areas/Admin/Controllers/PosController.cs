using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers;

public class PosController : AdminBaseController
{
    protected override string Section => "POS";

    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;

    public PosController(ApplicationDbContext db, IStockService stock)
    {
        _db = db;
        _stock = stock;
    }

    // ── POS register screen ───────────────────────────────────────────────────
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Point of Sale";
        var stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        return View(stores);
    }

    // ── Product search (JSON) — only products with stock at the chosen store ──
    [HttpGet]
    public async Task<IActionResult> Search(string? q, int storeId)
    {
        q = (q ?? "").Trim();
        var query = _db.Products.Where(p => p.IsActive);
        if (q.Length > 0)
            query = query.Where(p => EF.Functions.ILike(p.Name, $"%{q}%")
                                  || EF.Functions.ILike(p.Sku ?? "", $"%{q}%"));

        var products = await query
            .OrderBy(p => p.Name)
            .Take(40)
            .Select(p => new
            {
                id = p.Id,
                name = p.Name,
                sku = p.Sku,
                price = p.Price,
                image = p.Images.Where(i => i.IsPrimary).Select(i => i.Url).FirstOrDefault()
                        ?? p.Images.Select(i => i.Url).FirstOrDefault(),
                stock = p.StoreInventories.Where(si => si.StoreId == storeId)
                                          .Select(si => si.QuantityOnHand).FirstOrDefault(),
                variants = p.ProductType == "variable"
                    ? p.Variants.Where(v => v.IsActive)
                        .Select(v => new { id = v.Id, name = v.Name, priceAdjustment = v.PriceAdjustment })
                        .ToList()
                    : null
            })
            .ToListAsync();

        return Json(products);
    }

    public class PosLine
    {
        public int ProductId { get; set; }
        public int? VariantId { get; set; }
        public int Quantity { get; set; }
    }

    public class PosCheckoutRequest
    {
        public int StoreId { get; set; }
        public string PaymentMethod { get; set; } = "Cash";
        public decimal AmountTendered { get; set; }
        public string? CustomerNote { get; set; }
        public List<PosLine> Items { get; set; } = new();
    }

    // ── Complete a sale ───────────────────────────────────────────────────────
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Checkout([FromBody] PosCheckoutRequest req)
    {
        if (req.Items == null || req.Items.Count == 0)
            return Json(new { success = false, message = "Cart is empty." });

        var store = await _db.Stores.FirstOrDefaultAsync(s => s.Id == req.StoreId && s.IsActive);
        if (store == null)
            return Json(new { success = false, message = "Please choose a valid branch." });

        // Load the products referenced, with their variants, and price server-side (don't trust client).
        var productIds = req.Items.Select(i => i.ProductId).Distinct().ToList();
        var products = await _db.Products
            .Include(p => p.Variants)
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);

        // Validate stock per product (summing duplicate lines).
        foreach (var grp in req.Items.GroupBy(i => i.ProductId))
        {
            if (!products.TryGetValue(grp.Key, out var prod))
                return Json(new { success = false, message = "A product in the cart no longer exists." });
            var requested = grp.Sum(i => Math.Max(1, i.Quantity));
            var available = await _stock.GetStockAsync(grp.Key, req.StoreId);
            if (requested > available)
                return Json(new { success = false, message = $"Not enough stock for '{prod.Name}' at {store.Name} ({available} left)." });
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var now = DateTime.UtcNow;
        var orderNumber = $"POS-{now:yyMMdd}-{now:HHmmssfff}";

        await using var tx = await _db.Database.BeginTransactionAsync();

        var order = new Order
        {
            OrderNumber = orderNumber,
            Channel = OrderChannel.Pos,
            FulfillmentType = FulfillmentType.StorePickup,
            PickupStoreId = store.Id,
            UserId = userId,
            Status = OrderStatus.Delivered,
            IsPaid = true,
            PaidAt = now,
            PaymentProvider = req.PaymentMethod,
            Notes = string.IsNullOrWhiteSpace(req.CustomerNote) ? null : req.CustomerNote.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };

        decimal subtotal = 0;
        foreach (var line in req.Items)
        {
            var prod = products[line.ProductId];
            var qty = Math.Max(1, line.Quantity);
            ProductVariant? variant = line.VariantId.HasValue
                ? prod.Variants.FirstOrDefault(v => v.Id == line.VariantId)
                : null;
            var unitPrice = prod.Price + (variant?.PriceAdjustment ?? 0);
            subtotal += unitPrice * qty;

            order.Items.Add(new OrderItem
            {
                ProductId = prod.Id,
                ProductVariantId = variant?.Id,
                ProductName = prod.Name,
                VariantName = variant?.Name,
                Quantity = qty,
                UnitPrice = unitPrice
            });

            await _stock.ApplyAsync(prod.Id, variant?.Id, store.Id, -qty,
                StockMovementType.Sale, orderNumber, userId: userId);
        }

        order.Subtotal = subtotal;
        order.Total = subtotal;
        order.AmountTendered = req.AmountTendered > 0 ? req.AmountTendered : subtotal;
        order.ChangeGiven = Math.Max(0, (order.AmountTendered ?? subtotal) - subtotal);

        _db.Orders.Add(order);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        await LogAsync("Create", "POS Sale", order.Id.ToString(),
            $"POS sale {orderNumber} at {store.Name} — ₦{subtotal:N0} ({order.Items.Count} item(s))");

        return Json(new
        {
            success = true,
            orderId = order.Id,
            orderNumber,
            total = subtotal,
            change = order.ChangeGiven
        });
    }

    // ── Printable receipt ─────────────────────────────────────────────────────
    public async Task<IActionResult> Receipt(int id)
    {
        var order = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.PickupStore)
            .FirstOrDefaultAsync(o => o.Id == id && o.Channel == OrderChannel.Pos);
        if (order == null) return NotFound();
        ViewData["Title"] = $"Receipt {order.OrderNumber}";
        return View(order);
    }

    // ── POS sales history ─────────────────────────────────────────────────────
    public async Task<IActionResult> Sales()
    {
        ViewData["Title"] = "POS Sales";
        var sales = await _db.Orders
            .Where(o => o.Channel == OrderChannel.Pos)
            .Include(o => o.PickupStore)
            .Include(o => o.Items)
            .OrderByDescending(o => o.CreatedAt)
            .Take(100)
            .ToListAsync();
        return View(sales);
    }
}
