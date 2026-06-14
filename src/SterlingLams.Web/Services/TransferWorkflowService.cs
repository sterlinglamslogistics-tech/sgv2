using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

public record TransferActionResult(bool Success, string? Error = null)
{
    public static TransferActionResult Ok() => new(true);
    public static TransferActionResult Fail(string error) => new(false, error);
}

public record ItemQtyDto(int ItemId, int Qty);

public class TransferLine { public int ProductId { get; set; } public int Quantity { get; set; } }
public class TransferRequest
{
    public int FromStoreId { get; set; }
    public int ToStoreId { get; set; }
    public string? Note { get; set; }
    public List<TransferLine> Items { get; set; } = new();
}

/// <summary>
/// Drives the inter-branch transfer workflow: Requested → Approved → In Transit →
/// Received (Completed or Partially Received), with Rejected/Cancelled side-branches.
/// Reservations are tracked via StoreInventory.QuantityReserved; stock actually moves
/// (via IStockService.ApplyAsync, recorded in the StockMovement ledger) at Dispatch
/// and Receive.
/// </summary>
public interface ITransferWorkflowService
{
    Task<(bool Success, string? Error, int? Id)> RequestAsync(TransferRequest req, string? userId);
    Task<TransferActionResult> ApproveAsync(int transferId, List<ItemQtyDto> items, string? userId);
    Task<TransferActionResult> RejectAsync(int transferId, string reason, string? userId);
    Task<TransferActionResult> DispatchAsync(int transferId, List<ItemQtyDto> items, string? trackingNumber, string? courierName, string? notes, string? userId);
    Task<TransferActionResult> ReceiveAsync(int transferId, List<ItemQtyDto> items, string? notes, string? userId);
    Task<TransferActionResult> CompleteAsync(int transferId, string? userId);
    Task<TransferActionResult> CancelAsync(int transferId, string reason, string? userId);
}

public class TransferWorkflowService : ITransferWorkflowService
{
    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;

    public TransferWorkflowService(ApplicationDbContext db, IStockService stock)
    {
        _db = db;
        _stock = stock;
    }

