using System.Text.Json;
using Kpi.Data;
using Kpi.Data.Auth;
using Kpi.Data.Cfg;
using Kpi.Web.Account;
using Kpi.Web.Integrations;
using Microsoft.EntityFrameworkCore;

namespace Kpi.Web.Colours;

/// <summary>One office's saved choices: its palette and the colours chosen (key "section.item" - only those changed).</summary>
public sealed class OfficeColours
{
    public List<PaletteColour> Palette { get; set; } = new();
    public Dictionary<string, string> Colours { get; set; } = new();

    public OfficeColours Copy() => new() { Palette = Palette.ToList(), Colours = new(Colours) };
}

/// <summary>Everything Settings > Colours saves - also the Export / Import file.</summary>
public sealed class ColourDocument
{
    public int Version { get; set; } = 1;
    public Dictionary<string, OfficeColours> Offices { get; set; } = new();

    public OfficeColours For(string office) =>
        Offices.TryGetValue(office, out var o) ? o : Offices[office] = new OfficeColours { Palette = ColourCatalogue.BrandPalette(office).ToList() };

    public ColourDocument Copy() => new() { Version = Version, Offices = Offices.ToDictionary(p => p.Key, p => p.Value.Copy()) };
}

/// <summary>
/// The colours the reports use (Settings > Colours, 13 Nov 2026). Saved as one Cfg.Integration row (provider "Colours",
/// its SettingsJson) - no migration. App.razor writes <see cref="Css"/> into every page's head; the charts drawn by script
/// ask for <see cref="ChartHex"/>, <see cref="RacePalette"/>, <see cref="HeatScale"/> and <see cref="Margin"/>. Read at
/// most once a minute (both deployment slots share the database, so the other slot catches up within a minute); a save
/// here is seen at once. A page already open shows new colours when it is next opened.
/// </summary>
public sealed class ColourTheme(IntegrationStore store, IDbContextFactory<KpiDbContext> dbs, ILogger<ColourTheme> log)
{
    public const string Provider = "Colours";
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ColourDocument _doc = new();
    private string _css = "";
    private DateTime _readUtc = DateTime.MinValue;

    /// <summary>The saved document (a copy: the caller may change it).</summary>
    public async Task<ColourDocument> GetAsync() { await RefreshAsync(); return _doc.Copy(); }

    /// <summary>Re-reads the saved colours if they are over a minute old - before a chart asks for its colours.</summary>
    public Task ReadyAsync() => RefreshAsync();

    /// <summary>The CSS for every page's head ("" while nothing is saved).</summary>
    public async Task<string> CssAsync() { await RefreshAsync(); return _css; }

    /// <summary>A chart's nth colour on the full view (the script-drawn pies), or null to keep today's.</summary>
    public string? ChartHex(bool shire, int index) =>
        index < 8 ? Saved(shire ? ColourCatalogue.Mobberley : ColourCatalogue.Kettering, $"charts.s{index + 1}") : null;

    /// <summary>The timelapse race: one colour per salesperson - the Charts colours in order once any is chosen.</summary>
    public string[]? RacePalette()
    {
        var any = Enumerable.Range(0, 8).Any(i => ChartHex(false, i) is not null);
        return any ? Enumerable.Range(0, 8).Select(i => ChartHex(false, i) ?? ColourCatalogue.DesktopCharts[i]).ToArray() : null;
    }

    /// <summary>The heat map's seven bands, or null to keep today's.</summary>
    public string[]? HeatScale() => Saved(ColourCatalogue.Kettering, "hm.base") is { } h ? ColourCatalogue.HeatScale(h) : null;

    /// <summary>Margin Analysis's full-view chart: margin bars, buy and sell lines.</summary>
    public (string Margin, string Buy, string Sell) Margin() => (
        Saved(ColourCatalogue.Kettering, "ma.margin") ?? "#F59E0B",
        Saved(ColourCatalogue.Kettering, "ma.buy") ?? "#06B6D4",
        Saved(ColourCatalogue.Kettering, "ma.sell") ?? "#10B981");

    private string? Saved(string office, string key) =>
        _doc.Offices.TryGetValue(office, out var o) && o.Colours.TryGetValue(key, out var h) ? h : null;

