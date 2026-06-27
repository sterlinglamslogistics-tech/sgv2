using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

public class ReportsController : InventoryAreaController
{
    private readonly ApplicationDbContext _db;
    public ReportsController(ApplicationDbContext db) => _db = db;

    public IActionResult Index() => RedirectToAction(nameof(Reorder));

    // ── Reorder report: products at/below their low-stock threshold ──────────────
    public async Task<IActionResult> Reorder(int? categoryId = null)
    {
        ViewData["Title"] = "Reorder report";
        var stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        ViewBag.Stores = stores;
        ViewBag.Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync();
        ViewBag.CategoryId = categoryId;
        return View(await ReorderRowsAsync(categoryId, stores));
    }

    public async Task<IActionResult> ReorderCsv(int? categoryId = null)
    {
        var stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        var rows = await ReorderRowsAsync(categoryId, stores);

        var sb = new StringBuilder();
        sb.Append("Product,SKU");
        foreach (var s in stores) sb.Append(',').Append(Csv(s.Name.Replace("Sterlin Glams ", "")));
        sb.AppendLine(",Total,Threshold,Suggested reorder");
        foreach (var r in rows)
        {
            sb.Append(Csv(r.Name)).Append(',').Append(Csv(r.Sku));
            foreach (var s in stores) sb.Append(',').Append(r.PerStore[s.Id]);
            var suggest = Math.Max(0, Math.Max(1, r.Threshold) * stores.Count - r.Total);
            sb.Append(',').Append(r.Total).Append(',').Append(r.Threshold).Append(',').Append(suggest).AppendLine();
        }
        await LogAsync("Export", "Inventory", null, $"Exported reorder report ({rows.Count} product(s))");
        return CsvFile(sb, "reorder");
    }

    // Aggregation pushed to SQL: per-product totals + threshold filter run in the database, and the
    // per-store breakdown is fetched only for the (few) products at/below threshold — instead of
    // pulling the entire catalogue + every inventory row into memory.
    private async Task<List<ReorderRow>> ReorderRowsAsync(int? categoryId, List<Store> stores)
    {
        var pq = _db.Products.Where(p => p.IsActive);
        if (categoryId.HasValue) pq = pq.Where(p => p.CategoryId == categoryId.Value);

        var prods = await pq
            .Select(p => new
            {
                p.Id, p.Name, p.Sku,
                Threshold = p.LowStockThreshold,
                Total = p.StoreInventories.Sum(si => (int?)si.QuantityOnHand) ?? 0
            })
            .Where(r => r.Total <= (r.Threshold < 1 ? 1 : r.Threshold))
            .OrderBy(r => r.Total).ThenBy(r => r.Name)
            .ToListAsync();

        var ids = prods.Select(r => r.Id).ToList();
        var perStore = (await _db.StoreInventories
                .Where(si => ids.Contains(si.ProductId))
                .GroupBy(si => new { si.ProductId, si.StoreId })
                .Select(g => new { g.Key.ProductId, g.Key.StoreId, Qty = g.Sum(si => si.QuantityOnHand) })
                .ToListAsync())
            .ToLookup(x => x.ProductId);

        return prods.Select(r => new ReorderRow
        {
            Id = r.Id, Name = r.Name, Sku = r.Sku, Threshold = r.Threshold, Total = r.Total,
            PerStore = stores.ToDictionary(s => s.Id, s => perStore[r.Id].Where(x => x.StoreId == s.Id).Sum(x => x.Qty))
        }).ToList();
    }

    // ── Stock value report: units × price per branch ────────────────────────────
    public async Task<IActionResult> Value(int? categoryId = null)
    {
        ViewData["Title"] = "Stock value";
        var stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        ViewBag.Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync();
        ViewBag.CategoryId = categoryId;

        var vm = new StockValueVm
        {
            PerBranch = await PerBranchValueAsync(categoryId, stores),
            TopProducts = await ProductValuesAsync(categoryId, take: 50)
        };
        vm.TotalUnits = vm.PerBranch.Sum(b => b.Units);
        vm.TotalValue = vm.PerBranch.Sum(b => b.Value);
        return View(vm);
    }

    public async Task<IActionResult> ValueCsv(int? categoryId = null)
    {
        var rows = await ProductValuesAsync(categoryId, take: null); // full list for the export

        var sb = new StringBuilder();
        sb.AppendLine("Product,SKU,Units,Price,Value");
        foreach (var r in rows)
            sb.Append(Csv(r.Name)).Append(',').Append(Csv(r.Sku)).Append(',').Append(r.Units)
              .Append(',').Append(r.Price).Append(',').Append(r.Value).AppendLine();
        await LogAsync("Export", "Inventory", null, $"Exported stock value report ({rows.Count} product(s))");
        return CsvFile(sb, "stock_value");
    }

