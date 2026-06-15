using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.ViewModels;

namespace SterlingLams.Web.Services;

/// <summary>
/// Read-only merchandising queries that drive revenue sections (best sellers, trending,
/// new arrivals, recently-viewed). All return the shared <see cref="ProductCardViewModel"/>
/// so any storefront card grid can render them. No schema of its own — best sellers are
/// derived from the order ledger (OrderItems).
/// </summary>
public interface IMerchandisingService
{
    /// <summary>Top products by units sold. Pass <paramref name="sinceDays"/> for "trending"
    /// (recent window); omit for all-time best sellers.</summary>
    Task<List<ProductCardViewModel>> BestSellersAsync(int take, int? sinceDays = null);

    /// <summary>Most recently added active products.</summary>
    Task<List<ProductCardViewModel>> NewArrivalsAsync(int take);

    /// <summary>Products most often bought in the same order as <paramref name="productId"/>
    /// (co-purchase analysis over the order ledger). Empty if the product has no co-purchases yet.</summary>
    Task<List<ProductCardViewModel>> FrequentlyBoughtTogetherAsync(int productId, int take);

    /// <summary>Active products for the given ids, preserving the supplied order
    /// (used for recently-viewed). Inactive/missing ids are dropped.</summary>
    Task<List<ProductCardViewModel>> ByIdsAsync(IReadOnlyList<int> ids);
}

public class MerchandisingService : IMerchandisingService
{
    private readonly ApplicationDbContext _db;
    private readonly IMemoryCache _cache;

    // These sections are the same for every visitor and change slowly, but the best-seller query
    // is a GROUP BY over the whole OrderItems ledger — caching it for a few minutes turns "every
    // page hit re-aggregates" into one query per window. Short TTL keeps it fresh enough.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public MerchandisingService(ApplicationDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public Task<List<ProductCardViewModel>> BestSellersAsync(int take, int? sinceDays = null) =>
        _cache.GetOrCreateAsync($"merch:best:{take}:{sinceDays?.ToString() ?? "all"}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;

            var q = _db.OrderItems.AsQueryable();
            if (sinceDays is int days)
            {
                var cutoff = DateTime.UtcNow.AddDays(-days);
                q = q.Where(oi => oi.Order.CreatedAt >= cutoff);
            }

            // Rank product ids by units sold. Over-fetch so inactive/deleted products can be
            // filtered out while still returning `take` cards.
            var ranked = await q
                .GroupBy(oi => oi.ProductId)
                .Select(g => new { ProductId = g.Key, Qty = g.Sum(x => x.Quantity) })
                .OrderByDescending(x => x.Qty)
                .Take(take * 3)
                .ToListAsync();

            var cards = await LoadCardsAsync(ranked.Select(r => r.ProductId).ToList());
            return ranked
                .Select(r => cards.GetValueOrDefault(r.ProductId))
                .Where(c => c != null)
                .Take(take)
                .Select(c => c!)
                .ToList();
        })!;

    public Task<List<ProductCardViewModel>> NewArrivalsAsync(int take) =>
        _cache.GetOrCreateAsync($"merch:new:{take}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            var ids = await _db.Products
                .Where(p => p.IsActive)
                .OrderByDescending(p => p.IsNewArrival)
                .ThenByDescending(p => p.CreatedAt)
                .Take(take)
                .Select(p => p.Id)
                .ToListAsync();
            return OrderBy(ids, await LoadCardsAsync(ids));
        })!;

    public Task<List<ProductCardViewModel>> FrequentlyBoughtTogetherAsync(int productId, int take) =>
        _cache.GetOrCreateAsync($"merch:fbt:{productId}:{take}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;

            // Orders that contain this product, then the OTHER products in those orders ranked by how
            // many of those orders they appear in (co-purchase frequency). Over-fetch to survive the
            // active/inactive filter in LoadCardsAsync.
            var orderIds = _db.OrderItems.Where(oi => oi.ProductId == productId).Select(oi => oi.OrderId);
            var ranked = await _db.OrderItems
                .Where(oi => orderIds.Contains(oi.OrderId) && oi.ProductId != productId)
                .GroupBy(oi => oi.ProductId)
                .Select(g => new { ProductId = g.Key, Orders = g.Select(x => x.OrderId).Distinct().Count() })
                .OrderByDescending(x => x.Orders)
                .Take(take * 3)
                .ToListAsync();

            var cards = await LoadCardsAsync(ranked.Select(r => r.ProductId).ToList());
            return ranked
                .Select(r => cards.GetValueOrDefault(r.ProductId))
                .Where(c => c != null)
                .Take(take)
                .Select(c => c!)
                .ToList();
        })!;

    public async Task<List<ProductCardViewModel>> ByIdsAsync(IReadOnlyList<int> ids)
    {
        if (ids == null || ids.Count == 0) return new();
        return OrderBy(ids, await LoadCardsAsync(ids));
    }

    private static List<ProductCardViewModel> OrderBy(IReadOnlyList<int> ids, Dictionary<int, ProductCardViewModel> cards) =>
        ids.Select(id => cards.GetValueOrDefault(id)).Where(c => c != null).Select(c => c!).ToList();

    private async Task<Dictionary<int, ProductCardViewModel>> LoadCardsAsync(IReadOnlyList<int> ids)
    {
        if (ids.Count == 0) return new();
        var products = await _db.Products
            .Where(p => ids.Contains(p.Id) && p.IsActive)
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
                CategoryName = p.Category.Name,
            })
            .ToListAsync();
        return products.ToDictionary(p => p.Id);
    }
}
