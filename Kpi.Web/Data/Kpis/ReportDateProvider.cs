using Microsoft.Extensions.Caching.Memory;

namespace Kpi.Web.Data.Kpis;

/// <summary>
/// Caches the business "as at" date of the figures (Rpt.spDataStatus) that ~9 KPI pages otherwise each query on
/// every load. It is company-wide (no user input) and changes at most once a day, so one shared value with a short
/// TTL is ample. Also the natural place the KPI cache reads the report date from — entries are keyed by it, so when
/// the business date rolls, yesterday's entries are superseded rather than served stale.
/// </summary>
public sealed class ReportDateProvider(KpiSessionFactory sessions, IMemoryCache cache)
{
    private const string Key = "kpi:report-date";
    // Short TTL: the value only rolls across a date boundary, so a few minutes of
    // staleness is harmless and keeps the DB round-trip off the hot path.
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    /// <summary>The current business report date, cached. One DB round-trip per TTL window, app-wide.</summary>
    public DateTime Get()
        => cache.GetOrCreate(Key, e =>
        {
            e.AbsoluteExpirationRelativeToNow = Ttl;
            using var db = sessions.Create();
            return db.ReportDate();
        });
}
