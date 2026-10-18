namespace Kpi.Data.Cfg;

/// <summary>
/// A published link (Settings > Published links, Oct 2026, Steve): one report, opened without signing in, by whoever has
/// the URL - for CMS's KPI tabs, replacing Klipfolio's published dashboards. A link is found by the SHA-256 hash of its
/// key; the key itself is kept ENCRYPTED (Data Protection, as the integrations' keys) so Settings can show the URL again
/// whenever wanted (Steve: not sensitive, and locked to the allowed addresses). Optionally only from the addresses in the
/// PublishedLinks settings (Cfg.Integration). Revoked links are kept, so their history stays readable.
/// </summary>
public class PublishedLink
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
    /// <summary>The report (Data/Kpis/KpiReports slug), e.g. "business-analysis".</summary>
    public string ReportSlug { get; set; } = "";
    public byte[] KeyHash { get; set; } = Array.Empty<byte>();
    /// <summary>The key, encrypted with the site's Data Protection key ring - so its URL can be shown again.</summary>
    public string? KeyCipher { get; set; }
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
