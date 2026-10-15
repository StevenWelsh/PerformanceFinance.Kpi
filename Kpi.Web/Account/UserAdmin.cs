using System.Security.Claims;
using Kpi.Data;
using Kpi.Data.Auth;
using Kpi.Web.Data.Kpis;
using Kpi.Web.Integrations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kpi.Web.Account;

/// <summary>
/// Settings > Users: the System Admin's alone (Oct 2026, Steve) - inviting, roles, report rights, disabling, and
/// resetting a lost phone. Every call checks the caller again and works in its own short-lived scope, never the
/// circuit's. A change of role or a disable ends that person's sessions (the security stamp rolls); a change of
/// report rights reaches them within a minute without signing them out.
/// </summary>
public sealed class UserAdmin(IServiceScopeFactory scopes, Mailer mailer, AccountLinks links)
{
    public const string InvitePurpose = "invite";
    public const string ReenrolPurpose = "reenrol";

    public sealed record UserRow(string Id, string Name, string Email, string Role, string Status,
                                 bool AllReports, IReadOnlyList<string> Reports, DateTime? LastSignInUtc, bool IsMe);

    /// <summary>What happened to the email; the link is for the System Admin to pass on when it wasn't sent.</summary>
    public sealed record LinkResult(MailOutcome Mail, string Link);

    public async Task<List<UserRow>> ListAsync(ClaimsPrincipal caller)
    {
        await using var s = await BeginAsync(caller);
        var roles = await (from ur in s.Db.UserRoles
                           join r in s.Db.Roles on ur.RoleId equals r.Id
                           select new { ur.UserId, r.Name }).ToListAsync();
        var users = await s.Db.Users.AsNoTracking().ToListAsync();
        return users
            .Select(u => new UserRow(u.Id, AuditLog.NameOf(u), u.Email ?? "",
                roles.FirstOrDefault(r => r.UserId == u.Id)?.Name ?? "", StatusOf(u),
                u.AllReports, Split(u.Reports), u.LastSignInUtc, u.Id == s.Actor.Id))
            .OrderBy(u => u.Name)
            .ToList();
    }

    public static string StatusOf(KpiUser u)
    {
        var now = DateTime.UtcNow;
        if (u.DisabledUtc is not null) return "Disabled";
        if (u.ActivatedUtc is null)
            return u.InviteExpiresUtc is { } exp && exp < now ? "Invite expired"
                 : u.SetupStartedUtc is not null ? "Setting up" : "Invited";
        return u.TwoFactorEnabled ? "Active" : "Authenticator reset";
    }

    public async Task<LinkResult> InviteAsync(ClaimsPrincipal caller, string name, string email, string role,
                                              bool allReports, IEnumerable<string> reports, string baseUrl)
    {
        name = name.Trim();
        email = email.Trim();
        if (name.Length == 0) throw new InvalidOperationException("Enter their name.");
        if (!email.Contains('@')) throw new InvalidOperationException("Enter their email address.");
        if (!KpiRoles.All.Contains(role)) throw new InvalidOperationException("Choose a role.");

        await using var s = await BeginAsync(caller);
        if (await s.Users.FindByEmailAsync(email) is not null)
            throw new InvalidOperationException("Somebody already has an account with that email address.");

        var user = new KpiUser
        {
            UserName = email, Email = email, EmailConfirmed = true, LockoutEnabled = true,
            DisplayName = name, AllReports = allReports, Reports = Join(reports),
            CreatedUtc = DateTime.UtcNow, InvitedUtc = DateTime.UtcNow, InvitedByUserId = s.Actor.Id,
            InviteExpiresUtc = DateTime.UtcNow + AccountLinks.InviteLifespan, InviteNonce = AccountLinks.NewNonce()
        };
        Check(await s.Users.CreateAsync(user));
        Check(await s.Users.AddToRoleAsync(user, role));
        AuditLog.Add(s.Db, AuditEvents.Invited, s.Actor, user, $"Invited as {KpiRoles.Label(role)}, seeing {Describe(role, allReports, user.Reports)}");
        await s.Db.SaveChangesAsync();
        return await SendInviteAsync(user, s.Actor, baseUrl);
    }

