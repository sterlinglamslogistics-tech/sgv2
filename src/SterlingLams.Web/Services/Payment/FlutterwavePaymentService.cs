using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SterlingLams.Web.Services.Payment;

public class FlutterwaveSettings
{
    public string SecretKey { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string EncryptionKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.flutterwave.com/v3";
}

public class FlutterwavePaymentService : IPaymentService
{
    private readonly HttpClient _http;
    private readonly FlutterwaveSettings _settings;
    private readonly ILogger<FlutterwavePaymentService> _logger;

    public string ProviderName => "Flutterwave";

    public FlutterwavePaymentService(HttpClient http, FlutterwaveSettings settings, ILogger<FlutterwavePaymentService> logger)
    {
        _http = http;
        _http.BaseAddress = new Uri(settings.BaseUrl);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.SecretKey);
        _settings = settings;
        _logger = logger;
    }

    public async Task<InitiatePaymentResult> InitiatePaymentAsync(InitiatePaymentRequest request)
    {
        try
        {
            var txRef = $"SL-{request.OrderNumber}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

            var payload = new
            {
                tx_ref = txRef,
                amount = request.Amount,
                currency = request.Currency,
                redirect_url = request.CallbackUrl,
                customer = new
                {
                    email = request.CustomerEmail,
                    name = request.CustomerName
                },
                customizations = new
                {
                    title = "Sterlin Glams",
                    description = $"Order {request.OrderNumber}",
                    logo = "https://sterlinglams.com/images/logo.png"
                },
                meta = new
                {
                    order_number = request.OrderNumber
                }
            };

            var response = await _http.PostAsJsonAsync("/payments", payload);
            var content = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<FlwInitResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (result?.Status == "success" && result.Data?.Link != null)
            {
                return new InitiatePaymentResult
                {
                    Success = true,
                    AuthorizationUrl = result.Data.Link,
                    Reference = txRef
                };
            }

            return new InitiatePaymentResult { Success = false, ErrorMessage = result?.Message ?? "Unknown error" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flutterwave initiate failed for order {OrderNumber}", request.OrderNumber);
            return new InitiatePaymentResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<VerifyPaymentResult> VerifyPaymentAsync(string reference)
    {
        try
        {
            // Flutterwave uses transaction_id (numeric) for verify; reference is tx_ref
            // First search by tx_ref
            var response = await _http.GetAsync($"/transactions?tx_ref={Uri.EscapeDataString(reference)}");
            var content = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<FlwListResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var tx = result?.Data?.FirstOrDefault();
            if (tx == null)
                return new VerifyPaymentResult { Success = false, ErrorMessage = "Transaction not found" };

            // Verify the transaction using the numeric transaction ID
            var verifyResponse = await _http.GetAsync($"/transactions/{tx.Id}/verify");
            var verifyContent = await verifyResponse.Content.ReadAsStringAsync();
            var verifyResult = JsonSerializer.Deserialize<FlwVerifyResponse>(verifyContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var isPaid = verifyResult?.Data?.Status == "successful"
                         && verifyResult.Data.Currency == tx.Currency;

            return new VerifyPaymentResult
            {
                Success = isPaid,
                IsPaid = isPaid,
                Reference = reference,
                AmountPaid = verifyResult?.Data?.Amount ?? 0,
                OrderNumber = verifyResult?.Data?.Meta?.OrderNumber
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flutterwave verify failed for reference {Reference}", reference);
            return new VerifyPaymentResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public Task<RefundResult> RefundPaymentAsync(RefundPaymentRequest request)
        => Task.FromResult(new RefundResult
        {
            Success = false,
            Supported = false,
            ErrorMessage = "Automated Flutterwave refunds are not configured — refund via the Flutterwave dashboard."
        });

    public Task<bool> ValidateWebhookAsync(string payload, string signature)
    {
        // Flutterwave uses SHA256 HMAC with the secret hash header
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(_settings.SecretKey), Encoding.UTF8.GetBytes(payload));
        var computed = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        return Task.FromResult(string.Equals(computed, signature, StringComparison.OrdinalIgnoreCase));
    }

    // ─── Flutterwave response DTOs ────────────────────────────────────────────

    private class FlwInitResponse
    {
        [JsonPropertyName("status")] public string Status { get; set; } = "";
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public FlwInitData? Data { get; set; }
    }

    private class FlwInitData
    {
        [JsonPropertyName("link")] public string? Link { get; set; }
    }

    private class FlwListResponse
    {
        [JsonPropertyName("status")] public string Status { get; set; } = "";
        [JsonPropertyName("data")] public List<FlwTransactionItem>? Data { get; set; }
    }

    private class FlwTransactionItem
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("tx_ref")] public string TxRef { get; set; } = "";
        [JsonPropertyName("currency")] public string Currency { get; set; } = "";
    }

    private class FlwVerifyResponse
    {
        [JsonPropertyName("status")] public string Status { get; set; } = "";
        [JsonPropertyName("data")] public FlwVerifyData? Data { get; set; }
    }

    private class FlwVerifyData
    {
        [JsonPropertyName("status")] public string Status { get; set; } = "";
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("currency")] public string Currency { get; set; } = "";
        [JsonPropertyName("meta")] public FlwMeta? Meta { get; set; }
    }

    private class FlwMeta
    {
        [JsonPropertyName("order_number")] public string? OrderNumber { get; set; }
    }
}
