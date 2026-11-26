namespace Kpi.Web.Reports;

// Auto Approval (26 Nov 2026, Steve - for Stuart: "monitor the leads that get auto approved"), from Rpt.spAutoApproval.

/// <summary>One division's applications in the period: decided -> eligible -> auto approved -> paid out.</summary>
public class AutoApprovalFunnelRow
{
    public int DivisionId { get; set; }
    public string Division { get; set; } = "";
    public int Decided { get; set; }
    public int Eligible { get; set; }
    public int AutoApproved { get; set; }
    public int PaidOut { get; set; }
    public decimal DecidedAF { get; set; }
    public decimal EligibleAF { get; set; }
    public decimal AutoApprovedAF { get; set; }
    public decimal PaidOutAF { get; set; }

    public int[] Counts => [Decided, Eligible, AutoApproved, PaidOut];
    public decimal[] Amounts => [DecidedAF, EligibleAF, AutoApprovedAF, PaidOutAF];
}

/// <summary>Eligible but not auto approved: what a person decided instead.</summary>
public class AutoApprovalOutcome
{
    public int ApprovedByHand { get; set; }
    public int DeclinedByHand { get; set; }
    public int Other { get; set; }
    public decimal ApprovedAF { get; set; }
    public decimal DeclinedAF { get; set; }
}

/// <summary>One reason and how many applications had it; Total = applications with any reason (one can have several).</summary>
public class AutoApprovalReasonRow
{
    public string Reason { get; set; } = "";
    public int SortOrder { get; set; }
    public int Applications { get; set; }
    public int Total { get; set; }
    /// <summary>The amount financed behind the reason, and behind every application in the list.</summary>
    public decimal AF { get; set; }
    public decimal TotalAF { get; set; }
}

/// <summary>What lay behind a reason (a profession, a facility, an amount band ...) and how many applications.</summary>
public class AutoApprovalReasonDetailRow
{
    public bool Eligible { get; set; }
    public string Reason { get; set; } = "";
    public string Detail { get; set; } = "";
    public int Applications { get; set; }
    public decimal AF { get; set; }
}

/// <summary>
/// Paid-out deals since auto approval began, each counted once at its worst. Kind "Rule" (by rule name) or "Control"
/// (every other deal, not auto approved).
/// </summary>
public class AutoApprovalPerformanceRow
{
    public string Kind { get; set; } = "";
    public string? RuleName { get; set; }
    public bool IsCurrent { get; set; }
    public int PaidOut { get; set; }
    public int SeriousArrears { get; set; }
    public int Terminated { get; set; }
    public int BadDebt { get; set; }
    public decimal WrittenOff { get; set; }
    /// <summary>Amount financed: every deal in the row; and those paid out in the last 12 months.</summary>
    public decimal AF { get; set; }
    public decimal AF12m { get; set; }

    public int Adverse => SeriousArrears + Terminated + BadDebt;
    public decimal Rate => PaidOut > 0 ? (decimal)Adverse / PaidOut : 0m;
}

/// <summary>Everything the page shows for one division and period (cached as one).</summary>
public class AutoApprovalData
{
    public List<AutoApprovalFunnelRow> Funnel { get; set; } = new();
    public AutoApprovalOutcome Outcome { get; set; } = new();
    public List<AutoApprovalReasonRow> NotEligible { get; set; } = new();
    public List<AutoApprovalReasonRow> EligibleNotAuto { get; set; } = new();
    public List<AutoApprovalReasonDetailRow> ReasonDetails { get; set; } = new();

    /// <summary>A reason's hover: its name, then up to five details with their counts (or amounts), the rest as "Others".</summary>
    public string ReasonTip(AutoApprovalReasonRow reason, bool byAmount)
    {
        string Fig(int n, decimal af) => byAmount ? Kpi.Web.Components.Pages.Kpis.AutoApproval.AutoApprovalCharts.Money(af) : n.ToString("N0");
        var lines = new List<string> { $"{reason.Reason}: {Fig(reason.Applications, reason.AF)}" };
        var rows = ReasonDetails.Where(d => d.Reason == reason.Reason).OrderByDescending(d => byAmount ? d.AF : d.Applications).ToList();
        lines.AddRange(rows.Take(5).Select(d => $"{d.Detail}: {Fig(d.Applications, d.AF)}"));
        if (rows.Count > 5) lines.Add($"Others ({rows.Count - 5}): {Fig(rows.Skip(5).Sum(d => d.Applications), rows.Skip(5).Sum(d => d.AF))}");
        return string.Join("\n", lines);
    }
    public List<AutoApprovalPerformanceRow> Performance { get; set; } = new();
}
