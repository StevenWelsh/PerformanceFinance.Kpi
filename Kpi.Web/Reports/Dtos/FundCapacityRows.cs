namespace Kpi.Web.Reports;

/// <summary>
/// Fund Capacity (26 Nov 2026, Steve - for Stuart): one lender CMS ticks as an "active block lender", as its Own Book >
/// Fund Lender Capacity screen, from Rpt.spFundCapacity.
/// </summary>
public class FundCapacityLender
{
    public int FundLenderId { get; set; }
    public string LenderName { get; set; } = "";
    /// <summary>"Block", "IWFA", "RCF" - or more than one, comma separated; empty when it has no live funds.</summary>
    public string FundType { get; set; } = "";
    /// <summary>Its capital limit; when it has none, its live RCF's / IWFA's own limit. Null = no limit set.</summary>
    public decimal? Limit { get; set; }
    public decimal Capital { get; set; }
    /// <summary>Capital counted against the limit (a block at its capacity percentage).</summary>
    public decimal Exposure { get; set; }
    /// <summary>The rate for NEW funding, a fraction (0.0775 = 7.75%); null when there is nothing to go on.</summary>
    public decimal? BuyRate { get; set; }
    /// <summary>Drawn in the 3 months to the report date: new blocks, deals moved onto an RCF, IWFA cohorts.</summary>
    public decimal Drawn3m { get; set; }

    public decimal? Remaining => Limit is { } l ? l - Exposure : null;
    public decimal? Used => Limit is > 0 ? Exposure / Limit : null;
}
