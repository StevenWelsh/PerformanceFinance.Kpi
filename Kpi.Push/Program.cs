using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

// The KPI copy, one way:  Kpi.Push Frequent | Nightly
//   Source  the KPI_Export database beside CMS (Export/KPI_Export.sql, applied by Kpi.Tools export-apply); it must be at
//           ExportVersion.Current or nothing is copied
//   CmsKpi  the KPI database, as a login that can only fill Stage and run Stage.spPublish
// Connection strings come from user-secrets (dotnet user-secrets set "ConnectionStrings:Source" "..." --project
// Kpi.Push) or environment variables (ConnectionStrings__Source), never from a committed file.

var scope = args.Length > 0 && args[0].Equals("Nightly", StringComparison.OrdinalIgnoreCase) ? "Nightly" : "Frequent";
// --azure: copy into the Azure KPI database (ConnectionStrings:CmsKpiAzure) - from a developer's machine while testing;
// the office server's scheduled copy is configured with CmsKpi pointing at Azure.
var kpiName = args.Contains("--azure") ? "CmsKpiAzure" : "CmsKpi";
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

// what is copied: export view at the source -> staging table in CMS_KPI
var copies = new List<(string From, string To)>
{
    ("KpiExport.vSalesOffice",   "Stage.SalesOffice"),
    ("KpiExport.vSalesperson",   "Stage.Salesperson"),
    ("KpiExport.vPeriod",        "Stage.Period"),
    ("KpiExport.vWorkingDay",    "Stage.WorkingDay"),
    ("KpiExport.vSalesTarget",   "Stage.SalesTarget"),
    ("KpiExport.vFundLender",    "Stage.FundLender"),
    ("KpiExport.vFund",          "Stage.Fund"),
    ("KpiExport.vLead",          "Stage.Lead"),
    ("KpiExport.vLeadPipeline",  "Stage.LeadPipeline"),
    ("KpiExport.vSupplierYear",  "Stage.SupplierYear"),
    ("KpiExport.vBookPosition",  "Stage.BookPosition"),
    ("KpiExport.vFundingLine",   "Stage.FundingLine"),
};
if (scope == "Nightly")
{
    copies.Add(("KpiExport.BadDebtSnapshot",  "Stage.BadDebtYear"));
    copies.Add(("KpiExport.ExposureSnapshot", "Stage.Exposure"));
}

