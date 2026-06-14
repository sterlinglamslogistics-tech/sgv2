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
    private const int PageSize = 40;

    public StocktakeController(ApplicationDbContext db, IStockService stock, IStoreAccessService access)
    {
        _db = db;
        _stock = stock;
        _access = access;
    }

    // Count sheet for one branch: enter physical counts, see variance, reconcile.
    public async Task<IActionResult> Index(int? storeId, string q = "", int page = 1, int? categoryId = null)
    {
        ViewData["Title"] = "Stock-take";
        ViewBag.Stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        ViewBag.Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync();
        ViewBag.StoreId = storeId;
        ViewBag.Q = q;
        ViewBag.Page = page;
        ViewBag.CategoryId = categoryId;

        if (storeId == null) return View(new List<StocktakeRow>());

        var pq = _db.Products.Where(p => p.IsActive);
        if (!string.IsNullOrWhiteSpace(q))
            pq = pq.Where(p => EF.Functions.ILike(p.Name, $"%{q}%")
                            || EF.Functions.ILike(p.Sku ?? "", $"%{q}%")
                            || EF.Functions.ILike(p.Barcode ?? "", $"%{q}%")
                            || p.Variants.Any(v => EF.Functions.ILike(v.Barcode ?? "", $"%{q}%")));

        if (categoryId.HasValue)
            pq = pq.Where(p => p.CategoryId == categoryId.Value);

        var rows = await pq.OrderBy(p => p.Name)
            .Select(p => new StocktakeRow
            {
                ProductId = p.Id,
                Name = p.Name,
                Sku = p.Sku,
                // Product-level pool row; variant products also list per-variant rows below.
                System = p.StoreInventories.Where(si => si.StoreId == storeId && si.ProductVariantId == null)
                    .Select(si => (int?)si.QuantityOnHand).FirstOrDefault() ?? 0,
                Variants = p.Variants.Where(v => v.IsActive).OrderBy(v => v.Name)
                    .Select(v => new StocktakeVariantRow
                    {
                        VariantId = v.Id,
                        Name = v.Name,
                        Sku = v.Sku,
                        System = p.StoreInventories.Where(si => si.StoreId == storeId && si.ProductVariantId == v.Id)
                            .Select(si => (int?)si.QuantityOnHand).FirstOrDefault() ?? 0
                    }).ToList()
            })
            .ToListAsync();

        ViewBag.TotalPages = (int)Math.Ceiling(rows.Count / (double)PageSize);
        return View(rows.Skip((page - 1) * PageSize).Take(PageSize).ToList());
    }

    // Exact barcode/SKU lookup for the scan box — returns the row data needed to insert/highlight
    // a product inline without reloading the page (so unsaved counts aren't lost).
    [HttpGet]
    public async Task<IActionResult> ScanLookup(string code, int storeId)
    {
        code = (code ?? "").Trim();
        if (code.Length == 0) return Json(new { found = false });

        var p = await _db.Products
            .Where(x => x.Barcode == code || x.Sku == code
                     || x.Variants.Any(v => v.Barcode == code || v.Sku == code))
            .Select(x => new { x.Id, x.Name, x.Sku, x.IsActive })
            .FirstOrDefaultAsync();
        if (p == null || !p.IsActive) return Json(new { found = false });

        var system = await _db.StoreInventories.Where(si => si.ProductId == p.Id && si.StoreId == storeId)
            .Select(si => (int?)si.QuantityOnHand).FirstOrDefaultAsync() ?? 0;

        return Json(new { found = true, productId = p.Id, name = p.Name, sku = p.Sku, system });
    }

    public class CountEntry { public int ProductId { get; set; } public int? VariantId { get; set; } public int Counted { get; set; } }
    public class StocktakeRequest { public int StoreId { get; set; } public List<CountEntry> Counts { get; set; } = new(); }

    // Reconcile: set the system quantity to the counted value (ledger Adjustment, reason "Stock-take").
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Apply([FromBody] StocktakeRequest req)
    {
        var store = await _db.Stores.FirstOrDefaultAsync(s => s.Id == req.StoreId && s.IsActive);
        if (store == null) return Json(new { success = false, message = "Invalid branch." });
        // Store-level authorization (writes-only).
        if (!await _access.CanWriteAsync(User, store.Id))
            return Json(new { success = false, message = "You can only run a stock-take for your assigned branch(es)." });
        if (req.Counts == null || req.Counts.Count == 0) return Json(new { success = true, count = 0 });

        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var ids = req.Counts.Select(c => c.ProductId).Distinct().ToList();
        var validIds = (await _db.Products.Where(p => ids.Contains(p.Id)).Select(p => p.Id).ToListAsync()).ToHashSet();

        // Validate submitted variant ids belong to their product.
        var variantIds = req.Counts.Where(c => c.VariantId.HasValue).Select(c => c.VariantId!.Value).Distinct().ToList();
        var validVariantPairs = variantIds.Count == 0
            ? new HashSet<(int, int)>()
            : (await _db.ProductVariants.Where(v => variantIds.Contains(v.Id))
                .Select(v => new { v.ProductId, v.Id }).ToListAsync())
                .Select(v => (v.ProductId, v.Id)).ToHashSet();

        var valid = req.Counts.Where(c => c.Counted >= 0 && validIds.Contains(c.ProductId)
            && (c.VariantId == null || validVariantPairs.Contains((c.ProductId, c.VariantId.Value)))).ToList();
        if (valid.Count == 0) return Json(new { success = true, count = 0 });

        var applied = 0;
        await using var tx = await _db.Database.BeginTransactionAsync();

        // Lock the counted rows (fixed ProductId order, matching POS checkout & transfers) so a
        // concurrent sale at this branch can't slip in between the system-qty read and the
        // reconciling adjustment — which would otherwise make the stock-take overwrite that sale.
        foreach (var pid in valid.Select(c => c.ProductId).Distinct().OrderBy(id => id))
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"StoreInventories\" WHERE \"ProductId\" = {pid} AND \"StoreId\" = {store.Id} FOR UPDATE");

        foreach (var c in valid)
        {
            // Re-read the EXACT (variant or pool) row under the lock so the variance is measured
            // against that row, not the shared pool via fallback.
            var current = await _stock.GetStockAsync(c.ProductId, c.VariantId, store.Id, fallback: false);
            var delta = c.Counted - current;
            if (delta != 0)
            {
                await _stock.ApplyAsync(c.ProductId, c.VariantId, store.Id, delta,
                    StockMovementType.Adjustment, "Stock-take", userId: userId,
                    materializeVariant: c.VariantId.HasValue);
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
            return Json(new { success = false, message = "Stock changed during the count. Please recount the affected items and apply again." });
        }

        await LogAsync("Update", "Inventory", null, $"Stock-take at {store.Name} — {applied} adjustment(s)");
        return Json(new { success = true, count = applied });
    }
}

public class StocktakeRow
{
    public int ProductId { get; set; }
    public string Name { get; set; } = "";
    public string? Sku { get; set; }
    public int System { get; set; }
    public List<StocktakeVariantRow> Variants { get; set; } = new();
    public bool HasVariants => Variants.Count > 0;
}

public class StocktakeVariantRow
{
    public int VariantId { get; set; }
    public string Name { get; set; } = "";
    public string? Sku { get; set; }
    public int System { get; set; }
}
