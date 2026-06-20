namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Lightweight order-attribution tracker: on storefront page views it records the visit's origin
/// (Direct / referring host / utm_source) once per session and counts how many pages were viewed.
/// Checkout snapshots these onto the order (WooCommerce-style "Order attribution").
/// Skips staff areas, the API, non-GET and non-HTML requests.
/// </summary>
public class OrderAttributionMiddleware
{
    private readonly RequestDelegate _next;
    public OrderAttributionMiddleware(RequestDelegate next) => _next = next;

    public const string OriginKey = "oa_origin";
    public const string PageViewsKey = "oa_pv";

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (IsTrackable(ctx.Request))
        {
            try
            {
                var session = ctx.Session;
                // Origin: set once, on the first trackable request of the session.
                if (string.IsNullOrEmpty(session.GetString(OriginKey)))
                    session.SetString(OriginKey, ResolveOrigin(ctx.Request));
                // Page-view counter.
                session.SetInt32(PageViewsKey, (session.GetInt32(PageViewsKey) ?? 0) + 1);
            }
            catch { /* tracking must never break a page render */ }
        }

        await _next(ctx);
    }

    private static bool IsTrackable(HttpRequest req)
    {
        if (!HttpMethods.IsGet(req.Method)) return false;
        var p = req.Path;
        if (p.StartsWithSegments("/Admin") || p.StartsWithSegments("/Inventory")
            || p.StartsWithSegments("/Pos") || p.StartsWithSegments("/Till")
            || p.StartsWithSegments("/api")) return false;
        // Skip static assets (have a file extension, e.g. .css/.js/.png).
        var last = p.Value is { Length: > 0 } v ? v[(v.LastIndexOf('/') + 1)..] : "";
        if (last.Contains('.')) return false;
        return true;
    }

    private static string ResolveOrigin(HttpRequest req)
    {
        var utm = req.Query["utm_source"].ToString();
        if (!string.IsNullOrWhiteSpace(utm)) return utm.Trim();

        var referer = req.Headers["Referer"].ToString();
        if (!string.IsNullOrWhiteSpace(referer)
            && Uri.TryCreate(referer, UriKind.Absolute, out var u)
            && !u.Host.Equals(req.Host.Host, StringComparison.OrdinalIgnoreCase))
            return u.Host;

        return "Direct";
    }

    /// <summary>Mobile / Tablet / Desktop from a user-agent string.</summary>
    public static string DeviceFromUserAgent(string? ua)
    {
        ua = (ua ?? "").ToLowerInvariant();
        if (ua.Contains("ipad") || ua.Contains("tablet")) return "Tablet";
        if (ua.Contains("mobi") || ua.Contains("iphone") || ua.Contains("android")) return "Mobile";
        return string.IsNullOrWhiteSpace(ua) ? "Unknown" : "Desktop";
    }
}
