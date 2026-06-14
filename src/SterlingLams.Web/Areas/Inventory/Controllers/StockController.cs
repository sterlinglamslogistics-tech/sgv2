using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

public class StockController : InventoryAreaController
{
    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;
    private readonly IStoreAccessService _access;
    private const int PageSize = 30;

    public StockController(ApplicationDbContext db, IStockService stock, IStoreAccessService access)
    {
        _db = db;
        _stock = stock;
        _access = access;
    }

    public async Task<IActionResult> Index(string q = "", int page = 1, int? categoryId = null)
    {
        ViewData["Title"] = "Stock";

        var stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();

        var pq = _db.Products.Include(p => p.Category).Include(p => p.Images)
            .Include(p => p.Variants.Where(v => v.IsActive))
            .Where(p => p.IsActive).AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
            pq = pq.Where(p => EF.Functions.ILike(p.Name, $"%{q}%")
                            || EF.Functions.ILike(p.Sku ?? "", $"%{q}%")
                            || EF.Functions.ILike(p.Barcode ?? "", $"%{q}%")
                            || p.Variants.Any(v => EF.Functions.ILike(v.Barcode ?? "", $"%{q}%")));

        if (categoryId.HasValue)
            pq = pq.Where(p => p.CategoryId == categoryId.Value);

        var all = await pq.OrderBy(p => p.Name).ToListAsync();
        var ids = all.Select(p => p.Id).ToList();
        var inv = ids.Count > 0
            ? await _db.StoreInventories.Where(si => ids.Contains(si.ProductId)).ToListAsync()
            : new List<StoreInventory>();

        var rows = all.Select(p => new ProductInventoryRow
        {
            ProductId = p.Id,
            ProductName = p.Name,
            Sku = p.Sku,
            CategoryName = p.Category?.Name ?? "—",
            ImageUrl = p.Images.OrderBy(i => i.SortOrder).FirstOrDefault()?.Url,
            LowStockThreshold = p.LowStockThreshold,
            // Product row = the pool (unallocated) row. Variant products track per-variant rows below.
            StockByStore = stores.ToDictionary(
                s => s.Id,
                s => inv.FirstOrDefault(si => si.ProductId == p.Id && si.StoreId == s.Id && si.ProductVariantId == null)?.QuantityOnHand ?? -1),
            Variants = p.Variants.OrderBy(v => v.Name).Select(v => new VariantInventoryRow
            {
                VariantId = v.Id,
                Name = v.Name,
                Sku = v.Sku,
                StockByStore = stores.ToDictionary(
                    s => s.Id,
                    s => inv.FirstOrDefault(si => si.ProductId == p.Id && si.StoreId == s.Id && si.ProductVariantId == v.Id)?.QuantityOnHand ?? -1)
            }).ToList()
        }).ToList();

        var total = rows.Count;
        var pageRows = rows.Skip((page - 1) * PageSize).Take(PageSize).ToList();

        return View(new AdminInventoryViewModel
        {
            Stores = stores,
            Products = pageRows,
            SearchQuery = q,
            CategoryFilter = categoryId?.ToString() ?? "",
            StockFilter = "",
            AvailableCategories = await _db.Categories.OrderBy(c => c.Name).ToListAsync(),
            CurrentPage = page,
            TotalPages = (int)Math.Ceiling(total / (double)PageSize),
            TotalCount = total,
        });
    }

