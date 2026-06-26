using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

public class ProductsController : InventoryAreaController
{
    private readonly ApplicationDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly SterlingLams.Web.Services.IStockService _stock;
    private readonly SterlingLams.Web.Services.IStoreAccessService _access;
    private const int PageSize = 30;
    public ProductsController(ApplicationDbContext db, IWebHostEnvironment env, IConfiguration config,
        SterlingLams.Web.Services.IStockService stock, SterlingLams.Web.Services.IStoreAccessService access)
    {
        _db = db;
        _env = env;
        _config = config;
        _stock = stock;
        _access = access;
    }

    // List — search matches name, SKU OR barcode (so a scanner finds the product). The "Current" tab
    // shows live products; "Archived" shows retired ones.
    public async Task<IActionResult> Index(string q = "", int page = 1, int? categoryId = null,
        string status = "all", string stock = "all", string sort = "name_asc",
        bool archived = false, int pageSize = 30, string variant = "all", string buttonColour = "")
    {
        ViewData["Title"] = "Products";
        if (pageSize != 30 && pageSize != 50 && pageSize != 100) pageSize = 30;
        var query = _db.Products.Include(p => p.Category).Include(p => p.Images)
            .Where(p => p.IsArchived == archived);

        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(p => EF.Functions.ILike(p.Name, $"%{q}%")
                                  || EF.Functions.ILike(p.Sku ?? "", $"%{q}%")
                                  || EF.Functions.ILike(p.Barcode ?? "", $"%{q}%")
                                  || p.Variants.Any(v => EF.Functions.ILike(v.Barcode ?? "", $"%{q}%")));

        if (categoryId.HasValue)
            query = query.Where(p => p.CategoryId == categoryId.Value);

        if (status == "active") query = query.Where(p => p.IsActive);
        else if (status == "hidden") query = query.Where(p => !p.IsActive);

        if (stock == "out") query = query.Where(p => (p.StoreInventories.Sum(si => (int?)si.QuantityOnHand) ?? 0) == 0);
        else if (stock == "low") query = query.Where(p => (p.StoreInventories.Sum(si => (int?)si.QuantityOnHand) ?? 0) <= Math.Max(1, p.LowStockThreshold));
        else if (stock == "in") query = query.Where(p => (p.StoreInventories.Sum(si => (int?)si.QuantityOnHand) ?? 0) > 0);

        if (variant == "variable") query = query.Where(p => p.ProductType == "variable");
        else if (variant == "simple") query = query.Where(p => p.ProductType != "variable");

        if (!string.IsNullOrWhiteSpace(buttonColour))
            query = buttonColour == "none"
                ? query.Where(p => p.PosButtonColour == null || p.PosButtonColour == "")
                : query.Where(p => p.PosButtonColour == buttonColour);

        query = sort switch
        {
            "name_desc" => query.OrderByDescending(p => p.Name),
            "barcode_asc" => query.OrderBy(p => p.Barcode),
            "barcode_desc" => query.OrderByDescending(p => p.Barcode),
            "category_asc" => query.OrderBy(p => p.Category != null ? p.Category.Name : ""),
            "category_desc" => query.OrderByDescending(p => p.Category != null ? p.Category.Name : ""),
            "price_asc" => query.OrderBy(p => p.Price),
            "price_desc" => query.OrderByDescending(p => p.Price),
            "stock_asc" => query.OrderBy(p => p.StoreInventories.Sum(si => (int?)si.QuantityOnHand) ?? 0),
            "stock_desc" => query.OrderByDescending(p => p.StoreInventories.Sum(si => (int?)si.QuantityOnHand) ?? 0),
            "status_asc" => query.OrderBy(p => p.IsActive),
            "status_desc" => query.OrderByDescending(p => p.IsActive),
            _ => query.OrderBy(p => p.Name)
        };

        var total = await query.CountAsync();
        var products = await query
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(p => new InvProductRow
            {
                Id = p.Id,
                Name = p.Name,
                Sku = p.Sku,
                Barcode = p.Barcode,
                Price = p.Price,
                CategoryName = p.Category != null ? p.Category.Name : "—",
                ImageUrl = p.Images.OrderBy(i => i.SortOrder).Select(i => i.Url).FirstOrDefault(),
                IsActive = p.IsActive,
                TrackStock = p.TrackStock,
                ButtonColour = p.PosButtonColour,
                Description = p.ShortDescription ?? p.Description,
                TotalStock = p.StoreInventories.Sum(si => (int?)si.QuantityOnHand) ?? 0,
                Variants = p.Variants.Where(v => v.IsActive).OrderBy(v => v.Name)
                    .Select(v => new InvVariantRow
                    {
                        Name = v.Name,
                        Sku = v.Sku,
                        Barcode = v.Barcode,
                        Price = p.Price + (v.PriceAdjustment ?? 0)
                    }).ToList()
            })
            .ToListAsync();

        await LoadCategories(categoryId);
        ViewBag.Query = q;
        ViewBag.Page = page;
        ViewBag.PageSize = pageSize;
        ViewBag.TotalPages = (int)Math.Ceiling(total / (double)pageSize);
        ViewBag.Total = total;
        ViewBag.FirstRow = total == 0 ? 0 : (page - 1) * pageSize + 1;
        ViewBag.LastRow = Math.Min(page * pageSize, total);
        ViewBag.CategoryId = categoryId;
        ViewBag.Status = status;
        ViewBag.Stock = stock;
        ViewBag.Sort = sort;
        ViewBag.Archived = archived;
        ViewBag.Variant = variant;
        ViewBag.ButtonColour = buttonColour;
        ViewBag.ButtonColours = await _db.Products
            .Where(p => p.PosButtonColour != null && p.PosButtonColour != "")
            .Select(p => p.PosButtonColour!).Distinct().OrderBy(c => c).ToListAsync();
        return View(products);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var product = await _db.Products
            .Include(p => p.Variants.OrderBy(v => v.Name))
            .FirstOrDefaultAsync(p => p.Id == id);
        if (product == null) return NotFound();
        ViewData["Title"] = "Edit Product";
        await LoadCategories(product.CategoryId);
        // Prev/Next product nav (by name order, within the current/Active list) for the header arrows.
        ViewBag.PrevId = await _db.Products.Where(p => !p.IsArchived && p.Name.CompareTo(product.Name) < 0)
            .OrderByDescending(p => p.Name).Select(p => (int?)p.Id).FirstOrDefaultAsync();
        ViewBag.NextId = await _db.Products.Where(p => !p.IsArchived && p.Name.CompareTo(product.Name) > 0)
            .OrderBy(p => p.Name).Select(p => (int?)p.Id).FirstOrDefaultAsync();
        return View(product);
    }

