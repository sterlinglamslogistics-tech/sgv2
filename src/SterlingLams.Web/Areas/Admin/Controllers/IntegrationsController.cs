using Microsoft.AspNetCore.Mvc;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers;

/// <summary>
/// Payment gateway keys, webhook config and SMTP credentials — the sensitive integration settings.
/// <c>Section => null</c> makes <see cref="AdminBaseController"/> restrict this to full administrators
/// only (no staff role can reach it), honouring "only me should have access to it". Secret values are
/// encrypted at rest and never rendered back to the browser.
/// </summary>
public class IntegrationsController : AdminBaseController
{
    protected override string? Section => "Integrations"; // grantable; write actions still require :manage

    private static readonly string[] Groups = { "Payments", "SMTP" };
    private static readonly string[] Providers = { "paystack", "stripe", "flutterwave" };

    private readonly ISettingsService _settings;
    private readonly ISettingsSecretProtector _secrets;
    private readonly IConfiguration _config;

    public IntegrationsController(ISettingsService settings, ISettingsSecretProtector secrets, IConfiguration config)
    {
        _settings = settings;
        _secrets = secrets;
        _config = config;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Integrations";

        // Raw DB values (secrets stay encrypted here — we only need to know if they're set).
        var raw = (await _settings.GetAllAsync())
            .Where(s => Groups.Contains(s.Group))
            .ToDictionary(s => s.Key, s => s.Value ?? "");

        // A secret is "configured" if it's stored in the DB or supplied via appsettings/env.
        bool Set(string key, string? configKey) =>
            !string.IsNullOrWhiteSpace(raw.GetValueOrDefault(key))
            || !string.IsNullOrWhiteSpace(configKey is null ? null : _config[configKey]);

        string Plain(string key, string? configKey) =>
            raw.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
                ? _secrets.Reveal(v)
                : (configKey is null ? "" : _config[configKey] ?? "");

        var baseUrl = (_config["App:BaseUrl"] ?? "").TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
            baseUrl = $"{Request.Scheme}://{Request.Host}";

        var vm = new IntegrationsViewModel
        {
            Provider              = (await _settings.GetAsync("payment.provider", _config["Payment:Provider"] ?? "paystack")).ToLowerInvariant(),
            PaystackPublicKey     = Plain("payment.paystack.public_key", "Payment:Paystack:PublicKey"),
            PaystackSecretSet     = Set("payment.paystack.secret_key", "Payment:Paystack:SecretKey"),
            StripePublishableKey  = Plain("payment.stripe.publishable_key", "Payment:Stripe:PublishableKey"),
            StripeSecretSet       = Set("payment.stripe.secret_key", "Payment:Stripe:SecretKey"),
            StripeWebhookSet      = Set("payment.stripe.webhook_secret", "Payment:Stripe:WebhookSecret"),
            FlutterwavePublicKey  = Plain("payment.flutterwave.public_key", "Payment:Flutterwave:PublicKey"),
            FlutterwaveSecretSet  = Set("payment.flutterwave.secret_key", "Payment:Flutterwave:SecretKey"),
            FlutterwaveEncSet     = Set("payment.flutterwave.encryption_key", "Payment:Flutterwave:EncryptionKey"),

            SmtpEnabled     = await _settings.GetBoolAsync("email.smtp.enabled", false),
            SmtpHost        = Plain("email.smtp.host", "Email:Host"),
            SmtpPort        = await _settings.GetIntAsync("email.smtp.port", 587),
            SmtpUsername    = Plain("email.smtp.username", "Email:Username"),
            SmtpPasswordSet = Set("email.smtp.password", "Email:Password"),
            SmtpFromAddress = Plain("email.smtp.from_address", "Email:FromAddress"),
            SmtpFromName    = string.IsNullOrWhiteSpace(Plain("email.smtp.from_name", null)) ? "Sterlin Glams" : Plain("email.smtp.from_name", null),
            SmtpSsl         = await _settings.GetBoolAsync("email.smtp.ssl", true),

            BaseUrl = baseUrl,
        };
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(IFormCollection form)
    {
        var defs = (await _settings.GetAllAsync()).Where(s => Groups.Contains(s.Group)).ToList();
        var updates = new Dictionary<string, string>();
        int secretsUpdated = 0;

        foreach (var s in defs)
        {
            if (s.Key == "payment.provider")
            {
                var p = (form["payment.provider"].ToString() ?? "").Trim().ToLowerInvariant();
                updates[s.Key] = Providers.Contains(p) ? p : "paystack";
            }
            else if (s.Type == "boolean")
            {
                updates[s.Key] = form.ContainsKey(s.Key) ? "true" : "false";
            }
            else if (s.Type == "secret")
            {
                // Secret fields render blank (never echoed). A blank submit means "keep current";
                // a non-blank submit replaces the stored secret with a freshly encrypted value.
                var entered = form[s.Key].ToString();
                if (!string.IsNullOrWhiteSpace(entered))
                {
                    updates[s.Key] = _secrets.Protect(entered.Trim());
                    secretsUpdated++;
                }
            }
            else if (form.ContainsKey(s.Key))
            {
                updates[s.Key] = form[s.Key].ToString().Trim();
            }
        }

        await _settings.SaveManyAsync(updates);
        await LogAsync("Update", "Setting", null,
            $"Updated Integrations settings ({updates.Count} fields, {secretsUpdated} secret(s) changed)");
        TempData["Success"] = "Integration settings saved.";
        return RedirectToAction(nameof(Index));
    }
}

public class IntegrationsViewModel
{
    public string Provider { get; set; } = "paystack";
    public string PaystackPublicKey { get; set; } = "";
    public bool PaystackSecretSet { get; set; }
    public string StripePublishableKey { get; set; } = "";
    public bool StripeSecretSet { get; set; }
    public bool StripeWebhookSet { get; set; }
    public string FlutterwavePublicKey { get; set; } = "";
    public bool FlutterwaveSecretSet { get; set; }
    public bool FlutterwaveEncSet { get; set; }

    public bool SmtpEnabled { get; set; }
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUsername { get; set; } = "";
    public bool SmtpPasswordSet { get; set; }
    public string SmtpFromAddress { get; set; } = "";
    public string SmtpFromName { get; set; } = "Sterlin Glams";
    public bool SmtpSsl { get; set; } = true;

    public string BaseUrl { get; set; } = "";
}
