using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Application.Tenants;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Desktop.Services;
using EngineeringWorkspace.Domain.Customers;
using EngineeringWorkspace.Domain.Tenants;

namespace EngineeringWorkspace.Desktop.Pages.Tenants;

public sealed partial class CapabilityOption(TenantCapability capability, bool isSelected) : ObservableObject
{
    public TenantCapability Capability { get; } = capability;

    [ObservableProperty]
    private bool _isSelected = isSelected;
}

public sealed record ReadinessItem(string Name, CheckState State);

/// <summary>
/// Workspace → Customer Profiles → Tenant App Registration. Configures and tests the app registration of one customer
/// tenant and runs the Create / Configure App Registration assistant one confirmed step at a time.
/// </summary>
public sealed partial class TenantAppRegistrationViewModel : PageViewModel
{
    private readonly ITenantRegistrationService _registrations;
    private readonly ICustomerContextService _customers;
    private readonly ITenantConnectionService _connections;
    private readonly IPermissionValidationService _permissions;
    private readonly AppRegistrationAssistant _assistant;
    private readonly IDialogService _dialogs;
    private readonly ITenantOnboardingService _onboarding;
    private readonly Application.Jobs.IJobService _jobs;

    public TenantAppRegistrationViewModel(ITenantRegistrationService registrations, ICustomerContextService customers, ITenantConnectionService connections,
        IPermissionValidationService permissions, AppRegistrationAssistant assistant, IDialogService dialogs, ITenantOnboardingService onboarding, Application.Jobs.IJobService jobs)
    {
        _onboarding = onboarding;
        _jobs = jobs;
        _registrations = registrations;
        _customers = customers;
        _connections = connections;
        _permissions = permissions;
        _assistant = assistant;
        _dialogs = dialogs;
        foreach (var capability in Enum.GetValues<TenantCapability>()) Capabilities.Add(new CapabilityOption(capability, true));
    }

    public event EventHandler? SecretInputsCleared;

