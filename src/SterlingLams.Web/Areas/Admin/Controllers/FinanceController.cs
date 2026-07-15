using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Areas.Admin.Controllers;

/// <summary>
/// Finance department dashboard: money in (paid sales), refunds, net, delivery/logistics fees —
/// broken down over time and by payment channel, branch and cashier, with charts and a CSV export.
/// Read-only. It's its own grantable "Finance" section so a Finance role can be given this alone.
/// </summary>
public class FinanceController : AdminBaseController
{
    protected override string Section => "Finance";

    private readonly ApplicationDbContext _db;
    private readonly SterlingLams.Web.Services.ILoyaltyService _loyalty;
    private readonly SterlingLams.Web.Services.ISettingsService _settings;
    private readonly SterlingLams.Web.Infrastructure.IFinanceReportService _report;
    public FinanceController(ApplicationDbContext db, SterlingLams.Web.Services.ILoyaltyService loyalty,
        SterlingLams.Web.Services.ISettingsService settings, SterlingLams.Web.Infrastructure.IFinanceReportService report)
    {
        _db = db;
        _loyalty = loyalty;
        _settings = settings;
        _report = report;
    }

    // Inclusive from/to (days). Defaults to the last 30 days.
    private static (DateTime From, DateTime ToExclusive) Range(string? from, string? to)
    {
        var today = DateTime.UtcNow.Date;
        var f = DateTime.TryParse(from, out var pf) ? pf.Date : today.AddDays(-29);
        var t = DateTime.TryParse(to, out var pt) ? pt.Date : today;
        if (t < f) t = f;
        return (DateTime.SpecifyKind(f, DateTimeKind.Utc), DateTime.SpecifyKind(t.AddDays(1), DateTimeKind.Utc));
    }

    public record ChannelPoint(string Channel, decimal Amount, int Count);
    // Order revenue = merchandise (Total minus the delivery charge); Logistics = the in-house
    // delivery fee we collect. Total = the two combined; Net = Total minus refunds.
    public record DayPoint(DateTime Day, int Count, decimal OrderRevenue, decimal Logistics, decimal Refunds)
    { public decimal Total => OrderRevenue + Logistics; public decimal Net => Total - Refunds; }
    public record PeriodPoint(string Label, DateTime Start, int Count, decimal OrderRevenue, decimal Logistics, decimal Refunds)
    { public decimal Total => OrderRevenue + Logistics; public decimal Net => Total - Refunds; }
    public record StorePoint(string Label, int Count, decimal Gross, decimal Refunds, decimal Delivery)
    { public decimal Net => Gross - Refunds; }
    public record StaffPoint(string Name, int Count, decimal Gross);
    public record StatePoint(string State, int Count, decimal Logistics)
    { public decimal AvgFee => Count > 0 ? Logistics / Count : 0; }
    public record DeliveryTypePoint(string Type, int Count, decimal Logistics)
    { public decimal AvgFee => Count > 0 ? Logistics / Count : 0; }

    // One closed till session's cash reconciliation. Expected = float + cash sales − cash refunds
    // + cash paid in − cash paid out; variance = counted − expected (positive = over, negative = short).
    public record CashSessionRow(int Id, DateTime Opened, DateTime? Closed, string Register, string Store,
        string Cashier, decimal OpeningFloat, decimal CashSales, decimal CashRefunds, decimal CashIn, decimal CashOut, decimal Counted)
    {
        public decimal Expected => OpeningFloat + CashSales - CashRefunds + CashIn - CashOut;
        public decimal Variance => Counted - Expected;
    }

