using Kpi.Data;
using Kpi.Data.Auth;
using Kpi.Web.Account;
using Kpi.Web.Data;
using Kpi.Web.Shire;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kpi.Web.Scheduling;

/// <summary>Every task the site runs on a clock (3 Nov 2026, Steve: "everything that is scheduled belongs in there"). Not
/// here: the report pre-load (KpiWarmer), which each copy of the site runs for its own memory, and Report activity's writer,
/// which saves clicks as they come.</summary>
public static class Jobs
{
    public const string CmsCopyKey = "cms-copy";
    public const string ShireCopyKey = "shire-copy";
    public const string AlertsKey = "alerts";

    public static IServiceCollection AddScheduledTasks(this IServiceCollection services)
    {
        services.AddSingleton<IScheduledJob, CmsCopyJob>();
        services.AddSingleton<IScheduledJob, ShireCopyJob>();
        services.AddSingleton<IScheduledJob, AlertsJob>();
        services.AddSingleton<IScheduledJob, HistoryPruneJob>();
        services.AddSingleton<IScheduledJob, ExpireSetupsJob>();
        services.AddSingleton<ScheduledTaskService>();
        services.AddSingleton<OfficeJobsStore>();
        services.AddSingleton<SchedulerRunner>();
        services.AddHostedService(sp => sp.GetRequiredService<SchedulerRunner>());
        return services;
    }
}

/// <summary>The figures from the office's KPI_Export, and the office jobs' status (<see cref="KpiPull"/>).</summary>
public sealed class CmsCopyJob(KpiPull pull) : IScheduledJob
{
    public string Key => Jobs.CmsCopyKey;
    public string Name => "Collect figures from the office";
    public string Description => "Checks the two office jobs above - their status and schedules - and copies their figures when there's something new.";
    public string Group => "Figures";
    public TaskSchedule DefaultSchedule => TaskSchedule.Repeating(2);
    public TimeSpan MinimumRepeat => TimeSpan.FromMinutes(2);
    public TimeSpan Timeout => TimeSpan.FromMinutes(20);
    public Task<string> RunAsync(ScheduledRunContext run, CancellationToken ct) => pull.RunAsync(force: run.Manual, ct);
}

/// <summary>Shire's won deals from its Salesforce (<see cref="ShirePull"/>). Shire:Copy can run it on the staging slot.</summary>
public sealed class ShireCopyJob(ShirePull pull) : IScheduledJob
{
    public string Key => Jobs.ShireCopyKey;
    public string Name => "Copy Shire from Salesforce";
    public string Description => "Shire's won deals, when anything in Salesforce has changed.";
    public string Group => "Mobberley (Shire)";
    public TaskSchedule DefaultSchedule => TaskSchedule.Repeating(15);
    public TimeSpan MinimumRepeat => TimeSpan.FromMinutes(5);
    public TimeSpan Timeout => TimeSpan.FromMinutes(10);
    public string? RunsHereSetting => "Shire:Copy";
    public Task<string> RunAsync(ScheduledRunContext run, CancellationToken ct) => pull.RunAsync(force: run.Manual, ct);
}

/// <summary>Failing tasks and stale figures, emailed (<see cref="SyncAlerts"/>).</summary>
public sealed class AlertsJob(SyncAlerts alerts) : IScheduledJob
{
    public string Key => Jobs.AlertsKey;
    public string Name => "Check for problems and email alerts";
    public string Description => "Failing tasks, figures that stopped arriving, old Bad Debt & Exposure.";
    public string Group => "Upkeep";
    public TaskSchedule DefaultSchedule => TaskSchedule.Repeating(2);
    public TimeSpan MinimumRepeat => TimeSpan.FromMinutes(2);
    public TimeSpan Timeout => TimeSpan.FromMinutes(5);
    public Task<string> RunAsync(ScheduledRunContext run, CancellationToken ct) => alerts.CheckAsync();
}

/// <summary>The audit trail, report views and report activity kept to <see cref="HistoryPruner.Months"/> months
/// (Auth.spPruneHistory - through ownership chaining, so the site's DENY on changing audit lines still stands).</summary>
public sealed class HistoryPruneJob(IDbContextFactory<KpiDbContext> dbs) : IScheduledJob
{
    public string Key => "history-prune";
    public string Name => "Tidy old history";
    public string Description => $"Deletes audit lines, report views and report activity over {HistoryPruner.Months} months old.";
    public string Group => "Upkeep";
    public TaskSchedule DefaultSchedule => TaskSchedule.DailyAt(2, 30);
    public TimeSpan MinimumRepeat => TimeSpan.FromMinutes(60);
    public TimeSpan Timeout => TimeSpan.FromMinutes(10);

    public async Task<string> RunAsync(ScheduledRunContext run, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlRawAsync("EXEC Auth.spPruneHistory @Months = {0}", [HistoryPruner.Months], ct);
        return "Done.";
    }
}

/// <summary>Mobile setups nobody finished within the hour (<see cref="AccountSetup.ExpireUnfinishedAsync"/>).</summary>
public sealed class ExpireSetupsJob : IScheduledJob
{
    public string Key => "expire-setups";
    public string Name => "Close expired account setups";
    public string Description => "Closes mobile setups not finished within an hour.";
    public string Group => "Upkeep";
    public TaskSchedule DefaultSchedule => TaskSchedule.Repeating(15);
    public TimeSpan MinimumRepeat => TimeSpan.FromMinutes(5);
    public TimeSpan Timeout => TimeSpan.FromMinutes(5);

    public async Task<string> RunAsync(ScheduledRunContext run, CancellationToken ct)
    {
        var n = await new AccountSetup(run.Services.GetRequiredService<KpiDbContext>(),
            run.Services.GetRequiredService<UserManager<KpiUser>>()).ExpireUnfinishedAsync();
        return n == 0 ? "None to close." : $"Closed {n}.";
    }
}
