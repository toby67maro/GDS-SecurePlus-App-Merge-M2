using System.Globalization;
using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Domain.Customers;

namespace EngineeringWorkspace.Desktop.Pages.Live;

public sealed record LiveTile(string Title, string Value, string Detail);
public sealed record LiveDataset(string Name, string Status, int Items, string Source, string? Message);
public sealed record LivePolicy(string Name, string State, string Users, string Targets, string Grant);
public sealed record LiveIntunePolicy(string Name, string Type, string Platforms, int Settings);

/// <summary>One tenant in the live run list with its status (Queued, Connecting, Running, Completed, Failed, Cancelled).</summary>
public sealed partial class LiveRunRow(CustomerProfile profile) : ObservableObject
{
    public CustomerProfile Profile { get; } = profile;
    public string Customer => Profile.Name;
    public string Tenant => Profile.PrimaryTenant?.PrimaryDomain ?? (IsConnected ? "Connected" : "Not connected");
    public bool IsConnected => Profile.PrimaryTenant?.ApplicationRegistration?.SecretReference is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFailed), nameof(IsCompleted))]
    private string _status = "Ready";

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _detail = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isActive;

    public bool IsIdle => !IsActive;
    public bool IsFailed => Status is "Failed" or "Partial";
    public bool IsCompleted => Status == "Completed";
    public AssessmentRunOutcome? Outcome { get; set; }
    public CancellationTokenSource? Cancellation { get; set; }
}

