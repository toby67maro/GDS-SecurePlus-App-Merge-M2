using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Configuration;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Application.State;
using EngineeringWorkspace.Application.Tenants;
using EngineeringWorkspace.Application.Workspaces;
using EngineeringWorkspace.Configuration.Appearance;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Desktop.Theming;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Customers;
using EngineeringWorkspace.Domain.Workspaces;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Desktop.Shell;

/// <summary>Shell: header, configuration-driven navigation, the current page and the status bar.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly IPageViewModelFactory _pages;
    private readonly IWorkspaceService _workspaces;
    private readonly IWorkspaceStore _workspaceStore;
    private readonly ICustomerContextService _customers;
    private readonly IDatabaseStatusService _database;
    private readonly ITenantConnectionService _tenants;
    private readonly IThemeService _theme;
    private readonly IApplicationStateStore _state;
    private readonly ITenantRegistrationService _customerProfiles;
    private readonly ILogger<ShellViewModel> _logger;
    private CancellationTokenSource? _pageCancellation;
    private bool _syncingTheme;
    private bool _syncingOptions;

    public ShellViewModel(INavigationService navigation, IPageViewModelFactory pages, IWorkspaceService workspaces, ICustomerContextService customers,
        IDatabaseStatusService database, ITenantConnectionService tenants, IThemeService theme, IApplicationStateStore state, IWorkspaceStore workspaceStore, IConfigurationDiagnostics diagnostics,
        ApplicationSettings application, BrandingSettings branding, ITenantRegistrationService customerProfiles, ActivityViewModel activity, ILogger<ShellViewModel> logger)
    {
        Activity = activity;
        Services.WindowZoom.Changed += (_, _) => OnPropertyChanged(nameof(ZoomText));
        _customerProfiles = customerProfiles;
        _navigation = navigation;
        _pages = pages;
        _workspaces = workspaces;
        _workspaceStore = workspaceStore;
        _customers = customers;
        _database = database;
        _tenants = tenants;
        _theme = theme;
        _state = state;
        _logger = logger;
        ApplicationName = branding.ApplicationName;
        WindowTitle = branding.ApplicationTitle;
        CompanyName = branding.CompanyName;
        EnvironmentName = application.Environment;
        Version = application.ApplicationVersion;
        var errors = diagnostics.Issues.Count(issue => issue.Severity == ConfigurationSeverity.Error);
        var warnings = diagnostics.Issues.Count(issue => issue.Severity == ConfigurationSeverity.Warning);
        ConfigurationStatus = errors > 0 ? $"Configuration: {errors} error(s)" : warnings > 0 ? $"Configuration: valid, {warnings} warning(s)" : "Configuration: valid";

        // Services raise events from background jobs too; the shell always handles them on the UI thread.
        _navigation.Navigated += (sender, e) => OnUi(() => OnNavigated(sender, e));
        _navigation.TreeChanged += (_, _) => OnUi(BuildTree);
        _workspaces.WorkspaceChanged += (_, _) => OnUi(() =>
        {
            UpdateContext();
            _ = RefreshOptionsAsync();
        });
        _customers.CustomerChanged += (_, _) => OnUi(() =>
        {
            UpdateContext();
            _ = RefreshOptionsAsync();
        });
        _database.StatusChanged += (_, report) => OnUi(() => DatabaseStatus = FormatDatabase(report));
        _tenants.ConnectionTested += (_, report) => OnUi(() => TenantStatus = $"Tenant: {report.State}");
        _theme.ThemeChanged += (_, _) => OnUi(SyncTheme);
        DatabaseStatus = _database.LatestStatus is { } latest ? FormatDatabase(latest) : $"Database: {_database.Settings.Provider} | {(_database.Settings.Provider == DatabaseProvider.None ? "Not Configured" : "Checking...")}";
        BuildTree();
        UpdateContext();
        SyncTheme();
    }

    public ObservableCollection<NavigationNodeViewModel> Navigation { get; } = [];

    /// <summary>Running tasks, the info bar and the activity panel.</summary>
    public ActivityViewModel Activity { get; }

    public string ZoomText => $"Zoom {Services.WindowZoom.Level:P0}";
    public ObservableCollection<Workspace> WorkspaceOptions { get; } = [];
    public ObservableCollection<CustomerProfile> CustomerOptions { get; } = [];
    public IReadOnlyList<ThemeMode> ThemeModes => _theme.AvailableModes;

    /// <summary>The theme picker: the workspace theme, then every built-in theme.</summary>
    public IReadOnlyList<ThemeOption> ThemeOptions => _themeOptions ??=
        [new ThemeOption(null, "Workspace Theme", "The workspace's stored theme or the appearance properties file.", "Workspace", null, null, null),
         .. _theme.Presets.Select(preset => new ThemeOption(preset.Name, preset.Name, preset.Description, preset.Family, preset.SwatchBackground, preset.SwatchNavigation, preset.SwatchAccent))];

    private IReadOnlyList<ThemeOption>? _themeOptions;

    [ObservableProperty] private ThemeOption? _selectedThemeOption;

    partial void OnSelectedThemeOptionChanged(ThemeOption? value)
    {
        if (!_syncingTheme && value is not null) _ = _theme.SetPresetAsync(value.PresetName);
    }
    public string ApplicationName { get; }
    public string WindowTitle { get; }
    public string CompanyName { get; }
    public string EnvironmentName { get; }

    /// <summary>True for a Test database: an orange TEST banner is shown at the top of the window.</summary>
    public bool IsTestDatabase => string.Equals(EnvironmentName, Application.Database.DatabaseModes.Test, StringComparison.OrdinalIgnoreCase);
    public string Version { get; }
    public string ConfigurationStatus { get; }

    [ObservableProperty] private PageViewModel? _currentPage;
    [ObservableProperty] private string _currentRoute = string.Empty;
    [ObservableProperty] private string _workspaceText = "No workspace";
    [ObservableProperty] private string _customerText = "No customer selected";
    [ObservableProperty] private string _tenantText = "No tenant";
    [ObservableProperty] private string _databaseStatus = string.Empty;
    [ObservableProperty] private string _tenantStatus = "Tenant: Not connected";
    [ObservableProperty] private string? _navigationMessage;
    [ObservableProperty] private string _workspaceInput = string.Empty;
    [ObservableProperty] private Workspace? _selectedWorkspaceOption;
    [ObservableProperty] private CustomerProfile? _selectedCustomerOption;
    [ObservableProperty] private ThemeMode _selectedThemeMode;
    [ObservableProperty] private bool _allowThemeSelection;

    partial void OnSelectedThemeModeChanged(ThemeMode value)
    {
        if (!_syncingTheme) ChangeThemeCommand.Execute(value);
    }

    [RelayCommand]
    private async Task ChangeThemeAsync(ThemeMode mode) => await _theme.SetThemeModeAsync(mode);

    [RelayCommand]
    private async Task NavigateAsync(string route)
    {
        var result = await _navigation.NavigateAsync(route);
        NavigationMessage = result.Succeeded ? null : result.Message;
    }

    /// <summary>Opens the selected workspace, or the one whose name was typed; creates it when no workspace has that name.</summary>
    [RelayCommand]
    private async Task OpenWorkspaceAsync()
    {
        var typed = WorkspaceInput.Trim();
        var target = SelectedWorkspaceOption is { } selected && (typed.Length == 0 || string.Equals(selected.Name, typed, StringComparison.OrdinalIgnoreCase))
            ? selected
            : WorkspaceOptions.FirstOrDefault(workspace => string.Equals(workspace.Name, typed, StringComparison.OrdinalIgnoreCase) ||
                                                           string.Equals(workspace.Id.Value, typed, StringComparison.OrdinalIgnoreCase));
        if (target is null && typed.Length == 0)
        {
            NavigationMessage = "Type a workspace name or choose one from the list.";
            return;
        }

        var result = target is not null ? await _workspaces.OpenAsync(target.Id) : await _workspaces.CreateAsync(typed);
        NavigationMessage = result.Succeeded ? null : result.Error!.ToString();
        if (!result.Succeeded) return;
        if (_customers.CurrentCustomer is { } customer && result.Value!.CurrentCustomerId is null)
            await _workspaces.SetCurrentCustomerAsync(customer.Id, _customers.CurrentTenant?.TenantId);
        else if (result.Value!.CurrentCustomerId is { } workspaceCustomer && _customers.CurrentCustomer?.Id != workspaceCustomer)
            await _customers.SelectAsync(workspaceCustomer, null, "Workspace opened");
        UpdateContext();
    }

    partial void OnSelectedCustomerOptionChanged(CustomerProfile? value)
    {
        if (_syncingOptions || value is null || _customers.CurrentCustomer?.Id == value.Id) return;
        _ = SelectCustomerAsync(value);
    }

    private async Task SelectCustomerAsync(CustomerProfile customer)
    {
        var result = await _customers.SelectAsync(customer.Id, null, "Selected in the header");
        NavigationMessage = result.Succeeded ? null : result.Error!.ToString();
    }

    [RelayCommand]
    private async Task CloseWorkspaceAsync() => await _workspaces.CloseAsync();

    /// <summary>Startup steps 8-10: navigation is resolved; navigate to the restored or default route; check the database read-only.</summary>
    public async Task StartAsync(string? restoredRoute, CancellationToken cancellationToken)
    {
        await RefreshOptionsAsync();
        var result = await _navigation.NavigateToDefaultAsync(restoredRoute, cancellationToken);
        if (!result.Succeeded) NavigationMessage = result.Message;
        if (_database.Settings.Provider != DatabaseProvider.None)
        {
            try
            {
                await Task.Run(() => _database.RefreshAsync(cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Startup database status check cancelled");
            }
        }
    }

    /// <summary>Shutdown step 1: save shell state.</summary>
    public async Task SaveStateAsync(CancellationToken cancellationToken)
    {
        _pageCancellation?.Cancel();
        CurrentPage?.OnNavigatedFrom();
        var state = await _state.LoadAsync(cancellationToken);
        await _state.SaveAsync(state with
        {
            LastRoute = _navigation.CurrentRoute?.Route,
            ZoomLevel = Services.WindowZoom.Level,
            PinnedNavigationIds = Navigation.Where(node => node.IsPinned).Select(node => node.Node.Id).ToArray()
        }, cancellationToken);
    }

    private async void OnNavigated(object? sender, NavigatedEventArgs e)
    {
        // Event handler: exceptions are caught so a failing page never crashes the shell.
        try
        {
            CurrentPage?.OnNavigatedFrom();
            _pageCancellation?.Cancel();
            _pageCancellation = new CancellationTokenSource();
            var page = _pages.Create(e.Current);
            CurrentPage = page;
            CurrentRoute = e.Current.Route;
            SelectRoute(e.Current.Route);
            await page.OnNavigatedToAsync(e.Current, _pageCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Navigation to {Route} superseded", e.Current.Route);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "Page {Route} failed to load", e.Current.Route);
            NavigationMessage = $"The page '{e.Current.DisplayName}' could not be loaded. See the application log.";
        }
    }

    private HashSet<string> _pinned = new(StringComparer.Ordinal);
    private bool _accordionBusy;

    private void BuildTree()
    {
        var expanded = _navigation.State.ExpandedNodeIds;
        Navigation.Clear();
        foreach (var section in _navigation.Sections)
        {
            var sectionNode = Create(section, expanded);
            foreach (var child in section.Children)
            {
                if (child is NavigationGroup { ShowHeader: false } hidden)
                {
                    foreach (var grandChild in hidden.Children) sectionNode.Children.Add(CreateRecursive(grandChild, expanded));
                    continue;
                }

                sectionNode.Children.Add(CreateRecursive(child, expanded));
            }

            Navigation.Add(sectionNode);
        }

        if (_navigation.CurrentRoute is { } current) SelectRoute(current.Route);
    }

    /// <summary>
    /// Applies the pinned sections saved in the application state and sets the launch state of the menu: every
    /// section starts collapsed except the ones the user pinned open (the only expanded state that is remembered).
    /// </summary>
    public void RestorePinned(IEnumerable<string>? pinned)
    {
        _pinned = new HashSet<string>(pinned ?? [], StringComparer.Ordinal);
        _accordionBusy = true;
        try
        {
            foreach (var node in Navigation)
            {
                node.IsPinned = _pinned.Contains(node.Node.Id);
                node.IsExpanded = node.IsPinned;
            }
        }
        finally
        {
            _accordionBusy = false;
        }
    }

    private NavigationNodeViewModel CreateRecursive(NavigationNode node, IReadOnlySet<string> expanded)
    {
        var viewModel = Create(node, expanded);
        foreach (var child in node.Children) viewModel.Children.Add(CreateRecursive(child, expanded));
        return viewModel;
    }

    private NavigationNodeViewModel Create(NavigationNode node, IReadOnlySet<string> expanded)
    {
        var viewModel = new NavigationNodeViewModel(node, expanded.Contains(node.Id), _pinned.Contains(node.Id), OnNodeSelected, OnNodeExpanded);
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NavigationNodeViewModel.IsPinned))
            {
                if (viewModel.IsPinned) _pinned.Add(viewModel.Node.Id);
                else _pinned.Remove(viewModel.Node.Id);
            }
        };
        return viewModel;
    }

    /// <summary>Accordion: expanding a section collapses the other sections unless they are pinned.</summary>
    private void OnNodeExpanded(NavigationNodeViewModel node, bool expanded)
    {
        _navigation.SetExpanded(node.Node.Id, expanded);
        if (!expanded || !node.IsSection || _accordionBusy) return;
        _accordionBusy = true;
        try
        {
            foreach (var other in Navigation.Where(other => other != node && other.IsExpanded && !other.IsPinned && !other.IsActive))
                other.IsExpanded = false;
        }
        finally
        {
            _accordionBusy = false;
        }
    }

    private void OnNodeSelected(NavigationNodeViewModel node)
    {
        if (node.Route is { } route && !string.Equals(route, CurrentRoute, StringComparison.Ordinal)) NavigateCommand.Execute(route);
    }

    private void SelectRoute(string route)
    {
        foreach (var section in Navigation)
        {
            var nodes = new[] { section }.Concat(section.Descendants()).ToArray();
            var contains = false;
            foreach (var node in nodes)
            {
                if (node.IsItem && node.Route == route)
                {
                    contains = true;
                    if (!node.IsSelected) node.IsSelected = true;
                }
            }

            section.IsActive = contains;
            foreach (var group in section.Descendants().Where(child => child.IsGroup))
                group.IsActive = group.Descendants().Any(child => child.Route == route);
            if (contains && !section.IsExpanded) section.IsExpanded = true;
        }
    }

    private void UpdateContext()
    {
        WorkspaceText = _workspaces.CurrentWorkspace?.Name ?? "No workspace";
        CustomerText = _customers.CurrentCustomer?.Name ?? "No customer selected";
        var tenant = _customers.CurrentTenant;
        TenantText = tenant is null ? "No tenant" : tenant.PrimaryDomain ?? tenant.TenantName ?? tenant.DisplayName;
        TenantStatus = tenant is null ? "Tenant: Not connected" : $"Tenant: {(_customers.CurrentCustomer is { } customer ? _tenants.GetLastReport(customer.Id.Value, tenant.TenantKey)?.State.ToString() : null) ?? "Not tested"}";
    }

    /// <summary>Reloads the customer and workspace pickers (after setup, and whenever the context changes).</summary>
    public async Task RefreshOptionsAsync()
    {
        try
        {
            var workspaces = await _workspaceStore.ListAsync();
            var customers = await _customerProfiles.ListCustomersAsync();
            _syncingOptions = true;
            WorkspaceOptions.Clear();
            foreach (var workspace in workspaces) WorkspaceOptions.Add(workspace);
            CustomerOptions.Clear();
            foreach (var customer in customers) CustomerOptions.Add(customer);
            SelectedWorkspaceOption = _workspaces.CurrentWorkspace is { } current ? WorkspaceOptions.FirstOrDefault(workspace => workspace.Id == current.Id) : null;
            WorkspaceInput = SelectedWorkspaceOption?.Name ?? string.Empty;
            SelectedCustomerOption = _customers.CurrentCustomer is { } currentCustomer ? CustomerOptions.FirstOrDefault(profile => profile.Id == currentCustomer.Id) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.LogWarning(exception, "Workspace or customer list could not be loaded");
        }
        finally
        {
            _syncingOptions = false;
        }
    }

    private void SyncTheme()
    {
        _syncingTheme = true;
        SelectedThemeMode = _theme.CurrentMode;
        SelectedThemeOption = ThemeOptions.FirstOrDefault(option => option.PresetName == _theme.CurrentPresetName) ?? ThemeOptions[0];
        AllowThemeSelection = _theme.AllowUserSelection;
        _syncingTheme = false;
    }

    /// <summary>Runs on the UI thread (immediately when already on it).</summary>
    internal static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    private static string FormatDatabase(DatabaseStatusReport report) => $"Database: {report.Provider} | {report.State}";
}

/// <summary>One entry of the theme picker (PresetName null: the workspace / properties-file theme).</summary>
public sealed record ThemeOption(string? PresetName, string Name, string Description, string Family, string? Background, string? Navigation, string? Accent)
{
    public bool HasSwatch => Background is not null;
}