    // Delete a single variant (X in the Product Matrix). Blocked if it carries order/stock history.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteVariant(int id, int productId)
    {
        var v = await _db.ProductVariants.FirstOrDefaultAsync(x => x.Id == id && x.ProductId == productId);
        if (v == null) return NotFound();
        var hasHistory = await _db.OrderItems.AnyAsync(oi => oi.ProductVariantId == id)
                       || await _db.StockMovements.AnyAsync(m => m.ProductVariantId == id);
        if (hasHistory)
        {
            TempData["Error"] = $"Variant '{v.Name}' has order or stock history and can't be deleted.";
            return RedirectToAction(nameof(Edit), new { id = productId });
        }
        var name = v.Name;
        _db.StoreInventories.RemoveRange(_db.StoreInventories.Where(si => si.ProductVariantId == id));
        _db.ProductVariants.Remove(v);
        await _db.SaveChangesAsync();
        await LogAsync("Delete", "Product", productId.ToString(), $"Deleted variant '{name}'");
        TempData["Success"] = $"Variant '{name}' deleted.";
        return RedirectToAction(nameof(Edit), new { id = productId });
    }

    // Save per-variant barcodes (parallel arrays from the variants table).
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveVariants(int productId, int[] variantId, string[] barcode)
    {
        var variants = await _db.ProductVariants.Where(v => v.ProductId == productId).ToListAsync();
        for (int i = 0; variantId != null && i < variantId.Length; i++)
        {
            var v = variants.FirstOrDefault(x => x.Id == variantId[i]);
            if (v != null)
                v.Barcode = (barcode != null && i < barcode.Length && !string.IsNullOrWhiteSpace(barcode[i]))
                    ? barcode[i].Trim() : null;
        }
        await _db.SaveChangesAsync();
        await LogAsync("Update", "Product", productId.ToString(), "Updated variant barcodes");
        TempData["Success"] = "Variant barcodes saved.";
        return RedirectToAction(nameof(Edit), new { id = productId });
    }

