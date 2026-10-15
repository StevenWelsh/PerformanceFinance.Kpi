namespace Kpi.Web.Data.Kpis;

/// <summary>
/// The KPI reports' Sales Office filter - always their FIRST filter. One choice per circuit, so every report
/// follows it as the user moves between them.
/// ⚠️ A MOCK until the KPI figures carry an office (Oct 2026, Steve): every deal so far is Kettering's, so All
/// and Kettering show the figures as they are and any other office shows none (<see cref="ShowsData"/>).
/// Wiring it for real = the reports' queries take <see cref="OfficeId"/> (a lead's office is its salesperson's -
/// CLAUDE.md "Office access") and ShowsData goes.
/// </summary>
public sealed class KpiOfficeState
{
    /// <summary>CMS.SalesOffice: Kettering is Id 1 and must stay 1.</summary>
    public const int KetteringId = 1;

    /// <summary>The chosen office; null = all offices.</summary>
    public int? OfficeId { get; private set; }

    /// <summary>Every sales office, in their own order.</summary>
    public IReadOnlyList<(int Id, string Name)> Offices { get; private set; } = Array.Empty<(int, string)>();

    public string OfficeName => OfficeId is int id ? Offices.FirstOrDefault(o => o.Id == id).Name ?? "" : "All";

    /// <summary>Whether the reports have figures for the chosen office (the mock - see the class note).</summary>
    public bool ShowsData => OfficeId is null || OfficeId == KetteringId;

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

    /// <summary>"" = all offices, else an office id.</summary>
    public void Choose(string value) => OfficeId = int.TryParse(value, out var id) ? id : null;
}
