using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Kpi.Web.Data;

/// <summary>
/// Opens one short-lived connection to the KPI database per operation - the KPI site's equivalent of CMS.Web's
/// CmsSessionFactory, kept in the same shape (<c>using var cms = await SessionFactory.BeginAsync(); ... cms.Db</c>)
/// so the KPI pages here and in CMS.Web stay easy to compare.
/// </summary>
public sealed class KpiSessionFactory(IConfiguration config)
{
    private readonly string _connectionString = config.GetConnectionString("CmsKpi") is { Length: > 0 } cs ? cs
        : throw new InvalidOperationException(
            "ConnectionStrings:CmsKpi is not set. Set it once with:  dotnet user-secrets set \"ConnectionStrings:CmsKpi\" \"...\" --project Kpi.Web");

    public Task<KpiSession> BeginAsync() => Task.FromResult(new KpiSession(new KpiDb(_connectionString)));

    /// <summary>For singletons that need one answer outside a page (the report date).</summary>
    public KpiDb Create() => new(_connectionString);
}

public sealed class KpiSession(KpiDb db) : IDisposable
{
    public KpiDb Db { get; } = db;
    public void Dispose() => Db.Dispose();
}

/// <summary>
/// One connection to the KPI database. The site may only run the report procedures (schema Rpt), so that is all
/// this offers.
/// </summary>
public sealed class KpiDb : IDisposable
{
    private readonly SqlConnection _connection;

    internal KpiDb(string connectionString) => _connection = new SqlConnection(connectionString);

    /// <summary>A query not finished by now is given up (KpiCache.LoadTimeout, the outer limit, is longer).</summary>
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(50);

    /// <summary>
    /// At most this many report queries at once, site-wide (18 Oct 2026). One phone view of Salesperson Summary started
    /// ~15 at the same moment, each wanting its own NEW connection - slow to make from App Service (8-32 s: TLS + the
    /// managed identity) and made largely one after another, so the last ones gave up ("Timeout expired ... obtaining a
    /// connection from the pool"). Queued here instead, they reuse the few connections already open, and the waiting
    /// counts towards <see cref="QueryTimeout"/>.
    /// </summary>
    private static readonly SemaphoreSlim Concurrent = new(6, 6);

    /// <summary>
    /// Runs a report procedure; its columns fill <typeparamref name="T"/> by name. Synchronous for the copied pages, but
    /// the work runs on the thread pool and is time-limited: a call that hangs reaching the database (16 Oct 2026: the
    /// whole site, for hours) fails with a message instead of freezing the page that made it.
    /// </summary>
    public List<T> Report<T>(string procedure, object? args = null)
    {
        var started = DateTime.UtcNow;
        var run = Task.Run(async () =>
        {
            if (!await Concurrent.WaitAsync(QueryTimeout)) throw new TimeoutException(Kpis.KpiCache.TimeoutMessage);
            try
            {
                // what is left of the minute for the query itself, at least 10 s
                var left = Math.Max(10, (int)(QueryTimeout - (DateTime.UtcNow - started)).TotalSeconds - 5);
                return Open().Query<T>(procedure, args, commandType: CommandType.StoredProcedure, commandTimeout: left).ToList();
            }
            finally { Concurrent.Release(); }
        });
        try { return run.WaitAsync(QueryTimeout).GetAwaiter().GetResult(); }
        catch (TimeoutException) { throw new TimeoutException(Kpis.KpiCache.TimeoutMessage); }
    }

    /// <summary>The business "as at" date of the figures, from the last copy.</summary>
    public DateTime ReportDate() => Report<DataStatusRow>("Rpt.spDataStatus").Single().ReportDate;

    /// <summary>A filter list: "Divisions" | "Salespeople" | "Arrangements" | "Years" | "Offices".</summary>
    public List<OptionRow> Options(string list) => Report<OptionRow>("Rpt.spFilterOptions", new { List = list });

    /// <summary>The health check's round trip (/health/db): opens a connection and asks for one row.</summary>
    public bool Ping()
    {
        var run = Task.Run(() => Open().ExecuteScalar<int>("SELECT 1", commandTimeout: 15) == 1);
        return run.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    // A connection Azure SQL cut off while its first handshake was slow is tried again (Kpi.Data.KpiRetryStrategy says why).
    private SqlConnection Open()
    {
        for (var attempt = 1; _connection.State != ConnectionState.Open; attempt++)
        {
            try { _connection.Open(); }
            catch (SqlException ex) when (attempt < 3 && Kpi.Data.KpiRetryStrategy.IsDroppedConnection(ex))
            {
                SqlConnection.ClearPool(_connection);
                Thread.Sleep(500 * attempt);
            }
        }
        return _connection;
    }

    private sealed class DataStatusRow
    {
        public DateTime ReportDate { get; set; }
    }
}

/// <summary>One entry of a filter list (Rpt.spFilterOptions).</summary>
public sealed class OptionRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}
