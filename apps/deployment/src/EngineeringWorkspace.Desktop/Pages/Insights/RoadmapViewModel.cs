using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Export;
using EngineeringWorkspace.Application.Governance;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Application.Reporting;
using EngineeringWorkspace.Desktop.Controls;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Desktop.Pages.Reporting;
using EngineeringWorkspace.Desktop.Services;
using EngineeringWorkspace.Domain.Customers;

namespace EngineeringWorkspace.Desktop.Pages.Insights;

/// <summary>A roadmap item as shown in the grid.</summary>
public sealed record RoadmapRow(PlannedItem Planned, string SolutionArea, string PackageName)
{
    public RoadmapItem Item => Planned.Item;
    public int Order => Planned.Order;
    public string Id => Item.Control.Id;
    public string Name => Item.Control.Name;
    public string Priority => Item.Priority;
    public string Severity => Item.Control.Severity;
    public string Effort => Item.Control.Implementation.Effort;
    public string Estimate => Item.Control.Implementation.EstimatedTimeText;
    public string Hours => Planned.Hours.ToString("0.#", CultureInfo.CurrentCulture);
    public string Status => Item.Status;
    public string Result => Item.Fact.Status;
    public string DependsOn => Item.DependsOn;
    public string Start => Planned.StartDate.ToString("ddd d MMM", CultureInfo.CurrentCulture);
    public string Finish => Planned.FinishDate.ToString("ddd d MMM", CultureInfo.CurrentCulture);
    public string Value => Planned.Score.ToString("0.#", CultureInfo.CurrentCulture);
    public string PolicySets => string.Join(", ", Item.Control.PolicySets);
}

/// <summary>A roadmap phase with its scheduled rows.</summary>
public sealed partial class RoadmapPhaseView(PlannedPhase phase, IReadOnlyList<RoadmapRow> rows) : ObservableObject
{
    public PlannedPhase Phase { get; } = phase;
    public IReadOnlyList<RoadmapRow> Rows { get; } = rows;
    public int Count => Rows.Count;
    public string Header => $"{Phase.Name} — {Phase.Window} — {Count} item(s), {Rows.Sum(row => row.Planned.Hours):0.#} hour(s)";

    /// <summary>Phases start collapsed the first time the page opens.</summary>
    [ObservableProperty] private bool _isExpanded;

    [ObservableProperty] private bool _isVisible = true;
}