    // ── Movement ledger: every stock change (sale/purchase/transfer/adjustment/damage/loss),
    //    filterable by type, branch, date range and product. ───────────────────────────────────
    public async Task<IActionResult> Movements(int? type = null, int? storeId = null,
        DateTime? from = null, DateTime? to = null, string q = "", int page = 1)
    {
        ViewData["Title"] = "Stock movements";
        const int PageSize = 50;
        var (f, t) = NormalizeRange(from, to);

        var query = _db.StockMovements.Where(m => m.CreatedAt >= f && m.CreatedAt < t.AddDays(1));
        if (type.HasValue) { var te = (StockMovementType)type.Value; query = query.Where(m => m.Type == te); }
        if (storeId.HasValue) query = query.Where(m => m.StoreId == storeId.Value);
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(m => EF.Functions.ILike(m.Product.Name, $"%{q}%")
                                  || EF.Functions.ILike(m.Product.Sku ?? "", $"%{q}%"));

        var total = await query.CountAsync();

        // KPI summary over the filtered set (Moniebook "Stock movement" parity).
        var stockIn = await query.Where(m => m.QuantityChange > 0).SumAsync(m => (int?)m.QuantityChange) ?? 0;
        var stockOut = -(await query.Where(m => m.QuantityChange < 0).SumAsync(m => (int?)m.QuantityChange) ?? 0);
        ViewBag.StockIn = stockIn;
        ViewBag.StockOut = stockOut;
        ViewBag.NetChange = stockIn - stockOut;

        var rows = await query.OrderByDescending(m => m.CreatedAt)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(m => new MovementLedgerRow
            {
                When = m.CreatedAt,
                Product = m.Product.Name,
                Variant = m.ProductVariantId == null ? null
                    : _db.ProductVariants.Where(v => v.Id == m.ProductVariantId).Select(v => v.Name).FirstOrDefault(),
                Store = m.Store.Name.Replace("Sterlin Glams ", ""),
                Type = m.Type.ToString(),
                Change = m.QuantityChange,
                BalanceAfter = m.BalanceAfter,
                Reference = m.Reference,
                By = m.CreatedByUserId == null ? null
                    : _db.Users.Where(u => u.Id == m.CreatedByUserId).Select(u => u.Email).FirstOrDefault()
            }).ToListAsync();

        ViewBag.Stores = await _db.Stores.OrderBy(s => s.Name).ToListAsync();
        ViewBag.Type = type; ViewBag.StoreId = storeId; ViewBag.Query = q;
        ViewBag.From = f; ViewBag.To = t; ViewBag.Page = page;
        ViewBag.TotalPages = (int)Math.Ceiling(total / (double)PageSize); ViewBag.Total = total;
        return View(rows);
    }

    // ── Shrinkage report: stock written off as Damage or Loss (theft), by product. ──────────────
    public async Task<IActionResult> Shrinkage(DateTime? from = null, DateTime? to = null, int? categoryId = null)
    {
        ViewData["Title"] = "Shrinkage report";
        var (f, t) = NormalizeRange(from, to);

        var q = _db.StockMovements
            .Where(m => (m.Type == StockMovementType.Damage || m.Type == StockMovementType.Loss)
                     && m.CreatedAt >= f && m.CreatedAt < t.AddDays(1));
        if (categoryId.HasValue) q = q.Where(m => m.Product.CategoryId == categoryId.Value);

        // Shrinkage data is small; group in memory (avoids GroupBy-translation pitfalls).
        var moves = await q.Select(m => new { m.ProductId, m.Product.Name, m.Product.Sku, m.Product.Price, m.Type, m.QuantityChange })
            .ToListAsync();

        var rows = moves
            .GroupBy(m => new { m.ProductId, m.Name, m.Sku, m.Price })
            .Select(g => new ShrinkageRow
            {
                Name = g.Key.Name,
                Sku = g.Key.Sku,
                DamageUnits = g.Where(x => x.Type == StockMovementType.Damage).Sum(x => -x.QuantityChange),
                LossUnits = g.Where(x => x.Type == StockMovementType.Loss).Sum(x => -x.QuantityChange),
                Value = (g.Sum(x => -x.QuantityChange)) * g.Key.Price
            })
            .OrderByDescending(r => r.Value)
            .ToList();

        ViewBag.Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync();
        ViewBag.CategoryId = categoryId; ViewBag.From = f; ViewBag.To = t;
        return View(new ShrinkageVm
        {
            Rows = rows,
            TotalDamage = rows.Sum(r => r.DamageUnits),
            TotalLoss = rows.Sum(r => r.LossUnits),
            TotalValue = rows.Sum(r => r.Value)
        });
    }

    public async Task<IActionResult> ShrinkageCsv(DateTime? from = null, DateTime? to = null, int? categoryId = null)
    {
        var (f, t) = NormalizeRange(from, to);
        var q = _db.StockMovements
            .Where(m => (m.Type == StockMovementType.Damage || m.Type == StockMovementType.Loss)
                     && m.CreatedAt >= f && m.CreatedAt < t.AddDays(1));
        if (categoryId.HasValue) q = q.Where(m => m.Product.CategoryId == categoryId.Value);
        var moves = await q.Select(m => new { m.Product.Name, m.Product.Sku, m.Product.Price, m.Type, m.QuantityChange }).ToListAsync();
        var rows = moves.GroupBy(m => new { m.Name, m.Sku, m.Price })
            .Select(g => new ShrinkageRow
            {
                Name = g.Key.Name, Sku = g.Key.Sku,
                DamageUnits = g.Where(x => x.Type == StockMovementType.Damage).Sum(x => -x.QuantityChange),
                LossUnits = g.Where(x => x.Type == StockMovementType.Loss).Sum(x => -x.QuantityChange),
                Value = (g.Sum(x => -x.QuantityChange)) * g.Key.Price
            }).OrderByDescending(r => r.Value).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("Product,SKU,Damaged,Lost,Total units,Value");
        foreach (var r in rows)
            sb.Append(Csv(r.Name)).Append(',').Append(Csv(r.Sku)).Append(',').Append(r.DamageUnits)
              .Append(',').Append(r.LossUnits).Append(',').Append(r.DamageUnits + r.LossUnits)
              .Append(',').Append(r.Value).AppendLine();
        await LogAsync("Export", "Inventory", null, $"Exported shrinkage report ({rows.Count} product(s))");
        return CsvFile(sb, "shrinkage");
    }

