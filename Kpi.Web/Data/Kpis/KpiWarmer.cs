namespace Kpi.Web.Data.Kpis;

/// <summary>
/// Watches the business report date and, when the morning's first copy from CMS moves it on, has the KPI cache
/// reload the report views people use (<see cref="KpiCache.WarmAsync"/>), so the first person in each morning doesn't
/// wait for each report's full calculation. Checks every two minutes; the date provider caches five, so the
/// pre-load starts within about seven minutes of the copy.
/// </summary>
public sealed class KpiWarmer(KpiCache cache, ReportDateProvider reportDate, ILogger<KpiWarmer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTime? last = null;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        do
        {
            try
            {
                var date = await Task.Run(reportDate.Get, stoppingToken);
                if (last is not null && date != last)
                {
                    var count = await cache.WarmAsync(date, stoppingToken);
                    log.LogInformation("Pre-loaded {Count} report view(s) for {Date:dd/MM/yyyy}", count, date);
                }
                last = date;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogWarning(ex, "KPI pre-load check failed; trying again in two minutes");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
