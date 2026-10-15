using Kpi.Data;
using Kpi.Data.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kpi.Tools;

/// <summary>
/// "seed": the first account of a brand-new KPI database - a System Admin - plus the three roles. A new database has
/// no way in otherwise: invitations are emailed, and email (Resend) is connected by somebody already signed in. The
/// seeded account's first sign-in may set up its authenticator without an emailed link, once, within the hour.
/// The password is typed here by the person it belongs to, hidden, and never written anywhere but as Identity's hash.
/// </summary>
public static class Seed
{
    public static async Task<int> RunAsync(string connectionString, string[] args)
    {
        string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        var email = Arg("--email");
        var name = Arg("--name");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine("Kpi.Tools seed --email <address> --name \"<full name>\" [--remote]");
            return 1;
        }

        var target = new SqlConnectionStringBuilder(connectionString);
        var server = target.DataSource;
        var local = server.StartsWith('.') || server.StartsWith("(local)", StringComparison.OrdinalIgnoreCase)
                 || server.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
                 || server.StartsWith(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        if (!local && !args.Contains("--remote"))
        {
            Console.Error.WriteLine($"{server} / {target.InitialCatalog} is not on this machine. Add --remote to seed it.");
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
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<KpiUser>>();
        var db = scope.ServiceProvider.GetRequiredService<KpiDbContext>();

        foreach (var role in KpiRoles.All)
            if (!await roles.RoleExistsAsync(role))
            {
                Check(await roles.CreateAsync(new IdentityRole(role)));
                Console.WriteLine($"Role {role} created.");
            }

        if (await users.FindByEmailAsync(email) is not null)
        {
            Console.Error.WriteLine($"{email} already has an account. Use the site's Settings > Users instead.");
            return 1;
        }

        Console.WriteLine($"{server} / {target.InitialCatalog}: creating {name} <{email}> as System Admin.");
        Console.WriteLine("Passwords need at least 10 characters, with upper and lower case letters and a number.");
        var password = ReadHidden("Password: ");
        if (password != ReadHidden("Again:    "))
        {
            Console.Error.WriteLine("Those didn't match. Nothing was created.");
            return 1;
        }

        var user = new KpiUser
        {
            UserName = email, Email = email, EmailConfirmed = true, LockoutEnabled = true,
            DisplayName = name.Trim(), IsSeeded = true, CreatedUtc = DateTime.UtcNow
        };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            Console.Error.WriteLine(string.Join(" ", created.Errors.Select(e => e.Description)) + " Nothing was created.");
            return 1;
        }
        Check(await users.AddToRoleAsync(user, KpiRoles.SystemAdmin));
        db.AuditEntries.Add(new AuditEntry
        {
            OccurredUtc = DateTime.UtcNow, Event = AuditEvents.Invited, ActorName = "Kpi.Tools seed",
            SubjectUserId = user.Id, SubjectName = user.DisplayName,
            Detail = "First System Admin of a new database, created by Kpi.Tools seed; sets up an authenticator at first sign-in"
        });
        await db.SaveChangesAsync();

        Console.WriteLine("Done. Sign in on the site: you'll set up an authenticator app straight away, and have an hour to finish it.");
        return 0;
    }

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
            if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
        }
        Console.WriteLine();
        return new string(chars.ToArray());
    }

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException(string.Join(" ", result.Errors.Select(e => e.Description)));
    }
}
