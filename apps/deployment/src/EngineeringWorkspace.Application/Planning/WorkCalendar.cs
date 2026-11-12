namespace EngineeringWorkspace.Application.Planning;

/// <summary>
/// Work-day calendar: Monday to Friday, <see cref="HoursPerDay"/> hours a day (8 by default). Converts effort hours
/// into dates: work that starts on a Monday morning and takes 12 hours finishes on Tuesday after 4 hours.
/// </summary>
public sealed class WorkCalendar(DateOnly start, double hoursPerDay = 8, IReadOnlySet<DateOnly>? holidays = null)
{
    public DateOnly Start { get; } = NextWorkDay(start, holidays);
    public double HoursPerDay { get; } = hoursPerDay <= 0 ? 8 : hoursPerDay;

    public static bool IsWorkDay(DateOnly date, IReadOnlySet<DateOnly>? holidays = null) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && holidays?.Contains(date) != true;

    public static DateOnly NextWorkDay(DateOnly date, IReadOnlySet<DateOnly>? holidays = null)
    {
        while (!IsWorkDay(date, holidays)) date = date.AddDays(1);
        return date;
    }

    /// <summary>The date on which work that started <paramref name="hoursFromStart"/> working hours after the start is being done.</summary>
    public DateOnly DateAt(double hoursFromStart)
    {
        var date = Start;
        var days = (int)Math.Floor(Math.Max(0, hoursFromStart) / HoursPerDay + 1e-9);
        while (days > 0)
        {
            date = date.AddDays(1);
            if (IsWorkDay(date, holidays)) days--;
        }

        return date;
    }

    /// <summary>The finish date of work occupying working hours [<paramref name="startHour"/>, <paramref name="startHour"/> + <paramref name="hours"/>).</summary>
    /// <summary>The working hour at which <paramref name="date"/> starts (0 for the calendar start or earlier; non-working days move to the next work day).</summary>
    public double HourOf(DateOnly date)
    {
        var hours = 0d;
        for (var guard = 0; DateAt(hours) < date && guard < 20000; guard++) hours += HoursPerDay;
        return hours;
    }

    public DateOnly FinishDate(double startHour, double hours) => DateAt(Math.Max(startHour, startHour + Math.Max(hours, 0.01) - 0.01));

    /// <summary>Number of work days from the start to <paramref name="date"/> inclusive.</summary>
    public int WorkDaysUntil(DateOnly date)
    {
        var count = 0;
        for (var day = Start; day <= date; day = day.AddDays(1))
            if (IsWorkDay(day, holidays)) count++;
        return count;
    }
}

/// <summary>A unit of work to schedule.</summary>
/// <param name="StartNotBeforeHour">Optional earliest start (working hours from the calendar start), for a task whose start date was set by hand.</param>
public sealed record SchedulableItem(string Key, double Hours, double Priority, IReadOnlyList<string> DependsOn, string? Lane = null, double? StartNotBeforeHour = null);

/// <summary>A scheduled item: working-hour offsets from the start and calendar dates.</summary>
public sealed record ScheduledItem(SchedulableItem Item, int Order, double StartHour, double FinishHour, DateOnly StartDate, DateOnly FinishDate, int Level);

/// <summary>
/// Orders and schedules work: a dependency always comes before the work that needs it; among the work that is ready,
/// the highest priority goes first (priority = risk reduced and compliance gained per hour). With
/// <paramref name="parallelStreams"/> engineers, ready work runs side by side; otherwise it runs one after another.
/// </summary>
public static class WorkScheduler
{
    public static IReadOnlyList<ScheduledItem> Schedule(IReadOnlyList<SchedulableItem> items, WorkCalendar calendar, int parallelStreams = 1)
    {
        var byKey = items.GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var dependencies = byKey.Values.ToDictionary(item => item.Key,
            item => item.DependsOn.Where(key => byKey.ContainsKey(key) && !string.Equals(key, item.Key, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            StringComparer.OrdinalIgnoreCase);
        BreakCycles(dependencies);

        var remaining = new HashSet<string>(byKey.Keys, StringComparer.OrdinalIgnoreCase);
        var finished = new Dictionary<string, (double Finish, int Level)>(StringComparer.OrdinalIgnoreCase);
        var streams = new double[Math.Max(1, parallelStreams)];
        var result = new List<ScheduledItem>();
        while (remaining.Count > 0)
        {
            // Ready: every dependency finished. Highest priority first; ties: the earliest it can start, then key.
            var ready = remaining.Where(key => dependencies[key].All(finished.ContainsKey))
                .Select(key => byKey[key])
                .OrderByDescending(item => item.Priority)
                .ThenBy(item => dependencies[item.Key].Select(dependency => finished[dependency].Finish).DefaultIfEmpty(0).Max())
                .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .First();
            var earliest = Math.Max(dependencies[ready.Key].Select(dependency => finished[dependency].Finish).DefaultIfEmpty(0).Max(), ready.StartNotBeforeHour ?? 0);
            var level = dependencies[ready.Key].Select(dependency => finished[dependency].Level + 1).DefaultIfEmpty(0).Max();
            var stream = Array.IndexOf(streams, streams.Min());
            var start = Math.Max(earliest, streams[stream]);
            var finish = start + Math.Max(0, ready.Hours);
            streams[stream] = finish;
            finished[ready.Key] = (finish, level);
            remaining.Remove(ready.Key);
            result.Add(new ScheduledItem(ready, result.Count + 1, start, finish, calendar.DateAt(start), calendar.FinishDate(start, ready.Hours), level));
        }

        return result;
    }

    /// <summary>Removes the last edge of any dependency cycle so scheduling always completes.</summary>
    private static void BreakCycles(Dictionary<string, List<string>> dependencies)
    {
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Visit(string key)
        {
            state[key] = 1;
            foreach (var dependency in dependencies[key].ToArray())
            {
                var mark = state.GetValueOrDefault(dependency);
                if (mark == 1) dependencies[key].Remove(dependency);
                else if (mark == 0) Visit(dependency);
            }

            state[key] = 2;
        }

        foreach (var key in dependencies.Keys.ToArray())
            if (state.GetValueOrDefault(key) == 0) Visit(key);
    }
}
