using System.Security.Claims;
using Kpi.Data;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Kpi.Web.Data.Kpis;

/// <summary>The two lists of the phone's reports menu.</summary>
public enum KpiMenuView { Favourites, All }

/// <summary>
/// The signed-in person's favourite KPI reports (by slug, in the order they were added) and the menu view they last
/// picked. The reports menu opens on that view, or - when they have never picked one - on Favourites when there are
/// any and All when there are none (KpiMobileMenu); the phone bar's star and My Account add or remove them.
/// Kept with the LOGIN (Auth.Users), so every device shows the same list - unlike CMS.Web's copy, which keeps them in
/// the phone's browser. Scoped: one per circuit, loaded once.
/// </summary>
public sealed class KpiFavourites(AuthenticationStateProvider auth, IDbContextFactory<KpiDbContext> dbs)
{
    private List<string> _slugs = new();
    private Task? _load;
    private string? _userId;

    /// <summary>Loaded (until then the menu shows nothing rather than the wrong view).</summary>
    public bool Loaded { get; private set; }

    public IReadOnlyList<string> Slugs => _slugs;

    /// <summary>The view the user last picked in the menu; null when they never have.</summary>
    public KpiMenuView? MenuView { get; private set; }

    public bool Contains(string slug) => _slugs.Contains(slug);

    public event Action? Changed;

    /// <summary>Reads the favourites, once per circuit.</summary>
    public Task EnsureLoadedAsync() => _load ??= LoadAsync();

    private async Task LoadAsync()
    {
        _userId = (await auth.GetAuthenticationStateAsync()).User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (_userId is not null)
        {
            await using var db = await dbs.CreateDbContextAsync();
            var row = await db.Users.AsNoTracking().Where(u => u.Id == _userId)
                .Select(u => new { u.FavouriteReports, u.FavouritesMenuView }).SingleOrDefaultAsync();
            _slugs = (row?.FavouriteReports ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
            MenuView = Enum.TryParse<KpiMenuView>(row?.FavouritesMenuView, true, out var view) ? view : null;
        }
        Loaded = true;
        Changed?.Invoke();
    }

    /// <summary>Adds the report, or removes it if it is already a favourite, and saves the list.</summary>
    public async Task ToggleAsync(string slug)
    {
        await EnsureLoadedAsync();
        if (!_slugs.Remove(slug)) _slugs.Add(slug);
        Changed?.Invoke();
        var value = string.Join(',', _slugs);
        await SaveAsync(u => u.SetProperty(x => x.FavouriteReports, value));
    }

    /// <summary>Remembers the view the user picked, so the menu opens on it next time.</summary>
    public async Task SetMenuViewAsync(KpiMenuView view)
    {
        MenuView = view;
        var value = view.ToString().ToLowerInvariant();
        await SaveAsync(u => u.SetProperty(x => x.FavouritesMenuView, value));
    }

    private async Task SaveAsync(Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<Kpi.Data.Auth.KpiUser>> set)
    {
        if (_userId is null) return;
        await using var db = await dbs.CreateDbContextAsync();
        await db.Users.Where(u => u.Id == _userId).ExecuteUpdateAsync(set);
    }
}
