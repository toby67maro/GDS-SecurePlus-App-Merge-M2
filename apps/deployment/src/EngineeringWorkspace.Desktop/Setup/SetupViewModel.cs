using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Application.State;
using EngineeringWorkspace.Application.Tenants;
using EngineeringWorkspace.Application.Jobs;
using EngineeringWorkspace.Application.Workspaces;
using EngineeringWorkspace.Desktop.Pages.Database;
using EngineeringWorkspace.Domain.Customers;
using EngineeringWorkspace.Domain.Workspaces;

namespace EngineeringWorkspace.Desktop.Setup;

public enum SetupStep
{
    Database,
    Mode,
    Customer,
    Tenant,
    Assessment,
    Workspace,
    Finish,
    Complete
}

/// <summary>
/// First-run setup. The application is not installed until its database is installed: (1) database connection,
/// creation, schema and permissions; (2) the first customer profile; (3) the customer's assessment history in the
/// database, offering the initial assessment; (4) the workspace; (5) sample data when nothing else exists.
/// </summary>
public sealed partial class SetupViewModel : ObservableObject
{
    private readonly IDatabaseStatusService _database;
    private readonly ITenantRegistrationService _customers;
    private readonly ICustomerContextService _customerContext;
    private readonly IWorkspaceStore _workspaceStore;
    private readonly IWorkspaceService _workspaces;
    private readonly IEngagementService _engagements;
    private readonly IApplicationStateStore _state;
    private readonly ApplicationSettings _application;
    private readonly ITenantOnboardingService _onboarding;
    private readonly Application.Jobs.IJobService _jobs;
    private readonly IAssessmentService _assessments;
    private readonly Application.Catalog.ICatalogSyncService _seed;
    private readonly Application.Configuration.ISharedSettingsStore _settings;
    private readonly Stack<SetupStep> _history = new();
    private CustomerEngagementState? _customerState;

