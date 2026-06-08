namespace SterlingLams.Web.Models.Domain;

public enum OrderStatus
{
    Pending,
    Confirmed,
    Processing,
    ReadyForPickup,
    Shipped,
    Delivered,
    Cancelled,
    Refunded
}

public enum FulfillmentType
{
    StorePickup,
    Delivery
}

public enum OrderChannel
{
    Online, // placed by a customer on the website
    Pos     // rung up in-store at a branch
}

public class Order
{
    public int Id { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string? ErpNextInvoiceName { get; set; }

    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;

    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public FulfillmentType FulfillmentType { get; set; }

    /// <summary>Where the sale came from. POS orders are rung up in-store by a cashier.</summary>
    public OrderChannel Channel { get; set; } = OrderChannel.Online;

    /// <summary>Cash handling for POS sales.</summary>
    public decimal? AmountTendered { get; set; }
    public decimal? ChangeGiven { get; set; }

    /// <summary>The till this sale was rung up on (POS sales).</summary>
    public int? RegisterId { get; set; }
    public Register? Register { get; set; }

    public int? PickupStoreId { get; set; }
    public Store? PickupStore { get; set; }

    public int? DeliveryAddressId { get; set; }
    public Address? DeliveryAddress { get; set; }

    public decimal Subtotal { get; set; }
    public decimal DeliveryFee { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public string Currency { get; set; } = "NGN";

    public string? DiscountCode { get; set; }
    public decimal DiscountAmount { get; set; }

    public string? PaymentReference { get; set; }
    public string? PaymentProvider { get; set; }
    public bool IsPaid { get; set; }
    public DateTime? PaidAt { get; set; }

    public string? TrackingNumber { get; set; }
    public string? Notes { get; set; }
    public string? AdminNotes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<OrderItem> Items { get; set; } = new List<OrderItem>();
}
