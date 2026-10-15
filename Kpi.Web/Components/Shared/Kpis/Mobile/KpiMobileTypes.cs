namespace Kpi.Web.Components.Shared.Kpis.Mobile;

/// <summary>The landscape hint a phone report shows first in its portrait view.</summary>
public enum KpiMobileHint
{
    None,
    /// <summary>A summary in portrait, the full detail in landscape.</summary>
    SummaryInPortrait,
    /// <summary>The report is meant to be read in landscape; portrait carries only a summary.</summary>
    LandscapeOnly,
    /// <summary>Landscape shows a chart and a table.</summary>
    ChartAndTable
}
