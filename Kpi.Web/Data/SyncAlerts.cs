using System.Text.Json;
using Kpi.Data;
using Kpi.Data.Auth;
using Kpi.Data.Cfg;
using Kpi.Web.Account;
using Kpi.Web.Integrations;
using Microsoft.EntityFrameworkCore;

namespace Kpi.Web.Data;

/// <summary>The alerts' settings and state: a Cfg.Integration row, provider "SyncAlerts" (no table of its own).</summary>
public sealed class SyncAlertSettings
{
    /// <summary>Who is emailed (user ids), chosen in Settings > Data sync.</summary>
    public List<string> RecipientIds { get; set; } = new();

    /// <summary>When each recipient was last sent an alert (Settings > Users' envelope).</summary>
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
/// Emails the chosen people when the figures stop arriving (19 Oct 2026, Steve: "alert me when sync fails to run, can't
/// get data, data seems not to be refreshing"). Every two minutes it looks for:
///   - the site's copy failing for 10 minutes or more (a blip the next try fixes emails nobody) - the office or the data
///     source unreachable, a refused login, a failed publish, or a copy that can't start (another holding the lock);
///   - the copy switched off (no data source connected), on the live site;
///   - the figures overdue: no new copy for 45 minutes in office hours (Rpt.spDataStatus) - also catches the office's
///     own job stopping, when nothing fails but nothing new arrives;
///   - Bad Debt and Exposure over 26 hours old (the nightly run missed).
/// One email when a problem starts (or a new one joins it), a reminder every 4 hours in office hours while it lasts,
/// and one when it is over. The state is saved with the settings, so a restart doesn't send it all again. If the site
/// itself is down it can't send anything: Azure's "KPI site cannot reach its database" alert covers that.
/// </summary>
public sealed class SyncAlerts(IDbContextFactory<KpiDbContext> dbs, IntegrationStore store, Mailer mailer, KpiPullHealth pull,
                               KpiCopyControl copy, KpiSessionFactory sessions, IConfiguration config, IWebHostEnvironment env,
                               ILogger<SyncAlerts> log)
    : BackgroundService
{
    public const string Provider = "SyncAlerts";
    public static readonly TimeSpan FailingFor = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan RemindEvery = TimeSpan.FromHours(4);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Raised when the alert state or the recipients change, so an open Data sync / Users screen redraws.</summary>
    public event Action? Changed;

    public async Task<SyncAlertSettings> GetAsync() =>
        IntegrationStore.ReadSettings<SyncAlertSettings>(await store.GetAsync(Provider)) ?? new SyncAlertSettings();

    public async Task SaveRecipientsAsync(KpiUser actor, IReadOnlyCollection<string> userIds, IReadOnlyCollection<string> names)
    {
        await UpdateAsync(s => s.RecipientIds = userIds.Distinct().ToList());
        await store.AuditAsync(actor, AuditEvents.IntegrationChanged,
            "Data sync alerts go to: " + (names.Count == 0 ? "nobody" : string.Join(", ", names)));
        Changed?.Invoke();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }   // let the copy have its first go
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        do
        {
            try { await CheckAsync(); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogWarning(ex, "Data sync alert check failed; trying again in two minutes");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CheckAsync()
    {
        var problems = await ProblemsAsync();
        var settings = await GetAsync();
        var now = DateTime.UtcNow;

        if (problems.Count == 0)
        {
            if (settings.Open is null) return;
            var since = settings.Open.SinceUtc;
            await SendAsync(settings, "KPI figures are arriving again",
                new[]
                {
                    $"The KPI site's figures are up to date again. The problem started at {UkTime.Show(since, "HH:mm 'on' dd MMM")} " +
                    $"and lasted {Duration(now - since)}.",
                    "Settings > Data sync shows each copy."
                });
            await UpdateAsync(s => s.Open = null);
            log.LogInformation("Data sync alert closed (open since {Since:u})", since);
            Changed?.Invoke();
            return;
        }

        var open = settings.Open;
        var isNew = open is null;
        var grew = open is not null && problems.Any(p => !open.Problems.Contains(p));
        var remind = open is not null && now - open.LastSentUtc >= RemindEvery && InOfficeHours(now);
        if (!isNew && !grew && !remind)
        {
            if (!open!.Problems.SequenceEqual(problems)) { await UpdateAsync(s => s.Open!.Problems = problems); Changed?.Invoke(); }
            return;
        }

        var startedUtc = open?.SinceUtc ?? now;
        var subject = isNew || grew ? "KPI figures are not updating" : "Reminder: KPI figures are still not updating";
        var lines = new List<string>
        {
            isNew ? "The KPI site's figures have stopped updating:"
                  : $"The KPI site's figures have not updated since {UkTime.Show(startedUtc, "HH:mm 'on' dd MMM")} ({Duration(now - startedUtc)}):"
        };
        lines.AddRange(problems.Select(p => "• " + p));
        lines.Add("Settings > Data sync shows each copy and its error. You'll get one more email when it's working again"
                  + (isNew || grew ? ", and a reminder every 4 hours in office hours until then." : "."));
        await SendAsync(settings, subject, lines.ToArray());
        await UpdateAsync(s => s.Open = new OpenAlert { SinceUtc = startedUtc, LastSentUtc = now, Problems = problems });
        log.LogWarning("Data sync alert sent ({Kind}): {Problems}", isNew ? "new" : grew ? "worse" : "reminder", string.Join(" | ", problems));
        Changed?.Invoke();
    }

    /// <summary>What is wrong right now, in plain words; empty when all is well.</summary>
    public async Task<List<string>> ProblemsAsync()
    {
        var problems = new List<string>();
        if (pull.IsOn == false && env.IsProduction())
            problems.Add("The copy is switched off: no SQL data source is connected (Settings > Integrations).");
        if (pull.FailingSinceUtc is DateTime failing && DateTime.UtcNow - failing >= FailingFor)
            problems.Add($"The copy has been failing since {UkTime.Show(failing, "HH:mm")}: {pull.LastError}");

        // paused on Settings > Data sync (29 Oct 2026): say so - and not that figures are overdue, which follows from it -
        // and give the first copy after a resume a few minutes to land before calling the figures overdue
        var schedule = await copy.GetAsync();
        if (schedule.Paused)
            problems.Add($"Copying is paused, by {schedule.PausedBy ?? "someone"} at {UkTime.Show(schedule.PausedUtc, "HH:mm 'on' dd MMM")}" +
                         (schedule.PausedReason is null ? "" : $" ({schedule.PausedReason})") + ". Resume it on Settings > Data sync.");
        var settling = schedule.Paused || DateTime.UtcNow - schedule.ResumedUtc < TimeSpan.FromMinutes(15);

        try
        {
            using var db = sessions.Create();
            var s = db.Report<DataStatus>("Rpt.spDataStatus").SingleOrDefault();
            if (s?.FiguresStale == true && !settling)
                problems.Add($"No new figures since {UkTime.Show(s.LastFrequentLoadUtc, "HH:mm 'on' dd MMM")} - the office refreshes every 10 minutes in office hours.");
            if (s?.NightlyStale == true && !schedule.Paused)   // a resume's first copy may not bring them: no grace here
                problems.Add($"Bad Debt and Exposure were last copied {UkTime.Show(s.LastNightlyLoadUtc, "HH:mm 'on' dd MMM")} - the nightly run has been missed.");
        }
        catch (Exception ex) { problems.Add("The KPI database couldn't be asked how fresh the figures are: " + ex.Message); }
        return problems;
    }

    private async Task SendAsync(SyncAlertSettings settings, string subject, string[] lines)
    {
        if (settings.RecipientIds.Count == 0) { log.LogWarning("Data sync alert not emailed: nobody is set to receive alerts"); return; }
        await using var db = await dbs.CreateDbContextAsync();
        var people = await db.Users.AsNoTracking()
            .Where(u => settings.RecipientIds.Contains(u.Id) && u.DisabledUtc == null && u.ActivatedUtc != null && u.Email != null)
            .ToListAsync();
        var link = SiteUrl() + "/settings?section=sync";
        var sent = new List<string>();
        foreach (var u in people)
        {
            var outcome = await mailer.SyncAlertAsync(u.Email!, AuditLog.NameOf(u), subject, lines, link);
            if (outcome == MailOutcome.Sent) sent.Add(u.Id);
            else log.LogWarning("Data sync alert to {Email} not sent: {Outcome}", u.Email, outcome);
        }
        if (sent.Count > 0)
            await UpdateAsync(s => { foreach (var id in sent) s.LastSentUtc[id] = DateTime.UtcNow; });
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
