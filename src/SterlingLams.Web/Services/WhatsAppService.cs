using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

/// <summary>Order lifecycle events that can trigger a WhatsApp to the customer. Each maps to a
/// <c>whatsapp.notify.*</c> toggle and a <c>whatsapp.template.*</c> Content SID in settings.</summary>
public enum WhatsAppOrderEvent { OrderConfirmed, PaymentReceived, ReadyForPickup, Shipped, Delivered }

/// <summary>
/// Sends WhatsApp messages via a Business API provider. Phase 1 implements Twilio (works with the
/// Twilio Sandbox for testing before a production number/templates exist). The interface is
/// provider-agnostic: swapping to Termii later is a new branch in <see cref="SendRawAsync"/>, not a
/// change to callers. Credentials are read settings-first (Admin → Integrations, encrypted at rest)
/// with env/config fallback, so entering keys takes effect immediately — no redeploy.
/// </summary>
public interface IWhatsAppService
{
    /// <summary>Sends a plain-text WhatsApp message (sandbox / within an open 24-hour session).
    /// Never throws — returns (ok, message).</summary>
    Task<(bool Ok, string Message)> SendAsync(string toPhone, string body, CancellationToken ct = default);

    /// <summary>Sends an approved template message (business-initiated, outside the 24-hour window).
    /// <paramref name="variables"/> fills the template's {{1}}, {{2}}… placeholders.</summary>
    Task<(bool Ok, string Message)> SendTemplateAsync(string toPhone, string contentSid,
        IDictionary<string, string> variables, CancellationToken ct = default);

    /// <summary>Fire-and-forget order notification: checks the master switch + the event's toggle,
    /// resolves the buyer's phone, and sends the approved template (or a plain body when no template
    /// SID is set yet, so it works on the sandbox). Loads its own DbContext scope; never throws.</summary>
    Task NotifyOrderAsync(int orderId, WhatsAppOrderEvent evt, CancellationToken ct = default);

    /// <summary>True when sending is switched on and the provider credentials are all present.</summary>
    Task<bool> IsConfiguredAsync();
}

public class WhatsAppService : IWhatsAppService
{
    private readonly HttpClient _http;
    private readonly ISettingsService _settings;
    private readonly IConfiguration _config;
    private readonly ILogger<WhatsAppService> _log;
    private readonly IServiceScopeFactory _scopeFactory;

    public WhatsAppService(HttpClient http, ISettingsService settings, IConfiguration config,
        ILogger<WhatsAppService> log, IServiceScopeFactory scopeFactory)
    {
        _http = http;
        _settings = settings;
        _config = config;
        _log = log;
        _scopeFactory = scopeFactory;
    }

    private async Task<string> Get(string key, string? configKey = null)
    {
        var v = await _settings.GetAsync(key, "");
        if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return (configKey is null ? "" : _config[configKey] ?? "").Trim();
    }

    public async Task<bool> IsConfiguredAsync()
    {
        if (!await _settings.GetBoolAsync("whatsapp.enabled", false)) return false;
        return (await Get("whatsapp.twilio.account_sid", "WhatsApp:Twilio:AccountSid")).Length > 0
            && (await Get("whatsapp.twilio.auth_token",   "WhatsApp:Twilio:AuthToken")).Length > 0
            && (await Get("whatsapp.twilio.from",         "WhatsApp:Twilio:From")).Length > 0;
    }

    /// <summary>Best-effort E.164: keep a leading +, turn a Nigerian 0-prefixed 11-digit local number
    /// into +234…, otherwise assume the caller passed an international number.</summary>
    public static string NormalizePhone(string? phone)
    {
        var s = new string((phone ?? "").Where(c => char.IsDigit(c) || c == '+').ToArray());
        if (s.StartsWith("+")) return s;
        if (s.StartsWith("0") && s.Length == 11) return "+234" + s[1..];   // 08012345678 → +2348012345678
        if (s.StartsWith("234")) return "+" + s;
        return s.Length > 0 ? "+" + s : "";
    }

