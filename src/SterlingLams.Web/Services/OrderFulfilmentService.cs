using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services.Payment;

namespace SterlingLams.Web.Services;

/// <summary>Result of trying to fulfil a paid order. Stock is no longer held before payment
/// (no reservations) — it's committed first-come-first-served at payment time.</summary>
public enum FulfilOutcome
{
    /// <summary>Stock committed (or order already fulfilled / not applicable).</summary>
    Fulfilled,
    /// <summary>An item sold out before this payment landed — caller should cancel + refund.</summary>
    SoldOut,
    /// <summary>Transient failure (e.g. concurrency/DB) — left for the retry service, do NOT refund.</summary>
    Deferred
}

public interface IOrderFulfilmentService
{
    /// <summary>Frees any legacy reservation rows for an order (no-op now that orders don't reserve
    /// before payment; kept for the abandoned-order sweeper + failed-payment path).</summary>
    Task ReleaseReservationAsync(int orderId);

    /// <summary>Fulfils a paid online order against the in-house stock ledger, committing stock
    /// first-come-first-served. Idempotent and safe to call from every payment-confirmation path
    /// (browser callback and webhook). Returns SoldOut if an item is no longer available.
    /// If the order needs stock from other branches it's left "Awaiting transfer" and finalised
    /// later by <see cref="FinalizeAwaitingOrderAsync"/> once the transfers are received.</summary>
    Task<FulfilOutcome> FulfilPaidOrderAsync(int orderId);

    /// <summary>Called when a transfer tied to an online order is received. If all of the order's
    /// transfers are now in, commits the sale at the fulfilling branch and marks the order ready
    /// to dispatch. Idempotent; no-op until every transfer is received.</summary>
    Task FinalizeAwaitingOrderAsync(int orderId);
}

public class OrderFulfilmentService : IOrderFulfilmentService
{
    private readonly ApplicationDbContext _db;
    private readonly IStockService _stock;
    private readonly ILogger<OrderFulfilmentService> _logger;
    private readonly IPaymentService _payment;
    private readonly IEmailService _email;
    private readonly ISettingsService _settings;

    public OrderFulfilmentService(ApplicationDbContext db, IStockService stock,
        ILogger<OrderFulfilmentService> logger, IPaymentService payment, IEmailService email,
        ISettingsService settings)
    {
        _db = db;
        _stock = stock;
        _logger = logger;
        _payment = payment;
        _email = email;
        _settings = settings;
    }

    // Variant-level stock: an order line for variant V draws on V's OWN inventory row — variants
    // never fall back to the shared product pool (ProductVariantId == null). A simple product
    // (variantId == null) uses the pool row. This MUST match StockService's resolution
    // (ResolveTargetVariantIdAsync, which now returns variantId verbatim — the pool-fallback
    // transition is complete, stock lives on the variants): allocation/availability here pairs with
    // the StockService deduction it commits, so both must target the same row. If allocation fell
    // back to the pool while the deduction targeted the (empty) variant row, a paid order would be
    // allocated then fail to deduct — left Deferred forever instead of refunded. Kept as a resolver
    // (rather than inlining `vid`) so the lock-step with StockService is explicit at every call site.
    private static Func<int, int?, int, int?> EffectiveVariantResolver(IEnumerable<StoreInventory> rows)
        => (_, vid, _) => vid;

