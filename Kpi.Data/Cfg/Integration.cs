namespace Kpi.Data.Cfg;

/// <summary>
/// One external service the site talks to (Cfg.Integration), as DavidsonHR's Integrations: one row per provider,
/// seeded empty by migration, configured by the System Admin on the site. Plain settings are JSON; the secret (an
/// API key) is encrypted with ASP.NET Data Protection and is never shown back.
/// </summary>
public class Integration
{
    public string Provider { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>"NotConnected" | "Connected" | "NeedsAttention".</summary>
    public string Status { get; set; } = IntegrationStatus.NotConnected;
    public string? SettingsJson { get; set; }
    public string? SecretsCipher { get; set; }
    public DateTime? SecretsUpdatedUtc { get; set; }
    /// <summary>Testing: every email goes here instead, subject marked. Empty = real recipients (and outside
    /// Production nothing is sent at all without it).</summary>
    public string? RedirectTo { get; set; }
    public string? ConnectedByUserId { get; set; }
    public DateTime? ConnectedUtc { get; set; }
    public DateTime? LastOkUtc { get; set; }
    public DateTime? LastErrorUtc { get; set; }
    public string? LastError { get; set; }
    public string? UpdatedByUserId { get; set; }
    public DateTime? UpdatedUtc { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

public static class IntegrationProviders
{
    public const string Resend = "Resend";
}

public static class IntegrationStatus
{
    public const string NotConnected = "NotConnected";
    public const string Connected = "Connected";
    public const string NeedsAttention = "NeedsAttention";
}
