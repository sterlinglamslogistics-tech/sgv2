using Microsoft.AspNetCore.Mvc;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers;

public class EmailCustomizerController : AdminBaseController
{
    protected override string Section => "Emails";

    private readonly ISettingsService _settings;
    private readonly IEmailService _email;

    public EmailCustomizerController(ISettingsService settings, IEmailService email)
    {
        _settings = settings;
        _email = email;
    }

    // The customer-facing emails whose subject + intro are editable.
    public static readonly (string Key, string Label, string DefaultSubject, string DefaultIntro)[] Types = new[]
    {
        ("order_confirmed",  "Order confirmation", "Your order is being processed", "Your order {order} ({date}) has been received and is now being processed."),
        ("order_processing", "Processing",         "Your order is being prepared",  "Good news {name} — your order {order} is now being prepared and will be on its way soon."),
        ("ready_for_pickup", "Ready for pickup",   "Your order is ready for pickup", "Your order {order} is ready to collect. Show the QR code below at the counter and we'll hand it over."),
        ("order_shipped",    "Shipped",            "Your order is on its way",      "Great news — your order {order} has been shipped and is on its way to you."),
        ("order_delivered",  "Delivered",          "Your order has been delivered", "Your order {order} has been delivered. We hope you love it — thank you for shopping with us!"),
        ("back_in_stock",   "Back in stock",      "Good news — it's back in stock", "An item you wanted is available again. These pieces sell quickly, so don't wait."),
        ("abandoned_cart",  "Abandoned cart",     "You left something in your bag", "You have items waiting in your bag — we've saved them for you."),
        ("password_reset",  "Password reset",     "Reset your password", "We received a request to reset your password. Click below to choose a new one. This link expires shortly."),
        ("email_confirm",   "Email confirmation", "Confirm your email", "Thanks for creating an account with us. Please confirm this is your email address by clicking below."),
        // Branch/staff emails — sent to a store's email (not the customer). Placeholders: {branch}, {order}.
        ("branch_transfer_request", "Transfer request (to branch)", "Send stock to {branch} — order {order}", "Please pack and send the stock below to {branch} so order {order} can be fulfilled."),
        ("branch_dispatch",         "Order dispatch (to branch)",   "Dispatch order {order}",                 "All stock for order {order} is now at your branch — please pack and fulfil it."),
    };

    public async Task<IActionResult> Index(string type = "order_confirmed")
    {
        if (!Types.Any(t => t.Key == type)) type = "order_confirmed";
        ViewData["Title"] = "Email Customizer";

        var def = Types.First(t => t.Key == type);
        var vm = new EmailCustomizerViewModel
        {
            Type = type,
            Types = Types.Select(t => (t.Key, t.Label)).ToList(),
            Subject = await _settings.GetAsync($"email.{type}.subject", def.DefaultSubject),
            Intro = await _settings.GetAsync($"email.{type}.intro", def.DefaultIntro),
            FromName = await _settings.GetAsync("email.from_name", "Sterlin Glams"),
            ReplyTo = await _settings.GetAsync("email.reply_to", ""),
            HeaderColor = await _settings.GetAsync("email.header_color", "#0a0a0a"),
            FooterText = await _settings.GetAsync("email.footer_text", "This is an automated message — please don't reply."),
            LogoHeight = (int)await _settings.GetDecimalAsync("email.logo_height", 72),
        };
        return View(vm);
    }

