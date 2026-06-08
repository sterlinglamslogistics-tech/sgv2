namespace SterlingLams.Web.Models.Domain;

public enum StockMovementType
{
    Adjustment, // manual set / opening stock / correction
    Sale,       // POS or online sale (stock out)
    Purchase,   // goods received (stock in)
    Transfer,   // inter-branch movement (out of one, into another)
    Return      // customer return (stock in)
}

/// <summary>
/// Append-only stock ledger. Every change to on-hand quantity writes one row here, so stock
/// is always traceable. StoreInventory.QuantityOnHand holds the running balance for speed.
/// </summary>
public class StockMovement
{
    public int Id { get; set; }

    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;

    public int? ProductVariantId { get; set; }
    public ProductVariant? ProductVariant { get; set; }

    public int StoreId { get; set; }
    public Store Store { get; set; } = null!;

    /// <summary>Signed change: positive = stock in, negative = stock out.</summary>
    public int QuantityChange { get; set; }

    /// <summary>Running balance at this store/product after applying this movement.</summary>
    public int BalanceAfter { get; set; }

    public StockMovementType Type { get; set; }

    /// <summary>Cross-reference, e.g. order number, PO number, transfer id.</summary>
    public string? Reference { get; set; }

    public string? Note { get; set; }

    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
