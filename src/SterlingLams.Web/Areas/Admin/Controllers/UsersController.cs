using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    public class UsersController : AdminBaseController
    {
        // Section == null → full administrators only. User & role management is owner-only.
        protected override string? Section => null;

        // Owner is view-only here: only Admin + Developer may create/edit users or change roles.
        public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var m = context.HttpContext.Request.Method;
            var isWrite = m == "POST" || m == "PUT" || m == "DELETE" || m == "PATCH";
            if (isWrite && !AdminSections.IsSystemManager(User))
            {
                context.Result = RedirectToAction("AccessDenied", "Account", new { area = "" });
                return;
            }
            await base.OnActionExecutionAsync(context, next);
        }

        private readonly ApplicationDbContext _db;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private const int PageSize = 30;

        public UsersController(ApplicationDbContext db, UserManager<ApplicationUser> userManager,
            RoleManager<IdentityRole> roleManager)
        {
            _db = db;
            _userManager = userManager;
            _roleManager = roleManager;
        }

        // Determines a user's single display role (first backend role, else "Customer")
        private static string PrimaryRole(IList<string> roles) =>
            roles.FirstOrDefault(r => r != "Customer") ?? "Customer";

        public async Task<IActionResult> Index(string q = "", string role = "", string status = "", int page = 1)
        {
            ViewData["Title"] = "User Management";

            var adminIds = (await _userManager.GetUsersInRoleAsync("Admin")).Select(u => u.Id).ToHashSet();

            // Backend (staff) roles — everything except the implicit Customer role.
            var staffRoles = await _roleManager.Roles
                .Where(r => r.Name != "Customer")
                .OrderBy(r => r.Name == "Admin" ? 0 : 1).ThenBy(r => r.Name)
                .Select(r => r.Name!)
                .ToListAsync();

            // This screen lists STAFF & ADMINS only — customers live in the Customers tab. Build the
            // set of everyone holding any backend role and restrict the whole page to them.
            var staffIds = new HashSet<string>();
            foreach (var r in staffRoles)
                foreach (var u in await _userManager.GetUsersInRoleAsync(r))
                    staffIds.Add(u.Id);

            var query = _db.Users.Where(u => staffIds.Contains(u.Id));

            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(u =>
                    EF.Functions.ILike(u.FirstName + " " + u.LastName, $"%{q}%") ||
                    EF.Functions.ILike(u.Email!, $"%{q}%") ||
                    EF.Functions.ILike(u.PhoneNumber ?? "", $"%{q}%"));

            // Role filter narrows within staff (e.g. only "Sales" or only "Admin").
            if (!string.IsNullOrWhiteSpace(role))
            {
                var inRole = (await _userManager.GetUsersInRoleAsync(role)).Select(u => u.Id).ToHashSet();
                query = query.Where(u => inRole.Contains(u.Id));
            }

            var now = DateTimeOffset.UtcNow;
            if (status == "locked")
                query = query.Where(u => u.LockoutEnd != null && u.LockoutEnd > now);
            else if (status == "active")
                query = query.Where(u => u.LockoutEnd == null || u.LockoutEnd <= now);

            var total = await query.CountAsync();

            var pageUsers = await query
                .OrderByDescending(u => u.CreatedAt)
                .Skip((page - 1) * PageSize)
                .Take(PageSize)
                .ToListAsync();

            // Order counts + spend per user (one grouped query)
            var pageUserIds = pageUsers.Select(u => u.Id).ToList();
            var orderStats = await _db.Orders
                .Where(o => pageUserIds.Contains(o.UserId))
                .GroupBy(o => o.UserId)
                .Select(g => new
                {
                    UserId = g.Key,
                    Count  = g.Count(),
                    Spend  = g.Where(o => o.IsPaid).Sum(o => (decimal?)o.Total) ?? 0
                })
                .ToListAsync();

            var rows = new List<AdminUserRow>();
            foreach (var u in pageUsers)
            {
                var stat = orderStats.FirstOrDefault(s => s.UserId == u.Id);
                var userRoles = await _userManager.GetRolesAsync(u);
                rows.Add(new AdminUserRow
                {
                    Id             = u.Id,
                    FullName       = u.FullName,
                    Email          = u.Email ?? "",
                    Phone          = u.PhoneNumber,
                    RoleName       = PrimaryRole(userRoles),
                    IsAdmin        = adminIds.Contains(u.Id),
                    IsLocked       = u.LockoutEnd.HasValue && u.LockoutEnd > now,
                    IsRevoked      = u.AccessRevoked,
                    EmailConfirmed = u.EmailConfirmed,
                    OrderCount     = stat?.Count ?? 0,
                    TotalSpend     = stat?.Spend ?? 0,
                    JoinedAt       = u.CreatedAt,
                    LastLoginAt    = u.LastLoginAt,
                });
            }

            // Stat cards (whole-table aggregates)
            var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var vm = new AdminUserListViewModel
            {
                Users          = rows,
                SearchQuery    = q,
                RoleFilter     = role,
                StatusFilter   = status,
                AvailableRoles = staffRoles,
                // Assignable per user: staff roles (never Admin) + Customer (removes backend access).
                AssignableRoles = staffRoles.Where(r => r != "Admin").Append("Customer").ToList(),
                CurrentPage    = page,
                TotalPages     = (int)Math.Ceiling(total / (double)PageSize),
                TotalCount     = total,
                TotalUsers     = staffIds.Count,
                AdminCount     = adminIds.Count,
                CustomerCount  = staffIds.Count - adminIds.Count,   // repurposed → non-admin staff ("Staff" card)
                LockedCount    = await _db.Users.CountAsync(u => u.LockoutEnd != null && u.LockoutEnd > now && staffIds.Contains(u.Id)),
                NewThisMonth   = await _db.Users.CountAsync(u => u.CreatedAt >= monthStart && staffIds.Contains(u.Id)),
            };

            return View(vm);
        }

        // ── Create new staff/admin user ────────────────────────────────────────
        // Staff roles a new user can be given (never Admin — full access isn't grantable here).
        private static string[] StaffRoles => SterlingLams.Web.Areas.Admin.AdminSections.DefaultStaffRoles;

        [HttpGet]
        public IActionResult Create()
        {
            ViewData["Title"] = "New User";
            ViewBag.Roles = StaffRoles;
            return View(new AdminCreateUserViewModel { Role = StaffRoles.First() });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(AdminCreateUserViewModel vm)
        {
            ViewData["Title"] = "New User";
            ViewBag.Roles = StaffRoles;

            if (string.IsNullOrWhiteSpace(vm.Email) || string.IsNullOrWhiteSpace(vm.Password))
            {
                TempData["Error"] = "Email and password are required.";
                return View(vm);
            }

            if (await _userManager.FindByEmailAsync(vm.Email) != null)
            {
                TempData["Error"] = "A user with that email already exists.";
                return View(vm);
            }

            var user = new ApplicationUser
            {
                UserName       = vm.Email.Trim(),
                Email          = vm.Email.Trim(),
                FirstName      = vm.FirstName.Trim(),
                LastName       = vm.LastName.Trim(),
                PhoneNumber    = vm.Phone?.Trim(),
                EmailConfirmed = true,
                CreatedAt      = DateTime.UtcNow,
            };

            var result = await _userManager.CreateAsync(user, vm.Password);
            if (!result.Succeeded)
            {
                TempData["Error"] = string.Join(" ", result.Errors.Select(e => e.Description));
                return View(vm);
            }

            // Every created user is staff: give them a backend role (which also lets them shop the
            // storefront). Full "Admin" is never assignable here.
            var role = StaffRoles.Contains(vm.Role) ? vm.Role : StaffRoles.First();
            await _userManager.AddToRoleAsync(user, role);

            await LogAsync("Create", "User", user.Id, $"Created staff account {user.Email} ({role})");
            TempData["Success"] = $"User {user.Email} created as {role}. They can sign in to the backend and the storefront.";
            return RedirectToAction(nameof(Index));
        }

        // ── Reset password (admin sets a new one) ──────────────────────────────
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(string id, string newPassword)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
            {
                TempData["Error"] = "New password must be at least 8 characters.";
                return RedirectToAction(nameof(Index));
            }

            var token  = await _userManager.GeneratePasswordResetTokenAsync(user);
            var result = await _userManager.ResetPasswordAsync(user, token, newPassword);

            if (result.Succeeded)
            {
                await LogAsync("Update", "User", user.Id, $"Reset password for {user.Email}");
                TempData["Success"] = $"Password reset for {user.Email}.";
            }
            else
            {
                TempData["Error"] = string.Join(" ", result.Errors.Select(e => e.Description));
            }

            return RedirectToAction(nameof(Index));
        }

        // ── Edit a user's details (name, email, optional new password) ─────────
        [HttpGet]
        public async Task<IActionResult> Edit(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();
            if (user.Email == User.Identity?.Name)
                return RedirectToAction("Index", "MyAccount", new { area = "" }); // edit your own on /me
            ViewData["Title"] = "Edit User";
            ViewBag.Role = (await _userManager.GetRolesAsync(user)).FirstOrDefault(r => r != "Customer") ?? "Customer";
            return View(user);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, string firstName, string lastName, string email, string? newPassword)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();
            if (user.Email == User.Identity?.Name)
            {
                TempData["Error"] = "Edit your own details from the account menu.";
                return RedirectToAction(nameof(Index));
            }

            email = (email ?? "").Trim();
            if (string.IsNullOrWhiteSpace(email))
            {
                TempData["Error"] = "Email is required.";
                return RedirectToAction(nameof(Edit), new { id });
            }

            // Email = username; keep them in sync (Identity re-normalises). Block duplicates.
            if (!string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase))
            {
                var dupe = await _userManager.FindByEmailAsync(email);
                if (dupe != null && dupe.Id != user.Id)
                {
                    TempData["Error"] = "Another account already uses that email.";
                    return RedirectToAction(nameof(Edit), new { id });
                }
                var r1 = await _userManager.SetUserNameAsync(user, email);
                var r2 = await _userManager.SetEmailAsync(user, email);
                if (!r1.Succeeded || !r2.Succeeded)
                {
                    TempData["Error"] = string.Join(" ", r1.Errors.Concat(r2.Errors).Select(e => e.Description));
                    return RedirectToAction(nameof(Edit), new { id });
                }
                user.EmailConfirmed = true;
            }

            user.FirstName = (firstName ?? "").Trim();
            user.LastName = (lastName ?? "").Trim();
            await _userManager.UpdateAsync(user);

            if (!string.IsNullOrWhiteSpace(newPassword))
            {
                if (newPassword.Length < 8)
                {
                    TempData["Error"] = "Name/email saved, but the new password must be at least 8 characters.";
                    return RedirectToAction(nameof(Edit), new { id });
                }
                var token = await _userManager.GeneratePasswordResetTokenAsync(user);
                var pr = await _userManager.ResetPasswordAsync(user, token, newPassword);
                if (!pr.Succeeded)
                {
                    TempData["Error"] = "Name/email saved, but password change failed: " + string.Join(" ", pr.Errors.Select(e => e.Description));
                    return RedirectToAction(nameof(Edit), new { id });
                }
            }

            await LogAsync("Update", "User", user.Id, $"Edited details for {user.Email}");
            TempData["Success"] = $"{user.Email} updated.";
            return RedirectToAction(nameof(Index));
        }

        // ── Set / clear a till PIN for this user ───────────────────────────────
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SetPin(string id, string pin)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            pin = (pin ?? "").Trim();
            if (pin.Length == 0)
            {
                user.PinHash = null; // clear — user can no longer sign in at a till
                await _userManager.UpdateAsync(user);
                await LogAsync("Update", "User", user.Id, $"Cleared till PIN for {user.Email}");
                TempData["Success"] = $"Till PIN removed for {user.Email}.";
                return RedirectToAction(nameof(Index));
            }
            if (pin.Length < 4 || pin.Length > 8 || !pin.All(char.IsDigit))
            {
                TempData["Error"] = "PIN must be 4–8 digits.";
                return RedirectToAction(nameof(Index));
            }

            user.PinHash = _userManager.PasswordHasher.HashPassword(user, pin);
            await _userManager.UpdateAsync(user);
            await LogAsync("Update", "User", user.Id, $"Set till PIN for {user.Email}");
            TempData["Success"] = $"Till PIN set for {user.Email}.";
            return RedirectToAction(nameof(Index));
        }

        // ── CSV export ─────────────────────────────────────────────────────────
        public async Task<IActionResult> ExportCsv()
        {
            var adminIds = (await _userManager.GetUsersInRoleAsync("Admin")).Select(u => u.Id).ToHashSet();
            var now = DateTimeOffset.UtcNow;

            var users = await _db.Users.OrderByDescending(u => u.CreatedAt).ToListAsync();

            var sb = new StringBuilder();
            sb.AppendLine("Full Name,Email,Phone,Role,Status,Joined,Last Login");
            foreach (var u in users)
            {
                var rl = adminIds.Contains(u.Id) ? "Admin" : "Customer";
                var st = u.LockoutEnd.HasValue && u.LockoutEnd > now ? "Locked" : "Active";
                sb.AppendLine(string.Join(",",
                    $"\"{u.FullName}\"", $"\"{u.Email}\"", $"\"{u.PhoneNumber}\"",
                    rl, st, $"\"{u.CreatedAt:yyyy-MM-dd}\"",
                    $"\"{(u.LastLoginAt.HasValue ? u.LastLoginAt.Value.ToString("yyyy-MM-dd HH:mm") : "Never")}\""));
            }

            await LogAsync("Export", "User", null, $"Exported {users.Count} user(s) to CSV");

            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return File(bytes, "text/csv", $"users_{DateTime.UtcNow:yyyyMMdd}.csv");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SetRole(string id, string role)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            if (user.Email == User.Identity?.Name)
            {
                TempData["Error"] = "You cannot change your own role.";
                return RedirectToAction(nameof(Index));
            }

            role = (role ?? "Customer").Trim();

            // Full admin access can't be granted here.
            if (role == "Admin")
            {
                TempData["Error"] = "Full administrator access can't be granted from this list.";
                return RedirectToAction(nameof(Index));
            }

            // Validate the target role exists (Customer = no backend role)
            if (role != "Customer" && !await _roleManager.RoleExistsAsync(role))
            {
                TempData["Error"] = $"Role '{role}' does not exist.";
                return RedirectToAction(nameof(Index));
            }

            // Replace all current roles with the chosen one (Customer = none).
            var current = await _userManager.GetRolesAsync(user);
            if (current.Any())
                await _userManager.RemoveFromRolesAsync(user, current);

            if (role != "Customer")
                await _userManager.AddToRoleAsync(user, role);

            // Invalidate any live session so the user immediately loses access to the previous role
            // and must sign in again under the new one.
            await _userManager.UpdateSecurityStampAsync(user);

            await LogAsync("Update", "User", user.Id, $"Set role of {user.Email} to {role}");
            TempData["Success"] = $"{user.Email} is now {role}. Any active session has been signed out.";
            return RedirectToAction(nameof(Index));
        }

        // ── Revoke / restore access ───────────────────────────────────────────
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Revoke(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();
            if (user.Email == User.Identity?.Name)
            {
                TempData["Error"] = "You cannot revoke your own access.";
                return RedirectToAction(nameof(Index));
            }
            user.AccessRevoked = true;
            await _userManager.UpdateAsync(user);
            await _userManager.UpdateSecurityStampAsync(user); // kicks out any live session
            await LogAsync("Update", "User", user.Id, $"Revoked access for {user.Email}");
            TempData["Success"] = $"{user.Email}'s access has been revoked — they can no longer sign in.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Restore(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();
            user.AccessRevoked = false;
            await _userManager.UpdateAsync(user);
            await LogAsync("Update", "User", user.Id, $"Restored access for {user.Email}");
            TempData["Success"] = $"{user.Email}'s access has been restored.";
            return RedirectToAction(nameof(Index));
        }

        // ── Delete a user (admin-only; safe — never orphans order history) ────────
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            if (user.Email == User.Identity?.Name)
            {
                TempData["Error"] = "You cannot delete your own account.";
                return RedirectToAction(nameof(Index));
            }
            if (await _userManager.IsInRoleAsync(user, "Admin"))
            {
                TempData["Error"] = "Administrator accounts cannot be deleted.";
                return RedirectToAction(nameof(Index));
            }

            // Never orphan order history — block deletion and suggest revoking instead.
            var orders = await _db.Orders.CountAsync(o => o.UserId == id || o.CustomerUserId == id);
            if (orders > 0)
            {
                TempData["Error"] = $"{user.Email} has {orders} order(s) on record — revoke their access instead of deleting, to keep the history.";
                return RedirectToAction(nameof(Index));
            }

            try
            {
                var result = await _userManager.DeleteAsync(user);
                if (!result.Succeeded)
                {
                    TempData["Error"] = "Could not delete: " + string.Join("; ", result.Errors.Select(e => e.Description));
                    return RedirectToAction(nameof(Index));
                }
                await LogAsync("Delete", "User", id, $"Deleted user {user.Email}");
                TempData["Success"] = $"{user.Email} has been deleted.";
            }
            catch
            {
                TempData["Error"] = "Could not delete this account — it's still linked to other records. Revoke their access instead.";
            }
            return RedirectToAction(nameof(Index));
        }

        // ── Store-level access (writes-only) ──────────────────────────────────
        [HttpGet]
        public async Task<IActionResult> Stores(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();
            ViewBag.User = user;
            ViewBag.AllStores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
            ViewBag.Assigned = (await _db.UserStores.Where(us => us.UserId == id)
                .Select(us => us.StoreId).ToListAsync()).ToHashSet();
            return View();
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SetStores(string id, int[] storeIds)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            var validStoreIds = (await _db.Stores.Where(s => s.IsActive).Select(s => s.Id).ToListAsync()).ToHashSet();
            var desired = (storeIds ?? Array.Empty<int>()).Where(validStoreIds.Contains).Distinct().ToList();

            var existing = await _db.UserStores.Where(us => us.UserId == id).ToListAsync();
            _db.UserStores.RemoveRange(existing);
            foreach (var sid in desired)
                _db.UserStores.Add(new UserStore { UserId = id, StoreId = sid });
            await _db.SaveChangesAsync();

            await LogAsync("Update", "User", id, desired.Count == 0
                ? $"Cleared branch access for {user.Email} (now unrestricted — all branches)"
                : $"Set branch access for {user.Email}: {desired.Count} branch(es)");

            TempData["Success"] = "Branch access updated.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleLock(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();

            if (user.Email == User.Identity?.Name)
            {
                TempData["Error"] = "You cannot lock your own account.";
                return RedirectToAction(nameof(Index));
            }

            if (user.LockoutEnd.HasValue && user.LockoutEnd > DateTimeOffset.UtcNow)
            {
                await _userManager.SetLockoutEndDateAsync(user, null);
                await LogAsync("Update", "User", user.Id, $"Unlocked account {user.Email}");
                TempData["Success"] = $"{user.Email} account unlocked.";
            }
            else
            {
                await _userManager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddYears(100));
                await LogAsync("Update", "User", user.Id, $"Locked account {user.Email}");
                TempData["Success"] = $"{user.Email} account locked.";
            }

            return RedirectToAction(nameof(Index));
        }
    }
}
