using System.Globalization;
using System.Text;

namespace Kpi.Web.Colours;

/// <summary>Where a colour shows: the full view, the phone view, or both.</summary>
public enum ColourWhere { Both, Desktop, Phone }

/// <summary>
/// One colour on Settings > Colours. <see cref="Default"/> is what the reports show TODAY when nothing is saved (the
/// full view's, where the phone differs - <see cref="Note"/> says so). A saved colour reaches the pages through
/// <see cref="Tokens"/> (CSS custom properties the stylesheets already read) and/or <see cref="Rules"/> (extra CSS for the
/// few places that have no token of their own). Nothing saved = nothing emitted, so each view keeps exactly today's look.
/// </summary>
public sealed record ColourItem(
    string Key, string Label, ColourWhere Where, string? Note, string Default,
    string[] Tokens, Func<string, string>? Rules = null, bool PfEverywhere = false);

public sealed record ColourSection(string Key, string Group, string Name, ColourItem[] Items);

/// <summary>A palette colour: a name and its hex.</summary>
public sealed record PaletteColour(string Name, string Hex);

/// <summary>
/// SETTINGS > COLOURS (13 Nov 2026, Steve and Stefanie - "keep everything on brand"): every colour the reports use that
/// is worth choosing, for Kettering (PF's reports) and Mobberley (Shire's), as the mock-up docs/colours-mockup.html.
/// Kettering's tokens are set on the root; Mobberley's inside .kpi-shire, the class every Shire page carries (desktop
/// .kpi-shell and phone .kpim) - and a Kettering colour is put back to Mobberley's own inside .kpi-shire, so one office's
/// colours never leak into the other's pages. The menu red and the star are PF's on every page (PfEverywhere).
/// </summary>
public static class ColourCatalogue
{
    public const string Kettering = "kettering";
    public const string Mobberley = "mobberley";
    public static readonly string[] Offices = { Kettering, Mobberley };

    public static string OfficeName(string office) => office == Mobberley ? "Mobberley" : "Kettering";

    // PF's brand guide (pages 20-21) and Shire's own site (shirepf.co.uk, 13 Nov 2026): what "Load the palette" loads
    public static readonly PaletteColour[] PfPalette =
    {
        new("PF red", "#c20430"), new("Cool grey", "#898b8e"), new("Black", "#1d1d1b"),
        new("Cyan", "#6bc2b9"), new("Orange", "#ee7624"), new("Dark grey", "#54585a")
    };
    public static readonly PaletteColour[] ShirePalette =
    {
        new("Shire indigo", "#3b0086"), new("Shire purple", "#7209b7"), new("Shire pink", "#f72585"),
        new("Shire green", "#06d6a0"), new("Shire plum", "#290628")
    };
    public static PaletteColour[] BrandPalette(string office) => office == Mobberley ? ShirePalette : PfPalette;

    /// <summary>The tints the picker offers for each palette colour (the brand guide allows lighter tints).</summary>
    public static readonly double[] Tints = { 1, .8, .6, .4, .2, .1 };

    // the phone's single bars (Bad Debt, Exposure, Business Analysis's lender bars, Sales Productivity's funnel) - they
    // use the menu red today, so a "bars of one colour" choice reaches them by rule. NOT .kpim-bar: that is the phone's
    // top bar (13 Nov 2026 - it turned the header orange), which follows the menu red.
    private const string PhoneBars = ".kpim-ba-bar,.kpim-ba-fill,.kpim-bd-bar.kpim-bd-cur .kpim-bd-fill,.kpim-ex-fill,.kpim-spr-fill-activated";

    // every totals row, both views (they borrowed the group title's colour; 13 Nov 2026 they follow "Totals row")
    private const string TotalsRows =
        ".kpi-shell .mi-card .mi-table tfoot td,.kpi-shell .sy-table tr.sy-total td,.kpi-shell .sy-table tbody tr.sy-total:hover td," +
        ".kpi-shell .sd-table tfoot td,.kpi-shell .kpi-ex-table tfoot td,.kpi-shell .ma-table tfoot td," +
        ".kpim-sy-total,.kpim-sd-total,.kpim-ex-total,.kpim-ex-table tfoot td,.kpim-mi-table tfoot tr";

