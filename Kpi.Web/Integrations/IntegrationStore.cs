using System.Security.Cryptography;
using System.Text.Json;
using Kpi.Data;
using Kpi.Data.Auth;
using Kpi.Data.Cfg;
using Kpi.Web.Account;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Kpi.Web.Integrations;

/// <summary>Resend's plain settings: who the email is from, and a hint of the saved key (never the key).</summary>
public sealed record ResendSettings(string FromAddress, string FromName, string KeyHint);

/// <summary>Resend's secret: the API key. Encrypted at rest; read only at the moment of sending.</summary>
public sealed record ResendSecrets(string ApiKey);

/// <summary>The saved secret can't be decrypted - this site's key ring changed. The card says "reconnect".</summary>
public sealed class IntegrationSecretsUnreadableException(string provider)
    : Exception($"{provider}'s saved key can no longer be read on this site. Connect it again.");

/// <summary>
/// The one way in and out of Cfg.Integration, as DavidsonHR's IntegrationStore: plain settings as JSON, secrets
/// encrypted with ASP.NET Data Protection under their own purpose, status and health, and an audit line for every
/// change (never the secret).
/// </summary>
public sealed class IntegrationStore(IDbContextFactory<KpiDbContext> dbs, IDataProtectionProvider protection)
{
    private readonly IDataProtector _secrets = protection.CreateProtector("Kpi.Web.Integrations.Secrets.v1");

    public async Task<Integration> GetAsync(string provider)
    {
        await using var db = await dbs.CreateDbContextAsync();
        return await db.Integrations.AsNoTracking().SingleAsync(i => i.Provider == provider);
    }

    public static T? ReadSettings<T>(Integration row) =>
        string.IsNullOrEmpty(row.SettingsJson) ? default : JsonSerializer.Deserialize<T>(row.SettingsJson);

    public T? ReadSecrets<T>(Integration row)
    {
        if (string.IsNullOrEmpty(row.SecretsCipher)) return default;
        try { return JsonSerializer.Deserialize<T>(_secrets.Unprotect(row.SecretsCipher)); }
        catch (CryptographicException) { throw new IntegrationSecretsUnreadableException(row.Provider); }
    }

    /// <summary>Saves Resend's settings and, when one is given, a new key (blank keeps the saved one).</summary>
    public async Task SaveResendAsync(KpiUser actor, string fromAddress, string fromName, string? newApiKey,
                                      string? redirectTo, bool enabled)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var row = await db.Integrations.SingleAsync(i => i.Provider == IntegrationProviders.Resend);
        var hint = ReadSettings<ResendSettings>(row)?.KeyHint ?? "";
        var changes = new List<string>();

        if (!string.IsNullOrWhiteSpace(newApiKey))
        {
            var key = newApiKey.Trim();
            row.SecretsCipher = _secrets.Protect(JsonSerializer.Serialize(new ResendSecrets(key)));
            row.SecretsUpdatedUtc = DateTime.UtcNow;
            hint = key.Length > 10 ? $"{key[..6]}…{key[^4..]}" : "saved";
            changes.Add("a new API key");
        }
        if (row.SecretsCipher is null) throw new InvalidOperationException("Enter the API key.");

        var settings = new ResendSettings(fromAddress.Trim(), fromName.Trim(), hint);
        if (row.SettingsJson != JsonSerializer.Serialize(settings)) changes.Add($"from {settings.FromName} <{settings.FromAddress}>");
        row.SettingsJson = JsonSerializer.Serialize(settings);

        var redirect = string.IsNullOrWhiteSpace(redirectTo) ? null : redirectTo.Trim();
        if (redirect != row.RedirectTo) changes.Add(redirect is null ? "test address cleared" : $"test address {redirect}");
        row.RedirectTo = redirect;

        if (enabled != row.Enabled) changes.Add(enabled ? "enabled" : "disabled");
        row.Enabled = enabled;

        if (row.Status != IntegrationStatus.Connected)
        {
            row.Status = IntegrationStatus.Connected;
            row.ConnectedByUserId = actor.Id;
            row.ConnectedUtc = DateTime.UtcNow;
        }
        row.UpdatedByUserId = actor.Id;
        row.UpdatedUtc = DateTime.UtcNow;
        AuditLog.Add(db, AuditEvents.IntegrationChanged, actor, null,
            $"Resend saved{(changes.Count > 0 ? ": " + string.Join(", ", changes) : "")}");
        await db.SaveChangesAsync();
    }

    public async Task DisconnectAsync(string provider, KpiUser actor)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var row = await db.Integrations.SingleAsync(i => i.Provider == provider);
        row.SecretsCipher = null;
        row.SettingsJson = null;
        row.SecretsUpdatedUtc = null;
        row.Enabled = false;
        row.Status = IntegrationStatus.NotConnected;
        row.UpdatedByUserId = actor.Id;
        row.UpdatedUtc = DateTime.UtcNow;
        AuditLog.Add(db, AuditEvents.IntegrationChanged, actor, null, $"{provider} disconnected; its key was deleted");
        await db.SaveChangesAsync();
    }

    /// <summary>Health: the last call worked, or what went wrong (never a secret - the callers write the text).</summary>
    public async Task RecordCallAsync(string provider, string? error)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var row = await db.Integrations.SingleAsync(i => i.Provider == provider);
        if (error is null) row.LastOkUtc = DateTime.UtcNow;
        else
        {
            row.LastErrorUtc = DateTime.UtcNow;
            row.LastError = error.Length > 1000 ? error[..1000] : error;
        }
        await db.SaveChangesAsync();
    }
}
