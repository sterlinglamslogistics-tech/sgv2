using Microsoft.AspNetCore.Identity;
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
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// ─── Identity ───────────────────────────────────────────────────────────────
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = true;
    options.Password.RequiredLength = 8;
    options.Password.RequireUppercase = false;
    options.SignIn.RequireConfirmedEmail = false;
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.SlidingExpiration = true;
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

// ─── Background Services ─────────────────────────────────────────────────────
builder.Services.AddHostedService<SterlingLams.Web.Infrastructure.InventorySyncHostedService>();

// ─── MVC ────────────────────────────────────────────────────────────────────
builder.Services.AddControllersWithViews()
    .AddJsonOptions(opts =>
        opts.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

builder.Services.AddHttpContextAccessor();

var app = builder.Build();

// ─── Middleware Pipeline ─────────────────────────────────────────────────────
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();

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
            // Production: run pending migrations automatically on startup.
            await db.Database.MigrateAsync();
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
// Replaces all website products with the CSV export and reconciles ERPNext
// (disables old Items, creates the imported ones), then exits without serving.
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

// Usage: dotnet run -- erpnext-ping  (tests the configured ERPNext connection)
if (args.Length >= 1 && args[0].Equals("erpnext-ping", StringComparison.OrdinalIgnoreCase))
{
    await SterlingLams.Web.Infrastructure.ErpNextPing.RunAsync(app.Services);
    Log.CloseAndFlush();
    return;
}

// Usage: dotnet run -- erpnext-push-items  (creates all website products as ERPNext Items)
if (args.Length >= 1 && args[0].Equals("erpnext-push-items", StringComparison.OrdinalIgnoreCase))
{
    await SterlingLams.Web.Infrastructure.WooMigrationRunner.PushAllItemsToErpNextAsync(app.Services);
    Log.CloseAndFlush();
    return;
}

await app.RunAsync();
