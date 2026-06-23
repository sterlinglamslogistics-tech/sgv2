using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using SterlingLams.Web.Data;

namespace SterlingLams.Web.Controllers;

// Public store-pickup pass: the customer opens /pickup/{token} (from the "ready for pickup" email)
// on their phone and shows the QR at the till. The token is the secret — no login required.
[AllowAnonymous]
public class PickupController : Controller
{
    private readonly ApplicationDbContext _db;
    public PickupController(ApplicationDbContext db) => _db = db;

    [HttpGet("/pickup/{token}")]
    public async Task<IActionResult> Pass(string token)
    {
        var order = await _db.Orders
            .Include(o => o.Items)
            .Include(o => o.PickupStore)
            .Include(o => o.User)
            .Include(o => o.Customer)
            .FirstOrDefaultAsync(o => o.PickupToken == token);
        if (order == null) return View("Invalid");
        return View(order);
    }

    // QR image (cross-platform PNG via QRCoder, no System.Drawing). Encodes the scan code the till
    // recognises: "SGPICK-<token>".
    [HttpGet("/pickup/{token}/qr.png")]
    public async Task<IActionResult> Qr(string token)
    {
        if (!await _db.Orders.AnyAsync(o => o.PickupToken == token)) return NotFound();
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode("SGPICK-" + token, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10);
        Response.Headers.CacheControl = "private,max-age=300";
        return File(png, "image/png");
    }
}