/// <summary>
/// Dashboard → Roadmap: the customer's remediation roadmap. Gaps from the latest assessment are placed in four phases
/// (foundation and critical, high, medium, hardening), ordered by dependencies and value per hour, and scheduled on a
/// Monday-Friday calendar of 8-hour days from the chosen start date (phase dates and hours come from that schedule).
/// The flow timeline shows the work packages left to right with arrows from each dependency.
/// </summary>
public sealed partial class RoadmapViewModel(ICustomerContextService context, ICustomerProfileStore store, IReportingService reporting, ICatalogService catalog,
    IChangeRequestRepository changes, ExportCoordinator export) : CustomerScopedPageViewModel(context, store)
{
    private IReadOnlyList<RoadmapPhase> _phases = [];
    private RoadmapPlan? _plan;

    public ObservableCollection<RoadmapPhaseView> Phases { get; } = [];
    public ObservableCollection<KpiTile> Kpis { get; } = [];
    public ObservableCollection<TimelineNode> Timeline { get; } = [];
    public ObservableCollection<ChartSlice> HoursByPhase { get; } = [];
    public ObservableCollection<ChartSlice> ItemsBySeverity { get; } = [];
    public ObservableCollection<ChartSlice> ValueBurnUp { get; } = [];
    public MultiSelectFilter SeverityFilter { get; } = new("Severity", options: new[] { "Critical", "High", "Medium", "Low", "Informational" }.Select(value => (value, value)));
    public MultiSelectFilter StatusFilter { get; } = new("Status", options: new[] { "Not Started", "In Progress", "Planned" }.Select(value => (value, value)));
    public MultiSelectFilter PhaseFilter { get; } = new("Phase", options: RoadmapBuilder.Phases.Select(phase => ($"Phase {phase.Number}", phase.Name)));
    public MultiSelectFilter AreaFilter { get; } = new("Solution Area");
    public IReadOnlyList<string> TimelineModes { get; } = ["Work packages (policy sets)", "Individual controls (first 80)"];

    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private string? _scopeText;
    [ObservableProperty] private bool _hasItems;
    [ObservableProperty] private string? _emptyText;
    [ObservableProperty] private DateTime _startDate = NextMonday(DateTime.Today);
    [ObservableProperty] private double _hoursPerDay = 8;
    [ObservableProperty] private int _engineers = 1;
    [ObservableProperty] private double _zoom = 48;
    [ObservableProperty] private string _timelineMode = "Work packages (policy sets)";
    [ObservableProperty] private string _planSummary = string.Empty;
    [ObservableProperty] private string _totalHoursText = string.Empty;
    [ObservableProperty] private string _orderingText = string.Empty;
    [ObservableProperty] private string _detailTitle = "Click a box or a chart for details.";
    [ObservableProperty] private string _detailText = string.Empty;
    [ObservableProperty] private bool _showGrid = true;
    [ObservableProperty] private bool _showTimeline = true;
    [ObservableProperty] private bool _showCharts = true;

    private static DateTime NextMonday(DateTime date)
    {
        while (date.DayOfWeek != DayOfWeek.Monday) date = date.AddDays(1);
        return date;
    }

    partial void OnSearchChanged(string value) => Show();
    partial void OnStartDateChanged(DateTime value) => Replan();
    partial void OnHoursPerDayChanged(double value) => Replan();
    partial void OnEngineersChanged(int value) => Replan();
    partial void OnTimelineModeChanged(string value) => BuildTimeline();

    public override async Task OnNavigatedToAsync(RouteDescriptor route, CancellationToken cancellationToken)
    {
        SeverityFilter.Changed += (_, _) => Show();
        StatusFilter.Changed += (_, _) => Show();
        PhaseFilter.Changed += (_, _) => Show();
        AreaFilter.Changed += (_, _) => Show();
        await base.OnNavigatedToAsync(route, cancellationToken);
    }

    protected override async Task OnCustomerChangedAsync(CustomerProfile? customer)
    {
        _phases = [];
        _plan = null;
        Show();
        if (customer is null)
        {
            EmptyText = "Choose a customer to see its roadmap.";
            return;
        }

        if (!reporting.IsAvailable)
        {
            EmptyText = "The application database is not ready, so stored assessment results are unavailable.";
            return;
        }

        IsBusy = true;
        try
        {
            var results = await reporting.GetCustomerResultsAsync(customer.Id.Value, ReportScope.ActiveBaselines);
            if (!results.HasResults)
            {
                EmptyText = $"No roadmap items for {customer.Name}: it has no stored assessment yet. Run one on Assessment Scan or Live Assessment.";
                ScopeText = null;
                return;
            }

            var planned = changes.IsAvailable
                ? (await changes.ListChangeRequestsAsync(customer.Id.Value)).Where(request => request.StatusCode is not (ChangeStatusCodes.Completed or ChangeStatusCodes.Rejected))
                    .SelectMany(request => request.Items).Select(item => catalog.Catalog.ResolveId(item.ItemKey)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;
            _phases = RoadmapBuilder.Build(results.Facts, catalog.Catalog, planned);
            ScopeText = $"{results.ScopeDescription}. Based on '{results.Latest!.Name}' of {(results.Latest.CompletedAtUtc ?? results.Latest.RequestedAtUtc).ToLocalTime():yyyy-MM-dd HH:mm}.";
            EmptyText = _phases.Sum(phase => phase.Items.Count) == 0 ? $"No roadmap items for {customer.Name}: every control in scope is met." : null;
            AreaFilter.SetOptions(_phases.SelectMany(phase => phase.Items).Select(item => catalog.Catalog.SolutionAreaName(item.Control.SolutionArea)).Distinct().Order()
                .Select(name => (name, name)));
            Replan();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Replan()
    {
        if (_phases.Count == 0)
        {
            Show();
            return;
        }

        _plan = RoadmapPlanner.Plan(_phases, catalog.Catalog, new RoadmapPlanOptions(DateOnly.FromDateTime(StartDate), Math.Clamp(HoursPerDay, 1, 24), Math.Clamp(Engineers, 1, 20)));
        OrderingText = "Order: work that others depend on comes first (arrows); then earlier phases; then the most value per hour " +
                       "(severity × frameworks covered × foundation), so the tenant is secured and compliance scores rise fastest. " +
                       $"Schedule: {HoursPerDay:0.#}-hour days, Monday to Friday, {Engineers} engineer(s) in parallel.";
        Show();
    }

    private void Show()
    {
        var expanded = Phases.Where(phase => phase.IsExpanded).Select(phase => phase.Phase.Number).ToHashSet();
        Phases.Clear();
        Kpis.Clear();
        HoursByPhase.Clear();
        ItemsBySeverity.Clear();
        ValueBurnUp.Clear();
        if (_plan is null)
        {
            HasItems = false;
            Timeline.Clear();
            return;
        }

        var term = Search.Trim();
        var current = catalog.Catalog;
        var packageNames = _plan.Packages.ToDictionary(package => package.Key, package => package.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var phase in _plan.Phases)
        {
            if (!PhaseFilter.Matches($"Phase {phase.Number}")) continue;
            var rows = phase.Items.Where(item => SeverityFilter.Matches(item.Item.Control.Severity) && StatusFilter.Matches(item.Item.Status) &&
                                                 AreaFilter.Matches(current.SolutionAreaName(item.Item.Control.SolutionArea)) &&
                                                 (term.Length == 0 || item.Item.Control.Id.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                                                  item.Item.Control.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)))
                .Select(item => new RoadmapRow(item, current.SolutionAreaName(item.Item.Control.SolutionArea), packageNames.GetValueOrDefault(item.Package, item.Package)))
                .OrderBy(row => row.Order).ToArray();
            Phases.Add(new RoadmapPhaseView(phase, rows) { IsExpanded = expanded.Contains(phase.Number) });
        }

        var all = Phases.SelectMany(phase => phase.Rows).ToArray();
        HasItems = all.Length > 0;
        var hours = all.Sum(row => row.Planned.Hours);
        Kpis.Add(new KpiTile("Roadmap Items", all.Length.ToString("N0", CultureInfo.CurrentCulture), $"{all.Count(row => row.Status == "Planned")} planned in change requests"));
        Kpis.Add(new KpiTile("Estimated Effort", $"{hours:N0} h", $"{Math.Ceiling(hours / Math.Max(1, HoursPerDay)):N0} work day(s) of effort; generated estimates – validate"));
        Kpis.Add(new KpiTile("Completion", _plan.Finish.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture),
            $"{_plan.WorkDays} work day(s) from {_plan.Start:ddd d MMM} with {Engineers} engineer(s)"));
        Kpis.Add(new KpiTile("Critical and High", all.Count(row => row.Severity is "Critical" or "High").ToString("N0", CultureInfo.CurrentCulture), "Severity"));
        foreach (var phase in Phases)
            Kpis.Add(new KpiTile($"Phase {phase.Phase.Number}", $"{phase.Count:N0} item(s) · {phase.Rows.Sum(row => row.Planned.Hours):N0} h",
                phase.Phase.Start is null ? "no items" : $"{phase.Phase.Start:ddd d MMM} – {phase.Phase.Finish:ddd d MMM} ({phase.Phase.WorkDays} work days)"));
        TotalHoursText = $"{hours:N0} h";
        PlanSummary = $"{all.Length:N0} item(s), {hours:N0} hours, {_plan.Start:ddd d MMM yyyy} to {_plan.Finish:ddd d MMM yyyy}.";

        foreach (var phase in Phases)
            HoursByPhase.Add(new ChartSlice($"Phase {phase.Phase.Number}", Math.Round(phase.Rows.Sum(row => row.Planned.Hours), 1), null, phase.Header, phase,
                phase.Rows.GroupBy(row => row.PackageName).Select(group => new ChartSlice(group.Key, Math.Round(group.Sum(row => row.Planned.Hours), 1), null,
                    $"{group.Count()} control(s)", group.Key)).OrderByDescending(slice => slice.Value).ToArray()));
        foreach (var severity in all.GroupBy(row => row.Severity).OrderBy(group => ReportDimensions.SeverityRank(group.Key)))
            ItemsBySeverity.Add(new ChartSlice(severity.Key, severity.Count(), severity.Key switch
                {
                    "Critical" => "#C42B1C", "High" => "#E06C00", "Medium" => "#C19C00", "Low" => "#107C10", _ => "#5B6B7F"
                }, $"{severity.Count()} item(s), {severity.Sum(row => row.Planned.Hours):0.#} h", null,
                severity.GroupBy(row => row.SolutionArea).Select(group => new ChartSlice(group.Key, group.Count(), null, string.Join(", ", group.Take(8).Select(row => row.Id)))).ToArray()));

        // Value delivered by week (cumulative %), showing how fast risk comes down.
        var totalValue = Math.Max(1e-9, all.Sum(row => row.Planned.Score));
        var cumulative = 0.0;
        foreach (var week in all.OrderBy(row => row.Planned.FinishHour).GroupBy(row => (int)(row.Planned.FinishHour / (Math.Max(1, HoursPerDay) * 5))))
        {
            cumulative += week.Sum(row => row.Planned.Score);
            ValueBurnUp.Add(new ChartSlice($"W{week.Key + 1}", Math.Round(cumulative / totalValue * 100, 1), null,
                $"Week {week.Key + 1}: {week.Count()} item(s) finished; {cumulative / totalValue:P0} of the roadmap's value delivered."));
        }

        BuildTimeline();
    }

    private void BuildTimeline()
    {
        Timeline.Clear();
        if (_plan is null) return;
        var visible = Phases.SelectMany(phase => phase.Rows).Select(row => row.Planned).ToHashSet();
        string Color(int phase) => phase switch { 1 => "#C42B1C", 2 => "#E06C00", 3 => "#2B88D8", _ => "#5B6B7F" };
        if (TimelineMode.StartsWith("Work", StringComparison.Ordinal))
        {
            foreach (var package in _plan.Packages.Where(package => package.Items.Any(visible.Contains)))
                Timeline.Add(new TimelineNode(package.Key, package.Name, $"{package.Items.Count} control(s) · {package.Hours:0.#} h · Phase {package.Phase}",
                    package.StartHour, package.FinishHour, package.DependsOn, Color(package.Phase),
                    $"{package.Name}\n{package.StartDate:ddd d MMM} – {package.FinishDate:ddd d MMM}, {package.Hours:0.#} hour(s), value {package.Score:0.#}\n{package.Reason}\n\n" +
                    string.Join("\n", package.Items.Take(25).Select(item => $"{item.Item.Control.Id} {item.Item.Control.Name} ({item.Hours:0.#} h, {item.Item.Control.Severity})")),
                    package));
        }
        else
        {
            var packages = _plan.Packages.ToDictionary(package => package.Key, StringComparer.OrdinalIgnoreCase);
            var items = _plan.Items.Where(visible.Contains).OrderBy(item => item.StartHour).Take(80).ToArray();
            var keys = items.Select(item => item.Item.Control.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                // A control depends on the last control of each package its package requires.
                var dependsOn = packages.TryGetValue(item.Package, out var package)
                    ? package.DependsOn.Select(key => packages.GetValueOrDefault(key)?.Items.LastOrDefault()?.Item.Control.Id).OfType<string>().Where(keys.Contains).ToArray()
                    : [];
                Timeline.Add(new TimelineNode(item.Item.Control.Id, item.Item.Control.Name, $"{item.Item.Control.Severity} · {item.Hours:0.#} h", item.StartHour, item.FinishHour,
                    dependsOn, Color(item.Item.Phase),
                    $"{item.Item.Control.Id} {item.Item.Control.Name}\n{item.StartDate:ddd d MMM} – {item.FinishDate:ddd d MMM}\nPhase {item.Item.Phase}, {item.Item.Control.Severity}, value {item.Score:0.#}",
                    item));
            }
        }
    }

    [RelayCommand]
    private void ExpandAll()
    {
        foreach (var phase in Phases) phase.IsExpanded = true;
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var phase in Phases) phase.IsExpanded = false;
    }

    [RelayCommand]
    private void ShowDetail(object? item)
    {
        switch (item)
        {
            case TimelineNode node:
                DetailTitle = node.Label;
                DetailText = node.Detail;
                break;
            case ChartSlice slice:
                DetailTitle = slice.Label;
                DetailText = slice.Detail ?? slice.Value.ToString("0.#", CultureInfo.CurrentCulture);
                break;
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => OnCustomerChangedAsync(SelectedCustomer);

    [RelayCommand]
    private async Task ExportAsync(object? format)
    {
        if (SelectedCustomer is null || !HasItems || _plan is null)
        {
            StatusMessage = "There is nothing to export.";
            return;
        }

        var document = new ExportDocument("Remediation Roadmap", SelectedCustomer.Name, DateTimeOffset.UtcNow,
            [.. Kpis.Select(kpi => new KeyValuePair<string, string>(kpi.Title, $"{kpi.Value} – {kpi.Detail}")), new("Ordering", OrderingText)],
            [
                new ExportTable("Work packages (in order)", ["#", "Work package", "Phase", "Start", "Finish", "Hours", "Controls", "Depends on", "Why here"],
                    _plan.Packages.Select((package, index) => (IReadOnlyList<string?>)
                    [
                        (index + 1).ToString(CultureInfo.InvariantCulture), package.Name, package.Phase.ToString(CultureInfo.InvariantCulture),
                        package.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), package.FinishDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        package.Hours.ToString("0.#", CultureInfo.InvariantCulture), package.Items.Count.ToString(CultureInfo.InvariantCulture), string.Join(", ", package.DependsOn), package.Reason
                    ]).ToArray()),
                .. Phases.Select(phase => new ExportTable(phase.Header,
                    ["#", "ID", "Control", "Work package", "Priority", "Severity", "Hours", "Start", "Finish", "Status", "Result", "Solution Area", "Depends On"],
                    phase.Rows.Select(row => (IReadOnlyList<string?>)new string?[]
                    {
                        row.Order.ToString(CultureInfo.InvariantCulture), row.Id, row.Name, row.PackageName, row.Priority, row.Severity, row.Hours,
                        row.Planned.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), row.Planned.FinishDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        row.Status, row.Result, row.SolutionArea, row.DependsOn
                    }).ToArray()))
            ], ScopeText);
        try
        {
            StatusMessage = await export.ExportAsync(document, ExportCoordinator.Parse(format)) ?? StatusMessage;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"The export could not be written: {exception.Message}";
        }
    }
}
