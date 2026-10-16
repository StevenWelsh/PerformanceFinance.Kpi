using System.Text.RegularExpressions;
using Kpi.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

// The KPI database's command line. Run by a person, never by an app:
//   dotnet run --project Kpi.Tools -- migrate [--azure]           apply CMS_KPI's migrations
//   dotnet run --project Kpi.Tools -- seed --email <a> --name "<n>" [--azure]   the first System Admin of a new database
//   dotnet run --project Kpi.Tools -- reset-sign-in --email <a> [--azure]   back to first sign-in: register a mobile
//   dotnet run --project Kpi.Tools -- grant-site --app <name> --app-id <guid> [--azure]
//                                                                 let a web app's managed identity in, as kpi_site
//   dotnet run --project Kpi.Tools -- compare [report]            every report (or those whose name contains [report]): as
//                                                                 CMS produces it (ConnectionStrings:Cms) against Rpt (CmsKpi)
// Which KPI database: ConnectionStrings:CmsKpi (your local one), or with --azure ConnectionStrings:CmsKpiAzure (Entra
// sign-in: Authentication=Active Directory Interactive). Connection strings come from user-secrets
// (dotnet user-secrets set "ConnectionStrings:CmsKpi" "..." --project Kpi.Tools) or environment variables, never from a
// committed file. A database on another server is only changed with --azure (or --remote), asked for explicitly.

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

var azure = args.Contains("--azure");
var kpiName = azure ? "CmsKpiAzure" : "CmsKpi";

try
{
    return args.FirstOrDefault()?.ToLowerInvariant() switch
    {
        "migrate" => await Migrate(),
        "seed" => RemoteAllowed(Required(kpiName)) ? await Kpi.Tools.Seed.RunAsync(Required(kpiName), args.Append("--remote").ToArray()) : 1,
        "grant-site" => await GrantSite(),
        "reset-sign-in" => RemoteAllowed(Required(kpiName)) ? await Kpi.Tools.ResetSignIn.RunAsync(Required(kpiName), args) : 1,
        "compare" => await Kpi.Tools.Compare.RunAsync(Required("Cms"), Required("CmsKpi"), config["CmsRepo"], args.ElementAtOrDefault(1)),
        _ => Usage()
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

async Task<int> Migrate()
{
    var cs = Required(kpiName);
    if (!RemoteAllowed(cs)) return 1;
    var target = new SqlConnectionStringBuilder(cs);

    await using var db = new KpiDbContext(KpiDbContext.Options(cs));
    var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
    Console.WriteLine($"{target.DataSource} / {target.InitialCatalog}: {pending.Count} migration(s) to apply.");
    pending.ForEach(m => Console.WriteLine($"  {m}"));
    if (pending.Count == 0) return 0;

    await db.Database.MigrateAsync();                     // creates the database first if it does not exist
    Console.WriteLine("Done.");
    return 0;
}

// A web app's managed identity becomes a database user in kpi_site (run the reports, its own Auth and Cfg tables).
// Created by its application id rather than by name, so no directory lookup is needed - which a guest account may not
// be allowed. Safe to run again.
async Task<int> GrantSite()
{
    string? Arg(string n) => args.SkipWhile(a => a != n).Skip(1).FirstOrDefault();
    var name = Arg("--app");
    if (name is null || !Regex.IsMatch(name, "^[A-Za-z0-9-]{1,60}$") || !Guid.TryParse(Arg("--app-id"), out var appId))
    {
        Console.Error.WriteLine("Kpi.Tools grant-site --app <web app name> --app-id <its managed identity's application id> [--azure]");
        return 1;
    }
    var cs = Required(kpiName);
    if (!RemoteAllowed(cs)) return 1;

    // a uniqueidentifier's bytes in SQL Server's order are .NET's ToByteArray order
    var sid = "0x" + Convert.ToHexString(appId.ToByteArray());
    await using var conn = new SqlConnection(cs);
    await conn.OpenAsync();
    await using var cmd = new SqlCommand(
        $"IF USER_ID(N'{name}') IS NULL CREATE USER [{name}] WITH SID = {sid}, TYPE = E; " +
        $"ALTER ROLE kpi_site ADD MEMBER [{name}];", conn);
    await cmd.ExecuteNonQueryAsync();
    Console.WriteLine($"{conn.DataSource} / {conn.Database}: {name} is a user in kpi_site.");
    return 0;
}

bool RemoteAllowed(string cs)
{
    var target = new SqlConnectionStringBuilder(cs);
    var server = target.DataSource;
    var local = server.StartsWith('.') || server.StartsWith("(local)", StringComparison.OrdinalIgnoreCase)
             || server.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
             || server.StartsWith(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    if (local || azure || args.Contains("--remote")) return true;
    Console.Error.WriteLine($"{server} / {target.InitialCatalog} is not on this machine. Add --azure to change it.");
    return false;
}

string Required(string name) =>
    config.GetConnectionString(name) is { Length: > 0 } cs ? cs
        : throw new InvalidOperationException(
            $"ConnectionStrings:{name} is not set. Set it once with:  dotnet user-secrets set \"ConnectionStrings:{name}\" \"...\" --project Kpi.Tools");

static int Usage()
{
    Console.WriteLine("Kpi.Tools  migrate [--azure] | seed --email <a> --name \"<n>\" [--azure] | reset-sign-in --email <a> [--azure] | grant-site --app <name> --app-id <guid> [--azure] | compare [report]");
    return 1;
}
