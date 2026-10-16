using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace Kpi.Copy;

/// <summary>Where KPI_Export's copies stand: refreshed when, as at which business date, Bad Debt / Exposure prepared when.</summary>
public sealed record ExportState(DateTime RefreshedUtc, DateTime ReportDate, DateTime? SnapshotsUtc);

/// <summary>What a copy did. <c>Published</c> false means nothing changed in CMS_KPI, and <c>Reason</c> says why.</summary>
public sealed record CopyOutcome(bool Published, string Scope, int Tables, long Rows, DateTime ReportDate, TimeSpan Elapsed, string? Reason = null);

/// <summary>
/// The KPI copy, one way: KPI_Export's copy tables (on the office server) -> CMS_KPI's Stage -> Stage.spPublish, which
/// swaps everything in at once. Nothing the site reads changes until the publish, so a copy that stops part way changes
/// nothing. The source is only READ, through a login that can see the copies alone (kpi_pull); CMS_KPI is written
/// through one that can only fill Stage and publish (kpi_push).
/// </summary>
public static class KpiCopier
{
    /// <summary>Every copy: KPI_Export's table -> CMS_KPI's staging table.</summary>
    public static readonly IReadOnlyList<(string From, string To)> Tables =
    [
        ("KpiCopy.SalesOffice",  "Stage.SalesOffice"),
        ("KpiCopy.Salesperson",  "Stage.Salesperson"),
        ("KpiCopy.Period",       "Stage.Period"),
        ("KpiCopy.WorkingDay",   "Stage.WorkingDay"),
        ("KpiCopy.SalesTarget",  "Stage.SalesTarget"),
        ("KpiCopy.FundLender",   "Stage.FundLender"),
        ("KpiCopy.Fund",         "Stage.Fund"),
        ("KpiCopy.Lead",         "Stage.Lead"),
        ("KpiCopy.LeadPipeline", "Stage.LeadPipeline"),
        ("KpiCopy.SupplierYear", "Stage.SupplierYear"),
        ("KpiCopy.BookPosition", "Stage.BookPosition"),
        ("KpiCopy.FundingLine",  "Stage.FundingLine"),
    ];

    /// <summary>Added when Bad Debt and Exposure were prepared since the last copy (the nightly job).</summary>
    public static readonly IReadOnlyList<(string From, string To)> SnapshotTables =
    [
        ("KpiExport.BadDebtSnapshot",  "Stage.BadDebtYear"),
        ("KpiExport.ExposureSnapshot", "Stage.Exposure"),
    ];

    /// <summary>
    /// The one-row refresh state - all the site reads every two minutes. Null when KPI_Export has never been refreshed.
    /// Throws when KPI_Export is not the version this code was built for.
    /// </summary>
    public static async Task<ExportState?> ReadStateAsync(SqlConnection source, CancellationToken ct = default)
    {
        await using (var v = new SqlCommand(
            "IF OBJECT_ID(N'KpiExport.Version') IS NULL SELECT 0 ELSE SELECT ISNULL(MAX(Version), 0) FROM KpiExport.Version", source))
        {
            var version = (int)(await v.ExecuteScalarAsync(ct))!;
            if (version != ExportVersion.Current)
                throw new InvalidOperationException(
                    $"KPI_Export is at version {version}; this copy needs version {ExportVersion.Current}. " +
                    "Apply the script that came with it:  Kpi.Tools export-apply --cms <CMS database>");
        }

        // a row prepared by a brand-new database's first night but never refreshed has RefreshedUtc 2000-01-01: no copies yet
        await using var cmd = new SqlCommand(
            "IF OBJECT_ID(N'KpiCopy.Refresh') IS NOT NULL " +
            "SELECT TOP (1) RefreshedUtc, ReportDate, SnapshotsUtc FROM KpiCopy.Refresh WHERE RefreshedUtc > '2001-01-01'", source);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new ExportState(r.GetDateTime(0), r.GetDateTime(1), r.IsDBNull(2) ? null : r.GetDateTime(2));
    }

