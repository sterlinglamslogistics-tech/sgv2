using Microsoft.AspNetCore.Mvc;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

// Placeholder target for Moniebook-style menu items that aren't built yet.
// Each unbuilt sidebar link points here with ?feature=<name>. See docs/moniebook/05_ROADMAP.md.
public class SoonController : InventoryAreaController
{
    public IActionResult Index(string? feature)
    {
        ViewData["Title"] = string.IsNullOrWhiteSpace(feature) ? "Coming soon" : feature;
        ViewBag.Feature = string.IsNullOrWhiteSpace(feature) ? "This feature" : feature;
        return View();
    }
}
