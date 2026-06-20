// ─────────────────────────────────────────────────────────────────────────────
//  Sterlin Glams Platform — storefront, admin back office & in-house inventory/POS
//  Developed by Dev Rapheal.
// ─────────────────────────────────────────────────────────────────────────────
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Serilog;
using SterlingLams.Web.Data;
using SterlingLams.Web.Infrastructure.Extensions;
using SterlingLams.Web.Models.Domain;

var builder = WebApplication.CreateBuilder(args);

// ─── Serilog ────────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/sterlinglams-.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

// ─── Database ───────────────────────────────────────────────────────────────
// Render/Heroku/Railway hand the database to the app as a postgres:// URL in DATABASE_URL.
// Convert it to the Npgsql key/value format; otherwise use ConnectionStrings:DefaultConnection.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
if (!string.IsNullOrWhiteSpace(databaseUrl))
{
    var uri = new Uri(databaseUrl);
    var creds = uri.UserInfo.Split(':', 2);
    connectionString = new Npgsql.NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.Port > 0 ? uri.Port : 5432,
        Database = uri.AbsolutePath.TrimStart('/'),
        Username = Uri.UnescapeDataString(creds[0]),
        Password = creds.Length > 1 ? Uri.UnescapeDataString(creds[1]) : string.Empty,
        SslMode = Npgsql.SslMode.Require
    }.ConnectionString;
}
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

// ─── Data Protection ──────────────────────────────────────────────────────────
// Persist keys so antiforgery tokens, auth cookies and other protected payloads survive
// app restarts/redeploys and are shared across instances. Without this, keys are ephemeral
// and every restart invalidates tokens/cookies (causing antiforgery failures and logouts).
// Path is configurable (DataProtection:KeysPath); defaults to App_Data/dp-keys under the
// content root. A fixed application name keeps keys valid even if the deploy path changes.
var dpKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (string.IsNullOrWhiteSpace(dpKeysPath))
    dpKeysPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "dp-keys");
Directory.CreateDirectory(dpKeysPath);
builder.Services.AddDataProtection()
    .SetApplicationName("SterlingLams")
    .PersistKeysToFileSystem(new DirectoryInfo(dpKeysPath));

// ─── Identity ───────────────────────────────────────────────────────────────
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    // Explicit, balanced password policy (applies to staff + customers). Requires a mix of
    // upper/lower/digit at 8+ chars with 4 distinct characters — strong without forcing a special
    // character (which is user-hostile and adds little once length + character classes are required).
    options.Password.RequiredLength = 8;
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequiredUniqueChars = 4;

    // Email confirmation is NOT enforced yet: existing users are unconfirmed and SMTP may be
    // unconfigured, so flipping this on would lock everyone out. The confirmation flow exists
    // (Register sends a link, AccountController.ConfirmEmail verifies it); enable enforcement only
    // once existing users are grandfathered (EmailConfirmed=true) and SMTP is live in production.
    options.SignIn.RequireConfirmedEmail = false;

    // Brute-force protection: lock the account after repeated failed logins.
    options.Lockout.AllowedForNewUsers = true;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.Cookie.HttpOnly = true;
    // Require HTTPS for the auth cookie outside Development (plain HTTP localhost has no TLS).
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.SlidingExpiration = true;

    // Staff/admin get a much shorter, non-persistent session than shoppers — a stolen or
    // shared back-office cookie shouldn't stay valid for a month. Customers keep the 30-day
    // sliding convenience above.

    options.Events ??= new Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationEvents();
    options.Events.OnSigningIn = ctx =>
    {
        string[] staffRoles = { "Admin", "Operations", "Sales", "Inventory", "Social Media" };
        if (ctx.Principal is not null && Array.Exists(staffRoles, r => ctx.Principal!.IsInRole(r)))
        {
            ctx.Properties.IsPersistent = false;
            ctx.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8);
        }
        return Task.CompletedTask;
    };
});

