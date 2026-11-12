using EngineeringWorkspace.Application.Scheduling;
using FluentAssertions;

namespace EngineeringWorkspace.Application.Tests;

public sealed class NextRunCalculatorTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static DateTimeOffset U(int year, int month, int day, int hour = 0, int minute = 0) => new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Daily_runs_today_when_the_time_is_still_ahead_otherwise_tomorrow()
    {
        var daily = new ScheduleRecurrence(ScheduleFrequency.Daily, new TimeOnly(6, 30));
        NextRunCalculator.Next(daily, U(2026, 11, 9, 5), Utc).Should().Be(U(2026, 11, 9, 6, 30));
        NextRunCalculator.Next(daily, U(2026, 11, 9, 6, 30), Utc).Should().Be(U(2026, 11, 10, 6, 30), "the result is strictly after the given instant");
    }

    [Fact]
    public void Daily_respects_the_selected_days()
    {
        var weekdays = new ScheduleRecurrence(ScheduleFrequency.Daily, new TimeOnly(8, 0), ScheduleMasks.Weekdays);
        // 2026-11-13 is a Friday: the next run after Friday 09:00 is Monday.
        NextRunCalculator.Next(weekdays, U(2026, 11, 13, 9), Utc).Should().Be(U(2026, 11, 16, 8));
    }

    [Fact]
    public void Weekly_every_two_weeks_counts_from_the_anchor_week()
    {
        var biweekly = new ScheduleRecurrence(ScheduleFrequency.Weekly, new TimeOnly(7, 0), ScheduleMasks.Day(DayOfWeek.Tuesday), IntervalWeeks: 2,
            AnchorDate: new DateOnly(2026, 11, 2));
        var runs = NextRunCalculator.NextOccurrences(biweekly, U(2026, 11, 1), Utc, 3);
        runs.Should().Equal(U(2026, 11, 3, 7), U(2026, 11, 17, 7), U(2026, 12, 1, 7));
    }

    [Fact]
    public void Monthly_day_31_skips_shorter_months_and_warns()
    {
        var day31 = new ScheduleRecurrence(ScheduleFrequency.Monthly, new TimeOnly(1, 0), DayOfMonth: 31);
        NextRunCalculator.NextOccurrences(day31, U(2026, 11, 1), Utc, 2).Should().Equal(U(2026, 12, 31, 1), U(2027, 1, 31, 1));
        NextRunCalculator.Validate(day31).Should().ContainSingle(message => message.IsWarning && message.Field == "DayOfMonth");
    }

    [Fact]
    public void Monthly_last_day_runs_every_month_including_february()
    {
        var last = new ScheduleRecurrence(ScheduleFrequency.Monthly, new TimeOnly(22, 0), DayOfMonth: ScheduleMasks.LastDay);
        NextRunCalculator.NextOccurrences(last, U(2028, 1, 1), Utc, 3).Should().Equal(U(2028, 1, 31, 22), U(2028, 2, 29, 22), U(2028, 3, 31, 22));
    }

    [Fact]
    public void Monthly_nth_weekday_and_last_weekday()
    {
        var secondTuesday = new ScheduleRecurrence(ScheduleFrequency.Monthly, new TimeOnly(18, 0), MonthlyMode: MonthlyMode.NthWeekday, WeekOfMonth: 2,
            Weekday: DayOfWeek.Tuesday);
        NextRunCalculator.NextOccurrences(secondTuesday, U(2026, 11, 1), Utc, 2).Should().Equal(U(2026, 11, 10, 18), U(2026, 12, 8, 18));

        var lastFriday = secondTuesday with { WeekOfMonth = ScheduleMasks.LastWeek, Weekday = DayOfWeek.Friday };
        NextRunCalculator.Next(lastFriday, U(2026, 11, 1), Utc).Should().Be(U(2026, 11, 27, 18));
    }

    [Fact]
    public void Monthly_only_in_the_selected_months()
    {
        var quarterly = new ScheduleRecurrence(ScheduleFrequency.Monthly, new TimeOnly(9, 0), Months: ScheduleMasks.Months([1, 4, 7, 10]), DayOfMonth: 15);
        NextRunCalculator.Next(quarterly, U(2026, 11, 1), Utc).Should().Be(U(2027, 1, 15, 9));
    }

    [Fact]
    public void Hourly_runs_every_n_hours_inside_the_window_on_the_selected_days()
    {
        var hourly = new ScheduleRecurrence(ScheduleFrequency.Hourly, TimeOnly.MinValue, ScheduleMasks.Weekdays, IntervalHours: 3, WindowStart: new TimeOnly(8, 0),
            WindowEnd: new TimeOnly(18, 0));
        // Friday 2026-11-13 16:30 -> 17:00 is the last slot (8, 11, 14, 17); then Monday 08:00.
        NextRunCalculator.NextOccurrences(hourly, U(2026, 11, 13, 16, 30), Utc, 2).Should().Equal(U(2026, 11, 13, 17), U(2026, 11, 16, 8));
    }

    [Fact]
    public void Hourly_without_window_runs_around_the_clock()
    {
        var every6 = new ScheduleRecurrence(ScheduleFrequency.Hourly, TimeOnly.MinValue, 0, IntervalHours: 6);
        NextRunCalculator.NextOccurrences(every6, U(2026, 11, 9, 19), Utc, 2).Should().Equal(U(2026, 11, 10, 0), U(2026, 11, 10, 6));
    }

    [Fact]
    public void Local_times_are_converted_across_daylight_saving_changes()
    {
        var daily = new ScheduleRecurrence(ScheduleFrequency.Daily, new TimeOnly(9, 0));
        // New York: EDT (UTC-4) before 2026-11-01, EST (UTC-5) after.
        NextRunCalculator.Next(daily, U(2026, 10, 31, 14), NewYork).Should().Be(U(2026, 11, 1, 14));
        NextRunCalculator.Next(daily, U(2026, 10, 30, 12), NewYork).Should().Be(U(2026, 10, 30, 13));

        // 02:30 does not exist on 2027-03-14 (clocks jump 02:00 -> 03:00): it runs at 03:00 local (07:00 UTC).
        var gap = new ScheduleRecurrence(ScheduleFrequency.Daily, new TimeOnly(2, 30));
        NextRunCalculator.Next(gap, U(2027, 3, 14, 0), NewYork).Should().Be(U(2027, 3, 14, 7));
    }

    [Theory]
    [InlineData(ScheduleFrequency.Daily, 0, 1, 1, 1, 1, "DaysOfWeek")]
    [InlineData(ScheduleFrequency.Hourly, ScheduleMasks.AllDays, 0, 1, 1, 1, "IntervalHours")]
    [InlineData(ScheduleFrequency.Hourly, ScheduleMasks.AllDays, 24, 1, 1, 1, "IntervalHours")]
    [InlineData(ScheduleFrequency.Weekly, ScheduleMasks.AllDays, 1, 0, 1, 1, "IntervalWeeks")]
    [InlineData(ScheduleFrequency.Monthly, ScheduleMasks.AllDays, 1, 1, 0, 1, "Months")]
    [InlineData(ScheduleFrequency.Monthly, ScheduleMasks.AllDays, 1, 1, ScheduleMasks.AllMonths, 0, "DayOfMonth")]
    public void Invalid_recurrences_are_reported_and_never_scheduled(ScheduleFrequency frequency, int days, int hours, int weeks, int months, int dayOfMonth, string field)
    {
        var recurrence = new ScheduleRecurrence(frequency, new TimeOnly(6, 0), days, hours, IntervalWeeks: weeks, Months: months, DayOfMonth: dayOfMonth);
        NextRunCalculator.Validate(recurrence).Should().Contain(message => message.Field == field && !message.IsWarning);
        NextRunCalculator.Next(recurrence, U(2026, 11, 1), Utc).Should().BeNull();
    }

    [Fact]
    public void Day_30_in_february_only_never_occurs()
    {
        var never = new ScheduleRecurrence(ScheduleFrequency.Monthly, new TimeOnly(6, 0), Months: ScheduleMasks.Month(2), DayOfMonth: 30);
        NextRunCalculator.Validate(never).Should().Contain(message => !message.IsWarning);
        NextRunCalculator.Next(never, U(2026, 11, 1), Utc).Should().BeNull();
    }

    [Fact]
    public void Descriptions_read_naturally()
    {
        new ScheduleRecurrence(ScheduleFrequency.Daily, new TimeOnly(6, 0), ScheduleMasks.Weekdays).Describe().Should().Be("Mon–Fri at 06:00");
        new ScheduleRecurrence(ScheduleFrequency.Monthly, new TimeOnly(18, 0), MonthlyMode: MonthlyMode.NthWeekday, WeekOfMonth: 2, Weekday: DayOfWeek.Tuesday)
            .Describe().Should().Be("2nd Tuesday of Every month at 18:00");
        new ScheduleRecurrence(ScheduleFrequency.Hourly, TimeOnly.MinValue, 0, IntervalHours: 4).Describe().Should().Be("Every 4 hours, Every day");
    }
}
