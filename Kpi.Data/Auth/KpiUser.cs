using Microsoft.AspNetCore.Identity;

namespace Kpi.Data.Auth;

/// <summary>
/// A KPI site login (Auth.Users): ASP.NET Identity's user, plus who they are, what they may see, and where their
/// account stands. Separate from CMS's users on purpose: the internet-facing site never shares a password with
/// the desktop.
/// </summary>
public class KpiUser : IdentityUser
{
    public string DisplayName { get; set; } = "";

    // ---- the reports a User may see (a System Admin sees everything) ----
    /// <summary>Every report, including any added later.</summary>
    public bool AllReports { get; set; }
    /// <summary>Otherwise these report slugs, comma separated (KpiReports in the site).</summary>
    public string? Reports { get; set; }

    // ---- the account's life ----
    public DateTime CreatedUtc { get; set; }

    /// <summary>Made by Kpi.Tools seed for a brand-new database: its first sign-in may set up an authenticator
    /// without an emailed link, once (there is no email until somebody signed in connects Resend).</summary>
    public bool IsSeeded { get; set; }

    public DateTime? InvitedUtc { get; set; }
    public string? InvitedByUserId { get; set; }
    public DateTime? InviteExpiresUtc { get; set; }
    /// <summary>Part of the invitation link's token: a new one (resend) kills the old link, none (withdrawn) kills all.</summary>
    public string? InviteNonce { get; set; }

    /// <summary>Set when the authenticator is first verified: the moment an account is usable.</summary>
    public DateTime? ActivatedUtc { get; set; }

    public DateTime? DisabledUtc { get; set; }
    public string? DisabledByUserId { get; set; }

    /// <summary>"Set up your authenticator again", after an administrator's reset: the link's nonce and expiry.</summary>
    public string? ReenrolNonce { get; set; }
    public DateTime? ReenrolExpiresUtc { get; set; }

    /// <summary>The one-hour setup window: the hash of the value in that browser's cookie, and when it opened.</summary>
    public string? SetupTokenHash { get; set; }
    public DateTime? SetupStartedUtc { get; set; }

    public DateTime? LastSignInUtc { get; set; }

    // ---- preferences ----
    /// <summary>Favourite report slugs, comma separated, in the order they were added.</summary>
    public string? FavouriteReports { get; set; }
    /// <summary>The phone menu's last chosen view: "favourites" | "all".</summary>
    public string? FavouritesMenuView { get; set; }
}

/// <summary>The two roles (Oct 2026, Steve: an Admin role became "a User with every report ticked"). One per person.</summary>
public static class KpiRoles
{
    /// <summary>Every report, and Settings: users, integrations, data sync, audit.</summary>
    public const string SystemAdmin = "SystemAdmin";
    /// <summary>The reports ticked for them in Settings > Users.</summary>
    public const string User = "User";

    public static readonly string[] All = { SystemAdmin, User };

    public static string Label(string? role) => role switch
    {
        SystemAdmin => "System Admin",
        User => "User",
        _ => "-"
    };
}