    // Exact barcode/SKU lookup for the scan box — returns the row data needed to insert/highlight
    // a product inline without reloading the page (so unsaved edits in the grid aren't lost).
    [HttpGet]
    public async Task<IActionResult> ScanLookup(string code)
    {
        code = (code ?? "").Trim();
        if (code.Length == 0) return Json(new { found = false });

        var p = await _db.Products
            .Where(x => x.Barcode == code || x.Sku == code
                     || x.Variants.Any(v => v.Barcode == code || v.Sku == code))
            .Select(x => new { x.Id, x.Name, x.Sku, x.IsActive })
            .FirstOrDefaultAsync();
        if (p == null || !p.IsActive) return Json(new { found = false });

        var storeIds = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).Select(s => s.Id).ToListAsync();
        // Pool row per store (the scan inserts a product-level row; variant rows excluded to avoid
        // duplicate StoreId keys).
        var inv = await _db.StoreInventories.Where(si => si.ProductId == p.Id && si.ProductVariantId == null)
            .ToDictionaryAsync(si => si.StoreId, si => si.QuantityOnHand);

        return Json(new
        {
            found = true,
            productId = p.Id,
            productName = p.Name,
            sku = p.Sku,
            stock = storeIds.ToDictionary(id => id, id => inv.TryGetValue(id, out var q) ? q : 0)
        });
    }

    public class StockEdit
    {
        public int ProductId { get; set; }
        public int? VariantId { get; set; }   // null = product-level pool
        public int StoreId { get; set; }
        public int Quantity { get; set; }
    }
    public class BulkStockRequest
    {
        public string? Reason { get; set; }
        public List<StockEdit> Edits { get; set; } = new();
    }

    // Bulk: set stock for many product×store cells, each as a traceable ledger Adjustment
    // tagged with the chosen reason (stock count / received / damage / loss / correction).
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetAll([FromBody] BulkStockRequest req)
    {
        var edits = req?.Edits;
        if (edits == null || edits.Count == 0)
            return Json(new { success = true, count = 0 });

        var reason = string.IsNullOrWhiteSpace(req!.Reason) ? "Stock update" : req.Reason.Trim();
        // Classify the movement from the chosen reason so receipts, damage and shrinkage are
        // first-class (and reportable) instead of all being lumped under "Adjustment". The reason
        // label is still kept on the movement (Reference) for the audit trail.
        var type = MovementTypeForReason(reason);
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var validStoreIds = (await _db.Stores.Where(s => s.IsActive).Select(s => s.Id).ToListAsync()).ToHashSet();
        var validProductIds = (await _db.Products
            .Where(p => edits.Select(e => e.ProductId).Distinct().Contains(p.Id))
            .Select(p => p.Id).ToListAsync()).ToHashSet();

        // Validate any submitted variant ids actually belong to their product (so we never
        // materialize a stock row for a bogus or mismatched variant).
        var variantIds = edits.Where(e => e.VariantId.HasValue).Select(e => e.VariantId!.Value).Distinct().ToList();
        var validVariantPairs = variantIds.Count == 0
            ? new HashSet<(int, int)>()
            : (await _db.ProductVariants.Where(v => variantIds.Contains(v.Id))
                .Select(v => new { v.ProductId, v.Id }).ToListAsync())
                .Select(v => (v.ProductId, v.Id)).ToHashSet();

        var valid = edits
            .Where(e => e.Quantity >= 0 && validStoreIds.Contains(e.StoreId) && validProductIds.Contains(e.ProductId)
                && (e.VariantId == null || validVariantPairs.Contains((e.ProductId, e.VariantId.Value))))
            .ToList();
        if (valid.Count == 0) return Json(new { success = true, count = 0 });

        // Store-level authorization (writes-only): reject edits targeting a branch the user
        // isn't assigned to. Reads are open, so the grid may show all branches' columns.
        var writable = await _access.WritableStoreIdsAsync(User);
        if (valid.Any(e => !writable.Contains(e.StoreId)))
            return Json(new { success = false, message = "You can only edit stock for your assigned branch(es)." });

        var applied = 0;
        await using var tx = await _db.Database.BeginTransactionAsync();

        // Lock the affected StoreInventory rows (fixed ProductId/StoreId order, matching POS
        // checkout & transfers) so a concurrent sale/transfer/edit on the same cell can't race
        // with the read-compute-write below — without this, the delta is computed from a stale
        // read and the save could either lose an update or throw an unhandled concurrency error.
        if (_db.Database.IsNpgsql()) // FOR UPDATE is Postgres-only (SQLite test harness no-ops)
            foreach (var (pid, sid) in valid.Select(e => (e.ProductId, e.StoreId)).Distinct()
                         .OrderBy(p => p.ProductId).ThenBy(p => p.StoreId))
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"StoreInventories\" WHERE \"ProductId\" = {pid} AND \"StoreId\" = {sid} FOR UPDATE");

        foreach (var e in valid)
        {
            // Re-read the EXACT (variant or pool) row under the lock so the delta sets that row to
            // the typed target — not measured against the shared pool via fallback.
            var current = await _stock.GetStockAsync(e.ProductId, e.VariantId, e.StoreId, fallback: false);
            var delta = e.Quantity - current;
            if (delta != 0)
            {
                await _stock.ApplyAsync(e.ProductId, e.VariantId, e.StoreId, delta, type, reason,
                    userId: userId, materializeVariant: e.VariantId.HasValue);
                applied++;
            }
        }

        try
        {
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Json(new { success = false, message = "Stock levels changed while saving. Please refresh and try again." });
        }

        if (applied > 0)
            await LogAsync("Update", "Inventory", null, $"Stock adjustment ({reason}) — {applied} change(s)");
        return Json(new { success = true, count = applied });
    }

    // Maps an adjustment-reason label (from the Stock page's reason picker) to the ledger
    // movement type, so the StockMovement record carries the true nature of the change.
    private static StockMovementType MovementTypeForReason(string reason) => reason switch
    {
        "Received"     => StockMovementType.Purchase,
        "Damage"       => StockMovementType.Damage,
        "Loss / theft" => StockMovementType.Loss,
        _              => StockMovementType.Adjustment,
    };

    // Export per-branch stock levels to CSV.
    public async Task<IActionResult> ExportCsv(string q = "", int? categoryId = null)
    {
        var stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        var pq = _db.Products.Where(p => p.IsActive);
        if (!string.IsNullOrWhiteSpace(q))
            pq = pq.Where(p => EF.Functions.ILike(p.Name, $"%{q}%")
                            || EF.Functions.ILike(p.Sku ?? "", $"%{q}%")
                            || EF.Functions.ILike(p.Barcode ?? "", $"%{q}%")
                            || p.Variants.Any(v => EF.Functions.ILike(v.Barcode ?? "", $"%{q}%")));

        if (categoryId.HasValue)
            pq = pq.Where(p => p.CategoryId == categoryId.Value);

        var all = await pq.OrderBy(p => p.Name).ToListAsync();
        var ids = all.Select(p => p.Id).ToList();
        var inv = ids.Count > 0
            ? await _db.StoreInventories.Where(si => ids.Contains(si.ProductId)).ToListAsync()
            : new List<StoreInventory>();

        static string Csv(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

        var sb = new StringBuilder();
        sb.Append("Product,SKU,Barcode");
        foreach (var s in stores) sb.Append(',').Append(Csv(s.Name.Replace("Sterlin Glams ", "")));
        sb.AppendLine(",Total");

        foreach (var p in all)
        {
            var byStore = stores.Select(s => inv.Where(si => si.ProductId == p.Id && si.StoreId == s.Id).Sum(si => si.QuantityOnHand)).ToList();
            sb.Append(Csv(p.Name)).Append(',').Append(Csv(p.Sku)).Append(',').Append(Csv(p.Barcode));
            foreach (var v in byStore) sb.Append(',').Append(v);
            sb.Append(',').Append(byStore.Sum()).AppendLine();
        }

        await LogAsync("Export", "Inventory", null, $"Exported stock for {all.Count} product(s) to CSV");
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv", $"stock_{DateTime.UtcNow:yyyyMMdd_HHmm}.csv");
    }
}
