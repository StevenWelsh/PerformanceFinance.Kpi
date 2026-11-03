using System.Net.Mail;
using System.Text.Json;
using Kpi.Data;
using Kpi.Data.Auth;
using Kpi.Data.Cfg;
using Kpi.Web.Account;
using Kpi.Web.Integrations;
using Kpi.Web.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace Kpi.Web.Data;

/// <summary>The alerts' settings and state: a Cfg.Integration row, provider "SyncAlerts" (no table of its own).</summary>
public sealed class SyncAlertSettings
{
    /// <summary>Who is emailed, as typed on Settings > Scheduled tasks; empty = every System Admin.</summary>
    public List<string> Addresses { get; set; } = new();

    /// <summary>Before 3 Nov 2026: the people ticked on Data sync (user ids) - read as their addresses until Addresses is saved.</summary>
    public List<string> RecipientIds { get; set; } = new();

    /// <summary>When each address was last sent an alert (lower case).</summary>
    public Dictionary<string, DateTime> LastSentUtc { get; set; } = new();

    /// <summary>The problem being alerted about now, or null when all is well. Kept here so a restart doesn't re-send.</summary>
    public OpenAlert? Open { get; set; }
}

public sealed class OpenAlert
{
    public DateTime SinceUtc { get; set; }
    public DateTime LastSentUtc { get; set; }
    public List<string> Problems { get; set; } = new();
}

