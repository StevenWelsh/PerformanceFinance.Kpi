using System.Diagnostics;
using Kpi.Data;
using Kpi.Data.Auth;
using Kpi.Data.Cfg;
using Kpi.Web.Account;
using Microsoft.EntityFrameworkCore;

namespace Kpi.Web.Scheduling;

/// <summary>
/// THE SCHEDULER (SHS-Web's SchedulerRunner, copied 3 Nov 2026). Every half minute it looks for tasks that are not paused
/// and are due, claims each with a lease (Cfg.ScheduledTask - shared by both deployment slots, so only one copy of the site
/// runs a task at a time), runs it with a time limit, and records the outcome on the task's row and as a Cfg.ScheduledTaskRun.
/// Then it works out the next run from the task's schedule. A missed run runs when the site is back, or is skipped; a failed
/// scheduled run may be tried again after a wait. Run now goes through the same claim, so it never overlaps a scheduled run.
///
/// Tasks run on their schedule only where Kpi:BackgroundWork allows (false on the staging slot), or a job's own setting
/// (Shire:Copy). Its first look at the database, as the site starts, is also the site's first connection - the slow one in
/// Azure - so it is rarely a person who pays for it. Failing tasks are emailed by the alerts task (SyncAlerts).
/// </summary>
public sealed class SchedulerRunner(IDbContextFactory<KpiDbContext> dbs, IServiceProvider services, IEnumerable<IScheduledJob> jobs,
                                    ScheduledTaskService tasks, IConfiguration config, ILogger<SchedulerRunner> log)
    : BackgroundService
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LeaseMargin = TimeSpan.FromMinutes(2);
    // a due time this far past counts as MISSED rather than merely picked up at the next check
    private static readonly TimeSpan MissedAfter = TimeSpan.FromMinutes(5);

    private readonly IReadOnlyList<IScheduledJob> _jobs = jobs.ToList();
    private readonly HashSet<string> _running = new();

    public IReadOnlyList<IScheduledJob> Jobs => _jobs;

    /// <summary>Which copy of the site this is, on its runs: the App Service instance and process.</summary>
    public static readonly string Instance =
        $"{(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID") is { Length: > 0 } id ? id[..Math.Min(12, id.Length)] : Environment.MachineName)}:{Environment.ProcessId}";

    /// <summary>True when this task doesn't run on its schedule in this copy of the site (the staging slot).</summary>
    public bool PausedHere(IScheduledJob job)
    {
        var all = config.GetValue("Kpi:BackgroundWork", true);
        return !(job.RunsHereSetting is { } setting ? config.GetValue(setting, all) : all);
    }

    /// <summary>Whether this copy of the site is running it now.</summary>
    public bool IsRunning(string key) { lock (_running) return _running.Contains(key); }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var db = await dbs.CreateDbContextAsync(stoppingToken);
            foreach (var job in _jobs) await ScheduledTaskService.EnsureRowAsync(db, job);
            await db.SaveChangesAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Scheduler: could not create the task rows; trying again at the next check");
        }

        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(CheckEvery);
        do
        {
            foreach (var job in _jobs)
            {
                if (stoppingToken.IsCancellationRequested) return;
                if (PausedHere(job)) continue;
                // each in its own task, so a long copy doesn't hold up the alerts
                if (IsRunning(job.Key)) continue;
                _ = Task.Run(async () =>
                {
                    try { await TryRunAsync(job, null, stoppingToken); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Scheduler: checking {Task} failed", job.Key); }
                }, stoppingToken);
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>Run now, from Settings > Scheduled tasks. Throws when the task is already running.</summary>
    public async Task<ScheduledTaskRun> RunNowAsync(string jobKey, KpiUser by)
    {
        var job = _jobs.FirstOrDefault(j => j.Key == jobKey) ?? throw new InvalidOperationException("That task no longer exists.");
        await using (var db = await dbs.CreateDbContextAsync())
        {
            await ScheduledTaskService.EnsureRowAsync(db, job);
            await db.SaveChangesAsync();
        }
        return await TryRunAsync(job, by, CancellationToken.None)
            ?? throw new InvalidOperationException($"\"{job.Name}\" is running already - wait for it to finish.");
    }

    // null when nothing ran: not due, paused, missed and skipped, or another copy of the site holds the lease
    private async Task<ScheduledTaskRun?> TryRunAsync(IScheduledJob job, KpiUser? manualBy, CancellationToken ct)
    {
        var manual = manualBy is not null;
        var now = DateTime.UtcNow;
        var until = now + job.Timeout + LeaseMargin;
        ScheduledRunTrigger trigger;
        DateTime? lastSucceeded;

        await using (var db = await dbs.CreateDbContextAsync(ct))
        {
            var state = await db.ScheduledTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Key == job.Key, ct);
            if (state is null) return null;
            if (!manual)
            {
                if (!state.Enabled || state.NextDueUtc is not { } due || due > now) return null;
                // missed, and set to skip missed runs: on to the next slot without running
                if (!state.RunMissed && now - due > MissedAfter && state.ConsecutiveFailures == 0)
                {
                    var next = TaskSchedule.Of(state).NextUtcAfter(now);
                    await db.ScheduledTasks.Where(t => t.Key == job.Key && t.NextDueUtc == due)
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.NextDueUtc, next), ct);
                    return null;
                }
            }
            trigger = manual ? ScheduledRunTrigger.Manual
                : state.ConsecutiveFailures > 0 && state.RetryCount > 0 ? ScheduledRunTrigger.Retry
                : ScheduledRunTrigger.Schedule;
            lastSucceeded = state.LastSucceededUtc;

            // THE CLAIM: the lease free (or lapsed), in one conditional UPDATE - whichever copy's lands first runs it
            var claimed = await db.ScheduledTasks
                .Where(t => t.Key == job.Key
                    && (t.LeaseUntilUtc == null || t.LeaseUntilUtc < now)
                    && (manual || (t.Enabled && t.NextDueUtc != null && t.NextDueUtc <= now)))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LeaseOwner, Instance).SetProperty(t => t.LeaseUntilUtc, until), ct);
            if (claimed == 0) return null;
        }

        lock (_running) _running.Add(job.Key);
        tasks.RaiseChanged();
        var started = DateTime.UtcNow;
        var clock = Stopwatch.StartNew();
        ScheduledRunOutcome outcome;
        string message;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(job.Timeout);
            try
            {
                await using var scope = services.CreateAsyncScope();
                message = await job.RunAsync(new ScheduledRunContext(scope.ServiceProvider, trigger, lastSucceeded), limit.Token);
                outcome = ScheduledRunOutcome.Succeeded;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                outcome = ScheduledRunOutcome.Failed;
                message = $"Stopped after {job.Timeout.TotalMinutes:0} minutes - it took too long.";
            }
            catch (OperationCanceledException)
            {
                outcome = ScheduledRunOutcome.Failed;
                message = "Stopped because the site was shutting down.";
            }
            catch (Exception ex)
            {
                outcome = ScheduledRunOutcome.Failed;
                message = ex.Message;
                log.LogWarning(ex, "Scheduler: {Task} failed", job.Key);
            }
        }
        finally { lock (_running) _running.Remove(job.Key); }
        clock.Stop();
        message = message.Length > 1000 ? message[..997] + "…" : message;
        var finished = DateTime.UtcNow;

        var run = new ScheduledTaskRun
        {
            TaskKey = job.Key,
            Trigger = trigger,
            TriggeredBy = manualBy is null ? null : AuditLog.NameOf(manualBy),
            StartedUtc = started,
            FinishedUtc = finished,
            DurationMs = (int)Math.Min(int.MaxValue, clock.ElapsedMilliseconds),
            Outcome = outcome,
            Message = message,
            Instance = Instance,
        };

        // recorded even when the site is stopping - CancellationToken.None, deliberately
        await using (var db = await dbs.CreateDbContextAsync(CancellationToken.None))
        {
            var row = await db.ScheduledTasks.FirstAsync(t => t.Key == job.Key, CancellationToken.None);
            row.LastStartedUtc = started;
            row.LastFinishedUtc = finished;
            row.LastOutcome = outcome;
            row.LastMessage = message;
            row.LastDurationMs = run.DurationMs;
            if (outcome == ScheduledRunOutcome.Succeeded) { row.ConsecutiveFailures = 0; row.LastSucceededUtc = started; }
            else row.ConsecutiveFailures++;

            // THE NEXT RUN: the next slot after it FINISHED (an overrun doesn't queue another behind it); a manual run leaves
            // the schedule alone unless it had fallen behind; a failed scheduled run may be tried again sooner
            var nextSlot = TaskSchedule.Of(row).NextUtcAfter(finished);
            if (manual)
            {
                if (row.NextDueUtc is null || row.NextDueUtc <= finished) row.NextDueUtc = nextSlot;
            }
            else if (outcome == ScheduledRunOutcome.Failed && row.RetryAfterMinutes is { } wait && row.ConsecutiveFailures <= row.RetryCount)
            {
                var retry = finished.AddMinutes(wait);
                row.NextDueUtc = nextSlot is { } s && s < retry ? s : retry;
            }
            else row.NextDueUtc = nextSlot;

            if (row.LeaseOwner == Instance) { row.LeaseOwner = null; row.LeaseUntilUtc = null; }
            db.ScheduledTaskRuns.Add(run);
            if (manualBy is not null)
                AuditLog.Add(db, AuditEvents.IntegrationChanged, manualBy, null,
                    $"Scheduled tasks: \"{job.Name}\" run now - {(outcome == ScheduledRunOutcome.Succeeded ? "worked" : "failed")}: {message}");
            await db.SaveChangesAsync(CancellationToken.None);

            var cutoff = DateTime.UtcNow.AddDays(-ScheduledTaskService.KeepRunsDays);
            await db.ScheduledTaskRuns.Where(r => r.TaskKey == job.Key && r.StartedUtc < cutoff).ExecuteDeleteAsync(CancellationToken.None);
        }
        tasks.RaiseChanged();
        return run;
    }
}
