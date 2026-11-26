using System.Globalization;
using System.Net;
using System.Text;
using Kpi.Web.Reports;
using Microsoft.AspNetCore.Components;

namespace Kpi.Web.Components.Pages.Kpis.AutoApproval;

/// <summary>
/// The Auto Approval page's two drawings as SVG (the agreed mock-up, docs/auto-approval-mockup.html): the funnel drawn to
/// scale, split Medical | Paper inside each band when both are shown, and the donut of what a person decided instead.
/// Hover = the SVG's own titles (no script).
/// </summary>
public static class AutoApprovalCharts
{
    public static readonly string[] Stages = ["Total applications", "Eligible for auto approval", "Auto approved", "Paid out"];

    // PF palette (Steve): Medical cyan, Paper orange (26 Nov 2026 - dark grey clashed with black text); approved by hand grey, declined red
    public static string DivisionColour(int divisionId) => divisionId == 0 ? "var(--kpim-aa-medical, #6bc2b9)" : "var(--kpim-aa-paper, #ee7624)";   // Medical cyan, Paper orange: black text reads on both
    public const string ApprovedColour = "var(--kpim-aa-approved, #898b8e)", DeclinedColour = "var(--kpim-aa-declined, #c20430)";
    // every colour here is a Settings > Colours token ("Auto Approval"), defaulting to the PF palette colour shown

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string E(string s) => WebUtility.HtmlEncode(s);
    /// <summary>A donut's figure: a count, or (Show: Amount financed) an amount.</summary>
    private static string Num(double v, bool byAmount) => byAmount ? Money((decimal)v) : F(v);

    /// <summary>Which way a donut slice pops out when hovered or tapped: along the middle of the slice.</summary>
    private static string Pop(double angle) => $"--dx:{F(6 * Math.Cos(angle))}px;--dy:{F(6 * Math.Sin(angle))}px";

    /// <summary>A chart part's words for js/chartTips.js (a new line = "\n").</summary>
    private static string Tip(string s) => WebUtility.HtmlEncode(s).Replace("\n", "&#10;");

    public static string Pct(double part, double whole) => whole > 0 ? (100 * part / whole).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "–";

