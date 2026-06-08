using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Admin.Controllers;

public class RegistersController : AdminBaseController
{
    protected override string Section => "Stores";

    private readonly ApplicationDbContext _db;
    public RegistersController(ApplicationDbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Tills";
        ViewBag.Stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        var registers = await _db.Registers.Include(r => r.Store)
            .OrderBy(r => r.Store.Name).ThenBy(r => r.Name).ToListAsync();
        return View(registers);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string name, int storeId)
    {
        if (string.IsNullOrWhiteSpace(name) || !await _db.Stores.AnyAsync(s => s.Id == storeId))
        {
            TempData["Error"] = "Please enter a till name and choose a branch.";
            return RedirectToAction(nameof(Index));
        }
        _db.Registers.Add(new Register { Name = name.Trim(), StoreId = storeId, IsActive = true });
        await _db.SaveChangesAsync();
        await LogAsync("Create", "Register", null, $"Added till '{name.Trim()}'");
        TempData["Success"] = "Till added.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(int id)
    {
        var r = await _db.Registers.FindAsync(id);
        if (r == null) return NotFound();
        r.IsActive = !r.IsActive;
        await _db.SaveChangesAsync();
        await LogAsync("Update", "Register", id.ToString(), $"{(r.IsActive ? "Enabled" : "Disabled")} till '{r.Name}'");
        return RedirectToAction(nameof(Index));
    }
}
