namespace SterlingLams.Web.Models.ViewModels;

public class StoreStockViewModel
{
    public string StoreName { get; set; } = string.Empty;
    public string StoreSlug { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public bool IsAvailable => Quantity > 0;
}

public class ProductDetailViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public string? Description { get; set; }
    public string? ShortDescription { get; set; }
    public decimal Price { get; set; }
    public string Currency { get; set; } = "NGN";
    public string FormattedPrice => $"₦{Price:N0}";

    public string? Material { get; set; }
    public string? Metal { get; set; }
    public string? GemstoneType { get; set; }
    public string? Carat { get; set; }
    public string? Weight { get; set; }

    public string CategoryName { get; set; } = string.Empty;
    public string CategorySlug { get; set; } = string.Empty;

    public List<string> ImageUrls { get; set; } = new();
    public string PrimaryImageUrl => ImageUrls.FirstOrDefault() ?? "/images/placeholder.jpg";

    public List<StoreStockViewModel> StoreStock { get; set; } = new();
    public bool IsAvailableAnywhere => StoreStock.Any(s => s.IsAvailable);
    public int TotalStock => StoreStock.Sum(s => s.Quantity);

    public List<ProductVariantOptionViewModel> Variants { get; set; } = new();
    public List<string> Tags { get; set; } = new();

    public bool IsInWishlist { get; set; }
    public List<ProductCardViewModel> RelatedProducts { get; set; } = new();
    public List<ProductCardViewModel> FrequentlyBoughtTogether { get; set; } = new();
}

public class AttributeLabelViewModel
{
    public string AttributeName { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? ColorHex { get; set; }
}

public class ProductVariantOptionViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal? PriceAdjustment { get; set; }
    public int Available { get; set; }   // combined available across active branches (per-variant, with pool fallback)
    public List<AttributeLabelViewModel> AttributeLabels { get; set; } = new();
}