    // ── Sales by staff (POS cashier), over a date range. ────────────────────────────────────────
    public async Task<IActionResult> Sales(DateTime? from = null, DateTime? to = null)
    {
        ViewData["Title"] = "Sales by staff";
        var (f, t) = NormalizeRange(from, to);

        var orders = _db.Orders.Where(o => o.Channel == OrderChannel.Pos
            && o.CreatedAt >= f && o.CreatedAt < t.AddDays(1)
            && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Refunded);

        var agg = await orders.GroupBy(o => o.UserId)
            .Select(g => new { UserId = g.Key, Sales = g.Sum(x => x.Total), Tx = g.Count() })
            .OrderByDescending(x => x.Sales).ToListAsync();
        var ids = agg.Select(a => a.UserId).ToList();
        var names = await _db.Users.Where(u => ids.Contains(u.Id)).Select(u => new { u.Id, u.Email }).ToListAsync();

        var rows = agg.Select(a => new SalesByStaffRow
        {
            Staff = names.FirstOrDefault(n => n.Id == a.UserId)?.Email ?? "—",
            Transactions = a.Tx,
            Sales = a.Sales,
            Average = a.Tx > 0 ? a.Sales / a.Tx : 0
        }).ToList();

        ViewBag.From = f; ViewBag.To = t;
        return View(new SalesByStaffVm
        {
            Rows = rows,
            TotalSales = rows.Sum(r => r.Sales),
            TotalTx = rows.Sum(r => r.Transactions)
        });
    }

    // ── Payment-method breakdown across all channels, over a date range. ────────────────────────
    public async Task<IActionResult> Payments(DateTime? from = null, DateTime? to = null)
    {
        ViewData["Title"] = "Payment methods";
        var (f, t) = NormalizeRange(from, to);

        var orders = _db.Orders.Where(o => o.CreatedAt >= f && o.CreatedAt < t.AddDays(1)
            && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Refunded);

        var agg = await orders
            .GroupBy(o => new { Method = o.PaymentProvider ?? "Unspecified", o.Channel })
            .Select(g => new { g.Key.Method, g.Key.Channel, Amount = g.Sum(x => x.Total), Tx = g.Count() })
            .ToListAsync();

        var rows = agg
            .GroupBy(a => a.Method)
            .Select(g => new PaymentMethodRow
            {
                Method = g.Key,
                Transactions = g.Sum(x => x.Tx),
                Amount = g.Sum(x => x.Amount),
                Pos = g.Where(x => x.Channel == OrderChannel.Pos).Sum(x => x.Amount),
                Online = g.Where(x => x.Channel == OrderChannel.Online).Sum(x => x.Amount)
            })
            .OrderByDescending(r => r.Amount).ToList();

        ViewBag.From = f; ViewBag.To = t;
        return View(new PaymentMethodVm
        {
            Rows = rows,
            TotalAmount = rows.Sum(r => r.Amount),
            TotalTx = rows.Sum(r => r.Transactions)
        });
    }

    // ── Sales summary: revenue / orders / units / avg + daily breakdown. ────────────────────────
    public async Task<IActionResult> Summary(DateTime? from = null, DateTime? to = null)
    {
        ViewData["Title"] = "Sales summary";
        var (f, t) = NormalizeRange(from, to);
        var orders = SoldOrders(f, t);

        var count = await orders.CountAsync();
        var revenue = await orders.SumAsync(o => (decimal?)o.Total) ?? 0;
        var units = await _db.OrderItems.Where(oi => orders.Any(o => o.Id == oi.OrderId))
            .SumAsync(oi => (int?)oi.Quantity) ?? 0;
        var daily = (await orders.GroupBy(o => o.CreatedAt.Date)
                .Select(g => new { Day = g.Key, Orders = g.Count(), Revenue = g.Sum(x => x.Total) })
                .OrderByDescending(x => x.Day).Take(60).ToListAsync())
            .Select(x => new DailySalesRow { Day = x.Day, Orders = x.Orders, Revenue = x.Revenue }).ToList();

        ViewBag.From = f; ViewBag.To = t;
        return View(new SalesSummaryVm
        {
            Orders = count, Revenue = revenue, Units = units,
            Average = count > 0 ? revenue / count : 0, Daily = daily
        });
    }

    // ── Sales by item ───────────────────────────────────────────────────────────────────────────
    public async Task<IActionResult> SalesByItem(DateTime? from = null, DateTime? to = null)
    {
        ViewData["Title"] = "Sales by item";
        var (f, t) = NormalizeRange(from, to);
        var orders = SoldOrders(f, t);
        var rows = await _db.OrderItems.Where(oi => orders.Any(o => o.Id == oi.OrderId))
            .GroupBy(oi => oi.ProductName)
            .Select(g => new NameValueRow
            {
                Name = g.Key,
                Units = g.Sum(x => x.Quantity),
                Revenue = g.Sum(x => (x.Quantity * x.UnitPrice) - x.DiscountAmount)
            })
            .OrderByDescending(x => x.Revenue).Take(200).ToListAsync();
        ViewBag.From = f; ViewBag.To = t; ViewBag.Heading = "Product"; ViewBag.Title = "Sales by item"; ViewBag.Active = "SalesByItem";
        return View("SalesBreakdown", rows);
    }

    // ── Sales by category ───────────────────────────────────────────────────────────────────────
    public async Task<IActionResult> SalesByCategory(DateTime? from = null, DateTime? to = null)
    {
        ViewData["Title"] = "Sales by category";
        var (f, t) = NormalizeRange(from, to);
        var orders = SoldOrders(f, t);
        var rows = await _db.OrderItems.Where(oi => orders.Any(o => o.Id == oi.OrderId))
            .GroupBy(oi => oi.Product.Category != null ? oi.Product.Category.Name : "Uncategorised")
            .Select(g => new NameValueRow
            {
                Name = g.Key,
                Units = g.Sum(x => x.Quantity),
                Revenue = g.Sum(x => (x.Quantity * x.UnitPrice) - x.DiscountAmount)
            })
            .OrderByDescending(x => x.Revenue).ToListAsync();
        ViewBag.From = f; ViewBag.To = t; ViewBag.Heading = "Category"; ViewBag.Title = "Sales by category"; ViewBag.Active = "SalesByCategory";
        return View("SalesBreakdown", rows);
    }

