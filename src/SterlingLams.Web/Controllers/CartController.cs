using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using SterlingLams.Web.Models.ViewModels;
using SterlingLams.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace SterlingLams.Web.Controllers;

public class CartController : Controller
{
    private const string CartSessionKey = "cart";
    private readonly ApplicationDbContext _db;

    public CartController(ApplicationDbContext db)
    {
        _db = db;
    }

    public IActionResult Index()
    {
        var cart = GetCart();
        return View(cart);
    }

    [HttpPost]
    public async Task<IActionResult> Add(int productId, int quantity = 1, int? variantId = null)
    {
        var product = await _db.Products
            .Include(p => p.Images)
            .Include(p => p.Variants)
            .FirstOrDefaultAsync(p => p.Id == productId && p.IsActive);

        if (product == null)
            return Json(new { success = false, message = "Product not found." });

        var cart = GetCart();
        var existing = cart.Items.FirstOrDefault(i => i.ProductId == productId && i.VariantId == variantId);

        if (existing != null)
        {
            existing.Quantity = Math.Min(existing.Quantity + quantity, existing.MaxQuantity);
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
                UnitPrice = product.Price + (variant?.PriceAdjustment ?? 0),
                Quantity = quantity
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

    [HttpPost]
    public IActionResult UpdateQuantity(int productId, int quantity, int? variantId = null)
    {
        var cart = GetCart();
        var item = cart.Items.FirstOrDefault(i => i.ProductId == productId && i.VariantId == variantId);

        if (item != null)
        {
            if (quantity <= 0)
                cart.Items.Remove(item);
            else
                item.Quantity = Math.Min(quantity, item.MaxQuantity);
        }

        SaveCart(cart);
        return Json(new { success = true, cartCount = cart.TotalItems, subtotal = cart.FormattedSubtotal });
    }

    [HttpPost]
    public IActionResult Remove(int productId, int? variantId = null)
    {
        var cart = GetCart();
        var item = cart.Items.FirstOrDefault(i => i.ProductId == productId && i.VariantId == variantId);
        if (item != null) cart.Items.Remove(item);
        SaveCart(cart);
        return Json(new { success = true, cartCount = cart.TotalItems });
    }

    [HttpPost]
    public async Task<IActionResult> ApplyDiscount(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Json(new { success = false, message = "Please enter a discount code." });

        var cart = GetCart();
        var discount = await _db.DiscountCodes
            .FirstOrDefaultAsync(d => d.Code == code.Trim().ToUpper());

        if (discount == null || !discount.IsValid(cart.Subtotal))
        {
            string reason = "Invalid or expired discount code.";
            if (discount != null && discount.MinimumOrderAmount.HasValue && cart.Subtotal < discount.MinimumOrderAmount.Value)
                reason = $"Minimum order of ₦{discount.MinimumOrderAmount.Value:N0} required.";
            return Json(new { success = false, message = reason });
        }

        cart.AppliedDiscountCode = discount.Code;
        cart.DiscountDescription = discount.Description ?? discount.Code;
        cart.DiscountAmount = discount.CalculateDiscount(cart.Subtotal);
        SaveCart(cart);

        return Json(new
        {
            success = true,
            code = cart.AppliedDiscountCode,
            description = cart.DiscountDescription,
            discount = cart.FormattedDiscount,
            total = cart.FormattedTotal
        });
    }

    [HttpPost]
    public IActionResult RemoveDiscount()
    {
        var cart = GetCart();
        cart.AppliedDiscountCode = null;
        cart.DiscountDescription = null;
        cart.DiscountAmount = 0;
        SaveCart(cart);
        return Json(new { success = true, total = cart.FormattedTotal });
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
}