    private async Task RefreshAsync(bool force = false)
    {
        if (!force && DateTime.UtcNow - _readUtc < Fresh) return;
        await _gate.WaitAsync();
        try
        {
            if (!force && DateTime.UtcNow - _readUtc < Fresh) return;
            var row = await store.GetAsync(Provider);
            Apply(Parse(row.SettingsJson) ?? new ColourDocument());
        }
        catch (Exception e) { log.LogWarning(e, "The saved colours could not be read; the reports keep their own"); }
        finally { _readUtc = DateTime.UtcNow; _gate.Release(); }
    }

    private void Apply(ColourDocument doc)
    {
        _doc = doc;
        _css = ColourCatalogue.Css(Colours(doc, ColourCatalogue.Kettering), Colours(doc, ColourCatalogue.Mobberley));
    }

    private static IReadOnlyDictionary<string, string> Colours(ColourDocument doc, string office) =>
        doc.Offices.TryGetValue(office, out var o) ? o.Colours : new Dictionary<string, string>();

    /// <summary>Reads an Export file (or the saved row), keeping only known colours with good hex codes.</summary>
    public static ColourDocument? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var doc = JsonSerializer.Deserialize<ColourDocument>(json) ?? throw new InvalidOperationException("That file has no colours in it.");
        var clean = new ColourDocument();
        foreach (var office in ColourCatalogue.Offices)
        {
            if (!doc.Offices.TryGetValue(office, out var o)) continue;
            clean.Offices[office] = new OfficeColours
            {
                Palette = o.Palette.Where(p => ColourCatalogue.IsHex(p.Hex) && !string.IsNullOrWhiteSpace(p.Name))
                                   .Select(p => new PaletteColour(p.Name.Trim(), ColourCatalogue.Normalise(p.Hex))).ToList(),
                Colours = o.Colours.Where(c => ColourCatalogue.Find(office, c.Key) is not null && ColourCatalogue.IsHex(c.Value))
                                   .ToDictionary(c => c.Key, c => ColourCatalogue.Normalise(c.Value))
            };
        }
        return clean;
    }

    public static string Export(ColourDocument doc) => JsonSerializer.Serialize(doc, Json);

    /// <summary>Saves the whole document; the audit line names what changed.</summary>
    public async Task SaveAsync(KpiUser actor, ColourDocument doc)
    {
        doc = Parse(JsonSerializer.Serialize(doc)) ?? new ColourDocument();
        var before = await GetAsync();
        var changes = new List<string>();
        foreach (var office in ColourCatalogue.Offices)
        {
            var a = before.For(office);
            var b = doc.For(office);
            var n = ColourCatalogue.Sections(office).SelectMany(s => s.Items.Select(i => $"{s.Key}.{i.Key}"))
                .Count(k => a.Colours.GetValueOrDefault(k) != b.Colours.GetValueOrDefault(k));
            if (n > 0) changes.Add($"{ColourCatalogue.OfficeName(office)}: {n} colour{(n == 1 ? "" : "s")}");
            if (!a.Palette.SequenceEqual(b.Palette)) changes.Add($"{ColourCatalogue.OfficeName(office)}'s palette");
        }

        await using var db = await dbs.CreateDbContextAsync();
        if (!await db.Integrations.AnyAsync(i => i.Provider == Provider)) await store.GetAsync(Provider);   // makes the row
        var row = await db.Integrations.SingleAsync(i => i.Provider == Provider);
        row.SettingsJson = JsonSerializer.Serialize(doc);
        row.Enabled = true;
        row.Status = IntegrationStatus.Connected;
        row.UpdatedByUserId = actor.Id;
        row.UpdatedUtc = DateTime.UtcNow;
        AuditLog.Add(db, AuditEvents.IntegrationChanged, actor, null,
            "Colours saved" + (changes.Count > 0 ? ": " + string.Join(", ", changes) : " (no change)"));
        await db.SaveChangesAsync();

        await _gate.WaitAsync();
        try { Apply(doc); _readUtc = DateTime.UtcNow; }
        finally { _gate.Release(); }
    }
}
