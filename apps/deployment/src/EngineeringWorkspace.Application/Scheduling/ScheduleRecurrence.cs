using System.Globalization;

namespace EngineeringWorkspace.Application.Scheduling;

public enum ScheduleFrequency
{
    Hourly,
    Daily,
    Weekly,
    Monthly
}

public enum MonthlyMode
{
    /// <summary>A day of the month (1–31) or the last day.</summary>
    DayOfMonth,

    /// <summary>The 1st–4th or last given weekday of the month (for example the 2nd Tuesday).</summary>
    NthWeekday
}

/// <summary>Bit masks for days of the week (bit = 1 &lt;&lt; (int)DayOfWeek) and months (bit = 1 &lt;&lt; (month - 1)).</summary>
public static class ScheduleMasks
{
    public const int AllDays = 0b111_1111;
    public const int Weekdays = 0b011_1110;
    public const int AllMonths = 0b1111_1111_1111;

    /// <summary>Day-of-month value meaning "the last day of the month".</summary>
    public const int LastDay = 32;

    /// <summary>Week-of-month value meaning "the last such weekday of the month".</summary>
    public const int LastWeek = 5;

    public static int Day(DayOfWeek day) => 1 << (int)day;
    public static int Month(int month) => 1 << (month - 1);
    public static bool HasDay(int mask, DayOfWeek day) => (mask & Day(day)) != 0;
    public static bool HasMonth(int mask, int month) => (mask & Month(month)) != 0;

    public static int Days(IEnumerable<DayOfWeek> days) => days.Aggregate(0, (mask, day) => mask | Day(day));
    public static int Months(IEnumerable<int> months) => months.Aggregate(0, (mask, month) => mask | Month(month));

    /// <summary>Monday first, as people read a week.</summary>
    public static IReadOnlyList<DayOfWeek> WeekOrder { get; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    public static string DescribeDays(int mask)
    {
        mask &= AllDays;
        if (mask == AllDays || mask == 0) return "Every day";
        if (mask == Weekdays) return "Mon–Fri";
        if (mask == (Day(DayOfWeek.Saturday) | Day(DayOfWeek.Sunday))) return "Sat–Sun";
        return string.Join(", ", WeekOrder.Where(day => HasDay(mask, day)).Select(day => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(day)));
    }

    public static string DescribeMonths(int mask)
    {
        mask &= AllMonths;
        if (mask == AllMonths || mask == 0) return "Every month";
        return string.Join(", ", Enumerable.Range(1, 12).Where(month => HasMonth(mask, month)).Select(month => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(month)));
    }

    public static string Ordinal(int week) => week switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        4 => "4th",
        _ => "Last"
    };
}

/// <summary>
/// When a recurring assessment runs. Interpretation by frequency:
/// <list type="bullet">
/// <item>Hourly: every <see cref="IntervalHours"/> hours from <see cref="WindowStart"/> (default 00:00) up to
/// <see cref="WindowEnd"/> (default 23:59), on <see cref="DaysOfWeek"/> (0 = every day).</item>
/// <item>Daily: at <see cref="RunTime"/> on <see cref="DaysOfWeek"/>.</item>
/// <item>Weekly: at <see cref="RunTime"/> on <see cref="DaysOfWeek"/>, every <see cref="IntervalWeeks"/> weeks counted from the
/// week of <see cref="AnchorDate"/>.</item>
/// <item>Monthly: at <see cref="RunTime"/> in <see cref="Months"/>, on <see cref="DayOfMonth"/> (1–31 or
/// <see cref="ScheduleMasks.LastDay"/>; months without that day are skipped) or on the <see cref="WeekOfMonth"/>
/// <see cref="Weekday"/>.</item>
/// </list>
/// Times are wall-clock times in the schedule's time zone.
/// </summary>
public sealed record ScheduleRecurrence(
    ScheduleFrequency Frequency,
    TimeOnly RunTime,
    int DaysOfWeek = ScheduleMasks.AllDays,
    int IntervalHours = 1,
    TimeOnly? WindowStart = null,
    TimeOnly? WindowEnd = null,
    int IntervalWeeks = 1,
    DateOnly? AnchorDate = null,
    int Months = ScheduleMasks.AllMonths,
    MonthlyMode MonthlyMode = MonthlyMode.DayOfMonth,
    int DayOfMonth = 1,
    int WeekOfMonth = 1,
    DayOfWeek Weekday = DayOfWeek.Monday)
{
    public string Describe()
    {
        var time = RunTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        return Frequency switch
        {
            ScheduleFrequency.Hourly =>
                $"Every {(IntervalHours == 1 ? "hour" : $"{IntervalHours} hours")}" +
                (WindowStart is not null || WindowEnd is not null
                    ? $", {(WindowStart ?? TimeOnly.MinValue).ToString("HH:mm", CultureInfo.InvariantCulture)}–{(WindowEnd ?? new TimeOnly(23, 59)).ToString("HH:mm", CultureInfo.InvariantCulture)}"
                    : string.Empty) +
                $", {ScheduleMasks.DescribeDays(DaysOfWeek)}",
            ScheduleFrequency.Daily => $"{ScheduleMasks.DescribeDays(DaysOfWeek)} at {time}",
            ScheduleFrequency.Weekly => $"{(IntervalWeeks <= 1 ? "Weekly" : $"Every {IntervalWeeks} weeks")} on {ScheduleMasks.DescribeDays(DaysOfWeek)} at {time}",
            _ => (MonthlyMode == MonthlyMode.DayOfMonth
                     ? DayOfMonth == ScheduleMasks.LastDay ? "Last day" : $"Day {DayOfMonth}"
                     : $"{ScheduleMasks.Ordinal(WeekOfMonth)} {Weekday}") + $" of {ScheduleMasks.DescribeMonths(Months)} at {time}"
        };
    }

    /// <summary>Days column of the schedules list.</summary>
    public string DaysText => Frequency switch
    {
        ScheduleFrequency.Monthly => MonthlyMode == MonthlyMode.DayOfMonth
            ? DayOfMonth == ScheduleMasks.LastDay ? "Last day" : $"Day {DayOfMonth}"
            : $"{ScheduleMasks.Ordinal(WeekOfMonth)} {CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(Weekday)}",
        _ => ScheduleMasks.DescribeDays(DaysOfWeek)
    };

    /// <summary>Month column of the schedules list.</summary>
    public string MonthsText => Frequency == ScheduleFrequency.Monthly ? ScheduleMasks.DescribeMonths(Months) : "Every month";
}

