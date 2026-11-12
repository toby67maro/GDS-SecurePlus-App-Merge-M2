using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Governance;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Desktop.Services;
using EngineeringWorkspace.Domain.Customers;

namespace EngineeringWorkspace.Desktop.Pages.Deployment;

public sealed record ExecutionRow(ChangeExecutionRecord Record)
{
    public int Id => Record.ChangeExecutionId;
    public string Mode => Record.ModeCode switch
    {
        ExecutionModes.Apply => "Applied to tenant by Secure Plus",
        ExecutionModes.Execute => "Applied and verified",
        _ => "Simulation"
    };
    public string Status => Record.StatusCode;
    public string Started => Record.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    public string By => Record.ExecutedBy;
    public string Reassessment => Record.ReassessmentRunId?.ToString(CultureInfo.CurrentCulture) ?? "—";
}

/// <summary>
/// Deployment → Change Execution: pick an approved (or scheduled) change request of the customer, simulate it
/// (pre-checks, step-by-step plan, log) and save its deployment package (Deploy.ps1 and the Graph bodies, Conditional
/// Access in report-only state). After the change is applied, Record As Applied re-assesses the tenant (confirmation
/// required) and marks the request Completed when every control is in place. Apply to Tenant creates the policies in the
/// tenant after the Advanced Mode passcode and a confirmation prompt; deployment variables fill the policies' placeholders.
/// </summary>
public sealed partial class ChangeExecutionViewModel(ICustomerContextService context, ICustomerProfileStore store, IChangeRequestRepository repository,
    ChangeExecutionService executor, IDialogService dialogs, DeploymentSelection selection, EngineeringWorkspace.Application.Catalog.ICatalogService catalog,
    EngineeringWorkspace.Application.Tenants.ITenantInventoryRepository? inventory = null)
    : CustomerScopedPageViewModel(context, store)
{
    /// <summary>The deployment variables the selected change request needs, with the customer's values.</summary>
    public ObservableCollection<DeploymentVariableRow> Variables { get; } = [];

    [ObservableProperty] private string _variablesText = string.Empty;

    private void LoadVariables()
    {
        Variables.Clear();
        if (SelectedRequest is not { } request || SelectedCustomer is not { } customer) return;
        var controls = request.Record.Items.Select(item => catalog.Catalog.Find(item.ItemKey)).OfType<EngineeringWorkspace.Application.Catalog.CatalogControl>().ToArray();
        var units = DeploymentPlanner.Build(controls);
        var needed = units.SelectMany(unit => unit.Placeholders).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name == "RING" ? "" : name, StringComparer.OrdinalIgnoreCase)
            .Append(DeploymentVariables.PolicyNamePrefix).ToArray();
        var values = DeploymentVariables.Load(customer);
        foreach (var name in needed)
            Variables.Add(new DeploymentVariableRow(name, DeploymentVariables.Descriptions.GetValueOrDefault(name, "Value for this placeholder."), values.GetValueOrDefault(name) ?? string.Empty,
                units.Count(unit => unit.Placeholders.Contains(name, StringComparer.OrdinalIgnoreCase))));
        _ = LoadGroupChoicesAsync(customer);
        VariablesText = $"{units.Count} polic(ies) will be created from {controls.Length} control(s). " +
                        $"{Variables.Count(row => row.Uses > 0 && string.IsNullOrWhiteSpace(row.Value))} required variable(s) still empty.";
    }

    /// <summary>Group variables get a picker of the tenant's Entra groups from the last scan (4.0 beta.2); without a scan they stay free text.</summary>
    private async Task LoadGroupChoicesAsync(EngineeringWorkspace.Domain.Customers.CustomerProfile customer)
    {
        if (inventory is not { IsAvailable: true }) return;
        IReadOnlyList<EngineeringWorkspace.Application.Tenants.TenantGroup> groups;
        try
        {
            groups = await inventory.ListGroupsAsync(customer.Id.Value);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
        {
            return;
        }

        if (groups.Count == 0) return;
        foreach (var row in Variables.Where(row => DeploymentVariables.IsGroup(row.Name)))
        {
            var choices = new List<Customers.EntraGroupChoice> { Customers.EntraGroupChoice.None };
            choices.AddRange(groups.Select(group => new Customers.EntraGroupChoice(group.ObjectId, group.Display, group.MemberCount, true)));
            if (!string.IsNullOrWhiteSpace(row.Value) && choices.All(choice => !string.Equals(choice.ObjectId, row.Value, StringComparison.OrdinalIgnoreCase)))
                choices.Add(new Customers.EntraGroupChoice(row.Value, $"{row.Value} (typed name – pick the group)", null, false));
            row.Choices = choices;
        }
    }

    [RelayCommand]
    private async Task SaveVariablesAsync()
    {
        if (SelectedCustomer is not { } selected) return;
        var customer = await CustomerStore.LoadAsync(selected.Id) ?? selected;
        var updated = DeploymentVariables.Save(customer, Variables.ToDictionary(row => row.Name, row => row.Value, StringComparer.OrdinalIgnoreCase));
        await CustomerStore.SaveAsync(updated);
        if (CustomerContext.CurrentCustomer?.Id == updated.Id) CustomerContext.Refresh(updated);
        var index = CustomerOptions.IndexOf(selected);
        if (index >= 0) CustomerOptions[index] = updated;
        StatusMessage = $"Deployment variables saved for {updated.Name}.";
        LoadVariables();
    }

    private IReadOnlyList<DeploymentFile> _package = [];
    private CancellationTokenSource? _cancellation;

    public ObservableCollection<ChangeRequestRow> Requests { get; } = [];
    public ObservableCollection<ExecutionStep> Steps { get; } = [];
    public ObservableCollection<ExecutionRow> Executions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRequest))]
    private ChangeRequestRow? _selectedRequest;

    [ObservableProperty] private string _log = string.Empty;
    [ObservableProperty] private bool _hasPackage;
    [ObservableProperty] private ExecutionRow? _selectedExecution;

    public bool HasRequest => SelectedRequest is not null;

    partial void OnSelectedRequestChanged(ChangeRequestRow? value)
    {
        _ = LoadExecutionsAsync();
        LoadVariables();
    }

    partial void OnSelectedExecutionChanged(ExecutionRow? value)
    {
        if (value is not null) Log = value.Record.Log ?? string.Empty;
    }

    protected override async Task OnCustomerChangedAsync(CustomerProfile? customer)
    {
        Steps.Clear();
        Log = string.Empty;
        HasPackage = false;
        await LoadRequestsAsync(customer, selection.Take());
    }

    private async Task LoadRequestsAsync(CustomerProfile? customer, int? wanted)
    {
        Requests.Clear();
        if (customer is null) return;
        if (!repository.IsAvailable)
        {
            StatusMessage = "The application database is not ready; change requests are stored in it.";
            return;
        }

        foreach (var record in (await repository.ListChangeRequestsAsync(customer.Id.Value)).Where(ChangeExecutionService.CanRun)) Requests.Add(new ChangeRequestRow(record, customer.Name));
        SelectedRequest = Requests.FirstOrDefault(row => row.Id == wanted) ?? Requests.FirstOrDefault();
        StatusMessage = Requests.Count == 0 ? $"{customer.Name} has no approved or scheduled change requests. Approve one on Change Request first." : null;
    }

    private async Task LoadExecutionsAsync()
    {
        Executions.Clear();
        if (SelectedRequest is not { } request || !repository.IsAvailable) return;
        foreach (var execution in await repository.ListExecutionsAsync(request.Id)) Executions.Add(new ExecutionRow(execution));
    }

    [RelayCommand]
    private Task SimulateAsync() => RunAsync(simulate: true);

    [RelayCommand]
    private async Task RecordAppliedAsync()
    {
        if (SelectedRequest is not { } request) return;
        var confirmed = dialogs.Confirm(new ConfirmationRequest("Record Change As Applied",
            $"Confirm that change request #{request.Id} '{request.Title}' has been applied to {SelectedCustomer?.Name}'s tenant. Secure Plus will re-assess the tenant now and mark the request Completed when every control is in place.",
            [new("Customer", SelectedCustomer?.Name ?? string.Empty), new("Risk", request.Risk), new("Controls", request.Items.ToString(CultureInfo.CurrentCulture)),
             new("Approved by", request.ApprovedBy)], "Re-assess and Record"));
        if (confirmed) await RunAsync(simulate: false);
    }

    /// <summary>
    /// Creates the change request's policies in the customer's tenant. Requires the Advanced Mode passcode and a
    /// confirmation prompt that states exactly what will be written.
    /// </summary>
    [RelayCommand]
    private async Task ApplyToTenantAsync()
    {
        if (SelectedRequest is not { } request || IsBusy) return;
        var tenant = SelectedCustomer?.PrimaryTenant;
        if (tenant?.DeploymentRegistration is not { } deployment)
        {
            // 4.0 beta.2: the Read registration cannot write; deployment access is a separate registration for the deploy phase.
            dialogs.ShowInformation("Deployment Access Is Off",
                $"{SelectedCustomer?.Name ?? "This customer"} has no deployment access. Open Customer Profiles > Tenant Access and select Enable Deployment Access " +
                "for this deploy phase (a Global Administrator signs in), then apply the change. Remove deployment access when the phase ends.");
            return;
        }

        var confirmed = dialogs.Confirm(new ConfirmationRequest("Apply Change to the Live Tenant",
            $"Secure Plus will create the policies of change request #{request.Id} '{request.Title}' in {SelectedCustomer?.Name}'s Microsoft 365 tenant now. " +
            "Conditional Access policies are created in report-only state; Intune and endpoint security policies are created as defined in the catalog. " +
            "Policies already in place or already present by name are skipped. Every change is written to the audit log and the tenant is re-assessed afterwards.",
            [new("Customer", SelectedCustomer?.Name ?? string.Empty), new("Tenant", tenant?.PrimaryDomain ?? tenant?.DisplayName ?? "not connected"),
             new("Change request", $"#{request.Id} {request.Title}"), new("Controls", request.Items.ToString(CultureInfo.CurrentCulture)), new("Risk", request.Risk),
             new("Approved by", request.ApprovedBy), new("Written by", $"Deploy registration {deployment.ClientId}")],
            "Apply to Tenant", IsDestructive: true, Passcode: EngineeringWorkspace.Application.Navigation.AdvancedModeService.IsPasscode,
            PasscodeLabel: "Enter the passcode to allow this change"));
        if (!confirmed) return;
        IsBusy = true;
        Steps.Clear();
        Log = string.Empty;
        _cancellation = new CancellationTokenSource();
        try
        {
            var progress = new Progress<ExecutionStep>(step => Steps.Add(step));
            var result = await Task.Run(() => executor.ApplyToTenantAsync(request.Record, progress, _cancellation.Token));
            Log = result.Log;
            await LoadRequestsAsync(SelectedCustomer, request.Id);
            StatusMessage = $"Apply to tenant {result.Status.ToLowerInvariant()}. Review the steps; select Record As Applied to verify and complete the request.";
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    private async Task RunAsync(bool simulate)
    {
        if (SelectedRequest is not { } request || IsBusy) return;
        IsBusy = true;
        Steps.Clear();
        Log = string.Empty;
        _cancellation = new CancellationTokenSource();
        try
        {
            var progress = new Progress<ExecutionStep>(step => Steps.Add(step));
            var result = simulate
                ? await Task.Run(() => executor.SimulateAsync(request.Record, progress, _cancellation.Token))
                : await Task.Run(() => executor.RecordAppliedAsync(request.Record, progress, _cancellation.Token));
            Log = result.Log;
            _package = result.Package;
            HasPackage = _package.Count > 0;
            await LoadRequestsAsync(SelectedCustomer, request.Id);
            StatusMessage = $"{(simulate ? "Simulation" : "Verification")} {result.Status.ToLowerInvariant()}." +
                            (HasPackage ? " Save the deployment package, run Deploy.ps1, then select Record As Applied." : string.Empty) +
                            (Requests.All(row => row.Id != request.Id) ? $" Change request #{request.Id} is now Completed." : string.Empty);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SavePackageAsync()
    {
        if (_package.Count == 0) return;
        var path = dialogs.SaveFile("Deploy.ps1", "PowerShell script (*.ps1)|*.ps1");
        if (path is null) return;
        var folder = Path.GetDirectoryName(path)!;
        foreach (var file in _package)
            await File.WriteAllTextAsync(file.FileName == "Deploy.ps1" ? path : Path.Combine(folder, file.FileName), file.Content, new System.Text.UTF8Encoding(true));
        StatusMessage = $"Deployment package saved to {folder} ({_package.Count} file(s)).";
    }
}

/// <summary>One deployment variable (a placeholder's value) shown on Change Execution.</summary>
public sealed partial class DeploymentVariableRow(string name, string description, string value, int uses) : ObservableObject
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    public int Uses { get; } = uses;
    public string UsesText => Uses == 0 ? "optional" : $"used by {Uses} polic(ies)";

    [ObservableProperty] private string _value = value;

    /// <summary>Entra groups to pick from (group variables, after a scan); null = free text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChoices))]
    private IReadOnlyList<Customers.EntraGroupChoice>? _choices;

    public bool HasChoices => Choices is { Count: > 0 };
}
