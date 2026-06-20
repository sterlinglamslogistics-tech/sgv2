using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Periodically cancels online orders that were placed but never paid (the customer abandoned
/// checkout) once they pass the unpaid timeout — default 60 minutes, overridable via the
/// order.unpaid_cancel_minutes setting. Unpaid online orders hold no stock (it's only deducted on
/// payment / staff confirmation), so cancelling just closes the order; any legacy reservation rows
/// are released defensively.
/// </summary>
public class ReservationSweeper : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private const int DefaultTtlMinutes = 60; // cancel an unpaid order after this long

    private readonly IServiceProvider _sp;
    private readonly ILogger<ReservationSweeper> _logger;

    public ReservationSweeper(IServiceProvider sp, ILogger<ReservationSweeper> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Reservation sweep failed."); }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var fulfil = scope.ServiceProvider.GetRequiredService<IOrderFulfilmentService>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();

        var ttlMinutes = (int)await settings.GetDecimalAsync("order.reservation_timeout_minutes", DefaultTtlMinutes);
        if (ttlMinutes < 1) ttlMinutes = DefaultTtlMinutes;
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(ttlMinutes);

        // Unpaid, still-pending online orders past the cutoff. (POS sales are paid at the till;
        // staff-confirmed orders leave Pending, so they're never swept.)
        var staleOrderIds = await db.Orders
            .Where(o => o.Channel == OrderChannel.Online
                     && !o.IsPaid
                     && o.Status == OrderStatus.Pending
                     && o.CreatedAt < cutoff)
            .Select(o => o.Id)
            .ToListAsync(ct);

        foreach (var id in staleOrderIds)
        {
            await fulfil.ReleaseReservationAsync(id); // defensive: release any legacy reservation rows
            var order = await db.Orders.FindAsync(new object[] { id }, ct);
            if (order != null && !order.IsPaid && order.Status == OrderStatus.Pending)
            {
                order.Status = OrderStatus.Cancelled;
                order.UpdatedAt = DateTime.UtcNow;
                OrderNotes.AddSystem(db, id, $"Order auto-cancelled: payment not received within {ttlMinutes} minutes.");
                await db.SaveChangesAsync(ct);
                try { await audit.LogAsync("Cancel", "Order", id.ToString(), $"Order {order.OrderNumber} auto-cancelled — unpaid for over {ttlMinutes} min"); } catch { }
            }
        }

        if (staleOrderIds.Count > 0)
            _logger.LogInformation("Auto-cancelled {Count} unpaid order(s) past the {Ttl}-minute timeout.", staleOrderIds.Count, ttlMinutes);
    }
}
