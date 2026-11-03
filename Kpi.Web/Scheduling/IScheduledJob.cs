using Kpi.Data.Cfg;

namespace Kpi.Web.Scheduling;

/// <summary>What a task is told about the run it is doing. <see cref="Manual"/> = Run now on Settings > Scheduled tasks.</summary>
public sealed record ScheduledRunContext(IServiceProvider Services, ScheduledRunTrigger Trigger, DateTime? LastSucceededUtc)
{
    public bool Manual => Trigger == ScheduledRunTrigger.Manual;
}

/// <summary>A task the scheduler runs (Jobs.cs), registered in DI as IScheduledJob.</summary>
public interface IScheduledJob
{
    /// <summary>Stable, code-known id - the Cfg.ScheduledTask key. Never renamed.</summary>
    string Key { get; }
    string Name { get; }
    string Description { get; }
    /// <summary>The heading it is listed under.</summary>
    string Group { get; }
    TaskSchedule DefaultSchedule { get; }
    /// <summary>The shortest repeat the screen allows.</summary>
    TimeSpan MinimumRepeat { get; }
    /// <summary>How long a run may take before it is cancelled and recorded as failed.</summary>
    TimeSpan Timeout { get; }
    /// <summary>A setting that decides whether it runs on its schedule in THIS copy of the site, instead of
    /// Kpi:BackgroundWork (false on the staging slot). Run now works either way.</summary>
    string? RunsHereSetting => null;

    /// <summary>Does the work and returns one line saying what happened. Throw to record a failure.</summary>
    Task<string> RunAsync(ScheduledRunContext run, CancellationToken ct);
}
