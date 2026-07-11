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

/// <summary>Per-line receive reconciliation: of the dispatched units, how many arrived good,
/// damaged, or are written off as won't-fulfil (this round). Pending is derived.</summary>
public record ReceiveLineDto(int ItemId, int Received, int Damaged, int WontFulfil);

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
    Task<TransferActionResult> ReceiveAsync(int transferId, List<ReceiveLineDto> lines, string? notes, string? userId);
    Task<TransferActionResult> CompleteAsync(int transferId, string? userId);
    Task<TransferActionResult> CancelAsync(int transferId, string reason, string? userId);
}

public class TransferWorkflowService : ITransferWorkflowService
{
    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;
    private readonly IOrderFulfilmentService _fulfilment;

    public TransferWorkflowService(ApplicationDbContext db, IStockService stock, IOrderFulfilmentService fulfilment)
    {
        _db = db;
        _stock = stock;
        _fulfilment = fulfilment;
    }

    /// <summary>
    /// Acquires Postgres row locks (SELECT ... FOR UPDATE) on the given StoreInventory rows,
    /// in a fixed (ProductId, StoreId) order, before they're read/mutated. Serializes concurrent
    /// approvals/dispatches/cancellations (and POS sales/reservations, which lock the same rows
    /// in the same order) instead of racing on stale QuantityReserved/QuantityOnHand snapshots.
    /// Caller must already be inside a transaction. Rows that don't exist yet lock nothing.
    /// </summary>
    /// <summary>Short "3× Ring, 1× Bangle" summary of the transfer lines with a positive quantity
    /// (under the given selector), for order-timeline notes.</summary>
    private static string Summarize(IEnumerable<StockTransferItem> items, Func<StockTransferItem, int> qty)
    {
        var parts = items.Where(i => qty(i) > 0).Select(i => $"{qty(i)}× {i.ProductName}").ToList();
        return parts.Count == 0 ? "the requested items" : string.Join(", ", parts);
    }

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
            .Include(t => t.Items).Include(t => t.FromStore).Include(t => t.ToStore)
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

        // Timeline note on the order this transfer is fulfilling (if any).
        if (transfer.OrderId.HasValue)
            OrderNotes.AddSystem(_db, transfer.OrderId.Value,
                $"Inter-branch transfer {transfer.TransferNumber} approved — {Summarize(transfer.Items, i => i.ApprovedQty ?? 0)} from {transfer.FromStore.Name} → {transfer.ToStore.Name}. Awaiting dispatch.");

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

        if (transfer.OrderId.HasValue)
            OrderNotes.AddSystem(_db, transfer.OrderId.Value,
                $"Inter-branch transfer {transfer.TransferNumber} was rejected: {reason}");