/// <summary>A validation message for one field of the schedule editor.</summary>
public sealed record ScheduleValidationMessage(string Field, string Message, bool IsWarning = false);

/// <summary>
/// Pure next-run calculation and validation of <see cref="ScheduleRecurrence"/>. No clock, no I/O: callers pass
/// the instant to search after and the time zone.
/// </summary>
public static class NextRunCalculator
{
    /// <summary>How far ahead to search (covers 29 February and every-N-weeks schedules).</summary>
    private const int SearchDays = 366 * 5;

    /// <summary>The first occurrence strictly after <paramref name="afterUtc"/>, in UTC; null when the recurrence never occurs.</summary>
    public static DateTimeOffset? Next(ScheduleRecurrence recurrence, DateTimeOffset afterUtc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(recurrence);
        ArgumentNullException.ThrowIfNull(zone);
        if (Validate(recurrence).Any(message => !message.IsWarning)) return null;
        var afterLocal = TimeZoneInfo.ConvertTime(afterUtc, zone).DateTime;
        var start = DateOnly.FromDateTime(afterLocal);
        for (var offset = 0; offset < SearchDays; offset++)
        {
            var day = start.AddDays(offset);
            if (!Occurs(recurrence, day)) continue;
            foreach (var time in TimesOn(recurrence))
            {
                var utc = ToUtc(day.ToDateTime(time), zone);
                if (utc > afterUtc) return utc;
            }
        }

        return null;
    }

    /// <summary>The next <paramref name="count"/> occurrences (for the editor's preview).</summary>
    public static IReadOnlyList<DateTimeOffset> NextOccurrences(ScheduleRecurrence recurrence, DateTimeOffset afterUtc, TimeZoneInfo zone, int count)
    {
        var results = new List<DateTimeOffset>(count);
        var cursor = afterUtc;
        while (results.Count < count && Next(recurrence, cursor, zone) is { } next)
        {
            results.Add(next);
            cursor = next;
        }

        return results;
    }

