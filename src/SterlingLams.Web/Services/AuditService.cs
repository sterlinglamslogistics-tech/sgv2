using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

public interface IAuditService
{
    /// <summary>Records an admin action with the current user, IP, and timestamp.</summary>
    Task LogAsync(string action, string entityType, string? entityId, string description);
}

public class AuditService : IAuditService
{
    private readonly ApplicationDbContext _db;
    private readonly IHttpContextAccessor _http;

    public AuditService(ApplicationDbContext db, IHttpContextAccessor http)
    {
        _db = db;
        _http = http;
    }

    public async Task LogAsync(string action, string entityType, string? entityId, string description)
    {
        var ctx  = _http.HttpContext;
        var user = await ResolvePerformerAsync(ctx);
        var ip   = GetClientIp(ctx);

        _db.AuditLogs.Add(new AuditLog
        {
            Action      = action,
            EntityType  = entityType,
            EntityId    = entityId ?? "",
            Description = description.Length > 1000 ? description[..1000] : description,
            PerformedBy = user,
            IpAddress   = ip,
            CreatedAt   = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync();
    }

    /// <summary>The staff member's display name (First Last) when signed in, else their username,
    /// else "system" (background jobs / unauthenticated).</summary>
    private async Task<string> ResolvePerformerAsync(HttpContext? ctx)
    {
        var id = ctx?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrEmpty(id))
        {
            var u = await _db.Users.Where(x => x.Id == id)
                .Select(x => new { x.FirstName, x.LastName, x.UserName })
                .FirstOrDefaultAsync();
            if (u != null)
            {
                var name = $"{u.FirstName} {u.LastName}".Trim();
                return string.IsNullOrWhiteSpace(name) ? (u.UserName ?? "unknown") : name;
            }
        }
        return ctx?.User?.Identity?.Name ?? "system";
    }

    private static string? GetClientIp(HttpContext? ctx)
    {
        if (ctx == null) return null;

        // Respect reverse-proxy forwarded header (Frappe Cloud / nginx) if present
        var fwd = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(fwd))
            return fwd.Split(',')[0].Trim();

        return ctx.Connection.RemoteIpAddress?.ToString();
    }
}
