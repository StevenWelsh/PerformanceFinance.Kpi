using System.Collections.Concurrent;
using Kpi.Data;
using Kpi.Data.Auth;
using Microsoft.EntityFrameworkCore;

namespace Kpi.Web.Account;

/// <summary>
/// Records who opens which report (Auth.ReportView; Settings > Audit > Report views - 19 Oct 2026, Steve). Called by
/// KpiPanel when a report page opens on the site, and by CmsDesktopDoor when an embedding app's ticket opens one. The
/// same person opening the same report again within 30 minutes counts once (switching filters, reconnecting, the
/// page's own refreshes never count). Never throws: a record is a record, not a gate.
/// </summary>
public sealed class ReportViews(IDbContextFactory<KpiDbContext> dbs, ILogger<ReportViews> log)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, DateTime> _recent = new();

    public async Task RecordAsync(string report, string? userId, string userName, string source)
    {
        if (string.IsNullOrWhiteSpace(report) || string.IsNullOrWhiteSpace(userName)) return;
        var now = DateTime.UtcNow;
        var who = userId ?? "app:" + userName;
        var key = $"{who}|{report}".ToLowerInvariant();
        if (_recent.TryGetValue(key, out var last) && now - last < Window) return;
        _recent[key] = now;
        if (_recent.Count > 5000)
            foreach (var (k, at) in _recent) if (now - at > Window) _recent.TryRemove(k, out _);

        try
        {
            await using var db = await dbs.CreateDbContextAsync();
            db.ReportViews.Add(new ReportView
            {
                OpenedUtc = now, Report = report.Length > 60 ? report[..60] : report,
                UserId = userId, UserName = userName.Length > 256 ? userName[..256] : userName, Source = source
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex) { log.LogWarning(ex, "Report view of {Report} by {User} not recorded", report, userName); }
    }
}

/// <summary>
/// Keeps the audit trail and report views to <see cref="Months"/> months (19 Oct 2026, Steve: "3 months"): runs
/// Auth.spPruneHistory a few minutes after start-up and then every night at about 02:30 UK.
/// </summary>
public sealed class HistoryPruner(IDbContextFactory<KpiDbContext> dbs, ILogger<HistoryPruner> log) : BackgroundService
{
    public const int Months = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var db = await dbs.CreateDbContextAsync(stoppingToken);
                await db.Database.ExecuteSqlRawAsync("EXEC Auth.spPruneHistory @Months = {0}", [Months], stoppingToken);
                log.LogInformation("Audit trail and report views older than {Months} months removed", Months);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogWarning(ex, "History tidy-up failed; trying again tomorrow");
            }

            // next 02:30 UK
            var ukNow = Kpi.Web.Data.UkTime.FromUtc(DateTime.UtcNow);
            var next = ukNow.Date.AddHours(2.5);
            if (next <= ukNow) next = next.AddDays(1);
            try { await Task.Delay(next - ukNow, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
