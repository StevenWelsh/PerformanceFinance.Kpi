namespace Kpi.Copy;

/// <summary>
/// The versions this code was built for. The copy (the site, or Kpi.Push) refuses a KPI_Export of any other
/// <see cref="Current"/>, and KPI_Export's refresh refuses a CMS side of any other <see cref="Cms"/>, so an out-of-date
/// half can never quietly feed the wrong shape; Kpi.Tools export-apply refuses a script that disagrees with these, and
/// export-check reports a mismatch. Used by Kpi.Web, Kpi.Push and Kpi.Tools.
/// </summary>
public static class ExportVersion
{
    /// <summary>KPI_Export's own script (KpiCopy.Version, near the end of Export/KPI_Export.sql). Raise them together.</summary>
    public const int Current = 3;

    /// <summary>
    /// The CMS database's KpiExport schema - the views and snapshots, defined in the CMS repo (migration mgKpiExport,
    /// CMS.Models/Scripts/KpiExport). Raised there in a migration when a view's columns change; raise this and the
    /// script's @CmsVersion with it.
    /// </summary>
    public const int Cms = 1;
}
