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
        private readonly ILoyaltyService _loyalty;
        private readonly IGiftCardService _giftCards;
        private readonly IOrderFulfilmentService _fulfilment;
        private readonly IEmailService _email;
        private readonly ISettingsService _settings;
        private readonly SterlingLams.Web.Services.Logistics.ILogisticsDispatchService _logistics;
        private const int PageSize = 25;

        public OrdersController(ApplicationDbContext db, IStockService stock, IPaymentService payment,
            ILoyaltyService loyalty, IGiftCardService giftCards, IOrderFulfilmentService fulfilment, IEmailService email,
            ISettingsService settings,
            SterlingLams.Web.Services.Logistics.ILogisticsDispatchService logistics)
        {
            _db = db;
            _stock = stock;
            _payment = payment;
            _loyalty = loyalty;
            _giftCards = giftCards;
            _fulfilment = fulfilment;
            _email = email;
            _settings = settings;
            _logistics = logistics;
        }

        public async Task<IActionResult> Index(string status = "", string q = "", string channel = "",
            string paid = "", string? from = null, string? to = null, int page = 1)
        {
            ViewData["Title"] = "Orders";

            var query = _db.Orders
                .Include(o => o.User)
                .AsQueryable();

            // "Needs action" = paid orders still waiting on staff (not a real OrderStatus).
            var actionable = new[] { OrderStatus.Pending, OrderStatus.Confirmed, OrderStatus.Processing, OrderStatus.AwaitingTransfer };
            if (status == "NeedsAction")
                query = query.Where(o => o.IsPaid && actionable.Contains(o.Status));
            else if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<OrderStatus>(status, out var statusEnum))
                query = query.Where(o => o.Status == statusEnum);

            if (!string.IsNullOrWhiteSpace(channel) && Enum.TryParse<OrderChannel>(channel, out var channelEnum))
                query = query.Where(o => o.Channel == channelEnum);

            if (paid == "paid") query = query.Where(o => o.IsPaid);
            else if (paid == "unpaid") query = query.Where(o => !o.IsPaid);

            if (DateTime.TryParse(from, out var fromDate))
                query = query.Where(o => o.CreatedAt >= fromDate.Date);
            if (DateTime.TryParse(to, out var toDate))
                query = query.Where(o => o.CreatedAt < toDate.Date.AddDays(1));

            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(o =>
                    o.OrderNumber.Contains(q) ||
                    o.User.FirstName.Contains(q) ||
                    o.User.LastName.Contains(q) ||
                    o.User.Email!.Contains(q) ||
                    o.User.PhoneNumber!.Contains(q));

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
            var allStatuses = new[] { "Pending","Confirmed","Processing","ReadyForPickup","Collected","Shipped","Delivered","Cancelled" };
            var counts = await _db.Orders
                .GroupBy(o => o.Status)
                .Select(g => new { Status = g.Key.ToString(), Count = g.Count() })
                .ToListAsync();
            var statusCounts = allStatuses.ToDictionary(s => s, s => counts.FirstOrDefault(c => c.Status == s)?.Count ?? 0);
            statusCounts[""] = await _db.Orders.CountAsync(); // "All" tab
            statusCounts["NeedsAction"] = await _db.Orders.CountAsync(o => o.IsPaid && actionable.Contains(o.Status));

            var vm = new AdminOrderListViewModel
            {
                Orders = orders,
                StatusFilter = status,
                SearchQuery = q,
                ChannelFilter = channel,
                PaidFilter = paid,
                DateFrom = from ?? "",
                DateTo = to ?? "",
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

            var notes = await _db.OrderNotes
                .Where(n => n.OrderId == id)
                .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
                .ToListAsync();

            var refundedQty = refunds.SelectMany(r => r.Items)
                .GroupBy(ri => new { ri.ProductId, ri.ProductVariantId })
                .ToDictionary(g => (g.Key.ProductId, g.Key.ProductVariantId), g => g.Sum(x => x.Quantity));

            // Customer history: all orders attributed to this buyer (as the user or the POS customer).
            var customerId = order.Channel == OrderChannel.Pos ? order.CustomerUserId : order.UserId;
            int custOrders = 0; decimal custRevenue = 0, custAov = 0;
            if (!string.IsNullOrEmpty(customerId))
            {
                var theirOrders = _db.Orders.Where(o => o.UserId == customerId || o.CustomerUserId == customerId);
                custOrders = await theirOrders.CountAsync();
                var paid = theirOrders.Where(o => o.IsPaid);
                custRevenue = await paid.SumAsync(o => (decimal?)o.Total) ?? 0;
                var paidCount = await paid.CountAsync();
                custAov = paidCount > 0 ? custRevenue / paidCount : 0;
            }

            var vm = new AdminOrderDetailViewModel
            {
                Order = order,
                CustomerName = order.User.FullName,
                CustomerEmail = order.User.Email ?? "",
                Refunds = refunds,
                RefundedQty = refundedQty,
                RefundedTotal = refunds.Sum(r => r.Amount),
                RefundStoreId = order.FulfillingStoreId ?? order.PickupStoreId ?? 0,
                Notes = notes,
                CustomerTotalOrders = custOrders,
                CustomerTotalRevenue = custRevenue,
                CustomerAvgOrderValue = custAov,
                // Online refunds only here; POS returns are handled at the till.
                CanRefund = order.IsPaid && order.Channel == OrderChannel.Online && order.Status != OrderStatus.Refunded
            };

            return View(vm);
        }

        // Print-friendly packing slip (items + ship-to, no prices) and invoice (prices + totals).
        // Both share one standalone print view, toggled by ViewBag.Mode.
        public async Task<IActionResult> PackingSlip(int id) => await PrintDoc(id, "packing");
        public async Task<IActionResult> Invoice(int id) => await PrintDoc(id, "invoice");

        private async Task<IActionResult> PrintDoc(int id, string mode)
        {
            var order = await _db.Orders
                .Include(o => o.Items).Include(o => o.PickupStore).Include(o => o.DeliveryAddress)
                .Include(o => o.User).Include(o => o.Customer)
                .FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();
            ViewBag.Mode = mode;
            return View("PrintDoc", order);
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

                var old = order.Status;

                // Deduct stock when staff move an online order forward for the first time (e.g. they
                // confirmed an offline/bank-transfer payment). The fulfilment engine allocates from the
                // nearest branch + sets up any inter-branch transfers, and is idempotent (it no-ops once
                // FulfillingStoreId is set), so this is safe even if payment already fulfilled the order.
                var needsFulfil = order.Channel == OrderChannel.Online
                    && order.FulfillingStoreId == null
                    && newStatus is OrderStatus.Confirmed or OrderStatus.Processing
                        or OrderStatus.ReadyForPickup or OrderStatus.Shipped or OrderStatus.Delivered;

                if (needsFulfil)
                {
                    var outcome = await _fulfilment.FulfilPaidOrderAsync(order.Id);
                    await _db.Entry(order).ReloadAsync();
                    if (outcome == FulfilOutcome.SoldOut)
                    {
                        TempData["Error"] = $"Order {order.OrderNumber} can't be confirmed — an item is out of stock. It was cancelled.";
                        return RedirectToAction(nameof(Detail), new { id });
                    }
                    // The engine advances cross-branch orders to Awaiting Transfer (the transfer flow
                    // must run) — don't override that. Otherwise honour the status the staff picked.
                    if (order.Status != OrderStatus.AwaitingTransfer)
                    {
                        order.Status = newStatus;
                        OrderNotes.AddSystem(_db, order.Id, $"Marked {newStatus} by staff (stock deducted).");
                    }
                    order.UpdatedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync();
                }
                else
                {
                    order.Status = newStatus;
                    order.UpdatedAt = DateTime.UtcNow;
                    OrderNotes.AddSystem(_db, order.Id, $"Order status changed from {old} to {newStatus} by staff.");
                    await _db.SaveChangesAsync();
                }

                await LogAsync("Update", "Order", order.Id.ToString(),
                    $"Order {order.OrderNumber} status: {old} → {order.Status}");
                TempData["Success"] = $"Order {order.OrderNumber} updated to {order.Status}.";

                // Push the order to the Lagos delivery system once it's a confirmed delivery order
                // (covers the post-transfer "ready" moment + manual confirmation). Idempotent + guarded.
                await _logistics.PushOrderAsync(order.Id);

                // Keep the customer posted as their order reaches each milestone. Only on a real
                // transition (old != new) so re-saving the same status never re-sends. Subjects and
                // intros for these emails are editable in the Email Customizer.
                if (old != order.Status)
                {
                    if (order.Status == OrderStatus.ReadyForPickup
                        && order.FulfillmentType == FulfillmentType.StorePickup)
                    {
                        // Store-pickup: the QR pickup-pass email (sent once, guarded by PickupReadyEmailedAt).
                        if (order.PickupReadyEmailedAt == null)
                            await SendPickupReadyEmailAsync(order.Id);
                    }
                    else if (order.Status is OrderStatus.Processing or OrderStatus.Shipped
                             or OrderStatus.Delivered or OrderStatus.ReadyForPickup)
                    {
                        await SendStatusUpdateEmailAsync(order.Id, order.Status);
                    }
                }
            }

            return RedirectToAction(nameof(Detail), new { id });
        }

        // Sends the "ready for pickup" email with the QR pass (hosted image + pass link). Generates
        // the pickup token if missing and stamps PickupReadyEmailedAt so it only sends once.
        private async Task SendPickupReadyEmailAsync(int orderId)
        {
            var order = await _db.Orders
                .Include(o => o.Items).Include(o => o.PickupStore).Include(o => o.User).Include(o => o.Customer)
                .FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null || order.FulfillmentType != FulfillmentType.StorePickup) return;
            var email = order.User?.Email ?? order.Customer?.Email;
            if (string.IsNullOrWhiteSpace(email)) return;

            if (string.IsNullOrEmpty(order.PickupToken))
            {
                order.PickupToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                await _db.SaveChangesAsync(); // persist the token so the QR pass works even if email send fails
            }

            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            var passUrl = $"{baseUrl}/pickup/{order.PickupToken}";
            var qrUrl = $"{passUrl}/qr.png";
            var store = order.PickupStore;
            var firstName = order.User?.FirstName ?? order.Customer?.FirstName ?? "there";
            string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

            // Subject + intro are editable in the Email Customizer ("Ready for pickup" template).
            var def = EmailCustomizerController.Types.FirstOrDefault(t => t.Key == "ready_for_pickup");
            var subject = await _settings.GetAsync("email.ready_for_pickup.subject",
                def.DefaultSubject ?? $"Your order {order.OrderNumber} is ready for pickup");
            var introText = await _settings.GetAsync("email.ready_for_pickup.intro", def.DefaultIntro ?? "");
            var introHtml = OrderEmailTemplate.ApplyPlaceholders(introText, order.OrderNumber, order.CreatedAt, firstName);

            var items = string.Join("", order.Items.Select(i =>
                $"<tr><td style=\"padding:4px 0;color:#374151\">{Enc(i.ProductName)}{(i.VariantName != null ? " (" + Enc(i.VariantName) + ")" : "")} &times; {i.Quantity}</td>"
                + $"<td style=\"padding:4px 0;text-align:right;color:#111\">₦{i.LineTotal:N0}</td></tr>"));

            var storeBlock = store == null ? "" :
                $"<p style=\"margin:0 0 2px;font-weight:600\">{Enc(store.Name)}</p>"
                + $"<p style=\"margin:0;color:#6b7280;font-size:13px\">{Enc($"{store.Address}, {store.City}, {store.State}".Trim(' ', ','))}</p>"
                + (string.IsNullOrWhiteSpace(store.Phone) ? "" : $"<p style=\"margin:2px 0 0;color:#6b7280;font-size:13px\">{Enc(store.Phone)}</p>");

            var body =
                $"<p>Hi {Enc(firstName)},</p>"
                + $"<p>{introHtml}</p>"
                + $"<div style=\"text-align:center;margin:18px 0\"><img src=\"{qrUrl}\" alt=\"Pickup QR code\" width=\"200\" height=\"200\" style=\"width:200px;height:200px\" /><br/>"
                + $"<span style=\"font:12px monospace;color:#6b7280\">{Enc(order.OrderNumber)}</span></div>"
                + $"<div style=\"text-align:center;margin:0 0 18px\"><a href=\"{passUrl}\" style=\"display:inline-block;background:#ed028b;color:#fff;text-decoration:none;padding:10px 22px;border-radius:8px;font-weight:600\">View pickup pass</a></div>"
                + $"<p style=\"font-weight:600;margin:18px 0 4px\">Pickup location</p>{storeBlock}"
                + $"<table style=\"width:100%;border-collapse:collapse;margin-top:16px;font-size:14px\">{items}"
                + $"<tr><td style=\"padding-top:8px;border-top:1px solid #eee;font-weight:700\">Total</td><td style=\"padding-top:8px;border-top:1px solid #eee;text-align:right;font-weight:700\">₦{order.Total:N0}</td></tr></table>"
                + "<p style=\"color:#6b7280;font-size:13px;margin-top:16px\">Please bring a valid ID. This pass is unique to your order.</p>";

            var sent = await _email.SendAsync(email!, subject, body,
                order.User?.FullName ?? order.Customer?.FullName);
            if (sent)
            {
                order.PickupReadyEmailedAt = DateTime.UtcNow;
                OrderNotes.AddSystem(_db, order.Id, "Ready-for-pickup email with QR pass sent to the customer.");
                await _db.SaveChangesAsync();
            }
        }

        // Maps an order status to its Email Customizer template key.
        private static string StatusTemplateKey(OrderStatus s) => s switch
        {
            OrderStatus.Processing     => "order_processing",
            OrderStatus.ReadyForPickup => "ready_for_pickup",
            OrderStatus.Shipped        => "order_shipped",
            OrderStatus.Delivered      => "order_delivered",
            _                          => "order_confirmed",
        };

        // Sends a customer-facing status-update email (Processing / Shipped / Delivered, plus Ready for
        // pickup on delivery orders). Subject + intro come from the Email Customizer; body is the shared
        // compact order summary. Best-effort — a missing customer email or SMTP failure is a no-op.
        private async Task SendStatusUpdateEmailAsync(int orderId, OrderStatus status)
        {
            var order = await _db.Orders
                .Include(o => o.Items).Include(o => o.User).Include(o => o.Customer)
                .FirstOrDefaultAsync(o => o.Id == orderId);
            if (order == null) return;
            var email = order.User?.Email ?? order.Customer?.Email;
            if (string.IsNullOrWhiteSpace(email)) return;

            var key = StatusTemplateKey(status);
            var def = EmailCustomizerController.Types.FirstOrDefault(t => t.Key == key);
            var subject = await _settings.GetAsync($"email.{key}.subject", def.DefaultSubject ?? "Your order update");
            var introText = await _settings.GetAsync($"email.{key}.intro", def.DefaultIntro ?? "");

            var firstName = order.User?.FirstName ?? order.Customer?.FirstName ?? "there";
            var introHtml = OrderEmailTemplate.ApplyPlaceholders(introText, order.OrderNumber, order.CreatedAt, firstName);
            var items = order.Items
                .Select(i => new OrderEmailTemplate.Item(i.ProductName, i.VariantName, i.Quantity, i.LineTotal, null))
                .ToList();

            var body = OrderEmailTemplate.BuildStatusUpdate(subject, introHtml, order.OrderNumber, items, order.Total);
            var sent = await _email.SendAsync(email!, subject, body,
                order.User?.FullName ?? order.Customer?.FullName);
            if (sent)
            {
                OrderNotes.AddSystem(_db, order.Id, $"'{def.Label ?? status.ToString()}' status email sent to the customer.");
                await _db.SaveChangesAsync();
            }
        }

        // Re-send a customer email for an order: an order summary, or (for store-pickup orders) the
        // QR pickup pass. Useful when the original bounced or the customer lost it.
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ResendEmail(int id, string type = "summary")
        {
            var order = await _db.Orders
                .Include(o => o.Items).Include(o => o.PickupStore).Include(o => o.User).Include(o => o.Customer)
                .FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();

            var to = order.User?.Email ?? order.Customer?.Email;
            if (string.IsNullOrWhiteSpace(to))
            {
                TempData["Error"] = "This order has no customer email on file.";
                return RedirectToAction(nameof(Detail), new { id });
            }

            if (type == "pickup")
            {
                if (order.FulfillmentType != FulfillmentType.StorePickup)
                    TempData["Error"] = "A pickup pass only applies to store-pickup orders.";
                else
                {
                    await SendPickupReadyEmailAsync(order.Id); // re-sends and re-stamps; logs its own note
                    await LogAsync("EmailResend", "Order", order.Id.ToString(), $"Re-sent pickup pass for {order.OrderNumber} to {to}");
                    TempData["Success"] = $"Pickup pass re-sent to {to}.";
                }
                return RedirectToAction(nameof(Detail), new { id });
            }

            // Default: an order summary / confirmation.
            string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
            var firstName = order.User?.FirstName ?? order.Customer?.FirstName ?? "there";
            var rows = string.Join("", order.Items.Select(i =>
                $"<tr><td style=\"padding:4px 0;color:#374151\">{Enc(i.ProductName)}{(i.VariantName != null ? " (" + Enc(i.VariantName) + ")" : "")} &times; {i.Quantity}</td>"
                + $"<td style=\"padding:4px 0;text-align:right;color:#111\">₦{i.LineTotal:N0}</td></tr>"));
            var body =
                $"<p>Hi {Enc(firstName)},</p>"
                + $"<p>Here's a summary of your order <strong>{Enc(order.OrderNumber)}</strong> (status: {Enc(order.Status.ToString())}).</p>"
                + $"<table style=\"width:100%;border-collapse:collapse;margin-top:12px;font-size:14px\">{rows}"
                + $"<tr><td style=\"padding-top:8px;border-top:1px solid #eee;font-weight:700\">Total</td><td style=\"padding-top:8px;border-top:1px solid #eee;text-align:right;font-weight:700\">₦{order.Total:N0}</td></tr></table>"
                + "<p style=\"color:#6b7280;font-size:13px;margin-top:16px\">Thank you for shopping with Sterlin Glams.</p>";

            var sent = await _email.SendAsync(to!, $"Your order {order.OrderNumber}", body,
                order.User?.FullName ?? order.Customer?.FullName);
            if (sent)
            {
                OrderNotes.AddSystem(_db, order.Id, $"Order summary email re-sent to {to}.");
                await _db.SaveChangesAsync();
                await LogAsync("EmailResend", "Order", order.Id.ToString(), $"Re-sent order summary for {order.OrderNumber} to {to}");
                TempData["Success"] = $"Order summary re-sent to {to}.";
            }
            else TempData["Error"] = "Could not send the email. Check email settings.";
            return RedirectToAction(nameof(Detail), new { id });
        }

        // ── Order notes (WooCommerce-style timeline) ──────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddNote(int id, string content, string noteType = "private")
        {
            var order = await _db.Orders.Include(o => o.User).FirstOrDefaultAsync(o => o.Id == id);
            if (order == null) return NotFound();
            content = (content ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(content))
            {
                TempData["Error"] = "Note can't be empty.";
                return RedirectToAction(nameof(Detail), new { id });
            }

            var isCustomerNote = string.Equals(noteType, "customer", StringComparison.OrdinalIgnoreCase);
            var author = User.Identity?.Name ?? "Staff";

            _db.OrderNotes.Add(new OrderNote
            {
                OrderId = id,
                Content = content,
                IsSystem = false,
                IsCustomerNote = isCustomerNote,
                AuthorName = author,
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
            await LogAsync("Update", "Order", id.ToString(),
                $"Added {(isCustomerNote ? "customer" : "private")} note to order {order.OrderNumber}");

            // A "note to customer" is emailed to the buyer (best-effort).
            if (isCustomerNote && !string.IsNullOrEmpty(order.User?.Email))
            {
                var html = $"<p>Hello {System.Net.WebUtility.HtmlEncode(order.User.FullName)},</p>"
                         + $"<p>A note has been added to your order <strong>{order.OrderNumber}</strong>:</p>"
                         + $"<blockquote style=\"border-left:3px solid #ec1c8e;padding-left:12px;color:#555\">{System.Net.WebUtility.HtmlEncode(content)}</blockquote>";
                await _email.SendAsync(order.User.Email!, $"Update on your order {order.OrderNumber}", html, order.User.FullName);
            }

            TempData["Success"] = isCustomerNote ? "Note added and emailed to the customer." : "Private note added.";
            return RedirectToAction(nameof(Detail), new { id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteNote(int id, int noteId)
        {
            var note = await _db.OrderNotes.FirstOrDefaultAsync(n => n.Id == noteId && n.OrderId == id);
            if (note != null)
            {
                _db.OrderNotes.Remove(note);
                await _db.SaveChangesAsync();
                await LogAsync("Update", "Order", id.ToString(), $"Deleted an order note ({noteId})");
            }
            return RedirectToAction(nameof(Detail), new { id });
        }

        // ── Online order refund ───────────────────────────────────────────────
        // Mirrors the POS return flow (PosController.RefundProcess) but for online orders:
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

                // Refund what the customer actually PAID: unit price minus this line's discount,
                // spread across the quantity sold (gross price over-refunded discounted items).
                var netUnit = oi.Quantity > 0 ? Math.Round(oi.UnitPrice - oi.DiscountAmount / oi.Quantity, 2) : oi.UnitPrice;
                amount += netUnit * q;
                refund.Items.Add(new RefundItem
                {
                    ProductId = oi.ProductId,
                    ProductVariantId = oi.ProductVariantId,
                    ProductName = oi.ProductName,
                    VariantName = oi.VariantName,
                    Quantity = q,
                    UnitPrice = netUnit
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

            // Subtract the proportional share of any order-level discount (loyalty redemption), which
            // isn't attached to a line — otherwise a redeemed order refunds more than was tendered.
            var lineDiscountTotal = order.Items.Sum(i => i.DiscountAmount);
            var orderLevelDiscount = Math.Max(0, order.DiscountAmount - lineDiscountTotal);
            var totalNet = order.Items.Sum(i => i.UnitPrice * i.Quantity - i.DiscountAmount);
            if (orderLevelDiscount > 0 && totalNet > 0)
                amount -= Math.Round(orderLevelDiscount * amount / totalNet, 2);

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
            OrderNotes.AddSystem(_db, order.Id,
                $"Refunded ₦{amount:N0} ({refund.Items.Sum(r => r.Quantity)} item(s)){(restock ? ", stock restocked" : "")} — {gatewayNote}."
                + (fullyRefunded ? " Order fully refunded." : ""));

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

            // On a full refund, reverse loyalty (claw back earned, return redeemed) and return any
            // gift-card balance that was drawn on the order.
            if (fullyRefunded)
            {
                await _loyalty.ReverseForOrderAsync(order.Id);
                await _giftCards.ReverseForOrderAsync(order.Id);
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

        public async Task<IActionResult> ExportCsv(string status = "", string q = "",
            string channel = "", string paid = "", string? from = null, string? to = null)
        {
            var query = _db.Orders.Include(o => o.User).AsQueryable();

            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<OrderStatus>(status, out var statusEnum))
                query = query.Where(o => o.Status == statusEnum);

            if (!string.IsNullOrWhiteSpace(channel) && Enum.TryParse<OrderChannel>(channel, out var channelEnum))
                query = query.Where(o => o.Channel == channelEnum);

            if (paid == "paid") query = query.Where(o => o.IsPaid);
            else if (paid == "unpaid") query = query.Where(o => !o.IsPaid);

            if (DateTime.TryParse(from, out var fromDate))
                query = query.Where(o => o.CreatedAt >= fromDate.Date);
            if (DateTime.TryParse(to, out var toDate))
                query = query.Where(o => o.CreatedAt < toDate.Date.AddDays(1));

            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(o =>
                    o.OrderNumber.Contains(q) ||
                    o.User.FirstName.Contains(q) ||
                    o.User.LastName.Contains(q) ||
                    o.User.Email!.Contains(q) ||
                    o.User.PhoneNumber!.Contains(q));

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
