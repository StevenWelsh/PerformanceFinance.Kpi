using System.Text.Json;
using Kpi.Data;
using Kpi.Data.Auth;
using Kpi.Web.Account;
using Kpi.Web.Integrations;
using Microsoft.EntityFrameworkCore;

namespace Kpi.Web.Data;

/// <summary>The copy's schedule: a Cfg.Integration row, provider "CopySchedule" (no table of its own).</summary>
public sealed class CopyScheduleSettings
{
    /// <summary>How often the site checks the office for new figures (one of <see cref="KpiCopyControl.EveryChoices"/>).</summary>
    public int EveryMinutes { get; set; } = 2;

    public bool Paused { get; set; }
    public DateTime? PausedUtc { get; set; }
    public string? PausedBy { get; set; }
    public string? PausedReason { get; set; }

    /// <summary>When it was last resumed: the alerts give the first copy afterwards a few minutes to land.</summary>
    public DateTime? ResumedUtc { get; set; }
}

/// <summary>The last Copy now: who, when, and how it went (in memory - Data sync shows it).</summary>
public sealed record ManualCopy(string By, DateTime RequestedUtc, DateTime? FinishedUtc = null, bool? Ok = null, string? Result = null);

/// <summary>
/// Controls for the site's copy (<see cref="KpiPull"/>) on Settings > Data sync (29 Oct 2026, Steve, after a day of
/// failing copies): PAUSE it, change how often it CHECKS the office (2, 5, 10, 15 or 30 minutes), and COPY NOW - which
/// copies even when the office has nothing newer (the point, after a fix, is to see a copy work), and runs while paused.
/// Pausing and the interval are kept in the database (a restart keeps them); what the copy is doing right now and the last
/// Copy now are in memory. Every change and every Copy now is audited.
/// </summary>
public sealed class KpiCopyControl(IDbContextFactory<KpiDbContext> dbs, IntegrationStore store, ILogger<KpiCopyControl> log)
{
    public const string Provider = "CopySchedule";
    public static readonly int[] EveryChoices = [2, 5, 10, 15, 30];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Lock _gate = new();
    private CopyScheduleSettings _last = new();

    /// <summary>Raised when the schedule, the running copy or a Copy now changes, so an open Data sync redraws.</summary>
    public event Action? Changed;

    public bool Running { get; private set; }
    public DateTime? RunningSinceUtc { get; private set; }
    public DateTime? NextCheckUtc { get; private set; }
    /// <summary>A Copy now asked for and not started yet (it waits for a running copy to finish).</summary>
    public string? PendingBy { get; private set; }
    public ManualCopy? LastManual { get; private set; }

    /// <summary>The schedule; the last one read (or the default) if the database can't be reached. Never throws.</summary>
    public async Task<CopyScheduleSettings> GetAsync()
    {
        try
        {
            var s = IntegrationStore.ReadSettings<CopyScheduleSettings>(await store.GetAsync(Provider)) ?? new CopyScheduleSettings();
            if (!EveryChoices.Contains(s.EveryMinutes)) s.EveryMinutes = 2;
            return _last = s;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Copy schedule couldn't be read; using the last one");
            return _last;
        }
    }

    public async Task SetEveryAsync(KpiUser actor, int minutes)
    {
        if (!EveryChoices.Contains(minutes)) throw new ArgumentOutOfRangeException(nameof(minutes));
        await UpdateAsync(s => s.EveryMinutes = minutes);
        await store.AuditAsync(actor, AuditEvents.IntegrationChanged, $"Data sync: checks the office for new figures every {minutes} minutes");
        Wake();   // the next check is at the new interval from now
    }

    public async Task PauseAsync(KpiUser actor, string? reason)
    {
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(reason.Trim().Length, 200)];
        await UpdateAsync(s => { s.Paused = true; s.PausedUtc = DateTime.UtcNow; s.PausedBy = AuditLog.NameOf(actor); s.PausedReason = reason; });
        await store.AuditAsync(actor, AuditEvents.IntegrationChanged, "Data sync: copying paused" + (reason is null ? "" : $" ({reason})"));
        Changed?.Invoke();
    }

    public async Task ResumeAsync(KpiUser actor)
    {
        await UpdateAsync(s => { s.Paused = false; s.PausedUtc = null; s.PausedBy = null; s.PausedReason = null; s.ResumedUtc = DateTime.UtcNow; });
        await store.AuditAsync(actor, AuditEvents.IntegrationChanged, "Data sync: copying resumed");
        Wake();   // check straight away
    }

    /// <summary>Asks for a copy now. False when one is already asked for and waiting.</summary>
    public async Task<bool> CopyNowAsync(KpiUser actor)
    {
        var by = AuditLog.NameOf(actor);
        lock (_gate)
        {
            if (PendingBy is not null) return false;
            PendingBy = by;
        }
        await store.AuditAsync(actor, AuditEvents.IntegrationChanged, "Data sync: Copy now");
        Wake();
        return true;
    }

    // ---- for KpiPull ----

    /// <summary>The Copy now waiting to run (and clears it), or null.</summary>
    internal string? TakeRequest()
    {
        lock (_gate)
        {
            var by = PendingBy;
            PendingBy = null;
            if (by is not null) LastManual = new ManualCopy(by, DateTime.UtcNow);
            return by;
        }
    }

    internal void Started()
    {
        Running = true; RunningSinceUtc = DateTime.UtcNow; NextCheckUtc = null;
        Changed?.Invoke();
    }

    internal void Finished(bool manual, bool ok, string result)
    {
        Running = false; RunningSinceUtc = null;
        if (manual && LastManual is { } m) LastManual = m with { FinishedUtc = DateTime.UtcNow, Ok = ok, Result = result };
        Changed?.Invoke();
    }

    /// <summary>Waits until the next check is due, or until woken (Copy now, resume, a new interval).</summary>
    internal async Task WaitAsync(TimeSpan every, CancellationToken ct)
    {
        NextCheckUtc = DateTime.UtcNow + every;
        Changed?.Invoke();
        await _wake.WaitAsync(every, ct);
    }

    private void Wake()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { /* already woken */ }
        Changed?.Invoke();
    }

    private async Task UpdateAsync(Action<CopyScheduleSettings> change)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var row = await db.Integrations.SingleOrDefaultAsync(i => i.Provider == Provider);
        if (row is null)
        {
            await store.GetAsync(Provider);   // makes the empty row
            row = await db.Integrations.SingleAsync(i => i.Provider == Provider);
        }
        var s = IntegrationStore.ReadSettings<CopyScheduleSettings>(row) ?? new CopyScheduleSettings();
        change(s);
        row.SettingsJson = JsonSerializer.Serialize(s, Json);
        row.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        _last = s;
    }
}
