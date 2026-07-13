using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Controllers;

[Authorize]
public class WishlistController : Controller
{
    private readonly ApplicationDbContext _db;

    public WishlistController(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> Index()
    {
        var userId = GetUserId();
        var items = await _db.WishlistItems
            .Include(w => w.Product).ThenInclude(p => p.Images)
            .Include(w => w.Product).ThenInclude(p => p.StoreInventories)
            .Include(w => w.Product).ThenInclude(p => p.Category)
            .Where(w => w.UserId == userId)
            .OrderByDescending(w => w.AddedAt)
            .ToListAsync();

        return View(items);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(int productId)
    {
        var userId = GetUserId();
        var existing = await _db.WishlistItems
            .FirstOrDefaultAsync(w => w.UserId == userId && w.ProductId == productId);

        bool added;
        if (existing != null)
        {
            _db.WishlistItems.Remove(existing);
            added = false;
        }
        else
        {
            _db.WishlistItems.Add(new WishlistItem { UserId = userId, ProductId = productId });
            added = true;
        }

        await _db.SaveChangesAsync();

        var count = await _db.WishlistItems.CountAsync(w => w.UserId == userId);
        return Json(new { success = true, added, count });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove(int productId)
    {
        var userId = GetUserId();
        var item = await _db.WishlistItems
            .FirstOrDefaultAsync(w => w.UserId == userId && w.ProductId == productId);

        if (item != null)
        {
            _db.WishlistItems.Remove(item);
            await _db.SaveChangesAsync();
        }

        return RedirectToAction(nameof(Index));
    }

    private string GetUserId() => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
}
