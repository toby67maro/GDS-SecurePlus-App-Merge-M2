using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Export;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Application.Reporting;
using EngineeringWorkspace.Desktop.Controls;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Desktop.Pages.Reporting;
using EngineeringWorkspace.Desktop.Services;
using EngineeringWorkspace.Domain.Common;

namespace EngineeringWorkspace.Desktop.Pages.Insights;

/// <summary>A fleet row as shown in the grid.</summary>
public sealed record FleetRowView(FleetRow Row)
{
    public string Customer => Row.CustomerName;
    public string Tenant => Row.Tenant;
    public string Connection => Row.IsConnected ? "Connected" : "Not connected";
    public string Industry => Row.Industry ?? "Not set";
    public string LatestAssessment => Row.LatestAssessmentUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? "Never";
    public string LatestStatus => Row.LatestStatus ?? "–";
    public string Compliance => Row.CompliancePercent is { } value ? $"{value:0.#}%" : "–";
    public double? CompliancePercent => Row.CompliancePercent;
    public string Trend => Row.Trend is { } trend ? trend switch { > 0 => $"▲ +{trend:0.#}", < 0 => $"▼ {trend:0.#}", _ => "■ 0" } : "–";
    public double? TrendValue => Row.Trend;
    public int Critical => Row.Critical;
    public int High => Row.High;
    public int Medium => Row.Medium;
    public int Low => Row.Low;
    public string Schedule => Row.EnabledSchedules == 0
        ? Row.ScheduleStatus
        : $"{Row.ScheduleStatus}; next {Row.NextScheduledRunUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? "–"}";
}

/// <summary>
/// Dashboard → Fleet View: every customer tenant with its latest stored assessment, compliance against the active
/// baselines, failing controls by severity, trend versus the previous assessment, schedule status and quick actions.
/// </summary>
public sealed partial class FleetViewModel : PageViewModel
{
    private readonly IReportingService _reporting;
    private readonly ICustomerContextService _customers;
    private readonly INavigationService _navigation;
    private readonly IAssessmentService _assessments;
    private readonly ExportCoordinator _export;

    public FleetViewModel(IReportingService reporting, ICustomerContextService customers, INavigationService navigation, IAssessmentService assessments, ExportCoordinator export)
    {
        _reporting = reporting;
        _customers = customers;
        _navigation = navigation;
        _assessments = assessments;
        _export = export;
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = item => item is FleetRowView row && Matches(row);
        ConnectionFilter.Changed += (_, _) => RefreshView();
        ScheduleFilter.Changed += (_, _) => RefreshView();
        HealthFilter.Changed += (_, _) => RefreshView();
        IndustryFilter.Changed += (_, _) => RefreshView();
    }

    public ObservableCollection<FleetRowView> Rows { get; } = [];
    public ICollectionView View { get; }
    public ObservableCollection<KpiTile> Kpis { get; } = [];
    public ObservableCollection<ChartSlice> ComplianceSlices { get; } = [];
    public ObservableCollection<ChartSlice> FailingSlices { get; } = [];
    public ObservableCollection<StackedRow> SeverityRows { get; } = [];

    [ObservableProperty] private string _chartDetail = "Click a chart for details.";

    [RelayCommand]
    private void ShowChartDetail(object? item)
    {
        if (item is ChartSlice slice) ChartDetail = $"{slice.Label}: {slice.Value:0.#}. {slice.Detail}";
    }

