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

    // Only a line on the page: if the database can't be reached it shows the date alone rather than breaking the
    // page, and the next call (nothing cached) tries again.
    public Status Get()
    {
        if (cache.TryGetValue(Key, out Status? hit) && hit is not null) return hit;
        try
        {
            using var db = sessions.Create();
            var row = db.Report<Row>("Rpt.spDataStatus").SingleOrDefault();
            return cache.Set(Key, new Status(row?.LastFrequentLoadUtc, row?.FiguresStale ?? false), TimeSpan.FromMinutes(1));
        }
        catch (Exception) { return new Status(null, false); }
    }

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
