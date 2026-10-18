using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kpi.Data;
using Kpi.Data.Auth;
using Kpi.Data.Cfg;
using Kpi.Web.Account;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Kpi.Web.Views;

/// <summary>The published-links settings (a Cfg.Integration row, provider PublishedLinks): the allowed addresses.</summary>
public sealed record PublishedLinkSettings(List<string> AllowedAddresses);

/// <summary>A link as Settings lists it, with the names of who made / revoked it.</summary>
public sealed record PublishedLinkRow(PublishedLink Link, string? CreatedBy, string? RevokedBy);

/// <summary>
/// Published links (Oct 2026, Steve - to replace the Klipfolio dashboards in CMS's KPI tabs): one report, opened at
/// /view/{key} without signing in. The key is 32 random bytes; a link is found by its SHA-256 hash, and the key is kept
/// encrypted (Data Protection) so Settings can show the URL again. Optionally only from the allowed addresses (empty =
/// any address - the key alone).
/// A System Admin makes and revokes them in Settings > Published links.
/// </summary>
public sealed class PublishedLinkStore(IDbContextFactory<KpiDbContext> dbs, IDataProtectionProvider protection)
{
    private readonly IDataProtector _keys = protection.CreateProtector("Kpi.Web.PublishedLinks.Key.v1");

    public const string SettingsProvider = "PublishedLinks";

    // a refused open is audited at most once an hour per link (or unknown key) and address
    private static readonly ConcurrentDictionary<string, DateTime> RefusalsAudited = new();

    public static string NewKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary>An address as the allowed list holds it: IPv4 as IPv4 (not IPv6-mapped), null if it isn't one.</summary>
    public static string? Normalise(IPAddress? ip) =>
        ip is null ? null : (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();

    public static string? Normalise(string text) =>
        IPAddress.TryParse(text.Trim(), out var ip) ? Normalise(ip) : null;

    // ---- links ------------------------------------------------------------------------------------------------

    public async Task<List<PublishedLinkRow>> ListAsync()
    {
        await using var db = await dbs.CreateDbContextAsync();
        var links = await db.PublishedLinks.AsNoTracking()
            .OrderBy(l => l.RevokedUtc != null).ThenBy(l => l.Label).ToListAsync();
        var ids = links.SelectMany(l => new[] { l.CreatedByUserId, l.RevokedByUserId }).Where(i => i is not null).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, AuditLog.NameOf);
        return links.Select(l => new PublishedLinkRow(l,
            l.CreatedByUserId is { } c && names.TryGetValue(c, out var cn) ? cn : null,
            l.RevokedByUserId is { } r && names.TryGetValue(r, out var rn) ? rn : null)).ToList();
    }

    /// <summary>Makes a link and returns its key.</summary>
    public async Task<string> CreateAsync(KpiUser actor, string label, string reportSlug, bool showHeader = true)
    {
        label = label.Trim();
        if (label.Length == 0) throw new InvalidOperationException("Give the link a label, e.g. \"CMS desktop: Business Analysis tab\".");
        if (label.Length > 200) throw new InvalidOperationException("Keep the label under 200 characters.");
        var report = Kpi.Web.Data.Kpis.KpiReports.All.FirstOrDefault(r => r.Slug == reportSlug)
                     ?? throw new InvalidOperationException("Choose a report.");
        if (PublishedReports.PageFor(report.Slug) is null) throw new InvalidOperationException("That report can't be published.");

        var key = NewKey();
        await using var db = await dbs.CreateDbContextAsync();
        db.PublishedLinks.Add(new PublishedLink
        {
            Label = label, ReportSlug = report.Slug, KeyHash = Hash(key), KeyCipher = _keys.Protect(key), KeyHint = key[..6], ShowHeader = showHeader,
            CreatedUtc = DateTime.UtcNow, CreatedByUserId = actor.Id
        });
        AuditLog.Add(db, AuditEvents.PublishedLinkChanged, actor, null, $"Published link made: \"{label}\" ({report.Label}), key {key[..6]}…");
        await db.SaveChangesAsync();
        return key;
    }

    /// <summary>A link's key, for its URL; null when it can't be read (the site's key ring changed - make it again).</summary>
    public string? KeyOf(PublishedLink link)
    {
        if (string.IsNullOrEmpty(link.KeyCipher)) return null;
        try { return _keys.Unprotect(link.KeyCipher); }
        catch (CryptographicException) { return null; }
    }

