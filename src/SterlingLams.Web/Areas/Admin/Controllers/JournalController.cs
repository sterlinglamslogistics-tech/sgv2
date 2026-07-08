using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers;

/// <summary>Manage Journal (blog / lookbook) posts. Content tool — full administrators only.</summary>
public class JournalController : AdminBaseController
{
    protected override string? Section => "Journal";

    private readonly ApplicationDbContext _db;
    public JournalController(ApplicationDbContext db) => _db = db;

    public async Task<IActionResult> Index(string filter = "all")
    {
        ViewData["Title"] = "Journal";
        var q = _db.BlogPosts.AsNoTracking().AsQueryable();
        if (filter == "published") q = q.Where(b => b.IsPublished);
        else if (filter == "draft") q = q.Where(b => !b.IsPublished);

        var posts = await q
            .OrderByDescending(b => b.PublishedAt ?? b.UpdatedAt)
            .ToListAsync();

        ViewBag.Filter = filter;
        ViewBag.PublishedCount = await _db.BlogPosts.CountAsync(b => b.IsPublished);
        ViewBag.DraftCount = await _db.BlogPosts.CountAsync(b => !b.IsPublished);
        return View(posts);
    }

    public IActionResult Create()
    {
        ViewData["Title"] = "New Journal Post";
        return View("Edit", new BlogPost());
    }

    public async Task<IActionResult> Edit(int id)
    {
        var post = await _db.BlogPosts.FindAsync(id);
        if (post == null) return NotFound();
        ViewData["Title"] = "Edit Journal Post";
        return View(post);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(BlogPost vm)
    {
        if (string.IsNullOrWhiteSpace(vm.Title))
        {
            TempData["Error"] = "A title is required.";
            return View("Edit", vm);
        }

        var isNew = vm.Id == 0;
        var post = isNew ? new BlogPost() : await _db.BlogPosts.FindAsync(vm.Id);
        if (post == null) return NotFound();

        post.Title = vm.Title.Trim();
        post.Slug = await UniqueSlugAsync(string.IsNullOrWhiteSpace(vm.Slug) ? Slugify(vm.Title) : Slugify(vm.Slug), post.Id);
        post.Excerpt = string.IsNullOrWhiteSpace(vm.Excerpt) ? null : vm.Excerpt.Trim();
        post.Body = ProductHtml.Sanitize(vm.Body ?? string.Empty);
        post.CoverImageUrl = string.IsNullOrWhiteSpace(vm.CoverImageUrl) ? null : vm.CoverImageUrl.Trim();
        post.AuthorName = string.IsNullOrWhiteSpace(vm.AuthorName) ? null : vm.AuthorName.Trim();
        post.MetaTitle = string.IsNullOrWhiteSpace(vm.MetaTitle) ? null : vm.MetaTitle.Trim();
        post.MetaDescription = string.IsNullOrWhiteSpace(vm.MetaDescription) ? null : vm.MetaDescription.Trim();
        post.UpdatedAt = DateTime.UtcNow;

        // Stamp PublishedAt the first time it goes live; keep it on later edits.
        if (vm.IsPublished && !post.IsPublished) post.PublishedAt = DateTime.UtcNow;
        post.IsPublished = vm.IsPublished;

        if (isNew) { post.CreatedAt = DateTime.UtcNow; _db.BlogPosts.Add(post); }
        await _db.SaveChangesAsync();

        await LogAsync(isNew ? "Create" : "Update", "BlogPost", post.Id.ToString(),
            $"{(isNew ? "Created" : "Updated")} journal post '{post.Title}'{(post.IsPublished ? " (published)" : " (draft)")}");
        TempData["Success"] = $"Post '{post.Title}' saved{(post.IsPublished ? " and published" : " as a draft")}.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> TogglePublish(int id)
    {
        var post = await _db.BlogPosts.FindAsync(id);
        if (post != null)
        {
            post.IsPublished = !post.IsPublished;
            if (post.IsPublished && post.PublishedAt == null) post.PublishedAt = DateTime.UtcNow;
            post.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await LogAsync("Update", "BlogPost", id.ToString(), $"{(post.IsPublished ? "Published" : "Unpublished")} '{post.Title}'");
            TempData["Success"] = post.IsPublished ? "Post published." : "Post moved to drafts.";
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var post = await _db.BlogPosts.FindAsync(id);
        if (post != null)
        {
            _db.BlogPosts.Remove(post);
            await _db.SaveChangesAsync();
            await LogAsync("Delete", "BlogPost", id.ToString(), $"Deleted journal post '{post.Title}'");
            TempData["Success"] = "Post deleted.";
        }
        return RedirectToAction(nameof(Index));
    }

    private static string Slugify(string s) =>
        Regex.Replace(s.ToLowerInvariant().Trim(), @"[^a-z0-9]+", "-").Trim('-');

    private async Task<string> UniqueSlugAsync(string baseSlug, int selfId)
    {
        if (string.IsNullOrWhiteSpace(baseSlug)) baseSlug = "post";
        var slug = baseSlug;
        var n = 2;
        while (await _db.BlogPosts.AnyAsync(b => b.Slug == slug && b.Id != selfId))
            slug = $"{baseSlug}-{n++}";
        return slug;
    }
}