    // ── Allocation ────────────────────────────────────────────────────────────
    // Spreads an order's lines across branches: the fulfilment branch first (pickup store, or
    // nearest to the customer), then the next-nearest. `avail(product, variant, store)` supplies the
    // usable quantity of the effective row; `effVid` maps each line to the inventory row it draws on
    // (the variant's own row, or the pool row for a simple product) so repeated lines of the same
    // product/variant decrement one balance. Returns the per-(store, product, variant) allocation and
    // the first line that couldn't be fully covered (null = success).
    private static (Store fulfilStore, Dictionary<(int store, int product, int? variant), int> alloc, OrderItem? shortLine)
        Allocate(Order order, List<Store> activeStores, List<Store> ranked,
            Func<int, int?, int, int?> effVid, Func<int, int?, int, int> avail)
    {
        Store fulfilStore = (order.FulfillmentType == FulfillmentType.StorePickup
                ? activeStores.FirstOrDefault(s => s.Id == order.PickupStoreId)
                : null)
            ?? ranked.First();

        var storeOrder = new List<int> { fulfilStore.Id };
        storeOrder.AddRange(ranked.Where(s => s.Id != fulfilStore.Id).Select(s => s.Id));

        // Keyed by the EFFECTIVE row so repeated lines of the same product/variant share one balance.
        var remaining = new Dictionary<(int product, int? effVid, int store), int>();
        int Remaining(int pid, int? vid, int sid)
        {
            var k = (pid, effVid(pid, vid, sid), sid);
            if (!remaining.TryGetValue(k, out var v)) { v = avail(pid, vid, sid); remaining[k] = v; }
            return v;
        }

        var alloc = new Dictionary<(int store, int product, int? variant), int>();
        foreach (var line in order.Items)
        {
            var need = line.Quantity;
            foreach (var sid in storeOrder)
            {
                if (need <= 0) break;
                var take = Math.Min(need, Remaining(line.ProductId, line.ProductVariantId, sid));
                if (take <= 0) continue;
                remaining[(line.ProductId, effVid(line.ProductId, line.ProductVariantId, sid), sid)] -= take;
                alloc.TryGetValue((sid, line.ProductId, line.ProductVariantId), out var acc);
                alloc[(sid, line.ProductId, line.ProductVariantId)] = acc + take;
                need -= take;
            }
            if (need > 0) return (fulfilStore, alloc, line);
        }
        return (fulfilStore, alloc, null);
    }

