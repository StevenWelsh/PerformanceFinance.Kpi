using Kpi.Web.Data;

namespace Kpi.Web.Reports;

/// <summary>
/// The two supplier reports (Suppliers Year on Year, Supplier Development), both shaped from one set of figures:
/// each supplier's amount financed per year (Rpt.spSupplierFigures).
/// </summary>
public sealed class SupplierService(KpiDb db)
{
    // Klipfolio's Suppliers Year on Year leaves these out by name: placeholders, not suppliers.
    private static readonly HashSet<string> NotSuppliers = new(new[] { "Loan", "HMRC", "T B A" }, StringComparer.OrdinalIgnoreCase);

    public List<SupplierYearRow> GetFigures() => db.Report<SupplierYearRow>("Rpt.spSupplierFigures");

    /// <summary>The names of the given salespeople (active ones: the cards are for the current team).</summary>
    public Dictionary<int, string> GetSalespeople(IEnumerable<int> salesIds)
    {
        var ids = salesIds.ToHashSet();
        return db.Options("Salespeople").Where(s => ids.Contains(s.Id)).ToDictionary(s => s.Id, s => s.Name);
    }

    public static SupplierYoyYearResult BuildYearOnYear(IEnumerable<SupplierYearRow> figures, int year, int take = 30)
    {
        var lines = figures
            .Where(f => f.Year == year && !NotSuppliers.Contains(f.Supplier.Trim()))
            .GroupBy(f => new { Name = f.Supplier.Trim().ToUpperInvariant(), Profession = f.Profession.ToUpperInvariant() })
            .Select(g => new { First = g.First(), Amount = g.Sum(f => f.AmountFinanced) })
            .OrderByDescending(x => x.Amount)
            .Take(take)
            .Select((x, i) => new SupplierRankResult
            {
                Rank = i + 1,
                Supplier = x.First.Supplier.Trim(),
                Profession = x.First.Profession,
                AmountFinanced = x.Amount
            })
            .ToList();
        return new SupplierYoyYearResult { Year = year, Lines = lines };
    }

    public static SupplierDevelopmentResult BuildDevelopment(IEnumerable<SupplierYearRow> figures, int salesId,
                                                             string salesperson, int year)
    {
        var theirs = figures
            .Where(f => f.AccountManagerId == salesId && !f.ManagedByHQ)
            .ToList();

        var years = Enumerable.Range(year - 5, 6).Reverse()
            .Select(y =>
            {
                var ofYear = theirs.Where(f => f.Year == y).OrderByDescending(f => f.AmountFinanced).ToList();
                return new SupplierDevelopmentYearResult
                {
                    Year = y,
                    Top10AmountFinanced = ofYear.Take(10).Sum(f => f.AmountFinanced),
                    ActiveSuppliers = ofYear.Count,
                    TotalAmountFinanced = ofYear.Sum(f => f.AmountFinanced)
                };
            })
            .ToList();

        var top10 = theirs
            .Where(f => f.Year == year)
            .OrderByDescending(f => f.AmountFinanced)
            .Take(10)
            .Select((f, i) => new SupplierRankResult
            {
                Rank = i + 1,
                Supplier = f.Supplier.Trim(),
                Profession = f.Profession,
                AmountFinanced = f.AmountFinanced
            })
            .ToList();

        return new SupplierDevelopmentResult
        {
            SalesId = salesId, Salesperson = salesperson, Year = year, Top10 = top10, Years = years
        };
    }
}
