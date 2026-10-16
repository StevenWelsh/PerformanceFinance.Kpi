using Kpi.Data;
using Kpi.Data.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kpi.Tools;

/// <summary>
/// "reset-sign-in": puts an account back to the state `seed` leaves it in - password kept, no mobile registered, not yet
/// activated - so its next sign-in goes straight to registering a mobile (the seeded account's one exception to "a
/// password never leads to setup"). For a System Admin who can't get in at all: the move to texted codes (Oct 2026), or a
/// lost phone with nobody else to reset it. Every session ends. Run by a person, against a database they administer.
/// </summary>
public static class ResetSignIn
{
    public static async Task<int> RunAsync(string connectionString, string[] args)
    {
        var email = args.SkipWhile(a => a != "--email").Skip(1).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(email))
        {
            Console.Error.WriteLine("Kpi.Tools reset-sign-in --email <address> [--azure]");
            return 1;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<KpiDbContext>(o => o.UseKpi(connectionString));
        services.AddIdentityCore<KpiUser>(KpiIdentityRules.Apply)
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<KpiDbContext>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<KpiUser>>();
        var db = scope.ServiceProvider.GetRequiredService<KpiDbContext>();

        var user = await users.FindByEmailAsync(email);
        if (user is null) { Console.Error.WriteLine($"No account for {email}."); return 1; }
        if (!await users.HasPasswordAsync(user)) { Console.Error.WriteLine($"{email} has no password yet - send their invitation again instead."); return 1; }

        user.TwoFactorEnabled = false;
        user.PhoneNumber = null;
        user.PhoneNumberConfirmed = false;
        user.IsSeeded = true;
        user.ActivatedUtc = null;
        user.DisabledUtc = null;
        user.SetupStartedUtc = null;
        user.SetupTokenHash = null;
        user.LockoutEnd = null;
        user.AccessFailedCount = 0;
        Check(await users.UpdateAsync(user));
        await users.RemoveAuthenticationTokenAsync(user, "[AspNetUserStore]", "AuthenticatorKey");   // the old authenticator, if any
        Check(await users.UpdateSecurityStampAsync(user));                                          // every session, every remembered browser

        db.AuditEntries.Add(new AuditEntry
        {
            OccurredUtc = DateTime.UtcNow, Event = AuditEvents.AuthenticatorReset,
            ActorName = $"Kpi.Tools on {Environment.MachineName}", SubjectUserId = user.Id, SubjectName = user.DisplayName,
            Detail = "Sign-in reset by Kpi.Tools reset-sign-in: the next sign-in registers a mobile"
        });
        await db.SaveChangesAsync();

        Console.WriteLine($"{user.DisplayName} <{email}>: reset. Sign in with the same password - you'll register your mobile");
        Console.WriteLine("straight away, and must finish within the hour.");
        return 0;
    }

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException(string.Join(" ", result.Errors.Select(e => e.Description)));
    }
}
