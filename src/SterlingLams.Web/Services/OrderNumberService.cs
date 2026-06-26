using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Services;

/// <summary>Generates short, sequential order numbers like "SL-30012" / "POS-30013" from a single
/// shared database sequence (so they run in order across both channels and never collide). The
/// per-channel prefix is editable in Settings.</summary>
public interface IOrderNumberService
{
    Task<string> NextAsync(OrderChannel channel);
}

public class OrderNumberService : IOrderNumberService
{
    private readonly ApplicationDbContext _db;
    private readonly ISettingsService _settings;

    public OrderNumberService(ApplicationDbContext db, ISettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    public async Task<string> NextAsync(OrderChannel channel)
    {
        var prefix = channel == OrderChannel.Pos
            ? await _settings.GetAsync("order.pos_number_prefix", "POS-")
            : await _settings.GetAsync("order.number_prefix", "SL-");

        long n;
        if (_db.Database.IsNpgsql())
        {
            // Atomic + concurrency-safe: the DB sequence hands out each value exactly once.
            n = await _db.Database.SqlQueryRaw<long>("SELECT nextval('order_number_seq') AS \"Value\"").SingleAsync();
        }
        else
        {
            // Non-Postgres (e.g. the SQLite test harness): fall back to a count-based number.
            n = 30000 + await _db.Orders.CountAsync() + 1;
        }

        return $"{prefix}{n}";
    }
}