    // ── Sales by customer ───────────────────────────────────────────────────────────────────────
    public async Task<IActionResult> SalesByCustomer(DateTime? from = null, DateTime? to = null)
    {
        ViewData["Title"] = "Sales by customer";
        var (f, t) = NormalizeRange(from, to);
        var raw = await SoldOrders(f, t)
            .Select(o => new
            {
                Key = o.CustomerUserId != null ? o.CustomerUserId : (o.Channel == OrderChannel.Online ? o.UserId : null),
                o.Total
            }).ToListAsync();

        var grouped = raw.GroupBy(x => x.Key)
            .Select(g => new { g.Key, Orders = g.Count(), Spend = g.Sum(x => x.Total) })
            .OrderByDescending(x => x.Spend).Take(200).ToList();
        var ids = grouped.Where(g => g.Key != null).Select(g => g.Key!).ToList();
        var names = await _db.Users.Where(u => ids.Contains(u.Id)).Select(u => new { u.Id, u.Email }).ToListAsync();

        var rows = grouped.Select(g => new NameValueRow
        {
            Name = g.Key == null ? "Walk-in / guest" : (names.FirstOrDefault(n => n.Id == g.Key)?.Email ?? "—"),
            Units = g.Orders,
            Revenue = g.Spend
        }).ToList();
        ViewBag.From = f; ViewBag.To = t; ViewBag.Heading = "Customer"; ViewBag.UnitsLabel = "Orders"; ViewBag.Title = "Sales by customer"; ViewBag.Active = "SalesByCustomer";
        return View("SalesBreakdown", rows);
    }

    // ── Discounts: usage + value given, by code, over a date range. ─────────────────────────────
    public async Task<IActionResult> Discounts(DateTime? from = null, DateTime? to = null)
    {
        ViewData["Title"] = "Discounts";
        var (f, t) = NormalizeRange(from, to);
        var rows = await SoldOrders(f, t)
            .Where(o => o.DiscountAmount > 0 && o.DiscountCode != null)
            .GroupBy(o => o.DiscountCode!)
            .Select(g => new NameValueRow { Name = g.Key, Units = g.Count(), Revenue = g.Sum(x => x.DiscountAmount) })
            .OrderByDescending(x => x.Revenue).ToListAsync();
        ViewBag.From = f; ViewBag.To = t;
        ViewBag.Heading = "Discount code"; ViewBag.UnitsLabel = "Orders"; ViewBag.RevenueLabel = "Discount given";
        ViewBag.Title = "Discounts"; ViewBag.Active = "Discounts";
        return View("SalesBreakdown", rows);
    }

    // ── Expiring inventory: dated stock received via adjustments, soonest expiry first. ──────────
    public async Task<IActionResult> Expiring()
    {
        ViewData["Title"] = "Expiring inventory";
        var rows = await _db.StockAdjustmentLines
            .Where(l => l.ExpiryDate != null && l.QtyDelta > 0)
            .OrderBy(l => l.ExpiryDate)
            .Select(l => new ExpiringRow
            {
                Product = l.ProductName,
                Variant = l.VariantName,
                Branch = l.StockAdjustment.Store.Name.Replace("Sterlin Glams ", ""),
                Qty = l.QtyDelta,
                Expiry = l.ExpiryDate!.Value,
                Reference = l.StockAdjustment.AdjustmentNumber
            }).Take(300).ToListAsync();
        return View(rows);
    }

    // ── Dead stock & aging: products with stock on hand that haven't sold in N days (capital
    //    tied up). "Value" is at retail (qty × price), matching the Stock value report. ──────────
    public async Task<IActionResult> DeadStock(int days = 90, int? categoryId = null)
    {
        ViewData["Title"] = "Dead stock";
        if (days < 1) days = 90;
        var cutoff = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(-days), DateTimeKind.Utc);

        var pq = _db.Products.Where(p => p.IsActive);
        if (categoryId.HasValue) pq = pq.Where(p => p.CategoryId == categoryId.Value);
        var prods = await pq
            .Select(p => new
            {
                p.Id, p.Name, p.Sku, Category = p.Category.Name, p.Price,
                Units = p.StoreInventories.Sum(si => (int?)si.QuantityOnHand) ?? 0
            })
            .Where(x => x.Units > 0)   // only stock that's actually tying up capital
            .ToListAsync();

        var ids = prods.Select(p => p.Id).ToList();
        var lastSold = (await _db.OrderItems
                .Where(oi => ids.Contains(oi.ProductId)
                          && oi.Order.Status != OrderStatus.Cancelled && oi.Order.Status != OrderStatus.Refunded)
                .GroupBy(oi => oi.ProductId)
                .Select(g => new { ProductId = g.Key, Last = g.Max(x => x.Order.CreatedAt) })
                .ToListAsync())
            .ToDictionary(x => x.ProductId, x => x.Last);

        var today = DateTime.UtcNow.Date;
        var rows = prods.Select(p =>
            {
                DateTime? last = lastSold.TryGetValue(p.Id, out var d) ? d : null;
                return new DeadStockRow
                {
                    Name = p.Name, Sku = p.Sku, Category = p.Category,
                    Units = p.Units, Value = p.Units * p.Price,
                    LastSold = last,
                    DaysSince = last.HasValue ? (int)(today - last.Value.Date).TotalDays : null
                };
            })
            .Where(r => !r.LastSold.HasValue || r.LastSold.Value < cutoff)  // never sold, or not since cutoff
            .OrderByDescending(r => r.Value)
            .ToList();

