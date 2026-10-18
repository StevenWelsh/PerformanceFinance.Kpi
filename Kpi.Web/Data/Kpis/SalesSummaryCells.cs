using Kpi.Web.Reports;

namespace Kpi.Web.Data.Kpis;

/// <summary>One Sales Summary cell: this period against the same period last year.</summary>
public sealed record SalesSummaryCell(SalesSummaryRow? Now, SalesSummaryRow? Prev);

/// <summary>
/// The cells of Sales Summary and Salesperson Summary (18 Oct 2026, Steve: "always slow"). Each cell is one
/// Rpt.spSalesSummary call (~0.2-0.7 s on Azure), and a page shows 9-15 of them. Shared here so that:
///   - the desktop panel (SalesSummaryPanel) and the phone card (SalesSummaryMobileCard) use the SAME cache entry -
///     before, a phone fetched every figure twice under two names;
///   - KpiWarmer can fill every cell of both pages before anyone opens them: at start-up, when the business date
///     moves, and after each new dataset is published.
/// </summary>
public static class SalesSummaryCells
{
    public static readonly string[] Periods = { "Year", "Quarter", "Month" };

    /// <summary>Salesperson Summary's people - hardcoded, as in CMS.Web (Klipfolio uses a manual tick-list): Susan
    /// Marshall, Pete George, Ryan O'Dwyer, Stuart Burn.</summary>
    public static readonly int[] SalespersonSummaryTeam = { 74, 68, 73, 82 };

    public static string Key(KpiCache cache, string period, int divisionId, int salespersonId) =>
        cache.Key("SalesSummaryPanel", period, divisionId, salespersonId);

    /// <summary>The "pure" loader: one session, the report date and the cell's two rows; nothing else touched.</summary>
    public static Func<Task<(SalesSummaryCell Data, DateTime AsAt)>> Loader(
        KpiSessionFactory sessions, string period, int divisionId, int salespersonId) => async () =>
    {
        using var cms = await sessions.BeginAsync();
        var asAt = cms.Db.ReportDate();
        var rows = new SalesSummaryService(cms.Db).GetSummary(period, divisionId: divisionId, salespersonId: salespersonId);
        return (new SalesSummaryCell(rows.FirstOrDefault(r => r.PeriodId == 1), rows.FirstOrDefault(r => r.PeriodId == 2)), asAt);
    };

    /// <summary>
    /// Every cell the two pages show, one at a time (so viewers keep their share of the database). With
    /// <paramref name="refresh"/> each is re-run even if cached - a new dataset keeps the same keys. Returns how many.
    /// </summary>
    public static async Task<int> PrimeAsync(KpiCache cache, KpiSessionFactory sessions, bool refresh, CancellationToken ct)
    {
        List<int> divisions;
        using (var cms = await sessions.BeginAsync())
            divisions = cms.Db.Options("Divisions").Select(o => o.Id).ToList();

        var cells = Periods.SelectMany(p => new[] { -1 }.Concat(divisions).Select(d => (p, d, s: -1)))     // Sales Summary
            .Concat(Periods.SelectMany(p => SalespersonSummaryTeam.Select(s => (p, d: -1, s))))         // Salesperson Summary
            .ToList();

        var done = 0;
        foreach (var (period, division, person) in cells)
        {
            if (ct.IsCancellationRequested) break;
            var key = Key(cache, period, division, person);
            var load = Loader(sessions, period, division, person);
            if (refresh) await cache.RefreshAsync(key, load);
            else await cache.GetOrLoad(key, load);
            done++;
        }
        return done;
    }
}
