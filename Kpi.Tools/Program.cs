using Kpi.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

// The KPI database's command line. Run by a person, never by an app:
//   dotnet run --project Kpi.Tools -- migrate [--remote]   apply CMS_KPI's migrations to ConnectionStrings:CmsKpi
//   dotnet run --project Kpi.Tools -- seed --email <a> --name "<n>"   the first System Admin of a new database
//   dotnet run --project Kpi.Tools -- compare [report]     every report (or those whose name contains [report]): as CMS
//                                                          produces it (ConnectionStrings:Cms) against Rpt (CmsKpi)
// Connection strings come from user-secrets (dotnet user-secrets set "ConnectionStrings:CmsKpi" "..." --project Kpi.Tools)
// or environment variables (ConnectionStrings__CmsKpi), never from a committed file.

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

try
{
    return args.FirstOrDefault()?.ToLowerInvariant() switch
    {
        "migrate" => await Migrate(),
        "seed" => await Kpi.Tools.Seed.RunAsync(Required("CmsKpi"), args),
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
    var cs = Required("CmsKpi");
    var target = new SqlConnectionStringBuilder(cs);
    var server = target.DataSource;
    var local = server.StartsWith('.') || server.StartsWith("(local)", StringComparison.OrdinalIgnoreCase)
             || server.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
             || server.StartsWith(Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    // a database on another server (Azure) is changed only when asked for explicitly
    if (!local && !args.Contains("--remote"))
    {
        Console.Error.WriteLine($"{server} / {target.InitialCatalog} is not on this machine. Add --remote to migrate it.");
        return 1;
    }

    await using var db = new KpiDbContext(KpiDbContext.Options(cs));
    var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
    Console.WriteLine($"{server} / {target.InitialCatalog}: {pending.Count} migration(s) to apply.");
    pending.ForEach(m => Console.WriteLine($"  {m}"));
    if (pending.Count == 0) return 0;

    await db.Database.MigrateAsync();                     // creates the database first if it does not exist
    Console.WriteLine("Done.");
    return 0;
}

string Required(string name) =>
    config.GetConnectionString(name) is { Length: > 0 } cs ? cs
        : throw new InvalidOperationException(
            $"ConnectionStrings:{name} is not set. Set it once with:  dotnet user-secrets set \"ConnectionStrings:{name}\" \"...\" --project Kpi.Tools");

static int Usage()
{
    Console.WriteLine("Kpi.Tools  migrate [--remote] | seed --email <address> --name \"<name>\" [--remote] | compare [report]");
    return 1;
}
