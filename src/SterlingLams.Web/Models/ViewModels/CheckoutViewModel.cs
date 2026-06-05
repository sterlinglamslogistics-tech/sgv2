using System.ComponentModel.DataAnnotations;

namespace SterlingLams.Web.Models.ViewModels;

public class CheckoutViewModel
{
    public CartViewModel Cart { get; set; } = new();

    public FulfillmentChoice FulfillmentType { get; set; } = FulfillmentChoice.Delivery;

    // Store Pickup
    public int? SelectedStoreId { get; set; }
    public List<StorePickupOptionViewModel> AvailableStores { get; set; } = new();

    // Delivery
    public DeliveryAddressViewModel DeliveryAddress { get; set; } = new();

    // Payment
    public string PaymentProvider { get; set; } = "Paystack";
    public string? PaystackPublicKey { get; set; }

    // Totals
    public decimal Subtotal { get; set; }
    public decimal DeliveryFee { get; set; }
    public decimal DiscountAmount { get; set; }
    public string? AppliedDiscountCode { get; set; }
    public string? DiscountDescription { get; set; }
    public decimal Total => Subtotal - DiscountAmount + DeliveryFee;
    public string FormattedTotal => $"₦{Total:N0}";
    public string FormattedSubtotal => $"₦{Subtotal:N0}";
    public string FormattedDeliveryFee => DeliveryFee == 0 ? "Free" : $"₦{DeliveryFee:N0}";
    public string FormattedDiscount => $"-₦{DiscountAmount:N0}";
    public bool HasDiscount => DiscountAmount > 0;
}

public enum FulfillmentChoice
{
    Delivery,
    StorePickup
}

public class StorePickupOptionViewModel
{
    public int StoreId { get; set; }
    public string StoreName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string? OpeningHours { get; set; }
    public bool AllItemsAvailable { get; set; }
}

public class DeliveryAddressViewModel
{
    [Required] public string FullName { get; set; } = string.Empty;
    [Required] public string Phone { get; set; } = string.Empty;
    [Required] public string Line1 { get; set; } = string.Empty;
    public string? Line2 { get; set; }
    [Required] public string City { get; set; } = string.Empty;
    [Required] public string State { get; set; } = string.Empty;
    public string Country { get; set; } = "Nigeria";
    public string? PostalCode { get; set; }
    public bool SaveAddress { get; set; }
}