    /// <summary>Header on or off; an open tab follows within a minute.</summary>
    public async Task SetHeaderAsync(KpiUser actor, int id, bool showHeader)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var link = await db.PublishedLinks.SingleOrDefaultAsync(l => l.Id == id)
                   ?? throw new InvalidOperationException("That link no longer exists.");
        if (link.ShowHeader == showHeader) return;
        link.ShowHeader = showHeader;
        AuditLog.Add(db, AuditEvents.PublishedLinkChanged, actor, null,
            $"Published link \"{link.Label}\": header {(showHeader ? "shown" : "hidden")}");
        await db.SaveChangesAsync();
    }

    public async Task RevokeAsync(KpiUser actor, int id)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var link = await db.PublishedLinks.SingleOrDefaultAsync(l => l.Id == id)
                   ?? throw new InvalidOperationException("That link no longer exists.");
        if (link.RevokedUtc is not null) return;
        link.RevokedUtc = DateTime.UtcNow;
        link.RevokedByUserId = actor.Id;
        AuditLog.Add(db, AuditEvents.PublishedLinkChanged, actor, null, $"Published link revoked: \"{link.Label}\", key {link.KeyHint}…");
        await db.SaveChangesAsync();
    }

    /// <summary>The link a key opens, unless revoked. Null for an unknown key.</summary>
    public async Task<PublishedLink?> FindActiveAsync(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 100) return null;
        var hash = Hash(key);
        await using var db = await dbs.CreateDbContextAsync();
        return await db.PublishedLinks.AsNoTracking().SingleOrDefaultAsync(l => l.KeyHash == hash && l.RevokedUtc == null);
    }

    public async Task RecordUseAsync(int id, string? address)
    {
        await using var db = await dbs.CreateDbContextAsync();
        await db.PublishedLinks.Where(l => l.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(l => l.LastUsedUtc, DateTime.UtcNow)
            .SetProperty(l => l.LastUsedAddress, address)
            .SetProperty(l => l.UseCount, l => l.UseCount + 1));
    }

    public async Task AuditRefusedAsync(PublishedLink? link, string reason, string? address)
    {
        var throttleKey = $"{link?.Id.ToString() ?? "unknown"}|{address}";
        var now = DateTime.UtcNow;
        if (RefusalsAudited.TryGetValue(throttleKey, out var last) && now - last < TimeSpan.FromHours(1)) return;
        RefusalsAudited[throttleKey] = now;
        await using var db = await dbs.CreateDbContextAsync();
        AuditLog.Add(db, AuditEvents.PublishedLinkRefused, null, null,
            link is null ? $"Published link refused: {reason}" : $"Published link \"{link.Label}\" refused: {reason}", address);
        await db.SaveChangesAsync();
    }

    // ---- the allowed addresses ------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<string>> AllowedAddressesAsync()
    {
        await using var db = await dbs.CreateDbContextAsync();
        var json = await db.Integrations.AsNoTracking().Where(i => i.Provider == SettingsProvider)
            .Select(i => i.SettingsJson).SingleOrDefaultAsync();
        return string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<PublishedLinkSettings>(json)?.AllowedAddresses ?? [];
    }

    /// <summary>Saves the allowed addresses (one per line or comma). Empty = any address.</summary>
    public async Task<List<string>> SaveAllowedAddressesAsync(KpiUser actor, string text)
    {
        var entries = text.Split(['\n', '\r', ',', ';', ' '], StringSplitOptions.RemoveEmptyEntries);
        var addresses = new List<string>();
        foreach (var e in entries)
            addresses.Add(Normalise(e) ?? throw new InvalidOperationException($"\"{e}\" isn't an IP address."));
        addresses = addresses.Distinct().ToList();

        await using var db = await dbs.CreateDbContextAsync();
        var row = await db.Integrations.SingleOrDefaultAsync(i => i.Provider == SettingsProvider);
        if (row is null)
        {
            row = new Integration { Provider = SettingsProvider, Enabled = true, Status = IntegrationStatus.Connected };
            db.Integrations.Add(row);
        }
        row.SettingsJson = JsonSerializer.Serialize(new PublishedLinkSettings(addresses));
        row.UpdatedByUserId = actor.Id;
        row.UpdatedUtc = DateTime.UtcNow;
        AuditLog.Add(db, AuditEvents.PublishedLinkChanged, actor, null,
            addresses.Count == 0 ? "Published links: allowed from ANY address" : $"Published links: allowed only from {string.Join(", ", addresses)}");
        await db.SaveChangesAsync();
        return addresses;
    }
}
