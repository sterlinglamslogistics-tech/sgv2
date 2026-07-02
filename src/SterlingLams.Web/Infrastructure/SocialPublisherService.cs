using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;
using SterlingLams.Web.Services.Social;

namespace SterlingLams.Web.Infrastructure;

/// <summary>
/// Publishes due scheduled social posts. Dormant until an account is connected (ISocialPublisher.
/// IsEnabled) — while dormant, scheduled posts simply wait in the calendar. Once publishing is
/// enabled it picks up posts whose ScheduledAt has passed and marks them Published/Failed.
/// </summary>
public class SocialPublisherService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<SocialPublisherService> _logger;

    public SocialPublisherService(IServiceProvider sp, ILogger<SocialPublisherService> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch { }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Social publish sweep failed."); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch { }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<ISocialPublisher>();
        if (!publisher.IsEnabled) return; // dormant: posts wait in the calendar until accounts are connected

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = DateTime.UtcNow;
        var due = await db.SocialPosts
            .Where(p => p.Status == SocialPostStatus.Scheduled && p.ScheduledAt != null && p.ScheduledAt <= now)
            .OrderBy(p => p.ScheduledAt).Take(20).ToListAsync(ct);
        if (due.Count == 0) return;

        foreach (var p in due)
        {
            var (ok, error) = await publisher.PublishAsync(p, ct);
            p.Status = ok ? SocialPostStatus.Published : SocialPostStatus.Failed;
            p.PublishedAt = ok ? DateTime.UtcNow : null;
            p.Error = ok ? null : error;
            p.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Social publish sweep: processed {Count} post(s).", due.Count);
    }
}