    public static string Money(decimal v) =>
        Math.Abs(v) >= 1_000_000m ? "£" + (v / 1_000_000m).ToString("0.0", CultureInfo.InvariantCulture) + "m"
        : Math.Abs(v) >= 1_000m ? "£" + (v / 1_000m).ToString("0", CultureInfo.InvariantCulture) + "k"
        : "£" + v.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>The funnel: each band starts at its own figure's width and narrows to the next one's (the last stays level).</summary>
    public static MarkupString Funnel(IReadOnlyList<AutoApprovalFunnelRow> rows, bool byAmount, bool compact)
    {
        double Val(AutoApprovalFunnelRow r, int i) => byAmount ? (double)r.Amounts[i] : r.Counts[i];
        string Fmt(double v) => byAmount ? Money((decimal)v) : ((int)Math.Round(v)).ToString("N0", CultureInfo.GetCultureInfo("en-GB"));

        double W = compact ? 340 : 820, x0 = compact ? 5 : 215, fw = compact ? 330 : 400, top = compact ? 0 : 36, cx = x0 + fw / 2;
        double H = compact ? 58 : 64, gap = 5;
        var totals = Enumerable.Range(0, 4).Select(i => rows.Sum(r => Val(r, i))).ToArray();
        // drawn to scale: only a sliver as a minimum (an empty stage is a thin grey line with its 0) - a bigger minimum made
        // small stages all the same width (Steve, 26 Nov 2026: "Amount financed" by 3 months)
        var widths = new[] { 0, 1, 2, 3, 3 }.Select(i => Math.Max(fw * 0.015, fw * totals[i] / (totals[0] > 0 ? totals[0] : 1))).ToArray();
        var id = compact ? "aap" : "aad";

        var s = new StringBuilder($"<svg viewBox=\"0 0 {F(W)} {F(top + 4 * (H + gap))}\" role=\"img\" aria-label=\"Application to paid out\">");
        if (!compact)
            s.Append($"<text x=\"{F(W - 115)}\" y=\"20\" text-anchor=\"end\" font-size=\"17\" fill=\"#6b6b6b\">% total</text>")
             .Append($"<text x=\"{F(W - 4)}\" y=\"20\" text-anchor=\"end\" font-size=\"17\" fill=\"#6b6b6b\">% step above</text>")
             .Append($"<line x1=\"{F(x0 + fw + 10)}\" x2=\"{F(W)}\" y1=\"{F(top - 6)}\" y2=\"{F(top - 6)}\" stroke=\"#d0d1d2\" stroke-width=\"1.5\"/>");

        for (var i = 0; i < 4; i++)
        {
            double y = top + i * (H + gap), wt = widths[i], wb = widths[i + 1], wm = (wt + wb) / 2, tot = totals[i];
            var trap = $"M{F(cx - wt / 2)},{F(y)} L{F(cx + wt / 2)},{F(y)} L{F(cx + wb / 2)},{F(y + H)} L{F(cx - wb / 2)},{F(y + H)} Z";
            s.Append($"<clipPath id=\"{id}{i}\"><path d=\"{trap}\"/></clipPath>");
            if (tot <= 0) s.Append($"<path d=\"{trap}\" fill=\"#e7e8e8\"/>");

            // split at the band's middle width; the outer segments run past the edges (clipped) to fill the slanted corners
            // (only divisions with something at this stage: a zero one would otherwise paint a corner)
            var x = cx - wm / 2;
            var shown = Enumerable.Range(0, rows.Count).Where(j => Val(rows[j], i) > 0).ToList();
            foreach (var j in shown)
            {
                var v = Val(rows[j], i);
                bool first = j == shown[0], last = j == shown[^1];
                double w = v / tot * wm, x1 = first ? x - 60 : x, w1 = w + (first ? 60 : 0) + (last ? 60 : 0);
                var tip = $"{Stages[i]}\n{rows[j].Division}: {Fmt(v)}" + (rows.Count > 1 ? $" ({Pct(v, tot)} of {Fmt(tot)})" : "");
                s.Append($"<path clip-path=\"url(#{id}{i})\" d=\"M{F(x1)},{F(y)} h{F(w1)} v{F(H)} h{F(-w1)} Z\" style=\"fill:{DivisionColour(rows[j].DivisionId)}\" data-tip=\"{Tip(tip)}\"/>");
                x += w;
            }

            if (compact)
            {
                // black text throughout, no outline (Steve, 26 Nov 2026); the name and figure inside the band, as they always
                // were - only a band too skinny for its FIGURE (amount financed: a few % of the top) puts them to its right
                bool fits = wm >= Fmt(tot).Length * 19 * 0.6 + 12;
                double tx = fits ? cx : cx + Math.Max(wt, wb) / 2 + 8;
                string anchor = fits ? "middle" : "start";
                s.Append($"<text x=\"{F(tx)}\" y=\"{F(y + 22)}\" text-anchor=\"{anchor}\" fill=\"#1a1a1a\" font-size=\"13\" style=\"pointer-events:none\">{Stages[i]}</text>")
                 .Append($"<text x=\"{F(tx)}\" y=\"{F(y + 42)}\" text-anchor=\"{anchor}\" fill=\"{(tot > 0 ? "#1a1a1a" : "#b0b0b0")}\" font-size=\"19\" font-weight=\"700\" style=\"pointer-events:none\">{E(Fmt(tot))}</text>");
            }
            else
            {
                s.Append($"<text x=\"{F(x0 - 14)}\" y=\"{F(y + H / 2 + 5)}\" text-anchor=\"end\" font-size=\"18\" fill=\"#1a1a1a\">{Stages[i]}</text>")
                 .Append($"<text x=\"{F(W - 115)}\" y=\"{F(y + H / 2 + 6)}\" text-anchor=\"end\" font-size=\"19\" font-weight=\"600\" fill=\"#1a1a1a\">{Pct(tot, totals[0])}</text>")
                 .Append($"<text x=\"{F(W - 4)}\" y=\"{F(y + H / 2 + 6)}\" text-anchor=\"end\" font-size=\"19\" font-weight=\"600\" fill=\"{(i > 0 ? "#1a1a1a" : "#b0b0b0")}\">{(i > 0 ? Pct(tot, totals[i - 1]) : "–")}</text>");
                // the number inside the band, or beside it (at its middle width) when the band is too narrow there
                s.Append(wm < Fmt(tot).Length * 23 * 0.6 + 14
                    ? $"<text x=\"{F(cx + wm / 2 + 10)}\" y=\"{F(y + H / 2 + 7)}\" font-size=\"23\" font-weight=\"700\" fill=\"#1a1a1a\" style=\"pointer-events:none\">{E(Fmt(tot))}</text>"
                    : $"<text x=\"{F(cx)}\" y=\"{F(y + H / 2 + 7)}\" text-anchor=\"middle\" fill=\"#fff\" font-size=\"23\" font-weight=\"700\" style=\"pointer-events:none;paint-order:stroke;stroke:rgba(0,0,0,.18);stroke-width:2px\">{E(Fmt(tot))}</text>");
            }
        }
        return new MarkupString(s.Append("</svg>").ToString());
    }

    /// <summary>
    /// The desktop funnel (the agreed mock-up, 26 Nov 2026): a grid - the stage names at the far left, the two % columns at
    /// the far right, and the funnel filling the room between, each band its own drawing stretched to the column's width.
    /// The number sits inside a band when it fits, beside it when it doesn't; an empty stage is a grey stub with a grey 0.
    /// </summary>
    public static MarkupString FunnelGrid(IReadOnlyList<AutoApprovalFunnelRow> rows, bool byAmount)
    {
        double Val(AutoApprovalFunnelRow r, int i) => byAmount ? (double)r.Amounts[i] : r.Counts[i];
        string Fmt(double v) => byAmount ? Money((decimal)v) : ((int)Math.Round(v)).ToString("N0", CultureInfo.GetCultureInfo("en-GB"));
        var totals = Enumerable.Range(0, 4).Select(i => rows.Sum(r => Val(r, i))).ToArray();
        var widths = new[] { 0, 1, 2, 3, 3 }.Select(i => Math.Max(1.5, 100 * totals[i] / (totals[0] > 0 ? totals[0] : 1))).ToArray();   // % of the column, to scale (a sliver at least)

        var s = new StringBuilder("<div class=\"aa-fun\" role=\"img\" aria-label=\"Application to paid out\">")
            .Append("<span></span><span></span><span class=\"aa-fh\">% total</span><span class=\"aa-fh\">% step above</span>");
        for (var i = 0; i < 4; i++)
        {
            double wt = widths[i], wb = widths[i + 1], wm = (wt + wb) / 2, tot = totals[i];
            string P(double v) => F(v);
            var trap = $"{P(50 - wt / 2)},0 {P(50 + wt / 2)},0 {P(50 + wb / 2)},10 {P(50 - wb / 2)},10";
            var band = new StringBuilder($"<svg viewBox=\"0 0 100 10\" preserveAspectRatio=\"none\"><clipPath id=\"aag{i}\"><polygon points=\"{trap}\"/></clipPath>");
            if (tot <= 0) band.Append($"<polygon points=\"{trap}\" fill=\"#e7e8e8\"/>");
            var x = 50 - wm / 2;
            var shown = Enumerable.Range(0, rows.Count).Where(j => Val(rows[j], i) > 0).ToList();
            foreach (var j in shown)
            {
                var v = Val(rows[j], i);
                bool first = j == shown[0], last = j == shown[^1];
                double w = v / tot * wm, x1 = first ? x - 30 : x, w1 = w + (first ? 30 : 0) + (last ? 30 : 0);
                var tip = $"{Stages[i]}\n{rows[j].Division}: {Fmt(v)}" + (rows.Count > 1 ? $" ({Pct(v, tot)} of {Fmt(tot)})" : "");
                band.Append($"<rect clip-path=\"url(#aag{i})\" x=\"{F(x1)}\" y=\"0\" width=\"{F(w1)}\" height=\"10\" style=\"fill:{DivisionColour(rows[j].DivisionId)}\" data-tip=\"{Tip(tip)}\"/>");
                x += w;
            }
            band.Append("</svg>");
            // inside when the number fits at the band's middle width (aa-fun's column is about 230px or more; 23px figures)
            var inside = tot > 0 && wm * 2.3 >= Fmt(tot).Length * 13 + 14;
            var num = inside
                ? $"<b class=\"aa-fn\">{E(Fmt(tot))}</b>"
                : $"<b class=\"aa-fn aa-fn-out{(tot > 0 ? "" : " aa-fn-zero")}\" style=\"left:calc({F(50 + wm / 2)}% + 8px)\">{E(Fmt(tot))}</b>";
            string zc = tot > 0 ? "" : " aa-fn-zero";
            s.Append($"<span class=\"aa-fl\">{Stages[i]}</span><span class=\"aa-fb\">{band}{num}</span>")
             .Append($"<span class=\"aa-fp{zc}\">{Pct(tot, totals[0])}</span>")
             .Append($"<span class=\"aa-fp{(i > 0 ? zc : " aa-fn-zero")}\">{(i > 0 ? Pct(tot, totals[i - 1]) : "–")}</span>");
        }
        return new MarkupString(s.Append("</div>").ToString());
    }

    // PF palette, in order, for the reason donut's slices
    private static readonly string[] PieColours = ["var(--kpim-aa-r1, #c20430)", "var(--kpim-aa-r2, #ee7624)", "var(--kpim-aa-r3, #6bc2b9)", "var(--kpim-aa-r4, #54585a)", "var(--kpim-aa-r5, #898b8e)"];
    public static string PieColour(int i) => PieColours[i % PieColours.Length];

    /// <summary>
    /// The eligible reasons as a donut, like the outcome's (Steve, 26 Nov 2026): the count on each slice, its % outside on a
    /// short leader line, the total in the middle (reasons, not applications: one can have two); the key carries names only.
    /// </summary>
    public static MarkupString ReasonDonut(IReadOnlyList<AutoApprovalReasonRow> rows, bool byAmount, Func<AutoApprovalReasonRow, string>? tipFor = null)
    {
        double Val(AutoApprovalReasonRow x) => byAmount ? (double)x.AF : x.Applications;
        double tot = rows.Sum(Val), R = 60, r = 36, cx = 120, cy = 76, a0 = -Math.PI / 2;
        string P(double rad, double ang) => $"{F(cx + rad * Math.Cos(ang))},{F(cy + rad * Math.Sin(ang))}";
        var segs = new StringBuilder(); var labs = new StringBuilder();
        if (tot == 0) segs.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F((R + r) / 2)}\" fill=\"none\" stroke=\"#e7e8e8\" stroke-width=\"{F(R - r)}\"/>");
        for (var i = 0; i < rows.Count && tot > 0; i++)
        {
            var v = Val(rows[i]);
            if (v == 0) continue;
            double a1 = a0 + 2 * Math.PI * v / tot, am = (a0 + a1) / 2, m = (R + r) / 2;
            var big = a1 - a0 > Math.PI ? 1 : 0;
            segs.Append(v == tot
                ? $"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(m)}\" fill=\"none\" style=\"stroke:{PieColour(i)}\" stroke-width=\"{F(R - r)}\" data-tip=\"{Tip(tipFor?.Invoke(rows[i]) ?? $"{rows[i].Reason}\n{Num(v, byAmount)} ({Pct(v, tot)})")}\"/>"
                : $"<g class=\"aa-slice\" style=\"{Pop(am)}\" data-tip=\"{Tip(tipFor?.Invoke(rows[i]) ?? $"{rows[i].Reason}\n{Num(v, byAmount)} ({Pct(v, tot)})")}\"><path d=\"M{P(R, a0)} A{F(R)},{F(R)} 0 {big} 1 {P(R, a1)} L{P(r, a1)} A{F(r)},{F(r)} 0 {big} 0 {P(r, a0)} Z\" style=\"fill:{PieColour(i)}\" stroke=\"#fff\" stroke-width=\"1\"/>");
            var number = $"<text x=\"{F(cx + m * Math.Cos(am))}\" y=\"{F(cy + m * Math.Sin(am) + 4)}\" text-anchor=\"middle\" font-size=\"12\" font-weight=\"700\" fill=\"#fff\" style=\"pointer-events:none\">{Num(v, byAmount)}</text>";
            if (byAmount) number = "";   // the amount goes outside, above the %
            if (v == tot) labs.Append(number); else segs.Append(number).Append("</g>");
            bool right = Math.Cos(am) >= 0; double ex = cx + (R + 10) * Math.Cos(am), ey = cy + (R + 10) * Math.Sin(am), hx = ex + (right ? 10 : -10);
            labs.Append($"<polyline points=\"{P(R + 2, am)} {F(ex)},{F(ey)} {F(hx)},{F(ey)}\" fill=\"none\" stroke=\"#898b8e\" stroke-width=\"1\"/>")
                .Append(byAmount
                    ? $"<text x=\"{F(hx + (right ? 3 : -3))}\" y=\"{F(ey - 1)}\" text-anchor=\"{(right ? "start" : "end")}\" font-size=\"11\" font-weight=\"700\" fill=\"#1a1a1a\">{Num(v, true)}</text>" +
                      $"<text x=\"{F(hx + (right ? 3 : -3))}\" y=\"{F(ey + 10)}\" text-anchor=\"{(right ? "start" : "end")}\" font-size=\"10\" font-weight=\"600\" fill=\"#6b6b6b\">{Pct(v, tot)}</text>"
                    : $"<text x=\"{F(hx + (right ? 3 : -3))}\" y=\"{F(ey + 4)}\" text-anchor=\"{(right ? "start" : "end")}\" font-size=\"11\" font-weight=\"600\" fill=\"#1a1a1a\">{Pct(v, tot)}</text>");
            a0 = a1;
        }
        return new MarkupString($"<svg viewBox=\"14 -4 212 160\" role=\"img\" aria-label=\"Eligible: not auto approved reason\">{segs}{labs}" +
                                $"<text x=\"{F(cx)}\" y=\"{F(cy + 8)}\" text-anchor=\"middle\" font-size=\"{(byAmount ? 17 : 24)}\" font-weight=\"700\" fill=\"#1a1a1a\">{Num(tot, byAmount)}</text></svg>");
    }

