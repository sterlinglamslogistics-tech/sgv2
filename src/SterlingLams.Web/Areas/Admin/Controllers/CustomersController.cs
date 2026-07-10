using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    public class CustomersController : AdminBaseController
    {
        protected override string Section => "Customers";

        private readonly ApplicationDbContext _db;
        private readonly ILoyaltyService _loyalty;
        private readonly UserManager<ApplicationUser> _userManager;
        private const int PageSize = 30;

        public CustomersController(ApplicationDbContext db, ILoyaltyService loyalty,
            UserManager<ApplicationUser> userManager)
        {
            _db = db;
            _loyalty = loyalty;
            _userManager = userManager;
        }

        // ── Create a storefront-only customer account ─────────────────────────
        [HttpGet]
        public IActionResult Create()
        {
            ViewData["Title"] = "New Customer";
            return View();
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(string firstName, string lastName, string email, string? phone, string password)
        {
            ViewData["Title"] = "New Customer";
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                TempData["Error"] = "Email and password are required.";
                return View();
            }
            if (await _userManager.FindByEmailAsync(email.Trim()) != null)
            {
                TempData["Error"] = "A user with that email already exists.";
                return View();
            }

            // Storefront-only: no backend role is assigned, so they can shop but never reach the backend.
            var user = new ApplicationUser
            {
                UserName = email.Trim(),
                Email = email.Trim(),
                FirstName = (firstName ?? "").Trim(),
                LastName = (lastName ?? "").Trim(),
                PhoneNumber = phone?.Trim(),
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
            };
            var result = await _userManager.CreateAsync(user, password);
            if (!result.Succeeded)
            {
                TempData["Error"] = string.Join(" ", result.Errors.Select(e => e.Description));
                return View();
            }
            await LogAsync("Create", "Customer", user.Id, $"Created customer account {user.Email} (storefront only)");
            TempData["Success"] = $"Customer {user.Email} created — storefront access only.";
            return RedirectToAction(nameof(Index));
        }

        // ── Delete a customer (admin-only; never orphans order history) ───────
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id)
        {
            if (!User.IsInRole("Admin")) return Forbid();

            var user = await _userManager.FindByIdAsync(id);
            if (user == null) return NotFound();
            if (user.Email == User.Identity?.Name)
            {
                TempData["Error"] = "You cannot delete your own account.";
                return RedirectToAction(nameof(Index));
            }
            // Only ever delete storefront customers here, never a staff/admin account.
            var roles = await _userManager.GetRolesAsync(user);
            if (roles.Any())
            {
                TempData["Error"] = "This is a staff account — manage it on the Users page.";
                return RedirectToAction(nameof(Index));
            }
            var orders = await _db.Orders.CountAsync(o => o.UserId == id || o.CustomerUserId == id);
            if (orders > 0)
            {
                TempData["Error"] = $"{user.Email} has {orders} order(s) on record — deleting would remove their history. Consider keeping the account.";
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
                await LogAsync("Delete", "Customer", id, $"Deleted customer {user.Email}");
                TempData["Success"] = $"{user.Email} has been deleted.";
            }
            catch
            {
                TempData["Error"] = "Could not delete this customer — they're still linked to other records.";
            }
            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Index(string q = "", string segment = "", string tag = "", int page = 1)
        {
            ViewData["Title"] = "Customers";

            var query = _db.Users.AsQueryable();

            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(u =>
                    EF.Functions.ILike(u.FirstName + " " + u.LastName, $"%{q}%") ||
                    EF.Functions.ILike(u.Email!, $"%{q}%"));

            if (!string.IsNullOrWhiteSpace(tag))
                query = query.Where(u => u.Tags != null && EF.Functions.ILike(u.Tags, $"%{tag}%"));

            // Segment filters mirror the derived badges on AdminCustomerRow.
            var lapsedCutoff = DateTime.UtcNow.AddDays(-CustomerSegments.LapsedDays);
            query = segment switch
            {
                "vip" => query.Where(u => (u.Orders.Where(o => o.IsPaid).Sum(o => (decimal?)o.Total) ?? 0) >= CustomerSegments.VipSpend),
                "repeat" => query.Where(u => u.Orders.Count >= 2),
                "lapsed" => query.Where(u => u.Orders.Any() && u.Orders.Max(o => o.CreatedAt) < lapsedCutoff),
                "new" => query.Where(u => u.Orders.Count <= 1),
                _ => query
            };

            var total = await query.CountAsync();

            var customers = await query
                .OrderByDescending(u => u.CreatedAt)
                .Skip((page - 1) * PageSize)
                .Take(PageSize)
                .Select(u => new AdminCustomerRow
                {
                    Id = u.Id,
                    FullName = u.FirstName + " " + u.LastName,
                    Email = u.Email ?? "",
                    Phone = u.PhoneNumber,
                    OrderCount = u.Orders.Count,
                    TotalSpend = u.Orders.Where(o => o.IsPaid).Sum(o => (decimal?)o.Total) ?? 0,
                    JoinedAt = u.CreatedAt,
                    LastOrderAt = u.Orders.Any() ? u.Orders.Max(o => (DateTime?)o.CreatedAt) : null,
                    Tags = u.Tags
                })
                .ToListAsync();

            var vm = new AdminCustomerListViewModel
            {
                Customers = customers,
                SearchQuery = q,
                SegmentFilter = segment,
                TagFilter = tag,
                CurrentPage = page,
                TotalPages = (int)Math.Ceiling(total / (double)PageSize)
            };

            return View(vm);
        }

        public async Task<IActionResult> Detail(string id)
        {
            ViewData["Title"] = "Customer";

            var user = await _db.Users.FindAsync(id);
            if (user == null) return NotFound();

            var orders = await _db.Orders
                .Where(o => o.UserId == id)
                .OrderByDescending(o => o.CreatedAt)
                .Take(10)
                .Select(o => new RecentOrderRow
                {
                    OrderNumber = o.OrderNumber,
                    CustomerName = user.FirstName + " " + user.LastName,
                    Total = o.Total,
                    Status = o.Status.ToString(),
                    CreatedAt = o.CreatedAt
                })
                .ToListAsync();

            var vm = new AdminCustomerDetailViewModel
            {
                Id = user.Id,
                FullName = user.FullName,
                Email = user.Email ?? "",
                Phone = user.PhoneNumber,
                JoinedAt = user.CreatedAt,
                OrderCount = await _db.Orders.CountAsync(o => o.UserId == id),
                TotalSpend = await _db.Orders
                    .Where(o => o.UserId == id && o.IsPaid)
                    .SumAsync(o => (decimal?)o.Total) ?? 0,
                RecentOrders = orders,
                Tags = user.Tags,
                LoyaltyBalance = await _loyalty.GetBalanceAsync(id),
                LoyaltyEntries = await _db.PointsLedgerEntries
                    .Where(p => p.Account.UserId == id)
                    .OrderByDescending(p => p.Id).Take(15).ToListAsync()
            };

            return View(vm);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> AdjustLoyalty(string id, int points, string? reason)
        {
            var user = await _db.Users.FindAsync(id);
            if (user == null) return NotFound();
            if (points == 0)
            {
                TempData["Error"] = "Enter a non-zero number of points.";
                return RedirectToAction(nameof(Detail), new { id });
            }
            var balance = await _loyalty.AdjustAsync(id, points, reason ?? "Manual adjustment");
            await LogAsync("LoyaltyAdjust", "Customer", id,
                $"Adjusted {user.FullName}'s points by {(points > 0 ? "+" : "")}{points} — new balance {balance}{(string.IsNullOrWhiteSpace(reason) ? "" : $" ({reason!.Trim()})")}");
            TempData["Success"] = $"Points adjusted. New balance: {balance}.";
            return RedirectToAction(nameof(Detail), new { id });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveTags(string id, string? tags)
        {
            var user = await _db.Users.FindAsync(id);
            if (user == null) return NotFound();
            // Normalise: trim, drop blanks, de-dupe (case-insensitive), comma-join.
            var cleaned = string.Join(", ", (tags ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            user.Tags = cleaned.Length == 0 ? null : cleaned;
            await _db.SaveChangesAsync();
            await LogAsync("Update", "Customer", id, $"Updated tags for {user.FullName}: {user.Tags ?? "(none)"}");
            TempData["Success"] = "Tags saved.";
            return RedirectToAction(nameof(Detail), new { id });
        }

        public async Task<IActionResult> ExportCsv(string q = "")
        {
            var query = _db.Users.AsQueryable();
            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(u =>
                    EF.Functions.ILike(u.FirstName + " " + u.LastName, $"%{q}%") ||
                    EF.Functions.ILike(u.Email!, $"%{q}%"));

            var customers = await query
                .OrderByDescending(u => u.CreatedAt)
                .Select(u => new
                {
                    FullName  = u.FirstName + " " + u.LastName,
                    u.Email,
                    Phone     = u.PhoneNumber ?? "",
                    Orders    = u.Orders.Count,
                    TotalSpend = u.Orders.Where(o => o.IsPaid).Sum(o => (decimal?)o.Total) ?? 0,
                    Joined    = u.CreatedAt.ToString("yyyy-MM-dd")
                })
                .ToListAsync();

            var sb = new StringBuilder();
            sb.AppendLine("Full Name,Email,Phone,Orders,Total Spend,Joined");
            foreach (var c in customers)
                sb.AppendLine($"\"{c.FullName}\",\"{c.Email}\",\"{c.Phone}\",{c.Orders},{c.TotalSpend},\"{c.Joined}\"");

            await LogAsync("Export", "Customer", null, $"Exported {customers.Count} customer record(s) to CSV");

            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return File(bytes, "text/csv", $"customers_{DateTime.UtcNow:yyyyMMdd}.csv");
        }
    }
}
