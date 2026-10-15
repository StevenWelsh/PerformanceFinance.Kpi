namespace Kpi.Web.Reports;

// The KPI reports' row shapes, ported from CMS.Core's Kpis/Dtos with the same names and properties, so the pages
// read the same here as in CMS.Web. Each fills by column name from its Rpt procedure.

/// <summary>
/// One year of the Own Book Bad Debt report (Rpt.spBadDebt: last night's copy of OB.AnnualBadDebtList). The three
/// calculated figures follow the desktop's Bad Debt History screen: net bad debt is written off less recovered, and
/// both ratios are over the year-end outstanding capital, to 4dp.
/// </summary>
public class BadDebtYearRow
{
    public int Id { get; set; }
    public int BadDebtYear { get; set; }
    public DateTime YearStartDate { get; set; }
    public DateTime YearEndDate { get; set; }
    public decimal AmountFinanced { get; set; }
    public decimal RemainingCapital { get; set; }
    public int ActiveLeadCount { get; set; }
    public decimal WrittenOffAmount { get; set; }
    public int WrittenOffLeadCount { get; set; }
    public decimal RecoveredAmount { get; set; }
    public decimal BadDebtCosts { get; set; }
    public int TerminatedLeadCount { get; set; }
    public int ArrearsLeadCount { get; set; }
    public int ArrearsCompanyCount { get; set; }

    /// <summary>Written off over the year-end book, as a fraction (0.0044 = 0.44%).</summary>
    public decimal BadDebtPercentage => RemainingCapital == 0 ? 0 : Math.Round(WrittenOffAmount / RemainingCapital, 4);

    public decimal NetBadDebt => WrittenOffAmount - RecoveredAmount;

    /// <summary>Net bad debt over the year-end book, as a fraction.</summary>
    public decimal NetBadDebtPercent => RemainingCapital == 0 ? 0 : Math.Round(NetBadDebt / RemainingCapital, 4);
}

/// <summary>
/// One Business Analysis "By Lender" cell: a (lender, year) with the year's total for the chosen measure and the
/// SAME lender's figure for the year before - to the report date when the year is the report year, the whole year
/// otherwise. Shaped by <see cref="BusinessAnalysisLenderService.Build"/>.
/// </summary>
public class BusinessAnalysisLenderRow
{
    public int Yr { get; set; }
    public string Lender { get; set; } = "";
    public decimal Value { get; set; }
    /// <summary>null = no row the year before (a new lender, the star); 0 = rows, but none by the report date
    /// ("–", no % change).</summary>
    public decimal? PrevValue { get; set; }
}

/// <summary>One (year, quarter, lender) from Rpt.spBusinessAnalysisByLender: both measures, for the whole period
/// and to the report date's month and day. Quarter 0 = the whole year.</summary>
public class BusinessAnalysisLenderFigureRow
{
    public int Year { get; set; }
    public int Quarter { get; set; }
    public string Lender { get; set; } = "";
    public decimal AF { get; set; }
    public decimal AFToDate { get; set; }
    public decimal TFI { get; set; }
    public decimal TFIToDate { get; set; }
}

/// <summary>One Business Analysis cell: a (dimension value, year) with its full-year and year-to-date totals for the
/// chosen measure.</summary>
public class BusinessAnalysisRow
{
    public int Yr { get; set; }
    public string DimValue { get; set; } = "";
    public decimal FullTotal { get; set; }
    public decimal YtdTotal { get; set; }
}

/// <summary>
/// One line of the Own Book Exposure report: a customer (company or personal contact) or a business group, with its
/// active leads and the capital they still owe, as a share of the whole own book. CustomerType says which kind of
/// line it is (0 company, 1 contact, 2 business group), and Id is that kind's own key.
/// </summary>
public class ExposureRow
{
    public int RowNumber { get; set; }
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int LeadCount { get; set; }
    public decimal RemainingCapital { get; set; }
    public decimal TotalRemainingCapital { get; set; }
    /// <summary>A fraction (0.2419 = 24.19%).</summary>
    public decimal PercentRemainingCapital { get; set; }
    public int CustomerType { get; set; }
}

