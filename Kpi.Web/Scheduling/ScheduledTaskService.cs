using Kpi.Data;
using Kpi.Data.Auth;
using Kpi.Data.Cfg;
using Kpi.Web.Account;
using Microsoft.EntityFrameworkCore;

namespace Kpi.Web.Scheduling;

/// <summary>
/// Settings > Scheduled tasks (3 Nov 2026, Steve - SHS-Web's, copied): the list, pausing and resuming, the schedule and the
/// history. The page is the System Admin's alone (KpiPolicies.Settings); running a task is <see cref="SchedulerRunner"/>'s.
/// Every change is audited (Settings).
/// </summary>
public sealed class ScheduledTaskService(IDbContextFactory<KpiDbContext> dbs)
{
    public const int KeepRunsDays = 90;

    // the screen's choices, in minutes; a job's MinimumRepeat trims the short end
    public static readonly int[] RepeatChoices = [2, 5, 10, 15, 30, 60, 120, 180, 240, 360, 480, 720];
    public static readonly int[] RetryChoices = [5, 10, 15, 30, 60];

    public sealed record Row(IScheduledJob Job, ScheduledTask Task);

    /// <summary>Raised when a task is changed or has run, so an open Scheduled tasks screen redraws.</summary>
    public event Action? Changed;
    internal void RaiseChanged() => Changed?.Invoke();

    public async Task<List<Row>> ListAsync(IEnumerable<IScheduledJob> jobs)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var rows = await db.ScheduledTasks.AsNoTracking().ToDictionaryAsync(t => t.Key);
        return jobs.Select(j => new Row(j, rows.GetValueOrDefault(j.Key) ?? NewRow(j))).ToList();
    }

    public async Task<List<ScheduledTaskRun>> GetRunsAsync(string taskKey, int take = 50)
    {
        await using var db = await dbs.CreateDbContextAsync();
        return await db.ScheduledTaskRuns.AsNoTracking().Where(r => r.TaskKey == taskKey)
            .OrderByDescending(r => r.StartedUtc).Take(take).ToListAsync();
    }

    public async Task SetPausedAsync(KpiUser actor, IScheduledJob job, bool paused)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var row = await EnsureRowAsync(db, job);
        if (row.Enabled == !paused) return;
        row.Enabled = !paused;
        // resuming picks up at the next slot, not with a burst for everything it slept through
        if (!paused) row.NextDueUtc = TaskSchedule.Of(row).NextUtcAfter(DateTime.UtcNow);
        Stamp(row, actor);
        AuditLog.Add(db, AuditEvents.IntegrationChanged, actor, null, $"Scheduled tasks: \"{job.Name}\" {(paused ? "paused" : "resumed")}");
        await db.SaveChangesAsync();
        Changed?.Invoke();
    }

    public async Task SaveScheduleAsync(KpiUser actor, IScheduledJob job, TaskSchedule schedule, bool runMissed, int? retryAfterMinutes, int retryCount)
    {
        if (schedule.Problem(job.MinimumRepeat) is { } problem) throw new InvalidOperationException(problem);
        if (retryCount is < 0 or > 5) throw new InvalidOperationException("Retry up to 5 times at most.");
        if (retryCount > 0 && (retryAfterMinutes is null || !RetryChoices.Contains(retryAfterMinutes.Value)))
            throw new InvalidOperationException("Choose how long to wait before trying again.");

        await using var db = await dbs.CreateDbContextAsync();
        var row = await EnsureRowAsync(db, job);
        var before = Describe(row);
        schedule.ApplyTo(row);
        row.RunMissed = runMissed;
        row.RetryCount = retryCount;
        row.RetryAfterMinutes = retryCount > 0 ? retryAfterMinutes : null;
        row.NextDueUtc = schedule.NextUtcAfter(DateTime.UtcNow);
        Stamp(row, actor);
        var after = Describe(row);
        if (before != after)
            AuditLog.Add(db, AuditEvents.IntegrationChanged, actor, null, $"Scheduled tasks: \"{job.Name}\" - {after} (was {before})");
        await db.SaveChangesAsync();
        Changed?.Invoke();
    }

    /// <summary>The schedule and its options in one line, for the audit trail.</summary>
    public static string Describe(ScheduledTask t) =>
        TaskSchedule.Of(t).Describe()
        + (t.RunMissed ? "" : "; missed runs skipped")
        + (t.RetryCount > 0 ? $"; retries {t.RetryCount}× after {t.RetryAfterMinutes} min" : "");

    /// <summary>The first sight of a job creates its row with the job's defaults.</summary>
    public static async Task<ScheduledTask> EnsureRowAsync(KpiDbContext db, IScheduledJob job)
    {
        var row = await db.ScheduledTasks.FirstOrDefaultAsync(t => t.Key == job.Key);
        if (row is not null) return row;
        row = NewRow(job);
        db.ScheduledTasks.Add(row);
        return row;
    }

    private static ScheduledTask NewRow(IScheduledJob job)
    {
        var row = new ScheduledTask { Key = job.Key, Enabled = true, RunMissed = job.DefaultSchedule.RepeatEveryMinutes is null };
        job.DefaultSchedule.ApplyTo(row);
        row.NextDueUtc = job.DefaultSchedule.NextUtcAfter(DateTime.UtcNow);
        return row;
    }

    private static void Stamp(ScheduledTask row, KpiUser actor)
    {
        row.UpdatedByUserId = actor.Id;
        row.UpdatedUtc = DateTime.UtcNow;
    }
}
