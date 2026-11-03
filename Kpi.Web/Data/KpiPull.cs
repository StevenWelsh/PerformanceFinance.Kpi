using System.Data;
using Kpi.Copy;
using Kpi.Data.Cfg;
using Kpi.Web.Integrations;
using Microsoft.Data.SqlClient;

namespace Kpi.Web.Data;

/// <summary>
/// The KPI copy, run by the site (Oct 2026, Steve: a pull, not a push) - since 3 Nov 2026 the task "Collect figures from the
/// office" on Settings > Scheduled tasks (Scheduling/Jobs.cs), every 2 minutes unless changed there. Each run reads the
/// office's two Agent jobs' own status and schedules (KpiCopy.JobStatus / JobSchedule, kept by OfficeJobsStore and shown
/// read only) and ONE row from KPI_Export (KpiCopy.Refresh, refreshed there by SQL Server Agent), and copies only when the office has
/// refreshed since CMS_KPI last published - Bad Debt and Exposure too when they were prepared since they were last copied
/// (the office prepares them every 2 hours, 06:00-20:00, since 3 Nov 2026). So the figures follow the office's schedule.
/// Run now copies even unchanged data.
///
/// Reads through the data source set in Settings > Integrations (CmsSource: a SQL login in kpi_pull, made by Kpi.Tools
/// grant-pull, which sees KPI_Export's copies and nothing in CMS), arriving from the site's own static IP through the
/// office's port forward; on a developer's machine ConnectionStrings:KpiExport can stand in. Looked up on every run, so a
/// change on the card takes effect at the next one. Writes through the site's usual CmsKpi connection, whose identity
/// is also in kpi_push (Kpi.Tools grant-push). Off while there is no data source, or it is switched off.
/// </summary>
public sealed class KpiPull(IConfiguration config, CmsSource cmsSource, IntegrationStore store, Kpis.KpiWarmer warmer,
                            KpiPullHealth health, Scheduling.OfficeJobsStore officeJobs, ILogger<KpiPull> log)
{
    private bool? _wasOn;

    /// <summary>One run: what happened, in a line for the task's History. Throws when the copy failed.</summary>
    public async Task<string> RunAsync(bool force, CancellationToken ct)
    {
        var kpi = config.GetConnectionString("CmsKpi")!;   // Program.cs refuses to start without it
        var source = await cmsSource.CopyConnectionAsync();
        if ((source is not null) != _wasOn)
            log.LogInformation(source is null ? "KPI copy is off: no data source connected, or it is switched off" : "KPI copy is on");
        _wasOn = source is not null;
        health.On(_wasOn.Value);   // for the alerts (SyncAlerts)
        if (source is null) return "Off: no SQL data source is connected, or it is switched off (Settings > Integrations).";

        try
        {
            var summary = await CopyIfRefreshedAsync(source, kpi, force, ct);
            await store.RecordCallAsync(IntegrationProviders.CmsSource, null);   // the card's "Last worked"
            return summary;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (ex is PublishFailedException publish)
                // the publish had opened its run: close it with the reason (spPublish may have already, if it caught the error)
                await KpiCopier.RecordFailureAsync(kpi, publish.Scope, publish.StartedUtc, ex.Message, closeOpenRun: true);
            try { await store.RecordCallAsync(IntegrationProviders.CmsSource, ex.Message); }   // the card's "Last problem"
            catch { /* CMS_KPI unreachable too: the task's run says so */ }
            throw;
        }
    }

    /// <summary>
    /// Copies when the office has refreshed since the last publish - or always, when <paramref name="force"/> (Run now).
    /// What happened, in a sentence.
    /// </summary>
    private async Task<string> CopyIfRefreshedAsync(string sourceConnection, string kpiConnection, bool force, CancellationToken ct)
    {
        await using var source = new SqlConnection(sourceConnection);
        try { await source.OpenAsync(ct); }
        catch (SqlException ex) { throw new CopyFailedException(KpiCopier.Explain(ex, KpiCopier.Steps.ConnectingOffice, source, null), ex); }
        ExportState? state;
        try { state = await KpiCopier.ReadStateAsync(source, ct); }
        catch (SqlException ex) { throw new CopyFailedException(KpiCopier.Explain(ex, KpiCopier.Steps.CheckingOffice, source, null), ex); }
        // the office's two Agent jobs, as they report themselves (Settings > Scheduled tasks, read only) - at every check
        try { await officeJobs.SaveAsync(await KpiCopier.ReadJobsAsync(source, ct)); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogWarning(ex, "The office's job status couldn't be read or kept"); }
        if (state is null) return "Nothing copied: the office's KPI_Export has never been refreshed.";

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
            return $"Nothing new in the office (data from {UkTime.ShowShort(state.RefreshedUtc)}).";

        var outcome = await KpiCopier.CopyAsync(source, kpi, state, withSnapshots, ct: ct);
        if (outcome.Published)
        {
            await store.RecordCallAsync(IntegrationProviders.CmsSource, null);   // the card's "Last worked"
            warmer.AfterCopy();   // re-run the cached reports now, before anyone asks for the new figures
            log.LogInformation("KPI copy ({Scope}) published {Tables} tables, {Rows:N0} rows, as at {Date:dd/MM/yyyy}, in {Seconds:0.0} s",
                outcome.Scope, outcome.Tables, outcome.Rows, outcome.ReportDate, outcome.Elapsed.TotalSeconds);
            var what = force && lastLoadUtc is not null && state.RefreshedUtc <= lastLoadUtc
                ? $"Copied the same data again ({UkTime.ShowShort(state.RefreshedUtc)})"
                : $"New data from {UkTime.ShowShort(state.RefreshedUtc)}, copied";
            return $"{what} in {outcome.Elapsed.TotalSeconds:0} s."
                   + (withSnapshots ? $" With Bad Debt & Exposure prepared {UkTime.ShowShort(state.SnapshotsUtc)}." : "");
        }
        if (outcome.Tables == 0)
            // refused before copying anything (another copy holds the lock): a failure, the task records it (19 Oct 2026)
            throw new CopyBlockedException(outcome.Reason ?? "the copy could not start", state.RefreshedUtc);

        // the office refreshed mid-copy: harmless, the next check copies the newer data
        log.LogInformation("KPI copy ({Scope}) not published: {Reason}", outcome.Scope, outcome.Reason);
        return "Not published: the office refreshed while copying, so the next run copies the newer figures.";
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
