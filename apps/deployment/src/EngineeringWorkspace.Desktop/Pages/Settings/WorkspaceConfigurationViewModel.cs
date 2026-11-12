using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Configuration;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Application.Themes;
using EngineeringWorkspace.Application.Workspaces;
using EngineeringWorkspace.Configuration.Appearance;
using EngineeringWorkspace.Contracts.Export;
using EngineeringWorkspace.Desktop.Bootstrap;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Desktop.Services;
using EngineeringWorkspace.Desktop.Theming;
using EngineeringWorkspace.Domain.Auditing;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Workspaces;

namespace EngineeringWorkspace.Desktop.Pages.Settings;

/// <summary>One setting shown in plain language: category, name, value and which computers it applies to.</summary>
public sealed partial class WorkspaceSettingRow : ObservableObject
{
    public WorkspaceSettingRow(string section, string key, string? value, bool changed, string appliesTo, string? changedBy)
    {
        Section = section;
        Key = key;
        OriginalValue = value ?? string.Empty;
        _value = OriginalValue;
        Changed = changed;
        AppliesTo = appliesTo;
        ChangedBy = changedBy;
    }

    public string Section { get; }
    public string Key { get; }
    public string SettingKey => $"{Section}:{Key}";
    public string Category => SettingLabels.Category(Section);
    public string Setting => SettingLabels.Name(Section, Key);
    public string AppliesTo { get; }
    public bool Changed { get; }
    public string Source => Changed ? "Changed" : "Default";
    public string? ChangedBy { get; }
    public string OriginalValue { get; private set; }
    public bool IsBoolean => OriginalValue is "true" or "false" or "True" or "False";
    public bool IsModified => !string.Equals(Value.Trim(), OriginalValue, StringComparison.Ordinal);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private string _value;

    [ObservableProperty] private string? _error;

    public void MarkSaved()
    {
        OriginalValue = Value.Trim();
        OnPropertyChanged(nameof(IsModified));
    }
}

/// <summary>Plain-language names for setting sections and keys.</summary>
public static partial class SettingLabels
{
    private static readonly Dictionary<string, string> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Application"] = "Application", ["Paths"] = "Folders (This Computer)", ["Workspace"] = "Workspaces", ["ImportExport"] = "Import and Export",
        ["Credentials"] = "Credentials", ["Branding"] = "Branding", ["Features"] = "Features", ["Repositories"] = "Repositories", ["Integrations"] = "Integrations",
        ["Logging"] = "Logging (This Computer)", ["GoldenTenant"] = "Baselines", ["Appearance"] = "Appearance",
    };

    public static string Category(string section) => Categories.GetValueOrDefault(section.Split(':')[0]) ?? Words(section.Split(':')[0]);

    public static string Name(string section, string key)
    {
        var parts = section.Split(':').Skip(1).Select(Words).Append(Words(key));
        return string.Join(" – ", parts);
    }

    public static string Words(string identifier) => SplitWords().Replace(identifier, " $1").Replace("Import Export", "Import and Export", StringComparison.Ordinal).Trim();

    [GeneratedRegex("(?<=[a-z0-9])([A-Z])")]
    private static partial Regex SplitWords();
}

/// <summary>A row of the import preview.</summary>
public sealed record ImportPreviewRow(string File, string Setting, string Current, string Imported, string Change, bool WillApply);

/// <summary>A workspace in the list.</summary>
public sealed record WorkspaceRow(Workspace Workspace, string Customer, string Theme, bool IsOpen)
{
    public string Name => Workspace.Name;
    public string Description => Workspace.Description ?? string.Empty;
    public string Modified => Workspace.ModifiedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    public string State => IsOpen ? "Open" : string.Empty;
}

/// <summary>A theme in the list.</summary>
public sealed record ThemeRow(ThemeRecord Theme, string Mode, bool IsDefault, int Workspaces)
{
    public string Name => Theme.Name;
    public string Type => Theme.IsSystem ? "GDS" : "Custom";
    public string Default => IsDefault ? "Default" : string.Empty;
}

