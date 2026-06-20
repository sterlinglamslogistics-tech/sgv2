using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

// Moniebook "Administration": branches, registers, activity log and staff & roles. Read views
// over existing data (deep editing stays in the Website Admin).
public class OrgController : InventoryAreaController
{
    private readonly ApplicationDbContext _db;
    private const int PageSize = 40;
    public OrgController(ApplicationDbContext db) => _db = db;

    public async Task<IActionResult> Branches()
    {
        ViewData["Title"] = "Branches";
        var stores = await _db.Stores.OrderBy(s => s.Name).ToListAsync();
        var ids = stores.Select(s => s.Id).ToList();
        var units = await _db.StoreInventories.Where(si => ids.Contains(si.StoreId))
            .GroupBy(si => si.StoreId).Select(g => new { Id = g.Key, Units = g.Sum(x => x.QuantityOnHand) })
            .ToDictionaryAsync(x => x.Id, x => x.Units);
        var registers = await _db.Registers.Where(r => ids.Contains(r.StoreId))
            .GroupBy(r => r.StoreId).Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count);
        ViewBag.Units = units; ViewBag.Registers = registers;
        return View(stores);
    }

    public async Task<IActionResult> Registers()
    {
        ViewData["Title"] = "Registers";
        ViewBag.Stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        var regs = await _db.Registers.Include(r => r.Store).OrderBy(r => r.Store.Name).ThenBy(r => r.Name).ToListAsync();
        ViewBag.OpenSessions = (await _db.TillSessions.Where(s => s.ClosedAt == null).Select(s => s.RegisterId).ToListAsync()).ToHashSet();
        return View(regs);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRegister(string name, int storeId)
    {
        if (string.IsNullOrWhiteSpace(name) || !await _db.Stores.AnyAsync(s => s.Id == storeId))
        {
            TempData["Error"] = "Please enter a register name and choose a branch.";
            return RedirectToAction(nameof(Registers));
        }
        _db.Registers.Add(new Register { Name = name.Trim(), StoreId = storeId, IsActive = true });
        await _db.SaveChangesAsync();
        await LogAsync("Create", "Register", null, $"Added register '{name.Trim()}'");
        TempData["Success"] = "Register added.";
        return RedirectToAction(nameof(Registers));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RenameRegister(int id, string name)
    {
        var r = await _db.Registers.FindAsync(id);
        if (r == null) return NotFound();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = "Enter a register name.";
            return RedirectToAction(nameof(Registers));
        }
        var old = r.Name;
        r.Name = name.Trim();
        await _db.SaveChangesAsync();
        await LogAsync("Update", "Register", id.ToString(), $"Renamed register '{old}' → '{r.Name}'");
        TempData["Success"] = "Register renamed.";
        return RedirectToAction(nameof(Registers));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleRegister(int id)
    {
        var r = await _db.Registers.FindAsync(id);
        if (r == null) return NotFound();
        r.IsActive = !r.IsActive;
        await _db.SaveChangesAsync();
        await LogAsync("Update", "Register", id.ToString(), $"{(r.IsActive ? "Enabled" : "Disabled")} register '{r.Name}'");
        return RedirectToAction(nameof(Registers));
    }

    public async Task<IActionResult> ActivityLog(string? act = null, string q = "", int page = 1)
    {
        ViewData["Title"] = "Activity log";
        var query = _db.AuditLogs.AsQueryable();
        if (!string.IsNullOrWhiteSpace(act)) query = query.Where(a => a.Action == act);
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(a => EF.Functions.ILike(a.Description, $"%{q}%")
                                  || EF.Functions.ILike(a.PerformedBy, $"%{q}%")
                                  || EF.Functions.ILike(a.EntityType, $"%{q}%"));

        var total = await query.CountAsync();
        var rows = await query.OrderByDescending(a => a.Id)
            .Skip((page - 1) * PageSize).Take(PageSize).ToListAsync();

        ViewBag.Actions = await _db.AuditLogs.Select(a => a.Action).Distinct().OrderBy(a => a).ToListAsync();
        ViewBag.Action = act; ViewBag.Query = q; ViewBag.Page = page;
        ViewBag.TotalPages = (int)Math.Ceiling(total / (double)PageSize); ViewBag.Total = total;
        return View(rows);
    }

    public async Task<IActionResult> Staff()
    {
        ViewData["Title"] = "Staff & roles";
        // Staff = users holding at least one role (via the Identity join tables).
        var joined = await (from ur in _db.UserRoles
                            join r in _db.Roles on ur.RoleId equals r.Id
                            join u in _db.Users on ur.UserId equals u.Id
                            select new { u.Id, u.Email, u.FirstName, u.LastName, u.LastLoginAt, Role = r.Name })
                           .ToListAsync();

        var staff = joined.GroupBy(x => x.Id)
            .Select(g => new StaffRow
            {
                Name = (g.First().FirstName + " " + g.First().LastName).Trim(),
                Email = g.First().Email ?? "",
                Roles = g.Select(x => x.Role!).OrderBy(r => r).ToList(),
                LastLogin = g.First().LastLoginAt
            })
            .OrderBy(s => s.Name).ToList();

        ViewBag.RoleCounts = joined.GroupBy(x => x.Role!)
            .Select(g => new { Role = g.Key, Count = g.Select(x => x.Id).Distinct().Count() })
            .OrderByDescending(x => x.Count).ToDictionary(x => x.Role, x => x.Count);
        return View(staff);
    }
}

public class StaffRow
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public List<string> Roles { get; set; } = new();
    public DateTime? LastLogin { get; set; }
}