    /// <summary>
    /// Acquires Postgres row locks (SELECT ... FOR UPDATE) on the given StoreInventory rows,
    /// in a fixed (ProductId, StoreId) order, before they're read/mutated. Serializes concurrent
    /// approvals/dispatches/cancellations (and POS sales/reservations, which lock the same rows
    /// in the same order) instead of racing on stale QuantityReserved/QuantityOnHand snapshots.
    /// Caller must already be inside a transaction. Rows that don't exist yet lock nothing.
    /// </summary>
    private async Task LockInventoryRowsAsync(IEnumerable<(int ProductId, int StoreId)> pairs)
    {
        if (!_db.Database.IsNpgsql()) return; // FOR UPDATE is Postgres-only (SQLite test harness no-ops)
        foreach (var (pid, sid) in pairs.Distinct().OrderBy(p => p.ProductId).ThenBy(p => p.StoreId))
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"StoreInventories\" WHERE \"ProductId\" = {pid} AND \"StoreId\" = {sid} FOR UPDATE");
    }

    public async Task<(bool Success, string? Error, int? Id)> RequestAsync(TransferRequest req, string? userId)
    {
        if (req.FromStoreId == req.ToStoreId)
            return (false, "Source and destination must be different branches.", null);

        var from = await _db.Stores.FirstOrDefaultAsync(s => s.Id == req.FromStoreId && s.IsActive);
        var to = await _db.Stores.FirstOrDefaultAsync(s => s.Id == req.ToStoreId && s.IsActive);
        if (from == null || to == null) return (false, "Pick valid branches.", null);

        var lines = (req.Items ?? new()).Where(l => l.Quantity > 0).ToList();
        if (lines.Count == 0) return (false, "Add at least one product.", null);

        var ids = lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await _db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        if (lines.Select(l => l.ProductId).Distinct().Any(id => !products.ContainsKey(id)))
            return (false, "A product no longer exists.", null);

        var now = DateTime.UtcNow;
        var transferNumber = $"TRF-{now:yyMMdd}-{now:HHmmssfff}";

        var transfer = new StockTransfer
        {
            TransferNumber = transferNumber,
            FromStoreId = from.Id,
            ToStoreId = to.Id,
            Status = TransferStatus.PendingApproval,
            CreatedByUserId = userId,
            Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
            CreatedAt = now
        };

        foreach (var grp in lines.GroupBy(l => l.ProductId))
        {
            var prod = products[grp.Key];
            transfer.Items.Add(new StockTransferItem
            {
                ProductId = prod.Id,
                ProductName = prod.Name,
                RequestedQty = grp.Sum(l => l.Quantity)
            });
        }

        await using var tx = await _db.Database.BeginTransactionAsync();
        _db.StockTransfers.Add(transfer);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        return (true, null, transfer.Id);
    }

    public async Task<TransferActionResult> ApproveAsync(int transferId, List<ItemQtyDto> items, string? userId)
    {
        var transfer = await _db.StockTransfers
            .Include(t => t.Items).Include(t => t.FromStore)
            .FirstOrDefaultAsync(t => t.Id == transferId);
        if (transfer == null) return TransferActionResult.Fail("Transfer not found.");
        if (transfer.Status != TransferStatus.PendingApproval)
            return TransferActionResult.Fail("Transfer is not pending approval.");

        var qtyByItem = items.ToDictionary(i => i.ItemId, i => i.Qty);
        var total = 0;
        foreach (var item in transfer.Items)
        {
            var qty = qtyByItem.GetValueOrDefault(item.Id, 0);
            if (qty < 0 || qty > item.RequestedQty)
                return TransferActionResult.Fail($"Approved quantity for '{item.ProductName}' must be between 0 and {item.RequestedQty}.");
            total += qty;
        }
        if (total == 0)
            return TransferActionResult.Fail("Approve at least one item, or use Reject.");

        var productIds = transfer.Items.Select(i => i.ProductId).Distinct().ToList();

        await using var tx = await _db.Database.BeginTransactionAsync();

        // Lock FromStore's StoreInventory rows for these products, then re-check
        // availability against the now-locked, up-to-date balances — closes the
        // check-then-act window where two approvals could both pass the pre-check
        // and over-reserve the same stock.
        await LockInventoryRowsAsync(productIds.Select(pid => (pid, transfer.FromStoreId)));

        var invs = await _db.StoreInventories
            .Where(si => si.StoreId == transfer.FromStoreId && productIds.Contains(si.ProductId)
                && si.ProductVariantId == null)   // transfers are product-level (pool) in Phase 1 of variant stock
            .ToListAsync();

        foreach (var item in transfer.Items)
        {
            var qty = qtyByItem.GetValueOrDefault(item.Id, 0);
            if (qty == 0) continue;
            var available = invs.FirstOrDefault(i => i.ProductId == item.ProductId)?.AvailableQuantity ?? 0;
            if (qty > available)
                return TransferActionResult.Fail($"Not enough available stock of '{item.ProductName}' at {transfer.FromStore.Name} (requested {qty}, available {available}).");
        }

        foreach (var item in transfer.Items)
        {
            var qty = qtyByItem.GetValueOrDefault(item.Id, 0);
            item.ApprovedQty = qty;
            if (qty > 0)
                invs.First(i => i.ProductId == item.ProductId).QuantityReserved += qty;
        }

        transfer.Status = TransferStatus.Approved;
        transfer.ApprovedByUserId = userId;
        transfer.ApprovedAt = DateTime.UtcNow;

        try
        {
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return TransferActionResult.Fail("Stock levels changed — please try again.");
        }
        return TransferActionResult.Ok();
    }

    public async Task<TransferActionResult> RejectAsync(int transferId, string reason, string? userId)
    {
        reason = (reason ?? "").Trim();
        if (reason.Length == 0) return TransferActionResult.Fail("A rejection reason is required.");

        var transfer = await _db.StockTransfers.FirstOrDefaultAsync(t => t.Id == transferId);
        if (transfer == null) return TransferActionResult.Fail("Transfer not found.");
        if (transfer.Status != TransferStatus.PendingApproval)
            return TransferActionResult.Fail("Transfer is not pending approval.");

        transfer.Status = TransferStatus.Rejected;
        transfer.RejectedByUserId = userId;
        transfer.RejectedAt = DateTime.UtcNow;
        transfer.RejectionReason = reason;

        await _db.SaveChangesAsync();
        return TransferActionResult.Ok();
    }

    public async Task<TransferActionResult> DispatchAsync(int transferId, List<ItemQtyDto> items, string? trackingNumber, string? courierName, string? notes, string? userId)
    {
        var transfer = await _db.StockTransfers
            .Include(t => t.Items).Include(t => t.ToStore)
            .FirstOrDefaultAsync(t => t.Id == transferId);
        if (transfer == null) return TransferActionResult.Fail("Transfer not found.");
        if (transfer.Status != TransferStatus.Approved)
            return TransferActionResult.Fail("Transfer is not approved.");

        var qtyByItem = items.ToDictionary(i => i.ItemId, i => i.Qty);
        var total = 0;
        foreach (var item in transfer.Items)
        {
            var qty = qtyByItem.GetValueOrDefault(item.Id, 0);
            var approved = item.ApprovedQty ?? 0;
            if (qty < 0 || qty > approved)
                return TransferActionResult.Fail($"Dispatched quantity for '{item.ProductName}' must be between 0 and {approved}.");
            total += qty;
        }
        if (total == 0)
            return TransferActionResult.Fail("Dispatch at least one item.");

        var productIds = transfer.Items.Select(i => i.ProductId).Distinct().ToList();
        var now = DateTime.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync();

        // Lock FromStore's StoreInventory rows before reading/mutating QuantityReserved
        // and QuantityOnHand, so a concurrent approval/dispatch/sale on the same product
        // can't interleave with this dispatch's release-and-decrement.
        await LockInventoryRowsAsync(productIds.Select(pid => (pid, transfer.FromStoreId)));

        var invs = await _db.StoreInventories
            .Where(si => si.StoreId == transfer.FromStoreId && productIds.Contains(si.ProductId)
                && si.ProductVariantId == null)   // transfers are product-level (pool) in Phase 1 of variant stock
            .ToListAsync();

        foreach (var item in transfer.Items)
        {
            var dispatched = qtyByItem.GetValueOrDefault(item.Id, 0);
            var approved = item.ApprovedQty ?? 0;

            var inv = invs.FirstOrDefault(i => i.ProductId == item.ProductId);
            if (inv != null) inv.QuantityReserved = Math.Max(0, inv.QuantityReserved - approved);

            if (dispatched > 0)
                await _stock.ApplyAsync(item.ProductId, item.ProductVariantId, transfer.FromStoreId, -dispatched,
                    StockMovementType.Transfer, transfer.TransferNumber, $"Dispatched to {transfer.ToStore.Name}", userId);

            item.DispatchedQty = dispatched;
        }

        transfer.Status = TransferStatus.InTransit;
        transfer.DispatchedByUserId = userId;
        transfer.DispatchedAt = now;
        transfer.TrackingNumber = string.IsNullOrWhiteSpace(trackingNumber) ? $"TRK-{now:yyMMdd}-{now:HHmmssfff}" : trackingNumber.Trim();
        transfer.CourierName = string.IsNullOrWhiteSpace(courierName) ? null : courierName.Trim();
        transfer.DispatchNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

        try
        {
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (InsufficientStockException)
        {
            return TransferActionResult.Fail("Stock levels changed — not enough stock to dispatch. Please review and try again.");
        }
        catch (DbUpdateConcurrencyException)
        {
            return TransferActionResult.Fail("Stock levels changed — please try again.");
        }
        return TransferActionResult.Ok();
    }

    public async Task<TransferActionResult> ReceiveAsync(int transferId, List<ItemQtyDto> items, string? notes, string? userId)
    {
        var transfer = await _db.StockTransfers
            .Include(t => t.Items).Include(t => t.FromStore)
            .FirstOrDefaultAsync(t => t.Id == transferId);
        if (transfer == null) return TransferActionResult.Fail("Transfer not found.");
        if (transfer.Status != TransferStatus.InTransit)
            return TransferActionResult.Fail("Transfer is not in transit.");

        var qtyByItem = items.ToDictionary(i => i.ItemId, i => i.Qty);
        foreach (var item in transfer.Items)
        {
            var qty = qtyByItem.GetValueOrDefault(item.Id, 0);
            var dispatched = item.DispatchedQty ?? 0;
            if (qty < 0 || qty > dispatched)
                return TransferActionResult.Fail($"Received quantity for '{item.ProductName}' must be between 0 and {dispatched}.");
        }

        var now = DateTime.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync();

        var allFull = true;
        foreach (var item in transfer.Items)
        {
            var received = qtyByItem.GetValueOrDefault(item.Id, 0);
            if (received > 0)
                await _stock.ApplyAsync(item.ProductId, item.ProductVariantId, transfer.ToStoreId, received,
                    StockMovementType.Transfer, transfer.TransferNumber, $"Received from {transfer.FromStore.Name}", userId);

            item.ReceivedQty = received;
            if (received != (item.DispatchedQty ?? 0)) allFull = false;
        }

        transfer.Status = allFull ? TransferStatus.Completed : TransferStatus.PartiallyReceived;
        transfer.ReceivedByUserId = userId;
        transfer.ReceivedAt = now;
        transfer.ReceiveNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return TransferActionResult.Ok();
    }

    public async Task<TransferActionResult> CompleteAsync(int transferId, string? userId)
    {
        var transfer = await _db.StockTransfers.FirstOrDefaultAsync(t => t.Id == transferId);
        if (transfer == null) return TransferActionResult.Fail("Transfer not found.");
        if (transfer.Status != TransferStatus.PartiallyReceived)
            return TransferActionResult.Fail("Transfer is not partially received.");

        transfer.Status = TransferStatus.Completed;
        await _db.SaveChangesAsync();
        return TransferActionResult.Ok();
    }

    public async Task<TransferActionResult> CancelAsync(int transferId, string reason, string? userId)
    {
        reason = (reason ?? "").Trim();
        if (reason.Length == 0) return TransferActionResult.Fail("A cancellation reason is required.");

        var transfer = await _db.StockTransfers.Include(t => t.Items).FirstOrDefaultAsync(t => t.Id == transferId);
        if (transfer == null) return TransferActionResult.Fail("Transfer not found.");
        if (transfer.Status is not (TransferStatus.Draft or TransferStatus.PendingApproval or TransferStatus.Approved))
            return TransferActionResult.Fail("This transfer can no longer be cancelled.");

        await using var tx = await _db.Database.BeginTransactionAsync();

        if (transfer.Status == TransferStatus.Approved)
        {
            var productIds = transfer.Items.Select(i => i.ProductId).Distinct().ToList();
            await LockInventoryRowsAsync(productIds.Select(pid => (pid, transfer.FromStoreId)));
            var invs = await _db.StoreInventories
                .Where(si => si.StoreId == transfer.FromStoreId && productIds.Contains(si.ProductId)
                && si.ProductVariantId == null)   // transfers are product-level (pool) in Phase 1 of variant stock
                .ToListAsync();
            foreach (var item in transfer.Items)
            {
                var approved = item.ApprovedQty ?? 0;
                if (approved == 0) continue;
                var inv = invs.FirstOrDefault(i => i.ProductId == item.ProductId);
                if (inv != null) inv.QuantityReserved = Math.Max(0, inv.QuantityReserved - approved);
            }
        }

        transfer.Status = TransferStatus.Cancelled;
        transfer.CancelledByUserId = userId;
        transfer.CancelledAt = DateTime.UtcNow;
        transfer.CancellationReason = reason;

        try
        {
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return TransferActionResult.Fail("Stock levels changed — please try again.");
        }
        return TransferActionResult.Ok();
    }
}
