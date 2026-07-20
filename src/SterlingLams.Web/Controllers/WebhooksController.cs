using Microsoft.AspNetCore.Mvc;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services.Payment;
using Microsoft.EntityFrameworkCore;
using System.IO;
using System.Text;

namespace SterlingLams.Web.Controllers;

[ApiController]
[Route("webhooks")]
public class WebhooksController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IPaymentService _payment;
    private readonly SterlingLams.Web.Services.IOrderFulfilmentService _fulfilment;
    private readonly SterlingLams.Web.Services.ILoyaltyService _loyalty;
    private readonly SterlingLams.Web.Services.IGiftCardService _giftCards;
    private readonly SterlingLams.Web.Services.Logistics.ILogisticsDispatchService _logistics;
    private readonly ISubscriptionPaymentService _subPay;
    private readonly ILogger<WebhooksController> _logger;

    private readonly SterlingLams.Web.Services.IAuditService _audit;
    private readonly SterlingLams.Web.Services.IWhatsAppService _whatsapp;

    public WebhooksController(
        ApplicationDbContext db,
        IPaymentService payment,
        SterlingLams.Web.Services.IOrderFulfilmentService fulfilment,
        SterlingLams.Web.Services.ILoyaltyService loyalty,
        SterlingLams.Web.Services.IGiftCardService giftCards,
        SterlingLams.Web.Services.Logistics.ILogisticsDispatchService logistics,
        ISubscriptionPaymentService subPay,
        SterlingLams.Web.Services.IAuditService audit,
        SterlingLams.Web.Services.IWhatsAppService whatsapp,
        ILogger<WebhooksController> logger)
    {
        _db = db;
        _payment = payment;
        _fulfilment = fulfilment;
        _loyalty = loyalty;
        _giftCards = giftCards;
        _logistics = logistics;
        _subPay = subPay;
        _audit = audit;
        _whatsapp = whatsapp;
        _logger = logger;
    }

    /// <summary>
    /// Paystack webhook for the API-connector SUBSCRIPTION (developer's Paystack account). Validated
    /// with the developer key. On a verified charge.success for a subscription reference, activates the
    /// subscription — so activation still happens even if the admin closed the tab before the redirect.
    /// Idempotent. Configure this URL (/webhooks/subscription) in the DEVELOPER's Paystack dashboard.
    /// </summary>
    [HttpPost("subscription")]
    public async Task<IActionResult> SubscriptionWebhook()
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var payload = await reader.ReadToEndAsync();
        var signature = Request.Headers["x-paystack-signature"].FirstOrDefault() ?? string.Empty;

        if (!await _subPay.ValidateWebhookAsync(payload, signature))
        {
            _logger.LogWarning("Invalid subscription Paystack webhook signature");
            return Unauthorized();
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var eventType = root.TryGetProperty("event", out var evtProp) ? evtProp.GetString() : null;
            if (eventType != "charge.success") return Ok();

            var data = root.GetProperty("data");
            var reference = data.TryGetProperty("reference", out var refProp) ? refProp.GetString() : null;

            // Only handle our subscription charges (metadata.purpose stamped at initiation).
            string? purpose = null, plan = null;
            if (data.TryGetProperty("metadata", out var meta) && meta.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (meta.TryGetProperty("purpose", out var p)) purpose = p.GetString();
                if (meta.TryGetProperty("plan", out var pl)) plan = pl.GetString();
            }
            if (purpose != "api_connector_subscription" || string.IsNullOrEmpty(reference))
                return Ok(); // not ours / nothing to do — ack so Paystack stops retrying

            // Re-verify against the API before activating (defence in depth), then activate idempotently.
            var (ok, paid, _) = await _subPay.VerifyAsync(reference);
            if (ok && paid)
            {
                var renews = await _subPay.ActivateAsync(plan ?? "monthly");
                try { await _audit.LogAsync("Update", "Subscription", null, $"Subscription activated via webhook ({reference}); renews {renews}", performedBy: "API System"); } catch { }
                _logger.LogInformation("Subscription activated via webhook: {Reference}", reference);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Subscription webhook processing failed");
            // Still ack — a 500 would make Paystack retry indefinitely; the browser callback is the backup.
        }
        return Ok();
    }

    [HttpPost("paystack")]
    public async Task<IActionResult> PaystackWebhook()
    {
        // Read raw body for HMAC validation
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var payload = await reader.ReadToEndAsync();

        var signature = Request.Headers["x-paystack-signature"].FirstOrDefault() ?? string.Empty;

        if (!await _payment.ValidateWebhookAsync(payload, signature))
        {
            _logger.LogWarning("Invalid Paystack webhook signature");
            return Unauthorized();
        }

        // Parse event type
        using var doc = System.Text.Json.JsonDocument.Parse(payload);
        var root = doc.RootElement;

        var eventType = root.TryGetProperty("event", out var evtProp) ? evtProp.GetString() : null;
        _logger.LogInformation("Paystack webhook event: {Event}", eventType);

        if (eventType == "charge.success")
        {
            var data = root.GetProperty("data");
            var reference = data.GetProperty("reference").GetString();
            var amount = data.GetProperty("amount").GetDecimal() / 100m;

            // Resolve the order precisely — no loose substring matching (a short OrderNumber could
            // otherwise be a substring of an unrelated reference). We stamp order_number into the
            // transaction metadata at initiation, so match that exactly; fall back to the exact
            // stored PaymentReference (set once the browser callback has run).
            string? orderNumber = null;
            if (data.TryGetProperty("metadata", out var meta)
                && meta.ValueKind == System.Text.Json.JsonValueKind.Object
                && meta.TryGetProperty("order_number", out var onProp))
                orderNumber = onProp.GetString();

            Order? order = null;
            if (!string.IsNullOrEmpty(orderNumber))
                order = await _db.Orders.FirstOrDefaultAsync(o => o.OrderNumber == orderNumber);
            if (order == null && !string.IsNullOrEmpty(reference))
                order = await _db.Orders.FirstOrDefaultAsync(o => o.PaymentReference == reference);

            if (order != null && !order.IsPaid)
            {
                // Verify the amount actually paid covers what we charged. The HMAC already proves the
                // payload is genuinely from Paystack, but this guards against a stale/mismatched
                // charge (e.g. a partial payment) marking a full order as paid.
                if (amount + 0.01m < order.Total)
                {
                    _logger.LogWarning(
                        "Paystack webhook amount mismatch for {OrderNumber}: paid {Paid} but order total is {Total}",
                        order.OrderNumber, amount, order.Total);
                    order.AdminNotes = $"[{DateTime.UtcNow:u}] Webhook amount mismatch: paid ₦{amount:N2} vs total ₦{order.Total:N2}. Needs review; not auto-fulfilled.";
                    await _db.SaveChangesAsync();
                    return Ok(); // ack so Paystack stops retrying; flagged for a human
                }

                var wasUnpaid = !order.IsPaid;
                order.IsPaid = true;
                order.PaidAt = DateTime.UtcNow;
                order.Status = OrderStatus.Confirmed;
                order.PaymentReference = reference;
                order.PaymentProvider = "Paystack";
                if (wasUnpaid)
                    SterlingLams.Web.Services.OrderNotes.AddSystem(_db, order.Id,
                        $"Payment via Paystack successful (Transaction Reference: {reference}).");
                await _db.SaveChangesAsync();
                if (wasUnpaid)
                {
                    try { await _audit.LogAsync("Payment", "Order", order.Id.ToString(), $"Payment received for {order.OrderNumber} — ₦{order.Total:N0} (Paystack)"); } catch { }
                    _ = _whatsapp.NotifyOrderAsync(order.Id, SterlingLams.Web.Services.WhatsAppOrderEvent.PaymentReceived);
                }

                // Deduct stock through the in-house ledger. Idempotent, so it's safe whether the
                // browser callback already fulfilled this order or the webhook is the only path
                // that fires (e.g. customer closed the tab right after paying). If the item sold
                // out before this payment landed, the service auto-cancels + refunds — skip loyalty.
                var outcome = await _fulfilment.FulfilPaidOrderAsync(order.Id);
                if (outcome == SterlingLams.Web.Services.FulfilOutcome.SoldOut)
                {
                    _logger.LogWarning("Order {OrderNumber} sold out before its webhook-confirmed payment — auto-refunded.", order.OrderNumber);
                    return Ok();
                }
                await _loyalty.RedeemForOrderAsync(order.Id);
                await _giftCards.RedeemForOrderAsync(order.Id);
                await _loyalty.AccrueForOrderAsync(order.Id);
                await _logistics.PushOrderAsync(order.Id);

                _logger.LogInformation("Order {OrderNumber} marked as paid via webhook", order.OrderNumber);
            }
        }

        return Ok();
    }

    /// <summary>
    /// Inbound from Sterlin Glams Logistics: a driver marked a delivery complete. Verifies the
    /// HMAC-SHA256 signature (x-sg-signature, shared secret), then marks the matching order
    /// Delivered. Idempotent — re-delivery of the same event is a no-op. The logistics app already
    /// notifies the customer, so we don't re-email here.
    /// </summary>
    [HttpPost("logistics/delivered")]
    public async Task<IActionResult> LogisticsDelivered()
    {
        if (!_logistics.IsConfigured)
            return StatusCode(503, new { ok = false, error = "Logistics integration not configured." });

        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var raw = await reader.ReadToEndAsync();

        var sig = Request.Headers["x-sg-signature"].FirstOrDefault();
        if (string.IsNullOrEmpty(sig) || !FixedTimeEquals(sig, _logistics.ComputeSignature(raw)))
        {
            _logger.LogWarning("Rejected logistics delivered callback: bad signature.");
            return Unauthorized(new { ok = false, error = "Invalid signature." });
        }

        string? orderNumber = null;
        string? signerName = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("orderNumber", out var on)) orderNumber = on.GetString();
            if (root.TryGetProperty("signerName", out var sn)) signerName = sn.GetString();
        }
        catch { return BadRequest(new { ok = false, error = "Invalid JSON." }); }

        if (string.IsNullOrWhiteSpace(orderNumber))
            return BadRequest(new { ok = false, error = "orderNumber is required." });

        var order = await _db.Orders.FirstOrDefaultAsync(o => o.OrderNumber == orderNumber);
        if (order == null)
        {
            // Not one of ours (e.g. a legacy WooCommerce order) — ack so logistics doesn't retry.
            _logger.LogInformation("Logistics delivered callback for unknown order {OrderNumber} — ignored.", orderNumber);
            return Ok(new { ok = true, matched = false });
        }

        // Idempotent + don't override a terminal state.
        if (order.Status is OrderStatus.Delivered or OrderStatus.Cancelled or OrderStatus.Refunded)
            return Ok(new { ok = true, alreadyFinal = true, status = order.Status.ToString() });

        var old = order.Status;
        order.Status = OrderStatus.Delivered;
        order.UpdatedAt = DateTime.UtcNow;
        var note = $"Marked Delivered via Sterlin Glams Logistics{(string.IsNullOrWhiteSpace(signerName) ? "" : $" (signed by {signerName})")}.";
        SterlingLams.Web.Services.OrderNotes.AddSystem(_db, order.Id, note);
        await _db.SaveChangesAsync();
        try { await _audit.LogAsync("Delivery", "Order", order.Id.ToString(), $"Order {order.OrderNumber} delivered (logistics): {old} → Delivered"); } catch { }

        _logger.LogInformation("Order {OrderNumber} marked Delivered via logistics callback.", order.OrderNumber);
        return Ok(new { ok = true, matched = true });
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
