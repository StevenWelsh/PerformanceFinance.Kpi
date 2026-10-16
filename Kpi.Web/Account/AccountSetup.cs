using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Kpi.Data;
using Kpi.Data.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kpi.Web.Account;

/// <summary>
/// THE ONE-HOUR SETUP WINDOW (DavidsonHR's security review H1). A correct password alone NEVER leads to setting up
/// a mobile for sign-in codes - otherwise whoever learnt a password could register their own phone. A mobile can be registered
/// only in a browser that has just followed one of our emailed links (an invitation, a password reset, a System
/// Admin's mobile reset), or has just proved the password inside a signed-in session (My Account, change mobile),
/// and only for an hour. A random value goes in that browser's cookie and its hash on the person's row.
/// </summary>
public sealed class AccountSetup(KpiDbContext db, UserManager<KpiUser> users)
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>Opens the window for this person and returns the raw value for the browser's cookie.</summary>
    public async Task<string> StartAsync(KpiUser user, string how)
    {
        var raw = Base64Url(RandomNumberGenerator.GetBytes(32));
        var row = await db.Users.SingleAsync(u => u.Id == user.Id);
        row.SetupTokenHash = Hash(raw);
        row.SetupStartedUtc = DateTime.UtcNow;
        AuditLog.Add(db, AuditEvents.SetupStarted, row, row, $"Started registering a mobile ({how}); it has an hour, in this browser");
        await db.SaveChangesAsync();
        return raw;
    }

    /// <summary>True when this browser's value matches a window that is still open.</summary>
    public async Task<bool> IsOpenAsync(string userId, string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return false;
        var row = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId);
        return row?.SetupTokenHash is { } hash && row.SetupStartedUtc is { } started
               && started + Window > DateTime.UtcNow
               && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(Hash(raw)));
    }

    /// <summary>The mobile is confirmed: the window closes, any re-enrolment link dies, and an invited
    /// account becomes an active one.</summary>
    public async Task FinishAsync(string userId)
    {
        var row = await db.Users.SingleAsync(u => u.Id == userId);
        row.SetupStartedUtc = null;
        row.SetupTokenHash = null;
        row.ReenrolNonce = null;
        row.ReenrolExpiresUtc = null;
        row.InviteNonce = null;
        row.ActivatedUtc ??= DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Closes every window more than an hour old that wasn't finished. Somebody who never finished setting up loses
    /// the password they chose, so nothing usable is left behind (their invitation works again until it expires).
    /// </summary>
    public async Task<int> ExpireUnfinishedAsync()
    {
        var cutoff = DateTime.UtcNow - Window;
        var stale = await db.Users.Where(u => u.SetupStartedUtc != null && u.SetupStartedUtc < cutoff).ToListAsync();
        foreach (var row in stale)
        {
            row.SetupStartedUtc = null;
            row.SetupTokenHash = null;
            if (row.TwoFactorEnabled) continue;
            var cleared = row.ActivatedUtc is null && await users.HasPasswordAsync(row);
            if (cleared) await users.RemovePasswordAsync(row);   // rolls the stamp: a half-set-up session ends
            else await users.UpdateSecurityStampAsync(row);
            AuditLog.Add(db, AuditEvents.SetupExpired, null, row, cleared
                ? "Didn't finish setting up within the hour, so the password they chose was cleared"
                : "Didn't finish registering their mobile within the hour");
        }
        await db.SaveChangesAsync();
        return stale.Count;
    }

    private static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The browser half of the setup window. Its own cookie, because the sign-in cookie is rebuilt every
/// minute by the security stamp check. HttpOnly, Secure, host-only, gone after the hour.</summary>
public static class SetupCookie
{
    public const string Name = "__Host-kpi-setup";

    public static void Write(HttpContext http, string raw) =>
        http.Response.Cookies.Append(Name, raw, new CookieOptions
        {
            HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/", IsEssential = true,
            Expires = DateTimeOffset.UtcNow + AccountSetup.Window
        });

    public static string? Read(HttpContext http) => http.Request.Cookies.TryGetValue(Name, out var v) ? v : null;

    public static void Delete(HttpContext http) =>
        http.Response.Cookies.Delete(Name, new CookieOptions { Secure = true, Path = "/", SameSite = SameSiteMode.Lax });
}

/// <summary>
/// The token behind an emailed account link (an invitation, "register your mobile again"): encrypted with this
/// site's key ring, holding when it was made, whose it is and its purpose - which carries the nonce on the person's
/// row, so a new nonce (resend) or none (withdrawn, used) kills the link. Not tied to the security stamp, so choosing
/// a password doesn't kill it.
/// </summary>
public sealed class AccountLinks(IDataProtectionProvider protection)
{
    public static readonly TimeSpan InviteLifespan = TimeSpan.FromDays(7);
    public static readonly TimeSpan ReenrolLifespan = TimeSpan.FromDays(1);

    private readonly IDataProtector _protector = protection.CreateProtector("Kpi.Web.AccountLinks.v1");

    public string Make(string userId, string purpose, string nonce)
    {
        var payload = string.Join('\n', DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture), userId, purpose, nonce);
        return AccountSetup.Base64Url(_protector.Protect(Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>True when the token is ours, for this person and purpose, carries this nonce and is in date.</summary>
    public bool Check(string? token, string userId, string purpose, string? nonce, TimeSpan lifespan)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(nonce)) return false;
        try
        {
            var b64 = token.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(b64))).Split('\n');
            if (parts.Length != 4 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) return false;
            return new DateTime(ticks, DateTimeKind.Utc) + lifespan > DateTime.UtcNow
                   && parts[1] == userId && parts[2] == purpose
                   && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(parts[3]), Encoding.UTF8.GetBytes(nonce));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return false; }
    }

    public static string NewNonce() => AccountSetup.Base64Url(RandomNumberGenerator.GetBytes(18));
}

/// <summary>
/// Every 15 minutes: closes setup windows nobody finished. Its first run, as the site starts, is also the site's first
/// database connection - the slow one in Azure (Kpi.Data.KpiRetryStrategy) - so it is rarely a person signing in who pays.
/// </summary>
public sealed class ExpireSetupsTask(IServiceScopeFactory scopes, ILogger<ExpireSetupsTask> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var n = await new AccountSetup(scope.ServiceProvider.GetRequiredService<KpiDbContext>(),
                    scope.ServiceProvider.GetRequiredService<UserManager<KpiUser>>()).ExpireUnfinishedAsync();
                if (n > 0) log.LogInformation("Closed {Count} unfinished mobile registration(s)", n);
            }
            catch (Exception ex) { log.LogError(ex, "Closing unfinished mobile registrations failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
