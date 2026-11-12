using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
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
using EngineeringWorkspace.Desktop.Services;
using EngineeringWorkspace.Domain.Customers;

namespace EngineeringWorkspace.Desktop.Pages.Reporting;

/// <summary>A summary figure shown as a KPI tile.</summary>
public sealed record KpiTile(string Title, string Value, string Detail);

/// <summary>One bar of a horizontal bar chart (width in pixels for a 0–100 % scale).</summary>
public sealed record ChartBar(string Label, double Percent, string Text, Brush Fill)
{
    public const double Scale = 3.2;
    public double Width => Math.Max(2, Percent * Scale);

    public static Brush FillFor(double? percent) =>
        (Brush)System.Windows.Application.Current.TryFindResource(percent switch
        {
            >= 80 => "AppBrush.Success",
            >= 50 => "AppBrush.Warning",
            null => "AppBrush.TextDisabled",
            _ => "AppBrush.Error"
        }) ?? Brushes.Gray;
}

/// <summary>A drill-down row: one control behind a group.</summary>
public sealed record FactRow(string Id, string Name, string Status, string Severity, string Kind, string SolutionArea, string Experience, string Effort, string? Evidence);

/// <summary>Definition of one of the six completion reports.</summary>
public sealed record CompletionReportDefinition(string Route, string Title, string DefaultGroupBy, IReadOnlyList<string> GroupByOptions, bool IsSpecification = false);

/// <summary>
/// Reporting → the completion views (Control, Solution, Specification, Compliance, Framework, Experience). One page,
/// configured by the route: customer drop-down (follows the global customer), scope and filters, KPIs, a bar chart, the
/// groups grid with drill-down to the controls, and export to .csv, .html, .json and .txt. Reads persisted results.
/// </summary>
public sealed partial class CompletionReportViewModel : CustomerScopedPageViewModel
{
    public const string SpecificationGroup = "Specification";

    private static readonly IReadOnlyList<CompletionReportDefinition> Definitions =
    [
        new(Routes.ReportingControlCompletion, "Control Completion View", ReportDimensions.Severity,
            [ReportDimensions.Severity, ReportDimensions.Status, ReportDimensions.Category, ReportDimensions.Kind]),
        new(Routes.ReportingSolutionCompletion, "Solution Completion View", ReportDimensions.SolutionArea, [ReportDimensions.SolutionArea, ReportDimensions.Kind]),
        new(Routes.ReportingSpecificationCompletion, "Specification Completion View", SpecificationGroup, [SpecificationGroup], IsSpecification: true),
        new(Routes.ReportingComplianceCompletion, "Compliance Completion View", ReportDimensions.ComplianceStandard, [ReportDimensions.ComplianceStandard]),
        new(Routes.ReportingFrameworkCompletion, "Framework Completion View", ReportDimensions.Framework, [ReportDimensions.Framework]),
        new(Routes.ReportingExperienceCompletion, "Experience Completion View", ReportDimensions.Experience, [ReportDimensions.Experience]),
    ];

    private readonly IReportingService _reporting;
    private readonly ICatalogService _catalog;
    private readonly ISpecificationRepository _specifications;
    private readonly ExportCoordinator _export;
    private CompletionReportDefinition _definition = Definitions[0];
    private CustomerResults? _results;
    private IReadOnlyList<ResultFact> _filtered = [];
    private readonly Dictionary<string, IReadOnlyList<ResultFact>> _groupFacts = new(StringComparer.OrdinalIgnoreCase);

    public CompletionReportViewModel(ICustomerContextService context, ICustomerProfileStore store, IReportingService reporting, ICatalogService catalog,
        ISpecificationRepository specifications, ExportCoordinator export) : base(context, store)
    {
        _reporting = reporting;
        _catalog = catalog;
        _specifications = specifications;
        _export = export;
        SeverityFilter.Changed += (_, _) => Recompute();
        SolutionFilter.Changed += (_, _) => Recompute();
        StatusFilter.Changed += (_, _) => Recompute();
        ControlsView = CollectionViewSource.GetDefaultView(Controls);
        ControlsView.Filter = item => item is FactRow row && MatchesSearch(row);
    }

