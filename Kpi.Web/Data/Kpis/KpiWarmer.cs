namespace Kpi.Web.Data.Kpis;

/// <summary>
/// Keeps the KPI cache ready before anyone asks (16 Oct 2026, then 18 Oct: Steve, "always slow" - Sales Summary and
/// Salesperson Summary are 9-15 queries each, and every restart or new day started them cold). It fills / refreshes:
///   - every report view people have opened (the cache remembers them, <see cref="KpiCache.WarmAsync"/> /
///     <see cref="KpiCache.RefreshViewsAsync"/>), and
///   - every cell of Sales Summary and Salesperson Summary (<see cref="SalesSummaryCells"/>), opened or not,
/// at three moments:
///   - START-UP (a deploy or restart empties the cache): loads what's missing, a minute after the site starts;
///   - a NEW BUSINESS DATE (checked every two minutes; the date provider caches five): loads under the new date;
///   - a NEW DATASET published by the site's own copy (<see cref="AfterCopy"/>, from KpiPull - Steve: "from the sync"):
///     RE-RUNS everything, as the keys are the same and only the figures changed.
/// One run at a time, one query at a time, so viewers keep their share of the database.
/// </summary>
public sealed class KpiWarmer(KpiCache cache, ReportDateProvider reportDate, KpiSessionFactory sessions, ILogger<KpiWarmer> log)
    : BackgroundService
{
    private readonly SemaphoreSlim _running = new(1, 1);
    private CancellationToken _stopping = CancellationToken.None;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }   // let the site start first
        catch (OperationCanceledException) { return; }

        DateTime? last = null;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        do
        {
            try
            {
                var date = await Task.Run(reportDate.Get, stoppingToken);
                if (last is null) await RunAsync("start-up", refresh: false, date);
                else if (date != last) await RunAsync($"new date {date:dd/MM/yyyy}", refresh: false, date);
                last = date;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogWarning(ex, "KPI pre-load check failed; trying again in two minutes");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>A new dataset was just published: refresh everything in the background (returns at once).</summary>
    public void AfterCopy() => _ = Task.Run(async () =>
    {
        try { await RunAsync("new dataset", refresh: true, await Task.Run(reportDate.Get)); }
        catch (Exception ex) { log.LogWarning(ex, "KPI refresh after a new dataset failed"); }
    });

    private async Task RunAsync(string why, bool refresh, DateTime date)
    {
        // one run at a time: a dataset arriving mid-run waits, then re-runs with the newer figures
        await _running.WaitAsync(_stopping);
        try
        {
            var started = DateTime.UtcNow;
            var views = refresh ? await cache.RefreshViewsAsync(date, _stopping) : await cache.WarmAsync(date, _stopping);
            // the remembered views include any cell already loaded (refreshed just above), so the cells pass only fills
            // what is missing - never re-runs a cell twice in one go
            var cells = await SalesSummaryCells.PrimeAsync(cache, sessions, refresh: false, _stopping);
            log.LogInformation("KPI pre-load ({Why}): {Views} remembered view(s) and {Cells} Sales Summary cells checked for {Date:dd/MM/yyyy} in {Seconds:0} s",
                why, views, cells, date, (DateTime.UtcNow - started).TotalSeconds);
        }
        finally { _running.Release(); }
    }
}