/// <summary>
/// Workspace → Settings → Workspace Configuration. Workspaces: create, edit (name, description, customer, theme), open
/// and archive. Themes: create from a few colours, preview, set the default theme, delete. Settings: every non-secret
/// setting in plain language (category, setting, value, which computers it applies to) with in-place editing, JSON
/// export and JSON import with validation and preview. Everything is stored in the SecurePlus database and shared
/// with every computer (folders and logging apply to this computer only).
/// </summary>
public sealed partial class WorkspaceConfigurationViewModel : PageViewModel
{
    private const string DefaultThemeOption = "(Default Theme)";
    private const string NoCustomer = "(None)";

    private static readonly HashSet<string> ExcludedSections = new(StringComparer.OrdinalIgnoreCase) { "Database", "Navigation", "Bootstrap" };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly IConfigurationDiagnostics _diagnostics;
    private readonly IConfigurationFileWriter _writer;
    private readonly IRedactionService _redaction;
    private readonly IWorkspaceService _workspaces;
    private readonly IWorkspaceStore _workspaceStore;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly ICustomerProfileStore _customers;
    private readonly IThemeRepository _themes;
    private readonly IThemeService _theme;
    private readonly WorkspaceThemeCoordinator _themeCoordinator;
    private readonly ISharedSettingsStore _settings;
    private readonly IAuditService _audit;
    private readonly AuditContext _auditContext;
    private readonly IDialogService _dialogs;
    private IReadOnlyList<ImportPreviewRow> _pendingImport = [];
    private IReadOnlyList<ThemeRecord> _themeRecords = [];
    private string? _defaultTheme;

    public WorkspaceConfigurationViewModel(IConfigurationDiagnostics diagnostics, IConfigurationFileWriter writer, IRedactionService redaction, IWorkspaceService workspaces,
        IWorkspaceStore workspaceStore, IWorkspaceRepository workspaceRepository, ICustomerProfileStore customers, IThemeRepository themes, IThemeService theme, WorkspaceThemeCoordinator themeCoordinator,
        ISharedSettingsStore settings, IAuditService audit, AuditContext auditContext, IDialogService dialogs)
    {
        _diagnostics = diagnostics;
        _writer = writer;
        _redaction = redaction;
        _workspaces = workspaces;
        _workspaceStore = workspaceStore;
        _workspaceRepository = workspaceRepository;
        _customers = customers;
        _themes = themes;
        _theme = theme;
        _themeCoordinator = themeCoordinator;
        _settings = settings;
        _audit = audit;
        _auditContext = auditContext;
        _dialogs = dialogs;
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = item => item is WorkspaceSettingRow row && (SelectedCategory == AllCategories || row.Category == SelectedCategory) && (!ChangedOnly || row.Changed || row.IsModified) &&
                                  (string.IsNullOrWhiteSpace(Search) || row.Setting.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase) ||
                                   row.Category.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase) || row.Value.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public const string AllCategories = "(All)";
    public ObservableCollection<WorkspaceSettingRow> Rows { get; } = [];
    public ICollectionView RowsView { get; }
    public ObservableCollection<string> Categories { get; } = [AllCategories];
    public ObservableCollection<ImportPreviewRow> Preview { get; } = [];
    public ObservableCollection<WorkspaceRow> WorkspaceRows { get; } = [];
    public ObservableCollection<ThemeRow> ThemeRows { get; } = [];
    public ObservableCollection<string> CustomerOptions { get; } = [];
    public ObservableCollection<string> ThemeOptions { get; } = [];
    public IReadOnlyList<ThemeMode> ThemeModes { get; } = [ThemeMode.Light, ThemeMode.Dark, ThemeMode.System];

    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private string _selectedCategory = AllCategories;
    [ObservableProperty] private bool _changedOnly;
    [ObservableProperty] private bool _restartRequired;
    [ObservableProperty] private bool _isPreviewOpen;
    [ObservableProperty] private string _previewCaption = string.Empty;
    [ObservableProperty] private string _loadedCaption = string.Empty;
    [ObservableProperty] private string _storageCaption = string.Empty;