    public ObservableCollection<CustomerProfile> CustomerProfiles { get; } = [];
    public ObservableCollection<CapabilityOption> Capabilities { get; } = [];
    public ObservableCollection<CapabilityPermissionStatus> Permissions { get; } = [];
    public ObservableCollection<ReadinessItem> Readiness { get; } = [];
    public ObservableCollection<AssistantStep> AssistantSteps { get; } = [];
    public IReadOnlyList<CloudEnvironment> CloudEnvironments { get; } = Enum.GetValues<CloudEnvironment>();
    public IReadOnlyList<TenantAuthenticationType> AuthenticationTypes { get; } = Enum.GetValues<TenantAuthenticationType>();
    public string? AssistantUnavailableReason => _assistant.UnavailableReason;
    public bool IsAssistantAvailable => _assistant.UnavailableReason is null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomer))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestConnectionCommand), nameof(RunAssistantStepCommand), nameof(RegisterAutomaticallyCommand), nameof(UpdatePermissionsAutomaticallyCommand))]
    private CustomerProfile? _selectedCustomer;

    [ObservableProperty] private string _newCustomerName = string.Empty;
    [ObservableProperty] private string _tenantKey = "primary";
    [ObservableProperty] private string _tenantDisplayName = string.Empty;
    [ObservableProperty] private string _tenantName = string.Empty;
    [ObservableProperty] private string _tenantId = string.Empty;
    [ObservableProperty] private string _primaryDomain = string.Empty;
    [ObservableProperty] private CloudEnvironment _cloudEnvironment = CloudEnvironment.Commercial;
    [ObservableProperty] private string _clientId = string.Empty;
    [ObservableProperty] private string _servicePrincipalId = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnsupportedAuthentication))]
    private TenantAuthenticationType _authenticationType = TenantAuthenticationType.ClientSecret;

    [ObservableProperty] private string _secretExpiration = string.Empty;
    [ObservableProperty] private string _certificateReference = string.Empty;
    [ObservableProperty] private string _secretState = "Not configured";
    [ObservableProperty] private TenantConnectionState _connectionState = TenantConnectionState.NotConfigured;
    [ObservableProperty] private string? _connectionDetail;
    [ObservableProperty] private DateTimeOffset? _lastSuccessfulConnection;
    [ObservableProperty] private DateTimeOffset? _lastValidation;
    [ObservableProperty] private string? _organizationSummary;
    [ObservableProperty] private string _assistantApplicationName = "Secure Plus Agent";
    [ObservableProperty] private string _assistantSecretLifetimeDays = "180";

    /// <summary>Transient client secret typed in the view; cleared as soon as it is stored.</summary>
    public string ClientSecretInput { private get; set; } = string.Empty;

    public bool HasCustomer => SelectedCustomer is not null;
    public bool IsUnsupportedAuthentication => AuthenticationType is not (TenantAuthenticationType.ClientSecret or TenantAuthenticationType.Certificate);

    public override async Task OnNavigatedToAsync(RouteDescriptor route, CancellationToken cancellationToken)
    {
        await base.OnNavigatedToAsync(route, cancellationToken);
        await LoadCustomersAsync(_customers.CurrentCustomer?.Id.Value);
        RefreshAssistant();
    }

    partial void OnSelectedCustomerChanged(CustomerProfile? value)
    {
        LoadTenant(value?.Tenants.FirstOrDefault(tenant => string.Equals(tenant.TenantKey, _customers.CurrentTenant?.TenantKey, StringComparison.OrdinalIgnoreCase))
                   ?? value?.PrimaryTenant);
    }

    [ObservableProperty] private string? _automaticStatus;
    [ObservableProperty] private bool _isAutomaticRunning;

    /// <summary>
    /// Fully automatic registration: an administrator signs in once (browser) and the app registration, permissions,
    /// admin consent, client secret, customer profile and connection test are all done in a background job.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasCustomer))]
    private void RegisterAutomatically()
    {
        if (SelectedCustomer is null) return;
        if (!_dialogs.Confirm(new ConfirmationRequest("Register the Secure Plus App Automatically",
                "A Microsoft sign-in opens in your browser. Sign in as a Global Administrator of the customer's tenant. Secure Plus Agent then creates (or reuses) the 'GDS Secure Plus Agent' app registration, adds the read-only Microsoft Graph permissions it needs, grants admin consent, creates a client secret (stored with DPAPI), saves the tenant to this customer and tests the connection.",
                [new("Customer", SelectedCustomer.Name), new("Tenant", string.IsNullOrWhiteSpace(TenantId) ? "the administrator's tenant" : TenantId),
                 new("Permissions", string.Join(", ", _permissions.RequiredApplicationPermissions(Enum.GetValues<TenantCapability>())))],
                "Sign in and register")))
            return;
        var tenantId = Guid.TryParse(TenantId, out var parsed) ? parsed : (Guid?)null;
        Track(_onboarding.RegisterAutomatically(SelectedCustomer.Id, tenantId, CloudEnvironment, startQuickScan: true));
    }

    /// <summary>Adds any missing permissions to the existing registration and grants consent (administrator sign-in).</summary>
    [RelayCommand(CanExecute = nameof(HasCustomer))]
    private void UpdatePermissionsAutomatically()
    {
        if (SelectedCustomer is null) return;
        if (!_dialogs.Confirm(new ConfirmationRequest("Update App Permissions",
                "A Microsoft sign-in opens in your browser. Sign in as a Global Administrator. The missing Microsoft Graph application permissions are added to the customer's app registration (existing ones are kept) and admin consent is granted.",
                [new("Customer", SelectedCustomer.Name)], "Sign in and update")))
            return;
        Track(_onboarding.UpdatePermissions(SelectedCustomer.Id));
    }

    private void Track(Application.Jobs.JobSnapshot job)
    {
        IsAutomaticRunning = true;
        AutomaticStatus = "Waiting for the administrator sign-in in your browser... Progress is shown in the activity panel.";
        void OnChanged(object? sender, Application.Jobs.JobSnapshot changed)
        {
            if (changed.Id != job.Id) return;
            Shell.ShellViewModel.OnUi(async () =>
            {
                AutomaticStatus = changed.IsFinished
                    ? changed.State == Application.Jobs.JobState.Succeeded ? $"Done: {changed.Result}" : $"{changed.State}: {changed.Error}"
                    : $"{changed.CurrentActivity}";
                if (!changed.IsFinished) return;
                _jobs.JobChanged -= OnChanged;
                IsAutomaticRunning = false;
                await LoadCustomersAsync(SelectedCustomer?.Id.Value);
            });
        }

        _jobs.JobChanged += OnChanged;
    }

    [RelayCommand]
    private async Task SelectCustomerAsync()
    {
        if (SelectedCustomer is null) return;
        var result = await _customers.SelectAsync(SelectedCustomer.Id, TenantKey, "Selected on Tenant App Registration");
        StatusMessage = result.Succeeded ? $"Current customer: {result.Value!.Name}." : result.Error!.ToString();
    }

    [RelayCommand]
    private async Task CreateCustomerAsync()
    {
        var result = await _registrations.CreateCustomerAsync(NewCustomerName);
        if (!result.Succeeded)
        {
            StatusMessage = result.Error!.ToString();
            return;
        }

        NewCustomerName = string.Empty;
        await LoadCustomersAsync(result.Value!.Id.Value);
        await _customers.SelectAsync(result.Value.Id, null, "Created on Tenant App Registration");
        StatusMessage = $"Customer '{result.Value.Name}' created and selected.";
    }

    [RelayCommand(CanExecute = nameof(HasCustomer))]
    private async Task SaveAsync()
    {
        try
        {
            if (SelectedCustomer is null || BuildTenant() is not { } tenant || !TryParseExpiration(out var expiration)) return;
            var result = await _registrations.SaveTenantAsync(SelectedCustomer.Id, tenant, ClientSecretInput, expiration);
            if (!result.Succeeded)
            {
                StatusMessage = result.Error!.ToString();
                return;
            }

            StatusMessage = "Tenant app registration saved. The client secret, if entered, was stored securely and is not shown again.";
            await LoadCustomersAsync(result.Value!.Id.Value);
        }
        finally
        {
            ClientSecretInput = string.Empty;
            SecretInputsCleared?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand(CanExecute = nameof(HasCustomer))]
    private async Task TestConnectionAsync()
    {
        if (SelectedCustomer is null || BuildTenant() is not { } tenant) return;
        IsBusy = true;
        ConnectionDetail = "Testing: credential, Microsoft Entra token, Microsoft Graph, tenant identity and permissions...";
        try
        {
            var report = await _connections.TestConnectionAsync(SelectedCustomer.Id.Value, tenant);
            ShowReport(report);
            _assistant.RecordConnectionTest(report);
            RefreshAssistant();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasCustomer))]
    private async Task RunAssistantStepAsync(AssistantStep? step)
    {
        if (step is null || SelectedCustomer is null || BuildTenant() is not { } tenant) return;
        var customerId = SelectedCustomer.Id.Value;
        if (step.Id is not (AssistantStepId.TestAuthentication or AssistantStepId.ValidatePermissions) &&
            !_dialogs.Confirm(new ConfirmationRequest(step.Title, step.WhatWillHappen,
                [new("Customer", SelectedCustomer.Name), new("Tenant ID", tenant.TenantId?.ToString() ?? "(not set)"), new("Cloud", tenant.CloudEnvironment.ToString()),
                 new("Requires administrator session", step.RequiresAdministratorSession ? "Yes" : "No")],
                "Run this step")))
            return;

        IsBusy = true;
        try
        {
            OperationResult result;
            switch (step.Id)
            {
                case AssistantStepId.ConnectAdministrator:
                    result = await _assistant.ConnectAsync(tenant, CancellationToken.None);
                    break;
                case AssistantStepId.ValidateAdministratorContext:
                    result = _assistant.ValidateAdministratorContext(tenant);
                    break;
                case AssistantStepId.CreateAppRegistration:
                    result = await _assistant.CreateApplicationAsync(customerId, tenant, AssistantApplicationName.Trim(), CancellationToken.None);
                    if (result.Succeeded && _assistant.Application is { } created) ClientId = created.ClientId.ToString();
                    break;
                case AssistantStepId.CreateOrLocateServicePrincipal:
                    result = await _assistant.EnsureServicePrincipalAsync(CancellationToken.None);
                    if (result.Succeeded && _assistant.ServicePrincipalId is { } servicePrincipal) ServicePrincipalId = servicePrincipal.ToString();
                    break;
                case AssistantStepId.ConfigurePermissions:
                    result = await _assistant.ConfigurePermissionsAsync(tenant, CancellationToken.None);
                    break;
                case AssistantStepId.GrantAdminConsent:
                    result = await _assistant.GrantAdminConsentAsync(tenant, CancellationToken.None);
                    break;
                case AssistantStepId.CreateCredential:
                case AssistantStepId.StoreCredential:
                    if (!int.TryParse(AssistantSecretLifetimeDays, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) || days is < 1 or > 730)
                    {
                        StatusMessage = "Enter a secret lifetime between 1 and 730 days.";
                        return;
                    }

                    result = await _assistant.CreateAndStoreClientSecretAsync(customerId, tenant, TimeSpan.FromDays(days), CancellationToken.None);
                    break;
                case AssistantStepId.AssociateWithCustomer:
                {
                    var association = _assistant.BuildAssociation(tenant);
                    result = association.Succeeded
                        ? await SaveAssociationAsync(association.Value!)
                        : OperationResult.Failure(association.Error!);
                    break;
                }
                default:
                    await TestConnectionAsync();
                    return;
            }

            StatusMessage = result.Succeeded ? $"{step.Title}: completed." : $"{step.Title}: {result.Error}";
        }
        finally
        {
            IsBusy = false;
            RefreshAssistant();
        }
    }

    [RelayCommand]
    private async Task SignOutAdministratorAsync()
    {
        await _assistant.SignOutAsync();
        StatusMessage = "Administrator session ended.";
        RefreshAssistant();
    }

    private async Task<OperationResult> SaveAssociationAsync(TenantConfiguration tenant)
    {
        var result = await _registrations.SaveTenantAsync(SelectedCustomer!.Id, tenant, null, null);
        if (!result.Succeeded) return OperationResult.Failure(result.Error!);
        await LoadCustomersAsync(result.Value!.Id.Value);
        return OperationResult.Success();
    }

    private async Task LoadCustomersAsync(string? selectId)
    {
        CustomerProfiles.Clear();
        foreach (var profile in await _registrations.ListCustomersAsync()) CustomerProfiles.Add(profile);
        SelectedCustomer = CustomerProfiles.FirstOrDefault(profile => profile.Id.Value == selectId) ?? SelectedCustomer switch
        {
            { } current => CustomerProfiles.FirstOrDefault(profile => profile.Id == current.Id),
            null => null
        };
    }

    private void LoadTenant(TenantConfiguration? tenant)
    {
        TenantKey = tenant?.TenantKey ?? "primary";
        TenantDisplayName = tenant?.DisplayName ?? SelectedCustomer?.Name ?? string.Empty;
        TenantName = tenant?.TenantName ?? string.Empty;
        TenantId = tenant?.TenantId?.ToString() ?? string.Empty;
        PrimaryDomain = tenant?.PrimaryDomain ?? string.Empty;
        CloudEnvironment = tenant?.CloudEnvironment ?? CloudEnvironment.Commercial;
        var registration = tenant?.ApplicationRegistration;
        ClientId = registration?.ClientId.ToString() ?? string.Empty;
        ServicePrincipalId = registration?.ServicePrincipalId?.ToString() ?? string.Empty;
        AuthenticationType = registration?.AuthenticationType ?? TenantAuthenticationType.ClientSecret;
        SecretExpiration = registration?.CredentialExpirationUtc?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        CertificateReference = registration?.CertificateReference ?? string.Empty;
        SecretState = registration?.SecretReference is { } reference ? $"Configured (******** stored as {reference})" : "Not configured";
        var configured = registration?.RequiredCapabilities is { Count: > 0 } list ? list : [TenantCapability.TenantDiscovery];
        foreach (var option in Capabilities) option.IsSelected = configured.Contains(option.Capability);

        var report = SelectedCustomer is null ? null : _connections.GetLastReport(SelectedCustomer.Id.Value, TenantKey);
        if (report is not null) ShowReport(report);
        else
        {
            ConnectionState = TenantConnectionState.NotConfigured;
            ConnectionDetail = null;
            OrganizationSummary = null;
            LastSuccessfulConnection = LastValidation = null;
            Readiness.Clear();
            Permissions.Clear();
            foreach (var row in _permissions.Describe(configured)) Permissions.Add(row);
        }

        RefreshAssistant();
    }

    private TenantConfiguration? BuildTenant()
    {
        Guid? tenantId = null;
        if (!string.IsNullOrWhiteSpace(TenantId))
        {
            if (!Guid.TryParseExact(TenantId.Trim(), "D", out var parsedTenant))
            {
                StatusMessage = "Tenant ID must be a GUID (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx).";
                return null;
            }

            tenantId = parsedTenant;
        }

        ApplicationRegistration? registration = null;
        if (!string.IsNullOrWhiteSpace(ClientId))
        {
            if (!Guid.TryParseExact(ClientId.Trim(), "D", out var clientId))
            {
                StatusMessage = "Client ID must be a GUID (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx).";
                return null;
            }

            Guid? servicePrincipal = null;
            if (!string.IsNullOrWhiteSpace(ServicePrincipalId))
            {
                if (!Guid.TryParseExact(ServicePrincipalId.Trim(), "D", out var parsedServicePrincipal))
                {
                    StatusMessage = "Service principal ID must be a GUID or empty.";
                    return null;
                }

                servicePrincipal = parsedServicePrincipal;
            }

            var existing = SelectedCustomer?.Tenants.FirstOrDefault(tenant => string.Equals(tenant.TenantKey, TenantKey, StringComparison.OrdinalIgnoreCase))?.ApplicationRegistration;
            registration = new ApplicationRegistration(clientId, servicePrincipal, AuthenticationType, existing?.SecretReference,
                string.IsNullOrWhiteSpace(CertificateReference) ? null : CertificateReference.Trim(), existing?.CredentialExpirationUtc,
                Capabilities.Where(option => option.IsSelected).Select(option => option.Capability).ToArray());
        }

        return new TenantConfiguration(TenantKey.Trim().ToLowerInvariant(), string.IsNullOrWhiteSpace(TenantDisplayName) ? TenantKey : TenantDisplayName.Trim(),
            string.IsNullOrWhiteSpace(TenantName) ? null : TenantName.Trim(), tenantId, string.IsNullOrWhiteSpace(PrimaryDomain) ? null : PrimaryDomain.Trim().ToLowerInvariant(),
            CloudEnvironment, registration);
    }

    private bool TryParseExpiration(out DateTimeOffset? expiration)
    {
        expiration = null;
        if (string.IsNullOrWhiteSpace(SecretExpiration)) return true;
        if (DateTimeOffset.TryParseExact(SecretExpiration.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            expiration = parsed.ToUniversalTime();
            return true;
        }

        StatusMessage = "Enter the secret expiration as yyyy-MM-dd, or leave it empty.";
        return false;
    }

    private void ShowReport(TenantConnectionReport report)
    {
        ConnectionState = report.State;
        ConnectionDetail = report.Error?.ToString() ?? (report.State == TenantConnectionState.Connected ? "Authentication, Microsoft Graph, tenant identity and permissions validated." : null);
        LastSuccessfulConnection = report.LastSuccessfulConnectionUtc;
        LastValidation = report.LastValidationUtc;
        OrganizationSummary = report.Organization is { } organization
            ? $"{organization.DisplayName} ({organization.TenantId}); default domain {organization.DefaultDomain ?? "unknown"}; {organization.VerifiedDomains.Count} verified domain(s)"
            : null;
        Readiness.Clear();
        var checks = report.Readiness;
        foreach (var (name, state) in new[]
                 {
                     ("Configuration", checks.Configuration), ("Credential", checks.Credential), ("Credential expiration", checks.CredentialExpiration),
                     ("Authentication", checks.Authentication), ("Microsoft Graph", checks.MicrosoftGraph), ("Tenant identity", checks.TenantIdentity),
                     ("Permissions", checks.Permissions), ("Overall", checks.Overall)
                 })
            Readiness.Add(new ReadinessItem(name, state));
        Permissions.Clear();
        foreach (var row in report.Permissions) Permissions.Add(row);
    }

    private void RefreshAssistant()
    {
        AssistantSteps.Clear();
        if (SelectedCustomer is null || BuildTenantQuietly() is not { } tenant) return;
        foreach (var step in _assistant.Describe(SelectedCustomer.Id.Value, tenant, AssistantApplicationName)) AssistantSteps.Add(step);
    }

    private TenantConfiguration? BuildTenantQuietly()
    {
        var message = StatusMessage;
        var tenant = BuildTenant();
        StatusMessage = message;
        return tenant;
    }
}
