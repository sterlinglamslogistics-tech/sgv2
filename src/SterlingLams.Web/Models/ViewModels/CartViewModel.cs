namespace SterlingLams.Web.Models.ViewModels;

public class CartItemViewModel
{
    public int ProductId { get; set; }
    public int? VariantId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? VariantName { get; set; }
    public string ImageUrl { get; set; } = "/images/placeholder.jpg";
    public string Slug { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public decimal LineTotal => UnitPrice * Quantity;
    public string FormattedLineTotal => $"₦{LineTotal:N0}";
    public string FormattedUnitPrice => $"₦{UnitPrice:N0}";
    public int MaxQuantity { get; set; } = 10;
}

public class CartViewModel
{
    public List<CartItemViewModel> Items { get; set; } = new();
    /// <summary>Items the shopper moved out of the bag to "save for later" — kept in the session
    /// cart but not counted toward totals or checkout.</summary>
    public List<CartItemViewModel> SavedItems { get; set; } = new();
    public decimal Subtotal => Items.Sum(i => i.LineTotal);
    public string FormattedSubtotal => $"₦{Subtotal:N0}";
    public int TotalItems => Items.Sum(i => i.Quantity);
    public bool IsEmpty => !Items.Any();

    // Discount
    public string? AppliedDiscountCode { get; set; }
    public string? DiscountDescription { get; set; }
    public decimal DiscountAmount { get; set; }
    public bool FreeShipping { get; set; }
    public bool IsAutomaticDiscount { get; set; }
    public bool HasDiscount => DiscountAmount > 0 || FreeShipping;
    public string FormattedDiscount => $"-₦{DiscountAmount:N0}";

    public decimal Total => Subtotal - DiscountAmount;
    public string FormattedTotal => $"₦{Total:N0}";
}
