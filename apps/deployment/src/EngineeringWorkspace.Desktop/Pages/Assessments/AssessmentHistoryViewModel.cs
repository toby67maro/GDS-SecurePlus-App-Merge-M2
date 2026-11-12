using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Application.Scheduling;
using EngineeringWorkspace.Application.Workspaces;
using EngineeringWorkspace.Desktop.Controls;
using EngineeringWorkspace.Desktop.Services;
using EngineeringWorkspace.Domain.Customers;

namespace EngineeringWorkspace.Desktop.Pages.Assessments;

/// <summary>A customer that can be selected for assessment.</summary>
public sealed partial class CustomerChoice(CustomerProfile profile, string lastAssessment) : ObservableObject
{
    public CustomerProfile Profile { get; } = profile;
    public string Name => Profile.Name;
    public bool IsConnected => Profile.PrimaryTenant?.ApplicationRegistration?.SecretReference is not null;
    public string Tenant => Profile.PrimaryTenant?.PrimaryDomain ?? (IsConnected ? "Connected" : "Not connected");
    public string LastAssessment { get; } = lastAssessment;

    [ObservableProperty] private bool _isSelected;
}

/// <summary>A customer added to the assessments list with "Include Other Customers".</summary>
public sealed record CustomerChip(string Key, string Name);

/// <summary>One row of the schedules list. The State switch saves immediately.</summary>
public sealed partial class ScheduleRowViewModel : ObservableObject
{
    private readonly Func<ScheduleRowViewModel, bool, Task> _setEnabled;
    private bool _loading;

    public ScheduleRowViewModel(AssessmentSchedule schedule, string customer, bool runningHere, Func<ScheduleRowViewModel, bool, Task> setEnabled)
    {
        Schedule = schedule;
        Customer = customer;
        _setEnabled = setEnabled;
        _loading = true;
        IsEnabled = schedule.IsEnabled;
        _loading = false;
        Status = runningHere || schedule.LastStatus == ScheduleStatusCodes.Running ? ScheduleStatusCodes.Running : schedule.LastStatus;
    }

    public AssessmentSchedule Schedule { get; }
    public string Customer { get; }
    public string Name => Schedule.Name;
    public string Frequency => Schedule.Recurrence.Frequency.ToString();
    public string Days => Schedule.Recurrence.DaysText;
    public string Month => Schedule.Recurrence.MonthsText;
    public string Description => Schedule.Describe();
    public DateTimeOffset? NextRunUtc => Schedule.IsEnabled ? Schedule.NextRunUtc : null;
    public DateTimeOffset? LastRunUtc => Schedule.LastRunUtc;
    public string NextRun => NextRunUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? (Schedule.IsEnabled ? "—" : "Off");
    public string LastRun => LastRunUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? "Never";
    public string Status { get; }
    public string StatusDetail => Schedule.LastMessage ?? (Status == ScheduleStatusCodes.Idle ? "Not run yet." : Status);
    public bool IsRunning => Status == ScheduleStatusCodes.Running;

    [ObservableProperty] private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_loading) _ = _setEnabled(this, value);
    }
}

/// <summary>A scan type choice for pickers.</summary>
public sealed record ScanTypeOption(string Code, string Label)
{
    public static IReadOnlyList<ScanTypeOption> All { get; } = [new(ScanTypes.Quick, ScanTypes.Display(ScanTypes.Quick)), new(ScanTypes.InDepth, ScanTypes.Display(ScanTypes.InDepth))];
}