    private void BuildCharts()
    {
        ComplianceSlices.Clear();
        FailingSlices.Clear();
        SeverityRows.Clear();
        foreach (var row in Rows.Select(view => view.Row).Where(row => row.CompliancePercent is not null).OrderBy(row => row.CompliancePercent))
            ComplianceSlices.Add(new ChartSlice(row.CustomerName, Math.Round(row.CompliancePercent!.Value, 1), null,
                $"Latest {row.LatestAssessmentUtc?.ToLocalTime():d}; trend {(row.Trend is { } trend ? $"{trend:+0.#;-0.#;0} pts" : "n/a")}"));
        var rows = Rows.Select(view => view.Row).ToArray();
        string[] colors = ["#C42B1C", "#E06C00", "#C19C00", "#107C10"];
        string[] names = ["Critical", "High", "Medium", "Low"];
        for (var index = 0; index < 4; index++)
        {
            var count = rows.Sum(row => index switch { 0 => row.Critical, 1 => row.High, 2 => row.Medium, _ => row.Low });
            if (count > 0)
                FailingSlices.Add(new ChartSlice(names[index], count, colors[index], $"{count} failing control(s) across the fleet", null,
                    rows.Select(row => new ChartSlice(row.CustomerName, index switch { 0 => row.Critical, 1 => row.High, 2 => row.Medium, _ => row.Low }))
                        .Where(slice => slice.Value > 0).OrderByDescending(slice => slice.Value).ToArray()));
        }

        foreach (var row in rows.Where(row => row.FailingTotal > 0).OrderByDescending(row => row.FailingTotal).Take(25))
            SeverityRows.Add(new StackedRow(row.CustomerName, Enumerable.Range(0, 4).Select(index => new ChartSlice(names[index],
                index switch { 0 => row.Critical, 1 => row.High, 2 => row.Medium, _ => row.Low }, colors[index])).ToArray()));
    }
    public MultiSelectFilter ConnectionFilter { get; } = new("Tenant", options: [("Connected", "Connected"), ("Not connected", "Not connected")]);
    public MultiSelectFilter ScheduleFilter { get; } = new("Schedule",
        options: new[] { "Scheduled", "Running", "Last run failed", "Off", "Not scheduled" }.Select(value => (value, value)));
    public MultiSelectFilter IndustryFilter { get; } = new("Industry");
    public MultiSelectFilter HealthFilter { get; } = new("Compliance",
        options: [("80", "80% or more"), ("50", "50–79%"), ("0", "Below 50%"), ("none", "Not assessed")]);

    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private FleetRowView? _selectedRow;
    [ObservableProperty] private string _countText = string.Empty;

    partial void OnSearchChanged(string value) => RefreshView();

    public override async Task OnNavigatedToAsync(RouteDescriptor route, CancellationToken cancellationToken)
    {
        await base.OnNavigatedToAsync(route, cancellationToken);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var rows = await _reporting.GetFleetAsync();
            Rows.Clear();
            foreach (var row in rows) Rows.Add(new FleetRowView(row));
            IndustryFilter.SetOptions(Rows.Select(row => row.Industry).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).Select(value => (value, value)));
            BuildCharts();
            Kpis.Clear();
            var assessed = rows.Where(row => row.CompliancePercent is not null).ToArray();
            Kpis.Add(new KpiTile("Customers", rows.Count.ToString("N0", CultureInfo.CurrentCulture), $"{rows.Count(row => row.IsConnected)} with a connected tenant"));
            Kpis.Add(new KpiTile("Average Compliance", assessed.Length == 0 ? "–" : $"{assessed.Average(row => row.CompliancePercent!.Value):0.#}%", $"{assessed.Length} assessed"));
            Kpis.Add(new KpiTile("Critical Gaps", rows.Sum(row => row.Critical).ToString("N0", CultureInfo.CurrentCulture), $"{rows.Count(row => row.Critical > 0)} customer(s) affected"));
            Kpis.Add(new KpiTile("High Gaps", rows.Sum(row => row.High).ToString("N0", CultureInfo.CurrentCulture), "Failing high-severity controls"));
            Kpis.Add(new KpiTile("Scheduled", rows.Count(row => row.EnabledSchedules > 0).ToString("N0", CultureInfo.CurrentCulture),
                $"{rows.Count(row => row.ScheduleStatus == "Last run failed")} with a failed last run"));
            StatusMessage = _reporting.IsAvailable ? null : "The application database is not ready: assessment figures are unavailable until it is.";
            RefreshView();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OpenCustomerAsync(FleetRowView? row)
    {
        if (row is null || !CustomerId.TryCreate(row.Row.CustomerKey, out var id)) return;
        var result = await _customers.SelectAsync(id, null, "Opened from Fleet View");
        if (!result.Succeeded)
        {
            StatusMessage = result.Error!.ToString();
            return;
        }

        await _navigation.NavigateAsync(Routes.DashboardOverview);
    }

    [RelayCommand]
    private void RunAssessment(FleetRowView? row)
    {
        if (row is null || !CustomerId.TryCreate(row.Row.CustomerKey, out var id)) return;
        if (!row.Row.IsConnected)
        {
            StatusMessage = $"{row.Customer} has no connected tenant. Register the app on Tenant App Registration first.";
            return;
        }

        _assessments.Queue(id, null, "Started from Fleet View");
        StatusMessage = $"The assessment of {row.Customer} has started; follow it in the activity panel and select Refresh when it finishes.";
    }

    private bool Matches(FleetRowView row)
    {
        if (!ConnectionFilter.Matches(row.Connection) || !ScheduleFilter.Matches(row.Row.ScheduleStatus)) return false;
        var band = row.CompliancePercent switch { null => "none", >= 80 => "80", >= 50 => "50", _ => "0" };
        if (!HealthFilter.Matches(band) || !IndustryFilter.Matches(row.Industry)) return false;
        if (string.IsNullOrWhiteSpace(Search)) return true;
        var term = Search.Trim();
        return row.Customer.Contains(term, StringComparison.CurrentCultureIgnoreCase) || row.Tenant.Contains(term, StringComparison.CurrentCultureIgnoreCase);
    }

    private void RefreshView()
    {
        View.Refresh();
        CountText = $"{View.Cast<object>().Count()} of {Rows.Count} customer(s)";
    }

    [RelayCommand]
    private async Task ExportAsync(object? format)
    {
        var rows = View.Cast<FleetRowView>().ToArray();
        var document = new ExportDocument("Fleet View", null, DateTimeOffset.UtcNow,
            Kpis.Select(kpi => new KeyValuePair<string, string>(kpi.Title, $"{kpi.Value} – {kpi.Detail}")).ToArray(),
            [new ExportTable("Customers", ["Customer", "Tenant", "Connection", "Latest Assessment", "Status", "Compliance", "Trend", "Critical", "High", "Medium", "Low", "Schedule"],
                rows.Select(row => (IReadOnlyList<string?>)new string?[]
                {
                    row.Customer, row.Tenant, row.Connection, row.LatestAssessment, row.LatestStatus, row.Compliance, row.Trend,
                    row.Critical.ToString(CultureInfo.InvariantCulture), row.High.ToString(CultureInfo.InvariantCulture), row.Medium.ToString(CultureInfo.InvariantCulture),
                    row.Low.ToString(CultureInfo.InvariantCulture), row.Schedule
                }).ToArray())]);
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
