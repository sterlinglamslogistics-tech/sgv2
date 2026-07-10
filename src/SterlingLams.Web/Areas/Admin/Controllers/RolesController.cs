using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers;

public class RolesController : AdminBaseController
{
    // Section == null → only full Administrators can manage roles (privilege-escalation guard)
    protected override string? Section => null;

    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IPermissionService _perms;
    private readonly ApplicationDbContext _db;

    // Roles that cannot be edited or deleted
    private static readonly string[] SystemRoles = { "Admin", "Customer" };

    public RolesController(
        RoleManager<IdentityRole> roleManager,
        UserManager<ApplicationUser> userManager,
        IPermissionService perms,
        ApplicationDbContext db)
    {
        _roleManager = roleManager;
        _userManager = userManager;
        _perms = perms;
        _db = db;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Roles & Permissions";

        var roles = await _roleManager.Roles.OrderBy(r => r.Name).ToListAsync();
        var rows = new List<AdminRoleRow>();

        foreach (var role in roles)
        {
            var name = role.Name ?? "";
            if (name == "Customer") continue; // not a backend role

            var usersInRole = await _userManager.GetUsersInRoleAsync(name);
            // Collapse granular grants ("Orders", "Orders:manage", "Settings:General") to distinct
            // base-section labels for the summary column.
            var sections = name == "Admin"
                ? AdminSections.All.Select(s => s.Label).ToList()
                : (await _perms.GetRoleSectionsAsync(name))
                    .Select(key => { var i = key.IndexOf(':'); return i < 0 ? key : key[..i]; })
                    .Distinct()
                    .Select(baseKey => AdminSections.All.FirstOrDefault(s => s.Key == baseKey)?.Label ?? baseKey)
                    .ToList();

            rows.Add(new AdminRoleRow
            {
                Name = name,
                IsSystem = SystemRoles.Contains(name),
                IsFullAccess = name == "Admin",
                UserCount = usersInRole.Count,
                Sections = sections,
            });
        }

        return View(new AdminRoleListViewModel { Roles = rows });
    }

    public IActionResult Create()
    {
        ViewData["Title"] = "New Role";
        return View("Edit", new AdminRoleEditViewModel { IsNew = true });
    }

    public async Task<IActionResult> Edit(string id)
    {
        ViewData["Title"] = "Edit Role";

        if (string.IsNullOrEmpty(id) || SystemRoles.Contains(id))
        {
            TempData["Error"] = "That role cannot be edited.";
            return RedirectToAction(nameof(Index));
        }

        if (!await _roleManager.RoleExistsAsync(id)) return NotFound();

        return View(new AdminRoleEditViewModel
        {
            Name = id,
            OriginalName = id,
            IsNew = false,
            SelectedSections = await _perms.GetRoleSectionsAsync(id),
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(AdminRoleEditViewModel vm, List<string> sections)
    {
        var name = vm.Name?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = "Role name is required.";
            return RedirectToAction(nameof(Create));
        }

        if (SystemRoles.Contains(name))
        {
            TempData["Error"] = "That role name is reserved.";
            return RedirectToAction(nameof(Index));
        }

        if (vm.IsNew)
        {
            if (await _roleManager.RoleExistsAsync(name))
            {
                TempData["Error"] = $"A role named '{name}' already exists.";
                return RedirectToAction(nameof(Create));
            }
            await _roleManager.CreateAsync(new IdentityRole(name));
            await LogAsync("Create", "Role", null, $"Created role '{name}'");
        }
        else if (vm.OriginalName != name)
        {
            // Rename: update the Identity role + carry permissions over
            var role = await _roleManager.FindByNameAsync(vm.OriginalName);
            if (role != null)
            {
                role.Name = name;
                await _roleManager.UpdateAsync(role);
                // Move permission rows to the new name
                var perms = await _db.RolePermissions.Where(rp => rp.RoleName == vm.OriginalName).ToListAsync();
                foreach (var p in perms) p.RoleName = name;
                await _db.SaveChangesAsync();
            }
        }

        await _perms.SetRoleSectionsAsync(name, sections ?? new List<string>());
        await LogAsync("Update", "Role", null,
            $"Set '{name}' permissions: {(sections?.Any() == true ? string.Join(", ", sections) : "none")}");

        TempData["Success"] = $"Role '{name}' saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(string id)
    {
        if (SystemRoles.Contains(id))
        {
            TempData["Error"] = "System roles cannot be deleted.";
            return RedirectToAction(nameof(Index));
        }

        var role = await _roleManager.FindByNameAsync(id);
        if (role == null) return NotFound();

        var usersInRole = await _userManager.GetUsersInRoleAsync(id);
        if (usersInRole.Any())
        {
            TempData["Error"] = $"Cannot delete '{id}' — {usersInRole.Count} user(s) still have this role. Reassign them first.";
            return RedirectToAction(nameof(Index));
        }

        // Remove permission rows then the role
        var perms = await _db.RolePermissions.Where(rp => rp.RoleName == id).ToListAsync();
        _db.RolePermissions.RemoveRange(perms);
        await _db.SaveChangesAsync();
        await _roleManager.DeleteAsync(role);
        _perms.ClearCache();

        await LogAsync("Delete", "Role", null, $"Deleted role '{id}'");
        TempData["Success"] = $"Role '{id}' deleted.";
        return RedirectToAction(nameof(Index));
    }
}
