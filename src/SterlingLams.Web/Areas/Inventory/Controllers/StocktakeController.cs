using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

public class StocktakeController : InventoryAreaController
{
    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;
    private readonly IStoreAccessService _access;
    private const int PageSize = 25;

    // Reasons offered for a counted difference (EPOS-style).
    public static readonly string[] LineReasons =
        { "External Branch Movement", "Internal Movement", "Missing Stock", "New Stock", "Stock Take" };

    public StocktakeController(ApplicationDbContext db, IStockService stock, IStoreAccessService access)
    {
        _db = db;
        _stock = stock;
        _access = access;
    }

    // Back Office Stock Take: pick staff + location, then count (search/scan → list → review → complete).
    public async Task<IActionResult> Index(int? storeId, string? staffId)
    {
        ViewData["Title"] = "Stock Take";
        ViewBag.Stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        ViewBag.Staff = await StaffOptionsAsync();
        ViewBag.Reasons = LineReasons;
        ViewBag.StoreId = storeId;
        ViewBag.StaffId = staffId;
        var store = storeId.HasValue ? await _db.Stores.FirstOrDefaultAsync(s => s.Id == storeId.Value) : null;
        ViewBag.StoreName = store?.Name ?? "";
        return View();
    }

    // Typeahead for the count box — name / SKU / barcode, with the system (expected) qty for the branch.
    [HttpGet]
    public async Task<IActionResult> StSearch(string q, int storeId)
    {
        q = (q ?? "").Trim();
        if (q.Length < 2) return Json(Array.Empty<object>());
        var rows = await _db.Products
            .Where(p => p.IsActive && (EF.Functions.ILike(p.Name, $"%{q}%")
                     || EF.Functions.ILike(p.Sku ?? "", $"%{q}%")
                     || EF.Functions.ILike(p.Barcode ?? "", $"%{q}%")))
            .OrderBy(p => p.Name).Take(20)
            .Select(p => new
            {
                id = p.Id, name = p.Name, sku = p.Sku, barcode = p.Barcode,
                category = p.Category != null ? p.Category.Name : "",
                expected = p.StoreInventories.Where(si => si.StoreId == storeId && si.ProductVariantId == null)
                    .Select(si => (int?)si.QuantityOnHand).FirstOrDefault() ?? 0
            })
            .ToListAsync();
        return Json(rows);
    }

    // Exact barcode/SKU lookup for the scan box.
    [HttpGet]
    public async Task<IActionResult> ScanLookup(string code, int storeId)
    {
        code = (code ?? "").Trim();
        if (code.Length == 0) return Json(new { found = false });
        var p = await _db.Products
            .Where(x => x.Barcode == code || x.Sku == code || x.Variants.Any(v => v.Barcode == code || v.Sku == code))
            .Select(x => new
            {
                x.Id, x.Name, x.Sku, x.Barcode, x.IsActive,
                category = x.Category != null ? x.Category.Name : "",
                expected = x.StoreInventories.Where(si => si.StoreId == storeId && si.ProductVariantId == null)
                    .Select(si => (int?)si.QuantityOnHand).FirstOrDefault() ?? 0
            })
            .FirstOrDefaultAsync();
        if (p == null || !p.IsActive) return Json(new { found = false });
        return Json(new { found = true, id = p.Id, name = p.Name, sku = p.Sku, barcode = p.Barcode, category = p.category, expected = p.expected });
    }

    public class CountLine { public int ProductId { get; set; } public int Counted { get; set; } public string? Reason { get; set; } }
    public class CompleteRequest { public int StoreId { get; set; } public string? StaffId { get; set; } public string? Note { get; set; } public List<CountLine> Lines { get; set; } = new(); }

    // Complete the stock-take: persist the StockTake record + reconcile each counted line through the
    // ledger (reason "Stock-take"). Returns the new stock-take id for redirect to its details.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Complete([FromBody] CompleteRequest req)
    {
        var store = await _db.Stores.FirstOrDefaultAsync(s => s.Id == req.StoreId && s.IsActive);
        if (store == null) return Json(new { success = false, message = "Invalid branch." });
        if (!await _access.CanWriteAsync(User, store.Id))
            return Json(new { success = false, message = "You can only run a stock-take for your assigned branch(es)." });
        if (req.Lines == null || req.Lines.Count == 0) return Json(new { success = false, message = "Nothing to count." });

        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        // Only accept a real staff member as the counter; otherwise fall back to the acting user.
        var staffId = await IsStaffAsync(req.StaffId) ? req.StaffId : userId;
        var staffName = await StaffNameAsync(staffId) ?? "—";

        var ids = req.Lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await _db.Products.Include(p => p.Category)
            .Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        var valid = req.Lines.Where(l => l.Counted >= 0 && products.ContainsKey(l.ProductId)).ToList();
        if (valid.Count == 0) return Json(new { success = false, message = "No valid items." });

        var seq = await NextRefAsync();
        var take = new StockTake
        {
            Reference = $"ST{seq:D5}", StoreId = store.Id, StaffUserId = staffId,
            StaffName = staffName, Status = "Completed", Note = req.Note, CreatedAt = DateTime.UtcNow
        };

        await using var tx = await _db.Database.BeginTransactionAsync();
        if (_db.Database.IsNpgsql())
            foreach (var pid in valid.Select(l => l.ProductId).Distinct().OrderBy(id => id))
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"StoreInventories\" WHERE \"ProductId\" = {pid} AND \"StoreId\" = {store.Id} FOR UPDATE");

