namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// A manually-recorded business expense (logistics/fuel, salaries, rent, etc.). Lets finance net
/// costs against revenue — e.g. delivery-fee revenue vs logistics costs for the in-house courier.
/// </summary>
public class Expense
{
    public int Id { get; set; }

    /// <summary>The day the cost was incurred (used for period reporting).</summary>
    public DateTime OccurredOn { get; set; } = DateTime.UtcNow.Date;

    /// <summary>Free-ish category — "Logistics", "Salaries", "Rent", "Marketing", "Other", etc.</summary>
    public string Category { get; set; } = "Logistics";

    public decimal Amount { get; set; }
    public string? Note { get; set; }

    /// <summary>Optional branch this cost belongs to.</summary>
    public int? StoreId { get; set; }
    public Store? Store { get; set; }

    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
