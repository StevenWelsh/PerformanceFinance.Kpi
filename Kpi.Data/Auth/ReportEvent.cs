namespace Kpi.Data.Auth;

/// <summary>
/// Something done ON a report (Auth.ReportEvent; Settings > Audit > Report activity, 29 Oct 2026, Steve: "how people
/// interact with the reports ... including when someone presses the timelapse button and for what year"). One row per
/// event: opening a report, choosing a filter, switching a view, pressing a button, closing it (with how long it was
/// looked at), and how long a report's figures took to load. Recorded by KpiPanel's tracker, which every report page
/// already contains, so the copied pages themselves are unchanged. Kept, like the audit trail, for 3 months.
/// </summary>
public class ReportEvent
{
    public long Id { get; set; }
    public DateTime OccurredUtc { get; set; }

    /// <summary>One opening of a report page: ties its events together (32 hex characters).</summary>
    public string Session { get; set; } = "";

    /// <summary>The report's catalogue slug (KpiReports).</summary>
    public string Report { get; set; } = "";

    public string? UserId { get; set; }
    public string UserName { get; set; } = "";

    /// <summary>One of <see cref="ReportViewSources"/>; empty for a load (not one person's).</summary>
    public string Source { get; set; } = "";

    /// <summary>One of <see cref="ReportEventKinds"/>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>What: the filter's caption, the view switch's name, the button's label, the loaded part.</summary>
    public string Label { get; set; } = "";

    /// <summary>The choice made (a filter's or view's), or for Closed the seconds the page was open.</summary>
    public string? Value { get; set; }

    /// <summary>The report's filters at that moment, e.g. "Measure: AF · Division: All · Year: 2025".</summary>
    public string? Filters { get; set; }

    /// <summary>Milliseconds: the time looked at (Closed), or the time taken (Load).</summary>
    public int? Ms { get; set; }
}

public static class ReportEventKinds
{
    public const string Opened = "Opened";
    public const string Filter = "Filter";
    public const string View = "View";
    public const string Button = "Button";
    public const string Closed = "Closed";
    public const string Load = "Load";
}
