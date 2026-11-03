using System.Text.Json;
using Kpi.Data;
using Kpi.Data.Auth;
using Kpi.Web.Account;
using Kpi.Web.Data;
using Kpi.Web.Integrations;
using Microsoft.EntityFrameworkCore;

namespace Kpi.Web.Shire;

/// <summary>The Shire copy's schedule: a Cfg.Integration row, provider "ShireCopySchedule" (as the CMS copy's).</summary>
public sealed class ShireCopySchedule
{
    /// <summary>How often the site checks Salesforce for changes (one of <see cref="ShireCopyControl.EveryChoices"/>).</summary>
    public int EveryMinutes { get; set; } = ShireCopyControl.DefaultEvery;

    public bool Paused { get; set; }
    public DateTime? PausedUtc { get; set; }
    public string? PausedBy { get; set; }
    public string? PausedReason { get; set; }
}

/// <summary>
/// Settings > Shire sync's controls for <see cref="ShirePull"/> (3 Nov 2026, Steve: "an extra data sync item just for Shire
/// Salesforce"), as <see cref="KpiCopyControl"/> for the CMS copy: PAUSE, how often it CHECKS Salesforce, and COPY NOW (copies
/// even when nothing changed; runs while paused). The schedule is kept in the database; the running copy and the last Copy
/// now are in memory. Every change is audited.
/// </summary>
public sealed class ShireCopyControl(IDbContextFactory<KpiDbContext> dbs, IntegrationStore store, ILogger<ShireCopyControl> log)
{
    public const string Provider = "ShireCopySchedule";
    public const int DefaultEvery = 15;
    public static readonly int[] EveryChoices = [5, 10, 15, 30, 60];

    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Lock _gate = new();
    private ShireCopySchedule _last = new();

    /// <summary>Raised when the schedule, the running copy or a Copy now changes, so an open Shire sync redraws.</summary>
    public event Action? Changed;

    public bool Running { get; private set; }
    public DateTime? RunningSinceUtc { get; private set; }
    public DateTime? NextCheckUtc { get; private set; }
    public string? PendingBy { get; private set; }
    public ManualCopy? LastManual { get; private set; }
    /// <summary>What the last check did, in a sentence (in memory).</summary>
    public string? LastResult { get; private set; }

    /// <summary>The schedule; the last one read (or the default) if the database can't be reached. Never throws.</summary>
    public async Task<ShireCopySchedule> GetAsync()
    {
        try
        {
            var s = IntegrationStore.ReadSettings<ShireCopySchedule>(await store.GetAsync(Provider)) ?? new ShireCopySchedule();
            if (!EveryChoices.Contains(s.EveryMinutes)) s.EveryMinutes = DefaultEvery;
            return _last = s;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Shire copy schedule couldn't be read; using the last one");
            return _last;
        }
    }

    public async Task SetEveryAsync(KpiUser actor, int minutes)
    {
        if (!EveryChoices.Contains(minutes)) throw new ArgumentOutOfRangeException(nameof(minutes));
        await UpdateAsync(s => s.EveryMinutes = minutes);
        await store.AuditAsync(actor, AuditEvents.IntegrationChanged, $"Shire sync: checks Salesforce every {minutes} minutes");
        Wake();
    }

    public async Task PauseAsync(KpiUser actor, string? reason)
    {
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(reason.Trim().Length, 200)];
        await UpdateAsync(s => { s.Paused = true; s.PausedUtc = DateTime.UtcNow; s.PausedBy = AuditLog.NameOf(actor); s.PausedReason = reason; });
        await store.AuditAsync(actor, AuditEvents.IntegrationChanged, "Shire sync: copying paused" + (reason is null ? "" : $" ({reason})"));
        Changed?.Invoke();
    }

    public async Task ResumeAsync(KpiUser actor)
    {
        await UpdateAsync(s => { s.Paused = false; s.PausedUtc = null; s.PausedBy = null; s.PausedReason = null; });
        await store.AuditAsync(actor, AuditEvents.IntegrationChanged, "Shire sync: copying resumed");
        Wake();
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
        await store.AuditAsync(actor, AuditEvents.IntegrationChanged, "Shire sync: Copy now");
        Wake();
        return true;
    }

    // ---- for ShirePull ----

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
        Running = false; RunningSinceUtc = null; LastResult = result;
        if (manual && LastManual is { } m) LastManual = m with { FinishedUtc = DateTime.UtcNow, Ok = ok, Result = result };
        Changed?.Invoke();
    }

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

    private async Task UpdateAsync(Action<ShireCopySchedule> change)
    {
        await using var db = await dbs.CreateDbContextAsync();
        if (!await db.Integrations.AnyAsync(i => i.Provider == Provider)) await store.GetAsync(Provider);   // makes the empty row
        var row = await db.Integrations.SingleAsync(i => i.Provider == Provider);
        var s = IntegrationStore.ReadSettings<ShireCopySchedule>(row) ?? new ShireCopySchedule();
        change(s);
        row.SettingsJson = JsonSerializer.Serialize(s);
        row.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        _last = s;
    }
}
