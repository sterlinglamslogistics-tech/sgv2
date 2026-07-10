using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SterlingLams.Web.Areas.Admin;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

public interface IPermissionService
{
    /// <summary>True if the user can VIEW the section (has view, manage, or any sub-permission of it).</summary>
    Task<bool> CanAccessAsync(ClaimsPrincipal user, string section);

    /// <summary>True if the user can MANAGE (create/edit/delete) the section — i.e. has "<section>:manage".</summary>
    Task<bool> CanManageAsync(ClaimsPrincipal user, string section);

    /// <summary>Settings groups the user may see/edit. <c>null</c> = all groups (admin / "Settings" / "Settings:manage").</summary>
    Task<HashSet<string>?> GetAllowedSettingsGroupsAsync(ClaimsPrincipal user);

    /// <summary>All section keys the user can view (base keys, for the sidebar). Admin returns all keys.</summary>
    Task<HashSet<string>> GetAllowedSectionsAsync(ClaimsPrincipal user);

    /// <summary>Section keys granted to a single role.</summary>
    Task<HashSet<string>> GetRoleSectionsAsync(string roleName);

    /// <summary>Replaces a role's granted sections with the supplied set.</summary>
    Task SetRoleSectionsAsync(string roleName, IEnumerable<string> sections);

    void ClearCache();
}

public class PermissionService : IPermissionService
{
    private readonly ApplicationDbContext _db;
    private readonly IMemoryCache _cache;
    private const string CacheKey = "role_permissions_map";

    public PermissionService(ApplicationDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<bool> CanAccessAsync(ClaimsPrincipal user, string section)
    {
        if (user.IsInRole(AdminSections.AdminRole)) return true;   // full access
        var raw = await GetRawGrantsAsync(user);
        // View if granted the section, its :manage, or any sub-permission (e.g. a Settings group).
        return raw.Contains(section)
            || raw.Contains(section + ":manage")
            || raw.Any(g => g.StartsWith(section + ":", StringComparison.Ordinal));
    }

    public async Task<bool> CanManageAsync(ClaimsPrincipal user, string section)
    {
        if (user.IsInRole(AdminSections.AdminRole)) return true;
        var raw = await GetRawGrantsAsync(user);
        return raw.Contains(section + ":manage");
    }

    public async Task<HashSet<string>?> GetAllowedSettingsGroupsAsync(ClaimsPrincipal user)
    {
        if (user.IsInRole(AdminSections.AdminRole)) return null; // all
        var raw = await GetRawGrantsAsync(user);
        if (raw.Contains("Settings") || raw.Contains("Settings:manage")) return null; // all groups
        var groups = new HashSet<string>(StringComparer.Ordinal);
        const string prefix = "Settings:";
        foreach (var g in raw)
            if (g.StartsWith(prefix, StringComparison.Ordinal))
            {
                var grp = g[prefix.Length..];
                if (grp != "manage") groups.Add(grp);
            }
        return groups;
    }

    public async Task<HashSet<string>> GetAllowedSectionsAsync(ClaimsPrincipal user)
    {
        // Admin sees everything
        if (user.IsInRole(AdminSections.AdminRole))
            return AdminSections.All.Select(s => s.Key).ToHashSet();

        // Base section keys the user can view (strip any ":manage" / ":group" suffix).
        var raw = await GetRawGrantsAsync(user);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in raw)
        {
            var i = g.IndexOf(':');
            result.Add(i < 0 ? g : g[..i]);
        }
        return result;
    }

    /// <summary>Raw union of every permission string granted to the user's roles.</summary>
    private async Task<HashSet<string>> GetRawGrantsAsync(ClaimsPrincipal user)
    {
        var map = await GetMapAsync();
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in user.FindAll(ClaimTypes.Role).Select(c => c.Value))
            if (map.TryGetValue(role, out var sections))
                result.UnionWith(sections);
        return result;
    }

    public async Task<HashSet<string>> GetRoleSectionsAsync(string roleName)
    {
        var map = await GetMapAsync();
        return map.TryGetValue(roleName, out var s)
            ? new HashSet<string>(s, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    public async Task SetRoleSectionsAsync(string roleName, IEnumerable<string> sections)
    {
        var existing = await _db.RolePermissions.Where(rp => rp.RoleName == roleName).ToListAsync();
        _db.RolePermissions.RemoveRange(existing);

        foreach (var section in sections.Where(AdminSections.IsValidPermission).Distinct())
            _db.RolePermissions.Add(new RolePermission { RoleName = roleName, Section = section });

        await _db.SaveChangesAsync();
        ClearCache();
    }

    public void ClearCache() => _cache.Remove(CacheKey);

    /// <summary>roleName → set of section keys, cached.</summary>
    private async Task<Dictionary<string, HashSet<string>>> GetMapAsync()
    {
        if (_cache.TryGetValue(CacheKey, out Dictionary<string, HashSet<string>>? cached) && cached != null)
            return cached;

        var all = await _db.RolePermissions.ToListAsync();
        var map = all
            .GroupBy(rp => rp.RoleName)
            .ToDictionary(
                g => g.Key,
                g => g.Select(rp => rp.Section).ToHashSet(StringComparer.Ordinal));

        _cache.Set(CacheKey, map, TimeSpan.FromMinutes(10));
        return map;
    }
}