/// <summary>
/// The task "Check for problems and email alerts" (19 Oct 2026, Steve: "alert me when sync fails to run, can't get data,
/// data seems not to be refreshing"; on Settings > Scheduled tasks since 3 Nov 2026). Each run looks for:
///   - a scheduled task failing: three runs in a row for one that repeats, the first for a once-a-day one (SHS-Web's rule);
///   - one of the office's two Agent jobs failed, disabled, or "Running" for over an hour (OfficeJobsStore);
///   - a copy paused on Scheduled tasks;
///   - the CMS copy switched off (no data source connected), on the live site;
///   - the figures overdue: no new copy for 45 minutes in office hours (Rpt.spDataStatus) - also catches the office's own
///     job stopping, when nothing fails but nothing new arrives;
///   - Bad Debt and Exposure over 26 hours old.
/// One email when a problem starts (or a new one joins it), a reminder every 4 hours in office hours while it lasts, and
/// one when it is over. The state is saved with the settings, so a restart doesn't send it all again. If the site itself
/// is down it can't send anything: Azure's "KPI site cannot reach its database" alert covers that.
/// </summary>
public sealed class SyncAlerts(IDbContextFactory<KpiDbContext> dbs, IntegrationStore store, Mailer mailer, KpiPullHealth pull,
                               KpiSessionFactory sessions, IServiceProvider services, IConfiguration config, IWebHostEnvironment env,
                               ILogger<SyncAlerts> log)
{
    public const string Provider = "SyncAlerts";
    public const int FailuresBeforeAlert = 3;
    public const int MaxAddresses = 20;
    public static readonly TimeSpan RemindEvery = TimeSpan.FromHours(4);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Raised when the alert state or the addresses change, so an open screen redraws.</summary>
    public event Action? Changed;

    public async Task<SyncAlertSettings> GetAsync() =>
        IntegrationStore.ReadSettings<SyncAlertSettings>(await store.GetAsync(Provider)) ?? new SyncAlertSettings();

    /// <summary>The addresses as saved - or, before any were, those of the people ticked on the old Data sync.</summary>
    public async Task<List<string>> AddressesAsync()
    {
        var s = await GetAsync();
        if (s.Addresses.Count > 0 || s.RecipientIds.Count == 0) return s.Addresses;
        await using var db = await dbs.CreateDbContextAsync();
        return await db.Users.AsNoTracking().Where(u => s.RecipientIds.Contains(u.Id) && u.DisabledUtc == null && u.Email != null)
            .Select(u => u.Email!).ToListAsync();
    }

    /// <summary>The System Admins' addresses: who is told while no addresses are set.</summary>
    public async Task<List<string>> SystemAdminAddressesAsync()
    {
        await using var db = await dbs.CreateDbContextAsync();
        return await (from u in db.Users.AsNoTracking()
                      join ur in db.UserRoles on u.Id equals ur.UserId
                      join r in db.Roles on ur.RoleId equals r.Id
                      where r.Name == KpiRoles.SystemAdmin && u.DisabledUtc == null && u.ActivatedUtc != null && u.Email != null
                      select u.Email!).OrderBy(e => e).ToListAsync();
    }

    /// <summary>Saves who is told (commas, semicolons or spaces between). Empty = the System Admins. Returns it as saved.</summary>
    public async Task<string> SaveAddressesAsync(KpiUser actor, string? text)
    {
        var addresses = (text ?? "").Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (addresses.Count > MaxAddresses) throw new InvalidOperationException($"Keep it to {MaxAddresses} addresses - a shared mailbox can reach more.");
        if (addresses.FirstOrDefault(a => !MailAddress.TryCreate(a, out var m) || m.Address != a) is { } bad)
            throw new InvalidOperationException($"\"{bad}\" isn't an email address.");
        await UpdateAsync(s => { s.Addresses = addresses; s.RecipientIds = new(); });
        var saved = string.Join(", ", addresses);
        await store.AuditAsync(actor, AuditEvents.IntegrationChanged,
            "Scheduled tasks: alerts go to " + (saved.Length == 0 ? "all the System Admins" : saved));
        Changed?.Invoke();
        return saved;
    }

    /// <summary>One run of the task: what happened, in a line for its History.</summary>
    public async Task<string> CheckAsync()
    {
        var problems = await ProblemsAsync();
        var settings = await GetAsync();
        var now = DateTime.UtcNow;

        if (problems.Count == 0)
        {
            if (settings.Open is null) return "All clear.";
            var since = settings.Open.SinceUtc;
            var told = await SendAsync(settings, "KPI site: working again",
                new[]
                {
                    $"Everything is working again. The problem started at {UkTime.Show(since, "HH:mm 'on' dd MMM")} and lasted {Duration(now - since)}.",
                });
            await UpdateAsync(s => s.Open = null);
            Changed?.Invoke();
            return $"All clear again; told {told}.";
        }

        var open = settings.Open;
        var isNew = open is null;
        var grew = open is not null && problems.Any(p => !open.Problems.Contains(p));
        var remind = open is not null && now - open.LastSentUtc >= RemindEvery && InOfficeHours(now);
        if (!isNew && !grew && !remind)
        {
            if (!open!.Problems.SequenceEqual(problems)) { await UpdateAsync(s => s.Open!.Problems = problems); Changed?.Invoke(); }
            return $"Still open (emailed {UkTime.Show(open.LastSentUtc, "HH:mm")}): {string.Join(" ", problems)}";
        }

        var startedUtc = open?.SinceUtc ?? now;
        var subject = isNew || grew ? "KPI site: something needs looking at" : "Reminder: KPI site still needs looking at";
        var lines = new List<string>
        {
            isNew ? "Something on the KPI site needs looking at:"
                  : $"Since {UkTime.Show(startedUtc, "HH:mm 'on' dd MMM")} ({Duration(now - startedUtc)}):"
        };
        lines.AddRange(problems.Select(p => "• " + p));
        var sent = await SendAsync(settings, subject, lines.ToArray());
        await UpdateAsync(s => s.Open = new OpenAlert { SinceUtc = startedUtc, LastSentUtc = now, Problems = problems });
        log.LogWarning("Alert sent ({Kind}): {Problems}", isNew ? "new" : grew ? "worse" : "reminder", string.Join(" | ", problems));
        Changed?.Invoke();
        return $"Emailed {sent}: {string.Join(" ", problems)}";
    }

    /// <summary>What is wrong right now, in plain words; empty when all is well.</summary>
    public async Task<List<string>> ProblemsAsync()
    {
        var problems = new List<string>();
        var jobs = services.GetServices<IScheduledJob>().ToList();
        Dictionary<string, ScheduledTask> rows;
        await using (var db = await dbs.CreateDbContextAsync())
            rows = await db.ScheduledTasks.AsNoTracking().ToDictionaryAsync(t => t.Key);

        foreach (var job in jobs.Where(j => j.Key != Jobs.AlertsKey))
        {
            if (!rows.TryGetValue(job.Key, out var t)) continue;
            if (!t.Enabled && job is CmsCopyJob or ShireCopyJob)
                problems.Add($"\"{job.Name}\" is paused.");
            var threshold = t.RepeatEveryMinutes is null ? 1 : FailuresBeforeAlert;
            if (t.ConsecutiveFailures >= threshold)
                problems.Add($"\"{job.Name}\" has failed{(t.ConsecutiveFailures == 1 ? "" : $" {t.ConsecutiveFailures} times in a row")}: {t.LastMessage}");
        }
        // the office's two Agent jobs, as the copy last read them (OfficeJobsStore)
        try
        {
            if ((await services.GetRequiredService<OfficeJobsStore>().GetAsync())?.Jobs is { } office)
                foreach (var (name, title, _) in OfficeJobsStore.Known)
                {
                    var j = office.Jobs.FirstOrDefault(x => x.Job == name);
                    if (j is null) continue;
                    if (j.Enabled == false) problems.Add($"The office job \"{title}\" is disabled in SQL Agent on PF-APP1.");
                    if (j.LastOutcome == "Failed") problems.Add($"The office job \"{title}\" failed at {UkTime.ShowShort(j.LastFinishedUtc)}: {j.LastError}");
                    if (j.LastOutcome == "Running" && DateTime.UtcNow - j.LastStartedUtc > OfficeJobsStore.StuckAfter)
                        problems.Add($"The office job \"{title}\" has said Running since {UkTime.ShowShort(j.LastStartedUtc)} - check SQL Agent on PF-APP1.");
                }
        }
        catch (Exception ex) { log.LogWarning(ex, "The office jobs' status couldn't be read for the alerts"); }

        if (pull.IsOn == false && env.IsProduction())
            problems.Add("Copying figures from CMS is switched off: no SQL data source is connected (Settings > Integrations).");

        // a paused CMS copy says so - not that the figures are overdue, which follows from it - and the first copy after a
        // resume gets a few minutes to land before the figures are called overdue
        var cms = rows.GetValueOrDefault(Jobs.CmsCopyKey);
        var paused = cms is { Enabled: false };
        var settling = paused || (cms?.UpdatedUtc is { } changed && DateTime.UtcNow - changed < TimeSpan.FromMinutes(15));
        try
        {
            using var db = sessions.Create();
            var s = db.Report<DataStatus>("Rpt.spDataStatus").SingleOrDefault();
            if (s?.FiguresStale == true && !settling)
                problems.Add($"No new figures since {UkTime.Show(s.LastFrequentLoadUtc, "HH:mm 'on' dd MMM")} - the office refreshes every 10 minutes in office hours.");
            if (s?.NightlyStale == true && !paused)
                problems.Add($"Bad Debt and Exposure were last copied {UkTime.Show(s.LastNightlyLoadUtc, "HH:mm 'on' dd MMM")}, over 26 hours ago.");
        }
        catch (Exception ex) { problems.Add("The KPI database couldn't be asked how fresh the figures are: " + ex.Message); }
        return problems;
    }

    // to the saved addresses, or every System Admin when none are saved; how many it went to
    private async Task<int> SendAsync(SyncAlertSettings settings, string subject, string[] lines)
    {
        var to = await AddressesAsync();
        if (to.Count == 0) to = await SystemAdminAddressesAsync();
        Dictionary<string, string> names;
        await using (var db = await dbs.CreateDbContextAsync())
            names = (await db.Users.AsNoTracking().Where(u => u.Email != null && to.Contains(u.Email)).ToListAsync())
                .GroupBy(u => u.Email!.ToLowerInvariant()).ToDictionary(g => g.Key, g => AuditLog.NameOf(g.First()));
        var link = SiteUrl() + "/settings?section=tasks";
        var sent = new List<string>();
        foreach (var address in to)
        {
            var outcome = await mailer.SyncAlertAsync(address, names.GetValueOrDefault(address.ToLowerInvariant(), "there"), subject, lines, link);
            if (outcome == MailOutcome.Sent) sent.Add(address.ToLowerInvariant());
            else log.LogWarning("Alert to {Email} not sent: {Outcome}", address, outcome);
        }
        if (sent.Count > 0)
            await UpdateAsync(s => { foreach (var a in sent) s.LastSentUtc[a] = DateTime.UtcNow; });
        return sent.Count;
    }

    // the address emailed links point at (no request to take it from): Kpi:SiteUrl, else the live site
    private string SiteUrl() => (config["Kpi:SiteUrl"] ?? "https://kpi.performancefinance.co.uk").TrimEnd('/');

    private async Task UpdateAsync(Action<SyncAlertSettings> change)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var row = await db.Integrations.SingleOrDefaultAsync(i => i.Provider == Provider);
        if (row is null)
        {
            await store.GetAsync(Provider);   // makes the empty row
            row = await db.Integrations.SingleAsync(i => i.Provider == Provider);
        }
        var s = IntegrationStore.ReadSettings<SyncAlertSettings>(row) ?? new SyncAlertSettings();
        change(s);
        row.SettingsJson = JsonSerializer.Serialize(s, Json);
        row.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    private static bool InOfficeHours(DateTime utc)
    {
        var uk = UkTime.FromUtc(utc);
        return uk.DayOfWeek != DayOfWeek.Sunday && uk.TimeOfDay >= TimeSpan.FromHours(7) && uk.TimeOfDay <= TimeSpan.FromHours(20);
    }

    private static string Duration(TimeSpan t) =>
        t.TotalMinutes < 90 ? $"{Math.Max(1, (int)t.TotalMinutes)} minutes"
        : t.TotalHours < 48 ? $"{t.TotalHours:0.#} hours" : $"{t.TotalDays:0} days";

    private sealed class DataStatus
    {
        public DateTime? LastFrequentLoadUtc { get; set; }
        public DateTime? LastNightlyLoadUtc { get; set; }
        public bool FiguresStale { get; set; }
        public bool NightlyStale { get; set; }
    }
}
