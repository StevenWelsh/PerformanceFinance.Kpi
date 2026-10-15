using Kpi.Web.Data;

namespace Kpi.Web.Reports;

/// <summary>
/// Business Analysis by lender: the figures from Rpt.spBusinessAnalysisByLender, shaped for the chosen measure and
/// quarter - each lender's year beside its own year before (to the report date for the report year).
/// </summary>
public sealed class BusinessAnalysisLenderService(KpiDb db)
{
    public List<BusinessAnalysisLenderRow> GetByLender(string measure, int quarter = 0)
    {
        var reportYear = db.ReportDate().Year;
        var figures = db.Report<BusinessAnalysisLenderFigureRow>("Rpt.spBusinessAnalysisByLender");
        return Build(figures, measure, quarter, reportYear);
    }

    public static List<BusinessAnalysisLenderRow> Build(IEnumerable<BusinessAnalysisLenderFigureRow> figures, string measure,
                                                        int quarter, int reportYear)
    {
        var tfi = string.Equals(measure, "TFI", StringComparison.OrdinalIgnoreCase);
        var ofQuarter = figures.Where(f => f.Quarter == quarter).ToList();

        // Lenders are matched across years as SQL groups them: case and trailing spaces ignored
        // ("BNP Paribas Leasing Solutions Limited " carries one in the data).
        var byYear = new Dictionary<string, BusinessAnalysisLenderFigureRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in ofQuarter)
            byYear[f.Year + "|" + f.Lender.TrimEnd()] = f;

        return ofQuarter
            .Select(f =>
            {
                decimal? prevValue = null;
                if (byYear.TryGetValue((f.Year - 1) + "|" + f.Lender.TrimEnd(), out var prev))
                {
                    var toDate = f.Year == reportYear;
                    prevValue = tfi ? (toDate ? prev.TFIToDate : prev.TFI)
                                    : (toDate ? prev.AFToDate : prev.AF);
                }
                return new BusinessAnalysisLenderRow
                {
                    Yr = f.Year,
                    Lender = f.Lender,
                    Value = tfi ? f.TFI : f.AF,
                    PrevValue = prevValue
                };
            })
            .OrderBy(r => r.Yr)
            .ThenByDescending(r => r.Value)
            .ToList();
    }
}
