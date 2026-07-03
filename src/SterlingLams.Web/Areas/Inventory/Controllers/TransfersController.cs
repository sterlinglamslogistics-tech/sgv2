using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

public class TransfersController : InventoryAreaController
{
    private readonly ApplicationDbContext _db;
    private readonly ITransferWorkflowService _workflow;
    private readonly IStoreAccessService _access;

    public TransfersController(ApplicationDbContext db, ITransferWorkflowService workflow, IStoreAccessService access)
    {
        _db = db;
        _workflow = workflow;
        _access = access;
    }

    // Store-level authorization (writes-only): a non-admin may only act on a transfer that
    // touches a branch they're assigned to. `from`/`to` pick which side(s) the action affects.
    // Returns an error result when denied, otherwise null.
    private async Task<IActionResult?> DenyIfNoStoreAccessAsync(int id, bool from, bool to)
    {
        var t = await _db.StockTransfers.Where(x => x.Id == id)
            .Select(x => new { x.FromStoreId, x.ToStoreId }).FirstOrDefaultAsync();
        if (t == null) return null; // let the workflow report "not found"
        var writable = await _access.WritableStoreIdsAsync(User);
        var ok = (from && writable.Contains(t.FromStoreId)) || (to && writable.Contains(t.ToStoreId));
        return ok ? null : Json(new { success = false, message = "You don't have access to this transfer's branch." });
    }

