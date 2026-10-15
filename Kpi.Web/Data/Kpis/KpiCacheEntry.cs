namespace Kpi.Web.Data.Kpis;

/// <summary>
/// One cached KPI result. Immutable snapshot of a single load: the data itself,
/// the business "as at" date it represents, a content hash for change detection,
/// and the two timestamps the UI shows/uses. Replaced wholesale on every refresh
/// that finds changed data; on an unchanged refresh only <see cref="LastCheckedUtc"/>
/// and <see cref="LoadedAtUtc"/> advance (via <see cref="WithChecked"/>).
///
/// The two timestamps are deliberately distinct and must never be merged:
///   • <see cref="AsAt"/>         — the business date the DATA represents
///                                  (WebKPI.fnReportDate()); rolls rarely.
///   • <see cref="LoadedAtUtc"/>  — wall-clock time WE last fetched; the
///                                  "Refreshed on" label; advances every fetch.
/// </summary>
/// <typeparam name="T">The materialised result the page holds (e.g. a List&lt;DTO&gt;).</typeparam>
public sealed record KpiCacheEntry<T>(
    T Data,
    DateTime AsAt,
    string ContentHash,
    DateTime LoadedAtUtc,
    DateTime LastCheckedUtc)
{
    /// <summary>
    /// A refresh ran and the data was byte-for-byte unchanged: keep the same
    /// Data/AsAt/hash, just advance both timestamps so "Refreshed on" ticks and
    /// the 10-minute staleness clock resets. This is the "checked, nothing new"
    /// path — the chart never re-renders.
    /// </summary>
    public KpiCacheEntry<T> WithChecked(DateTime nowUtc)
        => this with { LoadedAtUtc = nowUtc, LastCheckedUtc = nowUtc };
}
