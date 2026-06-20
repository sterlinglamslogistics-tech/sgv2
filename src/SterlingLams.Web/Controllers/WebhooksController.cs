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
    private readonly ILogger<WebhooksController> _logger;

    private readonly SterlingLams.Web.Services.IAuditService _audit;

    public WebhooksController(
        ApplicationDbContext db,
        IPaymentService payment,
        SterlingLams.Web.Services.IOrderFulfilmentService fulfilment,
        SterlingLams.Web.Services.ILoyaltyService loyalty,
        SterlingLams.Web.Services.IAuditService audit,
        ILogger<WebhooksController> logger)
    {
        _db = db;
        _payment = payment;
        _fulfilment = fulfilment;
        _loyalty = loyalty;
        _audit = audit;
        _logger = logger;
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
                    try { await _audit.LogAsync("Payment", "Order", order.Id.ToString(), $"Payment received for {order.OrderNumber} — ₦{order.Total:N0} (Paystack)"); } catch { }

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
                await _loyalty.AccrueForOrderAsync(order.Id);

                _logger.LogInformation("Order {OrderNumber} marked as paid via webhook", order.OrderNumber);
            }
        }

        return Ok();
    }
}