    public class CashVm
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public int? StoreId { get; set; }
        public List<Store> Stores { get; set; } = new();
        public List<CashSessionRow> Rows { get; set; } = new();
        public decimal TotalCashSales => Rows.Sum(r => r.CashSales);
        public decimal TotalExpected => Rows.Sum(r => r.Expected);
        public decimal TotalCounted => Rows.Sum(r => r.Counted);
        public decimal TotalVariance => Rows.Sum(r => r.Variance);
        public int OverShortCount => Rows.Count(r => r.Variance != 0);
    }

    public record NameAmount(string Label, int Count, decimal Amount);
    public record RefundProductRow(string Product, int Qty, decimal Amount);
    public record RefundListRow(string Number, DateTime When, string Order, decimal Amount, string Method, string Reason, string Cashier);

    public class RefundVm
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public int? StoreId { get; set; }
        public List<Store> Stores { get; set; } = new();
        public decimal GrossSales { get; set; }
        public decimal TotalRefunds { get; set; }
        public int RefundCount { get; set; }
        public int OrdersRefunded { get; set; }
        public decimal Rate => GrossSales > 0 ? TotalRefunds / GrossSales : 0;
        public decimal Avg => RefundCount > 0 ? TotalRefunds / RefundCount : 0;
        public List<NameAmount> ByReason { get; set; } = new();
        public List<NameAmount> ByMethod { get; set; } = new();
        public List<NameAmount> ByCashier { get; set; } = new();
        public List<RefundProductRow> ByProduct { get; set; } = new();
        public List<RefundListRow> Recent { get; set; } = new();
    }

    private static readonly string[] Periods = { "day", "week", "month", "quarter", "year" };

    // Buckets a calendar day into the chosen reporting period (start date + display label).
    private static (string Label, DateTime Start) PeriodBucket(DateTime day, string period)
    {
        switch (period)
        {
            case "week":
                var monday = day.AddDays(-(((int)day.DayOfWeek + 6) % 7)); // ISO week starts Monday
                return ($"Wk of {monday:dd MMM yyyy}", monday);
            case "month":
                var m = new DateTime(day.Year, day.Month, 1);
                return ($"{m:MMM yyyy}", m);
            case "quarter":
                var q = (day.Month - 1) / 3 + 1;
                return ($"Q{q} {day.Year}", new DateTime(day.Year, (q - 1) * 3 + 1, 1));
            case "year":
                return ($"{day.Year}", new DateTime(day.Year, 1, 1));
            default: // day
                return ($"{day:ddd, dd MMM yyyy}", day.Date);
        }
    }

    private static List<PeriodPoint> BucketPeriods(IEnumerable<DayPoint> days, string period) =>
        days.GroupBy(d => PeriodBucket(d.Day, period))
            .Select(g => new PeriodPoint(g.Key.Label, g.Key.Start,
                g.Sum(d => d.Count), g.Sum(d => d.OrderRevenue), g.Sum(d => d.Logistics), g.Sum(d => d.Refunds)))
            .OrderBy(p => p.Start).ToList();

    public class FinanceVm
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public int? StoreId { get; set; }
        public string Channel { get; set; } = "";
        public string Period { get; set; } = "day";
        public List<Store> Stores { get; set; } = new();

        public int Count { get; set; }
        public decimal Gross { get; set; }                       // total revenue (incl. delivery)
        public decimal Refunds { get; set; }
        public decimal DeliveryFees { get; set; }                // logistics revenue
        public decimal OrderRevenue => Gross - DeliveryFees;     // merchandise only
        public decimal LogisticsRevenue => DeliveryFees;
        public decimal Net => Gross - Refunds;
        public decimal Avg => Count > 0 ? Gross / Count : 0;
        public decimal OnlineGross { get; set; }
        public decimal PosGross { get; set; }
        public int OnlineCount { get; set; }
        public int PosCount { get; set; }

        // Money given away (discounts / loyalty redemptions / gift-card spend) — revenue leakage.
        public decimal DiscountTotal { get; set; }
        public decimal LoyaltyTotal { get; set; }
        public decimal GiftCardTotal { get; set; }
        public decimal Giveaway => DiscountTotal + LoyaltyTotal + GiftCardTotal;

        // Previous equal-length period, for period-over-period comparison chips.
        public int PrevCount { get; set; }
        public decimal PrevGross { get; set; }
        public decimal PrevLogistics { get; set; }
        public decimal PrevRefunds { get; set; }
        public decimal PrevOrderRevenue => PrevGross - PrevLogistics;
        public decimal PrevNet => PrevGross - PrevRefunds;

        public List<(string Label, string From, string To)> Presets { get; set; } = new();
        public List<string> Alerts { get; set; } = new();

        public List<PeriodPoint> ByPeriod { get; set; } = new();
        public List<StatePoint> ByState { get; set; } = new();
        public List<DeliveryTypePoint> ByDeliveryType { get; set; } = new();
        public List<ChannelPoint> ByChannel { get; set; } = new();
        public List<StorePoint> ByStore { get; set; } = new();
        public List<StaffPoint> ByStaff { get; set; } = new();
    }

    private IQueryable<Order> PaidOrders(DateTime f, DateTime t, int? storeId, string channel)
    {
        var q = _db.Orders.Where(o => o.IsPaid && o.CreatedAt >= f && o.CreatedAt < t);
        if (storeId.HasValue) q = q.Where(o => o.PickupStoreId == storeId || o.FulfillingStoreId == storeId);
        if (channel == "Online") q = q.Where(o => o.Channel == OrderChannel.Online);
        else if (channel == "Pos") q = q.Where(o => o.Channel == OrderChannel.Pos);
        return q;
    }

    // Lightweight totals for a window — used for the previous-period comparison.
    private async Task<(int Count, decimal Gross, decimal Logistics, decimal Refunds)> SnapshotAsync(
        DateTime f, DateTime t, int? storeId, string channel)
    {
        var g = await PaidOrders(f, t, storeId, channel).GroupBy(_ => 1)
            .Select(x => new { Count = x.Count(), Gross = x.Sum(o => o.Total), Logistics = x.Sum(o => o.DeliveryFee) })
            .FirstOrDefaultAsync();
        var refq = _db.Refunds.Where(r => r.CreatedAt >= f && r.CreatedAt < t);
        if (storeId.HasValue) refq = refq.Where(r => r.OriginalOrder.PickupStoreId == storeId || r.OriginalOrder.FulfillingStoreId == storeId);
        if (channel == "Online") refq = refq.Where(r => r.OriginalOrder.Channel == OrderChannel.Online);
        else if (channel == "Pos") refq = refq.Where(r => r.OriginalOrder.Channel == OrderChannel.Pos);
        var refunds = await refq.SumAsync(r => (decimal?)r.Amount) ?? 0;
        return (g?.Count ?? 0, g?.Gross ?? 0, g?.Logistics ?? 0, refunds);
    }

    // Date-range quick presets (This month, Last month, QTD, YTD, last 30 days).
    private static List<(string Label, string From, string To)> BuildPresets()
    {
        var today = DateTime.UtcNow.Date;
        string S(DateTime d) => d.ToString("yyyy-MM-dd");
        var thisMonth = new DateTime(today.Year, today.Month, 1);
        var lastMonth = thisMonth.AddMonths(-1);
        var qStart = new DateTime(today.Year, (today.Month - 1) / 3 * 3 + 1, 1);
        return new()
        {
            ("Last 30 days", S(today.AddDays(-29)), S(today)),
            ("This month",   S(thisMonth),          S(today)),
            ("Last month",   S(lastMonth),          S(thisMonth.AddDays(-1))),
            ("This quarter", S(qStart),             S(today)),
            ("Year to date", S(new DateTime(today.Year, 1, 1)), S(today)),
        };
    }

    public async Task<IActionResult> Index(string? from, string? to, int? storeId, string? channel, string? period)
    {
        ViewData["Title"] = "Finance";
        var vm = await BuildAsync(from, to, storeId, channel, period);
        ViewBag.CanManage = AdminSections.IsSystemManager(User);
        ViewBag.ReportEnabled = await _settings.GetBoolAsync("finance.report_email_enabled", false);
        ViewBag.ReportTo = await _settings.GetAsync("finance.report_email_to", "");
        ViewBag.ReportFreq = await _settings.GetAsync("finance.report_email_freq", "weekly");
        return View(vm);
    }

    // ── Scheduled finance summary email (config + test) ────────────────────────
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveReportSettings(bool enabled, string? recipients, string? frequency)
    {
        await _settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["finance.report_email_enabled"] = enabled ? "true" : "false",
            ["finance.report_email_to"] = (recipients ?? "").Trim(),
            ["finance.report_email_freq"] = frequency == "monthly" ? "monthly" : "weekly",
        });
        await LogAsync("Update", "Setting", null, $"Updated scheduled finance report (enabled={enabled}, {frequency})");
        TempData["Success"] = "Scheduled report settings saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SendReportNow(string? recipients)
    {
        // Fall back to the signed-in admin's own email so the test always has a recipient,
        // even before the scheduled recipient list has been saved.
        var to = string.IsNullOrWhiteSpace(recipients) ? User.Identity?.Name : recipients.Trim();
        var (ok, msg) = await _report.SendAsync(string.IsNullOrWhiteSpace(to) ? null : to);
        TempData[ok ? "Success" : "Error"] = ok ? $"Finance summary sent. {msg}" : $"Not sent: {msg}";
        return RedirectToAction(nameof(Index));
    }

    // CSV export of the same figures the dashboard shows — finance always wants the numbers in a sheet.
    public async Task<IActionResult> Export(string? from, string? to, int? storeId, string? channel, string? period)
    {
        var vm = await BuildAsync(from, to, storeId, channel, period);
        var storeName = vm.StoreId.HasValue ? vm.Stores.FirstOrDefault(s => s.Id == vm.StoreId)?.Name ?? "All" : "All";
        var channelName = vm.Channel switch { "Online" => "Online", "Pos" => "POS", _ => "All" };
        var totalChannel = vm.ByChannel.Sum(c => c.Amount);

        var sb = new StringBuilder();
        static string Q(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        void Row(params string[] cells) => sb.AppendLine(string.Join(",", cells.Select(Q)));

        var periodName = vm.Period switch { "week" => "Weekly", "month" => "Monthly", "quarter" => "Quarterly", "year" => "Yearly", _ => "Daily" };
        Row("Sterlin Glams — Finance report");
        Row("Range", $"{vm.From:yyyy-MM-dd} to {vm.To:yyyy-MM-dd}");
        Row("Branch", storeName);
        Row("Channel", channelName);
        Row("Grouped by", periodName);
        sb.AppendLine();
        Row("Summary");
        Row("Order revenue", vm.OrderRevenue.ToString("0.##"));
        Row("Logistics revenue", vm.LogisticsRevenue.ToString("0.##"));
        Row("Total revenue", vm.Gross.ToString("0.##"));
        Row("Refunds", vm.Refunds.ToString("0.##"));
        Row("Net", vm.Net.ToString("0.##"));
        Row("Transactions", vm.Count.ToString());
        Row("Average order", vm.Avg.ToString("0.##"));
        Row("Online revenue", vm.OnlineGross.ToString("0.##"));
        Row("POS revenue", vm.PosGross.ToString("0.##"));
        sb.AppendLine();
        Row("Payment channels", "Amount", "Transactions", "Share %");
        foreach (var c in vm.ByChannel)
            Row(c.Channel, c.Amount.ToString("0.##"), c.Count.ToString(),
                (totalChannel > 0 ? c.Amount / totalChannel * 100 : 0).ToString("0.#"));
        sb.AppendLine();
        Row("By branch", "Transactions", "Gross", "Refunds", "Net", "Delivery fees");
        foreach (var s in vm.ByStore)
            Row(s.Label, s.Count.ToString(), s.Gross.ToString("0.##"), s.Refunds.ToString("0.##"), s.Net.ToString("0.##"), s.Delivery.ToString("0.##"));
        sb.AppendLine();
        Row("By cashier (POS)", "Transactions", "Gross");
        foreach (var s in vm.ByStaff)
            Row(s.Name, s.Count.ToString(), s.Gross.ToString("0.##"));
        sb.AppendLine();
        Row("Logistics by delivery type", "Orders", "Logistics revenue", "Avg fee");
        foreach (var d in vm.ByDeliveryType)
            Row(d.Type, d.Count.ToString(), d.Logistics.ToString("0.##"), d.AvgFee.ToString("0.##"));
        sb.AppendLine();
        Row("Logistics by state", "Orders", "Logistics revenue", "Avg fee");
        foreach (var s in vm.ByState)
            Row(s.State, s.Count.ToString(), s.Logistics.ToString("0.##"), s.AvgFee.ToString("0.##"));
        sb.AppendLine();
        Row(periodName, "Transactions", "Order revenue", "Logistics revenue", "Total", "Refunds", "Net");
        foreach (var p in vm.ByPeriod)
            Row(p.Label, p.Count.ToString(), p.OrderRevenue.ToString("0.##"), p.Logistics.ToString("0.##"),
                p.Total.ToString("0.##"), p.Refunds.ToString("0.##"), p.Net.ToString("0.##"));

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv", $"finance_{vm.From:yyyyMMdd}-{vm.To:yyyyMMdd}.csv");
    }

    // Print-friendly branded finance statement (browser Print → PDF). Reuses the overview figures.
    public async Task<IActionResult> Statement(string? from, string? to, int? storeId, string? channel, string? period)
    {
        var vm = await BuildAsync(from, to, storeId, channel, period);
        return View(vm);
    }

    // ── Accounting export (balanced general journal, QuickBooks/Xero-friendly) ──
    public async Task<IActionResult> Journal(string? from, string? to, int? storeId, string? channel)
    {
        var (f, t) = Range(from, to);
        channel = channel is "Online" or "Pos" ? channel : "";
        var paid = PaidOrders(f, t, storeId, channel);

        var sales = await paid.GroupBy(o => o.CreatedAt.Date)
            .Select(g => new { Day = g.Key, Gross = g.Sum(o => o.Total), Delivery = g.Sum(o => o.DeliveryFee) })
            .ToListAsync();

        var refq = _db.Refunds.Where(r => r.CreatedAt >= f && r.CreatedAt < t);
        if (storeId.HasValue) refq = refq.Where(r => r.OriginalOrder.PickupStoreId == storeId || r.OriginalOrder.FulfillingStoreId == storeId);
        if (channel == "Online") refq = refq.Where(r => r.OriginalOrder.Channel == OrderChannel.Online);
        else if (channel == "Pos") refq = refq.Where(r => r.OriginalOrder.Channel == OrderChannel.Pos);
        var refunds = await refq.GroupBy(r => r.CreatedAt.Date)
            .Select(g => new { Day = g.Key, Amt = g.Sum(x => x.Amount) }).ToListAsync();

        var sb = new StringBuilder();
        static string Q(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        void Row(string date, string reference, string account, decimal debit, decimal credit, string memo)
            => sb.AppendLine(string.Join(",", new[] { date, reference, account, debit.ToString("0.##"), credit.ToString("0.##"), memo }.Select(Q)));

        sb.AppendLine("Date,Reference,Account,Debit,Credit,Description");
        foreach (var d in sales.OrderBy(x => x.Day))
        {
            var date = d.Day.ToString("yyyy-MM-dd");
            var reference = "SG-" + d.Day.ToString("yyyyMMdd");
            var merch = d.Gross - d.Delivery;
            Row(date, reference, "Bank", d.Gross, 0, "Daily takings");
            if (merch != 0) Row(date, reference, "Sales revenue", 0, merch, "Merchandise sales");
            if (d.Delivery != 0) Row(date, reference, "Delivery income", 0, d.Delivery, "Delivery / logistics fees");
        }
        foreach (var r in refunds.OrderBy(x => x.Day))
        {
            if (r.Amt == 0) continue;
            var date = r.Day.ToString("yyyy-MM-dd");
            var reference = "SGR-" + r.Day.ToString("yyyyMMdd");
            Row(date, reference, "Sales returns", r.Amt, 0, "Refunds");
            Row(date, reference, "Bank", 0, r.Amt, "Refunds paid out");
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv", $"journal_{f:yyyyMMdd}-{t.AddDays(-1):yyyyMMdd}.csv");
    }

    // ── Cash reconciliation ────────────────────────────────────────────────────
    // Every closed till session's counted cash vs what the drawer should hold — surfaces
    // over/short so finance can chase drawer discrepancies.
    public async Task<IActionResult> Cash(string? from, string? to, int? storeId)
    {
        ViewData["Title"] = "Finance — Cash-up";
        var (f, t) = Range(from, to);

        var stores = await _db.Stores.OrderBy(s => s.Name).ToListAsync();

        var sessQ = _db.TillSessions.Include(s => s.Register).ThenInclude(r => r.Store)
            .Where(s => s.ClosedAt != null && s.ClosedAt >= f && s.ClosedAt < t);
        if (storeId.HasValue) sessQ = sessQ.Where(s => s.Register.StoreId == storeId);
        var sessions = await sessQ.OrderByDescending(s => s.ClosedAt).ToListAsync();
        var ids = sessions.Select(s => s.Id).ToList();

        // Cash figures per session, aggregated in SQL (no per-session round trips).
        var cashSales = (await _db.OrderPayments
                .Where(p => p.Method == "Cash" && p.Order.TillSessionId != null && ids.Contains(p.Order.TillSessionId!.Value))
                .GroupBy(p => p.Order.TillSessionId!.Value)
                .Select(g => new { Sid = g.Key, Amt = g.Sum(x => x.Amount) }).ToListAsync())
            .ToDictionary(x => x.Sid, x => x.Amt);
        var cashRefunds = (await _db.Refunds
                .Where(r => r.RefundMethod == "Cash" && r.OriginalOrder.TillSessionId != null && ids.Contains(r.OriginalOrder.TillSessionId!.Value))
                .GroupBy(r => r.OriginalOrder.TillSessionId!.Value)
                .Select(g => new { Sid = g.Key, Amt = g.Sum(x => x.Amount) }).ToListAsync())
            .ToDictionary(x => x.Sid, x => x.Amt);
        var moves = (await _db.CashMovements
                .Where(m => ids.Contains(m.TillSessionId))
                .GroupBy(m => m.TillSessionId)
                .Select(g => new
                {
                    Sid = g.Key,
                    In = g.Where(x => x.Amount > 0).Sum(x => x.Amount),
                    Out = g.Where(x => x.Amount < 0).Sum(x => x.Amount)
                }).ToListAsync())
            .ToDictionary(x => x.Sid, x => (x.In, x.Out));

        var userIds = sessions.Select(s => s.OpenedByUserId).Distinct().ToList();
        var names = (await _db.Users.Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FirstName, u.LastName, u.UserName }).ToListAsync())
            .ToDictionary(u => u.Id, u =>
            {
                var n = $"{u.FirstName} {u.LastName}".Trim();
                return string.IsNullOrWhiteSpace(n) ? (u.UserName ?? "—") : n;
            });

        var rows = sessions.Select(s =>
        {
            var mv = moves.GetValueOrDefault(s.Id);
            return new CashSessionRow(s.Id, s.OpenedAt, s.ClosedAt, s.Register.Name, s.Register.Store.Name,
                names.GetValueOrDefault(s.OpenedByUserId, "—"), s.OpeningFloat,
                cashSales.GetValueOrDefault(s.Id), cashRefunds.GetValueOrDefault(s.Id),
                mv.In, -mv.Out, s.CountedCash ?? 0);
        }).ToList();

        return View(new CashVm { From = f, To = t.AddDays(-1), StoreId = storeId, Stores = stores, Rows = rows });
    }

    // ── Refund analytics ───────────────────────────────────────────────────────
    public async Task<IActionResult> Refunds(string? from, string? to, int? storeId)
    {
        ViewData["Title"] = "Finance — Refunds";
        var (f, t) = Range(from, to);
        var stores = await _db.Stores.OrderBy(s => s.Name).ToListAsync();

        var refq = _db.Refunds.Where(r => r.CreatedAt >= f && r.CreatedAt < t);
        if (storeId.HasValue) refq = refq.Where(r => r.OriginalOrder.PickupStoreId == storeId || r.OriginalOrder.FulfillingStoreId == storeId);

        var totals = await refq.GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Amt = g.Sum(r => r.Amount), Orders = g.Select(r => r.OriginalOrderId).Distinct().Count() })
            .FirstOrDefaultAsync();

        var byReason = (await refq.GroupBy(r => r.Reason)
                .Select(g => new { g.Key, Count = g.Count(), Amt = g.Sum(r => r.Amount) }).ToListAsync())
            .Select(x => new NameAmount(string.IsNullOrWhiteSpace(x.Key) ? "Unspecified" : x.Key!.Trim(), x.Count, x.Amt))
            .OrderByDescending(x => x.Amount).ToList();
        var byMethod = (await refq.GroupBy(r => r.RefundMethod)
                .Select(g => new { g.Key, Count = g.Count(), Amt = g.Sum(r => r.Amount) }).ToListAsync())
            .Select(x => new NameAmount(string.IsNullOrWhiteSpace(x.Key) ? "—" : x.Key, x.Count, x.Amt))
            .OrderByDescending(x => x.Amount).ToList();

        var cashierRaw = await refq.GroupBy(r => r.CashierUserId)
            .Select(g => new { g.Key, Count = g.Count(), Amt = g.Sum(r => r.Amount) }).ToListAsync();
        var cIds = cashierRaw.Select(c => c.Key).Distinct().ToList();
        var cNames = (await _db.Users.Where(u => cIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FirstName, u.LastName, u.UserName }).ToListAsync())
            .ToDictionary(u => u.Id, u =>
            {
                var n = $"{u.FirstName} {u.LastName}".Trim();
                return string.IsNullOrWhiteSpace(n) ? (u.UserName ?? "—") : n;
            });
        var byCashier = cashierRaw
            .Select(c => new NameAmount(string.IsNullOrWhiteSpace(c.Key) ? "—" : cNames.GetValueOrDefault(c.Key, "Unknown"), c.Count, c.Amt))
            .OrderByDescending(x => x.Amount).ToList();

        var itemsQ = _db.RefundItems.Where(i => i.Refund.CreatedAt >= f && i.Refund.CreatedAt < t);
        if (storeId.HasValue) itemsQ = itemsQ.Where(i => i.Refund.OriginalOrder.PickupStoreId == storeId || i.Refund.OriginalOrder.FulfillingStoreId == storeId);
        var byProduct = (await itemsQ.GroupBy(i => i.ProductName)
                .Select(g => new { g.Key, Qty = g.Sum(x => x.Quantity), Amt = g.Sum(x => x.Quantity * x.UnitPrice) })
                .OrderByDescending(x => x.Amt).Take(15).ToListAsync())
            .Select(x => new RefundProductRow(x.Key, x.Qty, x.Amt)).ToList();

        var recentRaw = await refq.OrderByDescending(r => r.CreatedAt).Take(50)
            .Select(r => new { r.RefundNumber, r.CreatedAt, Order = r.OriginalOrder.OrderNumber, r.Amount, r.RefundMethod, r.Reason, r.CashierUserId })
            .ToListAsync();
        var recent = recentRaw.Select(r => new RefundListRow(r.RefundNumber, r.CreatedAt, r.Order, r.Amount,
            r.RefundMethod, string.IsNullOrWhiteSpace(r.Reason) ? "—" : r.Reason!, cNames.GetValueOrDefault(r.CashierUserId ?? "", "—"))).ToList();

        var gross = await PaidOrders(f, t, storeId, "").SumAsync(o => (decimal?)o.Total) ?? 0;

        return View(new RefundVm
        {
            From = f, To = t.AddDays(-1), StoreId = storeId, Stores = stores,
            GrossSales = gross,
            TotalRefunds = totals?.Amt ?? 0,
            RefundCount = totals?.Count ?? 0,
            OrdersRefunded = totals?.Orders ?? 0,
            ByReason = byReason, ByMethod = byMethod, ByCashier = byCashier, ByProduct = byProduct, Recent = recent
        });
    }

    // ── Discount & giveaway leakage ────────────────────────────────────────────
    public class LeakageVm
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public int? StoreId { get; set; }
        public List<Store> Stores { get; set; } = new();
        public decimal Gross { get; set; }
        public decimal Discounts { get; set; }
        public decimal Loyalty { get; set; }
        public decimal GiftCards { get; set; }
        public int PointsRedeemed { get; set; }
        public decimal Total => Discounts + Loyalty + GiftCards;
        public decimal Pct => Gross > 0 ? Total / Gross : 0;
        public List<NameAmount> ByCode { get; set; } = new();
    }

    public async Task<IActionResult> Leakage(string? from, string? to, int? storeId)
    {
        ViewData["Title"] = "Finance — Leakage";
        var (f, t) = Range(from, to);
        var stores = await _db.Stores.OrderBy(s => s.Name).ToListAsync();
        var paid = PaidOrders(f, t, storeId, "");

        var tot = await paid.GroupBy(_ => 1).Select(g => new
        {
            Gross = g.Sum(o => o.Total),
            Disc = g.Sum(o => o.DiscountAmount),
            Loy = g.Sum(o => o.LoyaltyDiscount),
            Gift = g.Sum(o => o.GiftCardAmount),
            Pts = g.Sum(o => o.LoyaltyPointsRedeemed)
        }).FirstOrDefaultAsync();

        var byCode = (await paid.Where(o => o.DiscountAmount > 0 && o.DiscountCode != null)
                .GroupBy(o => o.DiscountCode!)
                .Select(g => new { Code = g.Key, Uses = g.Count(), Amt = g.Sum(o => o.DiscountAmount) })
                .OrderByDescending(x => x.Amt).ToListAsync())
            .Select(x => new NameAmount(x.Code, x.Uses, x.Amt)).ToList();

        return View(new LeakageVm
        {
            From = f, To = t.AddDays(-1), StoreId = storeId, Stores = stores,
            Gross = tot?.Gross ?? 0, Discounts = tot?.Disc ?? 0, Loyalty = tot?.Loy ?? 0,
            GiftCards = tot?.Gift ?? 0, PointsRedeemed = tot?.Pts ?? 0, ByCode = byCode
        });
    }

    // ── Outstanding liabilities (money we owe customers) ───────────────────────
    public record GiftCardRow(string Code, decimal Initial, decimal Balance, DateTime? Expires, string? Recipient);

    public class LiabilitiesVm
    {
        public decimal GiftCardOutstanding { get; set; }
        public decimal GiftCardIssued { get; set; }
        public int GiftCardCount { get; set; }
        public int LoyaltyPoints { get; set; }
        public decimal PointValue { get; set; }
        public decimal LoyaltyValue => LoyaltyPoints * PointValue;
        public int LoyaltyAccounts { get; set; }
        public decimal Total => GiftCardOutstanding + LoyaltyValue;
        public decimal GiftCardRedeemed => GiftCardIssued - GiftCardOutstanding;
        public List<GiftCardRow> TopCards { get; set; } = new();
    }

    public async Task<IActionResult> Liabilities()
    {
        ViewData["Title"] = "Finance — Liabilities";
        var now = DateTime.UtcNow;

        // Live gift cards still carrying a balance = money we owe.
        var liveCards = _db.GiftCards.Where(g => g.IsActive && g.Balance > 0 && (g.ExpiresAt == null || g.ExpiresAt > now));
        var gcOutstanding = await liveCards.SumAsync(g => (decimal?)g.Balance) ?? 0;
        var gcCount = await liveCards.CountAsync();
        var gcIssued = await _db.GiftCards.Where(g => g.IsActive).SumAsync(g => (decimal?)g.InitialAmount) ?? 0;
        var topCards = (await liveCards.OrderByDescending(g => g.Balance).Take(20)
                .Select(g => new { g.Code, g.InitialAmount, g.Balance, g.ExpiresAt, g.RecipientName }).ToListAsync())
            .Select(g => new GiftCardRow(g.Code, g.InitialAmount, g.Balance, g.ExpiresAt, g.RecipientName)).ToList();

        var points = await _db.Set<LoyaltyAccount>().SumAsync(a => (int?)a.PointsBalance) ?? 0;
        var accounts = await _db.Set<LoyaltyAccount>().CountAsync(a => a.PointsBalance > 0);
        var pointValue = await _loyalty.PointValueAsync();

        return View(new LiabilitiesVm
        {
            GiftCardOutstanding = gcOutstanding, GiftCardIssued = gcIssued, GiftCardCount = gcCount,
            LoyaltyPoints = points, PointValue = pointValue, LoyaltyAccounts = accounts, TopCards = topCards
        });
    }

    // ── Accounts receivable (unpaid orders, aged) ──────────────────────────────
    public record AgingBucket(string Label, int Count, decimal Amount);
    public record UnpaidRow(string Order, DateTime When, string Customer, decimal Amount, int AgeDays, string Status);

    public class ReceivablesVm
    {
        public decimal TotalOwed { get; set; }
        public int Count { get; set; }
        public List<AgingBucket> Buckets { get; set; } = new();
        public List<UnpaidRow> Orders { get; set; } = new();
    }

    public async Task<IActionResult> Receivables()
    {
        ViewData["Title"] = "Finance — Receivables";
        var now = DateTime.UtcNow;

        // Placed but unpaid, and not in a terminal (cancelled/refunded) state = money owed to us.
        var unpaidQ = _db.Orders.Include(o => o.User)
            .Where(o => !o.IsPaid && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Refunded);

        var raw = await unpaidQ.OrderBy(o => o.CreatedAt)
            .Select(o => new { o.OrderNumber, o.CreatedAt, o.Total, o.Status,
                Customer = o.User != null ? (o.User.FirstName + " " + o.User.LastName) : "Guest" })
            .ToListAsync();

        var orders = raw.Select(o => new UnpaidRow(o.OrderNumber, o.CreatedAt,
            string.IsNullOrWhiteSpace(o.Customer?.Trim()) ? "Guest" : o.Customer!.Trim(),
            o.Total, (int)(now - o.CreatedAt).TotalDays, o.Status.ToString())).ToList();

        (string Label, Func<int, bool> In)[] defs =
        {
            ("0–7 days",   d => d <= 7),
            ("8–30 days",  d => d > 7 && d <= 30),
            ("31–60 days", d => d > 30 && d <= 60),
            ("60+ days",   d => d > 60),
        };
        var buckets = defs.Select(b => new AgingBucket(b.Label,
            orders.Count(o => b.In(o.AgeDays)), orders.Where(o => b.In(o.AgeDays)).Sum(o => o.Amount))).ToList();

        return View(new ReceivablesVm
        {
            TotalOwed = orders.Sum(o => o.Amount),
            Count = orders.Count,
            Buckets = buckets,
            Orders = orders.OrderByDescending(o => o.AgeDays).Take(100).ToList()
        });
    }

    // ── Customer finance (new vs repeat, top spenders) ─────────────────────────
    public record CustomerRow(string Name, string Email, int Orders, decimal Revenue, DateTime Last);

    public class CustomersVm
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public decimal NewRevenue { get; set; }
        public decimal RepeatRevenue { get; set; }
        public int NewCustomers { get; set; }
        public int RepeatCustomers { get; set; }
        public decimal Total => NewRevenue + RepeatRevenue;
        public int Customers => NewCustomers + RepeatCustomers;
        public List<CustomerRow> Top { get; set; } = new();
    }

    public async Task<IActionResult> Customers(string? from, string? to)
    {
        ViewData["Title"] = "Finance — Customers";
        var (f, t) = Range(from, to);

        // Online orders carry the customer on UserId; a customer is "new" if their first paid online
        // order falls in range, otherwise "repeat".
        var onlinePaid = _db.Orders.Where(o => o.IsPaid && o.Channel == OrderChannel.Online && o.UserId != "");
        var inRange = await onlinePaid.Where(o => o.CreatedAt >= f && o.CreatedAt < t)
            .GroupBy(o => o.UserId)
            .Select(g => new { Uid = g.Key, Rev = g.Sum(o => o.Total), Cnt = g.Count(), Last = g.Max(o => o.CreatedAt) })
            .ToListAsync();
        var priorIds = (await onlinePaid.Where(o => o.CreatedAt < f).Select(o => o.UserId).Distinct().ToListAsync()).ToHashSet();

        var uids = inRange.Select(x => x.Uid).ToList();
        var users = (await _db.Users.Where(u => uids.Contains(u.Id))
                .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email }).ToListAsync())
            .ToDictionary(u => u.Id, u => (Name: $"{u.FirstName} {u.LastName}".Trim(), Email: u.Email ?? ""));

        var top = inRange.OrderByDescending(x => x.Rev).Take(25)
            .Select(x =>
            {
                var u = users.GetValueOrDefault(x.Uid, (Name: "", Email: ""));
                return new CustomerRow(string.IsNullOrWhiteSpace(u.Name) ? "Guest / unknown" : u.Name, u.Email, x.Cnt, x.Rev, x.Last);
            }).ToList();

        return View(new CustomersVm
        {
            From = f, To = t.AddDays(-1),
            NewRevenue = inRange.Where(x => !priorIds.Contains(x.Uid)).Sum(x => x.Rev),
            RepeatRevenue = inRange.Where(x => priorIds.Contains(x.Uid)).Sum(x => x.Rev),
            NewCustomers = inRange.Count(x => !priorIds.Contains(x.Uid)),
            RepeatCustomers = inRange.Count(x => priorIds.Contains(x.Uid)),
            Top = top
        });
    }

    // ── Paystack settlement & gateway-fee estimate ─────────────────────────────
    public record SettleDay(DateTime Day, int Count, decimal Gross, decimal Fee)
    { public decimal Net => Gross - Fee; }

    public class SettlementVm
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public int Count { get; set; }
        public decimal Gross { get; set; }
        public decimal EstFees { get; set; }
        public decimal NetSettled => Gross - EstFees;
        public List<SettleDay> ByDay { get; set; } = new();
    }

    // Standard Paystack Nigeria pricing: 1.5% + ₦100 (₦100 waived below ₦2,500), capped at ₦2,000.
    private static decimal PaystackFee(decimal amount)
    {
        var fee = amount * 0.015m + (amount >= 2500m ? 100m : 0m);
        return Math.Min(fee, 2000m);
    }

    public async Task<IActionResult> Settlement(string? from, string? to)
    {
        ViewData["Title"] = "Finance — Settlement";
        var (f, t) = Range(from, to);

        var orders = await _db.Orders
            .Where(o => o.IsPaid && o.Channel == OrderChannel.Online && o.CreatedAt >= f && o.CreatedAt < t)
            .Select(o => new { o.Total, o.CreatedAt }).ToListAsync();

        var byDay = orders.GroupBy(o => o.CreatedAt.Date)
            .Select(g => new SettleDay(g.Key, g.Count(), g.Sum(o => o.Total), g.Sum(o => PaystackFee(o.Total))))
            .OrderByDescending(d => d.Day).ToList();

        return View(new SettlementVm
        {
            From = f, To = t.AddDays(-1),
            Count = orders.Count,
            Gross = orders.Sum(o => o.Total),
            EstFees = orders.Sum(o => PaystackFee(o.Total)),
            ByDay = byDay
        });
    }

    // ── Profit & margin (needs product cost prices) ────────────────────────────
    public record ProfitRow(string Product, int Units, decimal Revenue, decimal Cost, bool HasCost)
    {
        public decimal Profit => Revenue - Cost;
        public decimal Margin => Revenue > 0 ? Profit / Revenue : 0;
    }

    public class ProfitVm
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public int? StoreId { get; set; }
        public List<Store> Stores { get; set; } = new();
        public decimal Revenue { get; set; }          // merchandise revenue with known cost
        public decimal Cost { get; set; }
        public decimal RevenueAll { get; set; }        // all merchandise revenue (incl. no-cost items)
        public decimal Profit => Revenue - Cost;
        public decimal Margin => Revenue > 0 ? Profit / Revenue : 0;
        public decimal Coverage => RevenueAll > 0 ? Revenue / RevenueAll : 0; // % of revenue with cost data
        public List<ProfitRow> Rows { get; set; } = new();
    }

    public async Task<IActionResult> Profit(string? from, string? to, int? storeId)
    {
        ViewData["Title"] = "Finance — Profit";
        var (f, t) = Range(from, to);
        var stores = await _db.Stores.OrderBy(s => s.Name).ToListAsync();

        // Paid order items in range, joined to the product's current cost price.
        var itemsQ = _db.OrderItems.Where(i => i.Order.IsPaid && i.Order.CreatedAt >= f && i.Order.CreatedAt < t);
        if (storeId.HasValue) itemsQ = itemsQ.Where(i => i.Order.PickupStoreId == storeId || i.Order.FulfillingStoreId == storeId);

        var grouped = await itemsQ
            .GroupBy(i => new { i.ProductId, i.ProductName })
            .Select(g => new
            {
                g.Key.ProductName,
                Units = g.Sum(x => x.Quantity),
                Revenue = g.Sum(x => x.Quantity * x.UnitPrice),
                Cost = g.Sum(x => x.Quantity * (x.Product.CostPrice ?? 0m)),
                HasCost = g.Max(x => x.Product.CostPrice) != null
            })
            .ToListAsync();

        var rows = grouped
            .Select(x => new ProfitRow(x.ProductName, x.Units, x.Revenue, x.HasCost ? x.Cost : 0m, x.HasCost))
            .OrderByDescending(r => r.Profit).ToList();

        var withCost = rows.Where(r => r.HasCost).ToList();

        return View(new ProfitVm
        {
            From = f, To = t.AddDays(-1), StoreId = storeId, Stores = stores,
            Revenue = withCost.Sum(r => r.Revenue),
            Cost = withCost.Sum(r => r.Cost),
            RevenueAll = rows.Sum(r => r.Revenue),
            Rows = rows
        });
    }

    // ── Logistics P&L (in-house delivery revenue vs logistics costs) ───────────
    public record ExpenseRow(int Id, DateTime On, string Category, decimal Amount, string? Note, string? Store);

    public class LogisticsVm
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public int? StoreId { get; set; }
        public List<Store> Stores { get; set; } = new();
        public decimal Revenue { get; set; }
        public decimal Expenses { get; set; }
        public decimal Net => Revenue - Expenses;
        public decimal Margin => Revenue > 0 ? Net / Revenue : 0;
        public int Deliveries { get; set; }
        public List<ExpenseRow> Items { get; set; } = new();
    }

    public async Task<IActionResult> Logistics(string? from, string? to, int? storeId)
    {
        ViewData["Title"] = "Finance — Logistics P&L";
        var (f, t) = Range(from, to);
        var stores = await _db.Stores.OrderBy(s => s.Name).ToListAsync();

        var deliveries = _db.Orders.Where(o => o.IsPaid && o.FulfillmentType == FulfillmentType.Delivery
            && o.CreatedAt >= f && o.CreatedAt < t);
        if (storeId.HasValue) deliveries = deliveries.Where(o => o.PickupStoreId == storeId || o.FulfillingStoreId == storeId);
        var revenue = await deliveries.SumAsync(o => (decimal?)o.DeliveryFee) ?? 0;
        var count = await deliveries.CountAsync(o => o.DeliveryFee > 0);

        var expQ = _db.Expenses.Include(e => e.Store)
            .Where(e => e.Category == "Logistics" && e.OccurredOn >= f && e.OccurredOn < t);
        if (storeId.HasValue) expQ = expQ.Where(e => e.StoreId == storeId);
        var expList = await expQ.OrderByDescending(e => e.OccurredOn).ThenByDescending(e => e.Id).ToListAsync();

        return View(new LogisticsVm
        {
            From = f, To = t.AddDays(-1), StoreId = storeId, Stores = stores,
            Revenue = revenue,
            Expenses = expList.Sum(e => e.Amount),
            Deliveries = count,
            Items = expList.Select(e => new ExpenseRow(e.Id, e.OccurredOn, e.Category, e.Amount, e.Note, e.Store?.Name)).ToList()
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddExpense(string category, decimal amount, DateTime? occurredOn,
        string? note, int? storeId, string? from, string? to)
    {
        if (amount > 0)
        {
            _db.Expenses.Add(new Expense
            {
                Category = string.IsNullOrWhiteSpace(category) ? "Logistics" : category.Trim(),
                Amount = amount,
                OccurredOn = DateTime.SpecifyKind((occurredOn ?? DateTime.UtcNow).Date, DateTimeKind.Utc),
                Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                StoreId = storeId,
                CreatedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier),
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
            await LogAsync("Create", "Expense", null, $"Recorded {category} expense ₦{amount:N0}");
            TempData["Success"] = "Expense recorded.";
        }
        return RedirectToAction(nameof(Logistics), new { from, to, storeId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteExpense(int id, string? from, string? to, int? storeId)
    {
        var e = await _db.Expenses.FindAsync(id);
        if (e != null)
        {
            _db.Expenses.Remove(e);
            await _db.SaveChangesAsync();
            await LogAsync("Delete", "Expense", id.ToString(), $"Deleted expense ₦{e.Amount:N0}");
        }
        return RedirectToAction(nameof(Logistics), new { from, to, storeId });
    }

    private async Task<FinanceVm> BuildAsync(string? from, string? to, int? storeId, string? channel, string? period)
    {
        var (f, t) = Range(from, to);
        channel = channel is "Online" or "Pos" ? channel : "";
        period = Periods.Contains(period) ? period! : "day";

        var stores = await _db.Stores.OrderBy(s => s.Name).ToListAsync();
        var storeName = stores.ToDictionary(s => s.Id, s => s.Name);

        var paid = PaidOrders(f, t, storeId, channel);

        // Headline + online/POS split + giveaways (discounts/loyalty/gift cards) in one grouped query.
        var chan = await paid.GroupBy(o => o.Channel)
            .Select(g => new
            {
                g.Key,
                Count = g.Count(),
                Gross = g.Sum(o => o.Total),
                Delivery = g.Sum(o => o.DeliveryFee),
                Disc = g.Sum(o => o.DiscountAmount),
                Loy = g.Sum(o => o.LoyaltyDiscount),
                Gift = g.Sum(o => o.GiftCardAmount)
            })
            .ToListAsync();
        var posGross = chan.Where(c => c.Key == OrderChannel.Pos).Sum(c => c.Gross);
        var posCount = chan.Where(c => c.Key == OrderChannel.Pos).Sum(c => c.Count);
        var onlineGross = chan.Where(c => c.Key == OrderChannel.Online).Sum(c => c.Gross);
        var onlineCount = chan.Where(c => c.Key == OrderChannel.Online).Sum(c => c.Count);

        // Refunds in range, attributed via the original order (respects the same filters).
        var refq = _db.Refunds.Where(r => r.CreatedAt >= f && r.CreatedAt < t);
        if (storeId.HasValue) refq = refq.Where(r => r.OriginalOrder.PickupStoreId == storeId || r.OriginalOrder.FulfillingStoreId == storeId);
        if (channel == "Online") refq = refq.Where(r => r.OriginalOrder.Channel == OrderChannel.Online);
        else if (channel == "Pos") refq = refq.Where(r => r.OriginalOrder.Channel == OrderChannel.Pos);
        var refundTotal = await refq.SumAsync(r => (decimal?)r.Amount) ?? 0;

        // By day: gross/count from orders, refunds from refunds, merged (incl. refund-only days).
        var grossByDay = await paid.GroupBy(o => o.CreatedAt.Date)
            .Select(g => new { Day = g.Key, Count = g.Count(), Total = g.Sum(o => o.Total), Logistics = g.Sum(o => o.DeliveryFee) }).ToListAsync();
        var refByDay = await refq.GroupBy(r => r.CreatedAt.Date)
            .Select(g => new { Day = g.Key, Refunds = g.Sum(x => x.Amount) }).ToListAsync();
        var refDayMap = refByDay.ToDictionary(x => x.Day, x => x.Refunds);
        var byDay = grossByDay
            .Select(x => new DayPoint(x.Day, x.Count, x.Total - x.Logistics, x.Logistics, refDayMap.GetValueOrDefault(x.Day, 0)))
            .ToList();
        foreach (var r in refByDay.Where(r => grossByDay.All(g => g.Day != r.Day)))
            byDay.Add(new DayPoint(r.Day, 0, 0, 0, r.Refunds));
        var byPeriod = BucketPeriods(byDay, period);

        // By branch (POS uses PickupStoreId; delivery-from-branch uses FulfillingStoreId).
        var grossByStore = await paid.GroupBy(o => o.PickupStoreId ?? o.FulfillingStoreId)
            .Select(g => new { StoreId = g.Key, Count = g.Count(), Gross = g.Sum(o => o.Total), Delivery = g.Sum(o => o.DeliveryFee) })
            .ToListAsync();
        var refByStore = await refq.GroupBy(r => r.OriginalOrder.PickupStoreId ?? r.OriginalOrder.FulfillingStoreId)
            .Select(g => new { StoreId = g.Key, Refunds = g.Sum(x => x.Amount) }).ToListAsync();
        var refStoreMap = refByStore.ToDictionary(x => x.StoreId ?? -1, x => x.Refunds);
        var byStore = grossByStore.Select(x => new StorePoint(
                x.StoreId.HasValue && storeName.ContainsKey(x.StoreId.Value) ? storeName[x.StoreId.Value] : "Online / unassigned",
                x.Count, x.Gross, refStoreMap.GetValueOrDefault(x.StoreId ?? -1, 0), x.Delivery))
            .OrderByDescending(s => s.Gross).ToList();

        // By cashier — POS only (on a POS sale Order.UserId is the cashier).
        var staffRaw = await paid.Where(o => o.Channel == OrderChannel.Pos)
            .GroupBy(o => o.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count(), Gross = g.Sum(o => o.Total) }).ToListAsync();
        var staffIds = staffRaw.Select(s => s.UserId).ToList();
        var users = await _db.Users.Where(u => staffIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.UserName }).ToListAsync();
        var nameMap = users.ToDictionary(u => u.Id, u =>
        {
            var n = $"{u.FirstName} {u.LastName}".Trim();
            return string.IsNullOrWhiteSpace(n) ? (u.UserName ?? "—") : n;
        });
        var byStaff = staffRaw
            .Select(s => new StaffPoint(nameMap.GetValueOrDefault(s.UserId, "Unknown"), s.Count, s.Gross))
            .OrderByDescending(s => s.Gross).ToList();

        // Logistics revenue (in-house delivery) broken down by destination state and by
        // Express vs Standard delivery. Only delivery orders that carry a fee.
        var deliveries = paid.Where(o => o.FulfillmentType == FulfillmentType.Delivery && o.DeliveryFee > 0);

        var stateRaw = await deliveries.Where(o => o.DeliveryAddressId != null)
            .GroupBy(o => o.DeliveryAddress!.State)
            .Select(g => new { State = g.Key, Count = g.Count(), Logistics = g.Sum(o => o.DeliveryFee) })
            .ToListAsync();
        var byState = stateRaw
            .Select(x => new StatePoint(string.IsNullOrWhiteSpace(x.State) ? "Unspecified" : x.State.Trim(), x.Count, x.Logistics))
            .OrderByDescending(s => s.Logistics).ToList();

        var typeRaw = await deliveries
            .GroupBy(o => o.DeliveryType)
            .Select(g => new { Type = g.Key, Count = g.Count(), Logistics = g.Sum(o => o.DeliveryFee) })
            .ToListAsync();
        var byDeliveryType = typeRaw
            .Select(x => new DeliveryTypePoint(string.IsNullOrWhiteSpace(x.Type) ? "Unspecified" : x.Type!.Trim(), x.Count, x.Logistics))
            .OrderByDescending(d => d.Logistics).ToList();

        // Payment channels: explicit POS tenders (Cash/Card/Transfer) + provider fallback
        // (e.g. Paystack for online, legacy POS) for orders that carry no tender rows.
        var payQ = _db.OrderPayments.Where(p => p.Order.IsPaid && p.Order.CreatedAt >= f && p.Order.CreatedAt < t);
        if (storeId.HasValue) payQ = payQ.Where(p => p.Order.PickupStoreId == storeId || p.Order.FulfillingStoreId == storeId);
        if (channel == "Online") payQ = payQ.Where(p => p.Order.Channel == OrderChannel.Online);
        else if (channel == "Pos") payQ = payQ.Where(p => p.Order.Channel == OrderChannel.Pos);
        var byMethod = await payQ.GroupBy(p => p.Method)
            .Select(g => new { Channel = g.Key, Amount = g.Sum(x => x.Amount), Count = g.Count() }).ToListAsync();

        var fbQ = paid.Where(o => !_db.OrderPayments.Any(p => p.OrderId == o.Id));
        var byProvider = await fbQ.GroupBy(o => o.PaymentProvider)
            .Select(g => new { Channel = g.Key, Amount = g.Sum(o => o.Total), Count = g.Count() }).ToListAsync();

        var channelMap = new Dictionary<string, (decimal Amount, int Count)>(StringComparer.OrdinalIgnoreCase);
        void AddCh(string? label, decimal amt, int cnt)
        {
            var key = string.IsNullOrWhiteSpace(label) ? "Other" : label.Trim();
            var cur = channelMap.GetValueOrDefault(key);
            channelMap[key] = (cur.Amount + amt, cur.Count + cnt);
        }
        foreach (var m in byMethod) AddCh(m.Channel, m.Amount, m.Count);
        foreach (var p in byProvider) AddCh(p.Channel, p.Amount, p.Count);
        var byChannel = channelMap
            .Select(kv => new ChannelPoint(kv.Key, kv.Value.Amount, kv.Value.Count))
            .OrderByDescending(c => c.Amount).ToList();

        var gross = chan.Sum(c => c.Gross);
        var discountTotal = chan.Sum(c => c.Disc);
        var loyaltyTotal = chan.Sum(c => c.Loy);
        var giftCardTotal = chan.Sum(c => c.Gift);

        // Previous equal-length window immediately before this one (for comparison chips).
        var prev = await SnapshotAsync(f - (t - f), f, storeId, channel);

        // Anomaly flags — quick things finance should look at.
        var alerts = new List<string>();
        if (gross > 0 && refundTotal / gross >= 0.15m)
            alerts.Add($"Refunds are {refundTotal / gross:P0} of gross revenue (₦{refundTotal:N0}) — worth reviewing returns.");
        var negPeriods = byPeriod.Count(p => p.Net < 0);
        if (negPeriods > 0)
            alerts.Add($"{negPeriods} {period}(s) closed with negative net — refunds exceeded sales.");
        var giveaway = discountTotal + loyaltyTotal + giftCardTotal;
        if (gross > 0 && giveaway / gross >= 0.15m)
            alerts.Add($"Discounts &amp; giveaways are {giveaway / gross:P0} of gross (₦{giveaway:N0}).");

        return new FinanceVm
        {
            From = f,
            To = t.AddDays(-1),
            StoreId = storeId,
            Channel = channel,
            Period = period,
            Stores = stores,
            Count = chan.Sum(c => c.Count),
            Gross = gross,
            Refunds = refundTotal,
            DeliveryFees = chan.Sum(c => c.Delivery),
            OnlineGross = onlineGross,
            PosGross = posGross,
            OnlineCount = onlineCount,
            PosCount = posCount,
            DiscountTotal = discountTotal,
            LoyaltyTotal = loyaltyTotal,
            GiftCardTotal = giftCardTotal,
            PrevCount = prev.Count,
            PrevGross = prev.Gross,
            PrevLogistics = prev.Logistics,
            PrevRefunds = prev.Refunds,
            Presets = BuildPresets(),
            Alerts = alerts,
            ByPeriod = byPeriod,
            ByState = byState,
            ByDeliveryType = byDeliveryType,
            ByChannel = byChannel,
            ByStore = byStore,
            ByStaff = byStaff
        };
    }
}
