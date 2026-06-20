using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SterlingLams.Web.Controllers;

/// <summary>
/// Keeps old POS URLs working after the Stage 1/2 refactor:
///  • the selling app moved /Till → /Pos
///  • POS management moved out of the website Admin into the Inventory System
/// Any legacy /Till*, /Admin/Pos* or /Admin/Registers* request is redirected to its new home.
/// </summary>
[AllowAnonymous]
public class TillRedirectController : Controller
{
    // Selling app: /Till* → /Pos* (preserve tail + query)
    [Route("/Till")]
    [Route("/Till/{**rest}")]
    public IActionResult ToPos()
    {
        var path = Request.Path.Value ?? "/Till";
        var tail = path.Length >= 5 ? path.Substring(5) : string.Empty; // strip "/Till"
        return Redirect("/Pos" + tail + Request.QueryString);
    }

    // POS management moved to the Inventory System.
    [Route("/Admin/Pos")]
    [Route("/Admin/Pos/{**rest}")]
    public IActionResult AdminPosToInventory(string? rest)
    {
        rest = (rest ?? string.Empty).Trim('/');
        // Receipt printing now lives in the POS app.
        if (rest.StartsWith("Receipt/", System.StringComparison.OrdinalIgnoreCase))
            return Redirect("/Pos/" + rest);
        if (rest.StartsWith("DiscountReasons", System.StringComparison.OrdinalIgnoreCase))
            return Redirect("/Inventory/Till/DiscountReasons");
        if (rest.StartsWith("Sales", System.StringComparison.OrdinalIgnoreCase))
            return Redirect("/Inventory/Sales/Completed");
        // Sessions / Index / anything else → POS oversight
        return Redirect("/Inventory/Till");
    }

    // Register management moved to the Inventory System.
    [Route("/Admin/Registers")]
    [Route("/Admin/Registers/{**rest}")]
    public IActionResult AdminRegistersToInventory() => Redirect("/Inventory/Org/Registers");
}
