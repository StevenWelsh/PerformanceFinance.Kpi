using Microsoft.AspNetCore.Components;

namespace Kpi.Web.Data.Kpis;

/// <summary>
/// The KPI reports' Sales Office filter - always their FIRST filter. A real filter since 3 Nov 2026 (Steve - Mobberley's
/// first deals): the pages pass <see cref="OfficeId"/> to their Rpt procedures' @SalesOfficeId (Fact.Lead.SalesOfficeId,
/// the deal's office as CMS records it). Sales Summary, Year on Year, Business Analysis, Sales Performance, Margin
/// Analysis and Minimum IRR have it.
/// REPORT SPECIFIC, AS EVERY OTHER FILTER (3 Nov 2026, Steve, trying it on staging: "they all should be report
/// specific"): the choice belongs to the report on screen and goes back to All when another report opens - as Division
/// does, being the page's own field. Kept here (per circuit) rather than on the page because the page and its phone
/// view each have an office field and share the choice.
/// </summary>
public sealed class KpiOfficeState
{
    /// <summary>CMS.SalesOffice: Kettering is Id 1 and must stay 1.</summary>
    public const int KetteringId = 1;

    private readonly NavigationManager _nav;
    private string? _chosenFor;   // the report the choice was made on
    private int? _chosen;

    public KpiOfficeState(NavigationManager nav)
    {
        _nav = nav;
        // another report (not just a new query on this one) starts again at All
        nav.LocationChanged += (_, e) =>
        {
            if (_chosenFor is not null && !string.Equals(PathOf(e.Location), _chosenFor, StringComparison.OrdinalIgnoreCase))
                (_chosenFor, _chosen) = (null, null);
        };
    }

    /// <summary>This report's chosen office; null = all offices.</summary>
    public int? OfficeId => string.Equals(_chosenFor, Report, StringComparison.OrdinalIgnoreCase) ? _chosen : null;

    /// <summary>Every sales office, in their own order.</summary>
    public IReadOnlyList<(int Id, string Name)> Offices { get; private set; } = Array.Empty<(int, string)>();

    public string OfficeName => OfficeId is int id ? Offices.FirstOrDefault(o => o.Id == id).Name ?? "" : "All";

    private Task? _load;

    /// <summary>Loads the offices once per circuit - a page can carry several office fields (full view, phone).</summary>
    public Task EnsureLoadedAsync(KpiSessionFactory sessions) => _load ??= LoadAsync(sessions);

    private async Task LoadAsync(KpiSessionFactory sessions)
    {
        using var cms = await sessions.BeginAsync();
        Offices = cms.Db.Options("Offices")
            .Select(o => (o.Id, o.Name))
            .ToList();
    }

    /// <summary>"" = all offices, else an office id - for the report on screen only.</summary>
    public void Choose(string value)
    {
        if (int.TryParse(value, out var id)) (_chosenFor, _chosen) = (Report, id);
        else (_chosenFor, _chosen) = (null, null);
    }

    // the report on screen: its address without the query (/kpis/sales-summary, /embed/sales-summary)
    private string Report => PathOf(_nav.Uri);
    private static string PathOf(string uri) => new Uri(uri).AbsolutePath.TrimEnd('/');
}
