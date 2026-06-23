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
    Refunded,
    // NOTE: append only — persisted as int. Paid online order waiting for stock to be physically
    // transferred between branches to the fulfilling branch before it can ship.
    AwaitingTransfer,
    // Store-pickup order collected by the customer (QR verified at the till).
    Collected
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

    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;

    /// <summary>The buyer attached to a POS sale (optional). On POS, <see cref="UserId"/> is the cashier,
    /// so the customer is tracked separately here. Null for walk-ins and online orders.</summary>
    public string? CustomerUserId { get; set; }
    public ApplicationUser? Customer { get; set; }

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

    /// <summary>The cashier shift this sale belongs to (POS sales).</summary>
    public int? TillSessionId { get; set; }
    public TillSession? TillSession { get; set; }

    public int? PickupStoreId { get; set; }
    public Store? PickupStore { get; set; }

    /// <summary>The branch that fulfils a paid online order (nearest to the customer). Set by the
    /// fulfilment engine on payment — also doubles as the "already fulfilled" idempotency flag
    /// (null = stock not yet deducted). Pickup orders use the chosen store.</summary>
    public int? FulfillingStoreId { get; set; }
    public Store? FulfillingStore { get; set; }

    public int? DeliveryAddressId { get; set; }
    public Address? DeliveryAddress { get; set; }

    public decimal Subtotal { get; set; }
    public decimal DeliveryFee { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public string Currency { get; set; } = "NGN";

    public string? DiscountCode { get; set; }
    public decimal DiscountAmount { get; set; }

    /// <summary>Loyalty points the buyer chose to redeem on this order (earmarked at placement).</summary>
    public int LoyaltyPointsRedeemed { get; set; }
    /// <summary>₦ discount from redeemed points (already reflected in <see cref="Total"/>).</summary>
    public decimal LoyaltyDiscount { get; set; }
    /// <summary>Set when the earmarked points were actually deducted (on payment) — idempotency guard.</summary>
    public DateTime? LoyaltyRedeemedAt { get; set; }
    /// <summary>Set when loyalty was reversed on a full refund (claw back earned, return redeemed) — idempotency guard.</summary>
    public DateTime? LoyaltyReversedAt { get; set; }

    public string? PaymentReference { get; set; }
    public string? PaymentProvider { get; set; }
    public bool IsPaid { get; set; }
    public DateTime? PaidAt { get; set; }

    /// <summary>Client-generated id for a POS sale rung up OFFLINE, used to make the sync upload
    /// idempotent (re-sending the same offline sale never creates a duplicate order). Null for
    /// normal online sales.</summary>
    public string? OfflineClientId { get; set; }

    /// <summary>Secure random token for the store-pickup QR pass — encoded in the customer's QR code
    /// and verified at the till on collection. Null for non-pickup orders / before it's issued.</summary>
    public string? PickupToken { get; set; }
    /// <summary>When the "ready for pickup" email (with the QR pass) was sent — used to send it once.</summary>
    public DateTime? PickupReadyEmailedAt { get; set; }

    public string? TrackingNumber { get; set; }
    public string? Notes { get; set; }
    public string? AdminNotes { get; set; }

    // ── Order attribution (captured at checkout for online orders) ──
    /// <summary>Client IP at the time the order was placed.</summary>
    public string? CustomerIp { get; set; }
    /// <summary>Mobile / Tablet / Desktop, derived from the user agent.</summary>
    public string? DeviceType { get; set; }
    /// <summary>Where the visit came from: Direct, a referring host, or a utm_source.</summary>
    public string? Origin { get; set; }
    /// <summary>Pages viewed in the session before the order was placed.</summary>
    public int? SessionPageViews { get; set; }

    /// <summary>Set when an admin alert has been sent for a paid-but-unfulfilled order, so the
    /// fulfilment retry service doesn't alert repeatedly. Cleared (stays null) once fulfilled.</summary>
    public DateTime? FulfilmentAlertedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<OrderItem> Items { get; set; } = new List<OrderItem>();
    public ICollection<OrderNote> Timeline { get; set; } = new List<OrderNote>();
}
