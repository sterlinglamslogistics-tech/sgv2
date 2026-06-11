using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

public class TransfersController : InventoryAreaController
{
    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;

    public TransfersController(ApplicationDbContext db, IStockService stock)
    {
        _db = db;
        _stock = stock;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Transfers";
        var transfers = await _db.StockTransfers
            .Include(t => t.FromStore).Include(t => t.ToStore).Include(t => t.Items)
            .OrderByDescending(t => t.CreatedAt).Take(100).ToListAsync();
        return View(transfers);
    }

    public async Task<IActionResult> Create()
    {
        ViewData["Title"] = "New Transfer";
        ViewBag.Stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        return View();
    }

    // Search products with stock at the source branch (JSON).
    [HttpGet]
    public async Task<IActionResult> SearchStock(string? q, int fromStoreId)
    {
        q = (q ?? "").Trim();
        var query = _db.Products.Where(p => p.IsActive);
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
                stock = p.StoreInventories.Where(si => si.StoreId == fromStoreId).Select(si => si.QuantityOnHand).FirstOrDefault()
            })
            .Where(x => x.stock > 0)
            .ToListAsync();
        return Json(products);
    }

    public class TransferLine { public int ProductId { get; set; } public int Quantity { get; set; } }
    public class TransferRequest
    {
        public int FromStoreId { get; set; }
        public int ToStoreId { get; set; }
        public string? Note { get; set; }
        public List<TransferLine> Items { get; set; } = new();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save([FromBody] TransferRequest req)
    {
        if (req.FromStoreId == req.ToStoreId)
            return Json(new { success = false, message = "Source and destination must be different branches." });

        var from = await _db.Stores.FirstOrDefaultAsync(s => s.Id == req.FromStoreId && s.IsActive);
        var to = await _db.Stores.FirstOrDefaultAsync(s => s.Id == req.ToStoreId && s.IsActive);
        if (from == null || to == null) return Json(new { success = false, message = "Pick valid branches." });

        var lines = (req.Items ?? new()).Where(l => l.Quantity > 0).ToList();
        if (lines.Count == 0) return Json(new { success = false, message = "Add at least one product." });

        var ids = lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await _db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        foreach (var grp in lines.GroupBy(l => l.ProductId))
        {
            if (!products.TryGetValue(grp.Key, out var prod))
                return Json(new { success = false, message = "A product no longer exists." });
            var requested = grp.Sum(l => l.Quantity);
            if (requested > await _stock.GetStockAsync(grp.Key, from.Id))
                return Json(new { success = false, message = $"Not enough stock of '{prod.Name}' at {from.Name}." });
        }

        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var now = DateTime.UtcNow;
        var transferNumber = $"TRF-{now:yyMMdd}-{now:HHmmssfff}";

        await using var tx = await _db.Database.BeginTransactionAsync();

        var transfer = new StockTransfer
        {
            TransferNumber = transferNumber,
            FromStoreId = from.Id,
            ToStoreId = to.Id,
            CreatedByUserId = userId,
            Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
            CreatedAt = now
        };

        foreach (var grp in lines.GroupBy(l => l.ProductId))
        {
            var prod = products[grp.Key];
            var qty = grp.Sum(l => l.Quantity);
            transfer.Items.Add(new StockTransferItem
            {
                ProductId = prod.Id, ProductName = prod.Name, Quantity = qty
            });
            await _stock.ApplyAsync(prod.Id, null, from.Id, -qty, StockMovementType.Transfer, transferNumber, $"To {to.Name}", userId);
            await _stock.ApplyAsync(prod.Id, null, to.Id, qty, StockMovementType.Transfer, transferNumber, $"From {from.Name}", userId);
        }

        _db.StockTransfers.Add(transfer);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        await LogAsync("Create", "Transfer", transfer.Id.ToString(),
            $"Transfer {transferNumber}: {from.Name} → {to.Name} ({transfer.Items.Count} line(s))");

        return Json(new { success = true, id = transfer.Id });
    }

    public async Task<IActionResult> Detail(int id)
    {
        var transfer = await _db.StockTransfers
            .Include(t => t.FromStore).Include(t => t.ToStore).Include(t => t.Items)
            .FirstOrDefaultAsync(t => t.Id == id);
        if (transfer == null) return NotFound();
        ViewData["Title"] = transfer.TransferNumber;
        return View(transfer);
    }
}
