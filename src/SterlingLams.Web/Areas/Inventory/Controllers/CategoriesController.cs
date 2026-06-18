using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

// Read view of product categories with stock rollups (count / units / retail value).
// Editing categories stays in the Website Admin.
public class CategoriesController : InventoryAreaController
{
    private readonly ApplicationDbContext _db;
    public CategoriesController(ApplicationDbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Categories";

        // Per-active-product units + price, grouped in memory (avoids nested-Sum translation issues).
        var prod = await _db.Products.Where(p => p.IsActive)
            .Select(p => new { p.CategoryId, p.Price, Units = p.StoreInventories.Sum(si => (int?)si.QuantityOnHand) ?? 0 })
            .ToListAsync();
        var byCat = prod.GroupBy(p => p.CategoryId).ToDictionary(g => g.Key, g => new
        {
            Products = g.Count(),
            Units = g.Sum(x => x.Units),
            Value = g.Sum(x => x.Units * x.Price)
        });

        var cats = await _db.Categories.OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new { c.Id, c.Name, Parent = c.Parent != null ? c.Parent.Name : null, c.IsActive })
            .ToListAsync();

        var rows = cats.Select(c =>
        {
            byCat.TryGetValue(c.Id, out var s);
            return new CategoryRow
            {
                Name = c.Name,
                Parent = c.Parent,
                IsActive = c.IsActive,
                Products = s?.Products ?? 0,
                Units = s?.Units ?? 0,
                Value = s?.Value ?? 0
            };
        }).ToList();

        return View(rows);
    }
}

public class CategoryRow
{
    public string Name { get; set; } = "";
    public string? Parent { get; set; }
    public bool IsActive { get; set; }
    public int Products { get; set; }
    public int Units { get; set; }
    public decimal Value { get; set; }
}
