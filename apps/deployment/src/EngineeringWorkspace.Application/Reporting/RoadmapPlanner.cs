using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Application.Planning;

namespace EngineeringWorkspace.Application.Reporting;

/// <summary>Roadmap planning options: start date, working hours per day (Monday to Friday) and engineers working in parallel.</summary>
public sealed record RoadmapPlanOptions(DateOnly Start, double HoursPerDay = 8, int Engineers = 1);

/// <summary>A roadmap item with its value score and its place in the schedule.</summary>
public sealed record PlannedItem(RoadmapItem Item, string Package, double Score, double StartHour, double FinishHour, DateOnly StartDate, DateOnly FinishDate, int Order)
{
    public double Hours => Item.EstimatedMinutes / 60.0;
}

/// <summary>A work package: the gaps of one policy set (or of one solution area when a control belongs to none), deployed together.</summary>
public sealed record WorkPackage(string Key, string Name, int Phase, IReadOnlyList<PlannedItem> Items, IReadOnlyList<string> DependsOn, double Hours, double Score,
    double StartHour, double FinishHour, DateOnly StartDate, DateOnly FinishDate, int Level, string Reason);

/// <summary>A phase with its real schedule: dates, work days and hours.</summary>
public sealed record PlannedPhase(int Number, string Name, IReadOnlyList<PlannedItem> Items, DateOnly? Start, DateOnly? Finish, int WorkDays, double Hours)
{
    public string Window => Start is { } start && Finish is { } finish ? $"{start:ddd d MMM} – {finish:ddd d MMM yyyy} ({WorkDays} work day(s))" : "no items";
}

public sealed record RoadmapPlan(IReadOnlyList<PlannedPhase> Phases, IReadOnlyList<WorkPackage> Packages, IReadOnlyList<PlannedItem> Items, DateOnly Start, DateOnly Finish,
    double TotalHours, int WorkDays, RoadmapPlanOptions Options)
{
    public WorkCalendar Calendar => new(Options.Start, Options.HoursPerDay);
}

/// <summary>
/// Orders and schedules the roadmap. Work packages (policy sets) that others require come first; among the packages that
/// are ready, earlier phases first and then the most value per hour: value = severity weight × (1 + 0.1 × frameworks
/// covered) × 1.5 for foundation policy sets, so the tenant is secured and compliance scores rise as fast as possible.
/// Effort hours become dates on a Monday-Friday calendar of <see cref="RoadmapPlanOptions.HoursPerDay"/> hours with
/// <see cref="RoadmapPlanOptions.Engineers"/> people working in parallel. Phase windows are the real first and last dates.
/// </summary>
public static class RoadmapPlanner
{
    public static double SeverityWeight(string severity) => ReportDimensions.SeverityRank(severity) switch { 0 => 10, 1 => 7, 2 => 4, 3 => 2, _ => 1 };

    public static double Value(RoadmapItem item) =>
        SeverityWeight(item.Control.Severity) * (1 + 0.1 * Math.Min(10, item.Control.Frameworks.Count)) * (item.DeploymentOrder == 0 ? 1.5 : 1);

    public static RoadmapPlan Plan(IReadOnlyList<RoadmapPhase> phases, ControlCatalog catalog, RoadmapPlanOptions options)
    {
        var calendar = new WorkCalendar(options.Start, options.HoursPerDay);
        var items = phases.SelectMany(phase => phase.Items).ToArray();

        // Work packages: the control's first top-level policy set, else its solution area.
        string PackageOf(RoadmapItem item)
        {
            var set = item.Control.PolicySets.Select(catalog.FindPolicySet).OfType<CatalogPolicySet>().Where(candidate => candidate.Includes.Count == 0)
                .OrderBy(candidate => candidate.DeploymentOrder).FirstOrDefault();
            return set?.Id ?? $"{catalog.SolutionAreaName(item.Control.SolutionArea)} (standalone)";
        }

        var groups = items.GroupBy(PackageOf, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var schedulable = groups.Select(pair =>
        {
            var set = catalog.FindPolicySet(pair.Key);
            var requires = (set?.Requires ?? []).Where(groups.ContainsKey).ToArray();
            var hours = pair.Value.Sum(item => item.EstimatedMinutes) / 60.0;
            var value = pair.Value.Sum(Value);
            var phase = pair.Value.Min(item => item.Phase);
            // Earlier phases first (whole-phase steps), then value per hour.
            var priority = (5 - phase) * 1000 + value / Math.Max(0.25, hours);
            return new SchedulableItem(pair.Key, hours, priority, requires);
        }).ToArray();
        var scheduled = WorkScheduler.Schedule(schedulable, calendar, Math.Max(1, options.Engineers)).ToDictionary(item => item.Item.Key, StringComparer.OrdinalIgnoreCase);

        var planned = new List<PlannedItem>();
        var packages = new List<WorkPackage>();
        foreach (var (key, slot) in scheduled.OrderBy(pair => pair.Value.Order))
        {
            var cursor = slot.StartHour;
            var packageItems = new List<PlannedItem>();
            foreach (var item in groups[key].OrderBy(item => item.Phase).ThenByDescending(item => Value(item) / Math.Max(0.1, item.EstimatedMinutes / 60.0)))
            {
                var hours = item.EstimatedMinutes / 60.0;
                var entry = new PlannedItem(item, key, Math.Round(Value(item), 2), cursor, cursor + hours, calendar.DateAt(cursor), calendar.FinishDate(cursor, hours), planned.Count + 1);
                packageItems.Add(entry);
                planned.Add(entry);
                cursor += hours;
            }

            var phase = groups[key].Min(item => item.Phase);
            var set = catalog.FindPolicySet(key);
            var reason = slot.Item.DependsOn.Count > 0
                ? $"After {string.Join(", ", slot.Item.DependsOn.Select(id => catalog.FindPolicySet(id)?.DisplayName ?? id))} (required first)."
                : phase == 1 ? "Foundation and critical gaps: highest risk reduced per hour." : $"Phase {phase}: ordered by value per hour.";
            packages.Add(new WorkPackage(key, set?.DisplayName ?? key, phase, packageItems, slot.Item.DependsOn, slot.Item.Hours, packageItems.Sum(item => item.Score),
                slot.StartHour, slot.FinishHour, slot.StartDate, slot.FinishDate, slot.Level, reason));
        }

        var plannedPhases = phases.Select(phase =>
        {
            var phaseItems = planned.Where(item => item.Item.Phase == phase.Number).OrderBy(item => item.StartHour).ToArray();
            DateOnly? start = phaseItems.Length == 0 ? null : phaseItems.Min(item => item.StartDate);
            DateOnly? finish = phaseItems.Length == 0 ? null : phaseItems.Max(item => item.FinishDate);
            var days = start is null ? 0 : CountWorkDays(start.Value, finish!.Value);
            return new PlannedPhase(phase.Number, phase.Name, phaseItems, start, finish, days, phaseItems.Sum(item => item.Hours));
        }).ToArray();
        var end = planned.Count == 0 ? calendar.Start : planned.Max(item => item.FinishDate);
        return new RoadmapPlan(plannedPhases, packages, planned, calendar.Start, end, planned.Sum(item => item.Hours), planned.Count == 0 ? 0 : CountWorkDays(calendar.Start, end), options);
    }

    private static int CountWorkDays(DateOnly start, DateOnly finish)
    {
        var count = 0;
        for (var day = start; day <= finish; day = day.AddDays(1))
            if (WorkCalendar.IsWorkDay(day)) count++;
        return count;
    }
}
