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

    /// <summary>A reason's pop-up: its name (bold), what it means, a space, then its figure and up to five details with
    /// theirs (or amounts), the rest as "Others" (Steve, 27 Nov 2026).</summary>
    public string ReasonTip(AutoApprovalReasonRow reason, bool byAmount)
    {
        string Fig(int n, decimal af) => byAmount ? Kpi.Web.Components.Pages.Kpis.AutoApproval.AutoApprovalCharts.Money(af) : n.ToString("N0");
        var lines = new List<string> { reason.Reason };
        if (Meanings.TryGetValue(reason.Reason, out var meaning)) lines.Add(meaning);
        lines.Add("");
        lines.Add(byAmount ? Fig(reason.Applications, reason.AF) : reason.Applications.ToString("N0") + (reason.Applications == 1 ? " application" : " applications"));
        var rows = ReasonDetails.Where(d => d.Reason == reason.Reason).OrderByDescending(d => byAmount ? d.AF : d.Applications).ToList();
        lines.AddRange(rows.Take(5).Select(d => $"{d.Detail}: {Fig(d.Applications, d.AF)}"));
        if (rows.Count > 5) lines.Add($"Others ({rows.Count - 5}): {Fig(rows.Skip(5).Sum(d => d.Applications), rows.Skip(5).Sum(d => d.AF))}");
        return string.Join("\n", lines);
    }

    /// <summary>What each reason means, in plain words (the names come from KpiExport.vAutoApprovalReason).</summary>
    private static readonly Dictionary<string, string> Meanings = new()
    {
        ["Bad debt or serious arrears ever"] = "The customer has had bad debt or serious arrears with us.",
        ["Currently in arrears"] = "The customer is in arrears with us now, for longer than the rule allows.",
        ["Late payments over the rule's limit"] = "The customer has had more late payments than the rule allows.",
        ["Not for this type of customer (business / personal)"] = "The rule is for business customers only, or personal only, and this customer is the other kind.",
        ["Company type not on the rule"] = "The rule doesn't cover this company type (e.g. Limited, sole trader).",
        ["Profession not on the rule"] = "The rule doesn't cover the customer's profession.",
        ["Facility not covered"] = "The rule doesn't cover this type of finance (e.g. loan, lease, HP).",
        ["Facility balance plus quote over the rule's limit"] = "What the customer already owes on this type of finance (e.g. loans), plus this quote, is over the rule's limit.",
        ["Introducer scheme"] = "The rule is for introducer scheme leads, and this one isn't.",
        ["Bespoke quote"] = "The quote type (bespoke or standard) isn't one the rule allows.",
        ["Balance plus quote over the rule's credit limit"] = "Everything the customer already owes, on all types of finance, plus this quote, is over the rule's credit limit.",
        ["Quote amount over the rule's limit"] = "The quote on its own is over the rule's limit.",
        ["Term over the rule's limit"] = "The term is outside what the rule allows.",
        ["Company too young for the rule"] = "The company was established too recently for the rule, or its year isn't set.",
        ["No applicant within the age limit at the end of the term"] = "No applicant is within the rule's age limit at the end of the term, or a date of birth is missing.",
        ["Debt : Turnover over the rule's limit"] = "The company's debt compared with its turnover is over the rule's limit.",
        ["Creditsafe check fail"] = "The company's Creditsafe score is below the rule's minimum.",
        ["TransUnion credit check fail"] = "An applicant's TransUnion credit score is below the rule's minimum.",
        ["TransUnion Call Validate fail"] = "An applicant's TransUnion Call Validate score is below the rule's minimum.",
        ["Searches not run"] = "A search the rule needs wasn't run, or the company's accounts weren't reviewed.",
        ["Every check passed (decided by hand)"] = "Everything on the rule passed, searches included, yet the application wasn't auto approved: a person made the decision instead (e.g. underwriting conditions still open, or auto approval not run).",
    };

    public List<AutoApprovalPerformanceRow> Performance { get; set; } = new();
}
