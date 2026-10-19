namespace Kpi.Data.Auth;

/// <summary>
/// Somebody opened a report (Auth.ReportView; Settings > Audit > Report views, 19 Oct 2026, Steve). One row per
/// OPENING - the same person reopening the same report within 30 minutes counts once - not per refresh. Who (a site
/// user, or the person an embedding app vouched for), which report (its catalogue slug) and how (browser, Home Screen
/// app, phone, embedded). Kept, like the audit trail, for 3 months (Auth.spPruneHistory, nightly). No foreign keys and
/// the name copied in, so a row still says who it was after that person has gone.
/// </summary>
public class ReportView
{
    public long Id { get; set; }
    public DateTime OpenedUtc { get; set; }
    public string Report { get; set; } = "";
    public string? UserId { get; set; }
    public string UserName { get; set; } = "";
    /// <summary>One of <see cref="ReportViewSources"/>.</summary>
    public string Source { get; set; } = "";
}

public static class ReportViewSources
{
    public const string Browser = "Browser";
    public const string Phone = "Phone";
    public const string App = "App";
    public const string Embedded = "Embedded";
}