    // Full rendered email HTML for the live-preview iframe (loaded via srcdoc, not framed).
    // Optional subject/intro overrides let the preview reflect unsaved edits as the admin types.
    public async Task<IActionResult> Preview(string type, string? subject = null, string? intro = null, int? logoHeight = null)
    {
        var (s, body) = await BuildSampleAsync(type, subject, intro);
        var html = await _email.RenderAsync(s, body, logoHeight);
        return Content(html, "text/html");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(string type, string? subject, string? intro,
        string? fromName, string? replyTo, string? headerColor, string? footerText, int? logoHeight)
    {
        if (!Types.Any(t => t.Key == type))
        {
            TempData["Error"] = "Unknown email type.";
            return RedirectToAction(nameof(Index));
        }
        var h = logoHeight is int lh && lh is >= 16 and <= 300 ? lh : 72;
        await _settings.SaveManyAsync(new Dictionary<string, string>
        {
            [$"email.{type}.subject"] = subject?.Trim() ?? "",
            [$"email.{type}.intro"]   = intro?.Trim() ?? "",
            ["email.from_name"]       = string.IsNullOrWhiteSpace(fromName) ? "Sterlin Glams" : fromName.Trim(),
            ["email.reply_to"]        = replyTo?.Trim() ?? "",
            ["email.header_color"]    = string.IsNullOrWhiteSpace(headerColor) ? "#0a0a0a" : headerColor.Trim(),
            ["email.footer_text"]     = footerText?.Trim() ?? "",
            ["email.logo_height"]     = h.ToString(),
        });
        await LogAsync("Update", "Setting", null, $"Updated email template '{type}' + branding");
        TempData["Success"] = "Email saved.";
        return RedirectToAction(nameof(Index), new { type });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SendTest(string type, string email)
    {
        email = (email ?? "").Trim();
        if (string.IsNullOrWhiteSpace(email) || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email))
            return Json(new { success = false, message = "Enter a valid email address." });

        var (subject, body) = await BuildSampleAsync(type);
        var ok = await _email.SendAsync(email, "[TEST] " + subject, body);
        await LogAsync("Update", "Setting", null, $"Sent test '{type}' email to {email}");
        return Json(new { success = ok, message = ok ? $"Test sent to {email}." : "Could not send — check SMTP settings." });
    }

    // ── Sample bodies (mirror the real emails, with placeholder data) ─────────
    private async Task<(string Subject, string Body)> BuildSampleAsync(string type, string? subjectOverride = null, string? introOverride = null)
    {
        var def = Types.FirstOrDefault(t => t.Key == type);
        if (def.Key == null) { def = Types[0]; type = def.Key; }
        var subject = !string.IsNullOrWhiteSpace(subjectOverride) ? subjectOverride
            : await _settings.GetAsync($"email.{type}.subject", def.DefaultSubject);
        var intro = introOverride != null ? introOverride
            : await _settings.GetAsync($"email.{type}.intro", def.DefaultIntro);
        string E(string s) => System.Net.WebUtility.HtmlEncode(s);

        string Button(string label, string href) => $@"
            <table role=""presentation"" cellpadding=""0"" cellspacing=""0"" style=""margin:24px 0;""><tr>
                <td style=""background:#ec1c8e;border-radius:2px;""><a href=""{href}"" style=""display:inline-block;padding:12px 28px;color:#fff;font-size:13px;letter-spacing:1px;text-transform:uppercase;text-decoration:none;"">{label}</a></td>
            </tr></table>";

        if (type == "order_confirmed")
        {
            var sampleDate = DateTime.UtcNow;
            var introHtml = OrderEmailTemplate.ApplyPlaceholders(intro, "#62175", sampleDate, "Zino Idoro");
            var body0 = OrderEmailTemplate.Build(
                heading: subject,
                introHtml: introHtml,
                orderNumber: "62175",
                orderDate: sampleDate,
                items: new List<OrderEmailTemplate.Item>
                {
                    new("Pearl Dangle Loop Earrings", "Silver", 2, 15000m,
                        "https://res.cloudinary.com/dxmadm7vj/image/upload/v1781722613/sterlinglams/products/cx7jwoftvtedwpdophjh.jpg"),
                },
                subtotal: 15000m,
                shippingLabel: "PICK UP (AJAH) — NOTIFICATION WILL BE SENT WITHIN 1-2 WORKING DAYS",
                total: 15000m,
                paymentMethod: "DEBIT/CREDIT CARDS/BANK TRANSFER/USSD/OPAY",
                billingLines: new[] { "Zino Idoro", "Victoria crest 3 estate Augusta amadi chevron", "Lagos", "08163866044", "zinoidoro@gmail.com" },
                shippingLines: new[] { "Zino Idoro", "Victoria crest 3 estate Augusta amadi chevron", "Lagos" });
            return (subject, body0);
        }

        // Order-status update emails (Processing / Ready for pickup / Shipped / Delivered) — share the
        // compact order-summary layout used by the real status emails.
        if (type is "order_processing" or "ready_for_pickup" or "order_shipped" or "order_delivered")
        {
            var sampleDate = DateTime.UtcNow;
            var introHtml = OrderEmailTemplate.ApplyPlaceholders(intro, "62175", sampleDate, "Zino");
            var sampleItems = new List<OrderEmailTemplate.Item>
            {
                new("Pearl Dangle Loop Earrings", "Silver", 2, 15000m, null),
                new("2-Tone Band Ring", null, 1, 6000m, null),
            };
            string? extra = type == "ready_for_pickup"
                ? @"<div style=""text-align:center;margin:18px 0;""><img src=""https://api.qrserver.com/v1/create-qr-code/?size=180x180&data=SAMPLE"" alt=""Pickup QR code"" width=""160"" height=""160"" style=""width:160px;height:160px;"" /><br/><span style=""font:12px monospace;color:#6b7280;"">62175</span></div>"
                : null;
            var statusBody = OrderEmailTemplate.BuildStatusUpdate(subject, introHtml, "62175", sampleItems, 21000m,
                extraHtml: extra,
                buttonLabel: type == "ready_for_pickup" ? "View pickup pass" : null,
                buttonHref: type == "ready_for_pickup" ? "#" : null);
            return (subject, statusBody);
        }

        // Branch/staff emails — subject + intro carry {branch}/{order}; the rest (item list, transfer
        // reference) is filled by the real send. Sample uses placeholder values.
        if (type is "branch_transfer_request" or "branch_dispatch")
        {
            string Fill(string s) => s.Replace("{branch}", "Lekki").Replace("{order}", "62175");
            var introText = Fill(intro);
            var items = @"<li style=""padding:3px 0;"">Pearl Dangle Loop Earrings (Silver) &times; 2</li><li style=""padding:3px 0;"">2-Tone Band Ring &times; 1</li>";
            var bodyBr = type == "branch_transfer_request"
                ? $@"<h2 style=""font-size:18px;margin:0 0 12px;"">Transfer needed — order 62175</h2>
                     <p style=""color:#44403c;"">{E(introText)}</p>
                     <ul style=""color:#374151;padding-left:18px;margin:14px 0;"">{items}</ul>
                     <p style=""color:#57534e;font-size:13px;"">Transfer reference <strong>TRF-260715-101530-2</strong>. Mark it dispatched in <strong>Inventory System → Stock transfer</strong> once sent.</p>"
                : $@"<h2 style=""font-size:18px;margin:0 0 12px;"">Order 62175 ready to dispatch</h2>
                     <p style=""color:#44403c;"">{E(introText)}</p>
                     <ul style=""color:#374151;padding-left:18px;margin:14px 0;"">{items}</ul>
                     <p style=""color:#57534e;font-size:13px;"">Deliver to Lekki, Lagos.</p>";
            return (Fill(subject), bodyBr);
        }

        string body = type switch
        {
            "back_in_stock" => $@"
                <h2 style=""font-size:18px;margin:0 0 16px;"">It's back in stock</h2>
                <p>{E(intro)}</p>
                <p style=""font-size:16px;""><strong>Pearl Dangle Loop Earrings — Silver</strong></p>
                {Button("Shop now", "#")}",
            "abandoned_cart" => $@"
                <h2 style=""font-size:18px;margin:0 0 16px;"">Your bag is waiting</h2>
                <p>{E(intro)}</p>
                <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""margin:20px 0;font-size:14px;"">
                    <tr><td style=""padding:8px 0;border-bottom:1px solid #f0efed;"">2-Tone Band Ring &times; 1</td><td align=""right"" style=""padding:8px 0;border-bottom:1px solid #f0efed;"">&#8358;6,000</td></tr>
                </table>
                {Button("Return to your bag", "#")}",
            "password_reset" => $@"
                <h2 style=""font-size:18px;margin:0 0 16px;"">Reset your password</h2>
                <p>{E(intro)}</p>
                {Button("Reset password", "#")}",
            "email_confirm" => $@"
                <h2 style=""font-size:18px;margin:0 0 16px;"">Confirm your email</h2>
                <p>{E(intro)}</p>
                {Button("Confirm email", "#")}",
            _ => $"<p>{E(intro)}</p>"
        };
        return (subject, body);
    }
}
