using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Kpi.Web.Account;

/// <summary>
/// Tells the open report (KpiPanel) when this browser's live connection drops or comes back (29 Oct 2026, Report
/// activity). The server keeps a dropped connection for 30 minutes (a phone that slept comes back quietly), so a report
/// left by loading another page isn't closed until then - its "time looked at" must stop when the connection does, and
/// its Closed is dated then. One per connection.
/// </summary>
public sealed class ReportCircuit : CircuitHandler
{
    /// <summary>True when the connection is back, false when it dropped.</summary>
    public event Action<bool>? Connection;

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Connection?.Invoke(false);
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Connection?.Invoke(true);
        return Task.CompletedTask;
    }
}
