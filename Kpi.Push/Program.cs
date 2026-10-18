using Kpi.Copy;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

// The KPI copy from a developer's machine:  Kpi.Push Frequent | Nightly [--azure] [--no-refresh]
// In production the SITE copies (Kpi.Web's KpiPull, every two minutes, from KPI_Export on the office server, refreshed
// there by SQL Server Agent). This runs the same steps (Kpi.Copy) against your own databases:
//   1. refreshes your local KPI_Export as the Agent jobs would: Nightly first prepares Bad Debt and Exposure
//      (--no-refresh copies what is there already)
//   2. copies KPI_Export's copy tables into CMS_KPI's Stage and publishes them
//   Source  your local KPI_Export (Windows sign-in, so it may refresh it)
//   CmsKpi  your local CMS_KPI, or with --azure CmsKpiAzure (the Azure database; Entra sign-in)
// Connection strings come from user-secrets (dotnet user-secrets set "ConnectionStrings:Source" "..." --project
// Kpi.Push) or environment variables (ConnectionStrings__Source), never from a committed file.

var nightly = args.Length > 0 && args[0].Equals("Nightly", StringComparison.OrdinalIgnoreCase);
var scope = nightly ? "Nightly" : "Frequent";
var kpiName = args.Contains("--azure") ? "CmsKpiAzure" : "CmsKpi";
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

var startedUtc = DateTime.UtcNow;
try
{
    await using var source = new SqlConnection(Required("Source"));
    await using var kpi = new SqlConnection(Required(kpiName));
    await source.OpenAsync();
    await kpi.OpenAsync();

    if (!args.Contains("--no-refresh"))
    {
        // as the office's Agent jobs do: nightly prepares Bad Debt and Exposure in CMS first (a few minutes)
        Console.WriteLine(nightly ? "Preparing Bad Debt and Exposure in CMS, then refreshing KPI_Export's copies..."
                                  : "Refreshing KPI_Export's copies...");
        await Exec(source, nightly ? "EXEC KpiCopy.spNightly" : "EXEC KpiCopy.spRefresh", 3600);
    }

    var state = await KpiCopier.ReadStateAsync(source)
        ?? throw new InvalidOperationException("KPI_Export has never been refreshed: run without --no-refresh.");
    var outcome = await KpiCopier.CopyAsync(source, kpi, state, withSnapshots: nightly, Console.WriteLine);

    if (!outcome.Published)
    {
        Console.WriteLine($"KPI copy ({scope}) not published: {outcome.Reason}.");
        return 0;
    }
    Console.WriteLine($"KPI copy ({scope}) published {outcome.Tables} tables, report date {outcome.ReportDate:dd/MM/yyyy}, " +
                      $"in {outcome.Elapsed.TotalSeconds:0.0} s.");
    return 0;
}
catch (Exception ex)
{
    // nothing was published: the site keeps the last good figures and, after 30 minutes, says they are overdue
    Console.Error.WriteLine($"KPI copy ({scope}) failed. Nothing was published.\n\n{ex}");
    if (ex is not PublishFailedException && config.GetConnectionString(kpiName) is { Length: > 0 } cs)
        await KpiCopier.RecordFailureAsync(cs, scope, startedUtc, ex.Message);
    return 1;
}

string Required(string name) =>
    config.GetConnectionString(name) is { Length: > 0 } cs ? cs
        : throw new InvalidOperationException(
            $"ConnectionStrings:{name} is not set. Set it once with:  dotnet user-secrets set \"ConnectionStrings:{name}\" \"...\" --project Kpi.Push");

static async Task Exec(SqlConnection c, string sql, int timeout)
{
    await using var cmd = new SqlCommand(sql, c) { CommandTimeout = timeout };
    await cmd.ExecuteNonQueryAsync();
}
