using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Export;
using EngineeringWorkspace.Application.Governance;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Desktop.Controls;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Desktop.Services;
using EngineeringWorkspace.Domain.Common;

namespace EngineeringWorkspace.Desktop.Pages.Deployment;

public sealed record TrackerRow(ChangeRequestRow Request, int Executions, string LastExecution)
{
    public int Id => Request.Id;
    public string Customer => Request.CustomerName;
    public string Title => Request.Title;
    public string Status => Request.Status;
    public string Risk => Request.Risk;
    public string Owner => Request.Owner;
    public string Window => Request.Window;
    public string Created => Request.Record.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
    public string Modified => Request.Modified;
}

/// <summary>
/// Deployment → Tracker: every change request and its executions across customers, with search and Customer, Status
/// and Risk filters, created/window dates, owner, and Open to go back to the request (or to execute it).
/// </summary>
public sealed partial class TrackerViewModel : PageViewModel
{
    private readonly IChangeRequestRepository _repository;
    private readonly ICustomerProfileStore _profiles;
    private readonly ICustomerContextService _context;
    private readonly INavigationService _navigation;
    private readonly DeploymentSelection _selection;
    private readonly ExportCoordinator _export;

    public TrackerViewModel(IChangeRequestRepository repository, ICustomerProfileStore profiles, ICustomerContextService context, INavigationService navigation,
        DeploymentSelection selection, ExportCoordinator export)
    {
        _repository = repository;
        _profiles = profiles;
        _context = context;
        _navigation = navigation;
        _selection = selection;
        _export = export;
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = item => item is TrackerRow row && Matches(row);
        foreach (var filter in new[] { CustomerFilter, StatusFilter, RiskFilter }) filter.Changed += (_, _) => RefreshView();
    }

    public ObservableCollection<TrackerRow> Rows { get; } = [];
    public ICollectionView View { get; }
    public ObservableCollection<Reporting.KpiTile> Kpis { get; } = [];
    public MultiSelectFilter CustomerFilter { get; } = new("Customer");
    public MultiSelectFilter StatusFilter { get; } = new("Status", options: ChangeStatusCodes.All.Select(value => (value, value)));
    public MultiSelectFilter RiskFilter { get; } = new("Risk", options: ChangeRisk.All.Select(value => (value, value)));

    [ObservableProperty] private string _search = string.Empty;
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
        Rows.Clear();
        Kpis.Clear();
        if (!_repository.IsAvailable)
        {
            StatusMessage = "The application database is not ready; change requests are stored in it.";
            return;
        }

        var names = (await _profiles.ListAsync()).ToDictionary(profile => profile.Id.Value, profile => profile.Name, StringComparer.OrdinalIgnoreCase);
        var executions = (await _repository.ListExecutionsAsync(null)).GroupBy(execution => execution.ChangeRequestId).ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var record in await _repository.ListChangeRequestsAsync(null))
        {
            var runs = executions.GetValueOrDefault(record.ChangeRequestId) ?? [];
            var last = runs.OrderByDescending(run => run.StartedAtUtc).FirstOrDefault();
            Rows.Add(new TrackerRow(new ChangeRequestRow(record, names.GetValueOrDefault(record.CustomerKey, record.CustomerKey)), runs.Length,
                last is null ? "Never" : $"{(last.ModeCode is ExecutionModes.Execute or ExecutionModes.Apply ? "Applied" : "Simulated")} {last.StartedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} ({last.StatusCode})"));
        }

        CustomerFilter.SetOptions(Rows.Select(row => (row.Request.Record.CustomerKey, row.Customer)).Distinct().OrderBy(option => option.Customer));
        foreach (var status in ChangeStatusCodes.All)
            Kpis.Add(new Reporting.KpiTile(status, Rows.Count(row => row.Status == status).ToString(CultureInfo.CurrentCulture), string.Empty));
        StatusMessage = Rows.Count == 0 ? "There are no change requests yet. Create one on Change Request." : null;
        RefreshView();
    }

    private bool Matches(TrackerRow row)
    {
        if (!CustomerFilter.Matches(row.Request.Record.CustomerKey) || !StatusFilter.Matches(row.Status) || !RiskFilter.Matches(row.Risk)) return false;
        if (string.IsNullOrWhiteSpace(Search)) return true;
        var term = Search.Trim();
        return row.Title.Contains(term, StringComparison.CurrentCultureIgnoreCase) || row.Customer.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
               row.Owner.Contains(term, StringComparison.CurrentCultureIgnoreCase) || row.Id.ToString(CultureInfo.InvariantCulture) == term.TrimStart('#');
    }

    private void RefreshView()
    {
        View.Refresh();
        CountText = $"{View.Cast<object>().Count()} of {Rows.Count} change request(s)";
    }

    [RelayCommand]
    private Task OpenAsync(TrackerRow? row) => GoAsync(row, Routes.DeploymentChangeRequest);

    [RelayCommand]
    private Task ExecuteAsync(TrackerRow? row) => GoAsync(row, Routes.DeploymentChangeExecution);

    private async Task GoAsync(TrackerRow? row, string route)
    {
        if (row is null || !CustomerId.TryCreate(row.Request.Record.CustomerKey, out var id)) return;
        _selection.ChangeRequestId = row.Id;
        if (_context.CurrentCustomer?.Id != id) await _context.SelectAsync(id, null, "Opened from the Tracker");
        await _navigation.NavigateAsync(route);
    }

    [RelayCommand]
    private async Task ExportAsync(object? format)
    {
        var rows = View.Cast<TrackerRow>().ToArray();
        var document = new ExportDocument("Change Tracker", null, DateTimeOffset.UtcNow, Kpis.Select(kpi => new KeyValuePair<string, string>(kpi.Title, kpi.Value)).ToArray(),
            [new ExportTable("Change Requests", ["ID", "Customer", "Title", "Status", "Risk", "Owner", "Window", "Created", "Modified", "Executions", "Last Execution"],
                rows.Select(row => (IReadOnlyList<string?>)new string?[]
                {
                    row.Id.ToString(CultureInfo.InvariantCulture), row.Customer, row.Title, row.Status, row.Risk, row.Owner, row.Window, row.Created, row.Modified,
                    row.Executions.ToString(CultureInfo.InvariantCulture), row.LastExecution
                }).ToArray())]);
        StatusMessage = await _export.ExportAsync(document, ExportCoordinator.Parse(format)) ?? StatusMessage;
    }
}
