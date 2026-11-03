using System.Text.Json;
using Kpi.Copy;
using Kpi.Data;
using Kpi.Web.Integrations;
using Microsoft.EntityFrameworkCore;

namespace Kpi.Web.Scheduling;

/// <summary>What the copy last read about the office's jobs: a Cfg.Integration row, provider "OfficeJobs", so both
/// deployment slots and a restarted site show it.</summary>
public sealed class OfficeJobsSnapshot
{
    public DateTime CheckedUtc { get; set; }
    /// <summary>Null when the office's KPI_Export is version 3, which doesn't report its jobs.</summary>
    public OfficeJobs? Jobs { get; set; }
}

/// <summary>
/// THE OFFICE'S TWO SQL SERVER AGENT JOBS, READ ONLY (3 Nov 2026, Steve: "they should be in their own section, READ ONLY
/// ... we don't hardcode every 2 hrs, we check that too"). The jobs record each run and their own schedules in KPI_Export
/// (KpiCopy.spRunJob); the task "Collect figures from the office" reads them at every check and keeps them here; Settings >
/// Scheduled tasks shows them, and the alerts task reports one failing, disabled or stuck.
/// </summary>
public sealed class OfficeJobsStore(IDbContextFactory<KpiDbContext> dbs, IntegrationStore store)
{
    public const string Provider = "OfficeJobs";
    public const string RefreshJob = "KPI_Export - refresh";
    public const string NightlyJob = "KPI_Export - Bad Debt and Exposure";
    /// <summary>Its name until 3 Nov 2026 (the version 4 script renames it): read as the same job.</summary>
    private const string NightlyJobBefore = "KPI_Export - nightly";
    /// <summary>A job "Running" longer than this was probably stopped mid-run (a server restart).</summary>
    public static readonly TimeSpan StuckAfter = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>The two jobs in the order the screen lists them, with what each does.</summary>
    public static readonly IReadOnlyList<(string Job, string Name, string Description)> Known =
    [
        (NightlyJob, "Prepare Bad Debt & Exposure", "Works out Bad Debt and Exposure in CMS_Live, then refreshes the figures."),
        (RefreshJob, "Refresh the figures", "Copies CMS_Live's figures into KPI_Export, ready for this site to collect."),
    ];

    public async Task<OfficeJobsSnapshot?> GetAsync() => IntegrationStore.ReadSettings<OfficeJobsSnapshot>(await store.GetAsync(Provider));

    public async Task SaveAsync(OfficeJobs? jobs)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var row = await db.Integrations.SingleOrDefaultAsync(i => i.Provider == Provider);
        if (row is null)
        {
            await store.GetAsync(Provider);   // makes the empty row
            row = await db.Integrations.SingleAsync(i => i.Provider == Provider);
        }
        if (jobs is not null)   // the job's old name, before the script renamed it
            jobs = new OfficeJobs(jobs.Jobs.Select(j => j.Job == NightlyJobBefore ? j with { Job = NightlyJob } : j).ToList(),
                                  jobs.Schedules.Select(s => s.Job == NightlyJobBefore ? s with { Job = NightlyJob } : s).ToList());
        row.SettingsJson = JsonSerializer.Serialize(new OfficeJobsSnapshot { CheckedUtc = DateTime.UtcNow, Jobs = jobs }, Json);
        row.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>A schedule as the site's own (SQL Agent's daily and weekly ones); null for any other kind.</summary>
    public static TaskSchedule? AsTaskSchedule(OfficeSchedule s)
    {
        int days;
        if (s.FreqType == 4) days = TaskSchedule.AllDays;   // daily (every N days is taken as every day)
        else if (s.FreqType == 8) days = ((s.FreqInterval >> 1) & 63) | ((s.FreqInterval & 1) << 6);   // Agent: Sun 1, Mon 2 ... Sat 64
        else return null;
        var start = Time(s.StartTime);
        int? every = s.SubdayType switch
        {
            4 => s.SubdayInterval,
            8 => s.SubdayInterval * 60,
            2 => Math.Max(1, s.SubdayInterval / 60),
            _ => null,
        };
        TimeOnly? until = every is null || s.EndTime >= 235900 ? null : Time(s.EndTime);
        return new TaskSchedule(days, start, every, until);

        static TimeOnly Time(int hhmmss) => new(hhmmss / 10000 % 24, hhmmss / 100 % 100, hhmmss % 100);
    }

    /// <summary>A job's schedules in words, and its next run (the soonest of its schedules that are on).</summary>
    public static (string Text, DateTime? NextUtc) Describe(IEnumerable<OfficeSchedule> schedules)
    {
        var parts = new List<string>();
        DateTime? next = null;
        foreach (var s in schedules)
        {
            var ts = AsTaskSchedule(s);
            parts.Add((ts?.Describe() ?? s.Name) + (s.Enabled ? "" : " (off)"));
            if (s.Enabled && ts?.NextUtcAfter(DateTime.UtcNow) is { } n && (next is null || n < next)) next = n;
        }
        return (parts.Count == 0 ? "No schedule" : string.Join("; ", parts), next);
    }
}
