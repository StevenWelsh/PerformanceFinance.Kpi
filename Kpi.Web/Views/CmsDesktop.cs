using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kpi.Data;
using Kpi.Data.Auth;
using Kpi.Data.Cfg;
using Kpi.Web.Account;
using Kpi.Web.Integrations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Kpi.Web.Views;

/// <summary>One report CMS may open, and whether its header (title, As at / Refreshed) shows.</summary>
public sealed record CmsDesktopReport(string Slug, bool ShowHeader);

/// <summary>The CMS desktop integration's settings (Cfg.Integration, provider CmsDesktop).</summary>
public sealed record CmsDesktopSettings(IReadOnlyList<CmsDesktopReport> Reports, int Generation, string SecretHint);

/// <summary>Its secret: shared with CMS, which signs each ticket with it. Encrypted at rest.</summary>
public sealed record CmsDesktopSecrets(string Secret);

/// <summary>
/// THE CMS DESKTOP INTEGRATION (19 Oct 2026, Steve - replaces published links for CMS's KPI tabs). CMS already knows who
/// is signed in and checks their KPIs permission; to open a report here it signs a TICKET with a secret only it and this
/// site hold, and its tab control (WebView2) POSTs it to /embed/{report}:
///
///     user    = the CMS username (for the audit)              issued = Unix seconds, UTC
///     nonce   = 16+ random characters, new every time         sig    = lowercase hex HMAC-SHA256, key = the secret's
///                                                                      UTF-8 bytes, over "{report}|{user}|{issued}|{nonce}"
///
/// The site accepts it when the signature matches, it was issued within the last 60 seconds (either way, for clocks a
/// little apart), its nonce hasn't been seen, the integration is on and the report is ticked for CMS. That tab then gets a
/// 12-hour cookie for THAT report only (Data Protection, carrying the secret's generation - replacing the secret ends
/// every open session) and is sent to GET /embed/{report}, the report on its own like a published link. Nothing usable is
/// ever in an address: a copied URL opens nothing without the cookie.
/// </summary>
public sealed class CmsDesktopStore(IntegrationStore store)
{
    public const string Provider = "CmsDesktop";

    public async Task<Integration> RowAsync() => await store.GetAsync(Provider);

    public static CmsDesktopSettings SettingsOf(Integration row) =>
        IntegrationStore.ReadSettings<CmsDesktopSettings>(row) ?? new CmsDesktopSettings(Array.Empty<CmsDesktopReport>(), 0, "");

    /// <summary>The integration as it stands: on, its settings and its secret (null when none or unreadable).</summary>
    public async Task<(bool On, CmsDesktopSettings Settings, string? Secret)> CurrentAsync()
    {
        var row = await store.GetAsync(Provider);
        string? secret = null;
        try { secret = store.ReadSecrets<CmsDesktopSecrets>(row)?.Secret; } catch (IntegrationSecretsUnreadableException) { }
        return (row.Enabled && row.Status == IntegrationStatus.Connected, SettingsOf(row), secret);
    }

