using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using SterlingLams.Web.Services.Payment;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    public class OrdersController : AdminBaseController
    {
        protected override string Section => "Orders";

        private readonly ApplicationDbContext _db;
        private readonly IStockService _stock;
        private readonly IPaymentService _payment;
        private const int PageSize = 25;

        public OrdersController(ApplicationDbContext db, IStockService stock, IPaymentService payment)
        {
            _db = db;
            _stock = stock;
            _payment = payment;
        }

        public async Task<IActionResult> Index(string status = "", string q = "", int page = 1)
        {
            ViewData["Title"] = "Orders";

            var query = _db.Orders
                .Include(o => o.User)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<OrderStatus>(status, out var statusEnum))
                query = query.Where(o => o.Status == statusEnum);

            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(o =>
                    o.OrderNumber.Contains(q) ||
                    o.User.FirstName.Contains(q) ||
                    o.User.LastName.Contains(q) ||
                    o.User.Email!.Contains(q));

            var total = await query.CountAsync();
            var orders = await query
                .OrderByDescending(o => o.CreatedAt)
                .Skip((page - 1) * PageSize)
                .Take(PageSize)
                .Select(o => new AdminOrderRow
                {
                    Id = o.Id,
                    OrderNumber = o.OrderNumber,
                    CustomerName = o.User.FirstName + " " + o.User.LastName,
                    CustomerEmail = o.User.Email ?? "",
                    Total = o.Total,
                    Status = o.Status.ToString(),
                    IsPaid = o.IsPaid,
                    FulfillmentType = o.FulfillmentType.ToString(),
                    CreatedAt = o.CreatedAt
                })
                .ToListAsync();

            // Count per status for tab badges
            var allStatuses = new[] { "Pending","Confirmed","Processing","ReadyForPickup","Shipped","Delivered","Cancelled" };
            var counts = await _db.Orders
                .GroupBy(o => o.Status)
                .Select(g => new { Status = g.Key.ToString(), Count = g.Count() })
                .ToListAsync();
            var statusCounts = allStatuses.ToDictionary(s => s, s => counts.FirstOrDefault(c => c.Status == s)?.Count ?? 0);
            statusCounts[""] = await _db.Orders.CountAsync(); // "All" tab

            var vm = new AdminOrderListViewModel
            {
                Orders = orders,
                StatusFilter = status,
                SearchQuery = q,
                CurrentPage = page,
                TotalPages = (int)Math.Ceiling(total / (double)PageSize),
                StatusCounts = statusCounts
            };

            return View(vm);
        }

        public async Task<IActionResult> Detail(int id)
        {
            ViewData["Title"] = "Order Detail";

            var order = await _db.Orders
                .Include(o => o.Items).ThenInclude(i => i.Product)
                .Include(o => o.Items).ThenInclude(i => i.ProductVariant)
                .Include(o => o.PickupStore)
                .Include(o => o.DeliveryAddress)
                .Include(o => o.User)
                .FirstOrDefaultAsync(o => o.Id == id);

            if (order == null) return NotFound();

            var refunds = await _db.Refunds.Include(r => r.Items)
                .Where(r => r.OriginalOrderId == id)
                .OrderByDescending(r => r.CreatedAt)
                .ToListAsync();

            var refundedQty = refunds.SelectMany(r => r.Items)
                .GroupBy(ri => new { ri.ProductId, ri.ProductVariantId })
                .ToDictionary(g => (g.Key.ProductId, g.Key.ProductVariantId), g => g.Sum(x => x.Quantity));

            var vm = new AdminOrderDetailViewModel
            {
                Order = order,
                CustomerName = order.User.FullName,
                CustomerEmail = order.User.Email ?? "",
                Refunds = refunds,
                RefundedQty = refundedQty,
                RefundedTotal = refunds.Sum(r => r.Amount),
                RefundStoreId = order.FulfillingStoreId ?? order.PickupStoreId ?? 0,
                // Online refunds only here; POS returns are handled at the till.
                CanRefund = order.IsPaid && order.Channel == OrderChannel.Online && order.Status != OrderStatus.Refunded
            };

            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateStatus(int id, string status)
        {
            var order = await _db.Orders.FindAsync(id);
            if (order == null) return NotFound();

            if (Enum.TryParse<OrderStatus>(status, out var newStatus))
            {
                // Refunds must go through the Refund action so a refund record is created, stock is
                // returned and the gateway refund is triggered — never a cosmetic status flip.
                if (newStatus == OrderStatus.Refunded)
                {
                    TempData["Error"] = "Use the Refund action to refund an order — it records the refund, returns stock and triggers the gateway refund.";
                    return RedirectToAction(nameof(Detail), new { id });
                }

                var old = order.Status.ToString();
                order.Status = newStatus;
                order.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                await LogAsync("Update", "Order", order.Id.ToString(),
                    $"Order {order.OrderNumber} status: {old} → {status}");
                TempData["Success"] = $"Order {order.OrderNumber} updated to {status}.";
            }

            return RedirectToAction(nameof(Detail), new { id });
        }

        // ── Online order refund ───────────────────────────────────────────────
        // Mirrors the POS return flow (TillController.RefundProcess) but for online orders:
        // records a Refund + RefundItems, optionally returns stock to the fulfilling store, and
        // attempts a gateway refund. Item arrays are parallel and rendered in order on Detail;
        // variant id 0 means "no variant" (the product pool line).
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RefundOrder(int id, int[] itemProductId, int[] itemVariantId,
            int[] refundQty, string method = "Paystack", string? reason = null, bool restock = true)
        {
            var order = await _db.Orders.Include(o => o.Items)
                .FirstOrDefaultAsync(o => o.Id == id && o.Channel == OrderChannel.Online);
            if (order == null) return NotFound();
            if (!order.IsPaid)
            {
                TempData["Error"] = "An unpaid order can't be refunded.";
                return RedirectToAction(nameof(Detail), new { id });
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
            var now = DateTime.UtcNow;
            var storeId = order.FulfillingStoreId ?? order.PickupStoreId ?? 0;
            var refundNumber = $"REF-{now:yyMMdd}-{now:HHmmssfff}";

            await using var tx = await _db.Database.BeginTransactionAsync();

            // Serialize concurrent refunds on the same order so the "already refunded" totals below
            // are read after any in-flight refund commits (FOR UPDATE is Postgres-only).
            if (_db.Database.IsNpgsql())
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"Orders\" WHERE \"Id\" = {order.Id} FOR UPDATE");

            var refundIds = _db.Refunds.Where(r => r.OriginalOrderId == order.Id).Select(r => r.Id);
            var alreadyRefunded = await _db.RefundItems.Where(ri => refundIds.Contains(ri.RefundId))
                .GroupBy(ri => new { ri.ProductId, ri.ProductVariantId })
                .Select(g => new { g.Key.ProductId, g.Key.ProductVariantId, Qty = g.Sum(x => x.Quantity) })
                .ToListAsync();
            int Done(int pid, int? vid) =>
                alreadyRefunded.FirstOrDefault(r => r.ProductId == pid && r.ProductVariantId == vid)?.Qty ?? 0;

            var refund = new Refund
            {
                RefundNumber = refundNumber,
                OriginalOrderId = order.Id,
                CashierUserId = userId,
                RefundMethod = string.IsNullOrWhiteSpace(method) ? (order.PaymentProvider ?? "Manual") : method,
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                CreatedAt = now
            };

            decimal amount = 0;
            int n = itemProductId?.Length ?? 0;
            for (int i = 0; i < n; i++)
            {
                int pid = itemProductId![i];
                int? vid = (itemVariantId != null && i < itemVariantId.Length && itemVariantId[i] != 0)
                    ? itemVariantId[i] : (int?)null;
                int want = (refundQty != null && i < refundQty.Length) ? refundQty[i] : 0;
                if (want <= 0) continue;

                var oi = order.Items.FirstOrDefault(x => x.ProductId == pid && x.ProductVariantId == vid);
                if (oi == null) continue;

                int q = Math.Min(want, oi.Quantity - Done(pid, vid));
                if (q <= 0) continue;

                amount += oi.UnitPrice * q;
                refund.Items.Add(new RefundItem
                {
                    ProductId = oi.ProductId,
                    ProductVariantId = oi.ProductVariantId,
                    ProductName = oi.ProductName,
                    VariantName = oi.VariantName,
                    Quantity = q,
                    UnitPrice = oi.UnitPrice
                });

                if (restock && storeId > 0)
                    await _stock.ApplyAsync(oi.ProductId, oi.ProductVariantId, storeId, q,
                        StockMovementType.Return, refundNumber, userId: userId);
            }

            if (refund.Items.Count == 0)
            {
                TempData["Error"] = "Nothing to refund — choose at least one item with remaining quantity.";
                return RedirectToAction(nameof(Detail), new { id });
            }

            refund.Amount = amount;
            _db.Refunds.Add(refund);

            // Attempt the gateway refund. Best-effort: the refund record + restock are authoritative,
            // so a gateway failure (or an unsupported provider) is flagged for a manual refund rather
            // than blocking the operation.
            string gatewayNote;
            if (!string.IsNullOrEmpty(order.PaymentReference))
            {
                var gw = await _payment.RefundPaymentAsync(new RefundPaymentRequest
                {
                    Reference = order.PaymentReference,
                    Amount = amount,
                    Reason = refund.Reason
                });
                gatewayNote = gw.Success
                    ? $"gateway refund OK ({gw.ProviderReference ?? "no ref"})"
                    : gw.Supported
                        ? $"gateway refund FAILED — refund manually: {gw.ErrorMessage}"
                        : $"gateway refund not automated — {gw.ErrorMessage}";
            }
            else
            {
                gatewayNote = "no payment reference on file — refund manually";
            }

            // Fully refunded (every line's refunded qty now covers the ordered qty) → mark Refunded.
            bool fullyRefunded = order.Items.All(oi =>
                Done(oi.ProductId, oi.ProductVariantId)
                    + refund.Items.Where(ri => ri.ProductId == oi.ProductId && ri.ProductVariantId == oi.ProductVariantId)
                                  .Sum(ri => ri.Quantity)
                >= oi.Quantity);
            if (fullyRefunded) order.Status = OrderStatus.Refunded;
            order.UpdatedAt = now;

            var stamp = $"[{now:u}] Refund {refundNumber}: ₦{amount:N2}, {refund.Items.Sum(r => r.Quantity)} item(s)" +
                        $"{(restock ? " (restocked)" : " (no restock)")}; {gatewayNote}.";
            order.AdminNotes = string.IsNullOrWhiteSpace(order.AdminNotes) ? stamp : order.AdminNotes + "\n" + stamp;

            try
            {
                await _db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                TempData["Error"] = "Stock levels changed while processing the refund. Please try again.";
                return RedirectToAction(nameof(Detail), new { id });
            }

            await LogAsync("Refund", "Order", order.Id.ToString(),
                $"Refund {refundNumber} for {order.OrderNumber}: ₦{amount:N0}, {refund.Items.Sum(r => r.Quantity)} item(s)" +
                $"{(fullyRefunded ? " (full)" : " (partial)")}; {gatewayNote}");
            TempData["Success"] = $"Refund {refundNumber} processed: ₦{amount:N0}. {gatewayNote}.";
            return RedirectToAction(nameof(Detail), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveNote(int id, string adminNotes)
        {
            var order = await _db.Orders.FindAsync(id);
            if (order == null) return NotFound();

            order.AdminNotes = adminNotes?.Trim();
            order.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            TempData["Success"] = "Note saved.";
            return RedirectToAction(nameof(Detail), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveTracking(int id, string trackingNumber)
        {
            var order = await _db.Orders.FindAsync(id);
            if (order == null) return NotFound();

            order.TrackingNumber = trackingNumber?.Trim();
            order.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            await LogAsync("Update", "Order", order.Id.ToString(),
                $"Set tracking number for {order.OrderNumber}: {order.TrackingNumber}");

            TempData["Success"] = "Tracking number saved.";
            return RedirectToAction(nameof(Detail), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BulkUpdateStatus(int[] orderIds, string status)
        {
            if (orderIds == null || orderIds.Length == 0 || string.IsNullOrWhiteSpace(status))
                return RedirectToAction(nameof(Index));

            if (!Enum.TryParse<OrderStatus>(status, out var newStatus))
                return RedirectToAction(nameof(Index));

            var orders = await _db.Orders
                .Where(o => orderIds.Contains(o.Id))
                .ToListAsync();

            foreach (var o in orders)
            {
                o.Status = newStatus;
                o.UpdatedAt = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync();
            await LogAsync("Update", "Order", null,
                $"Bulk updated {orders.Count} order(s) to {status}");
            TempData["Success"] = $"{orders.Count} order(s) updated to {status}.";
            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> ExportCsv(string status = "", string q = "")
        {
            var query = _db.Orders.Include(o => o.User).AsQueryable();

            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<OrderStatus>(status, out var statusEnum))
                query = query.Where(o => o.Status == statusEnum);

            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(o =>
                    o.OrderNumber.Contains(q) ||
                    o.User.FirstName.Contains(q) ||
                    o.User.LastName.Contains(q) ||
                    o.User.Email!.Contains(q));

            var orders = await query
                .OrderByDescending(o => o.CreatedAt)
                .Select(o => new
                {
                    o.OrderNumber,
                    CustomerName = o.User.FirstName + " " + o.User.LastName,
                    CustomerEmail = o.User.Email ?? "",
                    o.Total,
                    o.Subtotal,
                    o.DeliveryFee,
                    Status = o.Status.ToString(),
                    Fulfillment = o.FulfillmentType.ToString(),
                    o.IsPaid,
                    PaymentRef = o.PaymentReference ?? "",
                    CreatedAt = o.CreatedAt.ToString("yyyy-MM-dd HH:mm")
                })
                .ToListAsync();

            var sb = new StringBuilder();
            sb.AppendLine("Order #,Customer Name,Customer Email,Total,Subtotal,Delivery Fee,Status,Fulfillment,Paid,Payment Ref,Created At");

            foreach (var o in orders)
            {
                sb.AppendLine($"\"{o.OrderNumber}\",\"{o.CustomerName}\",\"{o.CustomerEmail}\",{o.Total},{o.Subtotal},{o.DeliveryFee},{o.Status},{o.Fulfillment},{o.IsPaid},\"{o.PaymentRef}\",\"{o.CreatedAt}\"");
            }

            await LogAsync("Export", "Order", null, $"Exported {orders.Count} order(s) to CSV");

            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return File(bytes, "text/csv", $"orders_{DateTime.UtcNow:yyyyMMdd}.csv");
        }
    }
}