    public async Task<IActionResult> Index(int? storeId, string status = "all")
    {
        ViewData["Title"] = "Transfers";
        ViewBag.Stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        ViewBag.StoreId = storeId;
        ViewBag.Status = status;

        var baseQuery = _db.StockTransfers.AsQueryable();
        if (storeId.HasValue)
            baseQuery = baseQuery.Where(t => t.FromStoreId == storeId.Value || t.ToStoreId == storeId.Value);

        var counts = await baseQuery.GroupBy(t => t.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync();
        ViewBag.StatusCounts = counts.ToDictionary(c => c.Status, c => c.Count);
        ViewBag.TotalCount = counts.Sum(c => c.Count);

        IQueryable<StockTransfer> query = baseQuery.Include(t => t.FromStore).Include(t => t.ToStore).Include(t => t.Items);
        query = status switch
        {
            "pending" => query.Where(t => t.Status == TransferStatus.PendingApproval),
            "approved" => query.Where(t => t.Status == TransferStatus.Approved),
            "intransit" => query.Where(t => t.Status == TransferStatus.InTransit),
            "partial" => query.Where(t => t.Status == TransferStatus.PartiallyReceived),
            "completed" => query.Where(t => t.Status == TransferStatus.Completed),
            "rejected" => query.Where(t => t.Status == TransferStatus.Rejected),
            "cancelled" => query.Where(t => t.Status == TransferStatus.Cancelled),
            _ => query
        };

        var transfers = await query.OrderByDescending(t => t.CreatedAt).Take(100).ToListAsync();
        return View(transfers);
    }

    public async Task<IActionResult> Create()
    {
        ViewData["Title"] = "Request Transfer";
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
                                  || EF.Functions.ILike(p.Barcode ?? "", $"%{q}%")
                                  || p.Variants.Any(v => EF.Functions.ILike(v.Barcode ?? "", $"%{q}%")));

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

    // ActionName keeps the public route /Inventory/Transfers/Request; the method is renamed so it
    // doesn't hide ControllerBase.Request (the HttpRequest) — was a CS0108 build warning.
    [HttpPost, ActionName("Request"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestTransfer([FromBody] TransferRequest req)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var writable = await _access.WritableStoreIdsAsync(User);
        if (!writable.Contains(req.FromStoreId) && !writable.Contains(req.ToStoreId))
            return Json(new { success = false, message = "You can only request transfers involving your assigned branch(es)." });
        var (success, error, id) = await _workflow.RequestAsync(req, userId);
        if (!success) return Json(new { success = false, message = error });

        var transfer = await _db.StockTransfers
            .Include(t => t.FromStore).Include(t => t.ToStore).Include(t => t.Items)
            .FirstAsync(t => t.Id == id);
        await LogAsync("Create", "Transfer", id.ToString(),
            $"Transfer {transfer.TransferNumber} requested: {transfer.FromStore.Name} → {transfer.ToStore.Name} ({transfer.Items.Count} line(s))");

        return Json(new { success = true, id });
    }

    public class ItemsRequest { public List<ItemQtyDto> Items { get; set; } = new(); }
    public class ReasonRequest { public string Reason { get; set; } = ""; }
    public class DispatchRequest
    {
        public List<ItemQtyDto> Items { get; set; } = new();
        public string? TrackingNumber { get; set; }
        public string? CourierName { get; set; }
        public string? Notes { get; set; }
    }
    public class ReceiveLine
    {
        public int ItemId { get; set; }
        public int Received { get; set; }
        public int Damaged { get; set; }
        public int WontFulfil { get; set; }
    }
    public class ReceiveRequest
    {
        public List<ReceiveLine> Items { get; set; } = new();
        public string? Notes { get; set; }
    }

    // Approving/rejecting a transfer is an administrator decision only — inventory staff can create
    // and request transfers, but a full admin signs off before stock moves.
    private const string AdminRole = "Admin";

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, [FromBody] ItemsRequest req)
    {
        if (!User.IsInRole(AdminRole)) return Json(new { success = false, message = "Only an administrator can approve transfers." });
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var deny = await DenyIfNoStoreAccessAsync(id, from: true, to: false); if (deny != null) return deny;
        var result = await _workflow.ApproveAsync(id, req.Items, userId);
        if (!result.Success) return Json(new { success = false, message = result.Error });
        await LogTransferAsync(id, "Approve", "Approved");
        return Json(new { success = true });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int id, [FromBody] ReasonRequest req)
    {
        if (!User.IsInRole(AdminRole)) return Json(new { success = false, message = "Only an administrator can reject transfers." });
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var deny = await DenyIfNoStoreAccessAsync(id, from: true, to: false); if (deny != null) return deny;
        var result = await _workflow.RejectAsync(id, req.Reason, userId);
        if (!result.Success) return Json(new { success = false, message = result.Error });
        await LogTransferAsync(id, "Reject", $"Rejected ({req.Reason})");
        return Json(new { success = true });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Dispatch(int id, [FromBody] DispatchRequest req)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var deny = await DenyIfNoStoreAccessAsync(id, from: true, to: false); if (deny != null) return deny;
        var result = await _workflow.DispatchAsync(id, req.Items, req.TrackingNumber, req.CourierName, req.Notes, userId);
        if (!result.Success) return Json(new { success = false, message = result.Error });
        await LogTransferAsync(id, "Dispatch", "Dispatched");
        return Json(new { success = true });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Receive(int id, [FromBody] ReceiveRequest req)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var deny = await DenyIfNoStoreAccessAsync(id, from: false, to: true); if (deny != null) return deny;
        var lines = req.Items.Select(i => new ReceiveLineDto(i.ItemId, i.Received, i.Damaged, i.WontFulfil)).ToList();
        var result = await _workflow.ReceiveAsync(id, lines, req.Notes, userId);
        if (!result.Success) return Json(new { success = false, message = result.Error });
        await LogTransferAsync(id, "Receive", "Received");
        return Json(new { success = true });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Complete(int id)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var deny = await DenyIfNoStoreAccessAsync(id, from: false, to: true); if (deny != null) return deny;
        var result = await _workflow.CompleteAsync(id, userId);
        if (!result.Success) return Json(new { success = false, message = result.Error });
        await LogTransferAsync(id, "Complete", "Marked as completed (shortage acknowledged)");
        return Json(new { success = true });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, [FromBody] ReasonRequest req)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var deny = await DenyIfNoStoreAccessAsync(id, from: true, to: true); if (deny != null) return deny;
        var result = await _workflow.CancelAsync(id, req.Reason, userId);
        if (!result.Success) return Json(new { success = false, message = result.Error });
        await LogTransferAsync(id, "Cancel", $"Cancelled ({req.Reason})");
        return Json(new { success = true });
    }

    private async Task LogTransferAsync(int id, string action, string description)
    {
        var tn = await _db.StockTransfers.Where(t => t.Id == id).Select(t => t.TransferNumber).FirstOrDefaultAsync();
        await LogAsync(action, "Transfer", id.ToString(), $"Transfer {tn}: {description}");
    }

