using Microsoft.Extensions.Caching.Memory;

namespace Kpi.Web.Data.Kpis;

/// <summary>
/// When the figures were last copied from CMS, and whether that copy is overdue (Rpt.spDataStatus: over 30 minutes in
/// office hours). The "As at" line shows the copy time beside the date, and turns into a warning when the copy has
/// stopped. Company-wide and cheap, so one shared answer, a minute old at most.
/// </summary>
public sealed class KpiDataStatus(KpiSessionFactory sessions, IMemoryCache cache)
{
    private const string Key = "kpi:data-status";

    public sealed record Status(DateTime? LastCopyUtc, bool Stale);

    public Status Get() =>
        cache.GetOrCreate(Key, e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            using var db = sessions.Create();
            var row = db.Report<Row>("Rpt.spDataStatus").SingleOrDefault();
            return new Status(row?.LastFrequentLoadUtc, row?.FiguresStale ?? false);
        })!;

    /// <summary>
    /// "15 Oct 2026 21:12": the business date, with the time of the last copy when it is that same day (a past year's
    /// figures run to its 31 December, and a copy time beside that date would mean nothing).
    /// </summary>
    public static string AsAtText(DateTime asAt, Status status)
    {
        var text = asAt.ToString("dd MMM yyyy");
        if (status.LastCopyUtc is DateTime utc && UkTime.FromUtc(utc) is var uk && uk.Date == asAt.Date)
            text += uk.ToString(" HH:mm");
        return text;
    }

    /// <summary>The hover text when the copy has stopped.</summary>
    public static string? StaleNote(Status status) => !status.Stale ? null
        : $"Not copied from CMS since {UkTime.Show(status.LastCopyUtc, "HH:mm on dd MMM")} - the copy from the office may have stopped.";

    private sealed class Row
    {
        public DateTime? LastFrequentLoadUtc { get; set; }
        public bool FiguresStale { get; set; }
    }
}
