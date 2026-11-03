using Kpi.Data.Cfg;
using Kpi.Web.Data;

namespace Kpi.Web.Scheduling;

/// <summary>
/// WHEN A TASK RUNS (SHS-Web's TaskSchedule, copied 3 Nov 2026): which days of the week, a start time, and optionally repeat
/// every so often until a finishing time. All times are UK time.
/// </summary>
public sealed record TaskSchedule(int DaysMask, TimeOnly Start, int? RepeatEveryMinutes, TimeOnly? RepeatUntil)
{
    public const int AllDays = 127;       // Mon 1 · Tue 2 · Wed 4 · Thu 8 · Fri 16 · Sat 32 · Sun 64
    public const int WorkingDays = 31;    // Mon–Fri

    public static readonly DayOfWeek[] WeekOrder =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    public static int Bit(DayOfWeek d) => d == DayOfWeek.Sunday ? 64 : 1 << ((int)d - 1);

    public bool RunsOn(DayOfWeek d) => (DaysMask & Bit(d)) != 0;

    public static TaskSchedule DailyAt(int hour, int minute = 0, int days = AllDays) =>
        new(days, new TimeOnly(hour, minute), null, null);

    public static TaskSchedule Repeating(int everyMinutes, int startHour = 0, int startMinute = 0, int days = AllDays) =>
        new(days, new TimeOnly(startHour, startMinute), everyMinutes, null);

    public static TaskSchedule Of(ScheduledTask t) => new(t.DaysMask, t.StartTime, t.RepeatEveryMinutes, t.RepeatUntil);

    public void ApplyTo(ScheduledTask t)
    {
        t.DaysMask = DaysMask; t.StartTime = Start; t.RepeatEveryMinutes = RepeatEveryMinutes; t.RepeatUntil = RepeatUntil;
    }

    /// <summary>The UK times it runs on one day, in order.</summary>
    public IEnumerable<DateTime> SlotsOn(DateOnly day)
    {
        if (!RunsOn(day.DayOfWeek)) yield break;
        var first = day.ToDateTime(Start);
        yield return first;
        if (RepeatEveryMinutes is not { } every || every <= 0) yield break;
        var last = RepeatUntil is { } until ? day.ToDateTime(until) : day.AddDays(1).ToDateTime(TimeOnly.MinValue).AddTicks(-1);
        for (var t = first.AddMinutes(every); t <= last; t = t.AddMinutes(every))
            yield return t;
    }

    /// <summary>The first run strictly after the given moment, in UTC; null if it never runs (no days chosen).</summary>
    public DateTime? NextUtcAfter(DateTime afterUtc)
    {
        var fromDay = DateOnly.FromDateTime(UkTime.FromUtc(afterUtc));
        for (var d = -1; d <= 8; d++)   // from yesterday: a repeat can run past midnight UTC
            foreach (var slot in SlotsOn(fromDay.AddDays(d)))
            {
                var utc = UkTime.ToUtc(slot);
                if (utc > afterUtc) return utc;
            }
        return null;
    }

    public IEnumerable<DateTime> NextRunsUtc(DateTime afterUtc, int count)
    {
        var at = afterUtc;
        for (var i = 0; i < count; i++)
        {
            if (NextUtcAfter(at) is not { } next) yield break;
            yield return next;
            at = next;
        }
    }

    /// <summary>"Every day at 02:30", "Every day, every 2 minutes", "Mon–Fri, every 2 hours from 06:00 until 20:00".</summary>
    public string Describe()
    {
        var days = DaysText(DaysMask);
        if (RepeatEveryMinutes is not { } every)
            return $"{days} at {Start:HH\\:mm}";
        if (Start == TimeOnly.MinValue && RepeatUntil is null)
            return $"{days}, every {EveryText(every)}";
        var until = RepeatUntil is { } u ? $" until {u:HH\\:mm}" : "";
        return $"{days}, every {EveryText(every)} from {Start:HH\\:mm}{until}";
    }

    public static string DaysText(int mask) => mask switch
    {
        AllDays => "Every day",
        WorkingDays => "Mon–Fri",
        63 => "Mon–Sat",
        96 => "Sat and Sun",
        0 => "No days",
        _ => string.Join(", ", WeekOrder.Where(d => (mask & Bit(d)) != 0).Select(d => d.ToString()[..3])),
    };

    public static string EveryText(int minutes) => minutes switch
    {
        < 60 => $"{minutes} minutes",
        60 => "hour",
        _ when minutes % 60 == 0 => $"{minutes / 60} hours",
        _ => $"{minutes / 60} h {minutes % 60} min",
    };

    /// <summary>Why the schedule can't be saved, or null.</summary>
    public string? Problem(TimeSpan minimumRepeat)
    {
        if ((DaysMask & AllDays) == 0) return "Choose at least one day.";
        if (RepeatEveryMinutes is { } every)
        {
            if (every < minimumRepeat.TotalMinutes) return $"This task can't repeat more often than every {EveryText((int)minimumRepeat.TotalMinutes)}.";
            if (every > 720) return "Repeat at most every 12 hours - for less often, run it once a day.";
            if (RepeatUntil is { } until && until <= Start) return "The finishing time must be after the start time.";
        }
        return null;
    }
}
