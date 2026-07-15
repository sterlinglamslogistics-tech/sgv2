using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// When the <c>store.maintenance_mode</c> setting is on, shows an elegant maintenance page to the
/// PUBLIC STOREFRONT only. Staff keep full access: the admin/inventory/marketing/POS areas, auth,
/// webhooks and static assets stay reachable, so the shop can be managed while the front is down.
/// Health probes (<c>/health</c>) also stay 200 so the host never pulls the instance (see IsExempt).
/// </summary>
public class MaintenanceModeMiddleware
{
    private readonly RequestDelegate _next;
    public MaintenanceModeMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx, ISettingsService settings, ApplicationDbContext db)
    {
        var path = ctx.Request.Path.Value ?? string.Empty;

        if (IsExempt(path) || IsStaff(ctx.User) || !await settings.GetBoolAsync("store.maintenance_mode", false))
        {
            await _next(ctx);
            return;
        }

        var siteName = await settings.GetAsync("general.site_name", "Sterlin Glams");
        var tagline = await settings.GetAsync("general.tagline", "");
        var email = await settings.GetAsync("general.contact_email", "");
        var logoUrl = await settings.GetAsync("general.logo_url", "");
        // Prefer the site's WhatsApp number for the contact line; fall back to the plain contact phone.
        var whatsapp = await settings.GetAsync("general.whatsapp_number", "");
        var phone = await settings.GetAsync("general.contact_phone", "");

        // Physical stores stay open while the online shop is down — show visitors where to reach us.
        // Wrapped so a DB hiccup never turns the maintenance page itself into an error.
        List<Store> stores;
        try
        {
            stores = await db.Stores.AsNoTracking()
                .Where(s => s.IsActive)
                .OrderBy(s => s.Name)
                .ToListAsync();
        }
        catch
        {
            stores = new List<Store>();
        }

        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        ctx.Response.Headers.RetryAfter = "3600";
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(Page(siteName, tagline, email, whatsapp, phone, logoUrl, stores));
    }

    // Areas/paths that must keep working so staff can manage the shop + the page can render.
    private static bool IsExempt(string path)
    {
        bool Starts(string p) => path.StartsWith(p, StringComparison.OrdinalIgnoreCase);
        return Starts($"/{StaffPaths.Admin}") || Starts($"/{StaffPaths.Inventory}") || Starts($"/{StaffPaths.Marketing}") || Starts("/Till") || Starts("/Pos")
            || Starts("/Account/Login") || Starts("/Account/Logout") || Starts("/Account/AccessDenied")
            || Starts("/webhooks")
            // Health probes MUST stay 200 in maintenance mode, or the host (Render) marks the instance
            // unhealthy and pulls it — turning a maintenance page into a full 502 outage.
            || Starts("/health")
            || Starts("/css") || Starts("/js") || Starts("/lib") || Starts("/uploads")
            || Starts("/favicon");
    }

    private static bool IsStaff(ClaimsPrincipal user)
    {
        if (user?.Identity?.IsAuthenticated != true) return false;
        if (user.IsInRole(AdminSections.AdminRole)) return true;
        return AdminSections.DefaultStaffRoles.Any(user.IsInRole);
    }

    private static string Page(string siteName, string tagline, string email, string whatsapp, string phone, string logoUrl, List<Store> stores)
    {
        static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
        // tel: links must be digits/+ only
        static string TelHref(string s) => new string((s ?? string.Empty).Where(c => char.IsDigit(c) || c == '+').ToArray());

        var name = Enc(string.IsNullOrWhiteSpace(siteName) ? "Sterlin Glams" : siteName);

        // ── Logo / wordmark at the top ───────────────────────────────────────
        // Use the uploaded logo image when set (general.logo_url); otherwise fall back to a large
        // serif wordmark of the site name.
        var topBrand = string.IsNullOrWhiteSpace(logoUrl)
            ? $@"<div class=""wordmark"">{name}</div>"
            : $@"<img class=""logo"" src=""{Enc(Img.Cld(logoUrl, 400) ?? logoUrl)}"" alt=""{name}"" />";

        // ── Contact row (email + WhatsApp/phone) ─────────────────────────────
        var contact = new StringBuilder();
        var hasWa = !string.IsNullOrWhiteSpace(whatsapp);
        var number = hasWa ? whatsapp : phone;
        if (!string.IsNullOrWhiteSpace(email) || !string.IsNullOrWhiteSpace(number))
        {
            contact.Append(@"<div class=""contact"">");
            if (!string.IsNullOrWhiteSpace(email))
                contact.Append($@"<a href=""mailto:{Enc(email)}"">{Enc(email)}</a>");
            if (!string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(number))
                contact.Append(@"<span class=""dot"">&middot;</span>");
            if (!string.IsNullOrWhiteSpace(number))
            {
                // WhatsApp number → click-to-chat (wa.me, digits only, same format as the footer);
                // a plain contact phone stays a tel: link.
                var href = hasWa
                    ? "https://wa.me/" + new string(number.Where(char.IsDigit).ToArray())
                    : "tel:" + TelHref(number);
                var extra = hasWa ? @" target=""_blank"" rel=""noopener""" : "";
                contact.Append($@"<a href=""{href}""{extra}>{Enc(number)}</a>");
            }
            contact.Append(@"</div>");
        }

        // ── Stores block ─────────────────────────────────────────────────────
        var storesHtml = new StringBuilder();
        if (stores.Count > 0)
        {
            storesHtml.Append(@"<div class=""stores-wrap"">
              <div class=""sub"">Our stores remain open &mdash; do visit us</div>
              <div class=""stores"">");
            foreach (var s in stores)
            {
                var loc = string.Join(", ", new[] { s.City, s.State }.Where(x => !string.IsNullOrWhiteSpace(x)));
                // Skip the city/state line if the full address already ends with it (avoids "Gwarimpa, Abuja" twice).
                var addr = s.Address ?? string.Empty;
                var addrHasLoc = !string.IsNullOrWhiteSpace(s.State) && addr.Contains(s.State, StringComparison.OrdinalIgnoreCase);
                storesHtml.Append(@"<div class=""store"">");
                storesHtml.Append($@"<div class=""store-name"">{Enc(s.Name)}</div>");
                if (!string.IsNullOrWhiteSpace(addr))
                    storesHtml.Append($@"<div class=""store-line"">{Enc(addr)}</div>");
                if (!string.IsNullOrWhiteSpace(loc) && !addrHasLoc)
                    storesHtml.Append($@"<div class=""store-line"">{Enc(loc)}</div>");
                if (!string.IsNullOrWhiteSpace(s.Phone))
                    storesHtml.Append($@"<div class=""store-line""><a href=""tel:{Enc(TelHref(s.Phone))}"">{Enc(s.Phone)}</a></div>");
                storesHtml.Append(@"</div>");
            }
            storesHtml.Append(@"</div></div>");
        }

        var taglineHtml = string.IsNullOrWhiteSpace(tagline)
            ? ""
            : $@"<div class=""tagline"">{Enc(tagline)}</div>";

        return $@"<!doctype html>
<html lang=""en""><head><meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>{name} &mdash; Back soon</title>
<style>
  *{{box-sizing:border-box}}
  body{{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;
       font-family:Georgia,'Times New Roman',serif;background:#0a0a0a;color:#f5f5f4;
       text-align:center;padding:48px 24px;
       background-image:radial-gradient(circle at 50% 0%,rgba(200,164,92,.10),transparent 60%);}}
  .wrap{{max-width:640px;width:100%}}
  .logo{{height:104px;width:auto;max-width:300px;object-fit:contain;display:block;margin:0 auto 22px}}
  .wordmark{{font-size:36px;letter-spacing:5px;text-transform:uppercase;color:#f5f5f4;margin-bottom:14px;font-weight:400}}
  .tagline{{font-size:12px;letter-spacing:2px;text-transform:uppercase;color:#78716c;margin-bottom:40px}}
  .rule{{width:48px;height:1px;background:#c8a45c;margin:28px auto;opacity:.7}}
  h1{{font-weight:300;font-size:38px;letter-spacing:1px;margin:0 0 18px;line-height:1.2}}
  p.lead{{color:#a8a29e;font-size:16px;line-height:1.8;margin:0 auto;max-width:460px}}
  .contact{{margin-top:22px;font-size:15px}}
  .contact a{{color:#e7d9bb;text-decoration:none;border-bottom:1px solid rgba(200,164,92,.35);padding-bottom:1px}}
  .contact a:hover{{color:#fff;border-color:#c8a45c}}
  .contact .dot{{color:#57534e;margin:0 12px}}
  .sub{{font-size:12px;letter-spacing:3px;text-transform:uppercase;color:#c8a45c;margin-bottom:22px}}
  .stores{{display:grid;grid-template-columns:repeat(auto-fit,minmax(200px,1fr));gap:20px;text-align:left}}
  .store{{border:1px solid rgba(255,255,255,.08);border-radius:2px;padding:18px 20px;background:rgba(255,255,255,.02)}}
  .store-name{{font-size:15px;color:#f5f5f4;margin-bottom:8px;letter-spacing:.5px}}
  .store-line{{font-size:13px;color:#a8a29e;line-height:1.6}}
  .store-line a{{color:#c8a45c;text-decoration:none}}
  .store-line a:hover{{color:#e7d9bb}}
  .foot{{margin-top:44px;font-size:11px;letter-spacing:1px;color:#57534e}}
  @media(max-width:520px){{h1{{font-size:30px}} .logo{{height:80px}} .wordmark{{font-size:28px;letter-spacing:3px}}}}
</style></head>
<body><div class=""wrap"">
  {topBrand}
  {taglineHtml}
  <h1>We&rsquo;ll be right back</h1>
  <p class=""lead"">Our online store is closed for a short while as we make a few refinements.
     Please check back soon &mdash; thank you for your patience.</p>
  {contact}
  <div class=""rule""></div>
  {storesHtml}
  <div class=""foot"">&copy; {DateTime.UtcNow:yyyy} {name}. All rights reserved.</div>
</div></body></html>";
    }
}
