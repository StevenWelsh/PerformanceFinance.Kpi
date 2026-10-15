using Kpi.Web.Data;

namespace Kpi.Web.Reports;

/// <summary>Which applications the Bad Debt figures cover (OB.AnnualBadDebtList's @IsAutoApproved).</summary>
public enum BadDebtApproval
{
    Manual = 0,
    AutoApproved = 1,
    All = 2
}

/// <summary>
/// The Own Book Bad Debt report: last night's results of the desktop's own OB.AnnualBadDebtList, copied with the
/// figures (Rpt.spBadDebt), so the definition stays the desktop's.
/// </summary>
public sealed class BadDebtService(KpiDb db)
{
    public List<BadDebtYearRow> GetHistory(BadDebtApproval approval = BadDebtApproval.All) =>
        db.Report<BadDebtYearRow>("Rpt.spBadDebt", new { IsAutoApproved = (int)approval });
}

public enum ExposureGrouping
{
    Combined,
    Customer,
    BusinessGroup
}

/// <summary>
/// The Own Book Exposure report: last night's top 10 from the desktop's own Reports.Exposure, for all lenders and
/// for each lender (Rpt.spExposure). The KPI site has no fund filter (Oct 2026, Steve): one run per fund would be
/// ~300 more a night. CMS.Web's internal page keeps it.
/// </summary>
public sealed class ExposureService(KpiDb db)
{
    public List<ExposureRow> GetExposure(ExposureGrouping grouping, int? fundLenderId = null) =>
        db.Report<ExposureRow>("Rpt.spExposure", new
        {
            CustomerBusinessGroup = grouping switch
            {
                ExposureGrouping.Customer => (int?)0,
                ExposureGrouping.BusinessGroup => 1,
                _ => null
            },
            FundLenderId = fundLenderId
        });

    public List<ExposureFundOptionResult> GetFundLenders() =>
        db.Report<ExposureFundOptionResult>("Rpt.spExposureFilters", new { List = "Lenders" });
}
