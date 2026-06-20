using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

public class TillController : InventoryAreaController
{
    private readonly ApplicationDbContext _db;
    private readonly ISettingsService _settings;
    public TillController(ApplicationDbContext db, ISettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    // Till oversight: cash-up sessions across all branches + today's POS totals.
    public async Task<IActionResult> Index(int? storeId = null, string status = "all")
    {
        ViewData["Title"] = "POS";
        var today = DateTime.UtcNow.Date;

        ViewBag.Stores = await _db.Stores.Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
        ViewBag.StoreId = storeId;
        ViewBag.Status = status;

        var sessionsQuery = _db.TillSessions
            .Include(s => s.Register).ThenInclude(r => r.Store)
            .AsQueryable();

        if (storeId.HasValue)
            sessionsQuery = sessionsQuery.Where(s => s.Register.StoreId == storeId.Value);

        sessionsQuery = status switch
        {
            "open" => sessionsQuery.Where(s => s.ClosedAt == null),
            "closed" => sessionsQuery.Where(s => s.ClosedAt != null),
            _ => sessionsQuery
        };

        var sessions = await sessionsQuery
            .OrderByDescending(s => s.OpenedAt).Take(100).ToListAsync();
        var ids = sessions.Select(s => s.Id).ToList();

        var sales = await _db.Orders.Where(o => o.TillSessionId != null && ids.Contains(o.TillSessionId!.Value))
            .GroupBy(o => o.TillSessionId!.Value)
            .Select(g => new { Id = g.Key, Total = g.Sum(x => x.Total), Count = g.Count(),
                               Cash = g.Where(x => x.PaymentProvider == "Cash").Sum(x => x.Total) })
            .ToListAsync();
        var refunds = await _db.Refunds.Where(r => r.TillSessionId != null && ids.Contains(r.TillSessionId!.Value))
            .GroupBy(r => r.TillSessionId!.Value)
            .Select(g => new { Id = g.Key, CashRef = g.Where(x => x.RefundMethod == "Cash").Sum(x => x.Amount) })
            .ToListAsync();

        var vm = new TillOversightViewModel
        {
            OpenSessions = sessions.Count(s => s.ClosedAt == null),
            Rows = sessions.Select(s =>
            {
                var sa = sales.FirstOrDefault(x => x.Id == s.Id);
                var rf = refunds.FirstOrDefault(x => x.Id == s.Id);
                var expected = s.OpeningFloat + (sa?.Cash ?? 0) - (rf?.CashRef ?? 0);
                return new TillSessionRow
                {
                    Session = s,
                    SaleCount = sa?.Count ?? 0,
                    Sales = sa?.Total ?? 0,
                    ExpectedCash = expected,
                    Variance = s.ClosedAt.HasValue ? (s.CountedCash ?? 0) - expected : (decimal?)null
                };
            }).ToList()
        };

        var posToday = _db.Orders.Where(o => o.Channel == OrderChannel.Pos && o.CreatedAt >= today);
        vm.SalesToday = await posToday.SumAsync(o => (decimal?)o.Total) ?? 0;
        vm.TxToday = await posToday.CountAsync();

        return View(vm);
    }

    // ── POS Discount Reasons (+ presets) ──────────────────────────────────────
    public async Task<IActionResult> DiscountReasons()
    {
        ViewData["Title"] = "POS Discount Reasons";
        var reasons = await _db.PosDiscountReasons
            .Include(r => r.Presets.OrderBy(p => p.SortOrder))
            .OrderBy(r => r.SortOrder)
            .ToListAsync();
        return View(reasons);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateReason(string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var maxSort = await _db.PosDiscountReasons.MaxAsync(r => (int?)r.SortOrder) ?? 0;
            var reason = new PosDiscountReason { Name = name.Trim(), SortOrder = maxSort + 10, IsActive = true };
            _db.PosDiscountReasons.Add(reason);
            await _db.SaveChangesAsync();
            await LogAsync("Create", "PosDiscountReason", reason.Id.ToString(), $"Created POS discount reason '{reason.Name}'");
        }
        return RedirectToAction(nameof(DiscountReasons));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EditReason(int id, string name, bool isActive)
    {
        var reason = await _db.PosDiscountReasons.FindAsync(id);
        if (reason != null && !string.IsNullOrWhiteSpace(name))
        {
            reason.Name = name.Trim();
            reason.IsActive = isActive;
            await _db.SaveChangesAsync();
            await LogAsync("Update", "PosDiscountReason", id.ToString(), $"Updated POS discount reason '{reason.Name}' (active={isActive})");
        }
        return RedirectToAction(nameof(DiscountReasons));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteReason(int id)
    {
        var reason = await _db.PosDiscountReasons.FindAsync(id);
        if (reason != null)
        {
            var name = reason.Name;
            _db.PosDiscountReasons.Remove(reason);
            await _db.SaveChangesAsync();
            await LogAsync("Delete", "PosDiscountReason", id.ToString(), $"Deleted POS discount reason '{name}'");
        }
        return RedirectToAction(nameof(DiscountReasons));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreatePreset(int reasonId, string label, string type, decimal value)
    {
        if (!string.IsNullOrWhiteSpace(label) && value > 0)
        {
            var maxSort = await _db.PosDiscountPresets.Where(p => p.ReasonId == reasonId).MaxAsync(p => (int?)p.SortOrder) ?? 0;
            var preset = new PosDiscountPreset
            {
                ReasonId = reasonId, Label = label.Trim(),
                Type = type, Value = value, SortOrder = maxSort + 10
            };
            _db.PosDiscountPresets.Add(preset);
            await _db.SaveChangesAsync();
            await LogAsync("Create", "PosDiscountPreset", preset.Id.ToString(), $"Added POS discount preset '{preset.Label}' ({type} {value}) to reason {reasonId}");
        }
        return RedirectToAction(nameof(DiscountReasons));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeletePreset(int id)
    {
        var preset = await _db.PosDiscountPresets.FindAsync(id);
        if (preset != null)
        {
            var label = preset.Label;
            _db.PosDiscountPresets.Remove(preset);
            await _db.SaveChangesAsync();
            await LogAsync("Delete", "PosDiscountPreset", id.ToString(), $"Deleted POS discount preset '{label}'");
        }
        return RedirectToAction(nameof(DiscountReasons));
    }

    // ── POS settings (receipt header/footer) ──────────────────────────────────
    public async Task<IActionResult> Settings()
    {
        ViewData["Title"] = "POS Settings";
        ViewBag.ReceiptHeader = await _settings.GetAsync("pos.receipt_header", "");
        ViewBag.ReceiptFooter = await _settings.GetAsync("pos.receipt_footer", "Thank you for shopping with us!");
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSettings(string? receiptHeader, string? receiptFooter)
    {
        await _settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["pos.receipt_header"] = receiptHeader?.Trim() ?? "",
            ["pos.receipt_footer"] = receiptFooter?.Trim() ?? ""
        });
        await LogAsync("Update", "Setting", null, "Updated POS receipt settings");
        TempData["Success"] = "POS settings saved.";
        return RedirectToAction(nameof(Settings));
    }
}

public class TillOversightViewModel
{
    public int OpenSessions { get; set; }
    public decimal SalesToday { get; set; }
    public int TxToday { get; set; }
    public List<TillSessionRow> Rows { get; set; } = new();
}
public class TillSessionRow
{
    public TillSession Session { get; set; } = null!;
    public int SaleCount { get; set; }
    public decimal Sales { get; set; }
    public decimal ExpectedCash { get; set; }
    public decimal? Variance { get; set; }
}
