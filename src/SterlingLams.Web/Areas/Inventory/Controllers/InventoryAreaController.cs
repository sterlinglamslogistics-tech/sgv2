using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

/// <summary>
/// Base for the dedicated Inventory System (its own /Inventory area + layout).
/// Restricted to the Inventory team and full Administrators. This is a self-contained
/// workspace (stock, transfers, till, stock-take) separate from the website admin.
/// </summary>
[Area("Inventory")]
[Authorize(Roles = "Admin,Inventory")]
public abstract class InventoryAreaController : Controller
{
    /// <summary>Staff members for "Staff member" pickers — users in any backend role (i.e. NOT just
    /// a Customer), excluding guest shells. Keeps customers out of staff dropdowns project-wide.
    /// Returns anonymous { id, name } objects (consumed as dynamic in views / serialized to JSON).</summary>
    protected async Task<List<object>> StaffOptionsAsync()
    {
        var db = HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
        var customerRoleId = await db.Roles.Where(r => r.Name == "Customer").Select(r => r.Id).FirstOrDefaultAsync();
        var rows = await db.Users
            .Where(u => !u.IsGuest && db.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId != customerRoleId))
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email })
            .ToListAsync();
        return rows.Select(u => (object)new
        {
            id = u.Id,
            name = ($"{u.FirstName} {u.LastName}").Trim() != "" ? $"{u.FirstName} {u.LastName}".Trim() : u.Email
        }).ToList();
    }

    /// <summary>True when the user is a real staff member (backend role, not a customer/guest).</summary>
    protected async Task<bool> IsStaffAsync(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        var db = HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
        var customerRoleId = await db.Roles.Where(r => r.Name == "Customer").Select(r => r.Id).FirstOrDefaultAsync();
        return await db.Users.AnyAsync(u => u.Id == userId && !u.IsGuest
            && db.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId != customerRoleId));
    }

    /// <summary>Display name (full name, else email) for a staff member id.</summary>
    protected async Task<string?> StaffNameAsync(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;
        var db = HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
        return await db.Users.Where(u => u.Id == userId)
            .Select(u => ($"{u.FirstName} {u.LastName}").Trim() != "" ? ($"{u.FirstName} {u.LastName}").Trim() : u.Email)
            .FirstOrDefaultAsync();
    }

    /// <summary>Records an action to the audit log. Best-effort — never throws.</summary>
    protected async Task LogAsync(string action, string entityType, string? entityId, string description)
    {
        try
        {
            var audit = HttpContext.RequestServices.GetRequiredService<IAuditService>();
            await audit.LogAsync(action, entityType, entityId, description);
        }
        catch { /* auditing must never break the operation */ }
    }

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var db = HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
        ViewData["PendingTransfersCount"] = await db.StockTransfers.CountAsync(
            t => t.Status == TransferStatus.PendingApproval || t.Status == TransferStatus.InTransit);

        await next();
    }
}
