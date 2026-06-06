using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;
using SterlingLams.Web.Services.Inventory;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    public class ProductsController : AdminBaseController
    {
        private readonly ApplicationDbContext _db;
        private readonly IInventoryService _inventory;
        private readonly IProductImportService _importer;
        private readonly IWooCommerceImportService _wooImporter;
        private const int PageSize = 30;

        public ProductsController(
            ApplicationDbContext db,
            IInventoryService inventory,
            IProductImportService importer,
            IWooCommerceImportService wooImporter)
        {
            _db = db;
            _inventory = inventory;
            _importer = importer;
            _wooImporter = wooImporter;
        }

        public async Task<IActionResult> Index(
            string q = "", string category = "", string status = "",
            decimal? minPrice = null, decimal? maxPrice = null, int page = 1)
        {
            ViewData["Title"] = "Products";

            var query = _db.Products
                .Include(p => p.Category)
                .Include(p => p.Images)          // ← images for thumbnails
                .AsQueryable();

            // ── Filters ──────────────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(p => EF.Functions.ILike(p.Name, $"%{q}%"));

            if (!string.IsNullOrWhiteSpace(category))
                query = query.Where(p => p.Category != null && p.Category.Slug == category);

            if (minPrice.HasValue)
                query = query.Where(p => p.Price >= minPrice.Value);

            if (maxPrice.HasValue)
                query = query.Where(p => p.Price <= maxPrice.Value);

            switch (status)
            {
                case "active":   query = query.Where(p => p.IsActive);           break;
                case "inactive": query = query.Where(p => !p.IsActive);          break;
                case "featured": query = query.Where(p => p.IsFeatured);         break;
                case "new":      query = query.Where(p => p.IsNewArrival);       break;
            }

            var total    = await query.CountAsync();
            var products = await query
                .OrderBy(p => p.Name)
                .Skip((page - 1) * PageSize)
                .Take(PageSize)
                .ToListAsync();

            var vm = new AdminProductListViewModel
            {
                Products            = products,
                SearchQuery         = q,
                CategoryFilter      = category,
                StatusFilter        = status,
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
                Description      = product.Description ?? "",
                ShortDescription = product.ShortDescription,
                Price            = product.Price,
                Colour           = product.Metal,
                Weight           = product.Weight,
                IsActive         = product.IsActive,
                IsFeatured       = product.IsFeatured,
                IsNewArrival     = product.IsNewArrival,
                ErpNextItemCode  = product.ErpNextItemCode,
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
                    PriceAdjustment = v.PriceAdjustment,
                    StockQuantity   = v.StockQuantity,
                    IsActive        = v.IsActive,
                    AttributeLabels = v.AttributeValues
                                       .OrderBy(av => av.Attribute.SortOrder)
                                       .Select(av => av.Value).ToList()
                }).ToList()
            };

            return View(vm);
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
            TempData["Success"] = created > 0
                ? $"{created} variant(s) generated."
                : "All combinations already exist.";

            return RedirectToAction(nameof(Edit), new { id });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveVariant(int productId, int variantId,
            string? sku, decimal? priceAdjustment, int stockQuantity, bool isActive)
        {
            var variant = await _db.ProductVariants
                .FirstOrDefaultAsync(v => v.Id == variantId && v.ProductId == productId);
            if (variant == null) return NotFound();

            variant.Sku             = sku?.Trim();
            variant.PriceAdjustment = priceAdjustment;
            variant.StockQuantity   = stockQuantity;
            variant.IsActive        = isActive;
            await _db.SaveChangesAsync();

            TempData["Success"] = $"Variant '{variant.Name}' updated.";
            return RedirectToAction(nameof(Edit), new { id = productId });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteVariant(int productId, int variantId)
        {
            var variant = await _db.ProductVariants
                .FirstOrDefaultAsync(v => v.Id == variantId && v.ProductId == productId);
            if (variant != null)
            {
                _db.ProductVariants.Remove(variant);
                await _db.SaveChangesAsync();
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

            product.Name = vm.Name.Trim();
            product.Slug = string.IsNullOrWhiteSpace(vm.Slug)
                ? Regex.Replace(vm.Name.ToLower().Trim(), @"[^a-z0-9]+", "-")
                : vm.Slug.Trim();
            product.Description = vm.Description;
            product.ShortDescription = vm.ShortDescription;
            product.Price = vm.Price;
            product.Metal = vm.Colour;
            product.Weight = vm.Weight;
            product.IsActive = vm.IsActive;
            product.IsFeatured = vm.IsFeatured;
            product.IsNewArrival = vm.IsNewArrival;
            product.ErpNextItemCode = vm.ErpNextItemCode?.Trim() ?? string.Empty;
            product.CategoryId = vm.CategoryId ?? product.CategoryId;
            product.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();

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
                ErpNextItemCode  = $"COPY-{Guid.NewGuid():N}"[..20],
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

            TempData["Success"] = $"'{product.Name}' is now {(product.IsActive ? "active" : "inactive")}.";
            return RedirectToAction(nameof(Index));
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
        public async Task<IActionResult> SyncFromERPNext()
        {
            try
            {
                await _inventory.SyncAllAsync();
                TempData["Success"] = "ERPNext inventory sync completed successfully.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Inventory sync failed: {ex.Message}";
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ImportFromERPNext()
        {
            try
            {
                var result = await _importer.ImportAllFromERPNextAsync();
                TempData[result.Success ? "Success" : "Warning"] =
                    $"ERPNext product import complete: {result.Summary}" +
                    (result.Errors.Any() ? $" — First error: {result.Errors[0]}" : "");
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Product import failed: {ex.Message}";
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
                _db.Products.Remove(product);
                await _db.SaveChangesAsync();
                TempData["Success"] = $"Product '{product.Name}' deleted.";
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddImage(int id, string imageUrl, string? altText, bool isPrimary)
        {
            var product = await _db.Products.FindAsync(id);
            if (product == null) return NotFound();

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
                Url = imageUrl.Trim(),
                AltText = altText?.Trim(),
                IsPrimary = isPrimary,
                SortOrder = maxSort + 1
            });

            await _db.SaveChangesAsync();
            TempData["Success"] = "Image added.";
            return RedirectToAction(nameof(Edit), new { id });
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
                TempData["Success"] = "Image removed.";
            }
            return RedirectToAction(nameof(Edit), new { id = productId });
        }
    }
}