/// <summary>A fund lender or fund to filter the Exposure report by.</summary>
public class ExposureFundOptionResult
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int FundLenderId { get; set; }
}

/// <summary>
/// One Postcode Heat Map row: a postcode area (+ its district town and region) with both lanes - the own-book
/// capital position and the chosen year's sales. Unmatched postcodes arrive as area 'U' / 'Unmapped'.
/// </summary>
public class HeatMapRow
{
    public string Region { get; set; } = "";
    public string District { get; set; } = "";
    public string PostCodeArea { get; set; } = "";
    // capital lane (own book, current position)
    public decimal RemainingCapital { get; set; }
    public int ActiveLeads { get; set; }
    public int ActiveCustomers { get; set; }
    // sales lane (chosen year, chosen arrangement)
    public decimal AF { get; set; }
    public decimal TFI { get; set; }
    public int ActivatedLeads { get; set; }
}

/// <summary>One year of Margin Analysis. Rates are FRACTIONS (0.0750 = 7.50%). TotalAF is null for a year with no
/// own-book deals, which hosts show as £0.</summary>
public class MarginYearRow
{
    public int YearInt { get; set; }
    public decimal BuyRate { get; set; }
    public decimal SellRate { get; set; }
    public decimal Margin { get; set; }
    public decimal TotalFunding { get; set; }
    public decimal? TotalAF { get; set; }
}

