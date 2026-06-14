using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Safety net for paid online orders whose fulfilment didn't complete (a transient DB error, a
/// deadlock, or genuine insufficient stock). FulfilPaidOrderAsync never throws to the customer, so
/// without this a paid order could sit silently unfulfilled. This service periodically:
///   1. RETRIES fulfilment for every paid-but-unfulfilled order (FulfilPaidOrderAsync is idempotent),
///      which self-heals transient failures; then
///   2. ALERTS the admin (once) for any order still unfulfilled past a grace period — these need a
///      human (e.g. real stock shortage).
/// "Unfulfilled" = IsPaid &amp;&amp; FulfillingStoreId == null &amp;&amp; not Cancelled/Refunded.
/// </summary>
public class FulfilmentRetryService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AlertAfter = TimeSpan.FromMinutes(15); // grace before pinging a human
    private static readonly TimeSpan LookbackWindow = TimeSpan.FromDays(7);  // ignore ancient stuck orders

    private readonly IServiceProvider _sp;
    private readonly ILogger<FulfilmentRetryService> _logger;

    public FulfilmentRetryService(IServiceProvider sp, ILogger<FulfilmentRetryService> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Fulfilment retry sweep failed."); }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var since = DateTime.UtcNow - LookbackWindow;

        // ── 1. Retry ────────────────────────────────────────────────────────────
        // Each retry runs in its own scope so a failure on one order can't poison the next.
        List<int> stuckIds;
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            stuckIds = await UnfulfilledQuery(db, since).Select(o => o.Id).ToListAsync(ct);
        }

        foreach (var id in stuckIds)
        {
            using var scope = _sp.CreateScope();
            var fulfil = scope.ServiceProvider.GetRequiredService<IOrderFulfilmentService>();
            await fulfil.FulfilPaidOrderAsync(id); // idempotent; sets FulfillingStoreId on success
        }

        if (stuckIds.Count > 0)
            _logger.LogInformation("Fulfilment retry attempted for {Count} unfulfilled paid order(s).", stuckIds.Count);

        // ── 2. Alert (once) for orders still stuck past the grace period ─────────
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var cutoff = DateTime.UtcNow - AlertAfter;
            var needAlert = await UnfulfilledQuery(db, since)
                .Where(o => o.FulfilmentAlertedAt == null && o.PaidAt != null && o.PaidAt < cutoff)
                .ToListAsync(ct);
            if (needAlert.Count == 0) return;

            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            var email = scope.ServiceProvider.GetRequiredService<IEmailService>();
            var adminEmail = await settings.GetAsync("notifications.admin_email", "");

            if (!string.IsNullOrWhiteSpace(adminEmail))
            {
                string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
                var rows = string.Join("", needAlert.Select(o =>
                    $"<tr><td style=\"padding:6px 0;border-bottom:1px solid #f0efed;\">{Enc(o.OrderNumber)}</td>" +
                    $"<td align=\"right\" style=\"padding:6px 0;border-bottom:1px solid #f0efed;\">&#8358;{o.Total:N0}</td>" +
                    $"<td style=\"padding:6px 0 6px 16px;border-bottom:1px solid #f0efed;color:#78716c;\">paid {o.PaidAt:dd MMM HH:mm}</td></tr>"));
                var body = $@"
                    <h2 style=""font-size:18px;margin:0 0 12px;"">Paid orders awaiting fulfilment</h2>
                    <p>These orders are paid but could not be fulfilled automatically (likely insufficient stock).
                       Please review them in Admin → Orders and fulfil or refund.</p>
                    <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""margin:16px 0;font-size:14px;"">{rows}</table>";
                try
                {
                    await email.SendAsync(adminEmail, $"⚠ {needAlert.Count} paid order(s) need fulfilment", body, ct: ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send fulfilment alert email.");
                    return; // don't mark as alerted if the email didn't go out — try again next sweep
                }
            }
            else
            {
                _logger.LogWarning("{Count} paid order(s) need fulfilment but notifications.admin_email is not set.", needAlert.Count);
            }

            var now = DateTime.UtcNow;
            foreach (var o in needAlert) o.FulfilmentAlertedAt = now;
            await db.SaveChangesAsync(ct);
        }
    }

    private static IQueryable<Order> UnfulfilledQuery(ApplicationDbContext db, DateTime since) =>
        db.Orders.Where(o => o.Channel == OrderChannel.Online   // POS sales are settled at the till, never via this pipeline
            && o.IsPaid && o.FulfillingStoreId == null
            && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Refunded
            && o.PaidAt != null && o.PaidAt >= since);
}
