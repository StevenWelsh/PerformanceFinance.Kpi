namespace Kpi.Web.Data.Kpis;

/// <summary>One report in the menus: its address under /kpis, name, icon and section. A TickOnly report is seen only by
/// the people ticked for it by name in Settings > Users: "every report" (System Admin, "All reports") leaves it out.</summary>
public sealed record KpiReport(string Slug, string Label, string Icon, string Section, bool TickOnly = false)
{
    /// <summary>The name away from its menu heading (a user's report list, the audit): a Shire report says Mobberley there,
    /// as "Year on Year" alone would read as PF's (3 Nov 2026).</summary>
    public string Name => Section == KpiReports.Shire ? $"{KpiReports.Shire}: {Label}" : Label;
}

/// <summary>Every report, in menu order: the menus, My Account's favourites, Settings > Users' report ticks and the
/// report check (ReportHandler) all read it. A new report page is added here too, or nobody but "all reports" sees it.</summary>
public static class KpiReports
{
    public static readonly string[] Sections = { "Sales", "Suppliers", "Analysis", "Own Book", Shire };

    /// <summary>Shire's Salesforce reports (3 Nov 2026, Steve): their own section, in Shire's colours (kpi.css "Shire"),
    /// called Mobberley (the office Shire became; first "Shire - Salesforce"). Each report's title is "Mobberley: ..." with
    /// "Taken from Salesforce" under it (ShireReports.Title / Source). Tick-only: Steve, Stuart and Andy, no one else -
    /// not even a System Admin unless ticked.</summary>
    public const string Shire = "Mobberley";

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
        new("exposure", "Exposure Top 10", "bi-pie-chart", "Own Book"),
        // the menu says "Year on Year" under the Shire heading; the report itself says Shire (Steve, 3 Nov 2026)
        new("shire-year-on-year", "Year on Year", "bi-calendar3", Shire, TickOnly: true),
        new("shire-sales-performance", "Sales Performance", "bi-bullseye", Shire, TickOnly: true),
        new("shire-business-analysis", "Business Analysis", "bi-diagram-3", Shire, TickOnly: true)
    };

    /// <summary>A stored slug's name for lists and the audit (<see cref="KpiReport.Name"/>); the slug if it's gone.</summary>
    public static string NameOf(string slug) => All.FirstOrDefault(r => r.Slug == slug)?.Name ?? slug;

    public static bool IsTickOnly(string slug) => All.Any(r => r.Slug == slug && r.TickOnly);
}
