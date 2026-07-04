using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    public class InventoryController : AdminBaseController
    {
        protected override string Section => "Inventory";

        private readonly ApplicationDbContext _db;
        private readonly IStockService _stock;
        private const int PageSize = 30;

        public InventoryController(ApplicationDbContext db, IStockService stock)
        {
            _db = db;
            _stock = stock;
        }

        // ── Index: product-centric view (all stores as columns) ───────────────────
        public async Task<IActionResult> Index(
            string q = "", string category = "", string stock = "", int page = 1)
        {
            ViewData["Title"] = "Inventory";

            var stores = await _db.Stores
                .Where(s => s.IsActive)
                .OrderBy(s => s.Name)
                .ToListAsync();

            // ── Name + category filter in SQL ─────────────────────────────────────
            var productQuery = _db.Products
                .Include(p => p.Category)
                .Include(p => p.Images)
                .Where(p => p.IsActive)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(q))
                productQuery = productQuery.Where(p => EF.Functions.ILike(p.Name, $"%{q}%"));

            if (!string.IsNullOrWhiteSpace(category))
                productQuery = productQuery.Where(p => p.Category != null && p.Category.Slug == category);

            var allMatchingProducts = await productQuery.OrderBy(p => p.Name).ToListAsync();
            var allProductIds       = allMatchingProducts.Select(p => p.Id).ToList();

            // Load inventory for all matching products
            var allInventory = allProductIds.Any()
                ? await _db.StoreInventories.Where(si => allProductIds.Contains(si.ProductId)).ToListAsync()
                : new List<StoreInventory>();

            // Build rows (needed before stock filter so we can check stock values)
            var allRows = allMatchingProducts.Select(p =>
            {
                var stockByStore = stores.ToDictionary(
                    s => s.Id,
                    s => allInventory.FirstOrDefault(si => si.ProductId == p.Id && si.StoreId == s.Id)
                                    ?.QuantityOnHand ?? -1);
                return new ProductInventoryRow
                {
                    ProductId         = p.Id,
                    ProductName       = p.Name,
                    Sku               = p.Sku,
                    CategoryName      = p.Category?.Name ?? "—",
                    ImageUrl          = p.Images.OrderBy(i => i.SortOrder).FirstOrDefault()?.Url,
                    LowStockThreshold = p.LowStockThreshold,
                    StockByStore      = stockByStore,
                };
            }).ToList();

            // ── Stock status filter (in-memory, after building rows) ──────────────
            var filteredRows = stock switch
            {
                "outofstock" => allRows.Where(r => r.HasAnyRecord && r.TotalStock == 0).ToList(),
                "low"        => allRows.Where(r => r.HasLowStock).ToList(),
                "instock"    => allRows.Where(r => r.TotalStock > 0).ToList(),
                "norecord"   => allRows.Where(r => r.HasMissingRecord).ToList(),
                _            => allRows
            };

            // Paginate filtered rows
            var total    = filteredRows.Count;
            var pageRows = filteredRows.Skip((page - 1) * PageSize).Take(PageSize).ToList();

            var lastSync = allInventory.Any()
                ? allInventory.Max(si => (DateTime?)si.UpdatedAt)
                : await _db.StoreInventories.MaxAsync(si => (DateTime?)si.UpdatedAt);

            return View(new AdminInventoryViewModel
            {
                Stores              = stores,
                Products            = pageRows,
                UpdatedAt        = lastSync,
                SearchQuery         = q,
                CategoryFilter      = category,
                StockFilter         = stock,
                AvailableCategories = await _db.Categories.OrderBy(c => c.Name).ToListAsync(),
                CurrentPage         = page,
                TotalPages          = (int)Math.Ceiling(total / (double)PageSize),
                TotalCount          = total,
            });
        }

        // ── Stock holds (reservations) — read-only diagnostic (OP-27) ────────────
        // Every unpaid online order holds its units (bumps StoreInventory.QuantityReserved) until it's
        // paid (→ sale) or abandoned (→ freed by the sweeper). This surfaces those holds so "out of
        // stock caused by a hold" can be diagnosed. Newest first.
        public async Task<IActionResult> Reservations()
        {
            ViewData["Title"] = "Stock Holds";

            var res = await _db.StockReservations
                .Include(r => r.Order)
                .OrderByDescending(r => r.CreatedAt)
                .Take(500)
                .ToListAsync();

            var productIds = res.Select(r => r.ProductId).Distinct().ToList();
            var storeIds   = res.Select(r => r.StoreId).Distinct().ToList();
            var variantIds = res.Where(r => r.ProductVariantId.HasValue)
                                 .Select(r => r.ProductVariantId!.Value).Distinct().ToList();

            var products = (await _db.Products.Where(p => productIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Name, p.Slug }).ToListAsync())
                .ToDictionary(p => p.Id);
            var stores = (await _db.Stores.Where(s => storeIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Name }).ToListAsync())
                .ToDictionary(s => s.Id, s => s.Name);
            var variants = (await _db.ProductVariants.Where(v => variantIds.Contains(v.Id))
                .Select(v => new { v.Id, v.Name }).ToListAsync())
                .ToDictionary(v => v.Id, v => v.Name);

            var rows = res.Select(r => new SterlingLams.Web.Areas.Admin.ViewModels.ReservationRowViewModel
            {
                OrderId     = r.OrderId,
                OrderNumber = r.Order?.OrderNumber ?? $"#{r.OrderId}",
                OrderStatus = r.Order?.Status.ToString() ?? "",
                IsPaid      = r.Order?.IsPaid ?? false,
                ProductName = products.TryGetValue(r.ProductId, out var p) ? p.Name : $"Product #{r.ProductId}",
                ProductSlug = products.TryGetValue(r.ProductId, out var p2) ? p2.Slug : null,
                VariantName = r.ProductVariantId.HasValue && variants.TryGetValue(r.ProductVariantId.Value, out var vn) ? vn : null,
                StoreName   = stores.TryGetValue(r.StoreId, out var sn) ? sn : $"Store #{r.StoreId}",
                Quantity    = r.Quantity,
                CreatedAt   = r.CreatedAt,
            }).ToList();

            return View(rows);
        }

        // ── Set stock for a product across all stores (saves absolute quantities) ─
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SetProductStock(int productId, IFormCollection form)
        {
            var product = await _db.Products.FindAsync(productId);
            if (product == null) return NotFound();

            var stores = await _db.Stores.Where(s => s.IsActive).ToListAsync();
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

            foreach (var store in stores)
            {
                var key = $"store_{store.Id}";
                if (!form.TryGetValue(key, out var qtyStr)) continue;
                if (!int.TryParse(qtyStr, out var qty) || qty < 0) continue;

                // Apply the change through the stock ledger so every restock is traceable.
                var current = await _stock.GetStockAsync(productId, null, store.Id);
                var delta = qty - current;
                if (delta != 0)
                    await _stock.ApplyAsync(productId, null, store.Id, delta,
                        StockMovementType.Adjustment, "Stock update", userId: userId);
            }

            await _db.SaveChangesAsync();

            var summary = string.Join(", ", stores.Select(s =>
                $"{s.Name.Replace("Sterlin Glams ", "")}: {form[$"store_{s.Id}"]}"));
            await LogAsync("Update", "Inventory", productId.ToString(),
                $"Set stock for '{product.Name}' — {summary}");

            TempData["Success"] = $"Stock updated for '{product.Name}'.";

            var q        = form["q"].ToString();
            var category = form["category"].ToString();
            var stock    = form["stock"].ToString();
            var page     = int.TryParse(form["page"], out var p) ? p : 1;
            return RedirectToAction(nameof(Index), new { q, category, stock, page });
        }

        public class StockEdit
        {
            public int ProductId { get; set; }
            public int StoreId { get; set; }
            public int Quantity { get; set; }
        }

        // ── Bulk: set stock for many product×store cells at once (one "Save all") ─────
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SetAllProductStock([FromBody] List<StockEdit> edits)
        {
            if (edits == null || edits.Count == 0)
                return Json(new { success = true, count = 0 });

            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            var validStoreIds = (await _db.Stores.Where(s => s.IsActive).Select(s => s.Id).ToListAsync()).ToHashSet();
            var validProductIds = (await _db.Products
                .Where(p => edits.Select(e => e.ProductId).Distinct().Contains(p.Id))
                .Select(p => p.Id).ToListAsync()).ToHashSet();

            var applied = 0;
            foreach (var e in edits)
            {
                if (e.Quantity < 0 || !validStoreIds.Contains(e.StoreId) || !validProductIds.Contains(e.ProductId))
                    continue;

                // Route every change through the ledger so each restock stays traceable.
                var current = await _stock.GetStockAsync(e.ProductId, null, e.StoreId);
                var delta = e.Quantity - current;
                if (delta != 0)
                {
                    await _stock.ApplyAsync(e.ProductId, null, e.StoreId, delta,
                        StockMovementType.Adjustment, "Bulk stock update", userId: userId);
                    applied++;
                }
            }

            await _db.SaveChangesAsync();
            if (applied > 0)
                await LogAsync("Update", "Inventory", null, $"Bulk stock update — {applied} change(s)");

            return Json(new { success = true, count = applied });
        }

        // ── Ensure all product × store combinations have an inventory record ──────
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> EnsureInventoryRecords()
        {
            var products = await _db.Products.Where(p => p.IsActive).Select(p => p.Id).ToListAsync();
            var stores   = await _db.Stores.Where(s => s.IsActive).Select(s => s.Id).ToListAsync();

            var existing = await _db.StoreInventories
                .Select(si => new { si.ProductId, si.StoreId })
                .ToListAsync();

            var existingSet = existing.Select(e => (e.ProductId, e.StoreId)).ToHashSet();
            int created = 0;

            foreach (var productId in products)
            {
                foreach (var storeId in stores)
                {
                    if (!existingSet.Contains((productId, storeId)))
                    {
                        _db.StoreInventories.Add(new StoreInventory
                        {
                            ProductId      = productId,
                            StoreId        = storeId,
                            QuantityOnHand = 0,
                            UpdatedAt   = DateTime.UtcNow,
                        });
                        created++;
                    }
                }
            }

            await _db.SaveChangesAsync();
            if (created > 0)
                await LogAsync("Update", "Inventory", null, $"Created {created} missing inventory record(s)");
            TempData["Success"] = $"Created {created} missing inventory record(s). All products now appear in the grid.";
            return RedirectToAction(nameof(Index));
        }

        // ── Update low-stock threshold for a product ──────────────────────────────
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateThreshold(int productId, int threshold)
        {
            var product = await _db.Products.FindAsync(productId);
            if (product == null) return NotFound();

            product.LowStockThreshold = Math.Max(0, threshold);
            product.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            await LogAsync("Update", "Inventory", productId.ToString(),
                $"Set low-stock threshold for '{product.Name}' to {threshold}");
            TempData["Success"] = $"Threshold for '{product.Name}' set to {threshold}.";
            return RedirectToAction(nameof(Index));
        }
    }
}
