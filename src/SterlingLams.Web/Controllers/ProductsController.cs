using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Models.ViewModels;

namespace SterlingLams.Web.Controllers;

public class ProductsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<ProductsController> _logger;
    private readonly SterlingLams.Web.Services.IMerchandisingService _merch;
    private readonly SterlingLams.Web.Services.ISettingsService _settings;

    public ProductsController(ApplicationDbContext db, ILogger<ProductsController> logger,
        SterlingLams.Web.Services.IMerchandisingService merch,
        SterlingLams.Web.Services.ISettingsService settings)
    {
        _db = db;
        _logger = logger;
        _merch = merch;
        _settings = settings;
    }

    // GET /products
    [Microsoft.AspNetCore.OutputCaching.OutputCache(PolicyName = "Storefront")]
    public async Task<IActionResult> Index(ProductFilterViewModel filters, int page = 1, int pageSize = 60)
    {
        // No Includes: the card only needs a handful of fields + three booleans, so we project
        // straight to ProductCardViewModel in SQL (below). Loading full Images/Variants/Inventory
        // graphs here caused a cartesian JOIN blow-up and over-fetch on the busiest page.
        var query = _db.Products.Where(p => p.IsActive);

        // Admin toggle: hide products that are out of stock everywhere (simple or all-variants-out).
        var hideOos = await _settings.GetBoolAsync("storefront.hide_out_of_stock", false);
        if (hideOos)
            query = query.Where(p => p.StoreInventories.Any(si => si.QuantityOnHand > 0));

        if (!string.IsNullOrWhiteSpace(filters.Search))
        {
            var term = filters.Search.Trim();
            query = query.Where(p => EF.Functions.ILike(p.Name, $"%{term}%")
                || EF.Functions.ILike(p.Description ?? "", $"%{term}%")
                || EF.Functions.ILike(p.Sku ?? "", $"%{term}%")
                || p.Variants.Any(v => EF.Functions.ILike(v.Sku ?? "", $"%{term}%")));
        }

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
                SalePrice = p.SalePrice,
                SaleStartsAt = p.SaleStartsAt,
                SaleEndsAt = p.SaleEndsAt,
                Currency = p.Currency,
                PrimaryImageUrl = p.Images.OrderByDescending(i => i.IsPrimary).ThenByDescending(i => i.IsHover).ThenBy(i => i.SortOrder)
                    .Select(i => i.Url).FirstOrDefault() ?? "/images/placeholder.jpg",
                SecondaryImageUrl = p.Images.OrderByDescending(i => i.IsPrimary).ThenByDescending(i => i.IsHover).ThenBy(i => i.SortOrder)
                    .Select(i => i.Url).Skip(1).FirstOrDefault(),
                IsAvailable = p.StoreInventories.Any(si => si.QuantityOnHand > 0),
                TotalStock = p.StoreInventories.Sum(si => (int?)si.QuantityOnHand) ?? 0,
                IsNewArrival = p.IsNewArrival,
                HasVariants = p.Variants.Any(v => v.IsActive),
                CategoryName = p.Category.Name
            })
            .ToListAsync();

        // Card ratings — one grouped query over the page's products (not a per-card subquery).
        var cardIds = products.Select(c => c.Id).ToList();
        if (cardIds.Count > 0)
        {
            var ratings = await _db.ProductReviews
                .Where(r => cardIds.Contains(r.ProductId) && r.IsApproved)
                .GroupBy(r => r.ProductId)
                .Select(g => new { ProductId = g.Key, Count = g.Count(), Avg = g.Average(x => (double)x.Rating) })
                .ToListAsync();
            foreach (var c in products)
            {
                var rr = ratings.FirstOrDefault(x => x.ProductId == c.Id);
                if (rr != null) { c.ReviewCount = rr.Count; c.AverageRating = Math.Round(rr.Avg, 1); }
            }
        }

        var wishlistProductIds = User.Identity?.IsAuthenticated == true
            ? (await _db.WishlistItems
                .Where(w => w.UserId == GetUserId())
                .Select(w => w.ProductId)
                .ToListAsync()).ToHashSet()
            : new HashSet<int>();
        foreach (var c in products) c.IsInWishlist = wishlistProductIds.Contains(c.Id);

        // Category navigation with live product counts (sidebar on desktop, top nav on mobile).
        // Counts respect the hide-out-of-stock toggle so they match what the listing shows.
        filters.Categories = await _db.Categories
            .Where(c => c.IsActive && c.Products.Any(p => p.IsActive
                && (!hideOos || p.StoreInventories.Any(si => si.QuantityOnHand > 0))))
            .OrderBy(c => c.Name)
            .Select(c => new CategoryFilterOption
            {
                Name = c.Name,
                Slug = c.Slug,
                Count = c.Products.Count(p => p.IsActive
                    && (!hideOos || p.StoreInventories.Any(si => si.QuantityOnHand > 0)))
            })
            .ToListAsync();

        // Page heading: the category being viewed, else the search term, else the default.
        string? pageTitle = null;
        if (!string.IsNullOrWhiteSpace(filters.Category))
            pageTitle = await _db.Categories.Where(c => c.Slug == filters.Category)
                .Select(c => c.Name).FirstOrDefaultAsync();
        else if (!string.IsNullOrWhiteSpace(filters.Search))
            pageTitle = $"Results for “{filters.Search.Trim()}”";

        var vm = new ProductListViewModel
        {
            Products = products,
            Filters = filters,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            ActiveCategory = filters.Category,
            PageTitle = pageTitle
        };

        // "View more" append: return just the next page of cards (no layout) for the JS to splice in.
        if (Request.Query["partial"] == "cards")
            return PartialView("_ProductCards", vm.Products);

        return View(vm);
    }

    // GET /products/compare?slugs=a,b,c — side-by-side comparison of up to 4 products.
    // (Literal route beats the {slug} route below, so "compare" is never treated as a product slug.)
    [HttpGet("products/compare")]
    public async Task<IActionResult> Compare(string? slugs)
    {
        ViewData["Title"] = "Compare Products";
        var list = (slugs ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().Take(4).ToList();

        var products = list.Count == 0 ? new List<Product>() : await _db.Products
            .Include(p => p.Category).Include(p => p.Images).Include(p => p.StoreInventories)
            .Where(p => p.IsActive && list.Contains(p.Slug))
            .ToListAsync();

        // Preserve the order the customer added them.
        var ordered = list.Select(s => products.FirstOrDefault(p => p.Slug == s)).Where(p => p != null).Select(p => p!).ToList();
        return View(ordered);
    }

    // GET /products/{slug}
    [HttpGet("products/{slug}")]
    public async Task<IActionResult> Detail(string slug)
    {
        // Read-only page. Four collection Includes (Images, Variants, StoreInventories, Tags) on one
        // root would cartesian-explode into tens of thousands of rows for a many-variant product like
        // the alphabet necklace (26 variants × 26 images × 81 stock rows). AsSplitQuery loads each
        // collection with its own query (~150 rows total), and AsNoTracking skips change tracking.
        var product = await _db.Products
            .AsNoTracking()
            .AsSplitQuery()
            .Include(p => p.Category)
            .Include(p => p.Images.OrderBy(i => i.SortOrder))
            .Include(p => p.Variants).ThenInclude(v => v.AttributeValues).ThenInclude(av => av.Attribute)
            .Include(p => p.StoreInventories).ThenInclude(si => si.Store)
            .Include(p => p.Tags)
            .FirstOrDefaultAsync(p => p.Slug == slug && p.IsActive);

        if (product == null) return NotFound();

        // Admin toggle: a product that's out of stock everywhere is hidden from the storefront
        // entirely — a direct link returns Not Found (matches it being absent from listings).
        var hideOos = await _settings.GetBoolAsync("storefront.hide_out_of_stock", false);
        if (hideOos && !product.StoreInventories.Any(si => si.QuantityOnHand > 0))
            return NotFound();

        // Track for the "Recently viewed" merchandising row (cookie-based; works for guests).
        SterlingLams.Web.Infrastructure.RecentlyViewed.Record(Request, Response, product.Id);

        var isInWishlist = User.Identity?.IsAuthenticated == true
            && await _db.WishlistItems.AnyAsync(w => w.UserId == GetUserId() && w.ProductId == product.Id);

        // NB: single-query (not split) on purpose — split + random ordering would re-roll the random
        // pick per collection query and mismatch the includes. Take(4) keeps the cartesian tiny anyway.
        var relatedProducts = await _db.Products
            .AsNoTracking()
            .Include(p => p.Images)
            .Include(p => p.StoreInventories)
            .Where(p => p.CategoryId == product.CategoryId && p.Id != product.Id && p.IsActive)
            .OrderBy(_ => EF.Functions.Random())
            .Take(4)
            .ToListAsync();

        // Per-variant available across active branches, using the effective-row fallback (variant's
        // own row if stocked, else the product pool) — mirrors StockService/cart so the page, cart
        // and checkout agree.
        var activeStores = product.StoreInventories.Where(si => si.Store.IsActive)
            .Select(si => new { si.StoreId, si.Store.Name, si.Store.Slug })
            .Distinct().OrderBy(s => s.Name).ToList();

        // Per-branch available for a variant (variant's own row if stocked, else the product pool).
        List<StoreStockViewModel> VariantStoreStock(int variantId) =>
            activeStores.Select(s =>
            {
                var row = product.StoreInventories.FirstOrDefault(si => si.StoreId == s.StoreId && si.ProductVariantId == variantId)
                          ?? product.StoreInventories.FirstOrDefault(si => si.StoreId == s.StoreId && si.ProductVariantId == null);
                return new StoreStockViewModel
                {
                    StoreName = s.Name, StoreSlug = s.Slug,
                    Quantity = row != null ? Math.Max(0, row.QuantityOnHand - row.QuantityReserved) : 0
                };
            }).ToList();
        int VariantAvailable(int variantId) => VariantStoreStock(variantId).Sum(s => s.Quantity);

        var vm = new ProductDetailViewModel
        {
            Id = product.Id,
            Name = product.Name,
            Slug = product.Slug,
            Sku = product.Sku,
            Description = product.Description,
            ShortDescription = product.ShortDescription,
            Price = product.Price,
            SalePrice = product.SalePrice,
            SaleStartsAt = product.SaleStartsAt,
            SaleEndsAt = product.SaleEndsAt,
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
            // When hiding out-of-stock, only surface variant options (size/colour) that have stock;
            // the sold-out ones drop out of both the dropdowns and the variant data.
            Variants = product.Variants.Where(v => v.IsActive && (!hideOos || VariantAvailable(v.Id) > 0)).Select(v => new ProductVariantOptionViewModel
            {
                Id = v.Id,
                Name = v.Name,
                PriceAdjustment = v.PriceAdjustment,
                ImageUrl = v.ImageUrl,
                Available = VariantAvailable(v.Id),
                StoreStock = VariantStoreStock(v.Id),
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
                SalePrice = p.SalePrice,
                SaleStartsAt = p.SaleStartsAt,
                SaleEndsAt = p.SaleEndsAt,
                PrimaryImageUrl = p.Images.OrderByDescending(i => i.IsPrimary).ThenByDescending(i => i.IsHover).ThenBy(i => i.SortOrder)
                    .Select(i => i.Url).FirstOrDefault() ?? "/images/placeholder.jpg",
                SecondaryImageUrl = p.Images.OrderByDescending(i => i.IsPrimary).ThenByDescending(i => i.IsHover).ThenBy(i => i.SortOrder)
                    .Select(i => i.Url).Skip(1).FirstOrDefault(),
                IsAvailable = p.StoreInventories.Any(si => si.QuantityOnHand > 0)
            }).ToList(),
            FrequentlyBoughtTogether = await _merch.FrequentlyBoughtTogetherAsync(product.Id, 4),
            OutOfStockMessage = await _settings.GetAsync("store.out_of_stock_msg",
                "This item is currently out of stock. Check back soon.")
        };

        // ── Reviews ──────────────────────────────────────────────────────────
        vm.ReviewsEnabled = await _settings.GetBoolAsync("reviews.enabled", true);
        if (vm.ReviewsEnabled)
        {
            var approved = await _db.ProductReviews
                .Where(r => r.ProductId == product.Id && r.IsApproved)
                .OrderByDescending(r => r.CreatedAt)
                .ToListAsync();
            vm.ReviewCount = approved.Count;
            vm.AverageRating = approved.Count > 0 ? Math.Round(approved.Average(r => r.Rating), 1) : 0;
            vm.RatingBreakdown = Enumerable.Range(1, 5).ToDictionary(s => s, s => approved.Count(r => r.Rating == s));
            vm.Reviews = approved.Take(30).Select(r => new ProductReviewViewModel
            {
                AuthorName = r.AuthorName, Rating = r.Rating, Title = r.Title, Body = r.Body,
                IsVerifiedBuyer = r.IsVerifiedBuyer, AdminReply = r.AdminReply, CreatedAt = r.CreatedAt
            }).ToList();
            if (User.Identity?.IsAuthenticated == true)
            {
                var uid = GetUserId();
                vm.CanReview = true;
                vm.HasReviewed = await _db.ProductReviews.AnyAsync(r => r.ProductId == product.Id && r.UserId == uid);
            }
        }

        return View(vm);
    }

    // GET /api/search?q=diamond  (live search suggestions — separate route to avoid slug conflict)
    [HttpGet("/api/search")]
    public async Task<IActionResult> SearchSuggestions(string q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return Json(Array.Empty<object>());

        var term = q.Trim();
        var results = await _db.Products
            .Where(p => p.IsActive && (
                EF.Functions.ILike(p.Name, $"%{term}%") ||
                EF.Functions.ILike(p.ShortDescription ?? "", $"%{term}%") ||
                EF.Functions.ILike(p.Sku ?? "", $"%{term}%") ||
                p.Variants.Any(v => EF.Functions.ILike(v.Sku ?? "", $"%{term}%"))))
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

    // GET /api/product-quickview?slug=...  (data for the listing "Select Options" popup)
    [HttpGet("/api/product-quickview")]
    public async Task<IActionResult> QuickView(string slug)
    {
        // Same cartesian-explosion guard as Detail (Images × Variants × StoreInventories).
        var product = await _db.Products
            .AsNoTracking()
            .AsSplitQuery()
            .Include(p => p.Category)
            .Include(p => p.Images.OrderBy(i => i.SortOrder))
            .Include(p => p.Variants).ThenInclude(v => v.AttributeValues).ThenInclude(av => av.Attribute)
            .Include(p => p.StoreInventories).ThenInclude(si => si.Store)
            .FirstOrDefaultAsync(p => p.Slug == slug && p.IsActive);

        if (product == null) return NotFound();

        // Per-variant availability with the variant-row → product-pool fallback (matches Detail/cart).
        var activeStores = product.StoreInventories.Where(si => si.Store.IsActive)
            .Select(si => si.StoreId).Distinct().ToList();
        int VariantAvailable(int variantId) => activeStores.Sum(storeId =>
        {
            var row = product.StoreInventories.FirstOrDefault(si => si.StoreId == storeId && si.ProductVariantId == variantId)
                      ?? product.StoreInventories.FirstOrDefault(si => si.StoreId == storeId && si.ProductVariantId == null);
            return row != null ? Math.Max(0, row.QuantityOnHand - row.QuantityReserved) : 0;
        });

        var onSale = product.SalePrice is decimal sp && sp > 0m && sp < product.Price;

        return Json(new
        {
            id = product.Id,
            name = product.Name,
            slug = product.Slug,
            category = product.Category.Name,
            price = product.Price,
            salePrice = onSale ? product.SalePrice : null,
            currency = product.Currency,
            primaryImage = product.Images.OrderByDescending(i => i.IsPrimary).Select(i => i.Url).FirstOrDefault()
                ?? "/images/placeholder.jpg",
            attributes = product.Variants.Where(v => v.IsActive)
                .SelectMany(v => v.AttributeValues)
                .GroupBy(av => new { av.Attribute.Name, av.Attribute.SortOrder })
                .OrderBy(g => g.Key.SortOrder)
                .Select(g => new { name = g.Key.Name, values = g.Select(av => av.Value).Distinct().ToList() }),
            variants = product.Variants.Where(v => v.IsActive).Select(v => new
            {
                id = v.Id,
                imageUrl = v.ImageUrl,
                priceAdjustment = v.PriceAdjustment,
                inStock = VariantAvailable(v.Id) > 0,
                attributes = v.AttributeValues.ToDictionary(av => av.Attribute.Name, av => av.Value)
            })
        });
    }

    // POST /Products/NotifyRestock  (back-in-stock email capture)
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth")]
    public async Task<IActionResult> NotifyRestock(int productId, string email)
    {
        email = (email ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(email) && new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email)
            && await _db.Products.AnyAsync(p => p.Id == productId && p.IsActive))
        {
            // Persist the request (deduped: one pending row per product+email). The BackInStockNotifier
            // background service emails them once the product is available again.
            var alreadyPending = await _db.BackInStockRequests
                .AnyAsync(r => r.ProductId == productId && r.Email == email && r.NotifiedAt == null);
            if (!alreadyPending)
            {
                _db.BackInStockRequests.Add(new BackInStockRequest
                {
                    ProductId = productId, Email = email, CreatedAt = DateTime.UtcNow
                });
                await _db.SaveChangesAsync();
                _logger.LogInformation("Back-in-stock request saved: product {ProductId} for {Email}", productId, SterlingLams.Web.Infrastructure.LogRedact.Email(email));
            }
        }

        return Json(new { success = true });
    }

    private string GetUserId() => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
}
