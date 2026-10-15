namespace Kpi.Web.Components.Shared.Kpis;

/// <summary>One choice in a KPI filter (KpiField) or view switch (KpiSegmented), full view or phone.</summary>
public sealed record KpiOption(string Value, string Text, string? Icon = null);