// ─── Caching ────────────────────────────────────────────────────────────────
var redisConn = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrEmpty(redisConn))
    builder.Services.AddStackExchangeRedisCache(opts => opts.Configuration = redisConn);
else
    builder.Services.AddMemoryCache();

// ─── Session ────────────────────────────────────────────────────────────────
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

// ─── Application Services ───────────────────────────────────────────────────
builder.Services.AddSterlingLamsServices(builder.Configuration);

// ─── Email (SMTP) ─────────────────────────────────────────────────────────────
builder.Services.Configure<SterlingLams.Web.Services.EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.AddScoped<SterlingLams.Web.Services.IEmailService, SterlingLams.Web.Services.SmtpEmailService>();
builder.Services.AddScoped<SterlingLams.Web.Services.BarcodeImportService>();

// ─── Rate limiting ────────────────────────────────────────────────────────────
// Per-IP throttle on auth & email-sending endpoints (brute-force / abuse protection).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", ctx =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

// ─── Background Services ─────────────────────────────────────────────────────
// Frees stock reserved by abandoned (unpaid) online orders so it returns to sale.
builder.Services.AddHostedService<SterlingLams.Web.Infrastructure.ReservationSweeper>();
// Retries paid-but-unfulfilled online orders (self-heals transient failures) and alerts the
// admin for ones that stay stuck (e.g. genuine stock shortage). See OP-2.
builder.Services.AddHostedService<SterlingLams.Web.Infrastructure.FulfilmentRetryService>();
builder.Services.AddHostedService<SterlingLams.Web.Infrastructure.LowStockAlertService>();
builder.Services.AddHostedService<SterlingLams.Web.Infrastructure.BackInStockNotifier>();
builder.Services.AddHostedService<SterlingLams.Web.Infrastructure.AbandonedCartService>();

// ─── MVC ────────────────────────────────────────────────────────────────────
builder.Services.AddControllersWithViews()
    .AddJsonOptions(opts =>
        opts.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

builder.Services.AddHttpContextAccessor();

var app = builder.Build();

// ─── Middleware Pipeline ─────────────────────────────────────────────────────
// Behind Render / any reverse proxy: honour X-Forwarded-Proto/For so the app knows the request
// actually came in over HTTPS. Without this, generated links + auth redirects come out as http://
// (the proxy forwards plain HTTP internally). Proxy IP is dynamic, so clear the known lists.
var forwardedOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                     | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
};
forwardedOptions.KnownNetworks.Clear();
forwardedOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedOptions);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// ─── Security headers ───────────────────────────────────────────────────────
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

    // Per-request CSP nonce for inline <script> blocks (read in views via Context.Items["csp-nonce"]).
    // Hex (not base64) so there are no +/= characters for Razor to HTML-encode — the attribute value
    // then matches the header byte-for-byte.
    var nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    context.Items["csp-nonce"] = nonce;

    // Content-Security-Policy. The public storefront uses a strict nonce-based script-src (no
    // 'unsafe-inline'), so injected inline scripts/handlers are blocked. Staff areas (/Admin,
    // /Inventory, /Till) still rely on inline handlers, so they keep 'unsafe-inline' for now.
    // style-src keeps 'unsafe-inline' (inline style attributes are pervasive + low-risk) + Google Fonts.
    var p = context.Request.Path;
    var staffArea = p.StartsWithSegments("/Admin") || p.StartsWithSegments("/Inventory") || p.StartsWithSegments("/Till") || p.StartsWithSegments("/Pos");
    var scriptSrc = staffArea ? "script-src 'self' 'unsafe-inline'" : $"script-src 'self' 'nonce-{nonce}'";

    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        scriptSrc + "; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
        "font-src 'self' https://fonts.gstatic.com; " +
        "img-src 'self' data: https:; " +
        "connect-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        // Allow the checkout form to redirect to the Paystack hosted payment page (the payment
        // callback returns to our own origin, covered by 'self'). Without this, CSP blocks the
        // cross-origin redirect to checkout.paystack.com and the user is never sent to pay.
        "form-action 'self' https://checkout.paystack.com https://*.paystack.com https://*.paystack.co; " +
        "frame-ancestors 'none'";
    await next();
});

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var req = ctx.Context.Request;
        // Content-addressed assets never change in place, so they're safe to cache hard for a year:
        //  • css/js carry a ?v=<content-hash> (asp-append-version) that changes when the file changes;
        //  • everything under /uploads is saved with a unique Guid filename (a replacement = new URL).
        var contentAddressed = req.Query.ContainsKey("v")
            || req.Path.StartsWithSegments("/uploads", StringComparison.OrdinalIgnoreCase);
        ctx.Context.Response.Headers.CacheControl = contentAddressed
            ? "public,max-age=31536000,immutable"
            : "public,max-age=86400"; // other/unversioned assets (e.g. favicon) — revalidate daily
    }
});
app.UseRouting();
app.UseRateLimiter();
app.UseSession();
// Track storefront origin + page views per session for order attribution (needs session).
app.UseMiddleware<SterlingLams.Web.Infrastructure.OrderAttributionMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