        ViewBag.Days = days; ViewBag.CategoryId = categoryId;
        ViewBag.Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync();
        return View(new DeadStockVm { Rows = rows, Days = days, TotalUnits = rows.Sum(r => r.Units), TotalValue = rows.Sum(r => r.Value) });
    }

    public async Task<IActionResult> DeadStockCsv(int days = 90, int? categoryId = null)
    {
        var vm = (DeadStockVm)((ViewResult)await DeadStock(days, categoryId)).Model!;
        var sb = new StringBuilder();
        sb.AppendLine("Product,SKU,Category,Units,Value,Last sold,Days since");
        foreach (var r in vm.Rows)
            sb.Append(Csv(r.Name)).Append(',').Append(Csv(r.Sku)).Append(',').Append(Csv(r.Category)).Append(',')
              .Append(r.Units).Append(',').Append(r.Value).Append(',')
              .Append(r.LastSold?.ToString("yyyy-MM-dd") ?? "never").Append(',')
              .Append(r.DaysSince?.ToString() ?? "").AppendLine();
        await LogAsync("Export", "Inventory", null, $"Exported dead-stock report ({vm.Rows.Count} product(s), {days}d)");
        return CsvFile(sb, "dead_stock");
    }

    // Orders that count as sales in a window: not cancelled or refunded.
    private IQueryable<Order> SoldOrders(DateTime f, DateTime t) =>
        _db.Orders.Where(o => o.CreatedAt >= f && o.CreatedAt < t.AddDays(1)
            && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Refunded);

    // Default the report window to the last 30 days when no range is supplied.
    private static (DateTime from, DateTime to) NormalizeRange(DateTime? from, DateTime? to)
    {
        // Query-string dates bind with Kind=Unspecified; force UTC so Npgsql accepts them
        // for the timestamptz columns (otherwise filtering by an explicit range throws).
        var t = DateTime.SpecifyKind((to ?? DateTime.UtcNow).Date, DateTimeKind.Utc);
        var f = DateTime.SpecifyKind((from ?? t.AddDays(-30)).Date, DateTimeKind.Utc);
        if (f > t) (f, t) = (t, f);
        return (f, t);
    }

    // Per-branch units + value computed in SQL (join inventory→product, group by store).
    private async Task<List<BranchValue>> PerBranchValueAsync(int? categoryId, List<Store> stores)
    {
        var siq = _db.StoreInventories.Where(si => si.Product.IsActive);
        if (categoryId.HasValue) siq = siq.Where(si => si.Product.CategoryId == categoryId.Value);

        var raw = (await siq
                .GroupBy(si => si.StoreId)
                .Select(g => new
                {
                    StoreId = g.Key,
                    Units = g.Sum(si => si.QuantityOnHand),
                    Value = g.Sum(si => si.QuantityOnHand * si.Product.Price)
                })
                .ToListAsync())
            .ToDictionary(x => x.StoreId);

        return stores.Select(s => new BranchValue
        {
            Store = s.Name.Replace("Sterlin Glams ", ""),
            Units = raw.TryGetValue(s.Id, out var b) ? b.Units : 0,
            Value = raw.TryGetValue(s.Id, out var b2) ? b2.Value : 0
        }).ToList();
    }

    // Per-product units + value computed in SQL; ordered + (optionally) limited in the database.
    private async Task<List<ProductValue>> ProductValuesAsync(int? categoryId, int? take)
    {
        var pq = _db.Products.Where(p => p.IsActive);
        if (categoryId.HasValue) pq = pq.Where(p => p.CategoryId == categoryId.Value);

        var q = pq
            .Select(p => new ProductValue
            {
                Name = p.Name,
                Sku = p.Sku,
                Price = p.Price,
                Units = p.StoreInventories.Sum(si => (int?)si.QuantityOnHand) ?? 0,
                Value = (p.StoreInventories.Sum(si => (int?)si.QuantityOnHand) ?? 0) * p.Price
            })
            .Where(x => x.Units > 0)
            .OrderByDescending(x => x.Value);

        return take.HasValue ? await q.Take(take.Value).ToListAsync() : await q.ToListAsync();
    }

    // ── Stock Levels: per-product stock across branches (units & sale value; no cost/tax) ──────
    public async Task<IActionResult> Levels(int? storeId, int? categoryId, string q = "", int page = 1, string? format = null)
    {
        const int Size = 25;
        ViewData["Title"] = "Stock Levels";
        var stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        ViewBag.Stores = stores;
        ViewBag.Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync();
        ViewBag.StoreId = storeId; ViewBag.CategoryId = categoryId; ViewBag.Q = q;

        var pq = _db.Products.Where(p => p.IsActive);
        if (categoryId.HasValue) pq = pq.Where(p => p.CategoryId == categoryId.Value);
        if (!string.IsNullOrWhiteSpace(q))
            pq = pq.Where(p => EF.Functions.ILike(p.Name, $"%{q}%") || EF.Functions.ILike(p.Sku ?? "", $"%{q}%") || EF.Functions.ILike(p.Barcode ?? "", $"%{q}%"));

        var proj = pq.Select(p => new StockLevelRow
        {
            ProductId = p.Id, Name = p.Name, Barcode = p.Barcode,
            Category = p.Category != null ? p.Category.Name : "—",
            Price = p.Price,
            TotalStock = p.StoreInventories.Where(si => si.ProductVariantId == null).Sum(si => (int?)si.QuantityOnHand) ?? 0,
            OnOrder = p.StoreInventories.Where(si => si.ProductVariantId == null).Sum(si => (int?)si.OnOrder) ?? 0,
            CurrentStock = storeId == null ? -1 : (p.StoreInventories.Where(si => si.StoreId == storeId && si.ProductVariantId == null).Select(si => (int?)si.QuantityOnHand).FirstOrDefault() ?? 0)
        });

        if (format == "csv")
        {
            var all = await proj.OrderBy(r => r.Name).ToListAsync();
            var sb = new StringBuilder();
            sb.AppendLine("Product,Barcode,Category,Current Stock,Total Stock,On Order,Sale Price,Total Sale Value");
            foreach (var r in all)
                sb.Append(Csv(r.Name)).Append(',').Append(Csv(r.Barcode)).Append(',').Append(Csv(r.Category)).Append(',')
                  .Append(r.CurrentStock < 0 ? "" : r.CurrentStock.ToString()).Append(',').Append(r.TotalStock).Append(',')
                  .Append(r.OnOrder).Append(',').Append(r.Price).Append(',').Append(r.TotalSaleValue).AppendLine();
            await LogAsync("Export", "Inventory", null, "Exported stock levels");
            return CsvFile(sb, "stock_levels");
        }

        var total = await proj.CountAsync();
        ViewBag.Page = page; ViewBag.TotalPages = (int)Math.Ceiling(total / (double)Size); ViewBag.Total = total;
        return View(await proj.OrderBy(r => r.Name).Skip((page - 1) * Size).Take(Size).ToListAsync());
    }

    // Per-branch breakdown for one product (the "View Locations" expander).
    [HttpGet]
    public async Task<IActionResult> LevelBreakdown(int id)
    {
        var price = await _db.Products.Where(p => p.Id == id).Select(p => p.Price).FirstOrDefaultAsync();
        var rows = await _db.StoreInventories.Include(si => si.Store)
            .Where(si => si.ProductId == id && si.ProductVariantId == null && si.Store.IsActive)
            .OrderBy(si => si.Store.Name)
            .Select(si => new { location = si.Store.Name, stock = si.QuantityOnHand, onOrder = si.OnOrder, price, value = si.QuantityOnHand * price })
            .ToListAsync();
        return Json(rows);
    }

    // ── Stock Warnings: per-location rows at/below their min (no brand/supplier) ─────────────────
    public async Task<IActionResult> Warnings(int? storeId, int page = 1, string? format = null)
    {
        const int Size = 25;
        ViewData["Title"] = "Stock Warnings";
        ViewBag.Stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        ViewBag.StoreId = storeId;

        var q = _db.StoreInventories.Include(si => si.Product).ThenInclude(p => p.Category).Include(si => si.Store)
            .Where(si => si.Product.IsActive && si.ProductVariantId == null && si.Store.IsActive
                      && (si.MinStock ?? si.Product.LowStockThreshold) > 0
                      && si.QuantityOnHand <= (si.MinStock ?? si.Product.LowStockThreshold));
        if (storeId.HasValue) q = q.Where(si => si.StoreId == storeId.Value);

        var proj = q.Select(si => new StockWarningRow
        {
            Product = si.Product.Name, Barcode = si.Product.Barcode,
            Category = si.Product.Category != null ? si.Product.Category.Name : "—",
            Location = si.Store.Name,
            Current = si.QuantityOnHand,
            Min = si.MinStock ?? si.Product.LowStockThreshold,
            Max = si.MaxStock, OnOrder = si.OnOrder
        }).OrderBy(r => r.Product).ThenBy(r => r.Location);

        if (format == "csv")
        {
            var all = await proj.ToListAsync();
            var sb = new StringBuilder();
            sb.AppendLine("Product,Location,Barcode,Category,Current Stock,Min Stock,Max Stock,On Order,Reorder");
            foreach (var r in all)
                sb.Append(Csv(r.Product)).Append(',').Append(Csv(r.Location)).Append(',').Append(Csv(r.Barcode)).Append(',')
                  .Append(Csv(r.Category)).Append(',').Append(r.Current).Append(',').Append(r.Min).Append(',')
                  .Append(r.Max?.ToString() ?? "").Append(',').Append(r.OnOrder).Append(',').Append(r.Reorder).AppendLine();
            await LogAsync("Export", "Inventory", null, "Exported stock warnings");
            return CsvFile(sb, "stock_warnings");
        }

        var total = await proj.CountAsync();
        ViewBag.Page = page; ViewBag.TotalPages = (int)Math.Ceiling(total / (double)Size); ViewBag.Total = total;
        return View(await proj.Skip((page - 1) * Size).Take(Size).ToListAsync());
    }

    // ── Reorder Worksheet: per-location items to reorder, with a suggested quantity that accounts
    //    for stock already on order. Suggested = (Max ?? Min) − On-hand − On-order. No supplier/cost.
    public async Task<IActionResult> ReorderWorksheet(int? storeId, int page = 1, string? format = null)
    {
        const int Size = 25;
        ViewData["Title"] = "Reorder Worksheet";
        ViewBag.Stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        ViewBag.StoreId = storeId;

        // Reorder point reached when on-hand + on-order is at/below the location's min
        // (per-location Min, falling back to the product's low-stock threshold).
        var q = _db.StoreInventories.Include(si => si.Product).ThenInclude(p => p.Category).Include(si => si.Store)
            .Where(si => si.Product.IsActive && si.ProductVariantId == null && si.Store.IsActive
                      && (si.MinStock ?? si.Product.LowStockThreshold) > 0
                      && (si.QuantityOnHand + si.OnOrder) <= (si.MinStock ?? si.Product.LowStockThreshold));
        if (storeId.HasValue) q = q.Where(si => si.StoreId == storeId.Value);

        var rows = (await q.Select(si => new
        {
            Product = si.Product.Name, Barcode = si.Product.Barcode,
            Category = si.Product.Category != null ? si.Product.Category.Name : "—",
            Location = si.Store.Name,
            Current = si.QuantityOnHand,
            Min = si.MinStock ?? si.Product.LowStockThreshold,
            si.MaxStock, si.OnOrder
        }).ToListAsync())
        .Select(x => new StockReorderRow
        {
            Product = x.Product, Barcode = x.Barcode, Category = x.Category, Location = x.Location,
            Current = x.Current, Min = x.Min, Max = x.MaxStock, OnOrder = x.OnOrder,
            Suggested = Math.Max(0, (x.MaxStock ?? x.Min) - x.Current - x.OnOrder)
        })
        .Where(r => r.Suggested > 0)
        .OrderByDescending(r => r.Suggested).ThenBy(r => r.Product).ToList();

        if (format == "csv")
        {
            var sb = new StringBuilder();
            sb.AppendLine("Product,Location,Barcode,Category,Current Stock,On Order,Min,Max,Suggested Order");
            foreach (var r in rows)
                sb.Append(Csv(r.Product)).Append(',').Append(Csv(r.Location)).Append(',').Append(Csv(r.Barcode)).Append(',')
                  .Append(Csv(r.Category)).Append(',').Append(r.Current).Append(',').Append(r.OnOrder).Append(',')
                  .Append(r.Min).Append(',').Append(r.Max?.ToString() ?? "").Append(',').Append(r.Suggested).AppendLine();
            await LogAsync("Export", "Inventory", null, "Exported reorder worksheet");
            return CsvFile(sb, "reorder_worksheet");
        }

        ViewBag.TotalSuggested = rows.Sum(r => r.Suggested);
        ViewBag.Total = rows.Count;
        ViewBag.Page = page; ViewBag.TotalPages = (int)Math.Ceiling(rows.Count / (double)Size);
        return View(rows.Skip((page - 1) * Size).Take(Size).ToList());
    }

    // ── Stock Discrepancies: transfer received-vs-sent differences (no cost) ─────────────────────
    public async Task<IActionResult> Discrepancies(int days = 7, int? storeId = null, string? reason = null, int page = 1, string? format = null)
    {
        const int Size = 25;
        ViewData["Title"] = "Stock Discrepancies";
        ViewBag.Stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        ViewBag.Days = days; ViewBag.StoreId = storeId; ViewBag.Reason = reason;

        var fromD = DateTime.UtcNow.Date.AddDays(-days + 1);
        var baseQ = _db.StockTransferItems
            .Include(i => i.StockTransfer).ThenInclude(t => t.FromStore)
            .Include(i => i.StockTransfer).ThenInclude(t => t.ToStore)
            .Where(i => i.StockTransfer.ReceivedAt != null && i.StockTransfer.ReceivedAt >= fromD
                     && (i.ReceivedQty ?? 0) != (i.DispatchedQty ?? 0));
        if (storeId.HasValue) baseQ = baseQ.Where(i => i.StockTransfer.ToStoreId == storeId.Value || i.StockTransfer.FromStoreId == storeId.Value);

        var items = await baseQ.OrderByDescending(i => i.StockTransfer.ReceivedAt).ToListAsync();
        var pids = items.Select(i => i.ProductId).Distinct().ToList();
        var pinfo = await _db.Products.Where(p => pids.Contains(p.Id))
            .Select(p => new { p.Id, p.Barcode, p.Price }).ToDictionaryAsync(p => p.Id);

        var rows = items.Select(i =>
        {
            pinfo.TryGetValue(i.ProductId, out var pi);
            var disc = (i.ReceivedQty ?? 0) - (i.DispatchedQty ?? 0);
            return new StockDiscrepancyRow
            {
                TransRef = i.StockTransfer.TransferNumber,
                From = i.StockTransfer.FromStore.Name, To = i.StockTransfer.ToStore.Name,
                Sent = i.StockTransfer.DispatchedAt, Received = i.StockTransfer.ReceivedAt,
                Product = i.ProductName + (string.IsNullOrEmpty(i.VariantName) ? "" : $" – {i.VariantName}"),
                Barcode = pi?.Barcode,
                SentQty = i.DispatchedQty ?? 0, ReceivedQty = i.ReceivedQty ?? 0,
                Discrepancy = disc,
                DiscrepancyValue = disc * (pi?.Price ?? 0),
                ReasonLabel = disc < 0 ? "Short received" : "Over received"
            };
        }).ToList();

        if (!string.IsNullOrWhiteSpace(reason)) rows = rows.Where(r => r.ReasonLabel == reason).ToList();

        if (format == "csv")
        {
            var sb = new StringBuilder();
            sb.AppendLine("Trans Ref,From,To,Date Sent,Date Received,Product,Barcode,Sent Qty,Received Qty,Discrepancy,Discrepancy Value,Reason");
            foreach (var r in rows)
                sb.Append(Csv(r.TransRef)).Append(',').Append(Csv(r.From)).Append(',').Append(Csv(r.To)).Append(',')
                  .Append(r.Sent?.ToString("yyyy-MM-dd HH:mm")).Append(',').Append(r.Received?.ToString("yyyy-MM-dd HH:mm")).Append(',')
                  .Append(Csv(r.Product)).Append(',').Append(Csv(r.Barcode)).Append(',').Append(r.SentQty).Append(',')
                  .Append(r.ReceivedQty).Append(',').Append(r.Discrepancy).Append(',').Append(r.DiscrepancyValue).Append(',').Append(Csv(r.ReasonLabel)).AppendLine();
            await LogAsync("Export", "Inventory", null, "Exported stock discrepancies");
            return CsvFile(sb, "stock_discrepancies");
        }

        ViewBag.TotalDisc = rows.Sum(r => r.Discrepancy);
        ViewBag.TotalValue = rows.Sum(r => r.DiscrepancyValue);
        ViewBag.Page = page; ViewBag.TotalPages = (int)Math.Ceiling(rows.Count / (double)Size); ViewBag.Total = rows.Count;
        return View(rows.Skip((page - 1) * Size).Take(Size).ToList());
    }

    private static string Csv(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
    private FileContentResult CsvFile(StringBuilder sb, string name)
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv", $"{name}_{DateTime.UtcNow:yyyyMMdd_HHmm}.csv");
    }
}

