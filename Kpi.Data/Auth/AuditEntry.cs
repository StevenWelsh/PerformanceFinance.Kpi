namespace Kpi.Data.Auth;

/// <summary>
/// One line of the audit trail (Auth.AuditEntry): sign-ins, refusals, invitations, account and integration changes.
/// APPEND ONLY - the context refuses to change or delete one, and the site's database role is denied it. No foreign
/// keys and names copied in, so a line still says who it was about after that person has gone.
/// </summary>
public class AuditEntry
{
    public long Id { get; set; }
    public DateTime OccurredUtc { get; set; }
    /// <summary>What happened, one of <see cref="AuditEvents"/>.</summary>
    public string Event { get; set; } = "";
    public string? ActorUserId { get; set; }
    public string ActorName { get; set; } = "";
    public string? SubjectUserId { get; set; }
    public string? SubjectName { get; set; }
    public string Detail { get; set; } = "";
    public string? IpAddress { get; set; }
}

public static class AuditEvents
{
    public const string SignIn = "SignIn";
    public const string SignInFailed = "SignInFailed";
    public const string LockedOut = "LockedOut";
    public const string SignOut = "SignOut";
    public const string SetupStarted = "SetupStarted";
    public const string AuthenticatorSetUp = "AuthenticatorSetUp";
    public const string AuthenticatorReset = "AuthenticatorReset";
    public const string SetupExpired = "SetupExpired";
    public const string PasswordChanged = "PasswordChanged";
    public const string PasswordReset = "PasswordReset";
    public const string PasswordResetRequested = "PasswordResetRequested";
    public const string Invited = "Invited";
    public const string InviteWithdrawn = "InviteWithdrawn";
    public const string InviteAccepted = "InviteAccepted";
    public const string UserChanged = "UserChanged";
    public const string UserDisabled = "UserDisabled";
    public const string UserEnabled = "UserEnabled";
    public const string IntegrationChanged = "IntegrationChanged";
}