    /// <summary>A fresh link: the old one stops working.</summary>
    public async Task<LinkResult> ResendInviteAsync(ClaimsPrincipal caller, string userId, string baseUrl)
    {
        await using var s = await BeginAsync(caller);
        var user = await Find(s, userId);
        if (user.ActivatedUtc is not null) throw new InvalidOperationException("They have already accepted.");
        user.InviteNonce = AccountLinks.NewNonce();
        user.InviteExpiresUtc = DateTime.UtcNow + AccountLinks.InviteLifespan;
        user.InvitedUtc = DateTime.UtcNow;
        AuditLog.Add(s.Db, AuditEvents.Invited, s.Actor, user, "Invitation sent again; the earlier link no longer works");
        await s.Db.SaveChangesAsync();
        return await SendInviteAsync(user, s.Actor, baseUrl);
    }

    /// <summary>Removes an account that never got going. The audit trail keeps their name.</summary>
    public async Task WithdrawInviteAsync(ClaimsPrincipal caller, string userId)
    {
        await using var s = await BeginAsync(caller);
        var user = await Find(s, userId);
        if (user.ActivatedUtc is not null) throw new InvalidOperationException("They are already using the site: disable the account instead.");
        AuditLog.Add(s.Db, AuditEvents.InviteWithdrawn, s.Actor, user, "Invitation withdrawn and the account removed");
        await s.Db.SaveChangesAsync();
        Check(await s.Users.DeleteAsync(user));
    }

    public async Task UpdateAsync(ClaimsPrincipal caller, string userId, string name, string role, bool allReports, IEnumerable<string> reports)
    {
        name = name.Trim();
        if (name.Length == 0) throw new InvalidOperationException("Enter their name.");
        if (!KpiRoles.All.Contains(role)) throw new InvalidOperationException("Choose a role.");

        await using var s = await BeginAsync(caller);
        var user = await Find(s, userId);
        var current = (await s.Users.GetRolesAsync(user)).FirstOrDefault() ?? "";
        var changes = new List<string>();

        if (role != current)
        {
            if (user.Id == s.Actor.Id) throw new InvalidOperationException("You can't change your own role.");
            if (current == KpiRoles.SystemAdmin) await KeepOneSystemAdmin(s, user);
            if (current.Length > 0) Check(await s.Users.RemoveFromRoleAsync(user, current));
            Check(await s.Users.AddToRoleAsync(user, role));
            await s.Users.UpdateSecurityStampAsync(user);   // their sessions end: they sign in under the new role
            changes.Add($"role {KpiRoles.Label(current)} to {KpiRoles.Label(role)}");
        }
        if (name != user.DisplayName) changes.Add($"name {user.DisplayName} to {name}");
        var reportList = Join(reports);
        if (allReports != user.AllReports || reportList != user.Reports)
            changes.Add($"reports: {Describe(role, allReports, reportList)}");
        if (changes.Count == 0) return;

        user.DisplayName = name;
        user.AllReports = allReports;
        user.Reports = reportList;
        Check(await s.Users.UpdateAsync(user));
        AuditLog.Add(s.Db, AuditEvents.UserChanged, s.Actor, user, "Changed: " + string.Join(", ", changes));
        await s.Db.SaveChangesAsync();
    }

    public async Task SetDisabledAsync(ClaimsPrincipal caller, string userId, bool disabled)
    {
        await using var s = await BeginAsync(caller);
        var user = await Find(s, userId);
        if (user.Id == s.Actor.Id) throw new InvalidOperationException("You can't disable your own account.");
        if (disabled)
        {
            if ((await s.Users.GetRolesAsync(user)).Contains(KpiRoles.SystemAdmin)) await KeepOneSystemAdmin(s, user);
            user.DisabledUtc = DateTime.UtcNow;
            user.DisabledByUserId = s.Actor.Id;
            Check(await s.Users.UpdateAsync(user));
            await s.Users.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
            await s.Users.UpdateSecurityStampAsync(user);   // every session ends within a minute
        }
        else
        {
            user.DisabledUtc = null;
            user.DisabledByUserId = null;
            Check(await s.Users.UpdateAsync(user));
            await s.Users.SetLockoutEndDateAsync(user, null);
            await s.Users.ResetAccessFailedCountAsync(user);
        }
        AuditLog.Add(s.Db, disabled ? AuditEvents.UserDisabled : AuditEvents.UserEnabled, s.Actor, user,
            disabled ? "Disabled: can no longer sign in, and any open session ends" : "Enabled again");
        await s.Db.SaveChangesAsync();
    }

