using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers;

/// <summary>
/// Distance-based delivery zones for Lagos &amp; Abuja. Each zone carries its own Standard + Express
/// fees/timeframes and the list of areas it covers; the whole set is persisted as the
/// <c>shipping.delivery_zones</c> JSON setting (no schema/migration needed).
/// </summary>
public class DeliveryZonesController : AdminBaseController
{
    protected override string Section => "Settings";
    protected override bool EnforceManageOnWrite => false; // parity with SettingsController

    private readonly DeliveryZoneService _zones;
    private readonly ISettingsService _settings;

    public DeliveryZonesController(DeliveryZoneService zones, ISettingsService settings)
    {
        _zones = zones;
        _settings = settings;
    }

    public async Task<IActionResult> Index()
    {
        var zones = await _zones.GetZonesAsync();
        return View(zones);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save([FromBody] List<DeliveryZoneDef>? zones)
    {
        var clean = (zones ?? new())
            .Where(z => !string.IsNullOrWhiteSpace(z.Name))
            .Select(z => new DeliveryZoneDef
            {
                State        = string.Equals(z.State, "Abuja", StringComparison.OrdinalIgnoreCase) ? "Abuja" : "Lagos",
                Name         = z.Name.Trim(),
                StandardFee  = Math.Max(0, z.StandardFee),
                ExpressFee   = Math.Max(0, z.ExpressFee),
                StandardDays = string.IsNullOrWhiteSpace(z.StandardDays) ? "2 - 4 working days" : z.StandardDays.Trim(),
                ExpressDays  = string.IsNullOrWhiteSpace(z.ExpressDays)  ? "24 - 48 hours"     : z.ExpressDays.Trim(),
                Areas        = (z.Areas ?? new())
                    .Select(a => (a ?? "").Trim())
                    .Where(a => a.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .ToList();

        var json = JsonSerializer.Serialize(clean);
        await _settings.SaveManyAsync(new Dictionary<string, string> { ["shipping.delivery_zones"] = json });

        await LogAsync("Update", "Setting", "shipping.delivery_zones",
            $"Updated delivery zones ({clean.Count} zone(s): "
            + $"{clean.Count(z => z.State == "Lagos")} Lagos, {clean.Count(z => z.State == "Abuja")} Abuja)");

        return Json(new { ok = true, count = clean.Count });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Reset()
    {
        // Return the built-in defaults to the editor (does not persist until Save).
        return Json(new { ok = true, zones = DeliveryZoneService.DefaultZones() });
    }
}
