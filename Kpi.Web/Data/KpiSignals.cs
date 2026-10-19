namespace Kpi.Web.Data;

/// <summary>
/// New figures have landed and the cache holds them (19 Oct 2026, Steve: "will these pages refresh when new data
/// lands?"). Raised by <see cref="Kpis.KpiWarmer"/> once it has re-run the cached reports after a copy; every open report
/// (KpiPanel - the published / embedded view too) and Settings > Data sync listen, so they update within seconds instead
/// of at their next 10-minute check.
/// </summary>
public sealed class KpiFiguresSignal
{
    public event Action? Landed;

    public void Raise() => Landed?.Invoke();
}

/// <summary>
/// How the site's own copy (<see cref="KpiPull"/>) is doing, for the alerts (<see cref="SyncAlerts"/>): whether it is on,
/// and since when it has been failing, with the last error. In memory - the copy sets it every two minutes.
/// </summary>
public sealed class KpiPullHealth
{
    public bool? IsOn { get; private set; }
    public DateTime? FailingSinceUtc { get; private set; }
    public string? LastError { get; private set; }

    public void On(bool on) => IsOn = on;

    public void Failed(DateTime sinceUtc, string error)
    {
        FailingSinceUtc ??= sinceUtc;
        LastError = error;
    }

    public void Worked()
    {
        FailingSinceUtc = null;
        LastError = null;
    }
}