var clock = Stopwatch.StartNew();
var startedUtc = DateTime.UtcNow;
var publishing = false;      // Stage.spPublish logs its own failure in Ref.LoadRun
try
{
    await using var source = new SqlConnection(Required("Source"));
    await using var kpi    = new SqlConnection(Required(kpiName));
    await source.OpenAsync();
    await kpi.OpenAsync();

    // 1. one push at a time: a second copy started while this one runs gives up at once
    await using (var lockCmd = new SqlCommand(
        "DECLARE @r int; EXEC @r = sp_getapplock @Resource = 'KpiPush', @LockMode = 'Exclusive', " +
        "@LockOwner = 'Session', @LockTimeout = 0; SELECT @r;", kpi))
    {
        if ((int)(await lockCmd.ExecuteScalarAsync())! < 0)
        {
            Log("Another KPI push was still running, so this one was skipped.", EventLogEntryType.Warning);
            return 0;                                            // not a failure: the running push will publish
        }
    }                                                            // released when the connection closes

    // 1b. only from the KPI_Export this build was written for: an older script on the server could feed the wrong shape
    var version = (int)(await Scalar(source,
        "IF OBJECT_ID(N'KpiExport.Version') IS NULL SELECT 0 ELSE SELECT ISNULL(MAX(Version), 0) FROM KpiExport.Version"))!;
    if (version != Kpi.Push.ExportVersion.Current)
        throw new InvalidOperationException(
            $"KPI_Export is at version {version}; this Kpi.Push needs version {Kpi.Push.ExportVersion.Current}. " +
            "Apply the script that came with it:  Kpi.Tools export-apply --cms <CMS database>");

    // 2. nightly: refresh the Bad Debt and Exposure snapshots at the source first (a few minutes)
    if (scope == "Nightly")
    {
        Console.WriteLine("Preparing the Bad Debt and Exposure snapshots...");
        await Exec(source, "EXEC KpiExport.spPrepareSnapshots", timeout: 3600);
    }

    // 3. read every view as at ONE moment when the source allows snapshot reads; otherwise a few seconds apart
    await using var snapshot = config.GetValue<bool>("SnapshotReads")
        ? source.BeginTransaction(IsolationLevel.Snapshot) : null;

    var reportDate = (DateTime)(await Scalar(source, "SELECT KpiExport.fnReportDate()", snapshot))!;

    // 4. fill the staging tables: the site never reads them, so a push that stops here changes nothing
    foreach (var (from, to) in copies)
    {
        await Exec(kpi, $"DELETE {to}");

        await using var cmd    = new SqlCommand($"SELECT * FROM {from}", source, snapshot) { CommandTimeout = 600 };
        await using var reader = await cmd.ExecuteReaderAsync();
        using var bulk = new SqlBulkCopy(kpi, SqlBulkCopyOptions.TableLock, null)
            { DestinationTableName = to, BulkCopyTimeout = 600 };
        for (var i = 0; i < reader.FieldCount; i++)
            bulk.ColumnMappings.Add(reader.GetName(i), reader.GetName(i));   // by name, never by position
        await bulk.WriteToServerAsync(reader);
        Console.WriteLine($"  {to,-20} {bulk.RowsCopied,8:N0} rows");
    }

    // 5. everything arrived: swap it in, in one transaction on the CMS_KPI side
    publishing = true;
    await using var publish = new SqlCommand("Stage.spPublish", kpi)
        { CommandType = CommandType.StoredProcedure, CommandTimeout = 600 };
    publish.Parameters.Add("@Scope", SqlDbType.VarChar, 10).Value = scope;
    publish.Parameters.Add("@ReportDate", SqlDbType.Date).Value = reportDate.Date;
    await publish.ExecuteNonQueryAsync();

    Console.WriteLine($"KPI push ({scope}) published {copies.Count} tables, report date {reportDate:dd/MM/yyyy}, " +
                      $"in {clock.Elapsed.TotalSeconds:0.0} s.");
    return 0;
}
catch (Exception ex)
{
    // nothing was published: the site keeps the last good figures and, after 30 minutes, says they are stale
    Log($"KPI push ({scope}) failed. Nothing was published; the site keeps the last good figures.\n\n{ex}",
        EventLogEntryType.Error);
    // and in the KPI database's load log, so Settings > Data sync shows it - when it can still be reached
    if (!publishing) await RecordFailure(ex.Message);
    return 1;                                                    // the scheduler records the failure too
}

async Task RecordFailure(string error)
{
    try
    {
        await using var kpi = new SqlConnection(Required(kpiName));
        await kpi.OpenAsync();
        await using var cmd = new SqlCommand("Stage.spRecordFailure", kpi) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.Add("@Scope", SqlDbType.VarChar, 10).Value = scope;
        cmd.Parameters.Add("@StartedUtc", SqlDbType.DateTime2).Value = startedUtc;
        cmd.Parameters.Add("@Error", SqlDbType.NVarChar, 2000).Value = error.Length > 2000 ? error[..2000] : error;
        await cmd.ExecuteNonQueryAsync();
    }
    catch { /* the database could not be reached either: the stale-figures check covers that */ }
}

string Required(string name) =>
    config.GetConnectionString(name) is { Length: > 0 } cs ? cs
        : throw new InvalidOperationException(
            $"ConnectionStrings:{name} is not set. Set it once with:  dotnet user-secrets set \"ConnectionStrings:{name}\" \"...\" --project Kpi.Push");

static async Task Exec(SqlConnection c, string sql, int timeout = 600)
{
    await using var cmd = new SqlCommand(sql, c) { CommandTimeout = timeout };
    await cmd.ExecuteNonQueryAsync();
}

static async Task<object?> Scalar(SqlConnection c, string sql, SqlTransaction? tx = null)
{
    await using var cmd = new SqlCommand(sql, c, tx);
    return await cmd.ExecuteScalarAsync();
}

// Windows Application log, source Kpi.Push, so failures sit with the server's other errors. Register the source
// once per machine (admin PowerShell): New-EventLog -LogName Application -Source "Kpi.Push"
static void Log(string message, EventLogEntryType type)
{
    Console.Error.WriteLine(message);
    try { EventLog.WriteEntry("Kpi.Push", message, type); }
    catch { /* never fail the push over logging (e.g. the source is not registered yet) */ }
}
