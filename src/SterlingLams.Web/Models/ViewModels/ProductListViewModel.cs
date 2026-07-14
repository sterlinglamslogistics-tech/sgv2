namespace SterlingLams.Web.Models.ViewModels;

public class ProductCardViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string PrimaryImageUrl { get; set; } = "/images/placeholder.jpg";
    /// <summary>Second image revealed on card hover (Tiffany-style). Null when the product has only
    /// one image — the card then shows no swap.</summary>
    public string? SecondaryImageUrl { get; set; }
    public decimal Price { get; set; }
    public decimal? SalePrice { get; set; }
    /// <summary>Sale window (UTC); null = open-ended. Populate from Product so scheduled sales
    /// only show within their window. When both are null the sale is always live (legacy behaviour).</summary>
    public DateTime? SaleStartsAt { get; set; }
    public DateTime? SaleEndsAt { get; set; }
    public string Currency { get; set; } = "NGN";
    public bool IsOnSale => SalePrice is decimal s && s > 0m && s < Price
        && (SaleStartsAt == null || SaleStartsAt <= DateTime.UtcNow)
        && (SaleEndsAt == null || SaleEndsAt >= DateTime.UtcNow);
    public decimal EffectivePrice => IsOnSale ? SalePrice!.Value : Price;
    /// <summary>The price to show prominently (sale price when on sale).</summary>
    public string FormattedPrice => $"₦{EffectivePrice:N0}";
    /// <summary>The regular price — render struck-through when <see cref="IsOnSale"/>.</summary>
    public string FormattedRegularPrice => $"₦{Price:N0}";
    /// <summary>Whole-number % off (e.g. 21) when on sale, else 0 — drives the discount badge.</summary>
    public int DiscountPercent => IsOnSale && Price > 0m
        ? (int)Math.Round((Price - SalePrice!.Value) / Price * 100m)
        : 0;
    public bool IsAvailable { get; set; }
    /// <summary>Total on-hand across branches — drives the "Only N left" low-stock badge on cards.</summary>
    public int TotalStock { get; set; }
    public bool IsInWishlist { get; set; }
    public bool IsNewArrival { get; set; }
    public bool HasVariants { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public double AverageRating { get; set; }
    public int ReviewCount { get; set; }
}

public class ProductListViewModel
{
    public List<ProductCardViewModel> Products { get; set; } = new();
    public ProductFilterViewModel Filters { get; set; } = new();

    public int TotalCount { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 24;
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);

    public string? ActiveCategory { get; set; }
    public string? PageTitle { get; set; }
}

public class ProductFilterViewModel
{
    public string? Search { get; set; }
    public string? Category { get; set; }
    public string? Metal { get; set; }
    public string? GemstoneType { get; set; }
    public string? Store { get; set; }
    public decimal? MinPrice { get; set; }
    public decimal? MaxPrice { get; set; }
    public string? SortBy { get; set; } = "newest";
    public bool? InStockOnly { get; set; }

    public List<string> AvailableMetals { get; set; } = new();
    public List<string> AvailableGemstones { get; set; } = new();
    public List<CategoryFilterOption> Categories { get; set; } = new();
}

public class CategoryFilterOption
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public int Count { get; set; }
}
