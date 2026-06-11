using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

public class BarcodeImportResult
{
    public int RowsRead { get; set; }
    public int ProductsMatched { get; set; }
    public int SkusSkipped { get; set; }
    public int ProductBarcodes { get; set; }
    public int VariantBarcodes { get; set; }
    public int Unplaced { get; set; }
    public List<string> Errors { get; set; } = new();
    public string Summary =>
        $"{RowsRead} rows · {ProductsMatched} products matched · {ProductBarcodes} product barcodes · " +
        $"{VariantBarcodes} variant barcodes · {SkusSkipped} SKUs skipped (not in catalogue) · {Unplaced} barcodes unplaced";
}

/// <summary>
/// Imports EposNow barcodes from a CSV (sku,color,barcode). Matches each SKU to our
/// Product.Sku and assigns the barcode(s): the product's primary barcode + per-variant
/// barcodes (colour-matched first, then filling remaining variant slots). Because our scan
/// resolves any barcode to its parent product, every assigned barcode becomes scannable.
/// </summary>
public class BarcodeImportService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<BarcodeImportService> _log;
    public BarcodeImportService(ApplicationDbContext db, ILogger<BarcodeImportService> log)
    {
        _db = db;
        _log = log;
    }

    private static string Norm(string? s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    public async Task<BarcodeImportResult> ImportAsync(string csvPath, IProgress<string>? progress = null)
    {
        var r = new BarcodeImportResult();
        void Log(string m) { progress?.Report(m); _log.LogInformation("[barcode-import] {Msg}", m); }

        if (!File.Exists(csvPath)) { r.Errors.Add($"File not found: {csvPath}"); return r; }

        var rows = new List<(string Sku, string Color, string Barcode)>();
        foreach (var line in (await File.ReadAllLinesAsync(csvPath)).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var p = line.Split(',');
            if (p.Length < 3) continue;
            var sku = p[0].Trim(); var color = p[1].Trim(); var bc = p[2].Trim();
            if (sku.Length == 0 || bc.Length == 0) continue;
            rows.Add((sku, color, bc));
        }
        r.RowsRead = rows.Count;
        Log($"Read {rows.Count} barcode rows.");

        foreach (var grp in rows.GroupBy(x => x.Sku))
        {
            try
            {
                var product = await _db.Products
                    .Include(p => p.Variants).ThenInclude(v => v.AttributeValues).ThenInclude(av => av.Attribute)
                    .FirstOrDefaultAsync(p => p.Sku == grp.Key);
                if (product == null) { r.SkusSkipped++; continue; }
                r.ProductsMatched++;

                var list = grp.ToList();

                // Product-level barcode = first.
                product.Barcode = list[0].Barcode;
                product.UpdatedAt = DateTime.UtcNow;
                r.ProductBarcodes++;

                // Assign each row to a variant: colour-match first, then fill remaining slots.
                var used = new HashSet<int>();
                string VarColor(ProductVariant v) =>
                    Norm(v.AttributeValues.FirstOrDefault(av => av.Attribute != null && av.Attribute.Slug == "color")?.Value ?? v.Name);

                foreach (var row in list)
                {
                    var color = Norm(row.Color);
                    ProductVariant? v = null;
                    if (color.Length > 0)
                        v = product.Variants.FirstOrDefault(x => !used.Contains(x.Id) && VarColor(x) == color);
                    v ??= product.Variants.FirstOrDefault(x => !used.Contains(x.Id));
                    if (v != null) { v.Barcode = row.Barcode; used.Add(v.Id); r.VariantBarcodes++; }
                    else if (product.Variants.Count > 0) r.Unplaced++; // more rows than variant slots
                }

                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                var inner = ex; while (inner.InnerException != null) inner = inner.InnerException;
                r.Errors.Add($"SKU {grp.Key}: {inner.Message}");
                _log.LogWarning(ex, "Barcode import error for SKU {Sku}", grp.Key);
            }
        }

        Log($"Done: {r.Summary}");
        return r;
    }
}
