namespace Kpi.Web.Data.Kpis;

/// <summary>One report in the menus: its address under /kpis, name, icon and section.</summary>
public sealed record KpiReport(string Slug, string Label, string Icon, string Section);

/// <summary>Every report, in menu order: the menus, My Account's favourites, Settings > Users' report ticks and the
/// report check (ReportHandler) all read it. A new report page is added here too, or nobody but "all reports" sees it.</summary>
public static class KpiReports
{
    public static readonly string[] Sections = { "Sales", "Suppliers", "Analysis", "Own Book" };

    public static readonly KpiReport[] All =
    {
        new("sales-summary", "Sales Summary", "bi-bar-chart", "Sales"),
        new("year-on-year", "Year on Year", "bi-calendar3", "Sales"),
        new("salesperson-summary", "Salesperson Summary", "bi-person", "Sales"),
        new("sales-performance", "Sales Performance", "bi-bullseye", "Sales"),
        new("sales-productivity", "Sales Productivity", "bi-speedometer2", "Sales"),
        new("record-performances", "Record Performances", "bi-trophy", "Sales"),
        new("suppliers-yoy", "Suppliers Year on Year", "bi-truck", "Suppliers"),
        new("supplier-development", "Supplier Development", "bi-graph-up-arrow", "Suppliers"),
        new("business-analysis", "Business Analysis", "bi-diagram-3", "Analysis"),
        new("heat-map", "Postcode Heat Map", "bi-geo-alt", "Analysis"),
        new("margin-analysis", "Margin Analysis", "bi-percent", "Analysis"),
        new("minimum-irr", "Minimum IRR", "bi-shield-check", "Analysis"),
        new("bad-debt", "Bad Debt", "bi-graph-down-arrow", "Own Book"),
        new("exposure", "Exposure Top 10", "bi-pie-chart", "Own Book")
    };
}