// Public storefront maintenance page (store.maintenance_mode). After auth so staff are exempt.
app.UseMiddleware<SterlingLams.Web.Infrastructure.MaintenanceModeMiddleware>();

// Friendly redirects for the staff-area roots — the area default controller is "Home", which
// doesn't exist, so a bare /Admin or /Inventory would 404. Send them to the real landing pages.
app.MapGet("/Admin", () => Results.Redirect("/Admin/Dashboard"));
app.MapGet("/Inventory", () => Results.Redirect("/Inventory/Overview"));

app.MapControllerRoute(
    name: "areas",
    pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapControllers(); // API controllers (WebhooksController)

// ─── DB Initialisation ───────────────────────────────────────────────────────
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<ApplicationDbContext>>();

    try
    {
        // In Production: expect migrations to have been run before deploy.
        // In Development: use EnsureCreated so the app works without `dotnet ef` installed.
        if (app.Environment.IsDevelopment())
        {
            // EnsureCreated creates all tables from the model — no migration files needed.
            // Switch to MigrateAsync once you've run `dotnet ef migrations add InitialCreate`.
            var created = await db.Database.EnsureCreatedAsync();
            if (created) logger.LogInformation("Database created from EF model (EnsureCreated).");
        }
        else
        {
            // Production: do NOT silently auto-migrate on startup — a bad migration would take the
            // site down, and concurrent instances could race applying them. Migrations should be
            // applied as a gated deploy step (`dotnet ef database update` or a migration bundle)
            // BEFORE the app starts. If there are unapplied migrations we fail fast with guidance.
            // Opt back into startup migration with Database:AutoMigrate=true if you really want it.
            var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
            if (pending.Count == 0)
            {
                logger.LogInformation("Database schema is up to date.");
            }
            else if (app.Configuration.GetValue<bool>("Database:AutoMigrate"))
            {
                logger.LogWarning("Database:AutoMigrate=true — applying {Count} pending migration(s) on startup: {List}",
                    pending.Count, string.Join(", ", pending));
                await db.Database.MigrateAsync();
            }
            else
            {
                throw new InvalidOperationException(
                    $"{pending.Count} pending database migration(s) not applied: {string.Join(", ", pending)}. " +
                    "Apply them as a deploy step (dotnet ef database update / migration bundle) before starting, " +
                    "or set Database:AutoMigrate=true to migrate on startup.");
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database initialisation failed. Check your connection string.");
        if (!app.Environment.IsDevelopment()) throw; // Fail fast in production
        logger.LogWarning("Continuing without database in Development mode. Some features will not work.");
    }
}

// Seed roles, stores, and categories (all environments)
try
{
    await SterlingLams.Web.Infrastructure.SeedData.SeedAsync(app.Services);

    // Seed product attributes (Colour, Alphabet, Size, Length, Combo) + admin user
    using var attrScope   = app.Services.CreateScope();
    var attrDb            = attrScope.ServiceProvider.GetRequiredService<SterlingLams.Web.Data.ApplicationDbContext>();
    var attrLogger        = attrScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var attrUserManager   = attrScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var attrRoleManager   = attrScope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    await SterlingLams.Web.Infrastructure.RoleSeedData.SeedAsync(attrRoleManager, attrDb, attrLogger);
    await SterlingLams.Web.Infrastructure.AttributeSeedData.SeedAdminUserAsync(attrUserManager, attrRoleManager, attrLogger);
    await SterlingLams.Web.Infrastructure.AttributeSeedData.SeedAsync(attrDb, attrLogger);
    await SterlingLams.Web.Infrastructure.SettingsSeedData.SeedAsync(attrDb, attrLogger);
}
catch (Exception ex)
{
    var seedLogger = app.Services.GetRequiredService<ILogger<Program>>();
    seedLogger.LogError(ex, "Seeding failed — database may not be available.");
}

// ─── CLI maintenance commands ────────────────────────────────────────────────
// Usage: dotnet run -- migrate-woo "C:\path\to\product-export.csv"
// Replaces all website products with the CSV export, then exits without serving.
if (args.Length >= 1 && args[0].Equals("migrate-woo", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: dotnet run -- migrate-woo \"<path-to-csv>\"");
        return;
    }
    await SterlingLams.Web.Infrastructure.WooMigrationRunner.RunAsync(app.Services, args[1]);
    Log.CloseAndFlush();
    return;
}

// Usage: dotnet run -- clean-product-text  (decodes leftover HTML entities in descriptions)
if (args.Length >= 1 && args[0].Equals("clean-product-text", StringComparison.OrdinalIgnoreCase))
{
    await SterlingLams.Web.Infrastructure.WooMigrationRunner.CleanProductTextAsync(app.Services);
    Log.CloseAndFlush();
    return;
}

// Usage: dotnet run -- import-catalog "<path-to-catalog.json>" [--upsert]
//   default        → WIPE all products then import (dev/first-time seeding; destroys order history)
//   --upsert       → match by code, UPDATE existing / INSERT new / DEACTIVATE missing (production-safe)
if (args.Length >= 1 && args[0].Equals("import-catalog", StringComparison.OrdinalIgnoreCase))
{
    var path = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? "";
    var upsert = args.Any(a => a.Equals("--upsert", StringComparison.OrdinalIgnoreCase));
    using var scope = app.Services.CreateScope();
    var svc = scope.ServiceProvider.GetRequiredService<SterlingLams.Web.Services.ICatalogImportService>();
    Console.WriteLine(upsert ? "Mode: UPSERT (production-safe, preserves order history)" : "Mode: WIPE + import");
    var res = await svc.ImportAsync(path, wipeFirst: !upsert, skipUncategorized: true, new Progress<string>(Console.WriteLine));
    Console.WriteLine("RESULT: " + res.Summary);
    foreach (var e in res.Errors.Take(25)) Console.WriteLine("  ERR: " + e);
    Log.CloseAndFlush();
    return;
}

// Usage: dotnet run -- import-barcodes "tools/barcode-import/eposnow_barcodes.csv"
// Matches EposNow barcodes (sku,color,barcode) to our products and assigns them.
if (args.Length >= 1 && args[0].Equals("import-barcodes", StringComparison.OrdinalIgnoreCase))
{
    var path = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? "";
    using var scope = app.Services.CreateScope();
    var svc = scope.ServiceProvider.GetRequiredService<SterlingLams.Web.Services.BarcodeImportService>();
    var res = await svc.ImportAsync(path, new Progress<string>(Console.WriteLine));
    Console.WriteLine("RESULT: " + res.Summary);
    foreach (var e in res.Errors.Take(25)) Console.WriteLine("  ERR: " + e);
    Log.CloseAndFlush();
    return;
}

await app.RunAsync();
