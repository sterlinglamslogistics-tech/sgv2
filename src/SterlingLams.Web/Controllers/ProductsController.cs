using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.ViewModels;

namespace SterlingLams.Web.Controllers;

public class ProductsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<ProductsController> _logger;
    private readonly SterlingLams.Web.Services.IMerchandisingService _merch;

    public ProductsController(ApplicationDbContext db, ILogger<ProductsController> logger,
        SterlingLams.Web.Services.IMerchandisingService merch)
    {
        _db = db;
        _logger = logger;
        _merch = merch;
    }

    // GET /products
    public async Task<IActionResult> Index(ProductFilterViewModel filters, int page = 1, int pageSize = 24)
    {
        // No Includes: the card only needs a handful of fields + three booleans, so we project
        // straight to ProductCardViewModel in SQL (below). Loading full Images/Variants/Inventory
        // graphs here caused a cartesian JOIN blow-up and over-fetch on the busiest page.
        var query = _db.Products.Where(p => p.IsActive);

        if (!string.IsNullOrWhiteSpace(filters.Search))
            query = query.Where(p => EF.Functions.ILike(p.Name, $"%{filters.Search}%")
                || EF.Functions.ILike(p.Description ?? "", $"%{filters.Search}%"));

        if (!string.IsNullOrWhiteSpace(filters.Category))
            query = query.Where(p => p.Category.Slug == filters.Category);

        if (!string.IsNullOrWhiteSpace(filters.Metal))
            query = query.Where(p => p.Metal == filters.Metal);

        if (!string.IsNullOrWhiteSpace(filters.GemstoneType))
            query = query.Where(p => p.GemstoneType == filters.GemstoneType);

        if (filters.MinPrice.HasValue)
            query = query.Where(p => p.Price >= filters.MinPrice.Value);

        if (filters.MaxPrice.HasValue)
            query = query.Where(p => p.Price <= filters.MaxPrice.Value);

        if (filters.InStockOnly == true)
            query = query.Where(p => p.StoreInventories.Any(si => si.QuantityOnHand > 0));

        query = filters.SortBy switch
        {
            "price_asc" => query.OrderBy(p => p.Price),
            "price_desc" => query.OrderByDescending(p => p.Price),
            "name" => query.OrderBy(p => p.Name),
            _ => query.OrderByDescending(p => p.CreatedAt)
        };

        var totalCount = await query.CountAsync();
        var products = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new ProductCardViewModel
            {
                Id = p.Id,
                Name = p.Name,
                Slug = p.Slug,
                Price = p.Price,
                Currency = p.Currency,
                PrimaryImageUrl = p.Images.OrderByDescending(i => i.IsPrimary).Select(i => i.Url).FirstOrDefault()
                    ?? "/images/placeholder.jpg",
                IsAvailable = p.StoreInventories.Any(si => si.QuantityOnHand > 0),
                IsNewArrival = p.IsNewArrival,
                HasVariants = p.Variants.Any(v => v.IsActive),
                CategoryName = p.Category.Name
            })
            .ToListAsync();

        var wishlistProductIds = User.Identity?.IsAuthenticated == true
            ? (await _db.WishlistItems
                .Where(w => w.UserId == GetUserId())
                .Select(w => w.ProductId)
                .ToListAsync()).ToHashSet()
            : new HashSet<int>();
        foreach (var c in products) c.IsInWishlist = wishlistProductIds.Contains(c.Id);

        // Category navigation with live product counts (sidebar on desktop, top nav on mobile).
        filters.Categories = await _db.Categories
            .Where(c => c.IsActive && c.Products.Any(p => p.IsActive))
            .OrderBy(c => c.Name)
            .Select(c => new CategoryFilterOption
            {
                Name = c.Name,
                Slug = c.Slug,
                Count = c.Products.Count(p => p.IsActive)
            })
            .ToListAsync();

        var vm = new ProductListViewModel
        {
            Products = products,
            Filters = filters,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            ActiveCategory = filters.Category
        };

        return View(vm);
    }

    // GET /products/{slug}
    [HttpGet("products/{slug}")]
    public async Task<IActionResult> Detail(string slug)
    {
        var product = await _db.Products
            .Include(p => p.Category)
            .Include(p => p.Images.OrderBy(i => i.SortOrder))
            .Include(p => p.Variants).ThenInclude(v => v.AttributeValues).ThenInclude(av => av.Attribute)
            .Include(p => p.StoreInventories).ThenInclude(si => si.Store)
            .Include(p => p.Tags)
            .FirstOrDefaultAsync(p => p.Slug == slug && p.IsActive);

        if (product == null) return NotFound();

        // Track for the "Recently viewed" merchandising row (cookie-based; works for guests).
        SterlingLams.Web.Infrastructure.RecentlyViewed.Record(Request, Response, product.Id);

        var isInWishlist = User.Identity?.IsAuthenticated == true
            && await _db.WishlistItems.AnyAsync(w => w.UserId == GetUserId() && w.ProductId == product.Id);

        var relatedProducts = await _db.Products
            .Include(p => p.Images)
            .Include(p => p.StoreInventories)
            .Where(p => p.CategoryId == product.CategoryId && p.Id != product.Id && p.IsActive)
            .OrderBy(_ => EF.Functions.Random())
            .Take(4)
            .ToListAsync();

        // Per-variant available across active branches, using the effective-row fallback (variant's
        // own row if stocked, else the product pool) — mirrors StockService/cart so the page, cart
        // and checkout agree.
        var activeStoreIds = product.StoreInventories.Where(si => si.Store.IsActive)
            .Select(si => si.StoreId).Distinct().ToList();
        int VariantAvailable(int variantId)
        {
            var total = 0;
            foreach (var sid in activeStoreIds)
            {
                var row = product.StoreInventories.FirstOrDefault(si => si.StoreId == sid && si.ProductVariantId == variantId)
                          ?? product.StoreInventories.FirstOrDefault(si => si.StoreId == sid && si.ProductVariantId == null);
                if (row != null) total += Math.Max(0, row.QuantityOnHand - row.QuantityReserved);
            }
            return total;
        }

        var vm = new ProductDetailViewModel
        {
            Id = product.Id,
            Name = product.Name,
            Slug = product.Slug,
            Sku = product.Sku,
            Description = product.Description,
            ShortDescription = product.ShortDescription,
            Price = product.Price,
            Currency = product.Currency,
            Material = product.Material,
            Metal = product.Metal,
            GemstoneType = product.GemstoneType,
            Carat = product.Carat,
            Weight = product.Weight,
            CategoryName = product.Category.Name,
            CategorySlug = product.Category.Slug,
            ImageUrls = product.Images.Select(i => i.Url).ToList(),
            // Total available per branch (sum of pool + any variant rows at that store).
            StoreStock = product.StoreInventories.Where(si => si.Store.IsActive)
                .GroupBy(si => new { si.StoreId, si.Store.Name, si.Store.Slug })
                .Select(g => new StoreStockViewModel
                {
                    StoreName = g.Key.Name,
                    StoreSlug = g.Key.Slug,
                    Quantity = g.Sum(si => Math.Max(0, si.QuantityOnHand - si.QuantityReserved))
                }).ToList(),
            Variants = product.Variants.Where(v => v.IsActive).Select(v => new ProductVariantOptionViewModel
            {
                Id = v.Id,
                Name = v.Name,
                PriceAdjustment = v.PriceAdjustment,
                Available = VariantAvailable(v.Id),
                AttributeLabels = v.AttributeValues
                    .OrderBy(av => av.Attribute.SortOrder)
                    .Select(av => new AttributeLabelViewModel
                    {
                        AttributeName = av.Attribute.Name,
                        Value         = av.Value,
                        ColorHex      = av.ColorHex,
                    }).ToList()
            }).ToList(),
            Tags = product.Tags.Select(t => t.Name).ToList(),
            IsInWishlist = isInWishlist,
            RelatedProducts = relatedProducts.Select(p => new ProductCardViewModel
            {
                Id = p.Id,
                Name = p.Name,
                Slug = p.Slug,
                Price = p.Price,
                PrimaryImageUrl = p.Images.FirstOrDefault()?.Url ?? "/images/placeholder.jpg",
                IsAvailable = p.StoreInventories.Any(si => si.QuantityOnHand > 0)
            }).ToList(),
            FrequentlyBoughtTogether = await _merch.FrequentlyBoughtTogetherAsync(product.Id, 4)
        };

        return View(vm);
    }

    // GET /api/search?q=diamond  (live search suggestions — separate route to avoid slug conflict)
    [HttpGet("/api/search")]
    public async Task<IActionResult> SearchSuggestions(string q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return Json(Array.Empty<object>());

        var results = await _db.Products
            .Where(p => p.IsActive && (
                EF.Functions.ILike(p.Name, $"%{q}%") ||
                EF.Functions.ILike(p.ShortDescription ?? "", $"%{q}%")))
            .OrderBy(p => p.Name)
            .Take(6)
            .Select(p => new
            {
                p.Name,
                p.Slug,
                p.Price
            })
            .ToListAsync();

        return Json(results);
    }

    // POST /Products/NotifyRestock  (back-in-stock email capture)
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth")]
    public IActionResult NotifyRestock(int productId, string email)
    {
        if (!string.IsNullOrWhiteSpace(email))
            _logger.LogInformation("Restock notification requested: product {ProductId} for {Email}", productId, email);

        return Json(new { success = true });
    }

    private string GetUserId() => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
}