public class ReorderRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Sku { get; set; }
    public int Threshold { get; set; }
    public int Total { get; set; }
    public Dictionary<int, int> PerStore { get; set; } = new();
}
public class StockValueVm
{
    public List<BranchValue> PerBranch { get; set; } = new();
    public List<ProductValue> TopProducts { get; set; } = new();
    public int TotalUnits { get; set; }
    public decimal TotalValue { get; set; }
}
public class BranchValue { public string Store { get; set; } = ""; public int Units { get; set; } public decimal Value { get; set; } }
public class ProductValue { public string Name { get; set; } = ""; public string? Sku { get; set; } public int Units { get; set; } public decimal Price { get; set; } public decimal Value { get; set; } }

public class MovementLedgerRow
{
    public DateTime When { get; set; }
    public string Product { get; set; } = "";
    public string? Variant { get; set; }
    public string Store { get; set; } = "";
    public string Type { get; set; } = "";
    public int Change { get; set; }
    public int BalanceAfter { get; set; }
    public string? Reference { get; set; }
    public string? By { get; set; }
}
public class ShrinkageRow
{
    public string Name { get; set; } = "";
    public string? Sku { get; set; }
    public int DamageUnits { get; set; }
    public int LossUnits { get; set; }
    public decimal Value { get; set; }
}
public class ShrinkageVm
{
    public List<ShrinkageRow> Rows { get; set; } = new();
    public int TotalDamage { get; set; }
    public int TotalLoss { get; set; }
    public decimal TotalValue { get; set; }
}
public class SalesByStaffRow
{
    public string Staff { get; set; } = "";
    public int Transactions { get; set; }
    public decimal Sales { get; set; }
    public decimal Average { get; set; }
}
public class SalesByStaffVm
{
    public List<SalesByStaffRow> Rows { get; set; } = new();
    public decimal TotalSales { get; set; }
    public int TotalTx { get; set; }
}
public class PaymentMethodRow
{
    public string Method { get; set; } = "";
    public int Transactions { get; set; }
    public decimal Amount { get; set; }
    public decimal Pos { get; set; }
    public decimal Online { get; set; }
}
public class PaymentMethodVm
{
    public List<PaymentMethodRow> Rows { get; set; } = new();
    public decimal TotalAmount { get; set; }
    public int TotalTx { get; set; }
}
public class DailySalesRow { public DateTime Day { get; set; } public int Orders { get; set; } public decimal Revenue { get; set; } }
public class SalesSummaryVm
{
    public int Orders { get; set; }
    public decimal Revenue { get; set; }
    public int Units { get; set; }
    public decimal Average { get; set; }
    public List<DailySalesRow> Daily { get; set; } = new();
}
public class NameValueRow { public string Name { get; set; } = ""; public int Units { get; set; } public decimal Revenue { get; set; } }
public class ExpiringRow
{
    public string Product { get; set; } = "";
    public string? Variant { get; set; }
    public string Branch { get; set; } = "";
    public int Qty { get; set; }
    public DateTime Expiry { get; set; }
    public string Reference { get; set; } = "";
}
public class DeadStockRow
{
    public string Name { get; set; } = "";
    public string? Sku { get; set; }
    public string Category { get; set; } = "";
    public int Units { get; set; }
    public decimal Value { get; set; }
    public DateTime? LastSold { get; set; }
    public int? DaysSince { get; set; }
}
public class DeadStockVm
{
    public List<DeadStockRow> Rows { get; set; } = new();
    public int Days { get; set; }
    public int TotalUnits { get; set; }
    public decimal TotalValue { get; set; }
}

