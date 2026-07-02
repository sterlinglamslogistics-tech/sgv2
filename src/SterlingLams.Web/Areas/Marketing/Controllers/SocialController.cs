using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services.Social;

namespace SterlingLams.Web.Areas.Marketing.Controllers;

/// <summary>Social content calendar — compose + schedule posts for Instagram/Facebook/TikTok.
/// Publishing is dormant until accounts are connected (see ISocialPublisher.IsEnabled).</summary>
public class SocialController : MarketingAreaController
{
    private readonly ApplicationDbContext _db;
    private readonly ISocialPublisher _publisher;
    public SocialController(ApplicationDbContext db, ISocialPublisher publisher)
    {
        _db = db;
        _publisher = publisher;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Social";
        ViewBag.PublishingEnabled = _publisher.IsEnabled;
        var posts = await _db.SocialPosts.AsNoTracking()
            .OrderByDescending(p => p.ScheduledAt ?? p.CreatedAt)
            .Take(100).ToListAsync();
        return View(posts);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(int id, string content, string? imageUrl, SocialChannel[]? channels, DateTime? scheduledAt)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            TempData["Error"] = "Write something to post.";
            return RedirectToAction(nameof(Index));
        }

        var isNew = id == 0;
        var p = isNew ? new SocialPost() : await _db.SocialPosts.FindAsync(id);
        if (p == null) return NotFound();
        // Sent/failed posts are history — don't edit.
        if (!isNew && p.Status == SocialPostStatus.Published) return RedirectToAction(nameof(Index));

        p.Content = content.Trim();
        p.ImageUrl = string.IsNullOrWhiteSpace(imageUrl) ? null : imageUrl.Trim();
        var flags = SocialChannel.None;
        foreach (var c in channels ?? Array.Empty<SocialChannel>()) flags |= c;
        p.Channels = flags;
        p.ScheduledAt = scheduledAt.HasValue ? DateTime.SpecifyKind(scheduledAt.Value, DateTimeKind.Utc) : null;
        p.Status = p.ScheduledAt.HasValue ? SocialPostStatus.Scheduled : SocialPostStatus.Draft;
        p.Error = null;
        p.UpdatedAt = DateTime.UtcNow;
        if (isNew) { p.CreatedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier); _db.SocialPosts.Add(p); }
        await _db.SaveChangesAsync();
        await LogAsync(isNew ? "Create" : "Update", "SocialPost", p.Id.ToString(), $"{(isNew ? "Scheduled" : "Updated")} social post");
        TempData["Success"] = p.Status == SocialPostStatus.Scheduled ? "Post scheduled." : "Draft saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var p = await _db.SocialPosts.FindAsync(id);
        if (p != null)
        {
            _db.SocialPosts.Remove(p);
            await _db.SaveChangesAsync();
            await LogAsync("Delete", "SocialPost", id.ToString(), "Deleted social post");
            TempData["Success"] = "Post deleted.";
        }
        return RedirectToAction(nameof(Index));
    }
}
