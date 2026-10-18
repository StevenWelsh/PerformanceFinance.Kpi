namespace Kpi.Data.Cfg;

/// <summary>
/// A published link (Settings > Published links, Oct 2026, Steve): one report, opened without signing in, by whoever has
/// the URL - for CMS's KPI tabs, replacing Klipfolio's published dashboards. Only a SHA-256 hash of the link's key is
/// kept, as a password would be: the full URL is shown once, when it is made. Optionally only from the addresses in the
/// PublishedLinks settings (Cfg.Integration). Revoked links are kept, so their history stays readable.
/// </summary>
public class PublishedLink
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
    /// <summary>The report (Data/Kpis/KpiReports slug), e.g. "business-analysis".</summary>
    public string ReportSlug { get; set; } = "";
    public byte[] KeyHash { get; set; } = Array.Empty<byte>();
    /// <summary>The key's first few characters, to tell links apart (not enough to use one).</summary>
    public string KeyHint { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string? CreatedByUserId { get; set; }
    public DateTime? RevokedUtc { get; set; }
    public string? RevokedByUserId { get; set; }
    public DateTime? LastUsedUtc { get; set; }
    public string? LastUsedAddress { get; set; }
    public int UseCount { get; set; }
}
