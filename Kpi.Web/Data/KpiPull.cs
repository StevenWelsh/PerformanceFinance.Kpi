using System.Data;
using Kpi.Copy;
using Kpi.Data.Cfg;
using Kpi.Web.Integrations;
using Microsoft.Data.SqlClient;

namespace Kpi.Web.Data;

/// <summary>
/// The KPI copy, run by the site (Oct 2026, Steve: a pull, not a push). Every few minutes (2 unless changed on Settings >
/// Data sync) it reads ONE row from KPI_Export on the office server (KpiCopy.Refresh, refreshed there every 10 minutes by
/// SQL Server Agent) and copies only when the office has refreshed since CMS_KPI last published - Bad Debt and Exposure
/// too when they were prepared since the last nightly publish. So the figures follow the office's schedule. Data sync can
/// also PAUSE it and ask for a COPY NOW (<see cref="KpiCopyControl"/>), which copies even unchanged data.
///
/// Reads through the data source set in Settings > Integrations (CmsSource: a SQL login in kpi_pull, made by Kpi.Tools
/// grant-pull, which sees KPI_Export's copies and nothing in CMS), arriving from the site's own static IP through the
/// office's port forward; on a developer's machine ConnectionStrings:KpiExport can stand in. Looked up on every check, so a
/// change on the card takes effect at the next one. Writes through the site's usual CmsKpi connection, whose identity
/// is also in kpi_push (Kpi.Tools grant-push). Off while there is no data source, or it is switched off.
/// </summary>
public sealed class KpiPull(IConfiguration config, CmsSource cmsSource, IntegrationStore store, Kpis.KpiWarmer warmer,
                            KpiPullHealth health, KpiCopyControl control, ILogger<KpiPull> log)
    : BackgroundService
{
    // a failure goes to the load log (Settings > Data sync) once when it starts and then hourly while it lasts, not at
    // every check; the site's log has every one. A Copy now's failure, and a publish the site gave up on, always go.
    private static readonly TimeSpan RecordFailureEvery = TimeSpan.FromHours(1);
    private DateTime? _failingSinceUtc, _lastRecordedUtc;
    private bool? _wasOn;

    private sealed record CheckResult(string? Scope, string Summary);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var kpi = config.GetConnectionString("CmsKpi")!;   // Program.cs refuses to start without it
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);   // let the site start first
            while (!stoppingToken.IsCancellationRequested)
            {
                var schedule = await control.GetAsync();
                var requestedBy = control.TakeRequest();
                if (schedule.Paused && requestedBy is null)
                    health.Worked();   // paused on purpose: nothing is failing (the alerts say it's paused)
                else
                    await CheckAsync(kpi, requestedBy, stoppingToken);
                await control.WaitAsync(TimeSpan.FromMinutes(schedule.EveryMinutes), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task CheckAsync(string kpi, string? requestedBy, CancellationToken ct)
    {
        var manual = requestedBy is not null;
        var startedUtc = DateTime.UtcNow;
        var scope = "Frequent";
        control.Started();
        try
        {
            var source = await cmsSource.CopyConnectionAsync();
            if ((source is not null) != _wasOn)
                log.LogInformation(source is null ? "KPI copy is off: no data source connected, or it is switched off"
                                                  : "KPI copy is on");
            _wasOn = source is not null;
            health.On(_wasOn.Value);   // for the alerts (SyncAlerts)
            if (source is null)
            {
                health.Worked();
                control.Finished(manual, false, "Nothing copied: no SQL data source is connected, or it is switched off (Settings > Integrations).");
                return;
            }

            if (manual) log.LogInformation("KPI copy now, asked for by {Who}", requestedBy);
            var result = await CopyIfRefreshedAsync(source, kpi, force: manual, ct);
            scope = result.Scope ?? scope;
            if (_failingSinceUtc is not null)
            {
                log.LogInformation("KPI copy is working again (failing since {Since:u})", _failingSinceUtc);
                await store.RecordCallAsync(IntegrationProviders.CmsSource, null);
            }
            _failingSinceUtc = _lastRecordedUtc = null;
            health.Worked();
            control.Finished(manual, true, result.Summary);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _failingSinceUtc ??= startedUtc;
            health.Failed(_failingSinceUtc.Value, ex.Message);
            log.LogWarning(ex, "KPI copy failed; trying again at the next check");
            if (ex is PublishFailedException publish)
                // the publish had opened its run: close it with the reason (spPublish may have already, if it caught the error)
                await KpiCopier.RecordFailureAsync(kpi, publish.Scope, publish.StartedUtc, ex.Message, closeOpenRun: true);
            else if (manual || _lastRecordedUtc is null || DateTime.UtcNow - _lastRecordedUtc > RecordFailureEvery)
            {
                // anything before the publish (the office, KPI_Export, the bulk copy) is logged here
                await KpiCopier.RecordFailureAsync(kpi, scope, startedUtc, ex.Message, (ex as CopyBlockedException)?.SourceRefreshedUtc);
                try { await store.RecordCallAsync(IntegrationProviders.CmsSource, ex.Message); }   // the card's "Last problem"
                catch { /* CMS_KPI unreachable too: the load log and overdue flag cover it */ }
                _lastRecordedUtc = DateTime.UtcNow;
            }
            control.Finished(manual, false, ex.Message);
        }
        catch (OperationCanceledException)
        {
            control.Finished(manual, false, "Stopped: the site is shutting down.");
            throw;
        }
    }

    /// <summary>
    /// Copies when the office has refreshed since the last publish - or always, when <paramref name="force"/> (Copy now).
    /// The scope it copied (null when nothing to do), and what happened in a sentence for Data sync.
    /// </summary>
    private async Task<CheckResult> CopyIfRefreshedAsync(string sourceConnection, string kpiConnection, bool force, CancellationToken ct)
    {
        await using var source = new SqlConnection(sourceConnection);
        try { await source.OpenAsync(ct); }
        catch (SqlException ex) { throw new CopyFailedException(KpiCopier.Explain(ex, KpiCopier.Steps.ConnectingOffice, source, null), ex); }
        ExportState? state;
        try { state = await KpiCopier.ReadStateAsync(source, ct); }
        catch (SqlException ex) { throw new CopyFailedException(KpiCopier.Explain(ex, KpiCopier.Steps.CheckingOffice, source, null), ex); }
        if (state is null) return new(null, "Nothing copied: the office's KPI_Export has never been refreshed.");

        // its OWN connection, outside the site's pool (19 Oct 2026): the copy's lock lives on this connection's session,
        // so it must really close afterwards, never sit in the pool (KpiCopier releases the lock too)
        await using var kpi = new SqlConnection(new SqlConnectionStringBuilder(kpiConnection) { Pooling = false }.ConnectionString);
        try { await kpi.OpenAsync(ct); }
        catch (SqlException ex) { throw new CopyFailedException(KpiCopier.Explain(ex, KpiCopier.Steps.ConnectingSite, source, kpi), ex); }
        DateTime? lastLoadUtc, lastNightlyUtc;
        try { (lastLoadUtc, lastNightlyUtc) = await LastLoadsAsync(kpi, ct); }
        catch (SqlException ex) { throw new CopyFailedException(KpiCopier.Explain(ex, KpiCopier.Steps.CheckingSite, source, kpi), ex); }

        var withSnapshots = state.SnapshotsUtc is { } prepared && (lastNightlyUtc is null || prepared > lastNightlyUtc);
        if (!force && !withSnapshots && lastLoadUtc is not null && state.RefreshedUtc <= lastLoadUtc)   // already published
            return new(null, "Nothing new to copy.");

        var outcome = await KpiCopier.CopyAsync(source, kpi, state, withSnapshots, ct: ct);
        if (outcome.Published)
        {
            await store.RecordCallAsync(IntegrationProviders.CmsSource, null);   // the card's "Last worked"
            warmer.AfterCopy();   // re-run the cached reports now, before anyone asks for the new figures
            log.LogInformation("KPI copy ({Scope}) published {Tables} tables, {Rows:N0} rows, as at {Date:dd/MM/yyyy}, in {Seconds:0.0} s",
                outcome.Scope, outcome.Tables, outcome.Rows, outcome.ReportDate, outcome.Elapsed.TotalSeconds);
            return new(outcome.Scope, $"Copied and published{(withSnapshots ? ", with Bad Debt & Exposure," : "")} in {outcome.Elapsed.TotalSeconds:0} s.");
        }
        if (outcome.Tables == 0)
            // refused before copying anything (another copy holds the lock): a failure, recorded on Data sync (19 Oct 2026)
            throw new CopyBlockedException(outcome.Reason ?? "the copy could not start", state.RefreshedUtc);

        // the office refreshed mid-copy: harmless, the next check copies the newer data
        log.LogInformation("KPI copy ({Scope}) not published: {Reason}", outcome.Scope, outcome.Reason);
        return new(outcome.Scope, "Not published: the office refreshed while copying, so the next check copies the newer figures.");
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