    // Workspace editor.
    [ObservableProperty] private bool _isWorkspaceEditorOpen;
    [ObservableProperty] private string? _editingWorkspaceId;
    [ObservableProperty] private string _workspaceName = string.Empty;
    [ObservableProperty] private string _workspaceDescription = string.Empty;
    [ObservableProperty] private string _workspaceCustomer = NoCustomer;
    [ObservableProperty] private string _workspaceTheme = DefaultThemeOption;
    [ObservableProperty] private string? _workspaceMessage;
    [ObservableProperty] private bool _openAfterSave = true;
    [ObservableProperty] private WorkspaceRow? _selectedWorkspace;

    // Theme editor.
    [ObservableProperty] private bool _isThemeEditorOpen;
    [ObservableProperty] private string _themeName = string.Empty;
    [ObservableProperty] private ThemeMode _themeMode = ThemeMode.Light;
    [ObservableProperty] private string _themePrimary = string.Empty;
    [ObservableProperty] private string _themeAccent = string.Empty;
    [ObservableProperty] private string _themeHeader = string.Empty;
    [ObservableProperty] private string _themeNavigation = string.Empty;
    [ObservableProperty] private string? _themeMessage;
    [ObservableProperty] private ThemeRow? _selectedTheme;

    public string WorkspaceEditorTitle => EditingWorkspaceId is null ? "New Workspace" : "Edit Workspace";
    public int ModifiedCount => Rows.Count(row => row.IsModified);

    partial void OnEditingWorkspaceIdChanged(string? value) => OnPropertyChanged(nameof(WorkspaceEditorTitle));
    partial void OnSearchChanged(string value) => RowsView.Refresh();
    partial void OnSelectedCategoryChanged(string value) => RowsView.Refresh();
    partial void OnChangedOnlyChanged(bool value) => RowsView.Refresh();

    public override async Task OnNavigatedToAsync(RouteDescriptor route, CancellationToken cancellationToken)
    {
        await base.OnNavigatedToAsync(route, cancellationToken);
        StorageCaption = _settings.IsAvailable
            ? $"Stored in the SecurePlus database and shared with every computer. Folders and logging apply to this computer ({_settings.ComputerName}) only."
            : "The database is not ready yet: changes are kept on this computer and moved into the database once it is set up.";
        await LoadThemesAsync();
        await LoadWorkspacesAsync();
        await LoadSettingsAsync();
    }

    // ----- Workspaces -----

    private async Task LoadWorkspacesAsync()
    {
        WorkspaceRows.Clear();
        var customers = await _customers.ListAsync();
        CustomerOptions.Clear();
        CustomerOptions.Add(NoCustomer);
        foreach (var customer in customers.OrderBy(customer => customer.Name, StringComparer.CurrentCultureIgnoreCase)) CustomerOptions.Add(customer.Name);
        foreach (var workspace in await _workspaceStore.ListAsync())
        {
            var customer = customers.FirstOrDefault(item => item.Id == workspace.CurrentCustomerId)?.Name ?? NoCustomer;
            var theme = _themeRecords.FirstOrDefault(item => item.ThemeKey == workspace.ThemeKey)?.Name ?? DefaultThemeOption;
            WorkspaceRows.Add(new WorkspaceRow(workspace, customer, theme, _workspaces.CurrentWorkspace?.Id == workspace.Id));
        }

        foreach (var row in ThemeRows.ToArray())
            ThemeRows[ThemeRows.IndexOf(row)] = row with { Workspaces = WorkspaceRows.Count(workspace => workspace.Workspace.ThemeKey == row.Theme.ThemeKey) };
    }

    [RelayCommand]
    private void NewWorkspace()
    {
        EditingWorkspaceId = null;
        WorkspaceName = string.Empty;
        WorkspaceDescription = string.Empty;
        WorkspaceCustomer = NoCustomer;
        WorkspaceTheme = DefaultThemeOption;
        WorkspaceMessage = null;
        OpenAfterSave = true;
        IsWorkspaceEditorOpen = true;
    }