    // ── Concurrency helper ───────────────────────────────────────────────────
    /// <summary>
    /// Acquires Postgres row locks (SELECT ... FOR UPDATE) on the given product+store inventory
    /// rows (all variant rows + the pool for each pair, since the predicate omits the variant),
    /// in a fixed (ProductId, StoreId) order. Serializes concurrent reservations/sales/transfers
    /// instead of racing on stale snapshots. Caller must already be inside a transaction.
    /// </summary>
    private async Task LockInventoryRowsAsync(IEnumerable<(int ProductId, int StoreId)> pairs)
    {
        if (!_db.Database.IsNpgsql()) return; // FOR UPDATE is Postgres-only (SQLite test harness no-ops)
        foreach (var (pid, sid) in pairs.Distinct().OrderBy(p => p.ProductId).ThenBy(p => p.StoreId))
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"StoreInventories\" WHERE \"ProductId\" = {pid} AND \"StoreId\" = {sid} FOR UPDATE");
    }

    // ── Release (legacy holds + abandoned-order sweeper) ────────────────────────
    public async Task ReleaseReservationAsync(int orderId)
    {
        var rows = await _db.StockReservations.Where(r => r.OrderId == orderId).ToListAsync();
        if (rows.Count == 0) return;
        await using var tx = await _db.Database.BeginTransactionAsync();
        await ReleaseRowsAsync(rows);
        await tx.CommitAsync();
    }

    /// <summary>Releases reservation rows and their QuantityReserved holds (on the effective row).
    /// Caller must already be inside a transaction.</summary>
    private async Task ReleaseRowsAsync(List<StockReservation> rows)
    {
        await LockInventoryRowsAsync(rows.Select(r => (r.ProductId, r.StoreId)));

        var pids = rows.Select(r => r.ProductId).Distinct().ToList();
        var sids = rows.Select(r => r.StoreId).Distinct().ToList();
        var invRows = await _db.StoreInventories
            .Where(si => pids.Contains(si.ProductId) && sids.Contains(si.StoreId))
            .ToListAsync();
        var invMap = invRows.ToDictionary(si => (si.ProductId, si.ProductVariantId, si.StoreId));
        var effVid = EffectiveVariantResolver(invRows);
        foreach (var r in rows)
            if (invMap.TryGetValue((r.ProductId, effVid(r.ProductId, r.ProductVariantId, r.StoreId), r.StoreId), out var si))
                si.QuantityReserved = Math.Max(0, si.QuantityReserved - r.Quantity);
        _db.StockReservations.RemoveRange(rows);
        await _db.SaveChangesAsync();
    }

    // ── Fulfil ─────────────────────────────────────────────────────────────────
    /// <summary>
    /// Picks the branch nearest the customer, transfers in any units it lacks from other
    /// branches (transfer-then-sell so every branch balance stays correct), then sells the
    /// whole order from that branch. Stock is NOT held before payment — it's committed
    /// first-come-first-served here, under a row lock, so a simultaneous payment for the last
    /// unit serialises and the loser gets SoldOut (caller refunds).
    /// Variant-aware: allocation/transfer/sale all run against each line's effective row.
    /// Idempotent. Failures are logged, never thrown — the customer has already paid.
    /// </summary>
    public async Task<FulfilOutcome> FulfilPaidOrderAsync(int orderId)
    {
        try
        {
            var order = await _db.Orders
                .Include(o => o.Items)
                .Include(o => o.PickupStore)
                .Include(o => o.DeliveryAddress)
                .FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null) return FulfilOutcome.Fulfilled;

            // Only online orders are fulfilled through this multi-branch pipeline. POS sales are
            // already settled (stock deducted) at the till — never re-fulfil one.
            if (order.Channel != OrderChannel.Online) return FulfilOutcome.Fulfilled;

            // Idempotency: a fulfilled order already has its branch + ledger movements.
            if (order.FulfillingStoreId != null) return FulfilOutcome.Fulfilled;
            // Terminal already (e.g. a prior callback/webhook already refunded a sold-out order) —
            // never re-process, so we can't double-refund.
            if (order.Status is OrderStatus.Cancelled or OrderStatus.Refunded) return FulfilOutcome.Fulfilled;

            var activeStores = await _db.Stores.Where(s => s.IsActive).ToListAsync();
            if (activeStores.Count == 0)
            {
                _logger.LogError("No active stores — cannot fulfil order {OrderNumber}.", order.OrderNumber);
                return FulfilOutcome.Deferred;
            }
            var ranked = DeliveryZoneService.RankStoresByProximity(
                activeStores, order.DeliveryAddress?.State, order.DeliveryAddress?.City);

            var productIds = order.Items.Select(i => i.ProductId).Distinct().ToList();
            var storeIds = activeStores.Select(s => s.Id).ToList();
            var products = await _db.Products.Where(p => productIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Name);
            var now = DateTime.UtcNow;

            // Lock the candidate (product, store) rows for the whole allocate→deduct so two
            // concurrent payments for the same last unit can't both succeed. Nothing is written
            // on the sold-out path, so the lock is released cleanly before we refund.
            var soldOut = false;
            var shipNow = false;
            var awaitingTransfer = false;
            Store? fulfilStoreForEmail = null;
            var sourceNotices = new List<(Store Source, string Ref, List<(string Name, int Qty)> Items)>();

            await using (var tx = await _db.Database.BeginTransactionAsync())
            {
                await LockInventoryRowsAsync(productIds.SelectMany(pid => storeIds.Select(sid => (pid, sid))));

                var invRows = await _db.StoreInventories
                    .Where(si => productIds.Contains(si.ProductId) && storeIds.Contains(si.StoreId))
                    .ToListAsync();
                var invMap = invRows.ToDictionary(si => (si.ProductId, si.ProductVariantId, si.StoreId));
                var effVid = EffectiveVariantResolver(invRows);
                int SaleAvail(int pid, int? vid, int sid)
                {
                    var ev = effVid(pid, vid, sid);
                    return invMap.TryGetValue((pid, ev, sid), out var si)
                        ? Math.Max(0, si.QuantityOnHand - si.QuantityReserved) : 0;
                }

                var (fulfilStore, alloc, shortLine) = Allocate(order, activeStores, ranked, effVid, SaleAvail);
                if (shortLine != null)
                {
                    _logger.LogWarning("Order {OrderNumber} sold out before its payment landed — product {ProductId}.",
                        order.OrderNumber, shortLine.ProductId);
                    soldOut = true;
                }
                else if (alloc.All(kv => kv.Key.store == fulfilStore.Id))
                {
                    // Everything is already at the nearest branch — sell + ship straight away.
                    var reduced = new List<string>();
                    foreach (var line in order.Items)
                    {
                        var after = await _stock.ApplyAsync(line.ProductId, line.ProductVariantId, fulfilStore.Id,
                            -line.Quantity, StockMovementType.Sale, order.OrderNumber,
                            $"Online order {order.OrderNumber}", order.UserId);
                        var label = line.ProductName + (string.IsNullOrEmpty(line.VariantName) ? "" : " – " + line.VariantName);
                        reduced.Add($"{label} ({after + line.Quantity}→{after})");
                    }
                    if (reduced.Count > 0)
                        OrderNotes.AddSystem(_db, order.Id, "Stock levels reduced: " + string.Join(", ", reduced) + ".");

                    var prevStatus = order.Status;
                    order.FulfillingStoreId = fulfilStore.Id;
                    order.Status = order.FulfillmentType == FulfillmentType.StorePickup
                        ? OrderStatus.ReadyForPickup : OrderStatus.Processing;
                    OrderNotes.AddSystem(_db, order.Id, $"Order status changed from {prevStatus} to {order.Status} (fulfilled from {fulfilStore.Name}).");
                    await _db.SaveChangesAsync();
                    await tx.CommitAsync();
                    shipNow = true; fulfilStoreForEmail = fulfilStore;
                    _logger.LogInformation("Order {OrderNumber} fulfilled from {Store} (no transfer).", order.OrderNumber, fulfilStore.Name);
                }
                else
                {
                    // Cross-branch: don't move/sell yet. Hold the stock and create PENDING transfers;
                    // staff Dispatch from each source and Receive at the fulfilling branch, then the
                    // sale is committed (FinalizeAwaitingOrderAsync). Hold the fulfilling branch's own
                    // (local) units via a reservation so they aren't sold away while we wait.
                    foreach (var kv in alloc.Where(kv => kv.Key.store == fulfilStore.Id))
                    {
                        var ev = effVid(kv.Key.product, kv.Key.variant, fulfilStore.Id);
                        if (invMap.TryGetValue((kv.Key.product, ev, fulfilStore.Id), out var si)) si.QuantityReserved += kv.Value;
                        _db.StockReservations.Add(new StockReservation
                        {
                            OrderId = orderId, StoreId = fulfilStore.Id, ProductId = kv.Key.product,
                            ProductVariantId = kv.Key.variant, Quantity = kv.Value, CreatedAt = now
                        });
                    }

                    // One pre-approved transfer per source branch (units reserved at source, mirroring
                    // a manual Approve — staff then Dispatch and the fulfilling branch Receives).
                    foreach (var bySource in alloc.Where(kv => kv.Key.store != fulfilStore.Id).GroupBy(kv => kv.Key.store))
                    {
                        var sourceStore = activeStores.First(s => s.Id == bySource.Key);
                        var transferNumber = $"TRF-{now:yyMMdd}-{now:HHmmssfff}-{sourceStore.Id}";
                        var transfer = new StockTransfer
                        {
                            TransferNumber = transferNumber,
                            FromStoreId = sourceStore.Id,
                            ToStoreId = fulfilStore.Id,
                            OrderId = orderId,
                            Status = TransferStatus.Approved,
                            CreatedByUserId = order.UserId,
                            Note = $"Online order {order.OrderNumber}",
                            CreatedAt = now,
                            ApprovedByUserId = order.UserId,
                            ApprovedAt = now
                        };
                        var emailItems = new List<(string, int)>();
                        foreach (var kv in bySource)
                        {
                            var (pid, vid, qty) = (kv.Key.product, kv.Key.variant, kv.Value);
                            var ev = effVid(pid, vid, sourceStore.Id);
                            if (invMap.TryGetValue((pid, ev, sourceStore.Id), out var si)) si.QuantityReserved += qty; // reserve at source
                            var pname = products.GetValueOrDefault(pid, $"#{pid}");
                            transfer.Items.Add(new StockTransferItem
                            {
                                ProductId = pid, ProductVariantId = vid, ProductName = pname,
                                RequestedQty = qty, ApprovedQty = qty
                            });
                            emailItems.Add((pname, qty));
                        }
                        _db.StockTransfers.Add(transfer);
                        sourceNotices.Add((sourceStore, transferNumber, emailItems));
                    }

                    var prevStatus = order.Status;
                    order.FulfillingStoreId = fulfilStore.Id;
                    order.Status = OrderStatus.AwaitingTransfer;
                    OrderNotes.AddSystem(_db, order.Id,
                        $"Order status changed from {prevStatus} to Awaiting Transfer — fulfilling from {fulfilStore.Name}; "
                        + $"{sourceNotices.Count} inter-branch transfer(s) requested.");
                    await _db.SaveChangesAsync();
                    await tx.CommitAsync();
                    awaitingTransfer = true; fulfilStoreForEmail = fulfilStore;
                    _logger.LogInformation("Order {OrderNumber} awaiting {N} transfer(s) into {Store}.",
                        order.OrderNumber, sourceNotices.Count, fulfilStore.Name);
                }
            } // tx disposed — sold-out path wrote nothing, so the hold is released cleanly

            if (soldOut)
            {
                await RefundSoldOutAsync(order);
                return FulfilOutcome.SoldOut;
            }
            if (awaitingTransfer) await NotifyTransfersRequestedAsync(order, fulfilStoreForEmail!, sourceNotices);
            else if (shipNow) await NotifyReadyToDispatchAsync(order, fulfilStoreForEmail!);
            return FulfilOutcome.Fulfilled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fulfilment failed for order {OrderId}: {Message}", orderId, ex.Message);
            // Best-effort: record the failure on the order so it's visible in admin and picked up by
            // the retry service. The transaction above rolled back, so clear the tracker first.
            try
            {
                _db.ChangeTracker.Clear();
                var o = await _db.Orders.FindAsync(orderId);
                if (o != null && o.FulfillingStoreId == null)
                {
                    o.AdminNotes = $"Fulfilment error {DateTime.UtcNow:yyyy-MM-dd HH:mm}: {ex.Message}";
                    await _db.SaveChangesAsync();
                }
            }
            catch { /* never throw from fulfilment — the customer has already paid */ }
            return FulfilOutcome.Deferred; // transient — retry service will pick it up (no refund)
        }
    }

    // ── Finalise an awaiting-transfer order once its branch transfers are all received ──────────
    public async Task FinalizeAwaitingOrderAsync(int orderId)
    {
        try
        {
            var order = await _db.Orders.Include(o => o.Items).Include(o => o.PickupStore)
                .Include(o => o.DeliveryAddress).FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null || order.Status != OrderStatus.AwaitingTransfer || order.FulfillingStoreId == null) return;

            // Every transfer for this order must be received (terminal) before we commit the sale.
            var transfers = await _db.StockTransfers.Where(t => t.OrderId == orderId).ToListAsync();
            if (transfers.Count == 0) return;
            if (transfers.Any(t => t.Status != TransferStatus.Completed && t.Status != TransferStatus.PartiallyReceived))
                return; // still awaiting at least one

            var fulfilStoreId = order.FulfillingStoreId.Value;
            var productIds = order.Items.Select(i => i.ProductId).Distinct().ToList();

            await using var tx = await _db.Database.BeginTransactionAsync();
            await LockInventoryRowsAsync(productIds.Select(pid => (pid, fulfilStoreId)));

            // Free this order's local hold so the sale below can draw those units.
            var resRows = await _db.StockReservations.Where(r => r.OrderId == orderId).ToListAsync();
            if (resRows.Count > 0) await ReleaseRowsAsync(resRows);

            // Make sure the fulfilling branch really has every line now — a short/partial transfer
            // (damaged or won't-fulfil) means we can't complete; flag it rather than oversell.
            var inv = await _db.StoreInventories.Where(si => productIds.Contains(si.ProductId) && si.StoreId == fulfilStoreId).ToListAsync();
            var effVid = EffectiveVariantResolver(inv);
            int OnHand(int pid, int? vid)
            {
                var ev = effVid(pid, vid, fulfilStoreId);
                return inv.FirstOrDefault(x => x.ProductId == pid && x.ProductVariantId == ev)?.QuantityOnHand ?? 0;
            }
            var shortLine = order.Items.GroupBy(i => (i.ProductId, i.ProductVariantId))
                .FirstOrDefault(g => OnHand(g.Key.ProductId, g.Key.ProductVariantId) < g.Sum(x => x.Quantity));
            if (shortLine != null)
            {
                order.AdminNotes = $"Awaiting-transfer order short {DateTime.UtcNow:yyyy-MM-dd HH:mm}: not enough of "
                    + $"'{shortLine.First().ProductName}' arrived (damaged/won't-fulfil). Resolve manually.";
                await _db.SaveChangesAsync();
                await tx.CommitAsync();
                _logger.LogWarning("Order {OrderNumber} transfers received short — flagged for review.", order.OrderNumber);
                return;
            }

            foreach (var line in order.Items)
                await _stock.ApplyAsync(line.ProductId, line.ProductVariantId, fulfilStoreId, -line.Quantity,
                    StockMovementType.Sale, order.OrderNumber, $"Online order {order.OrderNumber}", order.UserId);

            order.Status = order.FulfillmentType == FulfillmentType.StorePickup
                ? OrderStatus.ReadyForPickup : OrderStatus.Processing;
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            var fulfilStore = await _db.Stores.FindAsync(fulfilStoreId);
            if (fulfilStore != null) await NotifyReadyToDispatchAsync(order, fulfilStore);
            _logger.LogInformation("Order {OrderNumber} finalised after its transfers were received.", order.OrderNumber);
        }
        catch (Exception ex) { _logger.LogError(ex, "FinalizeAwaitingOrder failed for {OrderId}", orderId); }
    }

    // ── Branch fulfilment emails ────────────────────────────────────────────────
    private static string ItemRows(IEnumerable<(string Name, int Qty)> items) =>
        string.Join("", items.Select(i => $"<li>{System.Net.WebUtility.HtmlEncode(i.Name)} &times; {i.Qty}</li>"));

    private async Task SendBranchAsync(string? toEmail, string subject, string html)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(toEmail)) await _email.SendAsync(toEmail!, subject, html);
            var admin = await _settings.GetAsync("notifications.admin_email", "");
            if (!string.IsNullOrWhiteSpace(admin) && !string.Equals(admin, toEmail, StringComparison.OrdinalIgnoreCase))
                await _email.SendAsync(admin, "[copy] " + subject, html);
        }
        catch (Exception ex) { _logger.LogError(ex, "Branch fulfilment email failed: {Subject}", subject); }
    }

    // Source branches: pack & send a transfer to the fulfilling branch for an online order.
    private async Task NotifyTransfersRequestedAsync(Order order, Store fulfilStore,
        List<(Store Source, string Ref, List<(string Name, int Qty)> Items)> notices)
    {
        if (!await _settings.GetBoolAsync("notifications.branch_fulfilment", true)) return;
        var dest = fulfilStore.Name.Replace("Sterlin Glams ", "");
        // Subject + intro are editable in Admin → Emails ("Transfer request (to branch)"); {branch}/{order} filled here.
        var subjT = await _settings.GetAsync("email.branch_transfer_request.subject", "Send stock to {branch} — order {order}");
        var introT = await _settings.GetAsync("email.branch_transfer_request.intro", "Please pack and send the stock below to {branch} so order {order} can be fulfilled.");
        string Fill(string s) => s.Replace("{branch}", dest).Replace("{order}", order.OrderNumber);
        var intro = System.Net.WebUtility.HtmlEncode(Fill(introT));
        foreach (var n in notices)
        {
            var html = $"<h2 style=\"font-size:18px;margin:0 0 12px;\">Transfer needed — order {System.Net.WebUtility.HtmlEncode(order.OrderNumber)}</h2>"
                + $"<p style=\"color:#44403c;\">{intro}</p>"
                + $"<ul style=\"color:#374151;padding-left:18px;margin:14px 0;\">{ItemRows(n.Items)}</ul>"
                + $"<p style=\"color:#57534e;font-size:13px;\">Transfer reference <strong>{n.Ref}</strong>. Mark it dispatched in Inventory System → Stock transfer once sent.</p>";
            await SendBranchAsync(n.Source.Email, Fill(subjT), html);
        }
    }

    // Fulfilling branch: all stock is in (or was already local) — pack & dispatch to the customer.
    private async Task NotifyReadyToDispatchAsync(Order order, Store fulfilStore)
    {
        if (!await _settings.GetBoolAsync("notifications.branch_fulfilment", true)) return;
        var branch = fulfilStore.Name.Replace("Sterlin Glams ", "");
        // Editable in Admin → Emails ("Order dispatch (to branch)"); {branch}/{order} filled here.
        var subjT = await _settings.GetAsync("email.branch_dispatch.subject", "Dispatch order {order}");
        var introT = await _settings.GetAsync("email.branch_dispatch.intro", "All stock for order {order} is now at your branch — please pack and fulfil it.");
        string Fill(string s) => s.Replace("{branch}", branch).Replace("{order}", order.OrderNumber);
        var items = order.Items.Select(i => (i.VariantName == null ? i.ProductName : $"{i.ProductName} ({i.VariantName})", i.Quantity));
        var where = order.FulfillmentType == FulfillmentType.StorePickup
            ? $"Customer pickup at {System.Net.WebUtility.HtmlEncode(branch)}."
            : $"Deliver to {System.Net.WebUtility.HtmlEncode((order.DeliveryAddress?.City + ", " + order.DeliveryAddress?.State).Trim(' ', ','))}.";
        var html = $"<h2 style=\"font-size:18px;margin:0 0 12px;\">Order {System.Net.WebUtility.HtmlEncode(order.OrderNumber)} ready to dispatch</h2>"
            + $"<p style=\"color:#44403c;\">{System.Net.WebUtility.HtmlEncode(Fill(introT))}</p>"
            + $"<ul style=\"color:#374151;padding-left:18px;margin:14px 0;\">{ItemRows(items)}</ul>"
            + $"<p style=\"color:#57534e;font-size:13px;\">{where}</p>";
        await SendBranchAsync(fulfilStore.Email, Fill(subjT), html);
    }

    // Cancel + refund a paid order whose item sold out before its payment landed (the "first to
    // pay wins" loser is made whole). Best-effort; runs from every payment path (callback/webhook/
    // retry) and is guarded by the terminal-status check above so it can't double-refund.
    private async Task RefundSoldOutAsync(Order order)
    {
        order.Status = OrderStatus.Cancelled;
        string note;
        try
        {
            var refund = await _payment.RefundPaymentAsync(new RefundPaymentRequest
            {
                Reference = order.PaymentReference ?? string.Empty,
                Amount = order.Total,
                Reason = "Item sold out before payment completed"
            });
            if (refund.Success)
            {
                order.Status = OrderStatus.Refunded;
                note = $"Auto-refunded {DateTime.UtcNow:yyyy-MM-dd HH:mm}: item sold out before payment landed.";
            }
            else
            {
                note = $"SOLD OUT after payment {DateTime.UtcNow:yyyy-MM-dd HH:mm} — "
                     + (refund.Supported ? $"auto-refund FAILED ({refund.ErrorMessage})" : $"{_payment.ProviderName} has no auto-refund")
                     + "; refund MANUALLY.";
            }
        }
        catch (Exception ex)
        {
            note = $"SOLD OUT after payment {DateTime.UtcNow:yyyy-MM-dd HH:mm} — refund error ({ex.Message}); refund MANUALLY.";
        }
        order.AdminNotes = note;
        await _db.SaveChangesAsync();

        try
        {
            var email = await _db.Users.Where(u => u.Id == order.UserId).Select(u => u.Email).FirstOrDefaultAsync();
            if (!string.IsNullOrEmpty(email))
                await _email.SendAsync(email, "Your Sterlin Glams order could not be completed",
                    $"<p>We're so sorry — an item in your order <strong>{order.OrderNumber}</strong> sold out just before your payment completed, so we couldn't fulfil it.</p>"
                  + $"<p>Your payment of ₦{order.Total:N0} has been refunded in full. Refunds typically settle within a few business days.</p>"
                  + "<p>Please accept our apologies — you're welcome to reorder if it comes back in stock.</p>");
        }
        catch (Exception ex) { _logger.LogError(ex, "Apology email failed for sold-out order {OrderNumber}", order.OrderNumber); }
    }
}
