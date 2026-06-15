using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SterlingLams.Web.Services.Payment;

public class PaystackSettings
{
    public string SecretKey { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.paystack.co";
}

public class PaystackPaymentService : IPaymentService
{
    private readonly HttpClient _http;
    private readonly PaystackSettings _settings;
    private readonly ILogger<PaystackPaymentService> _logger;

    public string ProviderName => "Paystack";

    public PaystackPaymentService(HttpClient http, PaystackSettings settings, ILogger<PaystackPaymentService> logger)
    {
        _http = http;
        _http.BaseAddress = new Uri(settings.BaseUrl);
        _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.SecretKey);
        _settings = settings;
        _logger = logger;
    }

    public async Task<InitiatePaymentResult> InitiatePaymentAsync(InitiatePaymentRequest request)
    {
        try
        {
            var payload = new
            {
                email = request.CustomerEmail,
                amount = (int)(request.Amount * 100), // Paystack uses kobo
                reference = $"SL-{request.OrderNumber}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
                currency = request.Currency,
                callback_url = request.CallbackUrl,
                metadata = new
                {
                    order_number = request.OrderNumber,
                    customer_name = request.CustomerName,
                    custom_fields = request.Metadata.Select(kv => new { display_name = kv.Key, variable_name = kv.Key, value = kv.Value }).ToArray()
                }
            };

            var response = await _http.PostAsJsonAsync("/transaction/initialize", payload);
            var content = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<PaystackInitResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (result?.Status == true && result.Data != null)
            {
                return new InitiatePaymentResult
                {
                    Success = true,
                    AuthorizationUrl = result.Data.AuthorizationUrl,
                    Reference = result.Data.Reference
                };
            }

            return new InitiatePaymentResult { Success = false, ErrorMessage = result?.Message ?? "Unknown error" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Paystack initiate payment failed for order {OrderNumber}", request.OrderNumber);
            return new InitiatePaymentResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<VerifyPaymentResult> VerifyPaymentAsync(string reference)
    {
        try
        {
            var response = await _http.GetAsync($"/transaction/verify/{reference}");
            var content = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<PaystackVerifyResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (result?.Status == true && result.Data?.Status == "success")
            {
                return new VerifyPaymentResult
                {
                    Success = true,
                    IsPaid = true,
                    Reference = reference,
                    AmountPaid = result.Data.Amount / 100m,
                    OrderNumber = result.Data.Metadata?.OrderNumber
                };
            }

            return new VerifyPaymentResult { Success = false, ErrorMessage = result?.Message };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Paystack verify failed for reference {Reference}", reference);
            return new VerifyPaymentResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<RefundResult> RefundPaymentAsync(RefundPaymentRequest request)
    {
        try
        {
            // Paystack: POST /refund { transaction, amount(kobo, optional → full refund), merchant_note }
            var payload = new
            {
                transaction = request.Reference,
                amount = (int)(request.Amount * 100),
                merchant_note = request.Reason
            };
            var response = await _http.PostAsJsonAsync("/refund", payload);
            var content = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<PaystackRefundResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (result?.Status == true)
                return new RefundResult { Success = true, ProviderReference = result.Data?.Id?.ToString() };

            return new RefundResult { Success = false, ErrorMessage = result?.Message ?? "Paystack refund was not accepted." };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Paystack refund failed for reference {Reference}", request.Reference);
            return new RefundResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public Task<bool> ValidateWebhookAsync(string payload, string signature)
    {
        var hash = HMACSHA512.HashData(Encoding.UTF8.GetBytes(_settings.SecretKey), Encoding.UTF8.GetBytes(payload));
        var computed = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        return Task.FromResult(string.Equals(computed, signature, StringComparison.OrdinalIgnoreCase));
    }

    // ─── Paystack response DTOs ───────────────────────────────────────────────

    private class PaystackInitResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public PaystackInitData? Data { get; set; }
    }

    private class PaystackInitData
    {
        [JsonPropertyName("authorization_url")] public string AuthorizationUrl { get; set; } = string.Empty;
        [JsonPropertyName("access_code")] public string AccessCode { get; set; } = string.Empty;
        [JsonPropertyName("reference")] public string Reference { get; set; } = string.Empty;
    }

    private class PaystackVerifyResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public PaystackVerifyData? Data { get; set; }
    }

    private class PaystackVerifyData
    {
        [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
        [JsonPropertyName("reference")] public string Reference { get; set; } = string.Empty;
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("metadata")] public PaystackMetadata? Metadata { get; set; }
    }

    private class PaystackMetadata
    {
        [JsonPropertyName("order_number")] public string? OrderNumber { get; set; }
    }

    private class PaystackRefundResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public PaystackRefundData? Data { get; set; }
    }

    private class PaystackRefundData
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
    }
}
