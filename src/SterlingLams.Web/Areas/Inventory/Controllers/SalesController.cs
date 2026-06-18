using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Inventory.Controllers;

// Moniebook-style sales screens inside the Inventory System: Completed (paid) sales, Outstanding
// (awaiting payment) sales, and Saved (parked) carts. Read views over the existing Orders/ParkedSales.
public class SalesController : InventoryAreaController
{
    private readonly ApplicationDbContext _db;
    private const int PageSize = 30;
    public SalesController(ApplicationDbContext db) => _db = db;

    public Task<IActionResult> Completed(DateTime? from = null, DateTime? to = null, int? storeId = null, string q = "", int page = 1)
        => ListAsync("Completed", from, to, storeId, q, page);

    public Task<IActionResult> Outstanding(DateTime? from = null, DateTime? to = null, int? storeId = null, string q = "", int page = 1)
        => ListAsync("Outstanding", from, to, storeId, q, page);

    private async Task<IActionResult> ListAsync(string view, DateTime? from, DateTime? to, int? storeId, string q, int page)
    {
        ViewData["Title"] = view == "Completed" ? "Completed sales" : "Outstanding sales";

        var qy = _db.Orders.AsQueryable();
        qy = view == "Completed"
            ? qy.Where(o => o.PaidAt != null && o.Status != OrderStatus.Cancelled)
            : qy.Where(o => o.PaidAt == null && o.Status != OrderStatus.Cancelled);

        if (from.HasValue) { var f = DateTime.SpecifyKind(from.Value.Date, DateTimeKind.Utc); qy = qy.Where(o => o.CreatedAt >= f); }
        if (to.HasValue) { var t = DateTime.SpecifyKind(to.Value.Date, DateTimeKind.Utc).AddDays(1); qy = qy.Where(o => o.CreatedAt < t); }
        if (storeId.HasValue) qy = qy.Where(o => o.FulfillingStoreId == storeId.Value || o.PickupStoreId == storeId.Value);
        if (!string.IsNullOrWhiteSpace(q)) qy = qy.Where(o => EF.Functions.ILike(o.OrderNumber, $"%{q}%"));

        var total = await qy.CountAsync();
        var sumValue = await qy.SumAsync(o => (decimal?)o.Total) ?? 0;

        var rows = await qy.OrderByDescending(o => o.Id)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(o => new SaleRow
            {
                Id = o.Id,
                OrderNumber = o.OrderNumber,
                Channel = o.Channel.ToString(),
                Status = o.Status.ToString(),
                Total = o.Total,
                Paid = o.PaidAt != null,
                PaymentProvider = o.PaymentProvider,
                When = o.CreatedAt,
                Customer = o.CustomerUserId != null
                    ? _db.Users.Where(u => u.Id == o.CustomerUserId).Select(u => u.Email).FirstOrDefault()
                    : (o.Channel == OrderChannel.Online
                        ? _db.Users.Where(u => u.Id == o.UserId).Select(u => u.Email).FirstOrDefault()
                        : null)
            }).ToListAsync();

        ViewBag.Stores = await _db.Stores.OrderBy(s => s.Name).ToListAsync();
        ViewBag.View = view;
        ViewBag.From = from; ViewBag.To = to; ViewBag.StoreId = storeId; ViewBag.Query = q;
        ViewBag.Page = page; ViewBag.TotalPages = (int)Math.Ceiling(total / (double)PageSize);
        ViewBag.Total = total; ViewBag.SumValue = sumValue;
        return View("List", rows);
    }

    public async Task<IActionResult> Saved(int page = 1)
    {
        ViewData["Title"] = "Saved carts";
        var qy = _db.ParkedSales.AsQueryable();
        var total = await qy.CountAsync();
        var stores = await _db.Stores.ToDictionaryAsync(s => s.Id, s => s.Name.Replace("Sterlin Glams ", ""));
        var rows = await qy.OrderByDescending(s => s.Id)
            .Skip((page - 1) * PageSize).Take(PageSize).ToListAsync();
        ViewBag.Stores = stores;
        ViewBag.Page = page; ViewBag.TotalPages = (int)Math.Ceiling(total / (double)PageSize); ViewBag.Total = total;
        return View(rows);
    }

    public async Task<IActionResult> Detail(int id)
    {
        var order = await _db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id);
        if (order == null) return NotFound();
        ViewData["Title"] = order.OrderNumber;

        if (order.FulfillingStoreId.HasValue || order.PickupStoreId.HasValue)
            ViewBag.Store = await _db.Stores.Where(s => s.Id == (order.FulfillingStoreId ?? order.PickupStoreId))
                .Select(s => s.Name).FirstOrDefaultAsync();
        var custId = order.CustomerUserId ?? (order.Channel == OrderChannel.Online ? order.UserId : null);
        if (custId != null)
            ViewBag.Customer = await _db.Users.Where(u => u.Id == custId).Select(u => u.Email).FirstOrDefaultAsync();
        ViewBag.Cashier = order.Channel == OrderChannel.Pos
            ? await _db.Users.Where(u => u.Id == order.UserId).Select(u => u.Email).FirstOrDefaultAsync() : null;
        return View(order);
    }
}

public class SaleRow
{
    public int Id { get; set; }
    public string OrderNumber { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Status { get; set; } = "";
    public decimal Total { get; set; }
    public bool Paid { get; set; }
    public string? PaymentProvider { get; set; }
    public DateTime When { get; set; }
    public string? Customer { get; set; }
}