    /// <summary>The donut: the number on each slice, its % outside on a short leader line, the total in the middle.</summary>
    public static MarkupString Donut(AutoApprovalOutcome o, bool byAmount)
    {
        double approved = byAmount ? (double)o.ApprovedAF : o.ApprovedByHand, declined = byAmount ? (double)o.DeclinedAF : o.DeclinedByHand;
        var vals = new[] { (approved, ApprovedColour, "Approved by hand"), (declined, DeclinedColour, "Declined by hand") };
        double tot = approved + declined, R = 60, r = 36, cx = 120, cy = 76,
               // turned so Declined sits centred on the left: its % label goes out to the side, not across the top
               a0 = tot > 0 ? Math.PI - 2 * Math.PI * approved / tot - Math.PI * declined / tot : -Math.PI / 2;
        string P(double rad, double ang) => $"{F(cx + rad * Math.Cos(ang))},{F(cy + rad * Math.Sin(ang))}";
        var segs = new StringBuilder(); var labs = new StringBuilder();
        if (tot == 0) segs.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F((R + r) / 2)}\" fill=\"none\" stroke=\"#e7e8e8\" stroke-width=\"{F(R - r)}\"/>");
        foreach (var (v, colour, label) in vals)
        {
            if (v == 0 || tot == 0) continue;
            double a1 = a0 + 2 * Math.PI * v / tot, am = (a0 + a1) / 2, m = (R + r) / 2;
            var big = a1 - a0 > Math.PI ? 1 : 0;
            segs.Append(v == tot
                ? $"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(m)}\" fill=\"none\" style=\"stroke:{colour}\" stroke-width=\"{F(R - r)}\" data-tip=\"{Tip($"{label}\n{Num(v, byAmount)} ({Pct(v, tot)})")}\"/>"
                : $"<g class=\"aa-slice\" style=\"{Pop(am)}\" data-tip=\"{Tip($"{label}\n{Num(v, byAmount)} ({Pct(v, tot)})")}\"><path d=\"M{P(R, a0)} A{F(R)},{F(R)} 0 {big} 1 {P(R, a1)} L{P(r, a1)} A{F(r)},{F(r)} 0 {big} 0 {P(r, a0)} Z\" style=\"fill:{colour}\"/>");
            var number = $"<text x=\"{F(cx + m * Math.Cos(am))}\" y=\"{F(cy + m * Math.Sin(am) + 4)}\" text-anchor=\"middle\" font-size=\"12\" font-weight=\"700\" fill=\"#fff\" style=\"pointer-events:none\">{Num(v, byAmount)}</text>";
            if (byAmount) number = "";   // the amount goes outside, above the %
            if (v == tot) labs.Append(number); else segs.Append(number).Append("</g>");
            bool right = Math.Cos(am) >= 0; double ex = cx + (R + 12) * Math.Cos(am), ey = cy + (R + 12) * Math.Sin(am), hx = ex + (right ? 12 : -12);
            labs.Append($"<polyline points=\"{P(R + 2, am)} {F(ex)},{F(ey)} {F(hx)},{F(ey)}\" fill=\"none\" stroke=\"#898b8e\" stroke-width=\"1\"/>")
                .Append(byAmount
                    ? $"<text x=\"{F(hx + (right ? 3 : -3))}\" y=\"{F(ey - 1)}\" text-anchor=\"{(right ? "start" : "end")}\" font-size=\"11\" font-weight=\"700\" fill=\"#1a1a1a\">{Num(v, true)}</text>" +
                      $"<text x=\"{F(hx + (right ? 3 : -3))}\" y=\"{F(ey + 10)}\" text-anchor=\"{(right ? "start" : "end")}\" font-size=\"10\" font-weight=\"600\" fill=\"#6b6b6b\">{Pct(v, tot)}</text>"
                    : $"<text x=\"{F(hx + (right ? 3 : -3))}\" y=\"{F(ey + 4)}\" text-anchor=\"{(right ? "start" : "end")}\" font-size=\"11\" font-weight=\"600\" fill=\"#1a1a1a\">{Pct(v, tot)}</text>");
            a0 = a1;
        }
        return new MarkupString($"<svg viewBox=\"0 0 240 152\" role=\"img\" aria-label=\"Eligible: not auto approved outcome\">{segs}{labs}" +
                                $"<text x=\"{F(cx)}\" y=\"{F(cy + 8)}\" text-anchor=\"middle\" font-size=\"{(byAmount ? 17 : 24)}\" font-weight=\"700\" fill=\"#1a1a1a\">{Num(tot, byAmount)}</text></svg>");
    }
}
