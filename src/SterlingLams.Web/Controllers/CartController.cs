using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Models.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace SterlingLams.Web.Controllers;

public class CartController : Controller
{
    private const string CartSessionKey = "cart";
    private readonly ApplicationDbContext _db;
    private readonly IDiscountService _discounts;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IStockService _stock;

    public CartController(ApplicationDbContext db, IDiscountService discounts,
        UserManager<ApplicationUser> userManager, IStockService stock)
    {
        _db = db;
        _discounts = discounts;
        _userManager = userManager;
        _stock = stock;
    }

    public async Task<IActionResult> Index()
    {
        var cart = GetCart();
        await ApplyAutomaticDiscountAsync(cart);
        return View(cart);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(int productId, int quantity = 1, int? variantId = null)
    {
        var product = await _db.Products
            .Include(p => p.Images)
            .Include(p => p.Variants)
            .FirstOrDefaultAsync(p => p.Id == productId && p.IsActive);

        if (product == null)
            return Json(new { success = false, message = "Product not found." });

        // Combined stock across all active branches is the ceiling (online fulfilment can
        // pull from any branch). StoreInventory is per-product, same as the POS ledger.
        var available = await CombinedAvailableAsync(productId, variantId);
        if (available <= 0)
            return Json(new { success = false, message = "This item is out of stock." });

        var cart = GetCart();
        var existing = cart.Items.FirstOrDefault(i => i.ProductId == productId && i.VariantId == variantId);

        if (existing != null)
        {
            existing.MaxQuantity = available;
            existing.Quantity = Math.Min(existing.Quantity + quantity, available);
        }
        else
        {
            var variant = variantId.HasValue ? product.Variants.FirstOrDefault(v => v.Id == variantId) : null;
            cart.Items.Add(new CartItemViewModel
            {
                ProductId = product.Id,
                VariantId = variantId,
                ProductName = product.Name,
                VariantName = variant?.Name,
                Slug = product.Slug,
                ImageUrl = product.Images.FirstOrDefault(i => i.IsPrimary)?.Url ?? "/images/placeholder.jpg",
                UnitPrice = product.EffectivePrice + (variant?.PriceAdjustment ?? 0),
                Quantity = Math.Min(Math.Max(1, quantity), available),
                MaxQuantity = available
            });
        }

        SaveCart(cart);

        return Json(new
        {
            success = true,
            cartCount = cart.TotalItems,
            subtotal = cart.FormattedSubtotal
        });
    }

    // Re-add every still-available item from a past order to the bag (one-click reorder).
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reorder(int orderId)
    {
        var userId = _userManager.GetUserId(User);
        if (userId == null) return Challenge();

        var order = await _db.Orders.Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId);
        if (order == null) return NotFound();

        var cart = GetCart();
        int added = 0, skipped = 0;
        foreach (var it in order.Items)
        {
            var product = await _db.Products.Include(p => p.Images).Include(p => p.Variants)
                .FirstOrDefaultAsync(p => p.Id == it.ProductId && p.IsActive);
            var available = product == null ? 0 : await CombinedAvailableAsync(it.ProductId, it.ProductVariantId);
            if (product == null || available <= 0) { skipped++; continue; }

            var qty = Math.Max(1, it.Quantity);
            var existing = cart.Items.FirstOrDefault(c => c.ProductId == it.ProductId && c.VariantId == it.ProductVariantId);
            if (existing != null)
            {
                existing.MaxQuantity = available;
                existing.Quantity = Math.Min(existing.Quantity + qty, available);
            }
            else
            {
                var variant = it.ProductVariantId.HasValue ? product.Variants.FirstOrDefault(v => v.Id == it.ProductVariantId) : null;
                cart.Items.Add(new CartItemViewModel
                {
                    ProductId = product.Id,
                    VariantId = it.ProductVariantId,
                    ProductName = product.Name,
                    VariantName = variant?.Name,
                    Slug = product.Slug,
                    ImageUrl = product.Images.FirstOrDefault(i => i.IsPrimary)?.Url ?? "/images/placeholder.jpg",
                    UnitPrice = product.EffectivePrice + (variant?.PriceAdjustment ?? 0),
                    Quantity = Math.Min(qty, available),
                    MaxQuantity = available
                });
            }
            added++;
        }
        SaveCart(cart);

        TempData["CartMessage"] = added > 0
            ? $"{added} item(s) from {order.OrderNumber} added to your bag." + (skipped > 0 ? $" {skipped} item(s) are no longer available and were skipped." : "")
            : "Sorry, none of the items from that order are available right now.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateQuantity(int productId, int quantity, int? variantId = null)
    {
        var cart = GetCart();
        var item = cart.Items.FirstOrDefault(i => i.ProductId == productId && i.VariantId == variantId);

        if (item != null)
        {
            if (quantity <= 0)
                cart.Items.Remove(item);
            else
            {
                item.MaxQuantity = await CombinedAvailableAsync(productId, variantId); // refresh against live stock (per variant)
                item.Quantity = Math.Min(quantity, Math.Max(1, item.MaxQuantity));
            }
        }

        SaveCart(cart);
        return Json(new { success = true, cartCount = cart.TotalItems, subtotal = cart.FormattedSubtotal });
    }

    /// <summary>Combined AVAILABLE stock (on-hand minus reservations held by unpaid orders) across
    /// all active branches for a specific (product, variant) — the orderable ceiling. Uses the
    /// stock service's per-variant resolution (variant row if stocked, else the product pool).</summary>
    private async Task<int> CombinedAvailableAsync(int productId, int? variantId)
    {
        var storeIds = await _db.Stores.Where(s => s.IsActive).Select(s => s.Id).ToListAsync();
        var total = 0;
        foreach (var sid in storeIds)
            total += await _stock.GetAvailableAsync(productId, variantId, sid);
        return total;
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Remove(int productId, int? variantId = null)
    {
        var cart = GetCart();
        var item = cart.Items.FirstOrDefault(i => i.ProductId == productId && i.VariantId == variantId);
        if (item != null) cart.Items.Remove(item);
        SaveCart(cart);
        return Json(new { success = true, cartCount = cart.TotalItems });
    }

    // ── Save for later ────────────────────────────────────────────────────────
    // Moves a line out of the bag into the saved list (kept in the session cart, not checked out).

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult SaveForLater(int productId, int? variantId = null)
    {
        var cart = GetCart();
        var item = cart.Items.FirstOrDefault(i => i.ProductId == productId && i.VariantId == variantId);
        if (item != null)
        {
            cart.Items.Remove(item);
            if (!cart.SavedItems.Any(i => i.ProductId == productId && i.VariantId == variantId))
                cart.SavedItems.Add(item);
        }
        SaveCart(cart);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveToBag(int productId, int? variantId = null)
    {
        var cart = GetCart();
        var saved = cart.SavedItems.FirstOrDefault(i => i.ProductId == productId && i.VariantId == variantId);
        if (saved != null)
        {
            cart.SavedItems.Remove(saved);

            // Re-check live availability before putting it back in the bag.
            var available = await CombinedAvailableAsync(productId, variantId);
            if (available <= 0)
            {
                TempData["Error"] = $"\"{saved.ProductName}\" is out of stock.";
                SaveCart(cart);
                return RedirectToAction(nameof(Index));
            }

            var existing = cart.Items.FirstOrDefault(i => i.ProductId == productId && i.VariantId == variantId);
            if (existing != null)
            {
                existing.MaxQuantity = available;
                existing.Quantity = Math.Min(existing.Quantity + saved.Quantity, available);
            }
            else
            {
                saved.MaxQuantity = available;
                saved.Quantity = Math.Min(Math.Max(1, saved.Quantity), available);
                cart.Items.Add(saved);
            }
        }
        SaveCart(cart);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult RemoveSaved(int productId, int? variantId = null)
    {
        var cart = GetCart();
        var saved = cart.SavedItems.FirstOrDefault(i => i.ProductId == productId && i.VariantId == variantId);
        if (saved != null) cart.SavedItems.Remove(saved);
        SaveCart(cart);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyDiscount(string code)
    {
        var cart = GetCart();
        var userId = _userManager.GetUserId(User);

        var result = await _discounts.EvaluateAsync(code, cart, userId);
        if (!result.Success)
            return Json(new { success = false, message = result.Error });

        cart.AppliedDiscountCode  = result.Code;
        cart.DiscountDescription  = result.Description;
        cart.DiscountAmount       = result.Amount;
        cart.FreeShipping         = result.FreeShipping;
        cart.IsAutomaticDiscount  = false;
        SaveCart(cart);

        return Json(new
        {
            success = true,
            code = cart.AppliedDiscountCode,
            description = cart.DiscountDescription,
            discount = cart.FormattedDiscount,
            freeShipping = cart.FreeShipping,
            total = cart.FormattedTotal
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult RemoveDiscount()
    {
        var cart = GetCart();
        cart.AppliedDiscountCode = null;
        cart.DiscountDescription = null;
        cart.DiscountAmount = 0;
        cart.FreeShipping = false;
        cart.IsAutomaticDiscount = false;
        SaveCart(cart);
        return Json(new { success = true, total = cart.FormattedTotal });
    }

    /// <summary>
    /// Applies the best automatic (no-code) promotion if the customer hasn't already
    /// applied a manual code. Re-evaluates each load so it stays correct as the cart changes.
    /// </summary>
    private async Task ApplyAutomaticDiscountAsync(CartViewModel cart)
    {
        if (cart.IsEmpty) return;

        // Don't override a manually-applied code
        if (!string.IsNullOrEmpty(cart.AppliedDiscountCode) && !cart.IsAutomaticDiscount)
            return;

        var userId = _userManager.GetUserId(User);
        var auto = await _discounts.FindAutomaticAsync(cart, userId);

        if (auto != null)
        {
            cart.AppliedDiscountCode = auto.Code;
            cart.DiscountDescription = auto.Description;
            cart.DiscountAmount      = auto.Amount;
            cart.FreeShipping        = auto.FreeShipping;
            cart.IsAutomaticDiscount = true;
            SaveCart(cart);
        }
        else if (cart.IsAutomaticDiscount)
        {
            // A previously auto-applied promo no longer qualifies — clear it
            cart.AppliedDiscountCode = null;
            cart.DiscountDescription = null;
            cart.DiscountAmount = 0;
            cart.FreeShipping = false;
            cart.IsAutomaticDiscount = false;
            SaveCart(cart);
        }
    }

    // Partial for mini-cart in nav dropdown
    public IActionResult MiniCart()
    {
        return PartialView("_MiniCart", GetCart());
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private CartViewModel GetCart()
    {
        var json = HttpContext.Session.GetString(CartSessionKey);
        if (string.IsNullOrEmpty(json)) return new CartViewModel();
        return JsonSerializer.Deserialize<CartViewModel>(json) ?? new CartViewModel();
    }

    private void SaveCart(CartViewModel cart)
    {
        HttpContext.Session.SetString(CartSessionKey, JsonSerializer.Serialize(cart));
    }

    private class SnapshotItem { public int ProductId { get; set; } public int? VariantId { get; set; } public int Quantity { get; set; } }

    // ── Abandoned-cart recovery ───────────────────────────────────────────────
    // Clicked from the recovery email. Rebuilds the bag from the snapshot, re-validating each item
    // against current price/availability, then marks the snapshot recovered.
    [HttpGet("/cart/recover")]
    public async Task<IActionResult> Recover(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return RedirectToAction(nameof(Index));

        var ab = await _db.AbandonedCarts.FirstOrDefaultAsync(a => a.Token == token);
        if (ab == null)
        {
            TempData["Error"] = "That recovery link is no longer valid.";
            return RedirectToAction(nameof(Index));
        }

        var snapshot = JsonSerializer.Deserialize<List<SnapshotItem>>(ab.ItemsJson) ?? new();
        var cart = GetCart();
        int added = 0;
        foreach (var s in snapshot)
        {
            if (cart.Items.Any(i => i.ProductId == s.ProductId && i.VariantId == s.VariantId)) continue;
            var product = await _db.Products.Include(p => p.Images).Include(p => p.Variants)
                .FirstOrDefaultAsync(p => p.Id == s.ProductId && p.IsActive);
            if (product == null) continue;
            var available = await CombinedAvailableAsync(s.ProductId, s.VariantId);
            if (available <= 0) continue;
            var variant = s.VariantId.HasValue ? product.Variants.FirstOrDefault(v => v.Id == s.VariantId) : null;
            cart.Items.Add(new CartItemViewModel
            {
                ProductId = product.Id,
                VariantId = s.VariantId,
                ProductName = product.Name,
                VariantName = variant?.Name,
                Slug = product.Slug,
                ImageUrl = product.Images.FirstOrDefault(i => i.IsPrimary)?.Url ?? product.Images.FirstOrDefault()?.Url ?? "/images/placeholder.jpg",
                UnitPrice = product.EffectivePrice + (variant?.PriceAdjustment ?? 0),
                Quantity = Math.Min(Math.Max(1, s.Quantity), available),
                MaxQuantity = available
            });
            added++;
        }
        SaveCart(cart);

        ab.RecoveredAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData[added > 0 ? "Success" : "Error"] = added > 0
            ? "Welcome back — we've restored your bag."
            : "Those items are no longer available.";
        return RedirectToAction(nameof(Index));
    }
}
