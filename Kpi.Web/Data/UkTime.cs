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

    public static string Show(DateTime? utc, string format = "dd MMM yyyy HH:mm") =>
        utc is null ? "-" : FromUtc(utc.Value).ToString(format);
}
