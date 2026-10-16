using System.Reflection;
using System.Text.RegularExpressions;
using Kpi.Copy;
using Microsoft.Data.SqlClient;

namespace Kpi.Tools;

// The KPI_Export database beside CMS (docs/kpi-copy-plan.html): the script is Kpi.Copy's
// (Kpi.Copy/Export/KPI_Export.sql, embedded here), so the tool and the copy always agree on its version.
//   export-apply --cms <CMS database>   create or bring KPI_Export up to date, its synonyms pointing at that database
//   export-check                        read only: the version, every synonym's target, every view still runs
// ConnectionStrings:Export names the SERVER (any database on it; KPI_Export is used by name).
public static partial class Export
{
    private const string Database = "KPI_Export";

    public static async Task<int> ApplyAsync(string cs, string? cmsDb)
    {
        if (cmsDb is null || !Regex.IsMatch(cmsDb, "^[A-Za-z0-9_]{1,128}$"))
        {
            Console.Error.WriteLine("Kpi.Tools export-apply --cms <CMS database, e.g. CMS_Live or CMS_Dev> [--remote]");
            return 1;
        }

        var script = ReadScript();
        var scriptVersion = ScriptVersion().Match(script) is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;
        if (scriptVersion != ExportVersion.Current)
            throw new InvalidOperationException(
                $"The script says version {scriptVersion} but ExportVersion.Current is {ExportVersion.Current}: raise both together.");

        // the one line that names the CMS database
        const string line = "DECLARE @CmsDb sysname = N'CMS_Dev';";
        if (script.Split(line).Length != 2)
            throw new InvalidOperationException($"The script must contain exactly one \"{line}\".");
        script = script.Replace(line, $"DECLARE @CmsDb sysname = N'{cmsDb}';");

        var batches = GoLine().Split(script).Where(b => !string.IsNullOrWhiteSpace(b)).ToList();

        await using var conn = new SqlConnection(cs);
        conn.InfoMessage += (_, e) => Console.WriteLine($"  {e.Message}");   // the script's PRINTs: the Agent jobs made or kept
        await conn.OpenAsync();
        Console.WriteLine($"{conn.DataSource}: applying {Database} version {ExportVersion.Current}, reading {cmsDb} ({batches.Count} batches)...");
        for (var i = 0; i < batches.Count; i++)
        {
            await using var cmd = new SqlCommand(batches[i], conn) { CommandTimeout = 600 };
            try { await cmd.ExecuteNonQueryAsync(); }
            catch (SqlException ex)
            {
                var first = batches[i].Trim().Split('\n').FirstOrDefault(l => !l.TrimStart().StartsWith("--"))?.Trim();
                throw new InvalidOperationException($"Batch {i + 1} of {batches.Count} failed ({first}): {ex.Message}", ex);
            }
        }
        Console.WriteLine("Done. Check it with:  Kpi.Tools export-check");
        return 0;
    }

    public static async Task<int> CheckAsync(string cs)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        if (await Scalar<int?>(conn, $"SELECT CAST(DB_ID(N'{Database}') AS int)") is null)
        {
            Console.Error.WriteLine($"{conn.DataSource}: there is no {Database} database. Create it with:  Kpi.Tools export-apply --cms <CMS database>");
            return 1;
        }
        await conn.ChangeDatabaseAsync(Database);
        var problems = new List<string>();

        var version = await Scalar<int>(conn,
            "IF OBJECT_ID(N'KpiExport.Version') IS NULL SELECT 0 ELSE SELECT ISNULL(MAX(Version), 0) FROM KpiExport.Version");
        if (version != ExportVersion.Current)
            problems.Add($"version {version}, but this code needs {ExportVersion.Current}: run export-apply");

        // every synonym must still point at something in CMS
        var synonyms = await Rows(conn, "SELECT SCHEMA_NAME(schema_id) + N'.' + name, base_object_name, " +
                                        "CASE WHEN OBJECT_ID(base_object_name) IS NULL THEN 0 ELSE 1 END FROM sys.synonyms ORDER BY 1");
        var cmsDbs = synonyms.Select(s => s[1].Split('.')[0].Trim('[', ']')).Distinct().ToList();
        problems.AddRange(synonyms.Where(s => s[2] == "0").Select(s => $"synonym {s[0]} points at {s[1]}, which doesn't exist"));

        // every view must still run (a column renamed or dropped in CMS shows up here)
        var views = await Rows(conn, "SELECT QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) FROM sys.views ORDER BY 1");
        foreach (var v in views.Select(r => r[0]))
        {
            try { await using var cmd = new SqlCommand($"SELECT TOP (0) * FROM {v}", conn) { CommandTimeout = 120 }; await cmd.ExecuteNonQueryAsync(); }
            catch (SqlException ex) { problems.Add($"view {v}: {ex.Message}"); }
        }

        Console.WriteLine($"{conn.DataSource} / {Database}: version {version}, reading {string.Join(", ", cmsDbs)}; " +
                          $"{synonyms.Count} synonyms, {views.Count} views.");
        if (problems.Count == 0) { Console.WriteLine("All good."); return 0; }
        problems.ForEach(p => Console.Error.WriteLine($"  {p}"));
        return 1;
    }

    private static string ReadScript()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("KPI_Export.sql")
            ?? throw new InvalidOperationException("KPI_Export.sql is not embedded in Kpi.Tools.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task<T> Scalar<T>(SqlConnection c, string sql)
    {
        await using var cmd = new SqlCommand(sql, c);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default! : (T)value;
    }

    private static async Task<List<string[]>> Rows(SqlConnection c, string sql)
    {
        await using var cmd = new SqlCommand(sql, c);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<string[]>();
        while (await r.ReadAsync())
            rows.Add(Enumerable.Range(0, r.FieldCount).Select(i => Convert.ToString(r.GetValue(i)) ?? "").ToArray());
        return rows;
    }

    // a line holding only GO ends a batch, as in SSMS and sqlcmd
    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();

    [GeneratedRegex(@"INSERT\s+KpiExport\.Version\s*\(Version,\s*AppliedUtc\)\s*VALUES\s*\((\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptVersion();
}