    [RelayCommand]
    private async Task EditWorkspaceAsync(WorkspaceRow? row)
    {
        if (row is null) return;
        var customers = await _customers.ListAsync();
        EditingWorkspaceId = row.Workspace.Id.Value;
        WorkspaceName = row.Workspace.Name;
        WorkspaceDescription = row.Workspace.Description ?? string.Empty;
        WorkspaceCustomer = customers.FirstOrDefault(customer => customer.Id == row.Workspace.CurrentCustomerId)?.Name ?? NoCustomer;
        WorkspaceTheme = row.Theme;
        WorkspaceMessage = null;
        OpenAfterSave = row.IsOpen;
        IsWorkspaceEditorOpen = true;
    }

    [RelayCommand]
    private void CancelWorkspace() => IsWorkspaceEditorOpen = false;

    [RelayCommand]
    private async Task SaveWorkspaceAsync()
    {
        if (string.IsNullOrWhiteSpace(WorkspaceName) || WorkspaceName.Trim().Length > 200)
        {
            WorkspaceMessage = "Enter a workspace name (at most 200 characters).";
            return;
        }

        var customers = await _customers.ListAsync();
        var customer = customers.FirstOrDefault(item => item.Name == WorkspaceCustomer)?.Id;
        var themeKey = _themeRecords.FirstOrDefault(item => item.Name == WorkspaceTheme)?.ThemeKey;
        Workspace workspace;
        if (EditingWorkspaceId is null)
        {
            var created = await _workspaces.CreateAsync(WorkspaceName.Trim());
            if (!created.Succeeded)
            {
                WorkspaceMessage = created.Error!.Message;
                return;
            }

            workspace = created.Value!;
        }
        else
        {
            workspace = await _workspaceStore.LoadAsync(new WorkspaceId(EditingWorkspaceId)) ?? WorkspaceRows.First(row => row.Workspace.Id.Value == EditingWorkspaceId).Workspace;
        }

        workspace = workspace with
        {
            Name = WorkspaceName.Trim(), Description = string.IsNullOrWhiteSpace(WorkspaceDescription) ? null : WorkspaceDescription.Trim(), CurrentCustomerId = customer,
            ThemeKey = themeKey, ModifiedAtUtc = DateTimeOffset.UtcNow
        };
        await _workspaceStore.SaveAsync(workspace);
        await _audit.RecordAsync(AuditEventTypes.ConfigurationChanged, AuditOutcome.Success, $"Workspace '{workspace.Name}' saved (theme {WorkspaceTheme}, customer {WorkspaceCustomer}).",
            new Dictionary<string, string?> { ["WorkspaceId"] = workspace.Id.Value });
        IsWorkspaceEditorOpen = false;
        if (OpenAfterSave || _workspaces.CurrentWorkspace?.Id == workspace.Id) await _workspaces.OpenAsync(workspace.Id);
        StatusMessage = $"Workspace '{workspace.Name}' saved{(OpenAfterSave ? " and opened" : string.Empty)}.";
        await LoadWorkspacesAsync();
    }

    [RelayCommand]
    private async Task OpenWorkspaceAsync(WorkspaceRow? row)
    {
        if (row is null) return;
        var result = await _workspaces.OpenAsync(row.Workspace.Id);
        StatusMessage = result.Succeeded ? $"Workspace '{row.Name}' opened." : result.Error!.Message;
        await LoadWorkspacesAsync();
    }

    [RelayCommand]
    private async Task ArchiveWorkspaceAsync(WorkspaceRow? row)
    {
        if (row is null) return;
        if (row.IsOpen)
        {
            StatusMessage = "Close or switch away from this workspace before archiving it.";
            return;
        }

        if (!_dialogs.Confirm(new ConfirmationRequest("Archive Workspace", $"Archive the workspace '{row.Name}'? It disappears from every computer's workspace list; its data is kept.",
                [new("Workspace", row.Name), new("Customer", row.Customer)], "Archive", IsDestructive: true)))
            return;
        if (!_workspaceRepository.IsAvailable)
        {
            StatusMessage = "Workspaces can be archived once the database is ready.";
            return;
        }

        await _workspaceRepository.ArchiveWorkspaceAsync(row.Workspace.Id);
        StatusMessage = $"Workspace '{row.Name}' archived.";
        await LoadWorkspacesAsync();
    }

    // ----- Themes -----

