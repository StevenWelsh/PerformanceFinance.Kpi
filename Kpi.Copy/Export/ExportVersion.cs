namespace Kpi.Copy;

/// <summary>
/// The KPI_Export version this code was built for (KpiExport.Version, near the end of Export/KPI_Export.sql).
/// The copy (the site, or Kpi.Push) refuses any other version, so an out-of-date script on the server can never quietly feed the
/// wrong shape; Kpi.Tools export-apply refuses a script whose number disagrees, and export-check reports a mismatch.
/// Raise it together with the script's INSERT. Used by Kpi.Web, Kpi.Push and Kpi.Tools.
/// </summary>
public static class ExportVersion
{
    public const int Current = 2;
}
