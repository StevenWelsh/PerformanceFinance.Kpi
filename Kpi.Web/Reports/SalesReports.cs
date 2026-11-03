using Kpi.Web.Data;

namespace Kpi.Web.Reports;

// The KPI report services, ported from CMS.Core's Kpis services with the same names and signatures so the pages read
// the same here as in CMS.Web. Each runs its report procedure in the KPI database (Rpt.*, the ports of CMS's WebKPI
// procedures; `Kpi.Tools compare` checks they give the same figures). The Sales Office the Rpt versions add (NULL =
// every office, as CMS.Web's reports, which have no office) is the pages' Sales Office filter (KpiOfficeState) -
// Sales Summary, Year on Year, Business Analysis, Sales Performance, Margin Analysis and Minimum IRR pass it
// (3 Nov 2026, Steve).

public sealed class SalesSummaryService(KpiDb db)
{
    /// <param name="analysisPeriod">"Year" | "Quarter" | "Month"</param>
    /// <param name="divisionId">-1 = all divisions, else a SalesCategoryId</param>
    /// <param name="salespersonId">-1 = all salespeople, else a SalesId (drives the income scope)</param>
    /// <param name="optical">null = no split, true = optical only, false = non-optical only</param>
    public List<SalesSummaryRow> GetSummary(string analysisPeriod, int divisionId = -1, int salespersonId = -1,
                                            bool? optical = null, int? salesOfficeId = null) =>
        db.Report<SalesSummaryRow>("Rpt.spSalesSummary", new
        {
            AnalysisPeriod = analysisPeriod, DivisionId = divisionId, SalespersonId = salespersonId, Optical = optical,
            SalesOfficeId = salesOfficeId
        });
}

public sealed class YearOnYearService(KpiDb db)
{
    public List<YearOnYearRow> GetYearOnYear(string measure, string interval, int divisionId = -1, int salespersonId = -1,
                                             string? arrangement = null, int? salesOfficeId = null) =>
        db.Report<YearOnYearRow>("Rpt.spYearOnYear", new
        {
            Measure = measure, Interval = interval, DivisionId = divisionId, SalespersonId = salespersonId,
            Arrangement = string.IsNullOrEmpty(arrangement) ? null : arrangement, SalesOfficeId = salesOfficeId
        });
}

public sealed class BusinessAnalysisService(KpiDb db)
{
    /// <param name="dimension">"Profession" | "Facility" | "Arrangement" | "Division"</param>
    public List<BusinessAnalysisRow> GetAnalysis(string dimension, string measure, int quarter = 0, int? salesOfficeId = null) =>
        db.Report<BusinessAnalysisRow>("Rpt.spBusinessAnalysis",
            new { Dimension = dimension, Measure = measure, Quarter = quarter, SalesOfficeId = salesOfficeId });
}

public sealed class SalesLeagueTableService(KpiDb db)
{
    public List<SalesLeagueRow> GetLeague(int year, string measure = "TFI", int? salesOfficeId = null) =>
        db.Report<SalesLeagueRow>("Rpt.spSalesLeagueTable", new { Year = year, Measure = measure, SalesOfficeId = salesOfficeId });

    public List<SalesTimelapseRow> GetTimelapse(int year, string measure = "TFI", int? salesOfficeId = null) =>
        db.Report<SalesTimelapseRow>("Rpt.spSalesLeagueTimelapse", new { Year = year, Measure = measure, SalesOfficeId = salesOfficeId });

    public List<SalesTargetRow> GetTargets(int year, string measure = "TFI") =>
        db.Report<SalesTargetRow>("Rpt.spSalesTargets", new { Year = year, Measure = measure });
}

public sealed class SalesProductivityService(KpiDb db)
{
    public List<SalesProductivityRow> GetProductivity(int salespersonId = -1) =>
        db.Report<SalesProductivityRow>("Rpt.spSalesProductivity", new { SalespersonId = salespersonId });
}

public sealed class RecordPerformancesService(KpiDb db)
{
    public List<RecordRow> GetRecords(string grain, int divisionId = -1) =>
        db.Report<RecordRow>("Rpt.spRecordPerformances", new { Grain = grain, DivisionId = divisionId });
}

public sealed class MinIRRService(KpiDb db)
{
    public List<MinIRRRow> GetAnalysis(string analysisPeriod, int divisionId = -1, int? salesOfficeId = null) =>
        db.Report<MinIRRRow>("Rpt.spMinIRRAnalysis",
            new { AnalysisPeriod = analysisPeriod, DivisionId = divisionId, SalesOfficeId = salesOfficeId });
}

public sealed class HeatMapService(KpiDb db)
{
    public List<HeatMapRow> GetHeatMap(string years, string arrangement = "OwnBook") =>
        db.Report<HeatMapRow>("Rpt.spHeatMap", new { Years = years, Arrangement = arrangement });
}

public sealed class MarginAnalysisService(KpiDb db)
{
    public List<MarginYearRow> GetByYear(int divisionId = -1, int? salesOfficeId = null) =>
        db.Report<MarginYearRow>("Rpt.spMarginAnalysisByYear", new { DivisionId = divisionId, SalesOfficeId = salesOfficeId })
          .OrderBy(r => r.YearInt).ToList();

    public List<MarginMonthRow> GetByMonth(int divisionId = -1, int rollingMonth = 0, int? salesOfficeId = null) =>
        db.Report<MarginMonthRow>("Rpt.spMarginAnalysisByMonth",
            new { DivisionId = divisionId, RollingMonth = rollingMonth, SalesOfficeId = salesOfficeId })
          .OrderBy(r => r.YearInt).ThenBy(r => r.MonthInt).ToList();

    public MarginDaysRow? GetByDays(int days, int divisionId = -1, int? salesOfficeId = null) =>
        db.Report<MarginDaysRow>("Rpt.spMarginAnalysisByDays", new { Days = days, DivisionId = divisionId, SalesOfficeId = salesOfficeId })
            .FirstOrDefault();
}