    public Task<(bool Ok, string Message)> SendAsync(string toPhone, string body, CancellationToken ct = default)
        => SendRawAsync(toPhone, new Dictionary<string, string> { ["Body"] = body ?? "" }, ct);

    public Task<(bool Ok, string Message)> SendTemplateAsync(string toPhone, string contentSid,
        IDictionary<string, string> variables, CancellationToken ct = default)
        => SendRawAsync(toPhone, new Dictionary<string, string>
        {
            ["ContentSid"] = contentSid,
            ["ContentVariables"] = System.Text.Json.JsonSerializer.Serialize(variables)
        }, ct);

    // Shared send: applies the current creds + From/To and posts to the provider. messageFields carries
    // either Body (plain) or ContentSid+ContentVariables (template). Never throws.
    private async Task<(bool Ok, string Message)> SendRawAsync(string toPhone,
        Dictionary<string, string> messageFields, CancellationToken ct)
    {
        try
        {
            if (!await _settings.GetBoolAsync("whatsapp.enabled", false))
                return (false, "WhatsApp sending is turned off (Admin → Integrations → WhatsApp).");

            var provider = (await Get("whatsapp.provider")).ToLowerInvariant();
            if (provider.Length == 0) provider = "twilio";
            if (provider != "twilio")
                return (false, $"WhatsApp provider '{provider}' isn't wired yet — only Twilio is available in Phase 1.");

            var sid   = await Get("whatsapp.twilio.account_sid", "WhatsApp:Twilio:AccountSid");
            var token = await Get("whatsapp.twilio.auth_token",  "WhatsApp:Twilio:AuthToken");
            var from  = await Get("whatsapp.twilio.from",        "WhatsApp:Twilio:From");
            if (sid.Length == 0 || token.Length == 0 || from.Length == 0)
                return (false, "Twilio credentials are incomplete — set Account SID, Auth Token and the From number.");

            var to = NormalizePhone(toPhone);
            if (to.Length < 8)
                return (false, "Enter a valid phone number in international format, e.g. +2348012345678.");

            var form = new Dictionary<string, string>(messageFields)
            {
                ["From"] = "whatsapp:" + NormalizePhone(from),
                ["To"]   = "whatsapp:" + to,
            };

            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"https://api.twilio.com/2010-04-01/Accounts/{sid}/Messages.json");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{sid}:{token}")));
            req.Content = new FormUrlEncodedContent(form);

            using var resp = await _http.SendAsync(req, ct);
            var payload = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
                return (true, $"Message queued to {to}.");

            var msg = TryReadTwilioError(payload) ?? $"Twilio returned {(int)resp.StatusCode}.";
            _log.LogWarning("WhatsApp send failed ({Status}): {Payload}", (int)resp.StatusCode, payload);
            return (false, msg);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "WhatsApp send threw");
            return (false, "Could not reach the WhatsApp provider: " + ex.Message);
        }
    }

    private static (string Toggle, string Template) Keys(WhatsAppOrderEvent e) => e switch
    {
        WhatsAppOrderEvent.OrderConfirmed  => ("whatsapp.notify.order_confirmed",  "whatsapp.template.order_confirmed"),
        WhatsAppOrderEvent.PaymentReceived => ("whatsapp.notify.payment_received", "whatsapp.template.payment_received"),
        WhatsAppOrderEvent.ReadyForPickup  => ("whatsapp.notify.ready_for_pickup", "whatsapp.template.ready_for_pickup"),
        WhatsAppOrderEvent.Shipped         => ("whatsapp.notify.shipped",          "whatsapp.template.shipped"),
        WhatsAppOrderEvent.Delivered       => ("whatsapp.notify.delivered",        "whatsapp.template.delivered"),
        _ => ("", ""),
    };

    public async Task NotifyOrderAsync(int orderId, WhatsAppOrderEvent evt, CancellationToken ct = default)
    {
        try
        {
            // Cheap gate first — avoid any DB work when off.
            if (!await _settings.GetBoolAsync("whatsapp.enabled", false)) return;
            var (toggleKey, templateKey) = Keys(evt);
            if (toggleKey.Length == 0 || !await _settings.GetBoolAsync(toggleKey, false)) return;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var order = await db.Orders
                .Include(o => o.User).Include(o => o.Customer).Include(o => o.PickupStore)
                .FirstOrDefaultAsync(o => o.Id == orderId, ct);
            if (order == null || !order.WhatsAppOptIn) return; // customer opted out at checkout

            // POS: the buyer is Order.Customer (Order.User is the cashier). Online: the buyer is Order.User.
            var buyer = order.Channel == OrderChannel.Pos ? order.Customer : order.User;
            var to = NormalizePhone(buyer?.PhoneNumber);
            if (to.Length < 8) return; // no usable phone → nothing to send (email still covers them)

            var name  = string.IsNullOrWhiteSpace(buyer?.FirstName) ? "there" : buyer!.FirstName.Trim();
            var total = $"₦{order.Total:N0}";
            var store = string.IsNullOrWhiteSpace(order.PickupStore?.Name) ? "our store" : order.PickupStore!.Name;
            var site  = await _settings.GetAsync("general.site_name", "Sterlin Glams");

            var templateSid = await Get(templateKey);
            if (templateSid.Length > 0)
            {
                // Production: approved template. Variable positions match the templates in the setup checklist.
                var vars = evt switch
                {
                    WhatsAppOrderEvent.OrderConfirmed  => new Dictionary<string, string> { ["1"] = name, ["2"] = order.OrderNumber, ["3"] = total },
                    WhatsAppOrderEvent.PaymentReceived => new Dictionary<string, string> { ["1"] = order.OrderNumber, ["2"] = total },
                    WhatsAppOrderEvent.ReadyForPickup  => new Dictionary<string, string> { ["1"] = order.OrderNumber, ["2"] = store },
                    WhatsAppOrderEvent.Shipped         => new Dictionary<string, string> { ["1"] = name, ["2"] = order.OrderNumber, ["3"] = "It's on the way." },
                    WhatsAppOrderEvent.Delivered       => new Dictionary<string, string> { ["1"] = order.OrderNumber, ["2"] = site },
                    _ => new Dictionary<string, string>(),
                };
                await SendTemplateAsync(to, templateSid, vars, ct);
            }
            else
            {
                // No approved template yet → plain body (works on the sandbox / open session), so the
                // pipeline is testable before Meta approves templates.
                var body = evt switch
                {
                    WhatsAppOrderEvent.OrderConfirmed  => $"Hi {name}, your {site} order {order.OrderNumber} is confirmed 🎉 Total {total}.",
                    WhatsAppOrderEvent.PaymentReceived => $"Payment received for order {order.OrderNumber} — {total}. Thank you! 💎",
                    WhatsAppOrderEvent.ReadyForPickup  => $"Your order {order.OrderNumber} is ready for pickup at {store}. Show this at the counter.",
                    WhatsAppOrderEvent.Shipped         => $"Good news {name}! Order {order.OrderNumber} is on the way.",
                    WhatsAppOrderEvent.Delivered       => $"Order {order.OrderNumber} delivered ✅ Thank you for shopping with {site}!",
                    _ => $"Update on your order {order.OrderNumber}.",
                };
                await SendAsync(to, body, ct);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "WhatsApp order notify failed (order {OrderId}, {Event})", orderId, evt);
        }
    }

    // Twilio error bodies are JSON: { "code": 63007, "message": "...", "more_info": "..." }.
    private static string? TryReadTwilioError(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("message", out var m)) return m.GetString();
        }
        catch { /* not JSON */ }
        return null;
    }
}