    private static readonly string[] Now = { "--kpim-now", "--kpim-yoy-cur", "--kpim-bd-now", "--kpim-spr-now" };
    private static readonly string[] NowHead = { "--kpim-now-head", "--kpim-yoy-cur-head", "--kpim-bd-now-head", "--kpim-spr-now-head" };
    private static readonly string[] Heads = { "--kpim-yoy-head", "--kpim-bd-head", "--kpim-ex-head", "--kpim-mi-head", "--kpim-sp-head" };
    private static readonly string[] Rules = { "--kpim-yoy-rule", "--kpim-mi-rule", "--kpim-sp-rule" };
    private static readonly string[] Lines = { "--kpim-yoy-line", "--kpim-mi-line", "--kpim-sp-line", "--kpim-row-line" };

    // today's chart colours: the full view's (app.css --chart-n) and the phone's (kpi.css --kpim-ba-sn)
    public static readonly string[] DesktopCharts = { "#4f46e5", "#f59e0b", "#10b981", "#ef4444", "#8b5cf6", "#06b6d4", "#f97316", "#ec4899" };
    private static readonly string[] PhoneCharts = { "#d20d43", "#8a0a2d", "#e86a8b", "#f4b6c6", "#3a3a3a", "#76777c", "#a1a2a7", "#d0d1d4" };
    private static readonly string[] ShirePhoneCharts = { "#62368b", "#b93491", "#8061a3", "#cdb9e3", "#3a3a3a", "#76777c", "#a1a2a7", "#d0d1d4" };
    private static readonly string[] Ordinals = { "1st", "2nd", "3rd", "4th", "5th", "6th", "7th", "8th" };

    private static ColourItem[] ChartSeries(bool shire) => Enumerable.Range(1, 8).Select(n => new ColourItem(
        $"s{n}", Ordinals[n - 1], ColourWhere.Both,
        n != 1 ? null : shire ? "today the full view's pie uses Kettering's colours" : "today the phone's pie uses reds and greys",
        shire ? ShirePhoneCharts[n - 1] : DesktopCharts[n - 1],
        new[] { $"--chart-{n}", $"--kpim-ba-s{n}" })).ToArray();