public class StockLevelRow
{
    public int ProductId { get; set; }
    public string Name { get; set; } = "";
    public string? Barcode { get; set; }
    public string Category { get; set; } = "";
    public decimal Price { get; set; }
    public int TotalStock { get; set; }
    public int OnOrder { get; set; }
    public int CurrentStock { get; set; }   // -1 = "All locations" selected (no single-branch figure)
    public decimal TotalSaleValue => Price * TotalStock;
}

public class StockWarningRow
{
    public string Product { get; set; } = "";
    public string? Barcode { get; set; }
    public string Category { get; set; } = "";
    public string Location { get; set; } = "";
    public int Current { get; set; }
    public int Min { get; set; }
    public int? Max { get; set; }
    public int OnOrder { get; set; }
    public int Reorder => System.Math.Max(0, (Max ?? Min) - Current);
}

public class StockReorderRow
{
    public string Product { get; set; } = "";
    public string? Barcode { get; set; }
    public string Category { get; set; } = "";
    public string Location { get; set; } = "";
    public int Current { get; set; }
    public int Min { get; set; }
    public int? Max { get; set; }
    public int OnOrder { get; set; }
    public int Suggested { get; set; }
}

public class StockDiscrepancyRow
{
    public string TransRef { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public DateTime? Sent { get; set; }
    public DateTime? Received { get; set; }
    public string Product { get; set; } = "";
    public string? Barcode { get; set; }
    public int SentQty { get; set; }
    public int ReceivedQty { get; set; }
    public int Discrepancy { get; set; }
    public decimal DiscrepancyValue { get; set; }
    public string ReasonLabel { get; set; } = "";
}
