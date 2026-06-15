using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stripe;

namespace SterlingLams.Web.Services.Payment;

public class StripeSettings
{
    public string SecretKey { get; set; } = string.Empty;
    public string PublishableKey { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
}

public class StripePaymentService : IPaymentService
{
    private readonly StripeSettings _settings;
    private readonly ILogger<StripePaymentService> _logger;

    public string ProviderName => "Stripe";

    public StripePaymentService(StripeSettings settings, ILogger<StripePaymentService> logger)
    {
        _settings = settings;
        _logger = logger;
        StripeConfiguration.ApiKey = settings.SecretKey;
    }

    public async Task<InitiatePaymentResult> InitiatePaymentAsync(InitiatePaymentRequest request)
    {
        try
        {
            // Convert to lowest denomination (kobo for NGN, cents for USD)
            var amountInKobo = (long)(request.Amount * 100);

            var options = new PaymentIntentCreateOptions
            {
                Amount = amountInKobo,
                Currency = request.Currency.ToLower(),
                ReceiptEmail = request.CustomerEmail,
                Metadata = new Dictionary<string, string>
                {
                    ["order_number"] = request.OrderNumber,
                    ["customer_name"] = request.CustomerName
                },
                Description = $"Sterlin Glams Order {request.OrderNumber}",
                AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions
                {
                    Enabled = true
                }
            };

            var service = new PaymentIntentService();
            var intent = await service.CreateAsync(options);

            // For Stripe, the "redirect URL" uses the client secret on a hosted page
            // We store the PaymentIntent ID as reference and redirect to a payment page
            return new InitiatePaymentResult
            {
                Success = true,
                // Stripe doesn't give a hosted checkout URL with PaymentIntents directly
                // Use Stripe Checkout Session for redirect flow instead
                AuthorizationUrl = $"/Checkout/StripeCheckout?intentId={intent.Id}",
                Reference = intent.Id
            };
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Stripe initiate failed for order {OrderNumber}", request.OrderNumber);
            return new InitiatePaymentResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// For Stripe, use Checkout Sessions which provide a hosted redirect URL.
    /// This alternative creates a Checkout Session.
    /// </summary>
    public async Task<InitiatePaymentResult> InitiateCheckoutSessionAsync(InitiatePaymentRequest request)
    {
        try
        {
            var options = new Stripe.Checkout.SessionCreateOptions
            {
                PaymentMethodTypes = new List<string> { "card" },
                Mode = "payment",
                CustomerEmail = request.CustomerEmail,
                SuccessUrl = request.CallbackUrl + "?session_id={CHECKOUT_SESSION_ID}",
                CancelUrl = request.CallbackUrl.Replace("/callback", "/cancelled"),
                LineItems = new List<Stripe.Checkout.SessionLineItemOptions>
                {
                    new()
                    {
                        PriceData = new Stripe.Checkout.SessionLineItemPriceDataOptions
                        {
                            Currency = request.Currency.ToLower(),
                            UnitAmount = (long)(request.Amount * 100),
                            ProductData = new Stripe.Checkout.SessionLineItemPriceDataProductDataOptions
                            {
                                Name = $"Sterlin Glams Order {request.OrderNumber}"
                            }
                        },
                        Quantity = 1
                    }
                },
                Metadata = new Dictionary<string, string>
                {
                    ["order_number"] = request.OrderNumber
                }
            };

            var service = new Stripe.Checkout.SessionService();
            var session = await service.CreateAsync(options);

            return new InitiatePaymentResult
            {
                Success = true,
                AuthorizationUrl = session.Url,
                Reference = session.Id
            };
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Stripe checkout session failed for order {OrderNumber}", request.OrderNumber);
            return new InitiatePaymentResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<VerifyPaymentResult> VerifyPaymentAsync(string reference)
    {
        try
        {
            // reference may be a session ID (cs_...) or payment intent ID (pi_...)
            if (reference.StartsWith("cs_"))
            {
                var sessionService = new Stripe.Checkout.SessionService();
                var session = await sessionService.GetAsync(reference);

                return new VerifyPaymentResult
                {
                    Success = session.PaymentStatus == "paid",
                    IsPaid = session.PaymentStatus == "paid",
                    Reference = reference,
                    AmountPaid = session.AmountTotal.HasValue ? session.AmountTotal.Value / 100m : 0,
                    OrderNumber = session.Metadata.TryGetValue("order_number", out var on) ? on : null
                };
            }
            else
            {
                var piService = new PaymentIntentService();
                var intent = await piService.GetAsync(reference);

                return new VerifyPaymentResult
                {
                    Success = intent.Status == "succeeded",
                    IsPaid = intent.Status == "succeeded",
                    Reference = reference,
                    AmountPaid = intent.Amount / 100m,
                    OrderNumber = intent.Metadata.TryGetValue("order_number", out var on) ? on : null
                };
            }
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Stripe verify failed for reference {Reference}", reference);
            return new VerifyPaymentResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public Task<RefundResult> RefundPaymentAsync(RefundPaymentRequest request)
        => Task.FromResult(new RefundResult
        {
            Success = false,
            Supported = false,
            ErrorMessage = "Automated Stripe refunds are not configured — refund via the Stripe dashboard."
        });

    public Task<bool> ValidateWebhookAsync(string payload, string signature)
    {
        try
        {
            // Stripe uses its own signature validation via EventUtility
            var stripeEvent = EventUtility.ConstructEvent(payload, signature, _settings.WebhookSecret);
            return Task.FromResult(stripeEvent != null);
        }
        catch (StripeException ex)
        {
            _logger.LogWarning(ex, "Stripe webhook validation failed");
            return Task.FromResult(false);
        }
    }
}
