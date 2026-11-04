using Microsoft.AspNetCore.Identity;

namespace Kpi.Data.Auth;

/// <summary>The password and lockout rules, in one place: the site and Kpi.Tools seed both apply them.</summary>
public static class KpiIdentityRules
{
    public static void Apply(IdentityOptions o)
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 10;
        o.Password.RequireDigit = true;
        o.Password.RequireLowercase = true;
        o.Password.RequireUppercase = true;
        o.Password.RequireNonAlphanumeric = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        o.Lockout.AllowedForNewUsers = true;
    }

    /// <summary>One password rule as the screens list it; <c>Check</c> is what the page's script tests as the password
    /// is typed, to tick it off (js/passwordRules.js).</summary>
    public sealed record Rule(string Text, string Check);

    /// <summary>The password rules above, as the screens list them (4 Nov 2026, Steve - as DavidsonHR), so the list
    /// can't disagree with what the server refuses.</summary>
    public static readonly Rule[] PasswordRules =
    [
        new("At least 10 characters", "length:10"),
        new("An uppercase letter", "upper"),
        new("A lowercase letter", "lower"),
        new("A number", "digit"),
    ];
}
