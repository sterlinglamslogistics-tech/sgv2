using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services;
using SterlingLams.Web.Services.ERPNext;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// One-off / on-demand migration: replaces all website products with a WooCommerce CSV export
/// and reconciles ERPNext — disabling the previous Items and creating the imported ones.
/// Invoked from the command line: <c>dotnet run -- migrate-woo "C:\path\to\export.csv"</c>.
/// </summary>
public static class WooMigrationRunner
{
    public static async Task RunAsync(IServiceProvider services, string csvPath)
    {
        using var scope = services.CreateScope();
        var sp     = scope.ServiceProvider;
        var db     = sp.GetRequiredService<ApplicationDbContext>();
        var woo    = sp.GetRequiredService<IWooCommerceImportService>();
        var erp    = sp.GetRequiredService<IERPNextService>();
        var logger = sp.GetRequiredService<ILogger<ApplicationDbContext>>();

        void Line(string msg) { Console.WriteLine(msg); logger.LogInformation("[migrate-woo] {Msg}", msg); }

        if (!File.Exists(csvPath))
        {
            Console.Error.WriteLine($"CSV file not found: {csvPath}");
            return;
        }

        Line($"Starting WooCommerce migration from: {csvPath}");

        // ── 1. Capture the codes of the products we're about to remove ──────────
        var oldCodes = await db.Products
            .Where(p => p.ErpNextItemCode != "")
            .Select(p => p.ErpNextItemCode)
            .Distinct()
            .ToListAsync();
        Line($"Found {oldCodes.Count} existing product(s) with ERPNext codes to disable.");

        // ── 2. Disable the old Items in ERPNext (safe alternative to deletion) ──
        int disabled = 0, disableFailed = 0;
        foreach (var code in oldCodes)
        {
            try
            {
                if (await erp.SetItemDisabledAsync(code, true)) { disabled++; Line($"  disabled ERPNext item: {code}"); }
                else { disableFailed++; Line($"  could NOT disable: {code}"); }
            }
            catch (Exception ex) { disableFailed++; Line($"  error disabling {code}: {ex.Message}"); }
        }
        Line($"ERPNext disable complete: {disabled} disabled, {disableFailed} failed.");

        // ── 3. Run the WooCommerce CSV import (wipes website products, loads CSV) ─
        Line("Running WooCommerce CSV import (this clears existing products first)…");
        await using (var fs = File.OpenRead(csvPath))
        {
            var result = await woo.ImportFromCsvAsync(fs);
            Line($"Import result: {result.Summary}");
            foreach (var e in result.Errors.Take(10)) Line($"  import error: {e}");
        }

        // ── 4. Create the newly-imported products as ERPNext Items ──────────────
        var newItems = await db.Products
            .Where(p => p.ErpNextItemCode != "")
            .Select(p => new { p.ErpNextItemCode, p.Name, p.Price, p.Description })
            .ToListAsync();
        Line($"Creating {newItems.Count} item(s) in ERPNext…");

        int created = 0, existed = 0, createFailed = 0;
        foreach (var p in newItems)
        {
            try
            {
                var (ok, error) = await erp.CreateItemAsync(new ERPNextNewItemRequest
                {
                    ItemCode     = p.ErpNextItemCode,
                    ItemName     = p.Name,
                    StandardRate = p.Price,
                    Description  = p.Description
                });
                if (ok) created++;
                else if (error == null) existed++;
                else { createFailed++; Line($"  create failed {p.ErpNextItemCode}: {error}"); }
            }
            catch (Exception ex) { createFailed++; Line($"  error creating {p.ErpNextItemCode}: {ex.Message}"); }
        }

        Line($"ERPNext create complete: {created} created, {existed} already existed, {createFailed} failed.");
        Line("Migration finished.");
    }

    /// <summary>
    /// Creates every website product (that has an ERPNext code) as an Item in the configured
    /// ERPNext instance. Existing items are left untouched. Use after pointing the config at a
    /// new/empty ERPNext. Usage: <c>dotnet run -- erpnext-push-items</c>.
    /// </summary>
    public static async Task PushAllItemsToErpNextAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db     = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var erp    = scope.ServiceProvider.GetRequiredService<IERPNextService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<ApplicationDbContext>>();

        var items = await db.Products
            .Where(p => p.ErpNextItemCode != "")
            .Select(p => new { p.ErpNextItemCode, p.Name, p.Price, p.Description })
            .ToListAsync();

        Console.WriteLine($"Pushing {items.Count} item(s) to ERPNext…");
        int created = 0, existed = 0, failed = 0;
        foreach (var p in items)
        {
            try
            {
                var (ok, error) = await erp.CreateItemAsync(new ERPNextNewItemRequest
                {
                    ItemCode = p.ErpNextItemCode, ItemName = p.Name, StandardRate = p.Price, Description = p.Description
                });
                if (ok) created++;
                else if (error == null) existed++;
                else { failed++; Console.WriteLine($"  failed {p.ErpNextItemCode}: {error}"); }
            }
            catch (Exception ex) { failed++; Console.WriteLine($"  error {p.ErpNextItemCode}: {ex.Message}"); }
        }
        Console.WriteLine($"Done: {created} created, {existed} already existed, {failed} failed.");
        logger.LogInformation("[erpnext-push-items] {Created} created, {Existed} existed, {Failed} failed.", created, existed, failed);
    }

    /// <summary>
    /// Decodes leftover HTML entities (e.g. &amp;#xD;&amp;#xA; newlines, &amp;#x2B; plus) in product
    /// descriptions that were imported before the importer decoded them. Website DB only — does not
    /// touch ERPNext. Usage: <c>dotnet run -- clean-product-text</c>.
    /// </summary>
    public static async Task CleanProductTextAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db     = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<ApplicationDbContext>>();

        static string? Decode(string? s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var d = System.Net.WebUtility.HtmlDecode(s).Replace("\r\n", "\n").Replace("\r", "\n");
            d = System.Text.RegularExpressions.Regex.Replace(d, @"[ \t]+", " ");
            d = System.Text.RegularExpressions.Regex.Replace(d, @"\n{3,}", "\n\n");
            return string.Join("\n", d.Split('\n').Select(l => l.TrimEnd())).Trim();
        }

        var products = await db.Products.ToListAsync();
        int changed = 0;
        foreach (var p in products)
        {
            var newDesc  = Decode(p.Description);
            var newShort = Decode(p.ShortDescription);
            if (newDesc != p.Description || newShort != p.ShortDescription)
            {
                p.Description = newDesc;
                p.ShortDescription = newShort;
                changed++;
            }
        }
        await db.SaveChangesAsync();
        Console.WriteLine($"Cleaned product text: {changed} of {products.Count} product(s) updated.");
        logger.LogInformation("[clean-product-text] {Changed}/{Total} products updated.", changed, products.Count);
    }
}