/// <summary>
/// Assessment → Assessment Scan: run assessments now (search for the customer), schedule recurring assessments
/// (frequency-first editor, filterable list with state switch and row actions) and review assessments of the current
/// customer, optionally including other customers.
/// </summary>
public sealed partial class AssessmentHistoryViewModel : AssessmentPageViewModel
{
    private readonly ICustomerContextService _customers;
    private readonly IWorkspaceService _workspaces;
    private readonly ICustomerProfileStore _profiles;
    private readonly IAssessmentService _assessments;
    private readonly IAssessmentScheduler _scheduler;
    private readonly IBackgroundScheduleHost _backgroundHost;
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(15) };
    private IReadOnlyList<CustomerProfile> _allProfiles = [];

    public AssessmentHistoryViewModel(IEngagementService engagements, ICustomerContextService customers, IWorkspaceService workspaces, ICustomerProfileStore profiles,
        IAssessmentService assessments, IAssessmentScheduler scheduler, IBackgroundScheduleHost backgroundHost, IDialogService dialogs) : base(engagements)
    {
        _customers = customers;
        _workspaces = workspaces;
        _profiles = profiles;
        _assessments = assessments;
        _scheduler = scheduler;
        _backgroundHost = backgroundHost;
        _dialogs = dialogs;
        CustomerChoicesView = CollectionViewSource.GetDefaultView(CustomerChoices);
        CustomerChoicesView.Filter = item => item is CustomerChoice choice && MatchesRunSearch(choice);
        SchedulesView = CollectionViewSource.GetDefaultView(Schedules);
        SchedulesView.Filter = item => item is ScheduleRowViewModel row && MatchesScheduleFilters(row);
        CustomerFilter.Changed += (_, _) => SchedulesView.Refresh();
        FrequencyFilter.Changed += (_, _) => SchedulesView.Refresh();
        _poll.Tick += async (_, _) => await LoadSchedulesAsync();
        OtherCustomerOptionsView = CollectionViewSource.GetDefaultView(OtherCustomerOptions);
        OtherCustomerOptionsView.Filter = item => item is CustomerProfile profile &&
                                                  (string.IsNullOrWhiteSpace(OtherCustomerText) || profile.Name.Contains(OtherCustomerText.Trim(), StringComparison.CurrentCultureIgnoreCase));
    }

    public ICollectionView OtherCustomerOptionsView { get; }

    partial void OnOtherCustomerTextChanged(string value)
    {
        OtherCustomerOptionsView.Refresh();
        if (OtherCustomer is null || !OtherCustomerOptionsView.Cast<CustomerProfile>().Contains(OtherCustomer))
            OtherCustomer = OtherCustomerOptionsView.Cast<CustomerProfile>().FirstOrDefault();
    }

    public ObservableCollection<CustomerChoice> CustomerChoices { get; } = [];
    public ICollectionView CustomerChoicesView { get; }
    public ObservableCollection<ScheduleRowViewModel> Schedules { get; } = [];
    public ICollectionView SchedulesView { get; }
    public ScheduleEditorViewModel Editor { get; } = new();
    public MultiSelectFilter CustomerFilter { get; } = new("Customer");
    public MultiSelectFilter FrequencyFilter { get; } = new("Frequency", options: Enum.GetValues<ScheduleFrequency>().Select(value => (value.ToString(), value.ToString())));
    public ObservableCollection<CustomerChip> IncludedCustomers { get; } = [];
    public ObservableCollection<CustomerProfile> OtherCustomerOptions { get; } = [];

    [ObservableProperty] private string _customerName = string.Empty;
    [ObservableProperty] private string _selectedScanType = ScanTypes.InDepth;

    /// <summary>Quick or in-depth scan for Scan Selected Now (4.0 beta.2).</summary>
    public IReadOnlyList<ScanTypeOption> ScanTypeOptions { get; } = ScanTypeOption.All;
    [ObservableProperty] private string _runSearch = string.Empty;
    [ObservableProperty] private string _scheduleSearch = string.Empty;
    [ObservableProperty] private DateTime? _nextRunFrom;
    [ObservableProperty] private DateTime? _nextRunTo;
    [ObservableProperty] private DateTime? _lastRunFrom;
    [ObservableProperty] private DateTime? _lastRunTo;
    [ObservableProperty] private string? _scheduleMessage;
    [ObservableProperty] private string _backgroundHostText = "Checking the background runner...";
    [ObservableProperty] private bool _isBackgroundHostRegistered;
    [ObservableProperty] private bool _includeOtherCustomers;
    [ObservableProperty] private CustomerProfile? _otherCustomer;
    [ObservableProperty] private string _otherCustomerText = string.Empty;
    [ObservableProperty] private string _scheduleCountText = string.Empty;

    partial void OnRunSearchChanged(string value) => CustomerChoicesView.Refresh();
    partial void OnScheduleSearchChanged(string value) => RefreshScheduleView();
    partial void OnNextRunFromChanged(DateTime? value) => RefreshScheduleView();
    partial void OnNextRunToChanged(DateTime? value) => RefreshScheduleView();
    partial void OnLastRunFromChanged(DateTime? value) => RefreshScheduleView();
    partial void OnLastRunToChanged(DateTime? value) => RefreshScheduleView();

    partial void OnIncludeOtherCustomersChanged(bool value)
    {
        if (!value) IncludedCustomers.Clear();
        _ = ReloadAssessmentsAsync(CancellationToken.None);
    }

    public override async Task OnNavigatedToAsync(RouteDescriptor route, CancellationToken cancellationToken)
    {
        await base.OnNavigatedToAsync(route, cancellationToken);
        _customers.CustomerChanged += OnGlobalCustomerChanged;
        _scheduler.StatusChanged += OnScheduleStatusChanged;
        await RefreshAsync(cancellationToken);
        Editor.Reset(_customers.CurrentCustomer?.Id.Value);
        _poll.Start();
        await RefreshBackgroundHostAsync();
    }

    public override void OnNavigatedFrom()
    {
        _poll.Stop();
        _customers.CustomerChanged -= OnGlobalCustomerChanged;
        _scheduler.StatusChanged -= OnScheduleStatusChanged;
        base.OnNavigatedFrom();
    }

    private void OnGlobalCustomerChanged(object? sender, CustomerContextChanged e) => Shell.ShellViewModel.OnUi(() => _ = RefreshAsync(CancellationToken.None));

    private void OnScheduleStatusChanged(object? sender, int scheduleId) => Shell.ShellViewModel.OnUi(() => _ = LoadSchedulesAsync());

    private bool MatchesRunSearch(CustomerChoice choice)
    {
        if (string.IsNullOrWhiteSpace(RunSearch)) return true;
        var term = RunSearch.Trim();
        return choice.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase) || choice.Tenant.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
               choice.LastAssessment.Contains(term, StringComparison.CurrentCultureIgnoreCase);
    }

    private bool MatchesScheduleFilters(ScheduleRowViewModel row)
    {
        if (!CustomerFilter.Matches(row.Schedule.AllCustomers ? ScheduleEditorViewModel.AllCustomersKey : row.Schedule.CustomerKeys.FirstOrDefault())) return false;
        if (!FrequencyFilter.Matches(row.Frequency)) return false;
        if (!InRange(row.NextRunUtc, NextRunFrom, NextRunTo) || !InRange(row.LastRunUtc, LastRunFrom, LastRunTo)) return false;
        if (string.IsNullOrWhiteSpace(ScheduleSearch)) return true;
        var term = ScheduleSearch.Trim();
        return new[] { row.Name, row.Customer, row.Description, row.Status, row.StatusDetail }.Any(text => text.Contains(term, StringComparison.CurrentCultureIgnoreCase));
    }

    private static bool InRange(DateTimeOffset? value, DateTime? from, DateTime? to)
    {
        if (from is null && to is null) return true;
        if (value is null) return false;
        var local = value.Value.ToLocalTime().Date;
        return (from is null || local >= from.Value.Date) && (to is null || local <= to.Value.Date);
    }

    private void RefreshScheduleView()
    {
        SchedulesView.Refresh();
        ScheduleCountText = $"{SchedulesView.Cast<object>().Count()} of {Schedules.Count} schedule(s)";
    }

    [RelayCommand]
    private Task RefreshAsync() => RefreshAsync(CancellationToken.None);

    [RelayCommand]
    private void SelectAll(string? value)
    {
        var select = value != "none";
        foreach (var choice in CustomerChoicesView.Cast<CustomerChoice>().Where(choice => choice.IsConnected || !select)) choice.IsSelected = select;
    }

    /// <summary>Queues an assessment for every selected customer (or the current customer when none is ticked).</summary>
    [RelayCommand]
    private void AssessSelected()
    {
        var selected = CustomerChoices.Where(choice => choice.IsSelected).ToArray();
        if (selected.Length == 0 && _customers.CurrentCustomer is { } current) selected = CustomerChoices.Where(choice => choice.Profile.Id == current.Id).ToArray();
        var notConnected = selected.Where(choice => !choice.IsConnected).Select(choice => choice.Name).ToArray();
        var jobs = selected.Where(choice => choice.IsConnected).Select(choice => _assessments.QueueScan(choice.Profile.Id, SelectedScanType, "Started on Assessment Scan")).ToArray();
        StatusMessage = (jobs.Length == 0 ? "Select at least one customer with a connected tenant." : $"Started {jobs.Length} {ScanTypes.Display(SelectedScanType).ToLowerInvariant()}(s); follow them in the activity panel.") +
                        (notConnected.Length > 0 ? $" Not connected (register the app first): {string.Join(", ", notConnected)}." : string.Empty);
    }

    [RelayCommand]
    private async Task RunPendingAsync()
    {
        var count = await _assessments.QueuePendingAsync();
        StatusMessage = count == 0 ? "There are no pending assessments." : $"Started {count} pending assessment(s).";
    }

    /// <summary>Records an assessment request (Pending) for the current customer, run as soon as possible.</summary>
    [RelayCommand]
    private async Task RequestAssessmentAsync()
    {
        if (_customers.CurrentCustomer is not { } customer) return;
        var result = await Engagements.RequestInitialAssessmentAsync(customer.Id, _workspaces.CurrentWorkspace?.Id);
        if (!result.Succeeded)
        {
            StatusMessage = result.Error!.ToString();
            return;
        }

        if (customer.PrimaryTenant?.ApplicationRegistration?.SecretReference is not null)
        {
            _assessments.Queue(customer.Id, result.Value!.AssessmentRunId, "Requested on Assessment Scan");
            StatusMessage = $"The assessment of {customer.Name} has started.";
        }
        else
        {
            StatusMessage = $"An assessment of {customer.Name} is recorded as Pending; it runs once the tenant is connected (Tenant App Registration > Register automatically).";
        }

        await RefreshAsync(CancellationToken.None);
    }

    // ----- Recurring assessments -----

    [RelayCommand]
    private void NewSchedule()
    {
        Editor.Reset(_customers.CurrentCustomer?.Id.Value);
        ScheduleMessage = null;
    }

    [RelayCommand]
    private async Task SaveScheduleAsync()
    {
        if (!IsDatabaseAvailable)
        {
            ScheduleMessage = "The application database is not ready; schedules are stored in it.";
            return;
        }

        if (Editor.Build() is not { } schedule)
        {
            ScheduleMessage = "Correct the highlighted fields, then save.";
            return;
        }

        try
        {
            var saved = await _scheduler.SaveAsync(schedule);
            ScheduleMessage = $"Schedule '{saved.Name}' saved. Next run {saved.NextRunUtc?.ToLocalTime().ToString("dddd d MMMM HH:mm", CultureInfo.CurrentCulture) ?? "—"}.";
            await LoadSchedulesAsync();
            Editor.Reset(_customers.CurrentCustomer?.Id.Value);
            if (!IsBackgroundHostRegistered && saved.IsEnabled)
            {
                var status = await _backgroundHost.RegisterAsync();
                ApplyBackgroundStatus(status);
                if (status.IsRegistered) ScheduleMessage += " The background runner was registered so it also runs while Secure Plus Agent is closed.";
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.Data.Common.DbException)
        {
            ScheduleMessage = $"The schedule could not be saved: {exception.Message}";
        }
    }

    [RelayCommand]
    private void EditSchedule(ScheduleRowViewModel? row)
    {
        if (row is null) return;
        Editor.Load(row.Schedule);
        ScheduleMessage = $"Editing '{row.Name}'. Save to apply the changes, or New Schedule to discard them.";
    }

    [RelayCommand]
    private async Task RunScheduleNowAsync(ScheduleRowViewModel? row)
    {
        if (row is null) return;
        try
        {
            ScheduleMessage = await _scheduler.RunNowAsync(row.Schedule.ScheduleId)
                ? $"'{row.Name}' is running; its status changes to Succeeded or Failed when it finishes."
                : $"'{row.Name}' is already running.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
        {
            ScheduleMessage = $"'{row.Name}' could not start: {exception.Message}";
        }

        await LoadSchedulesAsync();
    }

    [RelayCommand]
    private async Task DeleteScheduleAsync(ScheduleRowViewModel? row)
    {
        if (row is null) return;
        var confirmed = _dialogs.Confirm(new ConfirmationRequest("Delete Schedule", $"Delete the schedule '{row.Name}'? Its run history is deleted too; assessment results are kept.",
            [new("Customer", row.Customer), new("Frequency", row.Description), new("Next run", row.NextRun)], "Delete", IsDestructive: true));
        if (!confirmed) return;
        await _scheduler.DeleteAsync(row.Schedule.ScheduleId);
        if (Editor.ScheduleId == row.Schedule.ScheduleId) Editor.Reset(_customers.CurrentCustomer?.Id.Value);
        ScheduleMessage = $"Schedule '{row.Name}' deleted.";
        await LoadSchedulesAsync();
    }

    [RelayCommand]
    private void ClearScheduleFilters()
    {
        CustomerFilter.Reset();
        FrequencyFilter.Reset();
        NextRunFrom = NextRunTo = LastRunFrom = LastRunTo = null;
        ScheduleSearch = string.Empty;
    }

    [RelayCommand]
    private async Task RegisterBackgroundHostAsync() => ApplyBackgroundStatus(await _backgroundHost.RegisterAsync());

    [RelayCommand]
    private async Task RemoveBackgroundHostAsync() => ApplyBackgroundStatus(await _backgroundHost.UnregisterAsync());

    private async Task RefreshBackgroundHostAsync()
    {
        try
        {
            ApplyBackgroundStatus(await _backgroundHost.GetStatusAsync());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            BackgroundHostText = $"The background runner status could not be read: {exception.Message}";
        }
    }

    private void ApplyBackgroundStatus(BackgroundHostStatus status)
    {
        IsBackgroundHostRegistered = status.IsRegistered;
        BackgroundHostText = status.Detail;
    }

    private async Task SetEnabledAsync(ScheduleRowViewModel row, bool enabled)
    {
        try
        {
            await _scheduler.SetEnabledAsync(row.Schedule, enabled);
            ScheduleMessage = $"'{row.Name}' turned {(enabled ? "on" : "off")}.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
        {
            ScheduleMessage = $"'{row.Name}' could not be changed: {exception.Message}";
        }

        await LoadSchedulesAsync();
    }

    // ----- Assessments list -----

    [RelayCommand]
    private async Task AddOtherCustomerAsync()
    {
        var profile = OtherCustomer ?? OtherCustomerOptionsView.Cast<CustomerProfile>().FirstOrDefault();
        if (profile is null)
        {
            StatusMessage = "Choose a customer from the list to add.";
            return;
        }

        if (IncludedCustomers.All(chip => chip.Key != profile.Id.Value)) IncludedCustomers.Add(new CustomerChip(profile.Id.Value, profile.Name));
        OtherCustomer = null;
        OtherCustomerText = string.Empty;
        await ReloadAssessmentsAsync(CancellationToken.None);
    }

    [RelayCommand]
    private async Task RemoveOtherCustomerAsync(CustomerChip? chip)
    {
        if (chip is null) return;
        IncludedCustomers.Remove(chip);
        await ReloadAssessmentsAsync(CancellationToken.None);
    }

    private async Task ReloadAssessmentsAsync(CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        if (_customers.CurrentCustomer is { } current) keys.Add(current.Id.Value);
        if (IncludeOtherCustomers) keys.AddRange(IncludedCustomers.Select(chip => chip.Key));
        await LoadAssessmentsAsync(keys.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), cancellationToken);
        OtherCustomerOptions.Clear();
        foreach (var profile in _allProfiles.Where(profile => !keys.Contains(profile.Id.Value, StringComparer.OrdinalIgnoreCase)).OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase))
            OtherCustomerOptions.Add(profile);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        CustomerName = _customers.CurrentCustomer?.Name ?? "No customer selected";
        _allProfiles = await _profiles.ListAsync(cancellationToken);
        CustomerNames = _allProfiles.ToDictionary(profile => profile.Id.Value, profile => profile.Name, StringComparer.OrdinalIgnoreCase);
        await ReloadAssessmentsAsync(cancellationToken);
        var history = IsDatabaseAvailable ? await Engagements.ListAssessmentsAsync(null, cancellationToken) : null;
        var selected = CustomerChoices.Where(choice => choice.IsSelected).Select(choice => choice.Profile.Id).ToHashSet();
        CustomerChoices.Clear();
        foreach (var profile in _allProfiles.OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var latest = history?.Value?.Where(item => item.CustomerKey == profile.Id.Value).OrderByDescending(item => item.RequestedAtUtc).FirstOrDefault();
            var text = latest is null ? "Never" : $"{latest.StatusCode} {latest.RequestedAtUtc.ToLocalTime():d}{(latest.Score is { } score ? $" ({score:0.#}%)" : string.Empty)}";
            CustomerChoices.Add(new CustomerChoice(profile, text) { IsSelected = selected.Contains(profile.Id) });
        }

        var customerOptions = _allProfiles.OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase).Select(profile => (profile.Id.Value, profile.Name)).ToArray();
        Editor.SetCustomers(customerOptions, _customers.CurrentCustomer?.Id.Value);
        CustomerFilter.SetOptions(new[] { (ScheduleEditorViewModel.AllCustomersKey, "All connected customers") }.Concat(customerOptions));
        await LoadSchedulesAsync();
        if (IsDatabaseAvailable && Assessments.Count == 0)
            StatusMessage = $"The database has no assessment for {CustomerName}. Tick the customer and select Assess Selected Now to run one.";
    }

    private async Task LoadSchedulesAsync()
    {
        if (!IsDatabaseAvailable)
        {
            Schedules.Clear();
            RefreshScheduleView();
            return;
        }

        try
        {
            var running = _scheduler.RunningInProcess.ToHashSet();
            var rows = (await _scheduler.ListAsync()).Select(schedule => new ScheduleRowViewModel(schedule,
                schedule.AllCustomers ? "All connected customers" : string.Join(", ", schedule.CustomerKeys.Select(key => CustomerNames.GetValueOrDefault(key, key))),
                running.Contains(schedule.ScheduleId), SetEnabledAsync)).ToArray();
            Schedules.Clear();
            foreach (var row in rows.OrderBy(row => row.Customer, StringComparer.CurrentCultureIgnoreCase).ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase))
                Schedules.Add(row);
            RefreshScheduleView();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
        {
            ScheduleMessage = $"The schedules could not be loaded: {exception.Message}";
        }
    }
}
