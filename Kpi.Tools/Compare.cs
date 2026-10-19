using System.Data;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Kpi.Tools;

/// <summary>
/// "compare": every report as CMS produces it today (the WebKPI procedures, the desktop's Bad Debt and Exposure, and
/// the two SQL files CMS.Core embeds) against the KPI database's Rpt version, for each filter value the pages offer.
/// Read-only on both sides. Run it straight after a push, so both read the same data.
/// </summary>
public static class Compare
{
    sealed record Case(string Report, string Filters, string CmsSql, string KpiSql, int? CmsTop = null);

    // a value that differs by no more than a penny AND by no more than 0.1% is rounding, not a different answer
    const decimal Penny = 0.01m, Relative = 0.001m;

    public static async Task<int> RunAsync(string cmsConnection, string kpiConnection, string? cmsRepo, string? only)
    {
        await using var cms = new SqlConnection(cmsConnection);
        await using var kpi = new SqlConnection(kpiConnection);
        await cms.OpenAsync();
        await kpi.OpenAsync();

        var cases = (await BuildCases(kpi, cmsRepo))
            .Where(c => only is null || c.Report.Contains(only, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Console.WriteLine($"CMS  {cms.DataSource} / {cms.Database}");
        Console.WriteLine($"KPI  {kpi.DataSource} / {kpi.Database}");
        Console.WriteLine($"{cases.Count} cases\n");

        int matched = 0, rounding = 0, failed = 0;
        foreach (var group in cases.GroupBy(c => c.Report))
        {
            var clock = Stopwatch.StartNew();
            var problems = new List<string>();
            int groupMatched = 0;
            Console.Write($"{group.Key,-28}");
            foreach (var c in group)
            {
                string? problem;
                try
                {
                    var cmsTask = Load(cms, c.CmsSql);
                    var kpiTask = Load(kpi, c.KpiSql);
                    var (same, closeCells, detail) = Diff(await cmsTask, await kpiTask, c.CmsTop);
                    problem = same ? null : detail;
                    if (same && closeCells > 0) rounding++;
                }
                catch (Exception ex)
                {
                    problem = "      ERROR " + ex.Message.Split('\n')[0];
                }
                Console.Write(problem is null ? "." : "x");
                if (problem is null) groupMatched++;
                else problems.Add($"    {c.Filters}\n{problem}");
            }
            matched += groupMatched;
            failed += problems.Count;
            Console.WriteLine($"  {groupMatched}/{group.Count()} match ({clock.Elapsed.TotalSeconds:0} s)");
            foreach (var p in problems.Take(6)) Console.WriteLine(p);
            if (problems.Count > 6) Console.WriteLine($"    ... and {problems.Count - 6} more");
        }

        Console.WriteLine($"\n{matched} of {cases.Count} match" +
                          (rounding > 0 ? $" ({rounding} with differences of a penny or less)" : "") +
                          (failed > 0 ? $"; {failed} differ." : "."));
        return failed == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // the cases: each report with the filter values its page offers
    // ---------------------------------------------------------------------------------------------------------------
    static async Task<List<Case>> BuildCases(SqlConnection kpi, string? cmsRepo)
    {
        var reportYear = Convert.ToInt32(await Scalar(kpi, "SELECT YEAR(ReportDate) FROM Ref.Setting WHERE SettingId = 1"));
        var divisions = await Ints(kpi, "SELECT DISTINCT DivisionId FROM Fact.Lead WHERE DivisionId IS NOT NULL ORDER BY 1");
        var salespeople = await Ints(kpi,
            $"SELECT DISTINCT SalesId FROM Fact.Lead WHERE SalesId IS NOT NULL AND dtPayReceived >= '{reportYear - 1}0101' ORDER BY 1");
        var arrangements = await Strings(kpi, "SELECT DISTINCT Arrangement FROM Fact.Lead WHERE Arrangement IS NOT NULL ORDER BY 1");
        var lenders = await Ints(kpi, "SELECT DISTINCT FundLenderId FROM Fact.Exposure WHERE FundLenderId <> 0 ORDER BY 1");
        var allDivisions = new[] { -1 }.Concat(divisions).ToList();
        var measures = new[] { "AF", "TFI" };
        var cases = new List<Case>();

        void Add(string report, string filters, string cmsSql, string kpiSql, int? cmsTop = null) =>
            cases.Add(new Case(report, filters, cmsSql, kpiSql, cmsTop));
        void Both(string report, string filters, string proc, string args) =>
            Add(report, filters, $"EXEC WebKPI.{proc} {args}", $"EXEC Rpt.{proc} {args}");

        foreach (var p in new[] { "Year", "Quarter", "Month" })
        {
            foreach (var o in new[] { "NULL", "1", "0" })
                Both("Sales Summary", $"{p}, optical {o}", "spSalesSummary",
                     $"@AnalysisPeriod = N'{p}', @DivisionId = -1, @SalespersonId = -1, @Optical = {o}");
            foreach (var d in divisions)
                Both("Sales Summary", $"{p}, division {d}", "spSalesSummary",
                     $"@AnalysisPeriod = N'{p}', @DivisionId = {d}, @SalespersonId = -1, @Optical = NULL");
            foreach (var s in salespeople)
                Both("Sales Summary", $"{p}, salesperson {s}", "spSalesSummary",
                     $"@AnalysisPeriod = N'{p}', @DivisionId = -1, @SalespersonId = {s}, @Optical = NULL");
        }

        foreach (var m in measures)
        {
            foreach (var i in new[] { "Month", "Quarter" })
                Both("Year on Year", $"{m} by {i}", "spYearOnYear",
                     $"@Measure = N'{m}', @Interval = N'{i}', @DivisionId = -1, @SalespersonId = -1, @Arrangement = NULL");
            foreach (var d in divisions)
                Both("Year on Year", $"{m}, division {d}", "spYearOnYear",
                     $"@Measure = N'{m}', @Interval = N'Month', @DivisionId = {d}, @SalespersonId = -1, @Arrangement = NULL");
            foreach (var s in salespeople)
                Both("Year on Year", $"{m}, salesperson {s}", "spYearOnYear",
                     $"@Measure = N'{m}', @Interval = N'Month', @DivisionId = -1, @SalespersonId = {s}, @Arrangement = NULL");
            foreach (var a in arrangements)
                Both("Year on Year", $"{m}, {a}", "spYearOnYear",
                     $"@Measure = N'{m}', @Interval = N'Month', @DivisionId = -1, @SalespersonId = -1, @Arrangement = {Str(a)}");
        }

        foreach (var dim in new[] { "Profession", "Facility", "Arrangement", "Division" })
            foreach (var m in measures)
                for (var q = 0; q <= 4; q++)
                    Both("Business Analysis", $"{dim}, {m}, quarter {q}", "spBusinessAnalysis",
                         $"@Dimension = N'{dim}', @Measure = N'{m}', @Quarter = {q}");

        if (EmbeddedSql(cmsRepo, "BusinessAnalysisByLender.sql") is { } byLender)
            Add("Business Analysis Lender", "all", byLender, "EXEC Rpt.spBusinessAnalysisByLender");

        for (var y = 2020; y <= reportYear; y++)
            foreach (var m in measures)
            {
                Both("Sales League Table", $"{y} {m}", "spSalesLeagueTable", $"@Year = {y}, @Measure = N'{m}'");
                Both("Sales League Timelapse", $"{y} {m}", "spSalesLeagueTimelapse", $"@Year = {y}, @Measure = N'{m}'");
                Both("Sales Targets", $"{y} {m}", "spSalesTargets", $"@Year = {y}, @Measure = N'{m}'");
            }

        foreach (var s in new[] { -1 }.Concat(salespeople))
            Both("Sales Productivity", $"salesperson {s}", "spSalesProductivity", $"@SalespersonId = {s}");

        foreach (var g in new[] { "Annual", "Quarter", "Month", "Transaction" })
            foreach (var d in allDivisions)
                Both("Record Performances", $"{g}, division {d}", "spRecordPerformances", $"@Grain = N'{g}', @DivisionId = {d}");

        foreach (var p in new[] { "M_This", "Q_This", "Y_This" })
            foreach (var d in allDivisions)
                Both("Minimum IRR", $"{p}, division {d}", "spMinIRRAnalysis", $"@AnalysisPeriod = N'{p}', @DivisionId = {d}");

        foreach (var d in allDivisions)
        {
            Both("Margin Analysis", $"by year, division {d}", "spMarginAnalysisByYear", $"@DivisionId = {d}");
            foreach (var r in new[] { 0, 1 })
                Both("Margin Analysis", $"by month, rolling {r}, division {d}", "spMarginAnalysisByMonth",
                     $"@DivisionId = {d}, @RollingMonth = {r}");
            foreach (var days in new[] { 7, 15, 30, 60, 90 })
                Both("Margin Analysis", $"last {days} days, division {d}", "spMarginAnalysisByDays",
                     $"@Days = {days}, @DivisionId = {d}");
        }

        foreach (var a in new[] { "All", "Brokered", "OwnBook" })
            foreach (var years in new[] { "", $"{reportYear}", $"{reportYear - 1}", $"{reportYear - 1},{reportYear}" })
                Both("Heat Map", $"{a}, years '{years}'", "spHeatMap", $"@Years = '{years}', @Arrangement = '{a}'");

        if (EmbeddedSql(cmsRepo, "SupplierFigures.sql") is { } suppliers)
            Add("Supplier Figures", "all", suppliers, "EXEC Rpt.spSupplierFigures");

        foreach (var a in new[] { 0, 1, 2 })
            Add("Bad Debt", $"applications {a}", $"EXEC OB.AnnualBadDebtList @IsAutoApproved = {a}",
                $"EXEC Rpt.spBadDebt @IsAutoApproved = {a}");

        foreach (var g in new[] { "0", "1", "NULL" })
            foreach (var l in new int?[] { null }.Concat(lenders.Select(x => (int?)x)))
                Add("Exposure", $"grouping {g}, lender {(l?.ToString() ?? "all")}",
                    $"EXEC Reports.Exposure @CustomerBusinessGroup = {g}, @FundLenderIds = {(l is null ? "NULL" : $"N'{l}'")}, @FundIds = NULL, @OBBrokered = 1",
                    $"EXEC Rpt.spExposure @CustomerBusinessGroup = {g}, @FundLenderId = {(l?.ToString() ?? "NULL")}",
                    cmsTop: 10);

        return cases;
    }

    // CMS.Core's embedded report files, read from the CMS repository: the CmsRepo setting, else the folder beside this
    // repository. A report whose file cannot be found is left out, with a note.
    static string? EmbeddedSql(string? cmsRepo, string file)
    {
        var root = cmsRepo;
        if (root is null)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PerformanceFinance.Kpi.slnx"))) dir = dir.Parent;
            if (dir?.Parent is not null) root = Path.Combine(dir.Parent.FullName, "performance-finance-cms");
        }
        var path = root is null ? null : Path.Combine(root, "CMS.Core", "Kpis", "Sql", file);
        if (path is not null && File.Exists(path)) return File.ReadAllText(path);
        Console.WriteLine($"(skipping {file}: not found{(path is null ? "" : " at " + path)}; set CmsRepo to the CMS repository)");
        return null;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // comparing two results
    // ---------------------------------------------------------------------------------------------------------------
    // columns the KPI site adds on purpose: Rpt.spYearOnYear's TotalToDate (the current month / year against the same
    // DATE last year, as Klipfolio - CMS.Web's WebKPI version compares whole months)
    static readonly HashSet<string> KpiOnlyColumns = new(StringComparer.OrdinalIgnoreCase) { "TotalToDate" };

    static (bool Same, int CloseCells, string Detail) Diff(DataTable cms, DataTable kpi, int? cmsTop)
    {
        var lines = new List<string>();
        var cmsNames = cms.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList();
        var kpiNames = kpi.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList();
        var common = cmsNames.Where(kpi.Columns.Contains).ToList();
        var onlyCms = cmsNames.Except(common, StringComparer.OrdinalIgnoreCase).ToList();
        var onlyKpi = kpiNames.Except(common, StringComparer.OrdinalIgnoreCase).ToList();
        // the KPI side may leave a column out on purpose (Exposure's LeadList); a column it adds is a mistake - except
        // those added on purpose, which CMS doesn't have to compare with (Year on Year's TotalToDate, 19 Oct 2026)
        onlyKpi.RemoveAll(n => KpiOnlyColumns.Contains(n));
        if (onlyKpi.Count > 0) lines.Add($"      columns only in KPI: {string.Join(", ", onlyKpi)}");

        var cmsRows = cms.Rows.Cast<DataRow>().Take(cmsTop ?? int.MaxValue).Select(r => common.Select(n => r[n]).ToArray()).ToList();
        var kpiRows = kpi.Rows.Cast<DataRow>().Select(r => common.Select(n => r[n]).ToArray()).ToList();
        if (cmsRows.Count != kpiRows.Count) lines.Add($"      rows: CMS {cmsRows.Count}, KPI {kpiRows.Count}");

        var (bad, close, examples) = CompareRows(cmsRows, kpiRows, common);
        if (bad > 0 && cmsRows.Count == kpiRows.Count)
        {
            // the same rows in another order is worth knowing, but it is not a different answer
            var (sortedBad, sortedClose, _) = CompareRows(Sorted(cmsRows), Sorted(kpiRows), common);
            if (sortedBad == 0)
            {
                lines.Add("      same rows, different order");
                return (onlyKpi.Count == 0, sortedClose, string.Join("\n", lines));
            }
        }
        if (bad > 0) lines.Add($"      {bad} values differ, e.g.");
        lines.AddRange(examples);
        var same = bad == 0 && cmsRows.Count == kpiRows.Count && onlyKpi.Count == 0;
        if (!same && onlyCms.Count > 0) lines.Add($"      (CMS also returns {string.Join(", ", onlyCms)}, not compared)");
        return (same, close, string.Join("\n", lines));
    }

    static (int Bad, int Close, List<string> Examples) CompareRows(List<object[]> a, List<object[]> b, List<string> names)
    {
        int bad = 0, close = 0;
        var examples = new List<string>();
        for (var r = 0; r < Math.Min(a.Count, b.Count); r++)
            for (var c = 0; c < names.Count; c++)
            {
                var v = Same(a[r][c], b[r][c]);
                if (v == 1) close++;
                if (v < 2) continue;
                bad++;
                if (examples.Count < 4)
                    examples.Add($"      row {r + 1} {names[c]}: CMS {Show(a[r][c])}, KPI {Show(b[r][c])}   [{RowLabel(a[r], names)}]");
            }
        return (bad, close, examples);
    }

    // 0 = equal, 1 = within a penny (rounding), 2 = different
    static int Same(object x, object y)
    {
        if (x is DBNull || y is DBNull) return x is DBNull && y is DBNull ? 0 : 2;
        if (Number(x) is { } dx && Number(y) is { } dy)
        {
            var diff = Math.Abs(dx - dy);
            if (diff == 0) return 0;
            return diff <= Penny && diff <= Relative * Math.Max(Math.Abs(dx), Math.Abs(dy)) ? 1 : 2;
        }
        if (x is DateTime tx && y is DateTime ty) return tx == ty ? 0 : 2;
        return string.Equals(Convert.ToString(x, CultureInfo.InvariantCulture)?.TrimEnd(),
                             Convert.ToString(y, CultureInfo.InvariantCulture)?.TrimEnd(), StringComparison.Ordinal) ? 0 : 2;
    }

    static decimal? Number(object v) => v switch
    {
        decimal d => d,
        int or long or short or byte => Convert.ToDecimal(v),
        bool b => b ? 1 : 0,
        double or float when Math.Abs(Convert.ToDouble(v)) < 7.9e27 => (decimal)Convert.ToDouble(v),
        _ => null
    };

    static List<object[]> Sorted(List<object[]> rows) =>
        rows.OrderBy(r => string.Join("|", r.Select(Key)), StringComparer.Ordinal).ToList();

    static string Key(object v) => Number(v) is { } d ? Math.Round(d, 2).ToString("0.00", CultureInfo.InvariantCulture) : Show(v);

    static string Show(object v) => v switch
    {
        DBNull => "NULL",
        DateTime t => t.ToString(t.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        string s => $"'{s}'",
        _ => Number(v) is { } d ? d.ToString("0.####", CultureInfo.InvariantCulture) : v.ToString() ?? ""
    };

    // the row's text and date columns, so a difference says which row it is
    static string RowLabel(object[] row, List<string> names) =>
        string.Join(", ", names.Select((n, i) => (n, v: row[i])).Where(p => p.v is string or DateTime).Take(3)
            .Select(p => $"{p.n} {Show(p.v)}"));

    // ---------------------------------------------------------------------------------------------------------------
    static async Task<DataTable> Load(SqlConnection c, string sql)
    {
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 900 };
        await using var reader = await cmd.ExecuteReaderAsync();
        var table = new DataTable();
        table.Load(reader);
        return table;
    }

    static async Task<object?> Scalar(SqlConnection c, string sql)
    {
        await using var cmd = new SqlCommand(sql, c);
        return await cmd.ExecuteScalarAsync();
    }

    static async Task<List<int>> Ints(SqlConnection c, string sql) =>
        (await Load(c, sql)).Rows.Cast<DataRow>().Select(r => Convert.ToInt32(r[0])).ToList();

    static async Task<List<string>> Strings(SqlConnection c, string sql) =>
        (await Load(c, sql)).Rows.Cast<DataRow>().Select(r => Convert.ToString(r[0])!).ToList();

    static string Str(string? s) => s is null ? "NULL" : "N'" + s.Replace("'", "''") + "'";
}