    /// <summary>A new random secret: 32 bytes, base64url (43 characters, nothing that needs escaping).</summary>
    public static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public Task SaveAsync(KpiUser actor, IReadOnlyList<CmsDesktopReport> reports, string? newSecret, bool enabled) =>
        store.SaveCmsDesktopAsync(actor, reports, newSecret, enabled);

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The signature CMS must send (see the class summary). Public so a test or a tool can make one.</summary>
    public static string Sign(string secret, string report, string user, long issued, string nonce)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{report}|{user}|{issued.ToString(CultureInfo.InvariantCulture)}|{nonce}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>The door for /embed/{report}, before the sign-in gate (see <see cref="CmsDesktopStore"/>).</summary>
public static class CmsDesktopDoor
{
    public static readonly TimeSpan TicketWindow = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    private const string Unavailable = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Not available</title></head>
        <body style="font-family:'Segoe UI',system-ui,sans-serif;color:#444;display:flex;align-items:center;justify-content:center;height:90vh;margin:0">
        <p>This report isn't available here. Open it from CMS again.</p></body></html>
        """;

    // nonces seen in the last few minutes (one server), and the last audit line per CMS user + report / refused address
    private static readonly ConcurrentDictionary<string, DateTime> Seen = new();
    private static readonly ConcurrentDictionary<string, DateTime> LastAudit = new();

    private sealed record Session(string Report, string User, int Generation);

    public static string CookieName(string report) => "kpi-embed-" + report.ToLowerInvariant();

    public static IApplicationBuilder UseCmsDesktop(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/embed", out var rest)) { await next(); return; }
            var report = rest.Value?.Trim('/') ?? "";
            if (report.Length == 0 || report.Contains('/') || PublishedReports.PageFor(report) is null)
            {
                await Refuse(context, null, "unknown report"); return;
            }

            var desktop = context.RequestServices.GetRequiredService<CmsDesktopStore>();
            var (on, settings, secret) = await desktop.CurrentAsync();
            var allowed = on && secret is not null && settings.Reports.Any(r => r.Slug.Equals(report, StringComparison.OrdinalIgnoreCase));
            var protector = context.RequestServices.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("Kpi.Web.CmsDesktop.Session.v1").ToTimeLimitedDataProtector();

            if (HttpMethods.IsPost(context.Request.Method))
            {
                if (!allowed) { await Refuse(context, report, "integration off or report not ticked"); return; }
                if (!context.Request.HasFormContentType) { await Refuse(context, report, "no ticket"); return; }
                var form = await context.Request.ReadFormAsync();
                string user = form["user"].ToString().Trim(), nonce = form["nonce"].ToString(), sig = form["sig"].ToString().Trim().ToLowerInvariant();
                if (user.Length is 0 or > 100 || nonce.Length is < 16 or > 100 || sig.Length != 64
                    || !long.TryParse(form["issued"], NumberStyles.None, CultureInfo.InvariantCulture, out var issued))
                {
                    await Refuse(context, report, "malformed ticket"); return;
                }
                var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(issued);
                var expected = CmsDesktopStore.Sign(secret!, report.ToLowerInvariant(), user, issued, nonce);
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(sig)))
                {
                    await Refuse(context, report, "bad signature"); return;
                }
                if (age > TicketWindow || age < -TicketWindow) { await Refuse(context, report, $"ticket {age.TotalSeconds:0} s old"); return; }
                Prune();
                if (!Seen.TryAdd(nonce, DateTime.UtcNow)) { await Refuse(context, report, "ticket used before"); return; }

                var session = new Session(report.ToLowerInvariant(), user, settings.Generation);
                context.Response.Cookies.Append(CookieName(report), protector.Protect(JsonSerializer.Serialize(session), SessionLifetime),
                    new CookieOptions
                    {
                        HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Lax,
                        Path = "/embed/" + report.ToLowerInvariant(), MaxAge = SessionLifetime, IsEssential = true
                    });
                await AuditOpenAsync(context, user, report);
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Redirect("/embed/" + report.ToLowerInvariant());   // 302: the browser GETs the page
                return;
            }

            if (!(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
            {
                await Refuse(context, report, "method"); return;
            }
            Session? s = null;
            try
            {
                var cookie = context.Request.Cookies[CookieName(report)];
                if (cookie is not null) s = JsonSerializer.Deserialize<Session>(protector.Unprotect(cookie));
            }
            catch (Exception) { s = null; }   // expired, or from an old key ring
            if (!allowed || s is null || s.Generation != settings.Generation
                || !s.Report.Equals(report, StringComparison.OrdinalIgnoreCase))
            {
                await Refuse(context, report, s is null ? "no session (open it from CMS)" : "session no longer valid"); return;
            }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            await next();
        });

    private static void Prune()
    {
        var cutoff = DateTime.UtcNow - TicketWindow - TicketWindow - TimeSpan.FromMinutes(1);
        foreach (var (k, at) in Seen) if (at < cutoff) Seen.TryRemove(k, out _);
    }

    private static async Task AuditOpenAsync(HttpContext context, string user, string report)
    {
        var key = "open|" + user + "|" + report;
        if (LastAudit.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromHours(1)) return;
        LastAudit[key] = DateTime.UtcNow;
        try
        {
            await using var db = await context.RequestServices.GetRequiredService<IDbContextFactory<KpiDbContext>>().CreateDbContextAsync();
            AuditLog.Add(db, AuditEvents.CmsDesktopOpened, null, null, $"CMS user {user} opened {report} (at most one line an hour each)", AuditLog.Ip(context));
            await db.SaveChangesAsync();
        }
        catch { /* the audit is a record, not a gate */ }
    }

    private static async Task Refuse(HttpContext context, string? report, string why)
    {
        var ip = AuditLog.Ip(context) ?? "?";
        var key = "refused|" + ip;
        if (!LastAudit.TryGetValue(key, out var last) || DateTime.UtcNow - last >= TimeSpan.FromHours(1))
        {
            LastAudit[key] = DateTime.UtcNow;
            try
            {
                await using var db = await context.RequestServices.GetRequiredService<IDbContextFactory<KpiDbContext>>().CreateDbContextAsync();
                AuditLog.Add(db, AuditEvents.CmsDesktopRefused, null, null, $"/embed/{report ?? "?"} refused: {why} (at most one line an hour per address)", ip);
                await db.SaveChangesAsync();
            }
            catch { }
        }
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(Unavailable);
    }
}