    private async Task LoadThemesAsync()
    {
        ThemeRows.Clear();
        ThemeOptions.Clear();
        ThemeOptions.Add(DefaultThemeOption);
        _defaultTheme = _diagnostics.Entries.FirstOrDefault(entry => string.Equals(entry.Key, ThemeKeys.DefaultThemeSetting, StringComparison.OrdinalIgnoreCase))?.EffectiveValue;
        if (_settings.IsAvailable)
        {
            var stored = (await _settings.ListAsync()).FirstOrDefault(row => row.Scope == SettingScopes.Shared && row.Key == ThemeKeys.DefaultThemeSetting);
            if (stored is not null) _defaultTheme = stored.Value;
        }

        _themeRecords = _themes.IsAvailable ? await _themes.ListThemesAsync() : [];
        foreach (var record in _themeRecords)
        {
            var mode = ApplicationPropertiesLoader.Parse(record.PropertiesJson, record.Name).Properties.Appearance.Theme.Mode;
            ThemeRows.Add(new ThemeRow(record, mode == ThemeMode.System ? "Follow Windows" : mode.ToString(), record.ThemeKey == _defaultTheme, 0));
            ThemeOptions.Add(record.Name);
        }
    }

    [RelayCommand]
    private async Task PreviewThemeAsync(ThemeRow? row)
    {
        if (row is null) return;
        await _theme.UseThemeAsync(row.Name, row.Theme.PropertiesJson);
        StatusMessage = $"Previewing '{row.Name}' on this computer. Opening a workspace applies that workspace's theme again.";
    }

    [RelayCommand]
    private async Task SetDefaultThemeAsync(ThemeRow? row)
    {
        if (row is null || !_settings.IsAvailable) return;
        await _settings.WriteAsync([new SettingWrite(SettingScopes.Shared, ThemeKeys.DefaultThemeSetting, row.Theme.ThemeKey)], _auditContext.Actor);
        _themeCoordinator.SetDefaultTheme(row.Theme.ThemeKey);
        if (_workspaces.CurrentWorkspace?.ThemeKey is null) await _themeCoordinator.ApplyAsync(null);
        StatusMessage = $"'{row.Name}' is now the default theme for workspaces without their own theme.";
        await LoadThemesAsync();
        await LoadWorkspacesAsync();
    }

    [RelayCommand]
    private void NewTheme()
    {
        var colors = _theme.GetEffectiveTheme().Colors;
        ThemeName = string.Empty;
        ThemeMode = _theme.EffectiveMode == ThemeMode.Dark ? ThemeMode.Dark : ThemeMode.Light;
        ThemePrimary = colors.Primary;
        ThemeAccent = colors.Accent;
        ThemeHeader = colors.HeaderBackground;
        ThemeNavigation = colors.NavigationBackground;
        ThemeMessage = "Starts from the colours in use now. Colours are #RRGGBB.";
        IsThemeEditorOpen = true;
    }

    [RelayCommand]
    private void CancelTheme() => IsThemeEditorOpen = false;

