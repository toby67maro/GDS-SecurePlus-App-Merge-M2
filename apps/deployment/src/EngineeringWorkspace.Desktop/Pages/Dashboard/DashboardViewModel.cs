using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Application.Tenants;
using EngineeringWorkspace.Application.Workspaces;
using EngineeringWorkspace.Desktop.Pages.Assessments;

namespace EngineeringWorkspace.Desktop.Pages.Dashboard;

/// <summary>Dashboard → Overview: current context, database status, counts and the latest assessments across customers.</summary>
public sealed partial class DashboardViewModel(
    IEngagementService engagements,
    ICustomerContextService customers,
    IWorkspaceService workspaces,
    IWorkspaceStore workspaceStore,
    ITenantRegistrationService customerProfiles,
    IDatabaseStatusService database,
    Application.Settings.ApplicationSettings application,
    Application.Reporting.IReportingService reporting,
    Application.Catalog.ICatalogService catalog) : AssessmentPageViewModel(engagements)
{
    public System.Collections.ObjectModel.ObservableCollection<Controls.ChartSlice> StatusSlices { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<Controls.ChartSlice> SeveritySlices { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<Controls.ChartSlice> AreaSlices { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<Controls.ChartSlice> ScoreTrend { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<Controls.ChartSlice> FleetSlices { get; } = [];

    [ObservableProperty] private double _compliance;
    [ObservableProperty] private string _complianceCaption = "No assessment yet";
    [ObservableProperty] private string _chartDetail = "Click any chart for details; slices with › break down further.";
    [ObservableProperty] private bool _hasCharts;

    [RelayCommand]
    private void ShowChartDetail(object? item)
    {
        if (item is Controls.ChartSlice slice) ChartDetail = $"{slice.Label}: {slice.Value:0.#}. {slice.Detail}";
    }

    private async Task LoadChartsAsync(CancellationToken cancellationToken)
    {
        StatusSlices.Clear();
        SeveritySlices.Clear();
        AreaSlices.Clear();
        ScoreTrend.Clear();
        FleetSlices.Clear();
        HasCharts = false;
        if (!reporting.IsAvailable) return;
        try
        {
            foreach (var row in (await reporting.GetFleetAsync(cancellationToken)).Where(row => row.CompliancePercent is not null).OrderByDescending(row => row.CompliancePercent).Take(20))
                FleetSlices.Add(new Controls.ChartSlice(row.CustomerName, Math.Round(row.CompliancePercent!.Value, 1), null,
                    $"{row.FailingTotal} failing ({row.Critical} critical, {row.High} high); latest {row.LatestAssessmentUtc?.ToLocalTime():d}"));
            foreach (var assessment in Assessments.Where(assessment => assessment.Score is not null && (customers.CurrentCustomer is null || assessment.CustomerKey == customers.CurrentCustomer.Id.Value))
                         .OrderBy(assessment => assessment.RequestedAtUtc).TakeLast(12))
                ScoreTrend.Add(new Controls.ChartSlice(assessment.RequestedAtUtc.ToLocalTime().ToString("d MMM", System.Globalization.CultureInfo.CurrentCulture), (double)assessment.Score!.Value, null,
                    $"{assessment.Name} ({assessment.CustomerName}): {assessment.Score:0.#}%"));
            if (customers.CurrentCustomer is not { } customer)
            {
                ComplianceCaption = "Select a customer at the top";
                HasCharts = FleetSlices.Count > 0;
                return;
            }

            var results = await reporting.GetCustomerResultsAsync(customer.Id.Value, Application.Reporting.ReportScope.ActiveBaselines, null, null, cancellationToken);
            if (!results.HasResults)
            {
                ComplianceCaption = $"{customer.Name}: no assessment yet";
                HasCharts = FleetSlices.Count > 0;
                return;
            }

            var overall = results.Overall;
            Compliance = overall.Percent ?? 0;
            ComplianceCaption = $"{customer.Name}: {overall.Controls} controls, {overall.Gaps} gap(s)";
            var current = catalog.Catalog;
            Controls.ChartSlice[] ByArea(IEnumerable<Application.Reporting.ResultFact> facts) =>
                facts.GroupBy(fact => current.SolutionAreaName(fact.Control.SolutionArea)).OrderByDescending(group => group.Count())
                    .Select(group => new Controls.ChartSlice(group.Key, group.Count(), null, string.Join(", ", group.Take(10).Select(fact => fact.ControlId)))).ToArray();
            string? Color(string status) => status switch
            {
                Application.Reporting.FactStatus.Satisfied => "#107C10", Application.Reporting.FactStatus.Partial => "#C19C00",
                Application.Reporting.FactStatus.Excepted => "#8764B8", Application.Reporting.FactStatus.NotEvaluated => "#7F7F7F", _ => "#C42B1C"
            };
            foreach (var status in results.Facts.GroupBy(fact => fact.Status).OrderByDescending(group => group.Count()))
                StatusSlices.Add(new Controls.ChartSlice(status.Key, status.Count(), Color(status.Key), $"{status.Count()} control(s)", null, ByArea(status)));
            foreach (var severity in results.Facts.Where(fact => fact.IsGap).GroupBy(fact => fact.Control.Severity)
                         .OrderBy(group => Application.Reporting.ReportDimensions.SeverityRank(group.Key)))
                SeveritySlices.Add(new Controls.ChartSlice(severity.Key, severity.Count(), severity.Key switch
                {
                    "Critical" => "#C42B1C", "High" => "#E06C00", "Medium" => "#C19C00", "Low" => "#107C10", _ => "#5B6B7F"
                }, $"{severity.Count()} gap(s)", null, ByArea(severity)));
            foreach (var area in results.Facts.GroupBy(fact => current.SolutionAreaName(fact.Control.SolutionArea)))
            {
                var completion = Application.Reporting.Completion.Summarize(area.Key, area.Key, area);
                AreaSlices.Add(new Controls.ChartSlice(area.Key, completion.Percent ?? 0, null, $"{completion.Controls} control(s), {completion.Gaps} gap(s)", null,
                    area.Where(fact => fact.IsGap).GroupBy(fact => fact.Control.Category).Select(group => new Controls.ChartSlice(group.Key, group.Count(), null,
                        string.Join("\n", group.Take(12).Select(fact => $"{fact.ControlId} {fact.Control.Name}")))).ToArray()));
            }

            var sorted = AreaSlices.OrderBy(slice => slice.Value).ToArray();
            AreaSlices.Clear();
            foreach (var slice in sorted) AreaSlices.Add(slice);
            HasCharts = true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
        {
            ChartDetail = $"Charts could not be loaded: {exception.Message}";
        }
    }
    [ObservableProperty] private string _customerName = string.Empty;
    [ObservableProperty] private string _tenantName = string.Empty;
    [ObservableProperty] private string _workspaceName = string.Empty;
    [ObservableProperty] private string _databaseSummary = string.Empty;
    [ObservableProperty] private int _customerCount;
    [ObservableProperty] private int _workspaceCount;
    [ObservableProperty] private int _assessmentCount;
    [ObservableProperty] private string _averageScore = "—";
    [ObservableProperty] private bool _canLoadSampleData;

    public override async Task OnNavigatedToAsync(RouteDescriptor route, CancellationToken cancellationToken)
    {
        await base.OnNavigatedToAsync(route, cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    [RelayCommand]
    private Task RefreshAsync() => RefreshAsync(CancellationToken.None);

    [RelayCommand]
    private async Task LoadSampleDataAsync()
    {
        var result = await Engagements.LoadSampleDataAsync();
        StatusMessage = result.Succeeded ? $"Sample customer '{result.Value!.Customer.Name}' and workspace '{result.Value.Workspace.Name}' loaded." : result.Error!.ToString();
        await RefreshAsync(CancellationToken.None);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        StatusMessage = null;
        CustomerName = customers.CurrentCustomer?.Name ?? "No customer selected";
        var tenant = customers.CurrentTenant;
        TenantName = tenant is null ? "No tenant" : tenant.PrimaryDomain ?? tenant.TenantName ?? tenant.DisplayName;
        WorkspaceName = workspaces.CurrentWorkspace?.Name ?? "No workspace open";
        var report = database.LatestStatus;
        DatabaseSummary = report is null
            ? $"{database.Settings.Provider}: not checked"
            : $"{report.Provider} {report.Server}/{report.Database}: {report.State}, schema version {report.CurrentSchemaVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}";
        CustomerCount = (await customerProfiles.ListCustomersAsync(cancellationToken)).Count;
        WorkspaceCount = (await workspaceStore.ListAsync(cancellationToken)).Count;
        await LoadAssessmentsAsync((IReadOnlyCollection<string>?)null, cancellationToken);
        AssessmentCount = Assessments.Count;
        await LoadChartsAsync(cancellationToken);
        var scored = Assessments.Where(assessment => assessment.Score is not null).ToArray();
        AverageScore = scored.Length == 0 ? "—" : $"{scored.Average(assessment => assessment.Score!.Value):0.#}%";
        // Sample data belongs in Test databases only.
        CanLoadSampleData = application.Environment != Application.Database.DatabaseModes.Production && Assessments.All(assessment => !assessment.IsSample);
        if (IsDatabaseAvailable && Assessments.Count == 0)
            StatusMessage = CanLoadSampleData
                ? "No assessments are recorded yet. Open Assessment > Assessment Scan to request one, or load the sample data."
                : "No assessments are recorded yet. Open Assessment > Assessment Scan to request one.";
    }
}
