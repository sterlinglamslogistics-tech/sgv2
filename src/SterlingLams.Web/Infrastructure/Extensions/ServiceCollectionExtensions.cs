using Microsoft.Extensions.Caching.Memory;
using SterlingLams.Web.Services;
using SterlingLams.Web.Services.ERPNext;
using SterlingLams.Web.Services.Inventory;
using SterlingLams.Web.Services.Payment;

namespace SterlingLams.Web.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSterlingLamsServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ─── ERPNext ─────────────────────────────────────────────────────────
        var erpSettings = configuration.GetSection("ERPNext").Get<ERPNextSettings>()
            ?? throw new InvalidOperationException("ERPNext configuration is missing.");

        services.AddSingleton(erpSettings);

        services.AddHttpClient<IERPNextService, ERPNextService>(client =>
        {
            client.BaseAddress = new Uri(erpSettings.BaseUrl);
            client.DefaultRequestHeaders.Add(
                "Authorization",
                $"token {erpSettings.ApiKey}:{erpSettings.ApiSecret}");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // ─── Inventory ───────────────────────────────────────────────────────
        services.AddMemoryCache();
        services.AddScoped<IInventoryService, InventoryService>();
        services.AddScoped<IStockService, StockService>();

        // ─── Product Import ───────────────────────────────────────────────────
        services.AddScoped<IProductImportService, ProductImportService>();
        services.AddScoped<IWooCommerceImportService, WooCommerceImportService>();

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