    public static readonly ColourSection[] KetteringSections =
    {
        new("site", "Every report", "Menu", new ColourItem[]
        {
            new("menu", "Menu and top bar", ColourWhere.Both, null, "#d20d43", new[] { "--sidebar-bg", "--pf-crimson" },
                h => $":root:root{{--pf-crimson-dark:{Shade(h, .24)}}}", PfEverywhere: true),
            new("star", "Favourite star", ColourWhere.Both, null, "#f5b301", new[] { "--kpim-star" }, PfEverywhere: true),
        }),
        new("tables", "Every report", "Tables", new ColourItem[]
        {
            new("title", "Group title band", ColourWhere.Both, null, "#fafafa", new[] { "--kpim-card-head" }),
            new("head", "Column headings band", ColourWhere.Phone, "the tables you turn sideways; desktop headings are white", "#f5f5f5", Heads),
            new("foot", "Totals row", ColourWhere.Both, "Suppliers, Supplier Development, Exposure, Bad Debt, Minimum IRR, Margin Analysis", "#fafafa", Array.Empty<string>(),
                h => $"{TotalsRows}{{background:{h}}}"),
            new("now", "Current year: row or column", ColourWhere.Both, null, "#fdeef2", Now),
            new("nowhead", "Current year: its heading", ColourWhere.Both, null, "#f6c9d5", NowHead),
            new("pick", "Picked or pointed-at row", ColourWhere.Both, "Business Analysis, Heat Map list", "#fdeef2", new[] { "--kpim-hm-sel-row", "--row-hl" }),
            new("rule", "Lines under headings and above totals", ColourWhere.Both, null, "#cfcfcf", Rules),
            new("line", "Lines between rows", ColourWhere.Both, null, "#ececec", Lines),
        }),
        new("charts", "Every report", "Charts", ChartSeries(false).Concat(new ColourItem[]
        {
            new("bar", "Bars of one colour", ColourWhere.Both, "today navy on desktop, red on the phone (Bad Debt, Exposure)", "#2b579a", Array.Empty<string>(),
                h => $":root:root{{--kc-bar:{h}}}.kpi-shire{{--kc-bar:initial}}.bat-card{{--bat-bar:var(--kc-bar,var(--brand))}}{PhoneBars}{{background:var(--kc-bar,var(--kpim-crimson))}}"),
            new("part", "This year so far (a part bar)", ColourWhere.Both, null, "#a9bfe0", new[] { "--kpim-ba-part" },
                h => $":root:root{{--kc-part:{h}}}.kpi-shire{{--kc-part:initial}}.bat-card{{--bat-part:var(--kc-part,#a9bfe0)}}"),
        }).ToArray()),
        new("sp", "Only on one report", "Sales Performance", new ColourItem[]
        {
            new("col", "Columns", ColourWhere.Both, null, "#2b579a", new[] { "--kpim-sp-col" }),
            new("colon", "Tapped column", ColourWhere.Phone, null, "#1f3a5f", new[] { "--kpim-sp-col-on" }),
            new("play", "Timelapse play button and slider", ColourWhere.Both, "the timelapse race's bars take the Charts colours in order", "#d20d43", Array.Empty<string>(),
                h => $".slt-race .slt-ctrl,.kpim-sp-race .slt-ctrl,.slt-race .slt-ctrl:hover{{color:{h}}}.slt-race .slt-ctrl:hover{{border-color:{h}}}.slt-scrub,.kpim-sp-race .slt-scrub{{accent-color:{h}}}"),
            new("ytd", "Year to date target", ColourWhere.Both, "one yellow for both targets today", "#f4c20d", Array.Empty<string>(),
                h => $".kpi-shell .slt-tick-ytd,.kpim-sp-tick-ytd{{stroke:{h}}}.kpi-shell .slt-key-ytd{{border-top-color:{h}}}.slt-rline-ytd,.kpim-sp-race .slt-rline-ytd,.slt-race-key .slt-key-ytd{{border-left-color:{h}}}"),
            new("year", "Year target", ColourWhere.Both, null, "#f4c20d", Array.Empty<string>(),
                h => $".kpi-shell .slt-tick-year,.kpim-sp-tick-year{{stroke:{h}}}.kpi-shell .slt-key-year{{border-top-color:{h}}}"),
        }),
        new("spr", "Only on one report", "Sales Productivity", new ColourItem[]
        {
            new("appr", "Approved bar", ColourWhere.Phone, "the desktop page is a table", "#e86a8b", new[] { "--kpim-spr-approved" }),
        }),
        new("rp", "Only on one report", "Record Performances", new ColourItem[]
        {
            new("rec", "A record set this year", ColourWhere.Both, null, "#fdf6dc", new[] { "--kpim-rp-current" }),
            new("trophy", "Trophy", ColourWhere.Both, null, "#f2c94c", new[] { "--kpim-rp-trophy-fill" },
                h => $":root:root{{--kpim-rp-trophy:{Shade(h, .35)}}}"),
        }),
        new("sd", "Only on one report", "Supplier Development", new ColourItem[]
        {
            new("bar", "Trend bars", ColourWhere.Both, null, "#d20d43", Array.Empty<string>(),
                h => $".kpi-shell .sd-bar:not(.sd-bar-cur),.kpim-sd-fill{{background:{h}}}.kpi-shell .sd-bar-cur{{box-shadow:inset 0 0 0 1px {h}}}.kpim-sd-cur .kpim-sd-fill{{border-color:{h}}}"),
            new("cur", "This year's bar", ColourWhere.Both, null, "#f9dce4", new[] { "--kpim-sd-bar-cur" }),
        }),
        new("hm", "Only on one report", "Postcode Heat Map", new ColourItem[]
        {
            new("base", "Colour (the scale runs pale to dark)", ColourWhere.Both, null, "#d20d43", Array.Empty<string>()),
        }),
        new("ma", "Only on one report", "Margin Analysis", new ColourItem[]
        {
            new("margin", "Margin bars", ColourWhere.Both, null, "#f59e0b", new[] { "--kpim-ma-margin" }),
            new("buy", "Buy rate", ColourWhere.Both, null, "#06b6d4", new[] { "--kpim-ma-buy" }),
            new("sell", "Sell rate", ColourWhere.Both, null, "#10b981", new[] { "--kpim-ma-sell" }),
            new("band", "Band between buy and sell", ColourWhere.Phone, null, "#f4b6c6", new[] { "--kpim-ma-band" }),
        }),
    };

