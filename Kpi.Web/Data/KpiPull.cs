using System.Data;
using Kpi.Copy;
using Microsoft.Data.SqlClient;

namespace Kpi.Web.Data;

/// <summary>
/// The KPI copy, run by the site (Oct 2026, Steve: a pull, not a push). Every two minutes it reads ONE row from KPI_Export
/// on the office server (KpiCopy.Refresh, refreshed there every 10 minutes by SQL Server Agent) and copies only when the
/// office has refreshed since CMS_KPI last published - Bad Debt and Exposure too when they were prepared since the last
/// nightly publish. So the figures follow the office's schedule; nothing here has one of its own.
///
/// Reads through ConnectionStrings:KpiExport - a SQL login in kpi_pull (Kpi.Tools grant-pull), which sees KPI_Export's
/// copies and nothing in CMS - arriving from the site's own static IP through the office's port forward. Writes through
/// the site's usual CmsKpi connection, whose identity is also in kpi_push (Kpi.Tools grant-push). Off when KpiExport isn't
/// set (a developer's machine, unless you want it).
/// </summary>
public sealed class KpiPull(IConfiguration config, ILogger<KpiPull> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    // a failure goes to the load log (Settings > Data sync) once when it starts and then hourly while it lasts, not every
    // two minutes; the site's log has every one
    private static readonly TimeSpan RecordFailureEvery = TimeSpan.FromHours(1);
    private DateTime? _failingSinceUtc, _lastRecordedUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var source = config.GetConnectionString("KpiExport");
        var kpi = config.GetConnectionString("CmsKpi");
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(kpi))
        {
            log.LogInformation("KPI copy is off: ConnectionStrings:KpiExport is not set");
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);   // let the site start first
        using var timer = new PeriodicTimer(Interval);
        do
        {
            var startedUtc = DateTime.UtcNow;
            var scope = "Frequent";
            try
            {
                scope = await CopyIfRefreshedAsync(source, kpi, stoppingToken) ?? scope;
                if (_failingSinceUtc is not null)
                    log.LogInformation("KPI copy is working again (failing since {Since:u})", _failingSinceUtc);
                _failingSinceUtc = _lastRecordedUtc = null;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _failingSinceUtc ??= startedUtc;
                log.LogWarning(ex, "KPI copy failed; trying again in two minutes");
                // Stage.spPublish logs its own failure; anything before it (the office, KPI_Export, the bulk copy) is logged here
                if (ex is not PublishFailedException && (_lastRecordedUtc is null || DateTime.UtcNow - _lastRecordedUtc > RecordFailureEvery))
                {
                    await KpiCopier.RecordFailureAsync(kpi, scope, startedUtc, ex.Message);
                    _lastRecordedUtc = DateTime.UtcNow;
                }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Copies when the office has refreshed since the last publish; the scope it copied, or null when nothing to do.</summary>
    private async Task<string?> CopyIfRefreshedAsync(string sourceConnection, string kpiConnection, CancellationToken ct)
    {
        await using var source = new SqlConnection(sourceConnection);
        await source.OpenAsync(ct);
        var state = await KpiCopier.ReadStateAsync(source, ct);
        if (state is null) return null;                                    // KPI_Export never refreshed yet

        await using var kpi = new SqlConnection(kpiConnection);
        await kpi.OpenAsync(ct);
        var (lastLoadUtc, lastNightlyUtc) = await LastLoadsAsync(kpi, ct);

        var withSnapshots = state.SnapshotsUtc is { } prepared && (lastNightlyUtc is null || prepared > lastNightlyUtc);
        if (!withSnapshots && lastLoadUtc is not null && state.RefreshedUtc <= lastLoadUtc) return null;   // already published

        var outcome = await KpiCopier.CopyAsync(source, kpi, state, withSnapshots, ct: ct);
        if (outcome.Published)
            log.LogInformation("KPI copy ({Scope}) published {Tables} tables, {Rows:N0} rows, as at {Date:dd/MM/yyyy}, in {Seconds:0.0} s",
                outcome.Scope, outcome.Tables, outcome.Rows, outcome.ReportDate, outcome.Elapsed.TotalSeconds);
        else
            log.LogInformation("KPI copy ({Scope}) not published: {Reason}", outcome.Scope, outcome.Reason);
        return outcome.Scope;
    }

    // when CMS_KPI last published (Rpt.spDataStatus, which the site may run)
    private static async Task<(DateTime? Frequent, DateTime? Nightly)> LastLoadsAsync(SqlConnection kpi, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("Rpt.spDataStatus", kpi) { CommandType = CommandType.StoredProcedure };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return (null, null);
        var frequent = r.GetOrdinal("LastFrequentLoadUtc");
        var nightly = r.GetOrdinal("LastNightlyLoadUtc");
        return (r.IsDBNull(frequent) ? null : r.GetDateTime(frequent), r.IsDBNull(nightly) ? null : r.GetDateTime(nightly));
    }
}
