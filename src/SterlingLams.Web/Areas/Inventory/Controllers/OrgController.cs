using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

// Moniebook "Administration": branches, registers, activity log and staff & roles. Read views
// over existing data (deep editing stays in the Website Admin) — plus cashier (POS-login) management.
public class OrgController : InventoryAreaController
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private const int PageSize = 40;
    public OrgController(ApplicationDbContext db, UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    /// <summary>Managers of the Inventory admin (staff/registers/branches). Owner is view-only here.</summary>
    private bool CanManageOrg => User.IsInRole("Admin") || User.IsInRole("Developer") || User.IsInRole("Inventory");

    // Owner is view-only in the Inventory Administration — block every write.
    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ViewData["CanManageOrg"] = CanManageOrg; // let views hide management controls from Owner
        var m = context.HttpContext.Request.Method;
        var isWrite = m == "POST" || m == "PUT" || m == "DELETE" || m == "PATCH";
        if (isWrite && !CanManageOrg)
        {
            context.Result = RedirectToAction("AccessDenied", "Account", new { area = "" });
            return;
        }
        await base.OnActionExecutionAsync(context, next);
    }

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
                            select new { u.Id, u.Email, u.FirstName, u.LastName, u.LastLoginAt, HasPin = u.PinHash != null, Role = r.Name })
                           .ToListAsync();

        var staff = joined.GroupBy(x => x.Id)
            .Select(g => new StaffRow
            {
                Id = g.Key,
                Name = (g.First().FirstName + " " + g.First().LastName).Trim(),
                Email = g.First().Email ?? "",
                Roles = g.Select(x => x.Role!).OrderBy(r => r).ToList(),
                HasPin = g.First().HasPin,
                LastLogin = g.First().LastLoginAt
            })
            .OrderBy(s => s.Name).ToList();

        ViewBag.RoleCounts = joined.GroupBy(x => x.Role!)
            .Select(g => new { Role = g.Key, Count = g.Select(x => x.Id).Distinct().Count() })
            .OrderByDescending(x => x.Count).ToDictionary(x => x.Role, x => x.Count);

        // Cashiers = users with a POS PIN (they sign into the POS app via PIN, no back-office role).
        var cashierUsers = await _db.Users.Where(u => u.PinHash != null)
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.PhoneNumber, u.LastLoginAt })
            .ToListAsync();
        var cashierIds = cashierUsers.Select(c => c.Id).ToList();
        var storeMap = await (from us in _db.UserStores
                              join s in _db.Stores on us.StoreId equals s.Id
                              where cashierIds.Contains(us.UserId)
                              select new { us.UserId, us.StoreId, s.Name }).ToListAsync();
        ViewBag.Cashiers = cashierUsers.Select(c => new CashierRow
        {
            Id = c.Id,
            Name = (c.FirstName + " " + c.LastName).Trim(),
            Phone = c.PhoneNumber,
            StoreId = storeMap.Where(m => m.UserId == c.Id).Select(m => (int?)m.StoreId).FirstOrDefault(),
            Branches = storeMap.Where(m => m.UserId == c.Id).Select(m => m.Name.Replace("Sterlin Glams ", "")).ToList(),
            LastLogin = c.LastLoginAt
        }).OrderBy(c => c.Name).ToList();
        ViewBag.Stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();

        return View(staff);
    }

    // ── Cashier (POS-login) management ────────────────────────────────────────
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddCashier(string firstName, string lastName, string? phone, string? email, int storeId, string pin)
    {
        firstName = (firstName ?? "").Trim(); lastName = (lastName ?? "").Trim();
        phone = (phone ?? "").Trim(); email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        pin = (pin ?? "").Trim();

        if (firstName.Length == 0 && lastName.Length == 0)
        { TempData["Error"] = "Enter the cashier's name."; return RedirectToAction(nameof(Staff)); }
        if (pin.Length < 4 || pin.Length > 8 || !pin.All(char.IsDigit))
        { TempData["Error"] = "PIN must be 4–8 digits."; return RedirectToAction(nameof(Staff)); }
        if (email != null && await _db.Users.AnyAsync(u => u.NormalizedEmail == _userManager.NormalizeEmail(email)))
        { TempData["Error"] = "A user with that email already exists."; return RedirectToAction(nameof(Staff)); }

        var digits = new string(phone.Where(char.IsDigit).ToArray());
        var userName = email ?? (digits.Length > 0 ? $"cashier-{digits}" : $"cashier-{Guid.NewGuid():N}");
        if (await _userManager.FindByNameAsync(userName) != null) userName = $"cashier-{Guid.NewGuid():N}";

        var user = new ApplicationUser
        {
            UserName = userName,
            Email = email,
            EmailConfirmed = email != null,
            FirstName = firstName,
            LastName = lastName,
            PhoneNumber = phone.Length > 0 ? phone : null,
            CreatedAt = DateTime.UtcNow
        };
        user.PinHash = _userManager.PasswordHasher.HashPassword(user, pin);
        var res = await _userManager.CreateAsync(user);
        if (!res.Succeeded)
        { TempData["Error"] = string.Join(" ", res.Errors.Select(e => e.Description)); return RedirectToAction(nameof(Staff)); }

        if (storeId > 0 && await _db.Stores.AnyAsync(s => s.Id == storeId))
        {
            _db.UserStores.Add(new UserStore { UserId = user.Id, StoreId = storeId });
            await _db.SaveChangesAsync();
        }
        await LogAsync("Create", "User", user.Id, $"Created cashier '{user.FullName}'");
        TempData["Success"] = $"Cashier {user.FullName} added.";
        return RedirectToAction(nameof(Staff));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetCashierPin(string id, string? pin)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user == null) return NotFound();
        pin = (pin ?? "").Trim();
        if (pin.Length == 0)
        {
            user.PinHash = null;
            await _userManager.UpdateAsync(user);
            await LogAsync("Update", "User", id, $"Removed POS access (PIN) for {user.FullName}");
            TempData["Success"] = $"POS access removed for {user.FullName}.";
            return RedirectToAction(nameof(Staff));
        }
        if (pin.Length < 4 || pin.Length > 8 || !pin.All(char.IsDigit))
        { TempData["Error"] = "PIN must be 4–8 digits."; return RedirectToAction(nameof(Staff)); }
        user.PinHash = _userManager.PasswordHasher.HashPassword(user, pin);
        await _userManager.UpdateAsync(user);
        await LogAsync("Update", "User", id, $"Set POS PIN for {user.FullName}");
        TempData["Success"] = $"PIN updated for {user.FullName}.";
        return RedirectToAction(nameof(Staff));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetCashierStore(string id, int storeId)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound();
        var existing = await _db.UserStores.Where(us => us.UserId == id).ToListAsync();
        _db.UserStores.RemoveRange(existing);
        if (storeId > 0 && await _db.Stores.AnyAsync(s => s.Id == storeId))
            _db.UserStores.Add(new UserStore { UserId = id, StoreId = storeId });
        await _db.SaveChangesAsync();
        await LogAsync("Update", "User", id, $"Set cashier branch for {user.FullName}");
        TempData["Success"] = $"Branch updated for {user.FullName}.";
        return RedirectToAction(nameof(Staff));
    }
}

public class StaffRow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public List<string> Roles { get; set; } = new();
    public bool HasPin { get; set; }
    public DateTime? LastLogin { get; set; }
}

public class CashierRow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Phone { get; set; }
    public int? StoreId { get; set; }
    public List<string> Branches { get; set; } = new();
    public DateTime? LastLogin { get; set; }
}
