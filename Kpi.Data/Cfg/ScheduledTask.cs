namespace Kpi.Data.Cfg;

/// <summary>
/// SCHEDULED TASKS (3 Nov 2026, Steve - SHS-Web's System settings > Scheduled tasks, copied; it replaced Settings > Data
/// sync and Shire sync). The tasks are defined in CODE (Kpi.Web/Scheduling, IScheduledJob); this row is each one's schedule
/// and latest state, created the first time the runner sees the job, with the job's default schedule. Cfg.ScheduledTaskRun
/// is the record of every run. Times are UTC; the schedule's are UK time.
/// </summary>
public class ScheduledTask
{
    /// <summary>The job's code-known key. Never renamed.</summary>
    public string Key { get; set; } = "";

    /// <summary>False = PAUSED. The schedule is kept, and Run now still works.</summary>
    public bool Enabled { get; set; } = true;

    // the schedule (TaskSchedule), UK times
    public int DaysMask { get; set; } = 127;
    public TimeOnly StartTime { get; set; }
    public int? RepeatEveryMinutes { get; set; }
    public TimeOnly? RepeatUntil { get; set; }

    /// <summary>A run missed while the site was down runs when it is back; false = skipped.</summary>
    public bool RunMissed { get; set; } = true;

    /// <summary>A failed scheduled run is tried again after this many minutes, at most RetryCount times.</summary>
    public int? RetryAfterMinutes { get; set; }
    public int RetryCount { get; set; }

    /// <summary>When the runner next picks it up, worked out from the schedule after each run.</summary>
    public DateTime? NextDueUtc { get; set; }

    // the latest run, for the list
    public DateTime? LastStartedUtc { get; set; }
    public DateTime? LastFinishedUtc { get; set; }
    public ScheduledRunOutcome? LastOutcome { get; set; }
    public string? LastMessage { get; set; }
    public int? LastDurationMs { get; set; }
    public DateTime? LastSucceededUtc { get; set; }
    public int ConsecutiveFailures { get; set; }

    /// <summary>The lease: only one copy of the site runs a task at a time (both deployment slots share this table).
    /// Claimed with one conditional UPDATE; it lapses on its own if the site dies mid-run.</summary>
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }

    public string? UpdatedByUserId { get; set; }
    public DateTime? UpdatedUtc { get; set; }
}

/// <summary>One run of a task, for its History (kept <c>KeepRunsDays</c> days).</summary>
public class ScheduledTaskRun
{
    public long Id { get; set; }
    public string TaskKey { get; set; } = "";
    public ScheduledRunTrigger Trigger { get; set; }
    /// <summary>Who pressed Run now (their name, as the audit keeps it); null for a scheduled run.</summary>
    public string? TriggeredBy { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    public int DurationMs { get; set; }
    public ScheduledRunOutcome Outcome { get; set; }
    public string? Message { get; set; }
    /// <summary>Which copy of the site ran it (App Service instance and process).</summary>
    public string? Instance { get; set; }
}

public enum ScheduledRunOutcome { Succeeded = 0, Failed = 1 }

public enum ScheduledRunTrigger { Schedule = 0, Manual = 1, Retry = 2 }
