using System.Threading.Channels;
using Kpi.Data;
using Kpi.Data.Auth;
using Microsoft.EntityFrameworkCore;

namespace Kpi.Web.Account;

/// <summary>
/// Records what people do on the reports (Auth.ReportEvent; Settings > Audit > Report activity - 29 Oct 2026, Steve):
/// opening one, choosing filters, switching views, pressing buttons (the timelapse, with its year), closing it with the
/// time it was looked at, and how long figures took to load. KpiPanel's tracker and KpiCache call <see cref="Record"/>,
/// which only queues: a writer saves the queue every few seconds in one go, so a click never waits on the database and a
/// burst of clicks is one insert. Never throws; when the queue is full (the database unreachable for a long time) new
/// events are dropped rather than held in memory.
/// </summary>
public sealed class ReportActivity(IDbContextFactory<KpiDbContext> dbs, ILogger<ReportActivity> log) : BackgroundService
{
    private readonly Channel<ReportEvent> _queue = Channel.CreateBounded<ReportEvent>(
        new BoundedChannelOptions(20_000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public void Record(ReportEvent e)
    {
        e.OccurredUtc = e.OccurredUtc == default ? DateTime.UtcNow : e.OccurredUtc;
        e.Report = Cut(e.Report, 60)!; e.UserName = Cut(e.UserName, 256)!; e.Label = Cut(e.Label, 200)!;
        e.Value = Cut(e.Value, 400); e.Filters = Cut(e.Filters, 1000);
        _queue.Writer.TryWrite(e);
    }

    // which report a cache part belongs to (KpiCache's names; Salesperson Summary shares Sales Summary's cells)
    private static readonly Dictionary<string, string> PartReports = new(StringComparer.Ordinal)
    {
        ["BadDebt"] = "bad-debt", ["BusinessAnalysis"] = "business-analysis", ["Exposure"] = "exposure",
        ["HeatMap"] = "heat-map", ["MarginAnalysis"] = "margin-analysis", ["MinimumIrr"] = "minimum-irr",
        ["RecordPerformances"] = "record-performances", ["RecordTable"] = "record-performances",
        ["SalesPerformance"] = "sales-performance", ["SalesProductivity"] = "sales-productivity",
        ["SalesSummary"] = "sales-summary", ["SalesSummaryPanel"] = "sales-summary",
        ["SalespersonSummary"] = "salesperson-summary", ["SupplierFigures"] = "suppliers-yoy", ["YearOnYear"] = "year-on-year",
    };

    /// <summary>A report's figures loaded for someone waiting (not from the cache, not the background pre-load).</summary>
    public void RecordLoad(string part, string parameters, TimeSpan took) => Record(new ReportEvent
    {
        Session = "", Report = PartReports.GetValueOrDefault(part, part), UserName = "", Kind = ReportEventKinds.Load,
        Label = part, Value = parameters, Ms = (int)Math.Min(int.MaxValue, took.TotalMilliseconds)
    });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<ReportEvent>();
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);   // gather a burst into one insert
                while (batch.Count < 500 && _queue.Reader.TryRead(out var e)) batch.Add(e);
                await SaveAsync(batch);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            while (_queue.Reader.TryRead(out var e)) batch.Add(e);   // shutting down: save what's left
            await SaveAsync(batch);
        }
    }

    private async Task SaveAsync(List<ReportEvent> batch)
    {
        if (batch.Count == 0) return;
        try
        {
            await using var db = await dbs.CreateDbContextAsync();
            db.ReportEvents.AddRange(batch);
            await db.SaveChangesAsync();
        }
        catch (Exception ex) { log.LogWarning(ex, "{Count} report activity event(s) not recorded", batch.Count); }
        batch.Clear();
    }

    private static string? Cut(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;
}
