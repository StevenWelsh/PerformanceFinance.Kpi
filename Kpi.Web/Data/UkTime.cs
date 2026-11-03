namespace Kpi.Web.Data;

/// <summary>Times are stored in UTC and shown in UK time, clocks going back and forward included.</summary>
public static class UkTime
{
    private static readonly TimeZoneInfo Zone = Find();

    private static TimeZoneInfo Find()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); }
    }

    public static DateTime FromUtc(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    /// <summary>A UK wall-clock time as UTC (a time in the clocks-forward gap is moved on an hour).</summary>
    public static DateTime ToUtc(DateTime uk)
    {
        uk = DateTime.SpecifyKind(uk, DateTimeKind.Unspecified);
        if (Zone.IsInvalidTime(uk)) uk = uk.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(uk, Zone);
    }

    /// <summary>The time alone when it is today (UK), else with its date: "14:20:11", "19 Oct 23:41".</summary>
    public static string ShowShort(DateTime? utc) =>
        utc is null ? "-" : FromUtc(utc.Value).Date == FromUtc(DateTime.UtcNow).Date ? Show(utc, "HH:mm:ss") : Show(utc, "dd MMM HH:mm");

    public static string Show(DateTime? utc, string format = "dd MMM yyyy HH:mm") =>
        utc is null ? "-" : FromUtc(utc.Value).ToString(format);
}