    public async Task<IActionResult> Detail(int id)
    {
        var transfer = await _db.StockTransfers
            .Include(t => t.FromStore).Include(t => t.ToStore).Include(t => t.Items)
            .FirstOrDefaultAsync(t => t.Id == id);
        if (transfer == null) return NotFound();
        ViewData["Title"] = transfer.TransferNumber;

        var userIds = new[]
            {
                transfer.CreatedByUserId, transfer.ApprovedByUserId, transfer.RejectedByUserId,
                transfer.DispatchedByUserId, transfer.ReceivedByUserId, transfer.CancelledByUserId
            }
            .Where(uid => uid != null).Cast<string>().Distinct().ToList();
        ViewBag.UserNames = await _db.Users.Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Email ?? u.UserName ?? u.Id);

        var productIds = transfer.Items.Select(i => i.ProductId).Distinct().ToList();
        // A product can have several StoreInventory rows at one store (the null-variant pool + each
        // variant row), so sum available per product rather than ToDictionary on ProductId (which
        // would throw on the duplicate key).
        var invRows = await _db.StoreInventories
            .Where(si => si.StoreId == transfer.FromStoreId && productIds.Contains(si.ProductId))
            .Select(si => new { si.ProductId, si.QuantityOnHand, si.QuantityReserved })
            .ToListAsync();
        ViewBag.AvailableQty = invRows
            .GroupBy(x => x.ProductId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.QuantityOnHand - x.QuantityReserved));

        return View(transfer);
    }

    // Printable transfer receipt — minimal standalone page (no app chrome) that auto-prints.
    public async Task<IActionResult> Receipt(int id)
    {
        var transfer = await _db.StockTransfers
            .Include(t => t.FromStore).Include(t => t.ToStore).Include(t => t.Items)
            .FirstOrDefaultAsync(t => t.Id == id);
        if (transfer == null) return NotFound();

        var userIds = new[] { transfer.CreatedByUserId, transfer.DispatchedByUserId, transfer.ReceivedByUserId }
            .Where(uid => uid != null).Cast<string>().Distinct().ToList();
        ViewBag.UserNames = await _db.Users.Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Email ?? u.UserName ?? u.Id);
        ViewData["Title"] = $"Receipt {transfer.TransferNumber}";
        return View(transfer);
    }

    /// <summary>Printable transfer manifest for the receiving branch — QR (scan to open), product
    /// photos, approved quantities + total pieces, and who created/approved it. Available once the
    /// transfer is approved.</summary>
    public async Task<IActionResult> Manifest(int id)
    {
        var transfer = await _db.StockTransfers
            .Include(t => t.FromStore).Include(t => t.ToStore).Include(t => t.Items)
            .FirstOrDefaultAsync(t => t.Id == id);
        if (transfer == null) return NotFound();
        if (transfer.ApprovedAt == null)
        {
            TempData["Error"] = "The manifest is available once the transfer is approved.";
            return RedirectToAction(nameof(Detail), new { id });
        }

        // Primary image per product.
        var productIds = transfer.Items.Select(i => i.ProductId).Distinct().ToList();
        var imgRows = await _db.ProductImages
            .Where(img => productIds.Contains(img.ProductId))
            .OrderByDescending(img => img.IsPrimary).ThenBy(img => img.SortOrder)
            .Select(img => new { img.ProductId, img.Url })
            .ToListAsync();
        ViewBag.ImageByProduct = imgRows.GroupBy(x => x.ProductId).ToDictionary(g => g.Key, g => g.First().Url);

        // Created-by / approved-by display names.
        var uids = new[] { transfer.CreatedByUserId, transfer.ApprovedByUserId }
            .Where(u => u != null).Cast<string>().Distinct().ToList();
        ViewBag.Names = (await _db.Users.Where(u => uids.Contains(u.Id))
                .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email }).ToListAsync())
            .ToDictionary(u => u.Id, u =>
            {
                var n = $"{u.FirstName} {u.LastName}".Trim();
                return string.IsNullOrWhiteSpace(n) ? (u.Email ?? u.Id) : n;
            });

        // QR encodes the manifest URL so the receiver can scan to open it.
        var url = $"{Request.Scheme}://{Request.Host}/Inventory/Transfers/Manifest/{id}";
        using var gen = new QRCodeGenerator();
        using var qrData = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(qrData).GetGraphic(5);
        ViewBag.QrDataUri = "data:image/png;base64," + Convert.ToBase64String(png);

        ViewData["Title"] = $"Manifest {transfer.TransferNumber}";
        return View(transfer);
    }
}
