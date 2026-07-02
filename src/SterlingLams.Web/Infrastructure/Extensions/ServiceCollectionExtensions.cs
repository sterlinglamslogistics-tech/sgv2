using Microsoft.Extensions.Caching.Memory;
using SterlingLams.Web.Services;
using SterlingLams.Web.Services.Payment;

namespace SterlingLams.Web.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSterlingLamsServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ─── Inventory ───────────────────────────────────────────────────────
        services.AddMemoryCache();
        services.AddScoped<IStockService, StockService>();
        services.AddScoped<IOrderNumberService, OrderNumberService>();
        services.AddScoped<SeoDescriptionGenerator>();
        services.AddScoped<IOrderFulfilmentService, OrderFulfilmentService>();
        services.AddScoped<ITransferWorkflowService, TransferWorkflowService>();

        // ─── Merchandising (best sellers / trending / new arrivals / recently viewed) ──
        services.AddScoped<IMerchandisingService, MerchandisingService>();
        services.AddScoped<ILoyaltyService, LoyaltyService>();
        services.AddScoped<IGiftCardService, GiftCardService>();
        services.AddScoped<IStorefrontCache, StorefrontCache>();

        // ─── Logistics (Lagos delivery) integration — order push + delivered callback ──
        services.Configure<SterlingLams.Web.Services.Logistics.LogisticsOptions>(configuration.GetSection("Logistics"));
        services.AddHttpClient("logistics", c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddScoped<SterlingLams.Web.Services.Logistics.ILogisticsDispatchService, SterlingLams.Web.Services.Logistics.LogisticsDispatchService>();

        // ─── Marketing (campaigns / audiences) ────────────────────────────────
        services.AddScoped<SterlingLams.Web.Services.Marketing.IMarketingService, SterlingLams.Web.Services.Marketing.MarketingService>();
        services.AddScoped<IReferralService, ReferralService>();
        services.AddScoped<SterlingLams.Web.Services.Social.ISocialPublisher, SterlingLams.Web.Services.Social.NullSocialPublisher>();

        // ─── Store-level authorization (writes-only) ──────────────────────────
        services.AddScoped<IStoreAccessService, StoreAccessService>();

        // ─── Product Import (WooCommerce CSV) ─────────────────────────────────
        services.AddScoped<IWooCommerceImportService, WooCommerceImportService>();
        services.AddScoped<ICatalogImportService, CatalogImportService>();

        // ─── Site Settings ────────────────────────────────────────────────────
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<DeliveryZoneService>();

        // ─── Audit Log ────────────────────────────────────────────────────────
        services.AddScoped<IAuditService, AuditService>();

        // ─── Roles & Permissions ───────────────────────────────────────────────
        services.AddScoped<IPermissionService, PermissionService>();

        // ─── Discounts ──────────────────────────────────────────────────────────
        services.AddScoped<IDiscountService, DiscountService>();

        // ─── Payment ─────────────────────────────────────────────────────────
        var paymentProvider = configuration["Payment:Provider"] ?? "Paystack";

        switch (paymentProvider.ToLowerInvariant())
        {
            case "paystack":
                var paystackSettings = configuration.GetSection("Payment:Paystack").Get<PaystackSettings>()
                    ?? new PaystackSettings();
                services.AddSingleton(paystackSettings);
                services.AddHttpClient<IPaymentService, PaystackPaymentService>();
                break;

            case "stripe":
                var stripeSettings = configuration.GetSection("Payment:Stripe").Get<StripeSettings>()
                    ?? new StripeSettings();
                services.AddSingleton(stripeSettings);
                services.AddScoped<IPaymentService, StripePaymentService>();
                break;

            case "flutterwave":
                var flwSettings = configuration.GetSection("Payment:Flutterwave").Get<FlutterwaveSettings>()
                    ?? new FlutterwaveSettings();
                services.AddSingleton(flwSettings);
                services.AddHttpClient<IPaymentService, FlutterwavePaymentService>(client =>
                {
                    client.BaseAddress = new Uri(flwSettings.BaseUrl);
                });
                break;

            default:
                throw new InvalidOperationException($"Unknown payment provider: {paymentProvider}");
        }

        return services;
    }
}
