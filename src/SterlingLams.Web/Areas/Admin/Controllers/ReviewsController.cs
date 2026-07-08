using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Areas.Admin.Controllers;

/// <summary>Moderate product reviews — approve, hide, reply, delete. Full administrators only.</summary>
public class ReviewsController : AdminBaseController
{
    protected override string? Section => "Reviews";

    private readonly ApplicationDbContext _db;
    public ReviewsController(ApplicationDbContext db) => _db = db;

    public async Task<IActionResult> Index(string filter = "pending", int page = 1)
    {
        const int size = 30;
        ViewData["Title"] = "Reviews";
        var q = _db.ProductReviews.Include(r => r.Product).AsQueryable();
        if (filter == "pending") q = q.Where(r => !r.IsApproved);
        else if (filter == "approved") q = q.Where(r => r.IsApproved);

        var total = await q.CountAsync();
        var rows = await q.OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * size).Take(size)
            .Select(r => new ReviewRow
            {
                Id = r.Id, ProductName = r.Product.Name, ProductSlug = r.Product.Slug,
                Author = r.AuthorName, Rating = r.Rating, Title = r.Title, Body = r.Body,
                IsApproved = r.IsApproved, IsVerifiedBuyer = r.IsVerifiedBuyer,
                AdminReply = r.AdminReply, CreatedAt = r.CreatedAt
            })
            .ToListAsync();

        ViewBag.Filter = filter; ViewBag.Page = page;
        ViewBag.TotalPages = (int)Math.Ceiling(total / (double)size); ViewBag.Total = total;
        ViewBag.PendingCount = await _db.ProductReviews.CountAsync(r => !r.IsApproved);
        return View(rows);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetApproved(int id, bool approved, string? filter)
    {
        var r = await _db.ProductReviews.FindAsync(id);
        if (r != null)
        {
            r.IsApproved = approved;
            await _db.SaveChangesAsync();
            await LogAsync("Update", "Review", id.ToString(), $"{(approved ? "Approved" : "Hid")} review #{id}");
        }
        return RedirectToAction(nameof(Index), new { filter });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reply(int id, string? reply, string? filter)
    {
        var r = await _db.ProductReviews.FindAsync(id);
        if (r != null)
        {
            r.AdminReply = string.IsNullOrWhiteSpace(reply) ? null : reply.Trim();
            r.RepliedAt = r.AdminReply == null ? null : DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await LogAsync("Update", "Review", id.ToString(), $"Replied to review #{id}");
        }
        return RedirectToAction(nameof(Index), new { filter });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, string? filter)
    {
        var r = await _db.ProductReviews.FindAsync(id);
        if (r != null)
        {
            _db.ProductReviews.Remove(r);
            await _db.SaveChangesAsync();
            await LogAsync("Delete", "Review", id.ToString(), $"Deleted review #{id}");
        }
        return RedirectToAction(nameof(Index), new { filter });
    }

    public class ReviewRow
    {
        public int Id { get; set; }
        public string ProductName { get; set; } = "";
        public string ProductSlug { get; set; } = "";
        public string Author { get; set; } = "";
        public int Rating { get; set; }
        public string? Title { get; set; }
        public string Body { get; set; } = "";
        public bool IsApproved { get; set; }
        public bool IsVerifiedBuyer { get; set; }
        public string? AdminReply { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
