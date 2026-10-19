using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Kpi.Web.Account;

/// <summary>
/// Who has the site open right now, for Settings > Users (19 Oct 2026, Steve). Every interactive page (the reports,
/// My Account, Settings) keeps a live connection to the server - a Blazor circuit - and <see cref="OnlineTracker"/>
/// notes each one going up and down against its signed-in person. "Online" = at least one page connected now; a phone
/// that sleeps drops its connection, so it shows "last seen" until it wakes. Kept in memory on the one server, so after a
/// restart everyone shows again as soon as their page reconnects (seconds); "last seen" only goes back to the restart.
/// Published views (/view) are anonymous and not counted.
/// </summary>
public sealed class OnlineUsers
{
    private readonly ConcurrentDictionary<string, string> _circuits = new();      // circuit id -> user id, while connected
    private readonly ConcurrentDictionary<string, DateTime> _lastSeenUtc = new();  // user id -> when a page last went away

    /// <summary>Raised when anyone comes or goes, so an open Users screen can redraw.</summary>
    public event Action? Changed;

    public sealed record Presence(int Pages, DateTime? LastSeenUtc)
    {
        public bool IsOnline => Pages > 0;
    }

    public Presence For(string userId)
    {
        var pages = _circuits.Values.Count(u => u == userId);
        return new Presence(pages, _lastSeenUtc.TryGetValue(userId, out var seen) ? seen : null);
    }

    internal void Up(string circuitId, string userId)
    {
        _circuits[circuitId] = userId;
        Changed?.Invoke();
    }

    internal void Down(string circuitId)
    {
        if (!_circuits.TryRemove(circuitId, out var userId)) return;
        _lastSeenUtc[userId] = DateTime.UtcNow;
        Changed?.Invoke();
    }
}

/// <summary>Reports each page's connection going up and down to <see cref="OnlineUsers"/> (one per circuit).</summary>
public sealed class OnlineTracker(OnlineUsers online, AuthenticationStateProvider auth) : CircuitHandler
{
    public override async Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        try
        {
            var user = (await auth.GetAuthenticationStateAsync()).User;
            var id = user.Identity?.IsAuthenticated == true ? user.FindFirstValue(ClaimTypes.NameIdentifier) : null;
            if (id is not null) online.Up(circuit.Id, id);
        }
        catch { /* presence is only a convenience: never let it break a page */ }
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        online.Down(circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        online.Down(circuit.Id);
        return Task.CompletedTask;
    }
}