    public SetupViewModel(IDatabaseStatusService database, DatabaseConfigurationViewModel databaseStep, ITenantRegistrationService customers,
        ICustomerContextService customerContext, IWorkspaceStore workspaceStore, IWorkspaceService workspaces, IEngagementService engagements,
        IApplicationStateStore state, ApplicationSettings application, ITenantOnboardingService onboarding, Application.Jobs.IJobService jobs, IAssessmentService assessments,
        Application.Catalog.ICatalogSyncService seed, Application.Configuration.ISharedSettingsStore settings, Pages.Credentials.OrganizationKeyPanel organizationKey)
    {
        _settings = settings;
        OrganizationKey = organizationKey;
        _seed = seed;
        _onboarding = onboarding;
        _jobs = jobs;
        _assessments = assessments;
        _database = database;
        DatabaseStep = databaseStep;
        _customers = customers;
        _customerContext = customerContext;
        _workspaceStore = workspaceStore;
        _workspaces = workspaces;
        _engagements = engagements;
        _state = state;
        _application = application;
        DatabaseStep.Title = "Database";
        DatabaseStep.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DatabaseConfigurationViewModel.IsBusy)) IsBusy = DatabaseStep.IsBusy;
        };
        StatusMessage = database.Settings.Provider == DatabaseProvider.None
            ? "You don't have a database connection setup. Would you like to setup one now? Choose the provider and server below; Secure Plus Agent connects, checks tables and permissions, creates the database if needed and installs or repairs the schema."
            : $"Secure Plus Agent could not confirm that the database '{database.Settings.DatabaseName}' on {database.Settings.DisplayServer} is ready" +
              (database.LatestStatus is { } latest ? $" ({latest.State}: {latest.SanitizedError})" : string.Empty) +
              ". Review the settings below and select Check and install database.";
    }

    /// <summary>The full Database Configuration form, reused so every provider and authentication method is available.</summary>
    public DatabaseConfigurationViewModel DatabaseStep { get; }

    public ObservableCollection<CustomerProfile> CustomerProfiles { get; } = [];
    public ObservableCollection<Workspace> Workspaces { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDatabaseStep), nameof(IsModeStep), nameof(IsCustomerStep), nameof(IsTenantStep), nameof(IsAssessmentStep), nameof(IsWorkspaceStep), nameof(IsFinishStep), nameof(IsComplete),
        nameof(StepTitle), nameof(StepNumber), nameof(CanGoBack))]
    private SetupStep _step = SetupStep.Database;

    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _customerName = string.Empty;
    [ObservableProperty] private CustomerProfile? _selectedCustomer;
    [ObservableProperty] private bool _hasCustomers;
    [ObservableProperty] private string _assessmentQuestion = string.Empty;
    [ObservableProperty] private bool _runInitialAssessment = true;
    [ObservableProperty] private string _workspaceName = string.Empty;
    [ObservableProperty] private Workspace? _selectedWorkspace;
    [ObservableProperty] private bool _hasWorkspaces;
    [ObservableProperty] private bool _loadSampleData;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private double _seedProgress;
    [ObservableProperty] private bool _isSeeding;
    [ObservableProperty] private string? _seedSummary;

    public bool IsDatabaseStep => Step == SetupStep.Database;
    public bool IsCustomerStep => Step == SetupStep.Customer;
    public bool IsTenantStep => Step == SetupStep.Tenant;

    [ObservableProperty] private string? _tenantProgress;
    [ObservableProperty] private bool _isRegistering;
    [ObservableProperty] private bool _tenantConnected;
    public bool IsAssessmentStep => Step == SetupStep.Assessment;
    public bool IsWorkspaceStep => Step == SetupStep.Workspace;
    public bool IsFinishStep => Step == SetupStep.Finish;
    public bool IsComplete => Step == SetupStep.Complete;
    public bool CanGoBack => _history.Count > 0 && Step != SetupStep.Complete;
    public string StepNumber => Step == SetupStep.Complete ? "Done" : $"Step {(int)Step + 1} of 7";

    public Pages.Credentials.OrganizationKeyPanel OrganizationKey { get; }
    public bool IsModeStep => Step == SetupStep.Mode;

    /// <summary>The mode stored in the database (set by the first computer), or null when this setup chooses it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeIsChosen))]
    private string? _databaseMode;

    [ObservableProperty] private bool _chooseProduction;
    [ObservableProperty] private bool _chooseTest;
    [ObservableProperty] private string _modeText = string.Empty;

    public bool ModeIsChosen => DatabaseMode is not null;

    /// <summary>Sample data is offered only for Test databases.</summary>
    public bool SampleDataAllowed => (DatabaseMode ?? (ChooseTest ? Application.Database.DatabaseModes.Test : null)) != Application.Database.DatabaseModes.Production;

    partial void OnChooseTestChanged(bool value) => OnPropertyChanged(nameof(SampleDataAllowed));
    partial void OnChooseProductionChanged(bool value) => OnPropertyChanged(nameof(SampleDataAllowed));

    public string StepTitle => Step switch
    {
        SetupStep.Database => "Check for database access",
        SetupStep.Mode => "Production or Test, and the organization key",
        SetupStep.Customer => "Customer profile",
        SetupStep.Tenant => "Connect the Microsoft 365 tenant",
        SetupStep.Assessment => "Initial assessment",
        SetupStep.Workspace => "Workspace",
        SetupStep.Finish => "Finish setup",
        _ => "Setup complete"
    };

    partial void OnSelectedCustomerChanged(CustomerProfile? value)
    {
        if (value is not null) CustomerName = string.Empty;
    }

    partial void OnCustomerNameChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) SelectedCustomer = null;
    }

    partial void OnSelectedWorkspaceChanged(Workspace? value)
    {
        if (value is not null) WorkspaceName = string.Empty;
    }

    partial void OnWorkspaceNameChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) SelectedWorkspace = null;
    }

    /// <summary>Step 1: install or repair the database; the wizard continues only when it is Ready.</summary>
    [RelayCommand]
    private async Task CheckDatabaseAsync()
    {
        var report = await DatabaseStep.InstallAsync();
        if (report is null)
        {
            StatusMessage = DatabaseStep.StatusMessage ?? "Correct the database settings, then try again.";
            return;
        }

        if (report.State != DatabaseOverallState.Ready)
        {
            StatusMessage = $"The database is not ready yet: {DatabaseStep.StatusMessage}";
            return;
        }

        IsBusy = true;
        try
        {
            // Import the reference data shipped with the application (idempotent: only what changed is written).
            IsSeeding = true;
            SeedProgress = 0;
            StatusMessage = "Importing the reference data (controls, frameworks, policy sets, specifications and baselines)...";
            try
            {
                SeedSummary = await _seed.SeedNowAsync(false, new Progress<(double Progress, string Activity)>(update =>
                {
                    SeedProgress = update.Progress;
                    StatusMessage = update.Activity;
                }));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
            {
                SeedSummary = $"The reference data could not be imported: {exception.Message} It is retried automatically when the application starts.";
            }
            finally
            {
                IsSeeding = false;
            }

            await _engagements.SynchronizeAsync();
            var properties = _settings.IsAvailable ? await _settings.GetPropertiesAsync() : new Dictionary<string, string?>();
            DatabaseMode = properties.TryGetValue(Application.Database.DatabaseModes.PropertyKey, out var mode) && Application.Database.DatabaseModes.IsValid(mode) ? mode : null;
            ModeText = DatabaseMode is null
                ? "Is this the database your team uses for real customers (Production), or one for trying things out and training (Test)? Every computer that connects to it uses the same answer."
                : $"This database is a {DatabaseMode} database, chosen when it was first set up. Every computer that connects to it uses the same mode.";
            await OrganizationKey.RefreshAsync();
            GoTo(SetupStep.Mode);
            StatusMessage = "The database is installed and the reference data imported.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Step 2: store the database mode (first computer only) and continue; the organization key can also be set up later on Credential Management.</summary>
    [RelayCommand]
    private async Task ContinueFromModeAsync()
    {
        if (DatabaseMode is null)
        {
            if (!ChooseProduction && !ChooseTest)
            {
                StatusMessage = "Choose Production or Test.";
                return;
            }

            DatabaseMode = ChooseProduction ? Application.Database.DatabaseModes.Production : Application.Database.DatabaseModes.Test;
            await _settings.SetPropertyAsync(Application.Database.DatabaseModes.PropertyKey, DatabaseMode, Environment.UserName);
        }

        await LoadCustomersAsync();
        GoTo(SetupStep.Customer);
        StatusMessage = (HasCustomers
                            ? "Select the customer profile to work with, or add a new one."
                            : "You don't have a customer profile added. Would you like to add one now?") +
                        (OrganizationKey.IsReady ? string.Empty : " Tenant connections are shared once the organization key is set up (Credential Management).");
        OnPropertyChanged(nameof(SampleDataAllowed));
    }

    /// <summary>Step 2: select or create the customer, then read its history from the database.</summary>
    [RelayCommand]
    private async Task SaveCustomerAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var profile = SelectedCustomer;
            if (profile is null)
            {
                var created = await _customers.CreateCustomerAsync(CustomerName);
                if (!created.Succeeded)
                {
                    StatusMessage = created.Error!.ToString();
                    return;
                }

                profile = created.Value!;
            }

            var selected = await _customerContext.SelectAsync(profile.Id, null, "Selected during first-run setup");
            if (!selected.Succeeded)
            {
                StatusMessage = selected.Error!.ToString();
                return;
            }

            if (profile.PrimaryTenant?.ApplicationRegistration?.SecretReference is not null)
            {
                TenantConnected = true;
                await ContinueAfterTenantAsync(profile);
                return;
            }

            TenantConnected = false;
            TenantProgress = null;
            StatusMessage = $"Connect {profile.Name}'s Microsoft 365 tenant: sign in once as a Global Administrator and Secure Plus Agent registers and consents the 'GDS Secure Plus Agent' app automatically.";
            GoTo(SetupStep.Tenant);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Step 3: automatic app registration with one administrator sign-in (runs as a background job).</summary>
    [RelayCommand]
    private void RegisterTenant()
    {
        if (_customerContext.CurrentCustomer is not { } customer || IsRegistering) return;
        IsRegistering = true;
        TenantProgress = "A Microsoft sign-in opens in your browser. Sign in as a Global Administrator of the customer's tenant.";
        var job = _onboarding.RegisterAutomatically(customer.Id, null, Domain.Tenants.CloudEnvironment.Commercial);
        void OnChanged(object? sender, Application.Jobs.JobSnapshot changed)
        {
            if (changed.Id != job.Id) return;
            Shell.ShellViewModel.OnUi(async () =>
            {
                TenantProgress = changed.IsFinished ? changed.Result ?? changed.Error : changed.CurrentActivity;
                if (!changed.IsFinished) return;
                _jobs.JobChanged -= OnChanged;
                IsRegistering = false;
                if (changed.State != Application.Jobs.JobState.Succeeded)
                {
                    StatusMessage = $"The automatic registration did not complete: {changed.Error}. Try again, or skip and connect later on Tenant App Registration.";
                    return;
                }

                TenantConnected = true;
                StatusMessage = changed.Result ?? "The tenant is connected.";
                if (await _customers.ListCustomersAsync() is var all && all.FirstOrDefault(item => item.Id == customer.Id) is { } updated)
                    await _customerContext.SelectAsync(updated.Id, null, "Tenant connected during setup");
            });
        }

        _jobs.JobChanged += OnChanged;
    }

    [RelayCommand]
    private async Task ContinueFromTenantAsync()
    {
        if (_customerContext.CurrentCustomer is not { } customer || IsRegistering) return;
        await ContinueAfterTenantAsync(customer);
    }

    private async Task ContinueAfterTenantAsync(CustomerProfile profile)
    {
        IsBusy = true;
        try
        {
            var state = await _engagements.GetCustomerStateAsync(profile.Id);
            _customerState = state.Succeeded ? state.Value : null;
            await LoadWorkspacesAsync(profile);
            if (_customerState is { HasAssessment: true })
            {
                RunInitialAssessment = false;
                var latest = _customerState.LatestAssessment!;
                StatusMessage = $"{profile.Name} already has {_customerState.AssessmentCount} assessment(s) in the database; the latest is '{latest.Name}' ({latest.StatusCode}).";
                GoTo(SetupStep.Workspace);
                return;
            }

            AssessmentQuestion = $"The database has no assessment, scan or audit for {profile.Name}" +
                                 (HasWorkspaces ? "." : ", and no workspace has been built for it yet; one is created in the next step.") +
                                 " Would you like to do the initial Assessment scan now?" +
                                 (TenantConnected ? " It runs in the background and you can follow it in the activity panel." : " It runs as soon as the tenant is connected.");
            RunInitialAssessment = true;
            StatusMessage = AssessmentQuestion;
            GoTo(SetupStep.Assessment);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Step 2 alternative: continue without a customer (sample data is offered at the end).</summary>
    [RelayCommand]
    private void SkipCustomer()
    {
        LoadSampleData = SampleDataAllowed;
        UpdateSummary();
        StatusMessage = SampleDataAllowed
            ? "No customer profile was added. You can load the sample customer, workspace and assessment now, or add customers later."
            : "No customer profile was added. Add customers on Customer Profiles (sample data is not loaded into a Production database).";
        GoTo(SetupStep.Finish);
    }

    /// <summary>Step 3: answer the initial-assessment question.</summary>
    [RelayCommand]
    private void ContinueAssessment()
    {
        StatusMessage = HasWorkspaces
            ? "Select the customer's workspace or create a new one."
            : "Create the workspace for this customer.";
        GoTo(SetupStep.Workspace);
    }

    /// <summary>Step 4: open or create the workspace, and record the initial assessment request.</summary>
    [RelayCommand]
    private async Task SaveWorkspaceAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var result = SelectedWorkspace is not null
                ? await _workspaces.OpenAsync(SelectedWorkspace.Id)
                : await _workspaces.CreateAsync(WorkspaceName);
            if (!result.Succeeded)
            {
                StatusMessage = result.Error!.ToString();
                return;
            }

            if (_customerContext.CurrentCustomer is { } customer)
            {
                await _workspaces.SetCurrentCustomerAsync(customer.Id, _customerContext.CurrentTenant?.TenantId);
                if (RunInitialAssessment)
                {
                    var requested = await _engagements.RequestInitialAssessmentAsync(customer.Id, result.Value!.Id);
                    if (requested.Succeeded && TenantConnected) _assessments.Queue(customer.Id, requested.Value!.AssessmentRunId, "Initial assessment from setup");
                    StatusMessage = !requested.Succeeded ? requested.Error!.ToString()
                        : TenantConnected ? $"The initial assessment of {customer.Name} is running in the background."
                        : $"The initial assessment of {customer.Name} is recorded as Pending; it runs automatically once the tenant is connected.";
                }
                else
                {
                    StatusMessage = $"Workspace '{result.Value!.Name}' is ready for {customer.Name}.";
                }
            }

            var allCustomers = await _customers.ListCustomersAsync();
            LoadSampleData = allCustomers.Count == 0 && SampleDataAllowed;
            UpdateSummary();
            GoTo(SetupStep.Finish);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Step 5: optionally load sample data, record that setup completed, and close.</summary>
    [RelayCommand]
    private async Task FinishAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var customers = await _customers.ListCustomersAsync();
            var workspaces = await _workspaceStore.ListAsync();
            if (SampleDataAllowed && (LoadSampleData || customers.Count == 0 || workspaces.Count == 0))
            {
                var sample = await _engagements.LoadSampleDataAsync();
                if (!sample.Succeeded)
                {
                    StatusMessage = sample.Error!.ToString();
                    return;
                }

                StatusMessage = $"Sample customer '{sample.Value!.Customer.Name}' and workspace '{sample.Value.Workspace.Name}' were loaded" +
                                (sample.Value.Assessment is { } assessment ? $" with the completed assessment '{assessment.Name}' ({assessment.Score:0.#}%)." : ".");
            }
            else
            {
                StatusMessage = "Installation is complete. The application is ready.";
            }

            var state = await _state.LoadAsync();
            await _state.SaveAsync(state with { SetupCompletedVersion = _application.ApplicationVersion });
            UpdateSummary();
            GoTo(SetupStep.Complete);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (_history.Count == 0 || IsBusy) return;
        Step = _history.Pop();
        OnPropertyChanged(nameof(CanGoBack));
        StatusMessage = Step switch
        {
            SetupStep.Database => "Review the database settings.",
            SetupStep.Customer => "Select the customer profile to work with, or add a new one.",
            SetupStep.Tenant => "Connect the customer's Microsoft 365 tenant, or skip and do it later.",
            SetupStep.Assessment => AssessmentQuestion,
            SetupStep.Workspace => "Select the customer's workspace or create a new one.",
            _ => StatusMessage
        };
    }

    private void GoTo(SetupStep step)
    {
        if (step == Step) return;
        _history.Push(Step);
        Step = step;
        OnPropertyChanged(nameof(CanGoBack));
    }

    private void UpdateSummary()
    {
        var report = _database.LatestStatus;
        Summary = string.Join(Environment.NewLine, new[]
        {
            report is null ? null : $"Database: {report.Provider} {report.Server}/{report.Database} - {report.State}, schema version {report.CurrentSchemaVersion}",
            $"Customer: {_customerContext.CurrentCustomer?.Name ?? "none selected"}",
            $"Workspace: {_workspaces.CurrentWorkspace?.Name ?? "none"}",
            _customerState is { HasAssessment: true } state ? $"Assessments on record: {state.AssessmentCount}" : RunInitialAssessment && _customerContext.CurrentCustomer is not null ? "Initial assessment: requested (Pending)" : null
        }.Where(line => line is not null));
    }

    private async Task LoadCustomersAsync()
    {
        CustomerProfiles.Clear();
        foreach (var profile in await _customers.ListCustomersAsync()) CustomerProfiles.Add(profile);
        HasCustomers = CustomerProfiles.Count > 0;
        SelectedCustomer = _customerContext.CurrentCustomer is { } current ? CustomerProfiles.FirstOrDefault(profile => profile.Id == current.Id) : CustomerProfiles.FirstOrDefault();
    }

    private async Task LoadWorkspacesAsync(CustomerProfile customer)
    {
        Workspaces.Clear();
        var all = await _workspaceStore.ListAsync();
        foreach (var workspace in all.Where(workspace => workspace.CurrentCustomerId == customer.Id).Concat(all.Where(workspace => workspace.CurrentCustomerId != customer.Id)))
            Workspaces.Add(workspace);
        HasWorkspaces = all.Any(workspace => workspace.CurrentCustomerId == customer.Id);
        SelectedWorkspace = HasWorkspaces ? Workspaces.First() : null;
        if (!HasWorkspaces) WorkspaceName = customer.Name;
    }
}