    public static IReadOnlyList<ScheduleValidationMessage> Validate(ScheduleRecurrence recurrence)
    {
        var messages = new List<ScheduleValidationMessage>();
        switch (recurrence.Frequency)
        {
            case ScheduleFrequency.Hourly:
                if (recurrence.IntervalHours is < 1 or > 23) messages.Add(new("IntervalHours", "Every N hours must be between 1 and 23."));
                if (recurrence.WindowStart is { } from && recurrence.WindowEnd is { } to && to <= from)
                    messages.Add(new("Window", "The active window must end after it starts."));
                break;
            case ScheduleFrequency.Daily:
                if ((recurrence.DaysOfWeek & ScheduleMasks.AllDays) == 0) messages.Add(new("DaysOfWeek", "Select at least one day."));
                break;
            case ScheduleFrequency.Weekly:
                if ((recurrence.DaysOfWeek & ScheduleMasks.AllDays) == 0) messages.Add(new("DaysOfWeek", "Select at least one day."));
                if (recurrence.IntervalWeeks is < 1 or > 52) messages.Add(new("IntervalWeeks", "Every N weeks must be between 1 and 52."));
                break;
            case ScheduleFrequency.Monthly:
                if ((recurrence.Months & ScheduleMasks.AllMonths) == 0) messages.Add(new("Months", "Select at least one month."));
                if (recurrence.MonthlyMode == MonthlyMode.DayOfMonth)
                {
                    if (recurrence.DayOfMonth is < 1 or > 31 && recurrence.DayOfMonth != ScheduleMasks.LastDay)
                        messages.Add(new("DayOfMonth", "Choose a day from 1 to 31, or Last day."));
                    else if (recurrence.DayOfMonth is >= 29 and <= 31)
                    {
                        var shortMonths = Enumerable.Range(1, 12).Where(month => ScheduleMasks.HasMonth(recurrence.Months, month) && DateTime.DaysInMonth(2025, month) < recurrence.DayOfMonth)
                            .ToArray();
                        if (shortMonths.Length > 0 && shortMonths.Length == Enumerable.Range(1, 12).Count(month => ScheduleMasks.HasMonth(recurrence.Months, month)) &&
                            !(recurrence.DayOfMonth == 29 && shortMonths.All(month => month == 2)))
                            messages.Add(new("DayOfMonth", $"None of the selected months has a day {recurrence.DayOfMonth}. Choose Last day instead."));
                        else if (shortMonths.Length > 0)
                            messages.Add(new("DayOfMonth",
                                $"Months with fewer than {recurrence.DayOfMonth} days are skipped ({string.Join(", ", shortMonths.Select(month => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(month)))}). Choose Last day to run in every month.",
                                IsWarning: true));
                    }
                }
                else if (recurrence.WeekOfMonth is < 1 or > ScheduleMasks.LastWeek)
                {
                    messages.Add(new("WeekOfMonth", "Choose 1st, 2nd, 3rd, 4th or Last."));
                }

                break;
        }

        return messages;
    }

    private static bool Occurs(ScheduleRecurrence recurrence, DateOnly day)
    {
        switch (recurrence.Frequency)
        {
            case ScheduleFrequency.Hourly:
                return (recurrence.DaysOfWeek & ScheduleMasks.AllDays) == 0 || ScheduleMasks.HasDay(recurrence.DaysOfWeek, day.DayOfWeek);
            case ScheduleFrequency.Daily:
                return ScheduleMasks.HasDay(recurrence.DaysOfWeek, day.DayOfWeek);
            case ScheduleFrequency.Weekly:
            {
                if (!ScheduleMasks.HasDay(recurrence.DaysOfWeek, day.DayOfWeek)) return false;
                if (recurrence.IntervalWeeks <= 1) return true;
                var anchor = WeekStart(recurrence.AnchorDate ?? day);
                var weeks = (WeekStart(day).DayNumber - anchor.DayNumber) / 7;
                return weeks >= 0 && weeks % recurrence.IntervalWeeks == 0;
            }
            default:
            {
                if (!ScheduleMasks.HasMonth(recurrence.Months, day.Month)) return false;
                var daysInMonth = DateTime.DaysInMonth(day.Year, day.Month);
                if (recurrence.MonthlyMode == MonthlyMode.DayOfMonth)
                    return recurrence.DayOfMonth == ScheduleMasks.LastDay ? day.Day == daysInMonth : day.Day == recurrence.DayOfMonth;
                if (day.DayOfWeek != recurrence.Weekday) return false;
                return recurrence.WeekOfMonth == ScheduleMasks.LastWeek ? day.Day + 7 > daysInMonth : (day.Day - 1) / 7 + 1 == recurrence.WeekOfMonth;
            }
        }
    }

    private static IEnumerable<TimeOnly> TimesOn(ScheduleRecurrence recurrence)
    {
        if (recurrence.Frequency != ScheduleFrequency.Hourly)
        {
            yield return recurrence.RunTime;
            yield break;
        }

        var from = recurrence.WindowStart ?? TimeOnly.MinValue;
        var to = recurrence.WindowEnd ?? new TimeOnly(23, 59);
        var step = TimeSpan.FromHours(Math.Clamp(recurrence.IntervalHours, 1, 23));
        for (var time = from.ToTimeSpan(); time <= to.ToTimeSpan(); time += step)
            yield return TimeOnly.FromTimeSpan(time);
    }

    /// <summary>Monday of the week containing <paramref name="day"/>.</summary>
    private static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>
    /// Local wall-clock time to UTC. A time skipped by a daylight-saving change runs at the first valid minute after
    /// the gap; a repeated (ambiguous) time runs at its first occurrence.
    /// </summary>
    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var guard = 0;
        while (zone.IsInvalidTime(local) && guard++ < 180) local = local.AddMinutes(1);
        if (zone.IsAmbiguousTime(local))
        {
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return new DateTimeOffset(local, offset).ToUniversalTime();
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }
}