/// <summary>One month, plus an "Overall" row - the 12 Month Analysis table. IsFuture flags months not yet complete.</summary>
public class MarginMonthRow
{
    public int YearInt { get; set; }
    public int MonthInt { get; set; }
    public string MonthString { get; set; } = "";
    public decimal BuyRate { get; set; }
    public decimal SellRate { get; set; }
    public decimal Margin { get; set; }
    public decimal TotalFunding { get; set; }
    public decimal TotalAF { get; set; }
    public bool IsFuture { get; set; }
    public int OrderInt { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
}

/// <summary>A single date-range row - the Last N Days panel. TotalAF is null when the window holds no own-book deals.</summary>
public class MarginDaysRow
{
    public string DateRange { get; set; } = "";
    public decimal BuyRate { get; set; }
    public decimal SellRate { get; set; }
    public decimal Margin { get; set; }
    public decimal TotalFunding { get; set; }
    public decimal? TotalAF { get; set; }
}

/// <summary>
/// One Minimum IRR Analysis row: an own-book (division, salesperson) breakdown for a period. The Total row has
/// SalesDivision = Salesperson = "Total" and OrderInt = 1000. Rates and percents are FRACTIONS.
/// </summary>
public class MinIRRRow
{
    public int OrderInt { get; set; }
    public string GroupByPeriod { get; set; } = "";
    public string SalesDivision { get; set; } = "";
    public string Salesperson { get; set; } = "";
    public int TransactionsTotal { get; set; }
    public decimal AFTotal { get; set; }
    public decimal IRRAverage { get; set; }
    public int TransactionsUnderMinIRR { get; set; }
    public decimal AFUnderMinIRR { get; set; }
    public decimal PercentTransactionsUnderMinIRR { get; set; }
    public decimal PercentAFUnderMinIRR { get; set; }
    public bool IsTotal => OrderInt == 1000;
}

/// <summary>One performance record: a (salesperson, period) TFI figure at the requested grain.</summary>
public class RecordRow
{
    public int Yr { get; set; }
    public string Period { get; set; } = "";
    public int SalesId { get; set; }
    public string Salesperson { get; set; } = "";
    public decimal TFI { get; set; }
}

/// <summary>One (year, salesperson) league entry. IsCore = a core salesperson for that year; the page ranks the core
/// rows and rolls the rest into the "Other" breakdown.</summary>
public class SalesLeagueRow
{
    public int Yr { get; set; }
    public int SalesId { get; set; }
    public string Salesperson { get; set; } = "";
    public decimal MeasureValue { get; set; }
    public bool IsCore { get; set; }
}

/// <summary>One (salesperson, week) sales bucket for the race.</summary>
public class SalesTimelapseRow
{
    public int SalesId { get; set; }
    public string Salesperson { get; set; } = "";
    public int WeekNo { get; set; }
    public DateTime WeekEnd { get; set; }
    public decimal WeekValue { get; set; }
}

/// <summary>One (year, salesperson) sales target for the chosen measure. YtdTarget is the slice due by the report
/// date, prorated by working days (UsesWorkingDays) or calendar days. A zero YearTarget means no target is set.</summary>
public class SalesTargetRow
{
    public int Yr { get; set; }
    public int SalesId { get; set; }
    public string Salesperson { get; set; } = "";
    public decimal YearTarget { get; set; }
    public decimal YtdTarget { get; set; }
    public decimal ElapsedFraction { get; set; }
    public bool UsesWorkingDays { get; set; }
}

/// <summary>One (salesperson, year) row of the lead-to-sale funnel; the procedure returns more columns than this.</summary>
public class SalesProductivityRow
{
    public int SalesId { get; set; }
    public string Salesperson { get; set; } = "";
    public int Year { get; set; }
    // Leads In
    public decimal TotalLead_AF { get; set; }
    public int TotalLead_Count { get; set; }
    public int HQLead_Count { get; set; }
    public int Self_OtherSales_Count { get; set; }
    public decimal SelfGen_Percent_Count { get; set; }
    // Conversion (count-based percentages, 0..1)
    public decimal L_APR_Percent_Count { get; set; }
    public decimal L_ACT_Percent_Count { get; set; }
    public decimal APR_ACT_Percent_Count { get; set; }
}

/// <summary>One Sales Summary comparison line (PeriodId 1 = this year to date, 2 = last year to date).</summary>
public class SalesSummaryRow
{
    public int PeriodId { get; set; }
    public string AnalysisPeriodName { get; set; } = "";
    public DateTime DateFrom { get; set; }
    public DateTime DateTo { get; set; }
    public decimal TFI { get; set; }        // income for the scope (ALL = TFI, individual = GFI + SalesII)
    public decimal AF { get; set; }
    public decimal Margin { get; set; }
    public int Leads { get; set; }
    public int NewLeads { get; set; }
    public int ExistingLeads { get; set; }
}

/// <summary>One supplier's year: the amount financed on its equipment for deals paid out that year, with the
/// supplier's profession and its CURRENT account manager.</summary>
public class SupplierYearRow
{
    public int Year { get; set; }
    public int IntroducerId { get; set; }
    public string Supplier { get; set; } = "";
    public string Profession { get; set; } = "";
    public int? AccountManagerId { get; set; }
    public bool ManagedByHQ { get; set; }
    public decimal AmountFinanced { get; set; }
}

/// <summary>A supplier's place in a ranking.</summary>
public class SupplierRankResult
{
    public int Rank { get; set; }
    public string Supplier { get; set; } = "";
    public string Profession { get; set; } = "";
    public decimal AmountFinanced { get; set; }
}

/// <summary>One year card of Suppliers Year on Year: the top suppliers, largest first.</summary>
public class SupplierYoyYearResult
{
    public int Year { get; set; }
    public List<SupplierRankResult> Lines { get; set; } = new();
}

/// <summary>One year of a salesperson's suppliers: the top ten's total, how many were active, and all.</summary>
public class SupplierDevelopmentYearResult
{
    public int Year { get; set; }
    public decimal Top10AmountFinanced { get; set; }
    public int ActiveSuppliers { get; set; }
    public decimal TotalAmountFinanced { get; set; }
}

/// <summary>One salesperson's Supplier Development card: the suppliers they look after now, ranked for the report
/// year, and the same suppliers' figures for each of the six years, newest first.</summary>
public class SupplierDevelopmentResult
{
    public int SalesId { get; set; }
    public string Salesperson { get; set; } = "";
    public int Year { get; set; }
    public List<SupplierRankResult> Top10 { get; set; } = new();
    public List<SupplierDevelopmentYearResult> Years { get; set; } = new();
}

/// <summary>One Year on Year cell: a (Year, Period) with the measure summed and split OB / Brokered.</summary>
public class YearOnYearRow
{
    public int Year { get; set; }
    public int Period { get; set; }
    public decimal Total { get; set; }
    public decimal TotalOB { get; set; }
    public decimal TotalBrokered { get; set; }
}