/// <summary>
/// Assessment → Live Assessment: a run list of up to 25 customers' tenants. Each row can be assessed on its own, or
/// all at once with Run Live Assessments Now, throttled by Max Concurrent Assessments (default 5, 1–20). Cancel stops
/// queued work and cancels running work gracefully. Every run is stored in the database (source Live); selecting a
/// completed row shows what was read from its tenant.
/// </summary>
public sealed partial class LiveAssessmentViewModel(
    ICustomerContextService customers,
    ICustomerProfileStore profiles,
    IAssessmentEvaluator evaluator,
    ICatalogService catalog,
    IAssessmentService assessments) : PageViewModel
{
    public const int MaxRunList = 25;

    private CancellationTokenSource? _runAll;
    private IReadOnlyList<CustomerProfile> _allProfiles = [];

    public ObservableCollection<LiveTile> Tiles { get; } = [];
    public ObservableCollection<LiveDataset> Datasets { get; } = [];
    public ObservableCollection<AssessmentResultItem> Checks { get; } = [];
    public ObservableCollection<LivePolicy> ConditionalAccess { get; } = [];
    public ObservableCollection<LiveIntunePolicy> IntunePolicies { get; } = [];
    public ObservableCollection<LiveRunRow> RunList { get; } = [];
    public ObservableCollection<CustomerProfile> CustomerOptions { get; } = [];

    [ObservableProperty] private string _customerName = string.Empty;
    [ObservableProperty] private string? _collectedText;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string _customerSearch = string.Empty;
    [ObservableProperty] private CustomerProfile? _customerToAdd;
    [ObservableProperty] private LiveRunRow? _selectedRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd), nameof(RunListLimitText))]
    private int _runListCount;

    [ObservableProperty] private string _maxConcurrentText = ConcurrencyLimit.Default.ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private bool _maxCorrected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunAll))]
    private bool _isRunningAll;

    public bool CanAdd => RunListCount < MaxRunList;
    public bool CanRunAll => !IsRunningAll;
    public string RunListLimitText => CanAdd ? $"{RunListCount} of {MaxRunList} customers" : $"The run list is full ({MaxRunList} customers). Remove a customer to add another.";

    public int MaxConcurrent => int.TryParse(MaxConcurrentText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) ? ConcurrencyLimit.Clamp(value).Value : ConcurrencyLimit.Default;

    private bool _correcting;

    /// <summary>Above 20 (or below 1) the value is changed to the limit automatically and shown in red.</summary>
    partial void OnMaxConcurrentTextChanged(string value)
    {
        if (_correcting || !int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var requested)) return;
        var (clamped, corrected) = ConcurrencyLimit.Clamp(requested);
        MaxCorrected = corrected;
        if (!corrected) return;
        _correcting = true;
        MaxConcurrentText = clamped.ToString(CultureInfo.CurrentCulture);
        _correcting = false;
    }

    partial void OnCustomerSearchChanged(string value) => FilterOptions();

    partial void OnSelectedRowChanged(LiveRunRow? value)
    {
        if (value?.Outcome?.Snapshot is { } snapshot)
        {
            CustomerName = value.Customer;
            Show(snapshot);
        }
    }

    public override async Task OnNavigatedToAsync(RouteDescriptor route, CancellationToken cancellationToken)
    {
        await base.OnNavigatedToAsync(route, cancellationToken);
        _allProfiles = await profiles.ListAsync(cancellationToken);
        CustomerName = customers.CurrentCustomer?.Name ?? "No customer selected";
        if (RunList.Count == 0 && customers.CurrentCustomer is { } current) AddRow(current);
        FilterOptions();
        StatusMessage = RunList.Count == 0 ? "Search for a customer and select Add (+) to build the run list." : null;
    }

    private void FilterOptions()
    {
        var term = CustomerSearch.Trim();
        var inList = RunList.Select(row => row.Profile.Id).ToHashSet();
        CustomerOptions.Clear();
        foreach (var profile in _allProfiles.Where(profile => !inList.Contains(profile.Id) && (term.Length == 0 || profile.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)))
                     .OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase))
            CustomerOptions.Add(profile);
        if (CustomerToAdd is null || !CustomerOptions.Contains(CustomerToAdd)) CustomerToAdd = CustomerOptions.FirstOrDefault();
    }

    private void AddRow(CustomerProfile profile)
    {
        if (RunList.Count >= MaxRunList || RunList.Any(row => row.Profile.Id == profile.Id)) return;
        RunList.Add(new LiveRunRow(profile) { Status = profile.PrimaryTenant?.ApplicationRegistration?.SecretReference is null ? "Not connected" : "Ready" });
        RunListCount = RunList.Count;
    }

    [RelayCommand]
    private void AddCustomer()
    {
        if (!CanAdd)
        {
            StatusMessage = RunListLimitText;
            return;
        }

        if (CustomerToAdd is not { } profile)
        {
            StatusMessage = "Search for a customer to add.";
            return;
        }

        AddRow(profile);
        CustomerSearch = string.Empty;
        FilterOptions();
        StatusMessage = null;
    }

    [RelayCommand]
    private void RemoveCustomer(LiveRunRow? row)
    {
        if (row is null || row.IsActive) return;
        RunList.Remove(row);
        RunListCount = RunList.Count;
        FilterOptions();
    }

    [RelayCommand]
    private async Task AssessTenantNowAsync(LiveRunRow? row)
    {
        if (row is null || row.IsActive) return;
        using var cancellation = new CancellationTokenSource();
        await RunRowAsync(row, cancellation.Token);
    }

    [RelayCommand]
    private async Task RunAllAsync()
    {
        if (IsRunningAll) return;
        var rows = RunList.Where(row => !row.IsActive).ToArray();
        if (rows.Length == 0)
        {
            StatusMessage = "Add at least one customer to the run list.";
            return;
        }

        IsRunningAll = true;
        _runAll = new CancellationTokenSource();
        var limit = MaxConcurrent;
        foreach (var row in rows)
        {
            row.Status = "Queued";
            row.Progress = 0;
            row.Detail = $"Waiting for a free slot (at most {limit} at a time).";
            row.IsActive = true;
        }

        StatusMessage = $"Running {rows.Length} live assessment(s), {limit} at a time.";
        try
        {
            await ThrottledRunner.RunAsync(rows, limit, RunRowAsync, _runAll.Token);
        }
        catch (OperationCanceledException)
        {
            // Queued rows never started; running rows were cancelled through their tokens.
        }
        finally
        {
            foreach (var row in rows.Where(row => row.Status == "Queued"))
            {
                row.Status = "Cancelled";
                row.Detail = "Cancelled before it started.";
                row.IsActive = false;
            }

            var completed = rows.Count(row => row.IsCompleted);
            StatusMessage = $"{completed} of {rows.Length} live assessment(s) completed and saved." +
                            (rows.Any(row => row.IsFailed) ? $" Failed: {string.Join(", ", rows.Where(row => row.IsFailed).Select(row => row.Customer))}." : string.Empty);
            _runAll.Dispose();
            _runAll = null;
            IsRunningAll = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _runAll?.Cancel();
        foreach (var row in RunList.Where(row => row.IsActive)) row.Cancellation?.Cancel();
        StatusMessage = "Cancelling: queued assessments will not start and running ones stop after their current step.";
    }

    private async Task RunRowAsync(LiveRunRow row, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        row.Cancellation = linked;
        row.IsActive = true;
        row.Status = "Connecting";
        row.Progress = 0;
        row.Detail = "Starting...";
        try
        {
            var progress = new UiProgress(update =>
            {
                row.Status = update.Stage == AssessmentStages.Connecting ? "Connecting" : "Running";
                row.Progress = update.Percent;
                row.Detail = update.Activity;
            });
            var outcome = await Task.Run(() => assessments.RunAsync(new AssessmentRunRequest(row.Profile.Id, AssessmentRunSources.Live, Reason: "Live assessment (quick scan)", ScanType: ScanTypes.Quick), progress,
                linked.Token));
            row.Outcome = outcome;
            row.Progress = 100;
            row.Status = outcome.StatusCode switch
            {
                AssessmentStatusCodes.Completed => "Completed",
                AssessmentStatusCodes.Cancelled => "Cancelled",
                AssessmentStatusCodes.Partial => "Partial",
                _ => "Failed"
            };
            row.Detail = outcome.Succeeded
                ? $"{outcome.Score:0.#}% – {outcome.Failing} gap(s); {outcome.ResultsSaved:N0} results saved (run {outcome.RunId})."
                : outcome.Error ?? outcome.Summary;
            if (outcome.Succeeded && (SelectedRow is null || SelectedRow == row)) OnSelectedRowChanged(row);
        }
        finally
        {
            row.IsActive = false;
            row.Cancellation = null;
        }
    }

    /// <summary>Marshals progress from the background run to the UI thread.</summary>
    private sealed class UiProgress(Action<AssessmentProgressUpdate> handler) : IProgress<AssessmentProgressUpdate>
    {
        public void Report(AssessmentProgressUpdate value) => Shell.ShellViewModel.OnUi(() => handler(value));
    }

    private void Show(TenantSnapshot snapshot)
    {
        Tiles.Clear();
        Datasets.Clear();
        Checks.Clear();
        ConditionalAccess.Clear();
        IntunePolicies.Clear();
        foreach (var dataset in snapshot.Datasets.Values.OrderBy(dataset => dataset.Status).ThenBy(dataset => dataset.Name))
            Datasets.Add(new LiveDataset(dataset.Name, dataset.Status.ToString(), dataset.Items.Count, dataset.Source, dataset.Message));

        var score = snapshot.Get(TenantDatasets.SecureScores).Items.FirstOrDefault();
        if (score.ValueKind == JsonValueKind.Object && score.TryGetProperty("currentScore", out var current) && score.TryGetProperty("maxScore", out var max) && max.GetDouble() > 0)
            Tiles.Add(new LiveTile("Secure Score", $"{100 * current.GetDouble() / max.GetDouble():0.#}%", $"{current.GetDouble():0} of {max.GetDouble():0} points"));
        var registration = snapshot.Get(TenantDatasets.RegistrationDetails).Items;
        if (registration.Count > 0)
        {
            var capable = registration.Count(user => user.TryGetProperty("isMfaCapable", out var value) && value.ValueKind == JsonValueKind.True);
            Tiles.Add(new LiveTile("MFA capable", $"{100.0 * capable / registration.Count:0.#}%", $"{capable} of {registration.Count} users"));
        }

        var users = snapshot.Get(TenantDatasets.Users).Items;
        if (users.Count > 0)
            Tiles.Add(new LiveTile("Users", users.Count.ToString("N0"), $"{users.Count(user => Text(user, "userType") == "Guest")} guests"));
        var admins = snapshot.Get(TenantDatasets.RoleAssignments).Items.Where(item => Text(item, "roleDefinitionId") == "62e90394-69f5-4237-9190-012177145e10")
            .Select(item => Text(item, "principalId")).Distinct().Count();
        Tiles.Add(new LiveTile("Global Administrators", admins.ToString(), "Recommended: 2 to 4"));
        var devices = snapshot.Get(TenantDatasets.ManagedDevices).Items;
        if (devices.Count > 0)
            Tiles.Add(new LiveTile("Managed devices", devices.Count.ToString("N0"), $"{devices.Count(device => Text(device, "complianceState") == "compliant")} compliant"));
        var policies = snapshot.Get(TenantDatasets.ConditionalAccessPolicies).Items;
        Tiles.Add(new LiveTile("Conditional Access", policies.Count.ToString(),
            $"{policies.Count(p => Text(p, "state") == "enabled")} on, {policies.Count(p => Text(p, "state") == "enabledForReportingButNotEnforced")} report-only"));

        foreach (var policy in policies.OrderBy(p => Text(p, "displayName")))
        {
            var conditions = Property(policy, "conditions");
            var usersCondition = Property(conditions, "users");
            var applications = Property(conditions, "applications");
            var grant = Property(policy, "grantControls");
            ConditionalAccess.Add(new LivePolicy(Text(policy, "displayName"), Text(policy, "state"),
                string.Join(", ", Strings(usersCondition, "includeUsers").Concat(Strings(usersCondition, "includeRoles").Any() ? ["admin roles"] : [])),
                string.Join(", ", Strings(applications, "includeApplications").Concat(Strings(applications, "includeUserActions"))),
                string.Join($" {Text(grant, "operator")} ", Strings(grant, "builtInControls"))));
        }

        foreach (var policy in snapshot.Get(TenantDatasets.ConfigurationPolicies).Items.OrderBy(p => Text(p, "name")))
            IntunePolicies.Add(new LiveIntunePolicy(Text(policy, "name"), Text(Property(policy, "templateReference"), "templateDisplayName") is { Length: > 0 } template ? template : "Settings catalog",
                Text(policy, "platforms"), Property(policy, "settings").ValueKind == JsonValueKind.Array ? Property(policy, "settings").GetArrayLength() : 0));
        foreach (var policy in snapshot.Get(TenantDatasets.CompliancePolicies).Items.Concat(snapshot.Get(TenantDatasets.DeviceConfigurations).Items))
            IntunePolicies.Add(new LiveIntunePolicy(Text(policy, "displayName"), Text(policy, "@odata.type").Replace("#microsoft.graph.", string.Empty, StringComparison.Ordinal), string.Empty, 0));

        var results = evaluator.Evaluate(catalog.Catalog, snapshot);
        foreach (var result in results.Where(result => result.ControlId.StartsWith("CHK-", StringComparison.Ordinal) || result.ControlId.StartsWith("INFO-", StringComparison.Ordinal))
                     .OrderBy(result => result.Result == AssessmentResultValues.Pass).ThenBy(result => result.Area))
            Checks.Add(result);
        var forbidden = snapshot.Datasets.Values.Count(dataset => dataset.Status == DatasetStatus.Forbidden);
        CollectedText = $"Read {snapshot.CollectedAtUtc.ToLocalTime():g}. {snapshot.Datasets.Count} datasets" +
                        (forbidden > 0 ? $", {forbidden} not permitted (use Update permissions automatically on Tenant App Registration)." : ".");
        HasData = true;
    }

    private static JsonElement Property(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string Text(JsonElement element, string name) => Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? string.Empty : string.Empty;

    private static IEnumerable<string> Strings(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray().Select(item => item.GetString() ?? string.Empty) : [];
}
