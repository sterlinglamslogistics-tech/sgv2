using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using SterlingLams.Web.Data;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Controllers;

/// <summary>
/// Public, tokenised transfer manifest — a driver/receiver with no account opens it via the QR or
/// the shared link (the token is data-protected, so it can't be guessed or tampered with). Renders
/// the same manifest sheet used inside the Inventory System.
/// </summary>
[AllowAnonymous]
public class TransferManifestController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IManifestTokenService _tokens;
    public TransferManifestController(ApplicationDbContext db, IManifestTokenService tokens)
    {
        _db = db;
        _tokens = tokens;
    }

    [HttpGet("/t/manifest/{token}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Public(string token)
    {
        var id = _tokens.Unprotect(token);
        if (id is not int transferId) return NotFound();

        var transfer = await _db.StockTransfers
            .Include(t => t.FromStore).Include(t => t.ToStore).Include(t => t.Items)
            .FirstOrDefaultAsync(t => t.Id == transferId);
        // Only approved transfers have a manifest; a bad/expired token or unapproved transfer 404s.
        if (transfer == null || transfer.ApprovedAt == null) return NotFound();

        var productIds = transfer.Items.Select(i => i.ProductId).Distinct().ToList();
        var imgRows = await _db.ProductImages
            .Where(img => productIds.Contains(img.ProductId))
            .OrderByDescending(img => img.IsPrimary).ThenBy(img => img.SortOrder)
            .Select(img => new { img.ProductId, img.Url })
            .ToListAsync();
        ViewBag.ImageByProduct = imgRows.GroupBy(x => x.ProductId).ToDictionary(g => g.Key, g => g.First().Url);

        var uids = new[] { transfer.CreatedByUserId, transfer.ApprovedByUserId }
            .Where(u => u != null).Cast<string>().Distinct().ToList();
        ViewBag.Names = (await _db.Users.Where(u => uids.Contains(u.Id))
                .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email }).ToListAsync())
            .ToDictionary(u => u.Id, u =>
            {
                var n = $"{u.FirstName} {u.LastName}".Trim();
                return string.IsNullOrWhiteSpace(n) ? (u.Email ?? u.Id) : n;
            });

        var publicUrl = $"{Request.Scheme}://{Request.Host}/t/manifest/{token}";
        using var gen = new QRCodeGenerator();
        using var qrData = gen.CreateQrCode(publicUrl, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(qrData).GetGraphic(5);
        ViewBag.QrDataUri = "data:image/png;base64," + Convert.ToBase64String(png);
        ViewBag.PublicUrl = publicUrl;

        ViewData["Title"] = $"Manifest {transfer.TransferNumber}";
        return View("~/Areas/Inventory/Views/Transfers/Manifest.cshtml", transfer);
    }
}
