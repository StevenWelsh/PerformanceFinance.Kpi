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
        ("KpiCopy.BadDebtSnapshot",  "Stage.BadDebtYear"),
        ("KpiCopy.ExposureSnapshot", "Stage.Exposure"),
    ];

    /// <summary>
    /// The one-row refresh state - all the site reads every two minutes. Null when KPI_Export has never been refreshed.
    /// Throws when KPI_Export is not the version this code was built for.
    /// </summary>
    public static async Task<ExportState?> ReadStateAsync(SqlConnection source, CancellationToken ct = default)
    {
        await using (var v = new SqlCommand(
            "IF OBJECT_ID(N'KpiCopy.Version') IS NULL SELECT 0 ELSE SELECT ISNULL(MAX(Version), 0) FROM KpiCopy.Version", source))
        {
            var version = (int)(await v.ExecuteScalarAsync(ct))!;
            if (version != ExportVersion.Current)
                throw new InvalidOperationException(
                    $"KPI_Export is at version {version}; this copy needs version {ExportVersion.Current}. " +
                    "Apply the script that came with it:  Kpi.Tools export-apply --cms <CMS database>");
        }

        await using var cmd = new SqlCommand(
            "IF OBJECT_ID(N'KpiCopy.Refresh') IS NOT NULL SELECT TOP (1) RefreshedUtc, ReportDate, SnapshotsUtc FROM KpiCopy.Refresh", source);
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
        var startedUtc = DateTime.UtcNow;   // Data sync's "Started" / "Took" cover the whole copy, not just the publish
        var scope = withSnapshots ? "Nightly" : "Frequent";

        // one copy at a time into CMS_KPI (the site, or a developer's Kpi.Push). RELEASED EXPLICITLY when the copy ends,
        // however it ends (19 Oct 2026): "until the connection closes" was not enough - a pooled connection is only put
        // back in the pool, still holding the lock, and with Min Pool Size it could sit there for hours, every later copy
        // then refused as "another copy was still running" (14:51-17:45 that day, and 11:41-12:15).
        await using (var lockCmd = new SqlCommand(
            "DECLARE @r int; EXEC @r = sp_getapplock @Resource = 'KpiPush', @LockMode = 'Exclusive', " +
            "@LockOwner = 'Session', @LockTimeout = 0; SELECT @r;", kpi))
        {
            if ((int)(await lockCmd.ExecuteScalarAsync(ct))! < 0)
                return new CopyOutcome(false, scope, 0, 0, state.ReportDate, clock.Elapsed, "another copy was still running");
        }
        try
        {
            return await CopyLockedAsync(source, kpi, state, withSnapshots, scope, startedUtc, clock, progress, ct);
        }
        finally
        {
            try
            {
                if (kpi.State == ConnectionState.Open)
                {
                    await using var release = new SqlCommand(
                        "IF APPLOCK_MODE('public', 'KpiPush', 'Session') <> 'NoLock' EXEC sp_releaseapplock @Resource = 'KpiPush', @LockOwner = 'Session';", kpi);
                    await release.ExecuteNonQueryAsync(CancellationToken.None);
                }
            }
            catch { /* the connection broke: closing it ends the session, and the lock with it */ }
        }
    }

    private static async Task<CopyOutcome> CopyLockedAsync(SqlConnection source, SqlConnection kpi, ExportState state, bool withSnapshots,
                                                           string scope, DateTime startedUtc, Stopwatch clock, Action<string>? progress, CancellationToken ct)
    {
        var copies = withSnapshots ? Tables.Concat(SnapshotTables).ToList() : Tables.ToList();
        long rows = 0;
        ExportState? after;
        try
        {
            foreach (var (from, to) in copies)
            {
                // emptied with TRUNCATE (Stage.spClear), never DELETE (29 Oct 2026): Stage's tables are heaps, and a DELETE
                // there leaves its pages allocated - Stage.Lead grew to 4.3 GB for 25,000 rows and the copy from 1 minute to 15
                await using (var clear = new SqlCommand("Stage.spClear", kpi) { CommandType = CommandType.StoredProcedure, CommandTimeout = StepTimeoutSeconds })
                {
                    clear.Parameters.Add("@Table", SqlDbType.NVarChar, 128).Value = to["Stage.".Length..];
                    await clear.ExecuteNonQueryAsync(ct);
                }

                await using var cmd = new SqlCommand($"SELECT * FROM {from}", source) { CommandTimeout = StepTimeoutSeconds };
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                using var bulk = new SqlBulkCopy(kpi, SqlBulkCopyOptions.TableLock, null) { DestinationTableName = to, BulkCopyTimeout = StepTimeoutSeconds };
                for (var i = 0; i < reader.FieldCount; i++)
                    bulk.ColumnMappings.Add(reader.GetName(i), reader.GetName(i));   // by name, never by position
                await bulk.WriteToServerAsync(reader, ct);
                rows += bulk.RowsCopied;
                progress?.Invoke($"  {to,-20} {bulk.RowsCopied,8:N0} rows");
            }

            // refreshed while we read? then these tables may not belong together: publish nothing, copy again next time
            after = await ReadStateAsync(source, ct);
        }
        catch (Exception ex) when (ex is SqlException || ex.InnerException is SqlException)
        {
            throw new CopyFailedException(Explain(ex, Steps.Copying, source, kpi), ex);
        }
        if (after is null || after.RefreshedUtc != state.RefreshedUtc || (withSnapshots && after.SnapshotsUtc != state.SnapshotsUtc))
            return new CopyOutcome(false, scope, copies.Count, rows, state.ReportDate, clock.Elapsed,
                                   "KPI_Export was refreshed while copying; copying again next time");

        // everything arrived: swap it in, in one transaction on the CMS_KPI side
        await using var publish = new SqlCommand("Stage.spPublish", kpi) { CommandType = CommandType.StoredProcedure, CommandTimeout = StepTimeoutSeconds };
        publish.Parameters.Add("@Scope", SqlDbType.VarChar, 10).Value = scope;
        publish.Parameters.Add("@ReportDate", SqlDbType.Date).Value = state.ReportDate.Date;
        publish.Parameters.Add("@StartedUtc", SqlDbType.DateTime2).Value = startedUtc;
        publish.Parameters.Add("@SourceRefreshedUtc", SqlDbType.DateTime2).Value = state.RefreshedUtc;   // when the office made it
        publish.Parameters.Add("@SourceSnapshotsUtc", SqlDbType.DateTime2).Value =                         // and Bad Debt & Exposure
            withSnapshots && state.SnapshotsUtc is DateTime prepared ? prepared : DBNull.Value;
        try { await publish.ExecuteNonQueryAsync(ct); }
        catch (SqlException ex) { throw new PublishFailedException(ex, Explain(ex, Steps.Publishing, source, kpi), scope, startedUtc); }

        return new CopyOutcome(true, scope, copies.Count, rows, state.ReportDate, clock.Elapsed);
    }

    /// <summary>The longest any one step may take - a statement, a table's bulk copy, the publish - before it is stopped.</summary>
    public const int StepTimeoutSeconds = 600;

    /// <summary>A part of the copy, for <see cref="Explain"/>: what it was doing in each database, in words.</summary>
    public sealed record Step(string InOffice, string InSite, string During, bool OfficeByDefault, string Limit, bool Connecting = false);

    public static class Steps
    {
        private const string StepLimit = "10 minutes", CheckLimit = "30 seconds";   // StepTimeoutSeconds; SqlCommand's default
        public static readonly Step Copying = new("Reading from", "Copying into", "the copy", false, StepLimit);
        public static readonly Step Publishing = new("Reading from", "Publishing into", "publishing", false, StepLimit);
        public static readonly Step CheckingOffice = new("Checking", "Checking", "the check for new figures", true, CheckLimit);
        public static readonly Step CheckingSite = new("Checking", "Checking", "the check for new figures", false, CheckLimit);
        public static readonly Step ConnectingOffice = new("Connecting to", "Connecting to", "connecting", true, "", Connecting: true);
        public static readonly Step ConnectingSite = new("Connecting to", "Connecting to", "connecting", false, "", Connecting: true);
    }

    /// <summary>
    /// A copy failure in plain words that says WHICH database (29 Oct 2026, Steve: "which database?!"): the office's
    /// KPI_Export or the KPI site's own CMS_KPI - told apart by the connection the error came from - and whether it ran
    /// past the time limit (the database was slow: the connection was fine), lost its connection, or reported an error.
    /// </summary>
    public static string Explain(Exception ex, Step step, SqlConnection? source, SqlConnection? kpi)
    {
        var sql = ex as SqlException ?? ex.InnerException as SqlException;
        var office = sql is not null && kpi is not null && sql.ClientConnectionId == kpi.ClientConnectionId ? false
                   : sql is not null && source is not null && sql.ClientConnectionId == source.ClientConnectionId ? true
                   : step.OfficeByDefault;
        var conn = office ? source : kpi;
        var name = office ? OfficeName(conn) : SiteName(conn);
        var message = sql?.Message ?? ex.Message;

        if (step.Connecting) return $"Couldn't connect to {name}: {message}";
        if (sql?.Number == -2)   // the command's time limit
            return $"{(office ? step.InOffice : step.InSite)} {name} took longer than {step.Limit}, so the site stopped it. Nothing was published.";
        if (conn is not null && conn.State != ConnectionState.Open)
            return $"Lost the connection to {name} during {step.During}. Nothing was published.";
        return $"{char.ToUpperInvariant(name[0])}{name[1..]} reported an error during {step.During}: {message}";
    }

    private static string SiteName(SqlConnection? c) =>
        $"the KPI site's database ({(string.IsNullOrEmpty(c?.Database) ? "CMS_KPI" : c.Database)}" +
        $"{(c?.DataSource.Contains(".database.windows.net", StringComparison.OrdinalIgnoreCase) == true ? ", Azure" : "")})";

    private static string OfficeName(SqlConnection? c)
    {
        var db = string.IsNullOrEmpty(c?.Database) ? "KPI_Export" : c.Database;
        var host = c?.DataSource ?? "";
        var local = host.StartsWith('.') || host.StartsWith("(local)", StringComparison.OrdinalIgnoreCase)
                    || host.StartsWith("localhost", StringComparison.OrdinalIgnoreCase);
        return local ? $"{db} on this computer" : $"the office's {db} (PF-APP1)";
    }

    /// <summary>
    /// A failed copy in CMS_KPI's load log (Settings > Data sync). Never throws. <paramref name="closeOpenRun"/>: the site
    /// gave up on a PUBLISH that had already opened its run (<paramref name="startedUtc"/> = the one Stage.spPublish was
    /// given) - close that run with the reason rather than add another.
    /// </summary>
    public static async Task RecordFailureAsync(string kpiConnection, string scope, DateTime startedUtc, string error,
                                                DateTime? sourceRefreshedUtc = null, bool closeOpenRun = false)
    {
        try
        {
            await using var kpi = new SqlConnection(kpiConnection);
            await kpi.OpenAsync();
            await using var cmd = new SqlCommand("Stage.spRecordFailure", kpi) { CommandType = CommandType.StoredProcedure };
            cmd.Parameters.Add("@Scope", SqlDbType.VarChar, 10).Value = scope;
            cmd.Parameters.Add("@StartedUtc", SqlDbType.DateTime2).Value = startedUtc;
            cmd.Parameters.Add("@Error", SqlDbType.NVarChar, 2000).Value = error.Length > 2000 ? error[..2000] : error;
            cmd.Parameters.Add("@SourceRefreshedUtc", SqlDbType.DateTime2).Value = (object?)sourceRefreshedUtc ?? DBNull.Value;
            if (closeOpenRun) cmd.Parameters.Add("@CloseOpenRun", SqlDbType.Bit).Value = true;
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* CMS_KPI could not be reached either: the overdue check covers that */ }
    }
}

/// <summary>
/// A copy that could not go ahead although the office had new data (19 Oct 2026: another copy held the lock) - counted as
/// a failure, so Settings > Data sync shows a cross with the reason instead of nothing at all.
/// </summary>
public sealed class CopyBlockedException(string reason, DateTime sourceRefreshedUtc) : Exception($"Not copied: {reason}")
{
    public DateTime SourceRefreshedUtc { get; } = sourceRefreshedUtc;
}

/// <summary>
/// Stage.spPublish refused or failed. When it caught the error itself it has closed its run (Ref.LoadRun); when the site
/// stopped it (its time limit) or the connection dropped it couldn't, so the caller closes that run - found by
/// <see cref="Scope"/> and <see cref="StartedUtc"/> (KpiCopier.RecordFailureAsync, closeOpenRun).
/// </summary>
public sealed class PublishFailedException(SqlException inner, string message, string scope, DateTime startedUtc) : Exception(message, inner)
{
    public string Scope { get; } = scope;
    public DateTime StartedUtc { get; } = startedUtc;
}

/// <summary>A copy that failed before publishing, in words naming the database (<see cref="KpiCopier.Explain"/>).</summary>
public sealed class CopyFailedException(string message, Exception inner) : Exception(message, inner);
