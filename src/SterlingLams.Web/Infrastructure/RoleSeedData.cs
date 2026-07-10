using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Infrastructure;

public static class RoleSeedData
{
    // Default staff roles and the sections they can access out of the box.
    private static readonly Dictionary<string, string[]> DefaultRoles = new()
    {
        // Owner & Developer ship empty — the administrator ticks exactly what each can access.
        ["Owner"]        = Array.Empty<string>(),
        ["Developer"]    = Array.Empty<string>(),
        ["Operations"]   = new[] { "Dashboard", "Orders", "Inventory", "Stores" },
        ["Sales"]        = new[] { "Dashboard", "Orders", "Customers", "Discounts" },
        ["Inventory"]    = new[] { "Dashboard", "Products", "Inventory", "Stores", "Categories", "Attributes" },
        ["Social Media"] = new[] { "Dashboard", "Products" },
    };

    public static async Task SeedAsync(RoleManager<IdentityRole> roleManager, ApplicationDbContext db, ILogger logger)
    {
        foreach (var (roleName, sections) in DefaultRoles)
        {
            // Create the Identity role if missing
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                await roleManager.CreateAsync(new IdentityRole(roleName));
                logger.LogInformation("Created staff role: {Role}", roleName);
            }

            // Seed default section permissions only if this role has none yet
            // (so admin edits aren't overwritten on restart)
            var hasAny = await db.RolePermissions.AnyAsync(rp => rp.RoleName == roleName);
            if (!hasAny)
            {
                foreach (var section in sections)
                    db.RolePermissions.Add(new RolePermission { RoleName = roleName, Section = section });
                logger.LogInformation("Seeded default permissions for {Role}: {Sections}",
                    roleName, string.Join(", ", sections));
            }
        }

        await db.SaveChangesAsync();

        // One-time upgrade to the View/Manage model: before this feature every grant was a bare section
        // key meaning FULL access. Now a bare key means view-only and "<section>:manage" means write. To
        // keep existing roles working exactly as before, give every bare grant its :manage counterpart.
        // Self-detecting: once any granular ("x:y") permission exists we never run again, so later
        // view-only grants are respected.
        var alreadyGranular = await db.RolePermissions.AnyAsync(rp => rp.Section.Contains(":"));
        if (!alreadyGranular)
        {
            var bare = await db.RolePermissions.Where(rp => !rp.Section.Contains(":")).ToListAsync();
            var added = 0;
            foreach (var rp in bare)
            {
                if (!AdminSections.IsValidSection(rp.Section)) continue;
                var manageKey = rp.Section + ":manage";
                if (!bare.Any(x => x.RoleName == rp.RoleName && x.Section == manageKey))
                {
                    db.RolePermissions.Add(new RolePermission { RoleName = rp.RoleName, Section = manageKey });
                    added++;
                }
            }
            if (added > 0)
            {
                await db.SaveChangesAsync();
                logger.LogInformation("Upgraded {Count} role permissions to the View/Manage model.", added);
            }
        }
    }
}