    [RelayCommand]
    private async Task SaveThemeAsync()
    {
        if (string.IsNullOrWhiteSpace(ThemeName))
        {
            ThemeMessage = "Enter a name for the theme.";
            return;
        }

        if (_themeRecords.Any(record => string.Equals(record.Name, ThemeName.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            ThemeMessage = $"A theme called '{ThemeName.Trim()}' already exists.";
            return;
        }

        var invalid = new[] { ("Primary", ThemePrimary), ("Accent", ThemeAccent), ("Header", ThemeHeader), ("Navigation", ThemeNavigation) }
            .FirstOrDefault(pair => !ApplicationPropertiesLoader.IsColor(pair.Item2?.Trim()));
        if (invalid.Item1 is not null)
        {
            ThemeMessage = $"{invalid.Item1} colour '{invalid.Item2}' is not a colour such as #1F4E79.";
            return;
        }

        var defaults = ApplicationProperties.BuiltInDefaults.Appearance;
        Dictionary<string, string> Colors(IReadOnlyDictionary<string, string> baseline) => new(baseline, StringComparer.Ordinal)
        {
            ["primary"] = ThemePrimary.Trim(), ["accent"] = ThemeAccent.Trim(), ["headerBackground"] = ThemeHeader.Trim(), ["navigationBackground"] = ThemeNavigation.Trim(),
            ["buttonBackground"] = ThemePrimary.Trim()
        };
        var json = ApplicationPropertiesLoader.Serialize(ThemeMode, true, Colors(defaults.Light.Colors.Tokens), Colors(defaults.Dark.Colors.Tokens));
        var parsed = ApplicationPropertiesLoader.Parse(json, ThemeName.Trim());
        var key = $"THEME-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        await _themes.SaveThemeAsync(new ThemeRecord(key, ThemeName.Trim(), null, json, false, _auditContext.Actor, DateTimeOffset.UtcNow, null, null));
        IsThemeEditorOpen = false;
        StatusMessage = $"Theme '{ThemeName.Trim()}' saved{(parsed.Diagnostics.Any(diagnostic => diagnostic.Severity == ConfigurationSeverity.Warning) ? " (some colours have low contrast)" : string.Empty)}. Assign it to a workspace or make it the default.";
        await LoadThemesAsync();
        await LoadWorkspacesAsync();
    }

    [RelayCommand]
    private async Task DeleteThemeAsync(ThemeRow? row)
    {
        if (row is null) return;
        if (row.Theme.IsSystem)
        {
            StatusMessage = "GDS themes cannot be deleted.";
            return;
        }

        if (row.Workspaces > 0 || row.IsDefault)
        {
            StatusMessage = $"'{row.Name}' is in use ({(row.IsDefault ? "default theme" : $"{row.Workspaces} workspace(s)")}); choose another theme there first.";
            return;
        }

        if (!_dialogs.Confirm(new ConfirmationRequest("Delete Theme", $"Delete the theme '{row.Name}'?", [new("Theme", row.Name)], "Delete", IsDestructive: true))) return;
        await _themes.DeleteThemeAsync(row.Theme.ThemeKey);
        await LoadThemesAsync();
    }

    // ----- Settings -----

    private async Task LoadSettingsAsync()
    {
        Rows.Clear();
        var stored = _settings.IsAvailable
            ? (await _settings.ListAsync()).Where(row => row.Scope == SettingScopes.Shared || row.ComputerName == _settings.ComputerName)
                .GroupBy(row => row.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.OrderByDescending(row => row.ModifiedAtUtc).First(),
                    StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, StoredSetting>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in EffectiveEntries())
        {
            stored.TryGetValue(entry.Key, out var row);
            var changed = row is not null || entry.Layer is "Shared settings" or "This computer's settings";
            Rows.Add(new WorkspaceSettingRow(entry.Section, entry.OriginalKey, row?.Value ?? entry.EffectiveValue, changed,
                SettingScopes.For(entry.Section) == SettingScopes.Computer ? "This Computer" : "All Computers",
                row is null ? null : $"{row.ModifiedBy}, {row.ModifiedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}"));
        }

        foreach (var row in Rows) row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkspaceSettingRow.IsModified)) OnPropertyChanged(nameof(ModifiedCount));
        };
        Categories.Clear();
        Categories.Add(AllCategories);
        foreach (var category in Rows.Select(row => row.Category).Distinct().Order(StringComparer.CurrentCultureIgnoreCase)) Categories.Add(category);
        SelectedCategory = AllCategories;
        LoadedCaption = $"{Rows.Count} settings in {Categories.Count - 1} categories; {Rows.Count(row => row.Changed)} changed from the defaults. " +
                        "Passwords, client secrets and other secrets are never shown here and never exported. The database connection is set on Database Configuration.";
        OnPropertyChanged(nameof(ModifiedCount));
    }

    /// <summary>The effective value of each editable, non-secret setting.</summary>
    private IEnumerable<ConfigurationEntryDiagnostic> EffectiveEntries() =>
        _diagnostics.Entries.Where(entry => !entry.IsSecret && !_redaction.IsSecretKey(entry.Key) && !ExcludedSections.Contains(entry.Section.Split(':')[0]))
            .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(entry => entry.LayerOrder).First())
            .OrderBy(entry => SettingLabels.Category(entry.Section), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase);

    private string? Validate(string setting, string value, bool isBoolean)
    {
        if (_redaction.IsSecretKey(setting)) return "secrets cannot be stored as settings";
        if (value.Contains('\n') || value.Contains('\r')) return "line breaks are not allowed";
        if (isBoolean && value is not ("true" or "false" or "True" or "False")) return "enter true or false";
        return null;
    }

    [RelayCommand]
    private void Reset()
    {
        foreach (var row in Rows) row.Value = row.OriginalValue;
        StatusMessage = "Unsaved changes were discarded.";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var modified = Rows.Where(row => row.IsModified).ToArray();
        foreach (var row in Rows) row.Error = row.IsModified ? Validate(row.SettingKey, row.Value.Trim(), row.IsBoolean) : null;
        var invalid = modified.Where(row => row.Error is not null).ToArray();
        if (invalid.Length > 0)
        {
            ChangedOnly = true;
            StatusMessage = $"{invalid.Length} setting(s) are not valid and nothing was saved: {string.Join("; ", invalid.Select(row => $"{row.Setting}: {row.Error}"))}";
            return;
        }

        if (modified.Length == 0)
        {
            StatusMessage = "There are no changes to save.";
            return;
        }

        var written = await WriteAsync(modified.Select(row => (row.Section, row.Key, row.Value.Trim())).ToArray(), "Edited");
        if (written is null) return;
        foreach (var row in modified) row.MarkSaved();
        RestartRequired = true;
        StatusMessage = $"Saved {modified.Length} setting(s) to {written}. Restart Secure Plus Agent to apply them.";
    }

    private async Task<string?> WriteAsync(IReadOnlyList<(string Section, string Key, string Value)> values, string action)
    {
        var targets = new List<string>();
        foreach (var batch in values.GroupBy(value => value.Section))
        {
            var result = await _writer.WriteSectionAsync(FileOf(batch.Key), batch.Key, batch.ToDictionary(value => value.Key, value => (string?)value.Value, StringComparer.OrdinalIgnoreCase));
            if (!result.Succeeded)
            {
                StatusMessage = $"{SettingLabels.Category(batch.Key)} was not saved: {result.Error}";
                return null;
            }

            targets.Add(result.Value!);
        }

        await _audit.RecordAsync(AuditEventTypes.ConfigurationChanged, AuditOutcome.Success, $"Workspace configuration {action.ToLowerInvariant()}: {values.Count} setting(s).",
            new Dictionary<string, string?> { ["Settings"] = string.Join(", ", values.Select(value => $"{value.Section}:{value.Key}")) });
        return string.Join(" and ", targets.Distinct());
    }

    /// <summary>The INI file of a section (used only before a database exists).</summary>
    private static string FileOf(string section) => section.Split(':')[0] switch
    {
        "Branding" => "Branding.ini",
        "Features" => "Features.ini",
        "Repositories" => "Repositories.ini",
        "Integrations" => "Integrations.ini",
        "Logging" => "Logging.ini",
        "GoldenTenant" => "GoldenTenant.ini",
        _ => "Application.ini"
    };

    [RelayCommand]
    private async Task ExportAsync()
    {
        var path = _dialogs.SaveFile($"workspace-configuration-{DateTime.Now:yyyyMMdd-HHmm}.json", "JSON (*.json)|*.json");
        if (path is null) return;
        var settings = Rows.Select(row => new ConfigurationValueV1(row.Section, row.Key, row.OriginalValue)).ToArray();
        var export = new WorkspaceConfigurationExportV1(ContractVersions.WorkspaceConfigurationV1, DateTimeOffset.UtcNow, ApplicationBootstrapper.ApplicationVersion,
            _workspaces.CurrentWorkspace?.Id.Value ?? "application", settings);
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(export, Json));
            await _audit.RecordAsync(AuditEventTypes.ConfigurationExported, AuditOutcome.Success, $"Workspace configuration exported ({settings.Length} settings).",
                new Dictionary<string, string?> { ["Path"] = path });
            StatusMessage = $"Exported {settings.Length} setting(s) to {path}. Secrets are never exported.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"The export could not be written: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var path = _dialogs.OpenFile("JSON (*.json)|*.json");
        if (path is null) return;
        WorkspaceConfigurationExportV1? import;
        try
        {
            import = JsonSerializer.Deserialize<WorkspaceConfigurationExportV1>(await File.ReadAllTextAsync(path), Json);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            StatusMessage = $"The file is not a workspace configuration export: {exception.Message}";
            return;
        }

        if (import is null || !string.Equals(import.Schema, ContractVersions.WorkspaceConfigurationV1, StringComparison.Ordinal) || import.Settings is null)
        {
            StatusMessage = $"The file is not a workspace configuration export (expected schema '{ContractVersions.WorkspaceConfigurationV1}').";
            return;
        }

        var current = Rows.ToDictionary(row => row.SettingKey, StringComparer.OrdinalIgnoreCase);
        var preview = new List<ImportPreviewRow>();
        foreach (var setting in import.Settings.Where(setting => setting is not null))
        {
            var name = $"{setting.Section}:{setting.Key}";
            var value = (setting.Value ?? string.Empty).Trim();
            current.TryGetValue(name, out var row);
            var problem = string.IsNullOrWhiteSpace(setting.Section) || string.IsNullOrWhiteSpace(setting.Key) ? "section and key are required"
                : ExcludedSections.Contains(setting.Section.Split(':')[0]) ? "the connection and the menu are not imported"
                : Validate(name, value, row?.IsBoolean ?? false);
            var change = problem is not null ? $"Skipped: {problem}"
                : row is null ? "Added"
                : string.Equals(row.OriginalValue, value, StringComparison.Ordinal) ? "Unchanged"
                : "Changed";
            preview.Add(new ImportPreviewRow(SettingLabels.Category(setting.Section ?? string.Empty), name, row?.OriginalValue ?? "(not set)",
                _redaction.IsSecretKey(name) ? RedactionService.Mask : value, change, change is "Added" or "Changed"));
        }

        _pendingImport = preview;
        Preview.Clear();
        foreach (var row in preview.OrderBy(row => row.WillApply ? 0 : row.Change == "Unchanged" ? 2 : 1).ThenBy(row => row.Setting, StringComparer.OrdinalIgnoreCase))
            Preview.Add(row);
        var applying = preview.Count(row => row.WillApply);
        PreviewCaption = $"{Path.GetFileName(path)} (exported {import.ExportedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} by version {import.ExportedByApplicationVersion}): " +
                         $"{applying} change(s) to apply, {preview.Count(row => row.Change == "Unchanged")} unchanged, {preview.Count(row => row.Change.StartsWith("Skipped", StringComparison.Ordinal))} skipped.";
        IsPreviewOpen = true;
        StatusMessage = applying == 0 ? "Nothing in the file differs from the current configuration." : "Review the changes below, then apply them.";
    }

    [RelayCommand]
    private async Task ApplyImportAsync()
    {
        var changes = _pendingImport.Where(row => row.WillApply).ToArray();
        if (changes.Length == 0)
        {
            IsPreviewOpen = false;
            return;
        }

        if (!_dialogs.Confirm(new ConfirmationRequest("Apply Imported Configuration", $"Save {changes.Length} setting(s)? They apply after a restart.",
                changes.Take(8).Select(row => new KeyValuePair<string, string>(row.Setting, row.Imported)).ToArray(), "Apply")))
            return;

        var values = changes.Select(row =>
        {
            var split = row.Setting.LastIndexOf(':');
            return (row.Setting[..split], row.Setting[(split + 1)..], row.Imported);
        }).ToArray();
        var written = await WriteAsync(values, "Imported");
        if (written is null) return;
        IsPreviewOpen = false;
        RestartRequired = true;
        StatusMessage = $"Imported {changes.Length} setting(s) into {written}. Restart Secure Plus Agent to apply them.";
        await LoadSettingsAsync();
    }

    [RelayCommand]
    private void CancelImport()
    {
        IsPreviewOpen = false;
        _pendingImport = [];
        StatusMessage = "The import was cancelled; nothing was changed.";
    }

    [RelayCommand]
    private void Restart()
    {
        if (Environment.ProcessPath is not { } path) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
        System.Windows.Application.Current.MainWindow?.Close();
    }
}
