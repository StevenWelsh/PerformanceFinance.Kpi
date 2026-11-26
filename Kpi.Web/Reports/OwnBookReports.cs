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

/// <summary>
/// Auto Approval (26 Nov 2026, for Stuart): KPI-site only. A null division = every division; period = this | last | 3 | 6 | 12 (this month, last
/// calendar month, or that many months to the report date) (Performance and Rules ignore it: since auto approval began).
/// </summary>
public sealed class AutoApprovalService(KpiDb db)
{
    public List<AutoApprovalFunnelRow> GetFunnel(int? divisionId, string period) =>
        db.Report<AutoApprovalFunnelRow>("Rpt.spAutoApproval", new { Part = "Funnel", DivisionId = divisionId, Period = period });

    public AutoApprovalOutcome GetOutcome(int? divisionId, string period) =>
        db.Report<AutoApprovalOutcome>("Rpt.spAutoApproval", new { Part = "Outcome", DivisionId = divisionId, Period = period })
          .FirstOrDefault() ?? new AutoApprovalOutcome();

    /// <summary>"NotEligibleReasons" or "EligibleReasons": applications per reason, from the decisions recorded since 16 Oct 2026.</summary>
    public List<AutoApprovalReasonRow> GetReasons(string part, int? divisionId, string period) =>
        db.Report<AutoApprovalReasonRow>("Rpt.spAutoApproval", new { Part = part, DivisionId = divisionId, Period = period });

    public List<AutoApprovalReasonDetailRow> GetReasonDetails(int? divisionId, string period) =>
        db.Report<AutoApprovalReasonDetailRow>("Rpt.spAutoApproval", new { Part = "ReasonDetails", DivisionId = divisionId, Period = period });

    public List<AutoApprovalPerformanceRow> GetPerformance(int? divisionId) =>
        db.Report<AutoApprovalPerformanceRow>("Rpt.spAutoApproval", new { Part = "Performance", DivisionId = divisionId });

}

/// <summary>Fund Capacity (26 Nov 2026): KPI-site only (CMS's capacity screen is on the desktop, not CMS.Web).</summary>
public sealed class FundCapacityService(KpiDb db)
{
    public List<FundCapacityLender> GetLenders() => db.Report<FundCapacityLender>("Rpt.spFundCapacity", new { List = "Lenders" });
}