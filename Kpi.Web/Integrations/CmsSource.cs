using System.Data;
using System.Diagnostics;
using Kpi.Copy;
using Kpi.Data.Cfg;
using Microsoft.Data.SqlClient;

namespace Kpi.Web.Integrations;

/// <summary>The data source's plain settings: where KPI_Export is and who signs in (never the password).</summary>
public sealed record CmsSourceSettings(string Host, int Port, string Database, string UserName, bool Encrypt, bool TrustServerCertificate,
                                       string? Name = null)   // what the card calls it: "SQL data source (CMS)" (19 Oct 2026, Steve)
{
    /// <summary>The card's title: "SQL data source", with its name in brackets when it has one.</summary>
    public static string Title(CmsSourceSettings? s) =>
        string.IsNullOrWhiteSpace(s?.Name) ? "SQL data source" : $"SQL data source ({s!.Name!.Trim()})";
}

/// <summary>The data source's secret: the SQL login's password. Encrypted at rest; read only to connect.</summary>
public sealed record CmsSourceSecrets(string Password);

/// <summary>A test query's answer: the column names, the first rows (as text), and whether there were more.</summary>
public sealed record SourceQueryResult(IReadOnlyList<string> Columns, IReadOnlyList<string?[]> Rows, bool MoreRows, TimeSpan Elapsed);

/// <summary>
/// Where the KPI copy reads from (Oct 2026, Steve: set in Settings > Integrations like Klipfolio's SQL data source):
/// KPI_Export on the office SQL Server, reached through the port forward that admits only the site's static IP, as a SQL
/// login in kpi_pull - it reads KPI_Export's copies and nothing in CMS. Settings and the encrypted password live in
/// Cfg.Integration (provider CmsSource). With no data source connected, ConnectionStrings:KpiExport is used instead (a
/// developer's machine), and with neither the copy is off.
/// </summary>
public sealed class CmsSource(IntegrationStore store, IConfiguration config)
{
    public const int MaxRows = 100;
    public const int QueryTimeoutSeconds = 30;

    public static string ConnectionString(CmsSourceSettings s, string password) => new SqlConnectionStringBuilder
    {
        DataSource = $"tcp:{s.Host},{s.Port}",
        InitialCatalog = s.Database,
        UserID = s.UserName,
        Password = password,
        Encrypt = s.Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
        TrustServerCertificate = s.TrustServerCertificate,
        ConnectTimeout = 30,
        ApplicationName = "KPI site"
    }.ConnectionString;

    /// <summary>The connection the copy uses now, or null when the copy is off (not connected, or switched off).</summary>
    public async Task<string?> CopyConnectionAsync()
    {
        var row = await store.GetAsync(IntegrationProviders.CmsSource);
        if (row.Status != IntegrationStatus.NotConnected)
        {
            if (!row.Enabled) return null;
            var settings = IntegrationStore.ReadSettings<CmsSourceSettings>(row);
            var secrets = store.ReadSecrets<CmsSourceSecrets>(row);
            return settings is null || secrets is null ? null : ConnectionString(settings, secrets.Password);
        }
        return config.GetConnectionString("KpiExport") is { Length: > 0 } cs ? cs : null;
    }

    /// <summary>Signs in and reads KPI_Export's version and refresh time. Throws with a readable message when it can't.</summary>
    public static async Task<string> TestAsync(string connection)
    {
        var clock = Stopwatch.StartNew();
        await using var conn = new SqlConnection(connection);
        await conn.OpenAsync();
        var state = await KpiCopier.ReadStateAsync(conn);   // also refuses another KPI_Export version
        return state is null
            ? $"Connected in {clock.Elapsed.TotalSeconds:0.0} s. KPI_Export version {ExportVersion.Current}, not refreshed yet (the office's Agent job fills it)."
            : $"Connected in {clock.Elapsed.TotalSeconds:0.0} s. KPI_Export version {ExportVersion.Current}, refreshed {Kpi.Web.Data.UkTime.Show(state.RefreshedUtc)}.";
    }

    /// <summary>
    /// A test query, as the copy's own login (so it sees only what the copy can), inside a transaction that is always
    /// rolled back: nothing it does can stay. The first <see cref="MaxRows"/> rows, <see cref="QueryTimeoutSeconds"/> s.
    /// </summary>
    public static async Task<SourceQueryResult> RunAsync(string connection, string sql)
    {
        var clock = Stopwatch.StartNew();
        await using var conn = new SqlConnection(connection);
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        try
        {
            await using var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = QueryTimeoutSeconds };
            await using var r = await cmd.ExecuteReaderAsync();
            var columns = Enumerable.Range(0, r.FieldCount).Select(i => r.GetName(i) is { Length: > 0 } n ? n : $"(column {i + 1})").ToList();
            var rows = new List<string?[]>();
            var more = false;
            while (await r.ReadAsync())
            {
                if (rows.Count == MaxRows) { more = true; break; }
                rows.Add(Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? null : Show(r.GetValue(i))).ToArray());
            }
            return new SourceQueryResult(columns, rows, more, clock.Elapsed);
        }
        finally
        {
            try { await tx.RollbackAsync(); } catch (InvalidOperationException) { /* already ended by the server */ }
        }
    }

    private static string Show(object value) => value switch
    {
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd") : d.ToString("yyyy-MM-dd HH:mm:ss"),
        decimal m => m.ToString("#,##0.##########"),
        byte[] b => $"({b.Length} bytes)",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""
    };
}