    // Mobberley's tokens are set inside .kpi-shire; the menu section and the open report's marker are on the root (the
    // sidebar's Shire section sits outside any Shire page)
    public static readonly ColourSection[] MobberleySections =
    {
        new("site", "Every Mobberley report", "Menu", new ColourItem[]
        {
            new("menu", "Menu section and report titles", ColourWhere.Both, null, "#62368b", Array.Empty<string>(),
                h => $":root:root{{--shire-deep:{h};--shire-deep-dark:{Shade(h, .24)}}}"),
            new("open", "The open report", ColourWhere.Both, null, "#b93491", Array.Empty<string>(),
                h => $":root:root{{--shire-magenta:{h}}}"),
        }),
        new("tables", "Every Mobberley report", "Tables", new ColourItem[]
        {
            new("title", "Group title band", ColourWhere.Both, "today pale purple on desktop, grey on the phone", "#efe9f6", new[] { "--kpim-card-head" },
                h => $".kpi-shell.kpi-shire .ss-card-head{{background:{h}}}"),
            new("head", "Column headings band", ColourWhere.Phone, "the tables you turn sideways", "#f5f5f5", Heads),
            new("now", "Current year: row or column", ColourWhere.Both, null, "#efe9f6", Now),
            new("nowhead", "Current year: its heading", ColourWhere.Both, null, "#dccdec", NowHead),
            new("rule", "Lines under headings and above totals", ColourWhere.Both, null, "#cfcfcf", Rules),
            new("line", "Lines between rows", ColourWhere.Both, null, "#ececec", Lines),
        }),
        new("charts", "Every Mobberley report", "Charts", ChartSeries(true).Concat(new ColourItem[]
        {
            new("bar", "Bars of one colour", ColourWhere.Both, null, "#8061a3", Array.Empty<string>(),
                h => $".kpi-shire.kpi-shire{{--kc-bar:{h}}}.kpi-shire.kpi-shire .bat-card{{--bat-bar:{h}}}{string.Join(",", PhoneBars.Split(',').Select(s => ".kpi-shire " + s))}{{background:{h}}}"),
            new("part", "This year so far (a part bar)", ColourWhere.Both, null, "#cdb8e3", new[] { "--kpim-ba-part" },
                h => $".kpi-shire.kpi-shire{{--kc-part:{h}}}.kpi-shire.kpi-shire .bat-card{{--bat-part:{h}}}"),
        }).ToArray()),
        new("sp", "Only on one report", "Sales Performance", new ColourItem[]
        {
            new("col", "Columns", ColourWhere.Both, null, "#8061a3", new[] { "--kpim-sp-col" },
                h => $".kpi-shell.kpi-shire .slt-col{{fill:{h}}}"),
            new("colon", "Tapped column", ColourWhere.Phone, null, "#62368b", new[] { "--kpim-sp-col-on" }),
        }),
    };

    public static ColourSection[] Sections(string office) => office == Mobberley ? MobberleySections : KetteringSections;

    public static ColourItem? Find(string office, string fullKey)
    {
        var dot = fullKey.IndexOf('.');
        if (dot < 0) return null;
        return Sections(office).FirstOrDefault(s => s.Key == fullKey[..dot])?.Items.FirstOrDefault(i => i.Key == fullKey[(dot + 1)..]);
    }

    /// <summary>What each token is TODAY outside a Shire page (the stylesheets' own values).</summary>
    private static readonly Dictionary<string, string> TokenDefaults = new()
    {
        ["--kpim-card-head"] = "#fafafa",
        ["--kpim-yoy-head"] = "#f5f5f5", ["--kpim-bd-head"] = "#f5f5f5", ["--kpim-ex-head"] = "#f5f5f5", ["--kpim-mi-head"] = "#f5f5f5", ["--kpim-sp-head"] = "#f5f5f5",
        ["--kpim-now"] = "#fdeef2", ["--kpim-yoy-cur"] = "#fdeef2", ["--kpim-bd-now"] = "#fdeef2", ["--kpim-spr-now"] = "#fdeef2",
        ["--kpim-now-head"] = "#f6c9d5", ["--kpim-yoy-cur-head"] = "#f6c9d5", ["--kpim-bd-now-head"] = "#f6c9d5", ["--kpim-spr-now-head"] = "#f6c9d5",
        ["--kpim-hm-sel-row"] = "#fdeef2", ["--row-hl"] = "rgba(210, 13, 67, 0.10)",
        ["--kpim-yoy-rule"] = "#cfcfcf", ["--kpim-mi-rule"] = "#cfcfcf", ["--kpim-sp-rule"] = "#cfcfcf",
        ["--kpim-yoy-line"] = "#ececec", ["--kpim-mi-line"] = "#ececec", ["--kpim-sp-line"] = "#ececec", ["--kpim-row-line"] = "#f0f0f0",
        ["--kpim-ba-part"] = "#f4b6c6", ["--kpim-sp-col"] = "#2b579a", ["--kpim-sp-col-on"] = "#1f3a5f",
        ["--kpim-spr-approved"] = "#e86a8b", ["--kpim-rp-current"] = "#fdf6dc", ["--kpim-rp-trophy-fill"] = "#f2c94c",
        ["--kpim-sd-bar-cur"] = "#f9dce4", ["--kpim-ma-margin"] = "#f59e0b", ["--kpim-ma-buy"] = "#06b6d4",
        ["--kpim-ma-sell"] = "#10b981", ["--kpim-ma-band"] = "#f4b6c6",
    };