    /// <summary>
    /// Copies <paramref name="state"/>'s tables into Stage and publishes them (with Bad Debt and Exposure when
    /// <paramref name="withSnapshots"/>). Not published when another copy holds the lock, or when the office refreshed
    /// while this one was reading (the tables could then come from two refreshes) - the next try copies again.
    /// </summary>
    public static async Task<CopyOutcome> CopyAsync(SqlConnection source, SqlConnection kpi, ExportState state, bool withSnapshots,
                                                   Action<string>? progress = null, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        var scope = withSnapshots ? "Nightly" : "Frequent";

        // one copy at a time into CMS_KPI (the site, or a developer's Kpi.Push): held until this connection closes
        await using (var lockCmd = new SqlCommand(
            "DECLARE @r int; EXEC @r = sp_getapplock @Resource = 'KpiPush', @LockMode = 'Exclusive', " +
            "@LockOwner = 'Session', @LockTimeout = 0; SELECT @r;", kpi))
        {
            if ((int)(await lockCmd.ExecuteScalarAsync(ct))! < 0)
                return new CopyOutcome(false, scope, 0, 0, state.ReportDate, clock.Elapsed, "another copy was still running");
        }

        var copies = withSnapshots ? Tables.Concat(SnapshotTables).ToList() : Tables.ToList();
        long rows = 0;
        foreach (var (from, to) in copies)
        {
            await using (var del = new SqlCommand($"DELETE {to}", kpi) { CommandTimeout = 600 })
                await del.ExecuteNonQueryAsync(ct);

            await using var cmd = new SqlCommand($"SELECT * FROM {from}", source) { CommandTimeout = 600 };
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            using var bulk = new SqlBulkCopy(kpi, SqlBulkCopyOptions.TableLock, null) { DestinationTableName = to, BulkCopyTimeout = 600 };
            for (var i = 0; i < reader.FieldCount; i++)
                bulk.ColumnMappings.Add(reader.GetName(i), reader.GetName(i));   // by name, never by position
            await bulk.WriteToServerAsync(reader, ct);
            rows += bulk.RowsCopied;
            progress?.Invoke($"  {to,-20} {bulk.RowsCopied,8:N0} rows");
        }

        // refreshed while we read? then these tables may not belong together: publish nothing, copy again next time
        var after = await ReadStateAsync(source, ct);
        if (after is null || after.RefreshedUtc != state.RefreshedUtc || (withSnapshots && after.SnapshotsUtc != state.SnapshotsUtc))
            return new CopyOutcome(false, scope, copies.Count, rows, state.ReportDate, clock.Elapsed,
                                   "KPI_Export was refreshed while copying; copying again next time");

        // everything arrived: swap it in, in one transaction on the CMS_KPI side
        await using var publish = new SqlCommand("Stage.spPublish", kpi) { CommandType = CommandType.StoredProcedure, CommandTimeout = 600 };
        publish.Parameters.Add("@Scope", SqlDbType.VarChar, 10).Value = scope;
        publish.Parameters.Add("@ReportDate", SqlDbType.Date).Value = state.ReportDate.Date;
        try { await publish.ExecuteNonQueryAsync(ct); }
        catch (SqlException ex) { throw new PublishFailedException(ex); }

        return new CopyOutcome(true, scope, copies.Count, rows, state.ReportDate, clock.Elapsed);
    }

    /// <summary>A copy that failed before publishing, in CMS_KPI's load log (Settings > Data sync). Never throws.</summary>
    public static async Task RecordFailureAsync(string kpiConnection, string scope, DateTime startedUtc, string error)
    {
        try
        {
            await using var kpi = new SqlConnection(kpiConnection);
            await kpi.OpenAsync();
            await using var cmd = new SqlCommand("Stage.spRecordFailure", kpi) { CommandType = CommandType.StoredProcedure };
            cmd.Parameters.Add("@Scope", SqlDbType.VarChar, 10).Value = scope;
            cmd.Parameters.Add("@StartedUtc", SqlDbType.DateTime2).Value = startedUtc;
            cmd.Parameters.Add("@Error", SqlDbType.NVarChar, 2000).Value = error.Length > 2000 ? error[..2000] : error;
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* CMS_KPI could not be reached either: the overdue check covers that */ }
    }
}

/// <summary>Stage.spPublish refused or failed. It has logged that itself (Ref.LoadRun), so the caller doesn't again.</summary>
public sealed class PublishFailedException(SqlException inner) : Exception($"Publishing failed: {inner.Message}", inner);
