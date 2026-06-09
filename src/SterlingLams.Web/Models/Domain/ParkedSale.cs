namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// A sale parked ("held") on a till before payment. The cart is stored as JSON so it can be
/// recalled verbatim onto any till at the same store. No stock is reserved — stock only moves
/// when the recalled sale is paid.
/// </summary>
public class ParkedSale
{
    public int Id { get; set; }

    public int RegisterId { get; set; }
    public int StoreId { get; set; }

    public string CashierUserId { get; set; } = string.Empty;
    public string CashierName { get; set; } = string.Empty;

    public string? CustomerUserId { get; set; }
    public string? CustomerName { get; set; }

    /// <summary>Optional free-text label so a cashier can tell held sales apart.</summary>
    public string? Label { get; set; }

    public int ItemCount { get; set; }
    public decimal Total { get; set; }

    /// <summary>The serialized cart (mirrors the till's JS cart line shape) used to rehydrate on recall.</summary>
    public string CartJson { get; set; } = "[]";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
