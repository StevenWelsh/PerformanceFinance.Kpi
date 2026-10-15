using Kpi.Data;
using Kpi.Data.Auth;

namespace Kpi.Web.Account;

/// <summary>
/// Adds an audit line to the context, saved with the change it describes. Who did it and to whom are copied in
/// as names: no foreign keys, so a line still reads after that person has gone. Never a password, code or key.
/// </summary>
public static class AuditLog
{
    /// <summary>The person acting; null = the site itself (a scheduled task).</summary>
    public static void Add(KpiDbContext db, string auditEvent, KpiUser? actor, KpiUser? subject, string detail, string? ip = null) =>
        db.AuditEntries.Add(new AuditEntry
        {
            OccurredUtc = DateTime.UtcNow,
            Event = auditEvent,
            ActorUserId = actor?.Id,
            ActorName = actor is null ? "System" : NameOf(actor),
            SubjectUserId = subject?.Id,
            SubjectName = subject is null ? null : NameOf(subject),
            Detail = detail.Length > 1000 ? detail[..1000] : detail,
            IpAddress = ip
        });

    public static string NameOf(KpiUser u) => u.DisplayName.Length > 0 ? u.DisplayName : u.Email ?? u.UserName ?? u.Id;

    public static string? Ip(HttpContext? http) => http?.Connection.RemoteIpAddress?.ToString();
}
