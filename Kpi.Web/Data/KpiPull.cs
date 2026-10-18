using System.Data;
using Kpi.Copy;
using Kpi.Data.Cfg;
using Kpi.Web.Integrations;
using Microsoft.Data.SqlClient;

namespace Kpi.Web.Data;

/// <summary>
/// The KPI copy, run by the site (Oct 2026, Steve: a pull, not a push). Every two minutes it reads ONE row from KPI_Export
/// on the office server (KpiCopy.Refresh, refreshed there every 10 minutes by SQL Server Agent) and copies only when the
/// office has refreshed since CMS_KPI last published - Bad Debt and Exposure too when they were prepared since the last
/// nightly publish. So the figures follow the office's schedule; nothing here has one of its own.
///
/// Reads through the data source set in Settings > Integrations (CmsSource: a SQL login in kpi_pull, made by Kpi.Tools
/// grant-pull, which sees KPI_Export's copies and nothing in CMS), arriving from the site's own static IP through the
/// office's port forward; on a developer's machine ConnectionStrings:KpiExport can stand in. Looked up on every check, so a
/// change on the card takes effect within two minutes. Writes through the site's usual CmsKpi connection, whose identity
/// is also in kpi_push (Kpi.Tools grant-push). Off while there is no data source, or it is switched off.
/// </summary>
public sealed class KpiPull(IConfiguration config, CmsSource cmsSource, IntegrationStore store, Kpis.KpiWarmer warmer, ILogger<KpiPull> log)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    // a failure goes to the load log (Settings > Data sync) once when it starts and then hourly while it lasts, not every
    // two minutes; the site's log has every one
    private static readonly TimeSpan RecordFailureEvery = TimeSpan.FromHours(1);
    private DateTime? _failingSinceUtc, _lastRecordedUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var kpi = config.GetConnectionString("CmsKpi")!;   // Program.cs refuses to start without it
        bool? wasOn = null;

        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);   // let the site start first
        using var timer = new PeriodicTimer(Interval);
        do
        {
            var startedUtc = DateTime.UtcNow;
            var scope = "Frequent";
            try
            {
                var source = await cmsSource.CopyConnectionAsync();
                if ((source is not null) != wasOn)
                    log.LogInformation(source is null ? "KPI copy is off: no data source connected, or it is switched off"
                                                      : "KPI copy is on");
                wasOn = source is not null;
                if (source is null) continue;

                scope = await CopyIfRefreshedAsync(source, kpi, stoppingToken) ?? scope;
                if (_failingSinceUtc is not null)
                {
                    log.LogInformation("KPI copy is working again (failing since {Since:u})", _failingSinceUtc);
                    await store.RecordCallAsync(IntegrationProviders.CmsSource, null);
                }
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
                    try { await store.RecordCallAsync(IntegrationProviders.CmsSource, ex.Message); }   // the card's "Last problem"
                    catch { /* CMS_KPI unreachable too: the load log and overdue flag cover it */ }
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
        {
            await store.RecordCallAsync(IntegrationProviders.CmsSource, null);   // the card's "Last worked"
            warmer.AfterCopy();   // re-run the cached reports now, before anyone asks for the new figures
            log.LogInformation("KPI copy ({Scope}) published {Tables} tables, {Rows:N0} rows, as at {Date:dd/MM/yyyy}, in {Seconds:0.0} s",
                outcome.Scope, outcome.Tables, outcome.Rows, outcome.ReportDate, outcome.Elapsed.TotalSeconds);
        }
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
