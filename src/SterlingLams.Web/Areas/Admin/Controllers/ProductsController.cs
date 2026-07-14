using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    public class ProductsController : AdminBaseController
    {
        protected override string Section => "Products";

        private readonly ApplicationDbContext _db;
        private readonly IWooCommerceImportService _wooImporter;
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _config;
        private readonly IStorefrontCache _storefrontCache;
        private readonly SeoDescriptionGenerator _seo;
        private const int PageSize = 30;

        public ProductsController(
            ApplicationDbContext db,
            IWooCommerceImportService wooImporter,
            IWebHostEnvironment env,
            IConfiguration config,
            IStorefrontCache storefrontCache,
            SeoDescriptionGenerator seo)
        {
            _db = db;
            _wooImporter = wooImporter;
            _env = env;
            _config = config;
            _storefrontCache = storefrontCache;
            _seo = seo;
        }

        public async Task<IActionResult> Index(
            string q = "", string category = "", string status = "", string type = "",
            decimal? minPrice = null, decimal? maxPrice = null, string sort = "name_asc", int page = 1)
        {
            ViewData["Title"] = "Products";

            // Sticky filters: remember the last filter/sort/page in the session so the list stays put
            // when you come back to Products (refresh, the menu link, or returning from an edit) — until
            // you change it or hit "Clear All" (which sends ?clear=1). A fresh visit with no query string
            // restores the saved view.
            const string FilterKey = "admin.products.filter";
            if (Request.Query.ContainsKey("clear"))
            {
                HttpContext.Session.Remove(FilterKey);
                return RedirectToAction(nameof(Index));
            }
            if (Request.Query.Count == 0)
            {
                var saved = HttpContext.Session.GetString(FilterKey);
                if (!string.IsNullOrEmpty(saved))
                    return Redirect(Url.Action(nameof(Index), "Products", new { area = "Admin" }) + saved);
            }
            else
            {
                HttpContext.Session.SetString(FilterKey, Request.QueryString.Value ?? "");
            }

            var query = _db.Products
                .Include(p => p.Category)
                .Include(p => p.Images)          // ← images for thumbnails
                .Include(p => p.Variants)        // ← for the variant-count badge
                .AsQueryable();

            // ── Filters ──────────────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(p => EF.Functions.ILike(p.Name, $"%{q}%")
                                      || EF.Functions.ILike(p.Sku ?? "", $"%{q}%")
                                      || EF.Functions.ILike(p.Barcode ?? "", $"%{q}%")
                                      || p.Variants.Any(v => EF.Functions.ILike(v.Sku ?? "", $"%{q}%")
                                                          || EF.Functions.ILike(v.Barcode ?? "", $"%{q}%")));

            if (!string.IsNullOrWhiteSpace(category))
                query = query.Where(p => p.Category != null && p.Category.Slug == category);

            if (minPrice.HasValue)
                query = query.Where(p => p.Price >= minPrice.Value);

            if (maxPrice.HasValue)
                query = query.Where(p => p.Price <= maxPrice.Value);

            if (type == "variable")
                query = query.Where(p => p.ProductType == "variable");
            else if (type == "simple")
                query = query.Where(p => p.ProductType != "variable");

            switch (status)
            {
                case "active":   query = query.Where(p => p.IsActive);           break;
                case "inactive": query = query.Where(p => !p.IsActive);          break;
                case "featured": query = query.Where(p => p.IsFeatured);         break;
                case "new":      query = query.Where(p => p.IsNewArrival);       break;
            }

            query = sort switch
            {
                "name_desc"     => query.OrderByDescending(p => p.Name),
                "category_asc"  => query.OrderBy(p => p.Category != null ? p.Category.Name : ""),
                "category_desc" => query.OrderByDescending(p => p.Category != null ? p.Category.Name : ""),
                "price_asc"     => query.OrderBy(p => p.Price),
                "price_desc"    => query.OrderByDescending(p => p.Price),
                "status_asc"    => query.OrderBy(p => p.IsActive),
                "status_desc"   => query.OrderByDescending(p => p.IsActive),
                _               => query.OrderBy(p => p.Name),   // name_asc (default)
            };

            var total    = await query.CountAsync();
            var products = await query
                .Skip((page - 1) * PageSize)
                .Take(PageSize)
                .ToListAsync();

            var vm = new AdminProductListViewModel
            {
                Products            = products,
                SearchQuery         = q,
                Sort                = sort,
                CategoryFilter      = category,
                StatusFilter        = status,
                TypeFilter          = type,
                MinPrice            = minPrice,
                MaxPrice            = maxPrice,
                CurrentPage         = page,
                TotalCount          = total,
                TotalPages          = (int)Math.Ceiling(total / (double)PageSize),
                AvailableCategories = await _db.Categories.OrderBy(c => c.Name).ToListAsync(),
            };

            return View(vm);
        }

        public async Task<IActionResult> Create()
        {
            ViewData["Title"] = "New Product";
            var vm = new AdminProductEditViewModel
            {
                Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync()
            };
            return View("Edit", vm);
        }

        public async Task<IActionResult> Edit(int id)
        {
            ViewData["Title"] = "Edit Product";

            var product = await _db.Products
                .Include(p => p.Images)
                .Include(p => p.Variants).ThenInclude(v => v.AttributeValues).ThenInclude(av => av.Attribute)
                .FirstOrDefaultAsync(p => p.Id == id);
            if (product == null) return NotFound();

            var vm = new AdminProductEditViewModel
            {
                Id               = product.Id,
                Name             = product.Name,
                Slug             = product.Slug,
                Sku              = product.Sku,
                ProductType      = string.IsNullOrWhiteSpace(product.ProductType) ? "simple" : product.ProductType,
                Description      = product.Description ?? "",
                ShortDescription = product.ShortDescription,
                Price            = product.Price,
                CostPrice        = product.CostPrice,
                SalePrice        = product.SalePrice,
                SaleStartsAt     = product.SaleStartsAt,
                SaleEndsAt       = product.SaleEndsAt,
                Colour           = product.Metal,
                Weight           = product.Weight,
                IsActive         = product.IsActive,
                IsFeatured       = product.IsFeatured,
                IsNewArrival     = product.IsNewArrival,
                ExternalCode     = product.ExternalCode,
                CategoryId       = product.CategoryId,
                Categories       = await _db.Categories.OrderBy(c => c.Name).ToListAsync(),
                Images           = product.Images.OrderBy(i => i.SortOrder).ToList(),
                AllAttributes    = await _db.ProductAttributes
                                     .Include(a => a.Values.OrderBy(v => v.SortOrder))
                                     .Where(a => a.IsActive)
                                     .OrderBy(a => a.SortOrder).ThenBy(a => a.Name)
                                     .ToListAsync(),
                Variants = product.Variants.OrderBy(v => v.Name).Select(v => new AdminVariantViewModel
                {
                    Id              = v.Id,
                    Name            = v.Name,
                    Sku             = v.Sku,
                    ImageUrl        = v.ImageUrl,
                    PriceAdjustment = v.PriceAdjustment,
                    StockQuantity   = v.StockQuantity,
                    IsActive        = v.IsActive,
                    AttributeLabels = v.AttributeValues
                                       .OrderBy(av => av.Attribute.SortOrder)
                                       .Select(av => av.Value).ToList()
                }).ToList()
            };

            // Sidebar: stock per branch + a 90-day sales snapshot (existing products only).
            var branches = await _db.StoreInventories
                .Where(si => si.ProductId == id)
                .GroupBy(si => si.Store.Name)
                .Select(g => new ProductBranchStock
                {
                    Store = g.Key,
                    OnHand = g.Sum(x => x.QuantityOnHand),
                    Reserved = g.Sum(x => x.QuantityReserved)
                })
                .OrderBy(b => b.Store)
                .ToListAsync();

            var since90 = DateTime.UtcNow.Date.AddDays(-90);
            var soldItems = _db.OrderItems.Where(oi => oi.ProductId == id && oi.Order.IsPaid && oi.Order.CreatedAt >= since90);

            // The most recent paid sale of this product (all-time), for one-click order tracing.
            var lastSale = await _db.OrderItems
                .Where(oi => oi.ProductId == id && oi.Order.IsPaid)
                .OrderByDescending(oi => oi.Order.CreatedAt)
                .Select(oi => new { oi.Order.CreatedAt, oi.OrderId, oi.Order.OrderNumber })
                .FirstOrDefaultAsync();

            vm.Sidebar = new ProductEditSidebar
            {
                LowStockThreshold = product.LowStockThreshold,
                Branches = branches,
                TotalOnHand = branches.Sum(b => b.OnHand),
                TotalReserved = branches.Sum(b => b.Reserved),
                UnitsSold90 = await soldItems.SumAsync(oi => (int?)oi.Quantity) ?? 0,
                Revenue90 = await soldItems.SumAsync(oi => (decimal?)(oi.Quantity * oi.UnitPrice - oi.DiscountAmount)) ?? 0,
                LastSold = lastSale?.CreatedAt,
                LastSoldOrderId = lastSale?.OrderId,
                LastSoldOrderNumber = lastSale?.OrderNumber
            };

            return View(vm);
        }

        /// <summary>Generates an SEO description from the current form values (name + category) so the
        /// admin can drop it into the editor, then tweak and save. Category-aware: jewellery vs. bag,
        /// belt, cap/scarf, sunglasses, watch, etc. Nothing is saved here — the returned HTML lands in
        /// the editable rich-text field.</summary>
        [HttpGet]
        public async Task<IActionResult> GenerateDescription(int id, string name, int categoryId)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Json(new { ok = false, error = "Enter a product name first." });

            var catName = await _db.Categories.Where(c => c.Id == categoryId)
                .Select(c => c.Name).FirstOrDefaultAsync();
            if (string.IsNullOrWhiteSpace(catName))
                return Json(new { ok = false, error = "Choose a category first." });

            // Same seed rule as the batch tools so a saved product regenerates consistently.
            var seed = id > 0 ? id : Math.Abs(StringComparer.Ordinal.GetHashCode(name));
            var html = ProductHtml.Sanitize(_seo.Build(seed, name, catName));
            var shortText = _seo.BuildShort(seed, name, catName);
            var kind = SeoDescriptionGenerator.DetectKind(name, catName).ToString();
            return Json(new { ok = true, html, shortText, kind });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> GenerateVariants(int id, List<int> selectedValueIds)
        {
            var product = await _db.Products
                .Include(p => p.Variants).ThenInclude(v => v.AttributeValues)
                .FirstOrDefaultAsync(p => p.Id == id);
            if (product == null) return NotFound();

            // Group selected values by attribute
            var selectedValues = await _db.ProductAttributeValues
                .Include(v => v.Attribute)
                .Where(v => selectedValueIds.Contains(v.Id))
                .OrderBy(v => v.Attribute.SortOrder).ThenBy(v => v.SortOrder)
                .ToListAsync();

            var byAttribute = selectedValues
                .GroupBy(v => v.AttributeId)
                .Select(g => g.ToList())
                .ToList();

            if (!byAttribute.Any())
            {
                TempData["Error"] = "Select at least one attribute value to generate variants.";
                return RedirectToAction(nameof(Edit), new { id });
            }

            // Cartesian product of all attribute groups
            var combinations = CartesianProduct(byAttribute);
            int created = 0;

            foreach (var combo in combinations)
            {
                var comboIds = combo.Select(v => v.Id).OrderBy(x => x).ToList();
                // Skip if a variant with this exact combination already exists
                var exists = product.Variants.Any(v =>
                    v.AttributeValues.Select(av => av.Id).OrderBy(x => x).SequenceEqual(comboIds));
                if (exists) continue;

                var name    = string.Join(" / ", combo.Select(v => v.Value));
                var variant = new ProductVariant { ProductId = id, Name = name, IsActive = true };
                foreach (var val in combo) variant.AttributeValues.Add(val);
                _db.ProductVariants.Add(variant);
                created++;
            }

            await _db.SaveChangesAsync();
            if (created > 0)
                await LogAsync("Update", "Product", id.ToString(),
                    $"Generated {created} variant(s) for '{product.Name}'");
            TempData["Success"] = created > 0
                ? $"{created} variant(s) generated."
                : "All combinations already exist.";

            return RedirectToAction(nameof(Edit), new { id });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveAllVariants(int productId,
            Microsoft.AspNetCore.Http.IFormCollection form)
        {
            var variants = await _db.ProductVariants
                .Where(v => v.ProductId == productId)
                .ToListAsync();

            int saved = 0;
            foreach (var variant in variants)
            {
                var sku  = form[$"sku_{variant.Id}"].FirstOrDefault()?.Trim();
                var adj  = form[$"adj_{variant.Id}"].FirstOrDefault();
                var img  = form[$"img_{variant.Id}"].FirstOrDefault()?.Trim();
                // The form sends hidden=false + optional checkbox=true; last value wins
                var activeVals = form[$"active_{variant.Id}"];
                var active = activeVals.Contains("true");

                variant.Sku             = sku;
                variant.ImageUrl        = string.IsNullOrWhiteSpace(img) ? null : img;
                variant.PriceAdjustment = decimal.TryParse(adj,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
                variant.IsActive        = active;
                saved++;
            }

            if (saved > 0)
            {
                await _db.SaveChangesAsync();
                await LogAsync("Update", "Product", productId.ToString(), $"Saved {saved} variant(s)");
                TempData["Success"] = $"All {saved} variant(s) saved.";
            }

            return RedirectToAction(nameof(Edit), new { id = productId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteVariant(int productId, int variantId)
        {
            var variant = await _db.ProductVariants
                .FirstOrDefaultAsync(v => v.Id == variantId && v.ProductId == productId);
            if (variant != null)
            {
                var vName = variant.Name;
                _db.ProductVariants.Remove(variant);
                await _db.SaveChangesAsync();
                await LogAsync("Update", "Product", productId.ToString(), $"Deleted variant '{vName}'");
                TempData["Success"] = "Variant deleted.";
            }
            return RedirectToAction(nameof(Edit), new { id = productId });
        }

        private static IEnumerable<List<ProductAttributeValue>> CartesianProduct(
            List<List<ProductAttributeValue>> sets)
        {
            IEnumerable<List<ProductAttributeValue>> result = new[] { new List<ProductAttributeValue>() };
            foreach (var set in sets)
                result = result.SelectMany(
                    combo => set.Select(item => combo.Concat(new[] { item }).ToList()));
            return result;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(AdminProductEditViewModel vm)
        {
            vm.Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync();

            // Required fields (the Category FK is non-nullable, so guard it explicitly).
            if (string.IsNullOrWhiteSpace(vm.Name))
                ModelState.AddModelError(nameof(vm.Name), "Name is required.");
            if (vm.CategoryId is null or 0 || !vm.Categories.Any(c => c.Id == vm.CategoryId))
                ModelState.AddModelError(nameof(vm.CategoryId), "Please select a category.");

            if (!ModelState.IsValid)
            {
                ViewData["Title"] = vm.Id == 0 ? "New Product" : "Edit Product";
                return View("Edit", vm);
            }

            Product product;
            if (vm.Id == 0)
            {
                product = new Product();
                _db.Products.Add(product);
            }
            else
            {
                product = await _db.Products.FindAsync(vm.Id) ?? new Product();
            }

            // Snapshot for the audit before/after diff (meaningful on an update).
            var oldName = product.Name; var oldPrice = product.Price; var oldSale = product.SalePrice;
            var oldSku = product.Sku; var oldActive = product.IsActive;
            var oldFeatured = product.IsFeatured; var oldNewArr = product.IsNewArrival;

            product.Name = vm.Name.Trim();
            product.Slug = string.IsNullOrWhiteSpace(vm.Slug)
                ? Regex.Replace(vm.Name.ToLower().Trim(), @"[^a-z0-9]+", "-")
                : vm.Slug.Trim();
            product.Description = SterlingLams.Web.Services.ProductHtml.Sanitize(vm.Description);
            product.ShortDescription = vm.ShortDescription;
            product.Price = vm.Price;
            product.CostPrice = vm.CostPrice is decimal c && c >= 0m ? c : null;
            // Sale price only applies when it's a positive number below the regular price.
            product.SalePrice = vm.SalePrice is decimal sp && sp > 0m && sp < vm.Price ? sp : null;
            // Sale schedule (UTC). Cleared with the sale price; null on either side = open-ended.
            if (product.SalePrice == null)
            {
                product.SaleStartsAt = null;
                product.SaleEndsAt = null;
            }
            else
            {
                product.SaleStartsAt = vm.SaleStartsAt.HasValue ? DateTime.SpecifyKind(vm.SaleStartsAt.Value, DateTimeKind.Utc) : null;
                product.SaleEndsAt = vm.SaleEndsAt.HasValue ? DateTime.SpecifyKind(vm.SaleEndsAt.Value, DateTimeKind.Utc) : null;
            }
            product.Sku = string.IsNullOrWhiteSpace(vm.Sku) ? null : vm.Sku.Trim();
            product.ProductType = vm.ProductType == "variable" ? "variable" : "simple";
            product.IsActive = vm.IsActive;
            product.IsFeatured = vm.IsFeatured;
            product.IsNewArrival = vm.IsNewArrival;
            product.ExternalCode = vm.ExternalCode?.Trim() ?? string.Empty;
            product.CategoryId = vm.CategoryId!.Value;
            product.UpdatedAt = DateTime.UtcNow;

            var isNew = vm.Id == 0;
            await _db.SaveChangesAsync();
            await _storefrontCache.EvictAsync();

            var changes = isNew ? null : SterlingLams.Web.Services.AuditChanges.Build(
                ("Name", oldName, product.Name),
                ("Price", oldPrice, product.Price),
                ("Sale price", oldSale, product.SalePrice),
                ("SKU", oldSku, product.Sku),
                ("Active", oldActive, product.IsActive),
                ("Featured", oldFeatured, product.IsFeatured),
                ("New arrival", oldNewArr, product.IsNewArrival));
            await LogAsync(isNew ? "Create" : "Update", "Product", product.Id.ToString(),
                $"{(isNew ? "Created" : "Updated")} product '{product.Name}' (₦{product.Price:N0})", changes);

            if (isNew)
            {
                TempData["Success"] = $"Product '{product.Name}' created. You can now upload images below.";
                return RedirectToAction(nameof(Edit), new { id = product.Id });
            }

            TempData["Success"] = $"Product '{product.Name}' saved.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Duplicate(int id)
        {
            var source = await _db.Products.Include(p => p.Images).FirstOrDefaultAsync(p => p.Id == id);
            if (source == null) return NotFound();

            var baseSlug = source.Slug + "-copy";
            var slug = baseSlug;
            int n = 1;
            while (await _db.Products.AnyAsync(p => p.Slug == slug))
                slug = $"{baseSlug}-{n++}";

            var copy = new Product
            {
                Name             = source.Name + " (Copy)",
                Slug             = slug,
                ExternalCode     = $"COPY-{Guid.NewGuid():N}"[..20],
                Description      = source.Description,
                ShortDescription = source.ShortDescription,
                Price            = source.Price,
                Metal            = source.Metal,
                Weight           = source.Weight,
                CategoryId       = source.CategoryId,
                IsActive         = false,
                IsFeatured       = false,
                IsNewArrival     = source.IsNewArrival,
                CreatedAt        = DateTime.UtcNow,
                UpdatedAt        = DateTime.UtcNow,
            };

            foreach (var img in source.Images.OrderBy(i => i.SortOrder))
                copy.Images.Add(new ProductImage { Url = img.Url, AltText = img.AltText, IsPrimary = img.IsPrimary, SortOrder = img.SortOrder });

            _db.Products.Add(copy);
            await _db.SaveChangesAsync();

            await LogAsync("Create", "Product", copy.Id.ToString(),
                $"Duplicated '{source.Name}' → '{copy.Name}'");

            TempData["Success"] = $"Product duplicated as '{copy.Name}'. It is inactive — edit and activate when ready.";
            return RedirectToAction(nameof(Edit), new { id = copy.Id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleActive(int id)
        {
            var product = await _db.Products.FindAsync(id);
            if (product == null) return NotFound();

            product.IsActive = !product.IsActive;
            product.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await _storefrontCache.EvictAsync();

            await LogAsync("Update", "Product", product.Id.ToString(),
                $"Set '{product.Name}' to {(product.IsActive ? "active" : "inactive")}");

            TempData["Success"] = $"'{product.Name}' is now {(product.IsActive ? "active" : "inactive")}.";
            return RedirectToAction(nameof(Index));
        }

        // ── Bulk actions on selected products ────────────────────────────────────
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Bulk(string op, int[] ids,
            int? bulkCategoryId = null, decimal? bulkSalePrice = null,
            DateTime? bulkSaleStartsAt = null, DateTime? bulkSaleEndsAt = null,
            string q = "", string category = "", string status = "", string type = "", int page = 1)
        {
            var back = new { q, category, status, type, page };
            ids ??= Array.Empty<int>();
            if (ids.Length == 0)
            {
                TempData["Error"] = "No products were selected.";
                return RedirectToAction(nameof(Index), back);
            }

            var products = await _db.Products.Include(p => p.Category).Where(p => ids.Contains(p.Id)).ToListAsync();
            var n = products.Count;

            // Delete is special: skip products with order/stock history (RESTRICT FKs) and report.
            if (op == "delete")
            {
                var blocked = new HashSet<int>();
                foreach (var p in products)
                    if (await ProductHasHistoryAsync(p.Id)) blocked.Add(p.Id);
                var deletable = products.Where(p => !blocked.Contains(p.Id)).ToList();
                _db.Products.RemoveRange(deletable);
                try
                {
                    await _db.SaveChangesAsync();
                }
                catch (DbUpdateException)
                {
                    TempData["Error"] = "Some products are referenced by existing records and can't be deleted. Deactivate them instead.";
                    return RedirectToAction(nameof(Index), back);
                }
                await _storefrontCache.EvictAsync();
                await LogAsync("Delete", "Product", null, $"Bulk delete: {deletable.Count} deleted, {blocked.Count} skipped (history)");
                TempData[deletable.Count > 0 ? "Success" : "Error"] = blocked.Count > 0
                    ? $"{deletable.Count} product(s) deleted; {blocked.Count} kept — they have order/stock history (deactivate instead)."
                    : $"{deletable.Count} product(s) deleted.";
                return RedirectToAction(nameof(Index), back);
            }

            switch (op)
            {
                case "activate":   products.ForEach(p => { p.IsActive = true;   p.UpdatedAt = DateTime.UtcNow; }); break;
                case "deactivate": products.ForEach(p => { p.IsActive = false;  p.UpdatedAt = DateTime.UtcNow; }); break;
                case "feature":    products.ForEach(p => { p.IsFeatured = true; p.UpdatedAt = DateTime.UtcNow; }); break;
                case "unfeature":  products.ForEach(p => { p.IsFeatured = false;p.UpdatedAt = DateTime.UtcNow; }); break;

                case "set-category":
                    if (bulkCategoryId is not int catId || !await _db.Categories.AnyAsync(c => c.Id == catId))
                    {
                        TempData["Error"] = "Pick a category to move the selected products to.";
                        return RedirectToAction(nameof(Index), back);
                    }
                    products.ForEach(p => { p.CategoryId = catId; p.UpdatedAt = DateTime.UtcNow; });
                    break;

                case "set-sale":
                    if (bulkSalePrice is not decimal salePrice || salePrice <= 0m)
                    {
                        TempData["Error"] = "Enter a sale price greater than zero.";
                        return RedirectToAction(nameof(Index), back);
                    }
                    var saleStart = bulkSaleStartsAt.HasValue ? DateTime.SpecifyKind(bulkSaleStartsAt.Value, DateTimeKind.Utc) : (DateTime?)null;
                    var saleEnd   = bulkSaleEndsAt.HasValue ? DateTime.SpecifyKind(bulkSaleEndsAt.Value, DateTimeKind.Utc) : (DateTime?)null;
                    // Apply per product — only where the sale price is genuinely below that item's price.
                    var applied = 0;
                    foreach (var p in products.Where(p => salePrice < p.Price))
                    {
                        p.SalePrice = salePrice; p.SaleStartsAt = saleStart; p.SaleEndsAt = saleEnd;
                        p.UpdatedAt = DateTime.UtcNow; applied++;
                    }
                    await _db.SaveChangesAsync();
                    await _storefrontCache.EvictAsync();
                    await LogAsync("Update", "Product", null, $"Bulk set-sale ₦{salePrice:N0} on {applied}/{n} product(s)");
                    TempData["Success"] = $"Sale price applied to {applied} of {n} product(s)" +
                        (applied < n ? " (skipped those priced at or below the sale price)." : ".");
                    return RedirectToAction(nameof(Index), back);

                case "clear-sale":
                    products.ForEach(p => { p.SalePrice = null; p.SaleStartsAt = null; p.SaleEndsAt = null; p.UpdatedAt = DateTime.UtcNow; });
                    break;

                case "run-seo":
                    foreach (var p in products)
                    {
                        var catName = p.Category?.Name ?? "Jewellery";
                        p.Description = ProductHtml.Sanitize(_seo.Build(p.Id, p.Name, catName));
                        p.ShortDescription = _seo.BuildShort(p.Id, p.Name, catName);
                        p.UpdatedAt = DateTime.UtcNow;
                    }
                    break;

                default:
                    TempData["Error"] = "Unknown bulk action.";
                    return RedirectToAction(nameof(Index), back);
            }

            await _db.SaveChangesAsync();
            await _storefrontCache.EvictAsync();
            await LogAsync("Update", "Product", null, $"Bulk {op} on {n} product(s)");
            TempData["Success"] = $"{op} applied to {n} product(s).";
            return RedirectToAction(nameof(Index), back);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestFormLimits(MultipartBodyLengthLimit = 52428800)] // 50 MB
        public async Task<IActionResult> ImportFromWooCommerce(Microsoft.AspNetCore.Http.IFormFile csvFile)
        {
            if (csvFile == null || csvFile.Length == 0)
            {
                TempData["Error"] = "Please select a CSV file to upload.";
                return RedirectToAction(nameof(Index));
            }

            if (!csvFile.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                TempData["Error"] = "Only .csv files are supported.";
                return RedirectToAction(nameof(Index));
            }

            try
            {
                using var stream = csvFile.OpenReadStream();
                var result = await _wooImporter.ImportFromCsvAsync(stream);
                await LogAsync("Import", "Product", null,
                    $"WooCommerce CSV import ({csvFile.FileName}): {result.Summary}");
                TempData[result.Errors.Any() ? "Warning" : "Success"] =
                    $"WooCommerce import complete: {result.Summary}" +
                    (result.Errors.Any() ? $" — First error: {result.Errors[0]}" : "");
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Import failed: {ex.Message}";
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var product = await _db.Products.FindAsync(id);
            if (product != null)
            {
                // Products with sales or stock history can't be deleted — that would destroy order
                // line items / the stock ledger (FKs are RESTRICT). Deactivate instead.
                if (await ProductHasHistoryAsync(id))
                {
                    TempData["Error"] = $"'{product.Name}' has order or stock history and can't be deleted. Deactivate it instead.";
                    return RedirectToAction(nameof(Index));
                }

                var name = product.Name;
                _db.Products.Remove(product);
                try
                {
                    await _db.SaveChangesAsync();
                }
                catch (DbUpdateException)
                {
                    TempData["Error"] = $"'{name}' is referenced by existing records and can't be deleted. Deactivate it instead.";
                    return RedirectToAction(nameof(Index));
                }
                await _storefrontCache.EvictAsync();
                await LogAsync("Delete", "Product", id.ToString(), $"Deleted product '{name}'");
                TempData["Success"] = $"Product '{name}' deleted.";
            }

            return RedirectToAction(nameof(Index));
        }

        /// <summary>True if the product is referenced by any order line item or stock-ledger row —
        /// in which case it must be deactivated, not deleted (RESTRICT FKs preserve that history).</summary>
        private async Task<bool> ProductHasHistoryAsync(int productId) =>
            await _db.OrderItems.AnyAsync(oi => oi.ProductId == productId)
            || await _db.StockMovements.AnyAsync(m => m.ProductId == productId);

        // Stores an uploaded product image to Cloudinary (persistent + CDN) when configured — REQUIRED
        // on ephemeral hosts like Render, where /wwwroot/uploads is wiped on every redeploy (which is
        // why locally-saved product images broke on the storefront after a deploy). Falls back to local
        // disk only in dev/when Cloudinary isn't configured. Returns the URL, or null on failure.
        private async Task<string?> SaveProductImageAsync(IFormFile file)
        {
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            var cloudName = _config["Cloudinary:CloudName"];
            var apiKey    = _config["Cloudinary:ApiKey"];
            var apiSecret = _config["Cloudinary:ApiSecret"];
            if (!string.IsNullOrWhiteSpace(cloudName) && !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(apiSecret))
            {
                var cloudinary = new Cloudinary(new Account(cloudName, apiKey, apiSecret)) { Api = { Secure = true } };
                await using var s = file.OpenReadStream();
                var result = await cloudinary.UploadAsync(new ImageUploadParams
                {
                    File = new FileDescription(file.FileName, s),
                    Folder = "sterlinglams/products",
                    PublicId = Guid.NewGuid().ToString("N"),
                    UniqueFilename = false,
                    Overwrite = false
                });
                if (result.StatusCode != System.Net.HttpStatusCode.OK || result.SecureUrl == null) return null;
                return result.SecureUrl.ToString();
            }

            // Dev fallback: local disk (NOT persistent on Render).
            var dir = Path.Combine(_env.WebRootPath, "uploads", "products");
            Directory.CreateDirectory(dir);
            var fileName = $"{Guid.NewGuid():N}{ext}";
            await using var stream = System.IO.File.Create(Path.Combine(dir, fileName));
            await file.CopyToAsync(stream);
            return $"/uploads/products/{fileName}";
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddImage(int id, IFormFile? imageFile, string? imageUrl, string? altText, bool isPrimary)
        {
            var product = await _db.Products.FindAsync(id);
            if (product == null) return NotFound();

            // Resolve image URL: file upload takes priority over URL text field
            string resolvedUrl;
            if (imageFile != null && imageFile.Length > 0)
            {
                var saved = await SaveProductImageAsync(imageFile);
                if (saved == null)
                {
                    TempData["Error"] = "Image upload failed. Please try again.";
                    return RedirectToAction(nameof(Edit), new { id });
                }
                resolvedUrl = saved;
            }
            else if (!string.IsNullOrWhiteSpace(imageUrl))
            {
                resolvedUrl = imageUrl.Trim();
            }
            else
            {
                TempData["Error"] = "Please upload a file or provide an image URL.";
                return RedirectToAction(nameof(Edit), new { id });
            }

            if (isPrimary)
            {
                var existing = await _db.ProductImages
                    .Where(i => i.ProductId == id && i.IsPrimary)
                    .ToListAsync();
                existing.ForEach(i => i.IsPrimary = false);
            }

            var maxSort = await _db.ProductImages
                .Where(i => i.ProductId == id)
                .MaxAsync(i => (int?)i.SortOrder) ?? 0;

            _db.ProductImages.Add(new ProductImage
            {
                ProductId = id,
                Url = resolvedUrl,
                AltText = altText?.Trim(),
                IsPrimary = isPrimary,
                SortOrder = maxSort + 1
            });

            await _db.SaveChangesAsync();
            await LogAsync("Update", "Product", id.ToString(), $"Added image to '{product.Name}'");
            TempData["Success"] = "Image added.";
            return RedirectToAction(nameof(Edit), new { id });
        }

        // Promote an already-uploaded image to primary (the one shown by default on cards & detail).
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetPrimaryImage(int productId, int imageId)
        {
            var images = await _db.ProductImages.Where(i => i.ProductId == productId).ToListAsync();
            var target = images.FirstOrDefault(i => i.Id == imageId);
            if (target != null)
            {
                foreach (var i in images) i.IsPrimary = false;
                target.IsPrimary = true;
                target.IsHover = false; // an image can't be both the primary and the hover swap
                await _db.SaveChangesAsync();
                await LogAsync("Update", "Product", productId.ToString(), "Set primary product image");
                TempData["Success"] = "Primary image updated.";
            }
            return RedirectToAction(nameof(Edit), new { id = productId });
        }

        // Pick the image revealed on card hover (Tiffany-style). Clicking the current hover image
        // again clears it. The primary image can't double as the hover image.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetHoverImage(int productId, int imageId)
        {
            var images = await _db.ProductImages.Where(i => i.ProductId == productId).ToListAsync();
            var target = images.FirstOrDefault(i => i.Id == imageId);
            if (target == null) return RedirectToAction(nameof(Edit), new { id = productId });
            if (target.IsPrimary)
            {
                TempData["Error"] = "Pick a different image for hover — the primary can't also be the hover image.";
                return RedirectToAction(nameof(Edit), new { id = productId });
            }
            var turningOn = !target.IsHover;
            foreach (var i in images) i.IsHover = false;
            target.IsHover = turningOn;
            await _db.SaveChangesAsync();
            await LogAsync("Update", "Product", productId.ToString(), turningOn ? "Set hover product image" : "Cleared hover product image");
            TempData["Success"] = turningOn ? "Hover image set." : "Hover image cleared.";
            return RedirectToAction(nameof(Edit), new { id = productId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteImage(int productId, int imageId)
        {
            var image = await _db.ProductImages.FindAsync(imageId);
            if (image != null && image.ProductId == productId)
            {
                _db.ProductImages.Remove(image);
                await _db.SaveChangesAsync();
                await LogAsync("Update", "Product", productId.ToString(), "Removed a product image");
                TempData["Success"] = "Image removed.";
            }
            return RedirectToAction(nameof(Edit), new { id = productId });
        }
    }
}