    public ObservableCollection<KpiTile> Kpis { get; } = [];
    public ObservableCollection<ChartBar> Bars { get; } = [];
    public ObservableCollection<CompletionRow> Groups { get; } = [];
    public ObservableCollection<FactRow> Controls { get; } = [];
    public ICollectionView ControlsView { get; }
    public ObservableCollection<string> GroupByOptions { get; } = [];
    public IReadOnlyList<Choice<ReportScope>> Scopes { get; } =
    [
        new(ReportScope.ActiveBaselines, "Active Baselines"), new(ReportScope.AssignedSpecification, "Assigned Specification"), new(ReportScope.AllEvaluated, "All Evaluated Controls")
    ];

    public MultiSelectFilter SeverityFilter { get; } = new("Severity", options: new[] { "Critical", "High", "Medium", "Low" }.Select(value => (value, value)));
    public MultiSelectFilter SolutionFilter { get; } = new("Solution Area");
    public MultiSelectFilter StatusFilter { get; } = new("Status", options: FactStatus.All.Select(value => (value, value)));

    [ObservableProperty] private string _reportTitle = string.Empty;
    [ObservableProperty] private string _scopeText = string.Empty;
    [ObservableProperty] private string? _groupBy;
    [ObservableProperty] private Choice<ReportScope>? _scope;
    [ObservableProperty] private CompletionRow? _selectedGroup;
    [ObservableProperty] private string _controlSearch = string.Empty;
    [ObservableProperty] private string _controlsCaption = "Select a group to see its controls.";
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private bool _showScope = true;

    partial void OnGroupByChanged(string? value)
    {
        if (!_initializing) Recompute();
    }
    private bool _initializing;

    partial void OnScopeChanged(Choice<ReportScope>? value)
    {
        if (!_initializing) _ = ReloadAsync();
    }
    partial void OnControlSearchChanged(string value) => ControlsView.Refresh();
    partial void OnSelectedGroupChanged(CompletionRow? value) => ShowControls(value);

    public override async Task OnNavigatedToAsync(RouteDescriptor route, CancellationToken cancellationToken)
    {
        _definition = Definitions.FirstOrDefault(definition => definition.Route == route.Route) ?? Definitions[0];
        ReportTitle = _definition.Title;
        ShowScope = !_definition.IsSpecification;
        _initializing = true;
        GroupByOptions.Clear();
        foreach (var option in _definition.GroupByOptions) GroupByOptions.Add(option);
        GroupBy = _definition.DefaultGroupBy;
        _initializing = false;
        SolutionFilter.SetOptions(_catalog.Catalog.SolutionAreas.Select(area => (area.Name, area.Name)), []);
        _initializing = true;
        Scope = Scopes[0];
        _initializing = false;
        await base.OnNavigatedToAsync(route, cancellationToken);
    }

    protected override Task OnCustomerChangedAsync(CustomerProfile? customer) => ReloadAsync();

    [RelayCommand]
    private Task RefreshAsync() => ReloadAsync();

    [RelayCommand]
    private void ClearFilters()
    {
        SeverityFilter.Reset();
        SolutionFilter.Reset();
        StatusFilter.Reset();
        ControlSearch = string.Empty;
    }

