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
//   dotnet run --project Kpi.Tools -- grant-push --app <name> --app-id <guid> [--azure]
//                                                                 an Entra identity (the site's, which copies) into kpi_push
//   dotnet run --project Kpi.Tools -- grant-pull --user <name> [--remote]   the site's SQL login on the office server, reading
//                                                                 KPI_Export's copies only; a NEW password, shown once
//   dotnet run --project Kpi.Tools -- export-apply --cms <CMS db> [--remote]   create / update KPI_Export beside CMS
//   dotnet run --project Kpi.Tools -- export-check [--remote]     read only: KPI_Export's version, synonyms and views
//                                                                 (ConnectionStrings:Export names that server)
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
        "grant-site" => await Grant("kpi_site", "web app name", "its managed identity's application id"),
        "grant-push" => await Grant("kpi_push", "web app or app registration name", "its application (client) id"),
        "grant-pull" => RemoteAllowed(Required("Export")) ? await GrantPull() : 1,
        "export-apply" => RemoteAllowed(Required("Export")) ? await Kpi.Tools.Export.ApplyAsync(Required("Export"), Arg("--cms")) : 1,
        "export-check" => RemoteAllowed(Required("Export"), readOnly: true) ? await Kpi.Tools.Export.CheckAsync(Required("Export")) : 1,
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

string? Arg(string n) => args.SkipWhile(a => a != n).Skip(1).FirstOrDefault();

// An Entra identity becomes a database user in a role: grant-site, the web app's managed identity in kpi_site (run the
// reports, its own Auth and Cfg tables); grant-push, the copy's app registration in kpi_push (fill Stage, publish).
// Created by its application id rather than by name, so no directory lookup is needed - which a guest account may not
// be allowed. Safe to run again.
async Task<int> Grant(string role, string what, string idWhat)
{
    var name = Arg("--app");
    if (name is null || !Regex.IsMatch(name, "^[A-Za-z0-9._-]{1,60}$") || !Guid.TryParse(Arg("--app-id"), out var appId))
    {
        Console.Error.WriteLine($"Kpi.Tools {args[0]} --app <{what}> --app-id <{idWhat}> [--azure]");
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
        $"ALTER ROLE {role} ADD MEMBER [{name}];", conn);
    await cmd.ExecuteNonQueryAsync();
    Console.WriteLine($"{conn.DataSource} / {conn.Database}: {name} is a user in {role}.");
    return 0;
}

// The KPI site's login on the office SQL Server: a SQL login (the site is in Azure, outside the office's Windows domain),
// whose user in KPI_Export is in kpi_pull - it reads the copies, the snapshots and the version, nothing else, and it has
// no user in CMS. Every run sets a NEW random password and shows it once (the old one stops working at once), so a lost
// or leaked one is replaced by running this again. Run export-apply first: it makes the role.
async Task<int> GrantPull()
{
    var name = Arg("--user");
    if (name is null || !Regex.IsMatch(name, "^[A-Za-z0-9._-]{1,60}$"))
    {
        Console.Error.WriteLine("Kpi.Tools grant-pull --user <name, e.g. kpi_site_azure> [--remote]");
        return 1;
    }
    var password = NewPassword();

    await using var conn = new SqlConnection(Required("Export"));
    await conn.OpenAsync();
    await using (var login = new SqlCommand(
        $"IF SUSER_ID(N'{name}') IS NULL " +
        $"CREATE LOGIN [{name}] WITH PASSWORD = N'{password}', DEFAULT_DATABASE = KPI_Export, CHECK_POLICY = ON, CHECK_EXPIRATION = OFF " +
        $"ELSE ALTER LOGIN [{name}] WITH PASSWORD = N'{password}' UNLOCK;", conn))
        await login.ExecuteNonQueryAsync();

    await conn.ChangeDatabaseAsync("KPI_Export");
    await using (var user = new SqlCommand(
        "IF DATABASE_PRINCIPAL_ID(N'kpi_pull') IS NULL THROW 50001, 'KPI_Export has no kpi_pull role: run export-apply first.', 1; " +
        $"IF USER_ID(N'{name}') IS NULL CREATE USER [{name}] FOR LOGIN [{name}]; " +
        $"ALTER ROLE kpi_pull ADD MEMBER [{name}];", conn))
        await user.ExecuteNonQueryAsync();

    Console.WriteLine($"{conn.DataSource}: {name} can read KPI_Export's copies. Its password - shown ONCE, not saved anywhere:");
    Console.WriteLine();
    Console.WriteLine($"    {password}");
    Console.WriteLine();
    Console.WriteLine("Put it in the site's KpiExport connection string (the runbook's Azure step). Any previous password has stopped working.");
    return 0;
}

// 40 characters of every kind (SQL Server's password policy), none that needs quoting in SQL or a connection string
static string NewPassword()
{
    const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnopqrstuvwxyz", digits = "23456789", symbols = "!#*+-?@^_~";
    var all = upper + lower + digits + symbols;
    var chars = new[] { upper, lower, digits, symbols }
        .Select(set => set[System.Security.Cryptography.RandomNumberGenerator.GetInt32(set.Length)])
        .Concat(Enumerable.Range(0, 36).Select(_ => all[System.Security.Cryptography.RandomNumberGenerator.GetInt32(all.Length)]))
        .ToArray();
    System.Security.Cryptography.RandomNumberGenerator.Shuffle(chars.AsSpan());
    return new string(chars);
}

// another server is changed only when asked for (--azure / --remote); a read-only check may look anywhere
bool RemoteAllowed(string cs, bool readOnly = false)
{
    var target = new SqlConnectionStringBuilder(cs);
    var server = target.DataSource;
    var local = server.StartsWith('.') || server.StartsWith("(local)", StringComparison.OrdinalIgnoreCase)
             || server.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
             || server.StartsWith(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    if (local || readOnly || azure || args.Contains("--remote")) return true;
    Console.Error.WriteLine($"{server} / {target.InitialCatalog} is not on this machine. Add --azure (or --remote) to change it.");
    return false;
}

string Required(string name) =>
    config.GetConnectionString(name) is { Length: > 0 } cs ? cs
        : throw new InvalidOperationException(
            $"ConnectionStrings:{name} is not set. Set it once with:  dotnet user-secrets set \"ConnectionStrings:{name}\" \"...\" --project Kpi.Tools");

static int Usage()
{
    Console.WriteLine("Kpi.Tools  migrate [--azure] | seed --email <a> --name \"<n>\" [--azure] | reset-sign-in --email <a> [--azure] | grant-site|grant-push --app <name> --app-id <guid> [--azure] | grant-pull --user <name> [--remote] | export-apply --cms <db> [--remote] | export-check | compare [report]");
    return 1;
}
