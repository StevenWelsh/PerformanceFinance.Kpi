using Kpi.Web.Components.Shared.Kpis;
using Kpi.Web.Data;
using Kpi.Web.Data.Kpis;
using Kpi.Web.Reports;
using Microsoft.Extensions.Caching.Memory;

namespace Kpi.Web.Shire;

/// <summary>A (year, month or quarter) of Shire's won sales; TotalToDate = up to today's day of the year (as PF's).</summary>
public sealed class ShireYearOnYearRow
{
    public int Year { get; set; }
    public int Period { get; set; }
    public decimal Total { get; set; }
    public decimal TotalToDate { get; set; }
}

/// <summary>One salesperson's (opportunity owner's) sales for a year.</summary>
public sealed class ShireLeagueRow
{
    public int Yr { get; set; }
    public string OwnerId { get; set; } = "";
    public string Salesperson { get; set; } = "";
    public decimal MeasureValue { get; set; }
    public int Deals { get; set; }
}

/// <summary>A Shire salesperson for the filter: their Salesforce user Id and name.</summary>
public sealed class ShireOptionRow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>
/// The Shire reports' data (3 Nov 2026, Steve): the Shire procedures (Kpi.Data/Shire/001_, 002_ .sql) over the
/// won deals the copy brings from Shire's Salesforce. Their filters - Measure (Amount Financed / Income) and Proposed to PF
/// (All / Yes / No) - are shared by Shire Year on Year and Shire Sales Performance.
/// </summary>
public sealed class ShireReports(KpiDb db)
{
    /// <summary>The first year Shire's Salesforce has sales for (July 2021).</summary>
    public const int FirstYear = 2021;

    public static readonly KpiOption[] MeasureOptions = { new("AF", "Amount Financed"), new("Income", "Income") };
    public static readonly KpiOption[] ProposedToPFOptions = { new("", "All"), new("1", "Yes"), new("0", "No") };

    public static string MeasureLabel(string measure) => measure == "Income" ? "Income" : "Amount Financed";

    /// <summary>A Shire report's title on the full view, "Mobberley: Year on Year" (Steve, 3 Nov 2026), with
    /// <see cref="Source"/> in small type under it.</summary>
    public static string Title(string report) => $"{KpiReports.Shire}: {report}";

    public const string Source = "Taken from Salesforce";

    /// <summary>The phone bar's small line: there the title is the report's name alone and Mobberley moves down here, as
    /// "Mobberley: Business Analysis" didn't fit beside the menu button, star and dates (Steve, 3 Nov 2026).</summary>
    public static string PhoneSource => $"{KpiReports.Shire} · {Source}";

    /// <summary>"" = all, "1" = proposed to PF, "0" = not.</summary>
    public static bool? ProposedToPF(string? choice) => choice switch { "1" => true, "0" => false, _ => null };

    public List<ShireYearOnYearRow> GetYearOnYear(string measure, string interval, string? ownerId, bool? proposedToPF) =>
        db.Report<ShireYearOnYearRow>("Shire.spYearOnYear", new
        {
            Measure = measure, Interval = interval, OwnerId = string.IsNullOrEmpty(ownerId) ? null : ownerId, ProposedToPF = proposedToPF
        });

    public List<ShireLeagueRow> GetLeague(int year, string measure, bool? proposedToPF) =>
        db.Report<ShireLeagueRow>("Shire.spSalesLeague", new { Year = year, Measure = measure, ProposedToPF = proposedToPF });

    public List<ShireOptionRow> Salespeople() => db.Report<ShireOptionRow>("Shire.spSalespeople");

    // ---- Shire Business Analysis (002_ShireBusinessAnalysis.sql) ----

    /// <summary>The view that is PF's four lender cards; the others are a dimension's table, donut and trend.</summary>
    public const string LenderDimension = "Lender";

    /// <summary>Shire Business Analysis's dimensions (Steve): no Arrangement or Division; Purpose is Shire's Loan Purpose.</summary>
    public static readonly string[] Dimensions = { "Profession", "Facility", "Purpose", LenderDimension };

