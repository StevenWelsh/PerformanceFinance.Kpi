namespace Kpi.Push;

/// <summary>
/// The KPI_Export version this code was built for (KpiExport.Version, the last statement of Export/KPI_Export.sql).
/// Kpi.Push refuses to copy from any other version, so an out-of-date script on the server can never quietly feed the
/// wrong shape; Kpi.Tools export-apply refuses a script whose number disagrees, and export-check reports a mismatch.
/// Raise it together with the script's INSERT. Shared with Kpi.Tools (linked into its project).
/// </summary>
public static class ExportVersion
{
    public const int Current = 1;
}