    public async Task<IActionResult> Create()
    {
        ViewData["Title"] = "New Product";
        await LoadCategories(null);
        return View("Edit", new Product { IsActive = true, Currency = "NGN", LowStockThreshold = 3 });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(int id, string name, string? sku, string? barcode, decimal price,
        int? categoryId, int lowStockThreshold, bool isActive, string? description, string? buttonColour)
    {
        if (string.IsNullOrWhiteSpace(name) || categoryId == null)
        {
            TempData["Error"] = string.IsNullOrWhiteSpace(name) ? "Name is required." : "Please choose a category.";
            return RedirectToAction(id == 0 ? nameof(Create) : nameof(Edit), id == 0 ? null : new { id });
        }

        var isNew = id == 0;
        var product = isNew ? new Product() : await _db.Products.FindAsync(id);
        if (product == null) return NotFound();

        product.Name = name.Trim();
        product.Sku = string.IsNullOrWhiteSpace(sku) ? null : sku.Trim();
        product.Barcode = string.IsNullOrWhiteSpace(barcode) ? null : barcode.Trim();
        product.Price = price;
        product.CategoryId = categoryId.Value;
        product.LowStockThreshold = lowStockThreshold;
        product.IsActive = isActive;
        product.PosButtonColour = string.IsNullOrWhiteSpace(buttonColour) ? null : buttonColour.Trim();
        product.Description = SterlingLams.Web.Services.ProductHtml.Sanitize(description);
        product.UpdatedAt = DateTime.UtcNow;

        if (isNew)
        {
            product.Currency = "NGN";
            product.ProductType = "simple";
            product.ExternalCode = "";
            product.CreatedAt = DateTime.UtcNow;
            product.Slug = await UniqueSlugAsync(Slugify(name));
            _db.Products.Add(product);
        }

        await _db.SaveChangesAsync();
        await EnsureInventoryRecordsAsync(product.Id);
        await LogAsync(isNew ? "Create" : "Update", "Product", product.Id.ToString(),
            $"{(isNew ? "Created" : "Updated")} product '{product.Name}'" + (string.IsNullOrEmpty(product.Barcode) ? "" : $" (barcode {product.Barcode})"));

        TempData["Success"] = $"'{product.Name}' saved.";
        return RedirectToAction(nameof(Index));
    }

    // Quick Add — create a minimal product (name + category + price) from the list modal.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> QuickAdd(string name, int? categoryId, decimal price, string? sku)
    {
        if (string.IsNullOrWhiteSpace(name) || categoryId == null)
        {
            TempData["Error"] = "Name and category are required.";
            return RedirectToAction(nameof(Index));
        }
        var product = new Product
        {
            Name = name.Trim(),
            Sku = string.IsNullOrWhiteSpace(sku) ? null : sku.Trim(),
            Price = price,
            CategoryId = categoryId.Value,
            IsActive = true, TrackStock = true, Currency = "NGN", ProductType = "simple",
            ExternalCode = "", LowStockThreshold = 3,
            Slug = await UniqueSlugAsync(Slugify(name)),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        _db.Products.Add(product);
        await _db.SaveChangesAsync();
        await EnsureInventoryRecordsAsync(product.Id);
        await LogAsync("Create", "Product", product.Id.ToString(), $"Quick-added product '{product.Name}'");
        TempData["Success"] = $"'{product.Name}' added.";
        return RedirectToAction(nameof(Index));
    }

    // Archive / restore — moves a product between the Current and Archived tabs.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetArchived(int id, bool archived)
    {
        var p = await _db.Products.FindAsync(id);
        if (p == null) return NotFound();
        p.IsArchived = archived;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await LogAsync("Update", "Product", id.ToString(), (archived ? "Archived" : "Restored") + $" product '{p.Name}'");
        TempData["Success"] = archived ? $"'{p.Name}' archived." : $"'{p.Name}' restored.";
        return RedirectToAction(nameof(Index), new { archived = !archived });
    }

    // Toggle stock tracking for a product.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleTrackStock(int id)
    {
        var p = await _db.Products.FindAsync(id);
        if (p == null) return NotFound();
        p.TrackStock = !p.TrackStock;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await LogAsync("Update", "Product", id.ToString(), $"Set Track Stock = {p.TrackStock} for '{p.Name}'");
        TempData["Success"] = $"Stock tracking {(p.TrackStock ? "on" : "off")} for '{p.Name}'.";
        return RedirectToAction(nameof(Index));
    }

    // Duplicate a product (copy core fields; inactive + not archived; fresh slug).
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Duplicate(int id)
    {
        var src = await _db.Products.FindAsync(id);
        if (src == null) return NotFound();
        var copy = new Product
        {
            Name = src.Name + " (copy)", Sku = null, Barcode = null,
            Price = src.Price, SalePrice = src.SalePrice, CategoryId = src.CategoryId,
            Description = src.Description, ShortDescription = src.ShortDescription,
            Material = src.Material, Metal = src.Metal, GemstoneType = src.GemstoneType,
            Carat = src.Carat, Weight = src.Weight, ProductType = "simple",
            Currency = src.Currency, ExternalCode = "", LowStockThreshold = src.LowStockThreshold,
            TrackStock = src.TrackStock, PosButtonColour = src.PosButtonColour,
            IsActive = false, IsArchived = false,
            Slug = await UniqueSlugAsync(Slugify(src.Name) + "-copy"),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        _db.Products.Add(copy);
        await _db.SaveChangesAsync();
        await EnsureInventoryRecordsAsync(copy.Id);
        await LogAsync("Create", "Product", copy.Id.ToString(), $"Duplicated product '{src.Name}'");
        TempData["Success"] = $"Duplicated as '{copy.Name}' (inactive — edit & activate when ready).";
        return RedirectToAction(nameof(Edit), new { id = copy.Id });
    }

    // Delete — blocked if the product has order/stock history (deactivate/archive instead).
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var p = await _db.Products.FindAsync(id);
        if (p == null) return NotFound();
        var hasHistory = await _db.OrderItems.AnyAsync(oi => oi.ProductId == id)
                       || await _db.StockMovements.AnyAsync(m => m.ProductId == id);
        if (hasHistory)
        {
            TempData["Error"] = $"'{p.Name}' has order or stock history and can't be deleted — archive it instead.";
            return RedirectToAction(nameof(Index));
        }
        var name = p.Name;
        _db.StoreInventories.RemoveRange(_db.StoreInventories.Where(si => si.ProductId == id));
        _db.ProductVariants.RemoveRange(_db.ProductVariants.Where(v => v.ProductId == id));
        _db.Products.Remove(p);
        await _db.SaveChangesAsync();
        await LogAsync("Delete", "Product", id.ToString(), $"Deleted product '{name}'");
        TempData["Success"] = $"'{name}' deleted.";
        return RedirectToAction(nameof(Index));
    }

    // CSV export of the current view (respects search / category / tab).
    public async Task<IActionResult> Export(string q = "", int? categoryId = null, bool archived = false)
    {
        var query = _db.Products.Include(p => p.Category).Where(p => p.IsArchived == archived);
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(p => EF.Functions.ILike(p.Name, $"%{q}%") || EF.Functions.ILike(p.Sku ?? "", $"%{q}%") || EF.Functions.ILike(p.Barcode ?? "", $"%{q}%"));
        if (categoryId.HasValue) query = query.Where(p => p.CategoryId == categoryId.Value);
        var rows = await query.OrderBy(p => p.Name)
            .Select(p => new { p.Name, p.Sku, p.Barcode, Category = p.Category != null ? p.Category.Name : "", p.Price, p.IsActive, p.TrackStock })
            .ToListAsync();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Name,SKU,Barcode,Category,Price,Active,TrackStock");
        foreach (var r in rows)
            sb.AppendLine($"\"{r.Name}\",\"{r.Sku}\",\"{r.Barcode}\",\"{r.Category}\",{r.Price},{r.IsActive},{r.TrackStock}");
        var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv", $"products_{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    // Generate Labels builder — pick products (search/scan or bulk by location/category),
    // set quantities + which details to print, then generate the printable sheet.
    public async Task<IActionResult> GenerateLabels()
    {
        ViewData["Title"] = "Generate Labels";
        await LoadCategories(null);
        ViewBag.Stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        return View();
    }

    // Printable barcode label sheet. Accepts an explicit pick list ("pid:qty,…") and/or a bulk
    // selection (all products in a category and/or in stock at a location). Detail flags choose
    // which fields print. Products with per-variant barcodes get one label per variant.
    public async Task<IActionResult> Labels(string? ids, int? categoryId, int? storeId, int qty = 1,
        bool name = true, bool price = true, bool barcode = true, bool category = false,
        bool description = false, string? customText = null, string preset = "barcode")
    {
        if (qty < 1) qty = 1;
        if (qty > 200) qty = 200;

        // Explicit picks "pid:qty,pid" (qty optional → defaults to the page qty).
        var qtyById = new Dictionary<int, int>();
        foreach (var part in (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = part.Split(':');
            if (!int.TryParse(bits[0], out var pid) || pid <= 0) continue;
            var q = bits.Length > 1 && int.TryParse(bits[1], out var qq) && qq > 0 ? qq : qty;
            qtyById[pid] = qtyById.TryGetValue(pid, out var ex) ? ex + q : q;
        }

        // Bulk: every active product in the chosen category and/or in stock at the chosen location.
        if (categoryId.HasValue || storeId.HasValue)
        {
            var bulk = _db.Products.Where(p => p.IsActive);
            if (categoryId.HasValue) bulk = bulk.Where(p => p.CategoryId == categoryId.Value);
            if (storeId.HasValue) bulk = bulk.Where(p => p.StoreInventories.Any(si => si.StoreId == storeId.Value && si.QuantityOnHand > 0));
            foreach (var pid in await bulk.Select(p => p.Id).ToListAsync())
                if (!qtyById.ContainsKey(pid)) qtyById[pid] = qty;
        }

        var idList = qtyById.Keys.ToList();
        var products = await _db.Products.Include(p => p.Variants).Include(p => p.Category)
            .Where(p => idList.Contains(p.Id)).OrderBy(p => p.Name).ToListAsync();

        var rows = new List<LabelRow>();
        foreach (var p in products)
        {
            var copies = qtyById[p.Id];
            var catName = p.Category?.Name ?? "";
            var desc = p.ShortDescription ?? p.Description;
            var variants = p.Variants.Where(v => v.IsActive && !string.IsNullOrEmpty(v.Barcode))
                .OrderBy(v => v.Name).ToList();
            var labels = variants.Count > 0
                ? variants.Select(v => new LabelRow { Name = $"{p.Name} – {v.Name}", Price = p.Price + (v.PriceAdjustment ?? 0), Code = v.Barcode!, Category = catName, Description = desc }).ToList()
                : new List<LabelRow> { new() { Name = p.Name, Price = p.Price, Code = p.Barcode ?? p.Sku ?? ("P" + p.Id), Category = catName, Description = desc } };
            for (var i = 0; i < copies; i++) rows.AddRange(labels);
        }

        ViewData["Title"] = "Barcode Labels";
        ViewBag.ShowName = name; ViewBag.ShowPrice = price; ViewBag.ShowBarcode = barcode;
        ViewBag.ShowCategory = category; ViewBag.ShowDescription = description;
        ViewBag.CustomText = customText; ViewBag.Preset = preset;
        return View(rows);
    }

    // Full stock-movement ledger for one product (every sale / adjustment / transfer).
    public async Task<IActionResult> History(int id)
    {
        var product = await _db.Products.FindAsync(id);
        if (product == null) return NotFound();
        ViewData["Title"] = "Stock history";
        ViewBag.ProductName = product.Name;
        ViewBag.ProductId = id;
        var moves = await _db.StockMovements.Where(m => m.ProductId == id)
            .Include(m => m.Store)
            .OrderByDescending(m => m.Id).Take(300)
            .Select(m => new MovementHistoryRow
            {
                Date = m.CreatedAt,
                Store = m.Store.Name.Replace("Sterlin Glams ", ""),
                Type = m.Type.ToString(),
                Change = m.QuantityChange,
                Balance = m.BalanceAfter,
                Reference = m.Reference,
                Note = m.Note
            })
            .ToListAsync();
        return View(moves);
    }

    // Look up a product by exact barcode (for scan boxes). Returns id/name or 404.
    [HttpGet]
    public async Task<IActionResult> Lookup(string barcode)
    {
        barcode = (barcode ?? "").Trim();
        if (barcode.Length == 0) return Json(new { found = false });
        var p = await _db.Products
            .Where(x => x.Barcode == barcode || x.Sku == barcode
                     || x.Variants.Any(v => v.Barcode == barcode || v.Sku == barcode))
            .Select(x => new { x.Id, x.Name, x.Sku, x.Barcode })
            .FirstOrDefaultAsync();
        return p == null ? Json(new { found = false }) : Json(new { found = true, id = p.Id, name = p.Name });
    }

    // ── Quick-Edit slide-in modal (Product List "Edit") ─────────────────────────

    // Returns the editable fields for one product as JSON (populates the Edit modal).
    [HttpGet]
    public async Task<IActionResult> EditData(int id)
    {
        var p = await _db.Products
            .Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id, x.Name, x.CategoryId, x.Price, x.Barcode, x.Sku,
                buttonColour = x.PosButtonColour,
                imageUrl = x.Images.OrderBy(i => i.SortOrder).Select(i => i.Url).FirstOrDefault()
            })
            .FirstOrDefaultAsync();
        return p == null ? Json(new { found = false }) : Json(new { found = true, p.Id, p.Name, p.CategoryId, p.Price, p.Barcode, p.Sku, p.buttonColour, p.imageUrl });
    }

    // Inline save from the Edit modal — core fields only; returns the refreshed row values.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> QuickEdit(int id, string name, int? categoryId, decimal price, string? buttonColour, string? barcode)
    {
        var p = await _db.Products.FindAsync(id);
        if (p == null) return Json(new { ok = false, error = "Product not found." });
        if (string.IsNullOrWhiteSpace(name)) return Json(new { ok = false, error = "Name is required." });
        if (categoryId == null) return Json(new { ok = false, error = "Please choose a category." });

        var trimmedBarcode = string.IsNullOrWhiteSpace(barcode) ? null : barcode.Trim();
        if (trimmedBarcode != null &&
            await _db.Products.AnyAsync(x => x.Id != id && x.Barcode == trimmedBarcode))
            return Json(new { ok = false, error = $"Barcode '{trimmedBarcode}' is already used by another product." });

        p.Name = name.Trim();
        p.CategoryId = categoryId.Value;
        p.Price = price;
        p.PosButtonColour = string.IsNullOrWhiteSpace(buttonColour) ? null : buttonColour.Trim();
        p.Barcode = trimmedBarcode;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await LogAsync("Update", "Product", id.ToString(), $"Quick-edited product '{p.Name}'");

        var catName = await _db.Categories.Where(c => c.Id == p.CategoryId).Select(c => c.Name).FirstOrDefaultAsync() ?? "—";
        return Json(new
        {
            ok = true, id = p.Id, name = p.Name, sku = p.Sku,
            displayName = string.IsNullOrWhiteSpace(p.Sku) ? p.Name : $"{p.Sku} ({p.Name})",
            categoryName = catName,
            priceText = "₦" + p.Price.ToString("N2"),
            buttonColour = p.PosButtonColour ?? "",
            barcode = p.Barcode ?? ""
        });
    }

    // Generate a unique 12-digit barcode not already used by any product or variant.
    [HttpGet]
    public async Task<IActionResult> GenerateBarcode()
    {
        var rng = new Random();
        for (var attempt = 0; attempt < 25; attempt++)
        {
            var code = "";
            for (var i = 0; i < 12; i++) code += rng.Next(0, 10);
            var taken = await _db.Products.AnyAsync(p => p.Barcode == code)
                     || await _db.ProductVariants.AnyAsync(v => v.Barcode == code);
            if (!taken) return Json(new { ok = true, barcode = code });
        }
        return Json(new { ok = false, error = "Could not generate a unique barcode — try again." });
    }

    // Upload a product image from the modal (Cloudinary when configured, else local disk in dev).
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadImage(int id, IFormFile file)
    {
        var p = await _db.Products.Include(x => x.Images).FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return Json(new { ok = false, error = "Product not found." });
        if (file == null || file.Length == 0) return Json(new { ok = false, error = "No file provided." });
        if (file.Length > 10 * 1024 * 1024) return Json(new { ok = false, error = "File too large (max 10 MB)." });
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var allowed = new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" };
        if (!allowed.Contains(ext)) return Json(new { ok = false, error = "Invalid type — use JPG, PNG, WEBP or GIF." });

        string url;
        var cloudName = _config["Cloudinary:CloudName"];
        var apiKey = _config["Cloudinary:ApiKey"];
        var apiSecret = _config["Cloudinary:ApiSecret"];
        if (!string.IsNullOrWhiteSpace(cloudName) && !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(apiSecret))
        {
            var cloudinary = new CloudinaryDotNet.Cloudinary(new CloudinaryDotNet.Account(cloudName, apiKey, apiSecret)) { Api = { Secure = true } };
            await using var s = file.OpenReadStream();
            var result = await cloudinary.UploadAsync(new CloudinaryDotNet.Actions.ImageUploadParams
            {
                File = new CloudinaryDotNet.FileDescription(file.FileName, s),
                Folder = "sterlinglams/products",
                PublicId = Guid.NewGuid().ToString("N"),
                UniqueFilename = false, Overwrite = false
            });
            if (result.StatusCode != System.Net.HttpStatusCode.OK || result.SecureUrl == null)
                return Json(new { ok = false, error = "Image upload failed — try again." });
            url = result.SecureUrl.ToString();
        }
        else
        {
            var dir = Path.Combine(_env.WebRootPath, "uploads", "products");
            Directory.CreateDirectory(dir);
            var fileName = $"{Guid.NewGuid():N}{ext}";
            await using var stream = System.IO.File.Create(Path.Combine(dir, fileName));
            await file.CopyToAsync(stream);
            url = $"/uploads/products/{fileName}";
        }

        var isFirst = !p.Images.Any();
        _db.ProductImages.Add(new ProductImage
        {
            ProductId = p.Id, Url = url,
            SortOrder = isFirst ? 0 : p.Images.Max(i => i.SortOrder) + 1,
            IsPrimary = isFirst
        });
        await _db.SaveChangesAsync();
        await LogAsync("Update", "Product", id.ToString(), $"Added image to '{p.Name}'");
        return Json(new { ok = true, url });
    }

    // ── Track Stock slide-in modal (per-location stock editor) ──────────────────

    // Per-location stock levels for the Track Stock modal (product-pool rows).
    [HttpGet]
    public async Task<IActionResult> TrackStockData(int id)
    {
        var p = await _db.Products.FindAsync(id);
        if (p == null) return Json(new { found = false });
        var stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        var inv = await _db.StoreInventories
            .Where(si => si.ProductId == id && si.ProductVariantId == null)
            .ToDictionaryAsync(si => si.StoreId);
        var locations = stores.Select(s =>
        {
            inv.TryGetValue(s.Id, out var r);
            return new
            {
                storeId = s.Id,
                name = s.Name,
                onHand = r?.QuantityOnHand ?? 0,
                min = r?.MinStock,
                max = r?.MaxStock,
                onOrder = r?.OnOrder ?? 0,
                alerts = r?.StockAlerts ?? true
            };
        }).ToList();
        var staff = await StaffOptionsAsync();

        return Json(new
        {
            found = true,
            id = p.Id,
            name = p.Name,
            trackStock = p.TrackStock,
            defaultMin = p.LowStockThreshold,
            reasons = SterlingLams.Web.Models.Domain.AdjustmentReasons.All,
            staff,
            locations
        });
    }

    public class TrackStockLoc
    {
        public int StoreId { get; set; }
        public int OnHand { get; set; }
        public int? Min { get; set; }
        public int? Max { get; set; }
        public int OnOrder { get; set; }
        public bool Alerts { get; set; }
        public string? Reason { get; set; }   // optional per-location override
    }
    public class TrackStockSaveRequest
    {
        public int Id { get; set; }
        public bool TrackStock { get; set; }
        public string? Reason { get; set; }
        public string? StaffUserId { get; set; }
        public List<TrackStockLoc> Locations { get; set; } = new();
    }

    // Save the Track Stock modal: on-hand changes go through the ledger (one BSA header per
    // branch, like the Stock grid); Min/Max/On-order/Alerts are persisted on the location row.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveTrackStock([FromBody] TrackStockSaveRequest req)
    {
        if (req == null) return Json(new { ok = false, error = "No data." });
        var p = await _db.Products.FindAsync(req.Id);
        if (p == null) return Json(new { ok = false, error = "Product not found." });

        var defaultReason = string.IsNullOrWhiteSpace(req.Reason) ? "Correction" : req.Reason.Trim();
        // Staff member: the chosen user (must be a real, non-guest user) records who did the count;
        // fall back to the logged-in user.
        var actingUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var userId = actingUserId;
        if (await IsStaffAsync(req.StaffUserId))
            userId = req.StaffUserId;
        var validStoreIds = (await _db.Stores.Where(s => s.IsActive).Select(s => s.Id).ToListAsync()).ToHashSet();
        var locs = req.Locations.Where(l => validStoreIds.Contains(l.StoreId)).ToList();

        // Writes are branch-scoped: only allow edits to the user's assigned branch(es).
        var writable = await _access.WritableStoreIdsAsync(User);
        if (locs.Any(l => !writable.Contains(l.StoreId)))
            return Json(new { ok = false, error = "You can only edit stock for your assigned branch(es)." });

        p.TrackStock = req.TrackStock;
        p.UpdatedAt = DateTime.UtcNow;

        var applied = 0;
        await using var tx = await _db.Database.BeginTransactionAsync();

        if (_db.Database.IsNpgsql())
            foreach (var sid in locs.Select(l => l.StoreId).Distinct().OrderBy(x => x))
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"StoreInventories\" WHERE \"ProductId\" = {req.Id} AND \"StoreId\" = {sid} FOR UPDATE");

        var seq = await NextAdjustmentSeqAsync();
        foreach (var l in locs)
        {
            // A per-location reason overrides the all-locations default (each branch = its own BSA header).
            var reason = string.IsNullOrWhiteSpace(l.Reason) ? defaultReason : l.Reason.Trim();
            var type = SterlingLams.Web.Models.Domain.AdjustmentReasons.MovementType(reason);
            var current = await _stock.GetStockAsync(req.Id, null, l.StoreId, fallback: false);
            var delta = l.OnHand - current;
            if (delta != 0)
            {
                var adjNo = $"BSA{seq++:D5}";
                var balance = await _stock.ApplyAsync(req.Id, null, l.StoreId, delta, type, adjNo, note: reason, userId: userId);
                _db.StockAdjustments.Add(new StockAdjustment
                {
                    AdjustmentNumber = adjNo, StoreId = l.StoreId, Reason = reason, Source = "Form",
                    CreatedByUserId = userId, CreatedAt = DateTime.UtcNow,
                    Lines = { new StockAdjustmentLine { ProductId = req.Id, ProductName = p.Name, QtyDelta = delta, BalanceAfter = balance } }
                });
                applied++;
            }

            // Persist the reorder fields on the (pool) location row, creating it if needed.
            var row = await _db.StoreInventories
                .FirstOrDefaultAsync(si => si.ProductId == req.Id && si.StoreId == l.StoreId && si.ProductVariantId == null);
            if (row == null)
            {
                row = new StoreInventory { ProductId = req.Id, StoreId = l.StoreId };
                _db.StoreInventories.Add(row);
            }
            row.MinStock = l.Min;
            row.MaxStock = l.Max;
            row.OnOrder = Math.Max(0, l.OnOrder);
            row.StockAlerts = l.Alerts;
            row.UpdatedAt = DateTime.UtcNow;
        }

        try
        {
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Json(new { ok = false, error = "Stock changed while saving — refresh and try again." });
        }

        await LogAsync("Update", "Product", req.Id.ToString(), $"Track Stock update for '{p.Name}' ({applied} stock change(s))");
        return Json(new { ok = true, applied, trackStock = p.TrackStock });
    }

    private async Task<int> NextAdjustmentSeqAsync()
    {
        var last = await _db.StockAdjustments.OrderByDescending(a => a.Id)
            .Select(a => a.AdjustmentNumber).FirstOrDefaultAsync();
        return last != null && last.StartsWith("BSA") && int.TryParse(last[3..], out var n) ? n + 1 : 1;
    }

    // Typeahead for the Generate Labels builder — name / SKU / barcode, top matches.
    [HttpGet]
    public async Task<IActionResult> LabelSearch(string q)
    {
        q = (q ?? "").Trim();
        if (q.Length < 2) return Json(Array.Empty<object>());
        var rows = await _db.Products
            .Where(p => p.IsActive && (EF.Functions.ILike(p.Name, $"%{q}%")
                     || EF.Functions.ILike(p.Sku ?? "", $"%{q}%")
                     || EF.Functions.ILike(p.Barcode ?? "", $"%{q}%")))
            .OrderBy(p => p.Name).Take(15)
            .Select(p => new { id = p.Id, name = p.Name, sku = p.Sku })
            .ToListAsync();
        return Json(rows);
    }

    private async Task LoadCategories(int? selected)
    {
        ViewBag.Categories = await _db.Categories.OrderBy(c => c.Name)
            .Select(c => new SelectListItem { Value = c.Id.ToString(), Text = c.Name, Selected = c.Id == selected })
            .ToListAsync();
    }

    private async Task EnsureInventoryRecordsAsync(int productId)
    {
        var storeIds = await _db.Stores.Where(s => s.IsActive).Select(s => s.Id).ToListAsync();
        var existing = await _db.StoreInventories.Where(si => si.ProductId == productId).Select(si => si.StoreId).ToListAsync();
        foreach (var sid in storeIds.Except(existing))
            _db.StoreInventories.Add(new StoreInventory { ProductId = productId, StoreId = sid, QuantityOnHand = 0 });
        await _db.SaveChangesAsync();
    }

    private static string Slugify(string s)
    {
        s = (s ?? "").ToLowerInvariant().Trim();
        s = Regex.Replace(s, @"[^a-z0-9\s-]", "");
        s = Regex.Replace(s, @"[\s-]+", "-").Trim('-');
        return string.IsNullOrEmpty(s) ? "product" : s;
    }
    private async Task<string> UniqueSlugAsync(string baseSlug)
    {
        var slug = baseSlug; var n = 1;
        while (await _db.Products.AnyAsync(p => p.Slug == slug)) slug = $"{baseSlug}-{++n}";
        return slug;
    }
}

public class LabelRow
{
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public string Code { get; set; } = "";
    public string Category { get; set; } = "";
    public string? Description { get; set; }
}

public class MovementHistoryRow
{
    public DateTime Date { get; set; }
    public string Store { get; set; } = "";
    public string Type { get; set; } = "";
    public int Change { get; set; }
    public int Balance { get; set; }
    public string? Reference { get; set; }
    public string? Note { get; set; }
}

public class InvProductRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Sku { get; set; }
    public string? Barcode { get; set; }
    public decimal Price { get; set; }
    public string CategoryName { get; set; } = "";
    public string? ImageUrl { get; set; }
    public bool IsActive { get; set; }
    public bool TrackStock { get; set; }
    public string? ButtonColour { get; set; }
    public string? Description { get; set; }
    public int TotalStock { get; set; }
    public List<InvVariantRow> Variants { get; set; } = new();
}

public class InvVariantRow
{
    public string Name { get; set; } = "";
    public string? Sku { get; set; }
    public string? Barcode { get; set; }
    public decimal Price { get; set; }
}
