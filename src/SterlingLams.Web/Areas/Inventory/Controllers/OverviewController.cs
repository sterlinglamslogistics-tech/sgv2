using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

public class OverviewController : InventoryAreaController
{
    private readonly ApplicationDbContext _db;
    public OverviewController(ApplicationDbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Inventory Overview";
        var today = DateTime.UtcNow.Date;

        var stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();

        // Product-level stock totals (SQL-side sum over each product's inventory rows).
        var prod = await _db.Products.Where(p => p.IsActive)
            .Select(p => new
            {
                p.Name,
                p.LowStockThreshold,
                Total = p.StoreInventories.Sum(si => (int?)si.QuantityOnHand) ?? 0
            })
            .ToListAsync();

        int Thr(int t) => Math.Max(1, t);
        var vm = new InventoryOverviewViewModel
        {
            TotalSkus   = prod.Count,
            OutOfStock  = prod.Count(x => x.Total == 0),
            LowStock    = prod.Count(x => x.Total > 0 && x.Total <= Thr(x.LowStockThreshold)),
            UnitsOnHand = prod.Sum(x => x.Total),
            Alerts = prod.Where(x => x.Total <= Thr(x.LowStockThreshold))
                         .OrderBy(x => x.Total).ThenBy(x => x.Name).Take(12)
                         .Select(x => new StockAlertRow { Name = x.Name, Total = x.Total, Threshold = x.LowStockThreshold })
                         .ToList(),
        };

        // Units per branch.
        var byStore = await _db.StoreInventories.GroupBy(si => si.StoreId)
            .Select(g => new { StoreId = g.Key, Units = g.Sum(x => x.QuantityOnHand) })
            .ToListAsync();
        vm.PerBranch = stores.Select(s => new BranchUnitsRow
        {
            Store = s.Name.Replace("Sterlin Glams ", ""),
            Units = byStore.FirstOrDefault(b => b.StoreId == s.Id)?.Units ?? 0
        }).ToList();

        // Till summary.
        vm.OpenSessions = await _db.TillSessions.CountAsync(s => s.ClosedAt == null);
        var posToday = _db.Orders.Where(o => o.Channel == OrderChannel.Pos && o.CreatedAt >= today);
        vm.TillSalesToday = await posToday.SumAsync(o => (decimal?)o.Total) ?? 0;
        vm.TillTxToday = await posToday.CountAsync();

        // ── Sales insight: last 30 days, excluding cancelled/refunded. ──────────────────────
        var since = today.AddDays(-30);
        var soldOrders = _db.Orders.Where(o => o.CreatedAt >= since
            && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Refunded);

        // Top products by units sold.
        vm.TopProducts = await _db.OrderItems
            .Where(oi => soldOrders.Any(o => o.Id == oi.OrderId))
            .GroupBy(oi => oi.ProductName)
            .Select(g => new TopProductRow
            {
                Name = g.Key,
                Units = g.Sum(x => x.Quantity),
                Revenue = g.Sum(x => (x.Quantity * x.UnitPrice) - x.DiscountAmount)
            })
            .OrderByDescending(x => x.Units).Take(5)
            .ToListAsync();

        // Top staff by POS sales (UserId = cashier on POS orders).
        var staffAgg = await soldOrders.Where(o => o.Channel == OrderChannel.Pos)
            .GroupBy(o => o.UserId)
            .Select(g => new { UserId = g.Key, Sales = g.Sum(x => x.Total), Tx = g.Count() })
            .OrderByDescending(x => x.Sales).Take(5)
            .ToListAsync();
        var staffIds = staffAgg.Select(s => s.UserId).ToList();
        var staffNames = await _db.Users.Where(u => staffIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Email }).ToListAsync();
        vm.TopStaff = staffAgg.Select(s => new TopStaffRow
        {
            Name = staffNames.FirstOrDefault(n => n.Id == s.UserId)?.Email ?? "—",
            Sales = s.Sales,
            Transactions = s.Tx
        }).ToList();

        // Recent stock movements.
        vm.RecentMovements = await _db.StockMovements
            .OrderByDescending(m => m.Id).Take(8)
            .Select(m => new MovementRow
            {
                Product = m.Product.Name,
                Store = m.Store.Name.Replace("Sterlin Glams ", ""),
                Change = m.QuantityChange,
                Type = m.Type.ToString(),
                When = m.CreatedAt
            })
            .ToListAsync();

        return View(vm);
    }
}

public class InventoryOverviewViewModel
{
    public int TotalSkus { get; set; }
    public int OutOfStock { get; set; }
    public int LowStock { get; set; }
    public int UnitsOnHand { get; set; }
    public List<BranchUnitsRow> PerBranch { get; set; } = new();
    public List<StockAlertRow> Alerts { get; set; } = new();
    public int OpenSessions { get; set; }
    public decimal TillSalesToday { get; set; }
    public int TillTxToday { get; set; }
    public List<MovementRow> RecentMovements { get; set; } = new();
    public List<TopProductRow> TopProducts { get; set; } = new();
    public List<TopStaffRow> TopStaff { get; set; } = new();
}
public class TopProductRow { public string Name { get; set; } = ""; public int Units { get; set; } public decimal Revenue { get; set; } }
public class TopStaffRow { public string Name { get; set; } = ""; public decimal Sales { get; set; } public int Transactions { get; set; } }
public class BranchUnitsRow { public string Store { get; set; } = ""; public int Units { get; set; } }
public class StockAlertRow { public string Name { get; set; } = ""; public int Total { get; set; } public int Threshold { get; set; } }
public class MovementRow { public string Product { get; set; } = ""; public string Store { get; set; } = ""; public int Change { get; set; } public string Type { get; set; } = ""; public DateTime When { get; set; } }
