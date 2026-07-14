using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Areas.Admin.ViewModels;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Admin.Controllers
{
    public class CategoriesController : AdminBaseController
    {
        protected override string Section => "Categories";

        private readonly ApplicationDbContext _db;
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _config;

        public CategoriesController(ApplicationDbContext db, IWebHostEnvironment env, IConfiguration config)
        {
            _db = db;
            _env = env;
            _config = config;
        }

        public async Task<IActionResult> Index()
        {
            ViewData["Title"] = "Categories";
            var categories = await _db.Categories
                .Include(c => c.Parent)
                .OrderBy(c => c.SortOrder)
                .ThenBy(c => c.Name)
                .ToListAsync();
            return View(categories);
        }

        public async Task<IActionResult> Create()
        {
            ViewData["Title"] = "New Category";
            var vm = new AdminCategoryEditViewModel
            {
                IsActive = true,
                AllCategories = await _db.Categories.OrderBy(c => c.Name).ToListAsync()
            };
            return View("Edit", vm);
        }

        public async Task<IActionResult> Edit(int id)
        {
            ViewData["Title"] = "Edit Category";
            var cat = await _db.Categories.FindAsync(id);
            if (cat == null) return NotFound();

            var vm = new AdminCategoryEditViewModel
            {
                Id = cat.Id,
                Name = cat.Name,
                Slug = cat.Slug,
                Description = cat.Description,
                ImageUrl = cat.ImageUrl,
                IsActive = cat.IsActive,
                SortOrder = cat.SortOrder,
                ParentId = cat.ParentId,
                AllCategories = await _db.Categories
                    .Where(c => c.Id != id)
                    .OrderBy(c => c.Name)
                    .ToListAsync()
            };
            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(AdminCategoryEditViewModel vm, IFormFile? imageFile)
        {
            vm.AllCategories = await _db.Categories
                .Where(c => c.Id != vm.Id)
                .OrderBy(c => c.Name)
                .ToListAsync();

            if (!ModelState.IsValid)
            {
                ViewData["Title"] = vm.Id == 0 ? "New Category" : "Edit Category";
                return View("Edit", vm);
            }

            Category category;
            if (vm.Id == 0)
            {
                category = new Category();
                _db.Categories.Add(category);
            }
            else
            {
                category = await _db.Categories.FindAsync(vm.Id) ?? new Category();
            }

            category.Name = vm.Name.Trim();
            category.Slug = string.IsNullOrWhiteSpace(vm.Slug)
                ? Regex.Replace(vm.Name.ToLower().Trim(), @"[^a-z0-9]+", "-")
                : vm.Slug.Trim();
            category.Description = vm.Description;
            category.IsActive = vm.IsActive;
            category.SortOrder = vm.SortOrder;
            category.ParentId = vm.ParentId;

            // Image: a freshly uploaded file wins; otherwise keep the URL field value.
            if (imageFile != null && imageFile.Length > 0)
            {
                category.ImageUrl = await SaveCategoryImageAsync(imageFile);
            }
            else
            {
                category.ImageUrl = string.IsNullOrWhiteSpace(vm.ImageUrl) ? null : vm.ImageUrl.Trim();
            }

            var isNew = vm.Id == 0;
            await _db.SaveChangesAsync();
            await LogAsync(isNew ? "Create" : "Update", "Category", category.Id.ToString(),
                $"{(isNew ? "Created" : "Updated")} category '{category.Name}'");
            TempData["Success"] = $"Category '{category.Name}' saved.";
            return RedirectToAction(nameof(Index));
        }

        /// <summary>Persists a category image — Cloudinary when configured (survives redeploys on
        /// ephemeral hosts like Render), otherwise the local wwwroot/uploads folder (dev only).</summary>
        private async Task<string> SaveCategoryImageAsync(IFormFile file)
        {
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
                    Folder = "sterlinglams/categories",
                    PublicId = Guid.NewGuid().ToString("N"),
                    UniqueFilename = false,
                    Overwrite = false
                });
                if (result.StatusCode == System.Net.HttpStatusCode.OK && result.SecureUrl != null)
                    return result.SecureUrl.ToString();
            }

            // Fallback: local disk (Cloudinary not configured — dev only, NOT persistent on Render).
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            var dir = Path.Combine(_env.WebRootPath, "uploads", "categories");
            Directory.CreateDirectory(dir);
            var fileName = $"{Guid.NewGuid():N}{ext}";
            await using var stream = System.IO.File.Create(Path.Combine(dir, fileName));
            await file.CopyToAsync(stream);
            return $"/uploads/categories/{fileName}";
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var cat = await _db.Categories.Include(c => c.Products).FirstOrDefaultAsync(c => c.Id == id);
            if (cat == null) return NotFound();

            if (cat.Products.Any())
            {
                TempData["Error"] = "Cannot delete a category that has products assigned to it.";
                return RedirectToAction(nameof(Index));
            }

            var name = cat.Name;
            _db.Categories.Remove(cat);
            await _db.SaveChangesAsync();
            await LogAsync("Delete", "Category", id.ToString(), $"Deleted category '{name}'");
            TempData["Success"] = $"Category '{name}' deleted.";
            return RedirectToAction(nameof(Index));
        }
    }
}