        foreach (var l in valid)
        {
            var p = products[l.ProductId];
            var current = await _stock.GetStockAsync(l.ProductId, null, store.Id, fallback: false);
            var delta = l.Counted - current;
            if (delta != 0)
                await _stock.ApplyAsync(l.ProductId, null, store.Id, delta, StockMovementType.Adjustment,
                    take.Reference, note: l.Reason ?? "Stock-take", userId: userId);
            take.Lines.Add(new StockTakeLine
            {
                ProductId = p.Id, ProductName = p.Name, Barcode = p.Barcode,
                CategoryName = p.Category?.Name ?? "", ExpectedQty = current, CountedQty = l.Counted,
                Reason = delta != 0 ? l.Reason : null
            });
        }
        _db.StockTakes.Add(take);

        try
        {
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Json(new { success = false, message = "Stock changed during the count — recount the affected items and complete again." });
        }

        await LogAsync("Create", "StockTake", take.Id.ToString(), $"Stock-take {take.Reference} at {store.Name} — {take.Lines.Count} item(s)");
        return Json(new { success = true, id = take.Id, reference = take.Reference });
    }

    private async Task<int> NextRefAsync()
    {
        var last = await _db.StockTakes.OrderByDescending(t => t.Id).Select(t => t.Reference).FirstOrDefaultAsync();
        return last != null && last.StartsWith("ST") && int.TryParse(last[2..], out var n) ? n + 1 : 1;
    }

    // Stock Takes history — date range + location filter + barcode/ref search.
    public async Task<IActionResult> History(DateTime? from, DateTime? to, int? storeId, string? q, int page = 1, string? format = null)
    {
        ViewData["Title"] = "Stock Takes";
        ViewBag.Stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();

        var fromD = (from ?? DateTime.UtcNow.AddDays(-30)).Date;
        var toD = (to ?? DateTime.UtcNow).Date.AddDays(1).AddSeconds(-1);

        var query = _db.StockTakes.Include(t => t.Store).Include(t => t.Lines)
            .Where(t => t.CreatedAt >= fromD && t.CreatedAt <= toD);
        if (storeId.HasValue) query = query.Where(t => t.StoreId == storeId.Value);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(t => EF.Functions.ILike(t.Reference, $"%{q.Trim()}%"));

        if (format == "csv")
        {
            var all = await query.OrderByDescending(t => t.Id).ToListAsync();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Stock Ref,Location,Staff,Date,Items,Discrepancies");
            foreach (var t in all)
                sb.AppendLine($"\"{t.Reference}\",\"{t.Store.Name}\",\"{t.StaffName}\",{t.CreatedAt:yyyy-MM-dd HH:mm},{t.ItemCount},{t.Discrepancies}");
            var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return File(bytes, "text/csv", $"stock_takes_{DateTime.UtcNow:yyyyMMdd}.csv");
        }

        var total = await query.CountAsync();
        var takes = await query.OrderByDescending(t => t.Id)
            .Skip((page - 1) * PageSize).Take(PageSize).ToListAsync();

        ViewBag.From = fromD; ViewBag.To = (to ?? DateTime.UtcNow).Date;
        ViewBag.StoreId = storeId; ViewBag.Q = q;
        ViewBag.Page = page; ViewBag.TotalPages = (int)Math.Ceiling(total / (double)PageSize); ViewBag.Total = total;
        return View(takes);
    }

    // One completed stock-take's details (header + counted lines + variance).
    public async Task<IActionResult> Details(int id)
    {
        var take = await _db.StockTakes.Include(t => t.Store).Include(t => t.Lines)
            .FirstOrDefaultAsync(t => t.Id == id);
        if (take == null) return NotFound();
        ViewData["Title"] = $"Stock Take {take.Reference}";
        return View(take);
    }
}