    /// <summary>A lost or new phone: their authenticator is cleared and they are emailed a link (a day) to set it
    /// up again with their password. Check who is asking before doing this - it is the one way round two-factor.</summary>
    public async Task<LinkResult> ResetTwoFactorAsync(ClaimsPrincipal caller, string userId, string howChecked, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(howChecked)) throw new InvalidOperationException("Say how you checked it was them.");
        await using var s = await BeginAsync(caller);
        var user = await Find(s, userId);
        if (user.Id == s.Actor.Id) throw new InvalidOperationException("Use My Account to move to a new phone.");
        if (user.ActivatedUtc is null) throw new InvalidOperationException("They haven't set one up yet: send the invitation again instead.");

        await s.Users.SetTwoFactorEnabledAsync(user, false);
        await s.Users.ResetAuthenticatorKeyAsync(user);
        user.ReenrolNonce = AccountLinks.NewNonce();
        user.ReenrolExpiresUtc = DateTime.UtcNow + AccountLinks.ReenrolLifespan;
        Check(await s.Users.UpdateAsync(user));
        await s.Users.UpdateSecurityStampAsync(user);
        AuditLog.Add(s.Db, AuditEvents.AuthenticatorReset, s.Actor, user, $"Authenticator reset; identity checked by: {howChecked.Trim()}");
        await s.Db.SaveChangesAsync();

        var link = $"{baseUrl}account/setup-authenticator?userId={Uri.EscapeDataString(user.Id)}&code={links.Make(user.Id, ReenrolPurpose, user.ReenrolNonce)}";
        return new LinkResult(await mailer.ReenrolAsync(user.Email!, user.DisplayName, link), link);
    }

    // ---------------------------------------------------------------------------------------------------------------
    private async Task<LinkResult> SendInviteAsync(KpiUser user, KpiUser actor, string baseUrl)
    {
        var link = $"{baseUrl}account/invitation?userId={Uri.EscapeDataString(user.Id)}&code={links.Make(user.Id, InvitePurpose, user.InviteNonce!)}";
        return new LinkResult(await mailer.InvitationAsync(user.Email!, user.DisplayName, AuditLog.NameOf(actor), link), link);
    }

    // only known reports, in menu order - what is stored is exactly what the menus can show
    private static string? Join(IEnumerable<string> reports)
    {
        var chosen = reports.ToHashSet();
        var list = string.Join(',', KpiReports.All.Where(r => chosen.Contains(r.Slug)).Select(r => r.Slug));
        return list.Length == 0 ? null : list;
    }

    private static IReadOnlyList<string> Split(string? reports) => (reports ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);

    private static string Describe(string role, bool allReports, string? reports) =>
        role == KpiRoles.SystemAdmin || allReports ? "every report"
        : Split(reports) is { Count: > 0 } list
            ? string.Join(", ", list.Select(slug => KpiReports.All.FirstOrDefault(r => r.Slug == slug)?.Label ?? slug))
            : "no reports";

    private static async Task KeepOneSystemAdmin(Session s, KpiUser leaving)
    {
        var admins = await s.Users.GetUsersInRoleAsync(KpiRoles.SystemAdmin);
        if (!admins.Any(a => a.Id != leaving.Id && a.DisabledUtc is null))
            throw new InvalidOperationException("There must always be one System Admin.");
    }

    private static async Task<KpiUser> Find(Session s, string userId) =>
        await s.Users.FindByIdAsync(userId) ?? throw new InvalidOperationException("That account no longer exists.");

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException(string.Join(" ", result.Errors.Select(e => e.Description)));
    }

    private async Task<Session> BeginAsync(ClaimsPrincipal caller)
    {
        var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<KpiUser>>();
        var actor = caller.IsInRole(KpiRoles.SystemAdmin) && caller.FindFirstValue(KpiClaims.TwoFactor) == "on"
            ? await users.GetUserAsync(caller) : null;
        if (actor is null || actor.DisabledUtc is not null)
        {
            await scope.DisposeAsync();
            throw new UnauthorizedAccessException("Only a System Admin can manage users.");
        }
        return new Session(scope, scope.ServiceProvider.GetRequiredService<KpiDbContext>(), users, actor);
    }

    private sealed record Session(AsyncServiceScope Scope, KpiDbContext Db, UserManager<KpiUser> Users, KpiUser Actor) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Scope.DisposeAsync();
    }
}
