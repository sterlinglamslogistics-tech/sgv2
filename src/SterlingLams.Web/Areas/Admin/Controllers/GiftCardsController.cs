using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Areas.Admin.Controllers;

/// <summary>Issue and manage gift cards. Financial — full administrators only (Section = null).</summary>
public class GiftCardsController : AdminBaseController
{
    protected override string? Section => "GiftCards";

    private readonly ApplicationDbContext _db;
    private readonly IGiftCardService _giftCards;

    public GiftCardsController(ApplicationDbContext db, IGiftCardService giftCards)
    {
        _db = db;
        _giftCards = giftCards;
    }

    public async Task<IActionResult> Index(string? q, string status = "all", int page = 1)
    {
        const int size = 25;
        ViewData["Title"] = "Gift Cards";

        var query = _db.GiftCards.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var norm = q.Trim().ToUpperInvariant();
            query = query.Where(g => g.Code.Contains(norm)
                || (g.RecipientName != null && g.RecipientName.ToUpper().Contains(norm))
                || (g.RecipientEmail != null && g.RecipientEmail.ToUpper().Contains(norm)));
        }
        var now = DateTime.UtcNow;
        query = status switch
        {
            "active"   => query.Where(g => g.IsActive && g.Balance > 0 && (g.ExpiresAt == null || g.ExpiresAt > now)),
            "spent"    => query.Where(g => g.Balance <= 0),
            "inactive" => query.Where(g => !g.IsActive),
            "expired"  => query.Where(g => g.ExpiresAt != null && g.ExpiresAt <= now),
            _          => query
        };

        var total = await query.CountAsync();
        var rows = await query.OrderByDescending(g => g.CreatedAt)
            .Skip((page - 1) * size).Take(size)
            .ToListAsync();

        // Summary across all cards (unaffected by paging/filter).
        ViewBag.OutstandingBalance = await _db.GiftCards
            .Where(g => g.IsActive && g.Balance > 0 && (g.ExpiresAt == null || g.ExpiresAt > now))
            .SumAsync(g => (decimal?)g.Balance) ?? 0m;
        ViewBag.TotalIssued = await _db.GiftCards.SumAsync(g => (decimal?)g.InitialAmount) ?? 0m;
        ViewBag.ActiveCount = await _db.GiftCards.CountAsync(g => g.IsActive && g.Balance > 0 && (g.ExpiresAt == null || g.ExpiresAt > now));

        ViewBag.Q = q; ViewBag.Status = status; ViewBag.Page = page;
        ViewBag.TotalPages = (int)Math.Ceiling(total / (double)size); ViewBag.Total = total;
        return View(rows);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Issue(decimal amount, string? recipientName, string? recipientEmail,
        string? note, DateTime? expiresAt)
    {
        if (amount <= 0)
        {
            TempData["Error"] = "Enter a gift card amount greater than zero.";
            return RedirectToAction(nameof(Index));
        }
        var byUser = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var card = await _giftCards.IssueAsync(amount, recipientName, recipientEmail, note, expiresAt, byUser);
        await LogAsync("Create", "GiftCard", card.Id.ToString(), $"Issued gift card {card.Code} for ₦{amount:N0}");
        TempData["Message"] = $"Gift card {card.Code} issued for ₦{amount:N0}.";
        TempData["NewCardCode"] = card.Code;
        return RedirectToAction(nameof(Details), new { id = card.Id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var card = await _db.GiftCards.AsNoTracking()
            .Include(g => g.Transactions)
            .FirstOrDefaultAsync(g => g.Id == id);
        if (card is null) return NotFound();
        ViewData["Title"] = $"Gift Card {card.Code}";
        card.Transactions = card.Transactions.OrderByDescending(t => t.CreatedAt).ToList();
        return View(card);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetActive(int id, bool active)
    {
        var card = await _db.GiftCards.FindAsync(id);
        if (card != null)
        {
            card.IsActive = active;
            await _db.SaveChangesAsync();
            await LogAsync("Update", "GiftCard", id.ToString(), $"{(active ? "Reactivated" : "Deactivated")} gift card {card.Code}");
            TempData["Message"] = active ? "Gift card reactivated." : "Gift card deactivated.";
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Manual balance correction / top-up by an admin (recorded in the ledger).</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Adjust(int id, decimal amount, string? reason)
    {
        var card = await _db.GiftCards.Include(g => g.Transactions).FirstOrDefaultAsync(g => g.Id == id);
        if (card is null) return NotFound();
        if (amount == 0)
        {
            TempData["Error"] = "Enter a non-zero adjustment.";
            return RedirectToAction(nameof(Details), new { id });
        }
        // Never let a debit push the balance below zero.
        if (amount < 0) amount = -Math.Min(-amount, card.Balance);
        if (amount == 0)
        {
            TempData["Error"] = "That debit would take the balance below zero.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var now = DateTime.UtcNow;
        card.Balance += amount;
        card.Transactions.Add(new GiftCardTransaction
        {
            Amount = amount,
            Type = GiftCardTxnType.Adjust,
            Note = string.IsNullOrWhiteSpace(reason) ? "Manual adjustment" : reason.Trim(),
            CreatedAt = now
        });
        await _db.SaveChangesAsync();
        await LogAsync("Update", "GiftCard", id.ToString(), $"Adjusted gift card {card.Code} by ₦{amount:N0}");
        TempData["Message"] = $"Balance adjusted by ₦{amount:N0}.";
        return RedirectToAction(nameof(Details), new { id });
    }
}
