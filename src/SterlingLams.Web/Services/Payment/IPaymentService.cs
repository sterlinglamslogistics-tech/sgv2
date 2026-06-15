namespace SterlingLams.Web.Services.Payment;

public interface IPaymentService
{
    string ProviderName { get; }
    Task<InitiatePaymentResult> InitiatePaymentAsync(InitiatePaymentRequest request);
    Task<VerifyPaymentResult> VerifyPaymentAsync(string reference);
    Task<bool> ValidateWebhookAsync(string payload, string signature);

    /// <summary>
    /// Refunds (all or part of) a previously successful charge at the gateway. Best-effort: callers
    /// treat the in-house refund record + stock return as authoritative and use this result only to
    /// know whether the money was returned automatically or needs a manual refund.
    /// </summary>
    Task<RefundResult> RefundPaymentAsync(RefundPaymentRequest request);
}

public class RefundPaymentRequest
{
    /// <summary>The original successful transaction reference (Order.PaymentReference).</summary>
    public string Reference { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string? Reason { get; set; }
}

public class RefundResult
{
    public bool Success { get; set; }
    /// <summary>False when the provider has no automated refund wired up — the admin must refund
    /// via the provider dashboard. Distinct from a real failure (Success=false, Supported=true).</summary>
    public bool Supported { get; set; } = true;
    public string? ProviderReference { get; set; }
    public string? ErrorMessage { get; set; }
}

public class InitiatePaymentRequest
{
    public string OrderNumber { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "NGN";
    public string CustomerEmail { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string CallbackUrl { get; set; } = string.Empty;
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public class InitiatePaymentResult
{
    public bool Success { get; set; }
    public string? AuthorizationUrl { get; set; }
    public string? Reference { get; set; }
    public string? ErrorMessage { get; set; }
}

public class VerifyPaymentResult
{
    public bool Success { get; set; }
    public bool IsPaid { get; set; }
    public string? Reference { get; set; }
    public string? OrderNumber { get; set; }
    public decimal AmountPaid { get; set; }
    public string? ErrorMessage { get; set; }
}
