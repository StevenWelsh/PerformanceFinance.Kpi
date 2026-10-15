namespace Kpi.Web.Data.Kpis;

/// <summary>
/// When the open KPI report's figures were last fetched, and whether a fetch is running - published by the
/// report's <c>KpiPanel</c> (which owns the 10-minute refresh) so the phone bar can show the same as the full
/// view's date line. Scoped: one per circuit, and a circuit shows one KPI report at a time.
/// </summary>
public sealed class KpiRefreshStatus
{
    /// <summary>When the figures on screen were fetched (UTC); null until the report's first load has finished.</summary>
    public DateTime? RefreshedOnUtc { get; private set; }

    /// <summary>A background refresh is running.</summary>
    public bool Refreshing { get; private set; }

    /// <summary>Nothing to show yet, or a refresh under way.</summary>
    public bool Busy => Refreshing || RefreshedOnUtc is null;

    public event Action? Changed;

    public void Report(DateTime? refreshedOnUtc, bool refreshing)
    {
        if (refreshedOnUtc == RefreshedOnUtc && refreshing == Refreshing) return;
        RefreshedOnUtc = refreshedOnUtc;
        Refreshing = refreshing;
        Changed?.Invoke();
    }
}