        await _db.SaveChangesAsync();
        return TransferActionResult.Ok();
    }

    public async Task<TransferActionResult> DispatchAsync(int transferId, List<ItemQtyDto> items, string? trackingNumber, string? courierName, string? notes, string? userId)
    {
        var transfer = await _db.StockTransfers
            .Include(t => t.Items).Include(t => t.ToStore).Include(t => t.FromStore)
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

        if (transfer.OrderId.HasValue)
        {
            var carrier = new[] { transfer.CourierName, transfer.TrackingNumber }.Where(s => !string.IsNullOrWhiteSpace(s));
            var carrierText = carrier.Any() ? $" ({string.Join(", ", carrier)})" : "";
            OrderNotes.AddSystem(_db, transfer.OrderId.Value,
                $"Inter-branch transfer {transfer.TransferNumber} dispatched — {Summarize(transfer.Items, i => i.DispatchedQty ?? 0)} from {transfer.FromStore.Name} → {transfer.ToStore.Name}{carrierText}. In transit.");
        }

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

    public async Task<TransferActionResult> ReceiveAsync(int transferId, List<ReceiveLineDto> lines, string? notes, string? userId)
    {
        var transfer = await _db.StockTransfers
            .Include(t => t.Items).Include(t => t.FromStore).Include(t => t.ToStore)
            .FirstOrDefaultAsync(t => t.Id == transferId);
        if (transfer == null) return TransferActionResult.Fail("Transfer not found.");
        // Receiving can happen on the first arrival (InTransit) and again to reconcile what was
        // still pending (PartiallyReceived); amounts accumulate across rounds.
        if (transfer.Status is not (TransferStatus.InTransit or TransferStatus.PartiallyReceived))
            return TransferActionResult.Fail("Transfer is not awaiting receipt.");

        var byItem = (lines ?? new()).ToDictionary(l => l.ItemId, l => l);

        // Validate: each round's amounts are non-negative and the accumulated received+damaged+
        // won't-fulfil never exceeds what was dispatched.
        foreach (var item in transfer.Items)
        {
            if (!byItem.TryGetValue(item.Id, out var l)) continue;
            if (l.Received < 0 || l.Damaged < 0 || l.WontFulfil < 0)
                return TransferActionResult.Fail($"Quantities for '{item.ProductName}' cannot be negative.");
            var dispatched = item.DispatchedQty ?? 0;
            var alreadyAccounted = (item.ReceivedQty ?? 0) + (item.DamagedQty ?? 0) + (item.WontFulfilQty ?? 0);
            var thisRound = l.Received + l.Damaged + l.WontFulfil;
            if (alreadyAccounted + thisRound > dispatched)
                return TransferActionResult.Fail(
                    $"'{item.ProductName}': received + damaged + won't-fulfil ({alreadyAccounted + thisRound}) exceeds dispatched ({dispatched}).");
        }
        if (transfer.Items.All(i => !byItem.ContainsKey(i.Id)
                || (byItem[i.Id].Received + byItem[i.Id].Damaged + byItem[i.Id].WontFulfil) == 0))
            return TransferActionResult.Fail("Enter at least one received, damaged or won't-fulfil quantity.");

        var now = DateTime.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync();

        var receivedParts = new List<string>();
        var shortfallParts = new List<string>();
        foreach (var item in transfer.Items)
        {
            if (!byItem.TryGetValue(item.Id, out var l)) continue;

            // Only the good units received this round enter the destination's stock. Damaged and
            // won't-fulfil units left the source at dispatch and never become destination stock —
            // they're recorded on the line as the transit loss / shortage reconciliation.
            if (l.Received > 0)
                await _stock.ApplyAsync(item.ProductId, item.ProductVariantId, transfer.ToStoreId, l.Received,
                    StockMovementType.Transfer, transfer.TransferNumber, $"Received from {transfer.FromStore.Name}", userId);

            item.ReceivedQty = (item.ReceivedQty ?? 0) + l.Received;
            item.DamagedQty = (item.DamagedQty ?? 0) + l.Damaged;
            item.WontFulfilQty = (item.WontFulfilQty ?? 0) + l.WontFulfil;

            if (l.Received > 0) receivedParts.Add($"{l.Received}× {item.ProductName}");
            if (l.Damaged > 0 || l.WontFulfil > 0)
                shortfallParts.Add($"{item.ProductName} ({l.Damaged} damaged, {l.WontFulfil} won't-fulfil)");
        }

        // Fully reconciled (nothing still pending on any line) → Completed, else PartiallyReceived.
        var anyPending = transfer.Items.Any(i => i.PendingQty > 0);
        transfer.Status = anyPending ? TransferStatus.PartiallyReceived : TransferStatus.Completed;
        transfer.ReceivedByUserId = userId;
        transfer.ReceivedAt = now;
        transfer.ReceiveNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

        if (transfer.OrderId.HasValue)
        {
            var word = anyPending ? "partially received" : "received in full";
            var summary = receivedParts.Count > 0 ? string.Join(", ", receivedParts) : "no good units this round";
            var shortfall = shortfallParts.Count > 0 ? $" Shortfall: {string.Join("; ", shortfallParts)}." : "";
            OrderNotes.AddSystem(_db, transfer.OrderId.Value,
                $"Inter-branch transfer {transfer.TransferNumber} {word} at {transfer.ToStore.Name} — {summary}.{shortfall}");
        }

        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        // If this transfer was consolidating stock for an online order, try to finalise it (commits
        // the sale + marks the order ready once ALL its transfers are in). No-op otherwise.
        if (transfer.OrderId.HasValue)
            await _fulfilment.FinalizeAwaitingOrderAsync(transfer.OrderId.Value);

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

        if (transfer.OrderId.HasValue)
            OrderNotes.AddSystem(_db, transfer.OrderId.Value,
                $"Inter-branch transfer {transfer.TransferNumber} was cancelled: {reason}");

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