    /// <param name="dimension">"Profession" | "Facility" | "Purpose" (Shire.spBusinessAnalysis)</param>
    public List<BusinessAnalysisRow> GetBusinessAnalysis(string dimension, string measure, int quarter, bool? proposedToPF) =>
        db.Report<BusinessAnalysisRow>("Shire.spBusinessAnalysis",
            new { Dimension = dimension, Measure = measure, Quarter = quarter, ProposedToPF = proposedToPF });

    /// <summary>Each lender's year beside its own year before, to today's date for this year - shaped by PF's
    /// <see cref="BusinessAnalysisLenderService.Build"/> from the chosen measure's figures.</summary>
    public List<BusinessAnalysisLenderRow> GetBusinessAnalysisByLender(string measure, int quarter, bool? proposedToPF, int reportYear)
    {
        var income = measure == "Income";
        var figures = db.Report<LenderFigureRow>("Shire.spBusinessAnalysisByLender", new { ProposedToPF = proposedToPF })
            .Select(f => new BusinessAnalysisLenderFigureRow
            {
                Year = f.Year, Quarter = f.Quarter, Lender = f.Lender,
                AF = income ? f.Income : f.AF, AFToDate = income ? f.IncomeToDate : f.AFToDate
            });
        return BusinessAnalysisLenderService.Build(figures, "AF", quarter, reportYear);
    }

    private sealed class LenderFigureRow
    {
        public int Year { get; set; }
        public int Quarter { get; set; }
        public string Lender { get; set; } = "";
        public decimal AF { get; set; }
        public decimal AFToDate { get; set; }
        public decimal Income { get; set; }
        public decimal IncomeToDate { get; set; }
    }

    /// <summary>The Shire reports' "as at": today in the UK (their own reports are relative to today).</summary>
    public DateTime ReportDate() => db.Report<StatusRow>("Shire.spDataStatus").Single().ReportDate;

    private sealed class StatusRow { public DateTime ReportDate { get; set; } }
}

/// <summary>
/// When the site last checked Shire's Salesforce, and whether that's overdue (Shire.spDataStatus: no successful check for
/// 45 minutes in office hours) - the Shire reports' "As at" line, as <see cref="KpiDataStatus"/> is the CMS reports'.
/// </summary>
public sealed class ShireDataStatus(KpiSessionFactory sessions, IMemoryCache cache)
{
    private const string Key = "kpi:shire-data-status";

    public KpiDataStatus.Status Get()
    {
        if (cache.TryGetValue(Key, out KpiDataStatus.Status? hit) && hit is not null) return hit;
        try
        {
            using var db = sessions.Create();
            var row = db.Report<Row>("Shire.spDataStatus").SingleOrDefault();
            return cache.Set(Key, new KpiDataStatus.Status(row?.LastCheckUtc, row?.Stale ?? false), TimeSpan.FromMinutes(1));
        }
        catch (Exception) { return new KpiDataStatus.Status(null, false); }
    }

    /// <summary>The hover text when the checks have stopped.</summary>
    public static string? StaleNote(KpiDataStatus.Status status) => !status.Stale ? null
        : $"Not checked with Shire's Salesforce since {UkTime.Show(status.LastCopyUtc, "HH:mm on dd MMM")} - the Shire copy may have stopped.";

    private sealed class Row
    {
        public DateTime? LastCheckUtc { get; set; }
        public bool Stale { get; set; }
    }
}

/// <summary>
/// New Shire figures have landed (ShirePull): open Shire reports update within seconds, as KpiFiguresSignal does for CMS's.
/// <see cref="Generation"/> counts the copies since the site started and is part of every Shire report's cache key, so a
/// copy makes every cached Shire figure obsolete at once (3 Nov 2026: a view loaded before the first copy kept showing
/// nothing - the cache's remembered-views list it was dropped from had already been cleared by the start-up pre-load).
/// </summary>
public sealed class ShireFiguresSignal
{
    private int _generation;

    public event Action? Landed;

    public int Generation => Volatile.Read(ref _generation);

    public void Raise()
    {
        Interlocked.Increment(ref _generation);
        Landed?.Invoke();
    }
}