    private async Task ReloadAsync()
    {
        Kpis.Clear();
        Bars.Clear();
        Groups.Clear();
        Controls.Clear();
        _results = null;
        HasResults = false;
        if (SelectedCustomer is not { } customer)
        {
            StatusMessage = "Choose a customer.";
            return;
        }

        if (!_reporting.IsAvailable)
        {
            StatusMessage = "The application database is not ready, so stored assessment results are unavailable. Open Settings > Database Configuration.";
            return;
        }

        IsBusy = true;
        try
        {
            // The specification report scores every evaluated control and groups them per specification.
            var scope = _definition.IsSpecification ? ReportScope.AllEvaluated : Scope?.Value ?? ReportScope.ActiveBaselines;
            _results = await _reporting.GetCustomerResultsAsync(customer.Id.Value, scope);
            HasResults = _results.HasResults;
            ScopeText = _results.HasResults
                ? $"{_results.ScopeDescription}. Assessment '{_results.Latest!.Name}' of {(_results.Latest.CompletedAtUtc ?? _results.Latest.RequestedAtUtc).ToLocalTime():yyyy-MM-dd HH:mm} ({_results.Latest.StatusCode})."
                : _results.ScopeDescription;
            StatusMessage = _results.HasResults ? null : $"{customer.Name} has no stored assessment with results. Run one on Assessment Scan or Live Assessment.";
            await RecomputeAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Recompute() => _ = RecomputeAsync();

    private async Task RecomputeAsync()
    {
        Kpis.Clear();
        Bars.Clear();
        Groups.Clear();
        Controls.Clear();
        _groupFacts.Clear();
        if (_results is not { HasResults: true } results) return;
        var catalog = _catalog.Catalog;
        _filtered = results.Facts.Where(fact => SeverityFilter.Matches(fact.Control.Severity) && SolutionFilter.Matches(catalog.SolutionAreaName(fact.Control.SolutionArea)) &&
                                                StatusFilter.Matches(fact.Status)).ToArray();
        IReadOnlyList<CompletionRow> rows;
        if (_definition.IsSpecification)
        {
            var specs = _specifications.IsAvailable ? await _specifications.ListSpecificationsAsync() : [];
            var list = new List<CompletionRow>();
            foreach (var spec in specs)
            {
                var ids = ProfileResolver.ControlIds(catalog, spec.Items);
                var facts = _filtered.Where(fact => ids.Contains(fact.ControlId)).ToArray();
                var label = spec.AssignedCustomers.Contains(results.CustomerKey, StringComparer.OrdinalIgnoreCase) ? $"{spec.Name} (assigned)" : spec.Name;
                list.Add(Completion.Summarize(spec.SpecificationKey, label, facts));
                _groupFacts[spec.SpecificationKey] = facts;
            }

            rows = list.OrderByDescending(row => row.Label.EndsWith("(assigned)", StringComparison.Ordinal)).ThenBy(row => row.Label, StringComparer.CurrentCulture).ToArray();
        }
        else
        {
            var dimension = ReportDimensions.Get(GroupBy ?? _definition.DefaultGroupBy, catalog);
            rows = Completion.By(_filtered, dimension);
            foreach (var row in rows)
                _groupFacts[row.Key] = _filtered.Where(fact => dimension.Keys(fact).DefaultIfEmpty("Unspecified").Contains(row.Key, StringComparer.OrdinalIgnoreCase)).ToArray();
        }

        foreach (var row in rows) Groups.Add(row);
        foreach (var row in rows.Take(30)) Bars.Add(new ChartBar(row.Label, row.Percent ?? 0, $"{row.PercentText} ({row.Satisfied + row.Excepted} of {row.Scored})", ChartBar.FillFor(row.Percent)));

        var overall = Completion.Summarize("all", "all", _filtered);
        Kpis.Add(new KpiTile("Completion", overall.PercentText, $"{overall.Satisfied + overall.Excepted:N0} of {overall.Scored:N0} scored controls satisfied"));
        Kpis.Add(new KpiTile("Controls in Scope", overall.Controls.ToString("N0", CultureInfo.CurrentCulture), $"{overall.NotEvaluated:N0} not evaluated"));
        Kpis.Add(new KpiTile("Gaps", overall.Gaps.ToString("N0", CultureInfo.CurrentCulture), $"{overall.NotSatisfied:N0} not satisfied, {overall.Partial:N0} partial"));
        Kpis.Add(new KpiTile("Exceptions", overall.Excepted.ToString("N0", CultureInfo.CurrentCulture), "Approved exceptions count as satisfied"));
        Kpis.Add(new KpiTile(_definition.IsSpecification ? "Specifications" : $"{GroupBy} Groups", rows.Count.ToString("N0", CultureInfo.CurrentCulture),
            rows.Count == 0 ? "None" : $"Lowest: {rows.Where(row => row.Percent is not null).OrderBy(row => row.Percent).Select(row => $"{row.Label} {row.PercentText}").FirstOrDefault() ?? "–"}"));
        if (results.Previous is { } previous)
        {
            var previousResults = await _reporting.GetCustomerResultsAsync(results.CustomerKey, results.Scope, null, previous.CompletedAtUtc ?? previous.RequestedAtUtc);
            if (previousResults.Overall.Percent is { } before && overall.Percent is { } now)
                Kpis.Add(new KpiTile("Trend", $"{(now - before >= 0 ? "+" : string.Empty)}{now - before:0.#} pts", $"Since {(previous.CompletedAtUtc ?? previous.RequestedAtUtc).ToLocalTime():yyyy-MM-dd}"));
        }

        SelectedGroup = Groups.FirstOrDefault();
    }

    private void ShowControls(CompletionRow? group)
    {
        Controls.Clear();
        if (group is null || !_groupFacts.TryGetValue(group.Key, out var facts))
        {
            ControlsCaption = "Select a group to see its controls.";
            return;
        }

        var catalog = _catalog.Catalog;
        foreach (var fact in facts.OrderBy(fact => fact.Credit).ThenBy(fact => ReportDimensions.SeverityRank(fact.Control.Severity)).ThenBy(fact => fact.ControlId, StringComparer.Ordinal))
            Controls.Add(new FactRow(fact.ControlId, fact.Control.Name, fact.Status, fact.Control.Severity, fact.Control.KindDisplayName, catalog.SolutionAreaName(fact.Control.SolutionArea),
                catalog.ExperienceAreaName(fact.Control.ExperienceArea), fact.Control.Implementation.Effort, fact.Evidence));
        ControlsCaption = $"{group.Label}: {group.Controls:N0} control(s), {group.PercentText} complete.";
    }

    private bool MatchesSearch(FactRow row)
    {
        if (string.IsNullOrWhiteSpace(ControlSearch)) return true;
        var term = ControlSearch.Trim();
        return row.Id.Contains(term, StringComparison.OrdinalIgnoreCase) || row.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
               row.Status.Contains(term, StringComparison.CurrentCultureIgnoreCase) || (row.Evidence?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false);
    }

    [RelayCommand]
    private async Task ExportAsync(object? format)
    {
        if (_results is not { HasResults: true } results)
        {
            StatusMessage = "There is nothing to export yet.";
            return;
        }

        var groupHeader = _definition.IsSpecification ? SpecificationGroup : GroupBy ?? "Group";
        var document = new ExportDocument(ReportTitle, results.CustomerName, DateTimeOffset.UtcNow,
            Kpis.Select(kpi => new KeyValuePair<string, string>(kpi.Title, $"{kpi.Value} – {kpi.Detail}")).Prepend(new("Scope", ScopeText)).ToArray(),
            [
                new ExportTable($"Completion by {groupHeader}", [groupHeader, "Controls", "Satisfied", "Partial", "Not Satisfied", "Exceptions", "Not Evaluated", "Completion %"],
                    Groups.Select(row => (IReadOnlyList<string?>)new string?[]
                    {
                        row.Label, row.Controls.ToString(CultureInfo.InvariantCulture), row.Satisfied.ToString(CultureInfo.InvariantCulture),
                        row.Partial.ToString(CultureInfo.InvariantCulture), row.NotSatisfied.ToString(CultureInfo.InvariantCulture), row.Excepted.ToString(CultureInfo.InvariantCulture),
                        row.NotEvaluated.ToString(CultureInfo.InvariantCulture), row.PercentText
                    }).ToArray()),
                new ExportTable("Controls", ["ID", "Control", "Status", "Severity", "Type", "Solution Area", "Experience", "Effort", "Evidence"],
                    _filtered.Select(fact => (IReadOnlyList<string?>)new string?[]
                    {
                        fact.ControlId, fact.Control.Name, fact.Status, fact.Control.Severity, fact.Control.KindDisplayName, _catalog.Catalog.SolutionAreaName(fact.Control.SolutionArea),
                        _catalog.Catalog.ExperienceAreaName(fact.Control.ExperienceArea), fact.Control.Implementation.Effort, fact.Evidence
                    }).ToArray())
            ]);
        try
        {
            StatusMessage = await _export.ExportAsync(document, ExportCoordinator.Parse(format)) ?? StatusMessage;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"The export could not be written: {exception.Message}";
        }
    }
}