    /// <summary>...and inside a Shire page, where kpi.css's .kpi-shire turns some of them purple.</summary>
    private static readonly Dictionary<string, string> ShireTokenDefaults = new()
    {
        ["--kpim-now"] = "var(--shire-tint)", ["--kpim-now-head"] = "var(--shire-tint-head)",
        ["--kpim-yoy-cur"] = "var(--shire-tint)", ["--kpim-yoy-cur-head"] = "var(--shire-tint-head)",
        ["--kpim-sp-col"] = "var(--shire-purple)", ["--kpim-sp-col-on"] = "var(--shire-deep)",
        ["--kpim-ba-s1"] = "var(--shire-deep)", ["--kpim-ba-s2"] = "var(--shire-magenta)",
        ["--kpim-ba-s3"] = "var(--shire-purple)", ["--kpim-ba-s4"] = "var(--shire-lilac)", ["--kpim-ba-part"] = "var(--shire-lilac)",
    };

    private static string ShireDefault(string token)
    {
        if (ShireTokenDefaults.TryGetValue(token, out var v)) return v;
        if (TokenDefaults.TryGetValue(token, out v)) return v;
        if (token.StartsWith("--chart-")) return DesktopCharts[int.Parse(token[8..]) - 1];
        if (token.StartsWith("--kpim-ba-s")) return PhoneCharts[int.Parse(token[11..]) - 1];
        return "initial";
    }

    /// <summary>
    /// The CSS for the saved colours: Kettering's on the root, Mobberley's inside .kpi-shire, and every Kettering token
    /// put back to Mobberley's own inside .kpi-shire. Empty when nothing is saved.
    /// </summary>
    public static string Css(IReadOnlyDictionary<string, string> kettering, IReadOnlyDictionary<string, string> mobberley)
    {
        var root = new StringBuilder();
        var shire = new Dictionary<string, string>();
        var rules = new StringBuilder();

        foreach (var (key, hex) in kettering)
        {
            if (Find(Kettering, key) is not { } item || !IsHex(hex)) continue;
            foreach (var t in item.Tokens) root.Append(t).Append(':').Append(hex).Append(';');
            if (item.Rules is not null) rules.Append(item.Rules(hex));
            if (!item.PfEverywhere) foreach (var t in item.Tokens) shire[t] = ShireDefault(t);
        }
        foreach (var (key, hex) in mobberley)
        {
            if (Find(Mobberley, key) is not { } item || !IsHex(hex)) continue;
            foreach (var t in item.Tokens) shire[t] = hex;
            if (item.Rules is not null) rules.Append(item.Rules(hex));
        }

        var css = new StringBuilder();
        if (root.Length > 0) css.Append(":root:root{").Append(root).Append('}');
        if (shire.Count > 0) css.Append(".kpi-shire.kpi-shire{").Append(string.Concat(shire.Select(p => $"{p.Key}:{p.Value};"))).Append('}');
        css.Append(rules);
        return css.ToString();
    }

    // ---- colour arithmetic ----

    public static bool IsHex(string? s) =>
        s is { Length: 7 } && s[0] == '#' && s.Skip(1).All(Uri.IsHexDigit);

    public static string Normalise(string hex) => hex.Trim().ToLowerInvariant();

    private static (int R, int G, int B) Rgb(string hex) => (
        int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber),
        int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber),
        int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber));

    private static string Hex(double r, double g, double b) =>
        $"#{(int)Math.Round(r):x2}{(int)Math.Round(g):x2}{(int)Math.Round(b):x2}";

    /// <summary>A tint: the colour at <paramref name="strength"/> (1 = itself) over white - as the picker shows.</summary>
    public static string Tint(string hex, double strength)
    {
        var (r, g, b) = Rgb(hex);
        return Hex(255 - (255 - r) * strength, 255 - (255 - g) * strength, 255 - (255 - b) * strength);
    }

    /// <summary>The colour taken <paramref name="amount"/> of the way to black.</summary>
    public static string Shade(string hex, double amount)
    {
        var (r, g, b) = Rgb(hex);
        return Hex(r * (1 - amount), g * (1 - amount), b * (1 - amount));
    }

    /// <summary>The heat map's seven bands from one colour, palest to darkest (as today's are from the site red).</summary>
    public static string[] HeatScale(string hex) => new[]
    {
        Tint(hex, .14), Tint(hex, .36), Tint(hex, .58), Tint(hex, .8), hex, Shade(hex, .24), Shade(hex, .48)
    };
}
