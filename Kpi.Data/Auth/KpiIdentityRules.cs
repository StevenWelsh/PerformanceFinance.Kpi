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
}
