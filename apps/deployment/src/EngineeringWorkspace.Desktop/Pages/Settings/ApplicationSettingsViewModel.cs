using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Configuration;
using EngineeringWorkspace.Application.Features;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Desktop.Services;

namespace EngineeringWorkspace.Desktop.Pages.Settings;

public enum SettingKind
{
    Text,
    Boolean,
    Choice,
    Integer
}

/// <summary>One editable configuration key, its effective value and where that value came from.</summary>
public sealed partial class SettingField : ObservableObject
{
    public SettingField(string file, string section, string key, string label, string description, SettingKind kind, IReadOnlyList<string>? options,
        string value, string source, bool overriddenElsewhere)
    {
        File = file;
        Section = section;
        Key = key;
        Label = label;
        Description = description;
        Kind = kind;
        Options = options ?? [];
        OriginalValue = value;
        _value = value;
        Source = overriddenElsewhere ? $"Effective value comes from {source}, which overrides {file}; saving changes {file} only." : $"From {source}";
        OverriddenElsewhere = overriddenElsewhere;
    }

    public string File { get; }
    public string Section { get; }
    public string Key { get; }
    public string Label { get; }
    public string Description { get; }
    public SettingKind Kind { get; }
    public IReadOnlyList<string> Options { get; }
    public string OriginalValue { get; private set; }
    public string Source { get; }
    public bool OverriddenElsewhere { get; }
    public bool IsText => Kind is SettingKind.Text or SettingKind.Integer;
    public bool IsBoolean => Kind == SettingKind.Boolean;
    public bool IsChoice => Kind == SettingKind.Choice;
    public bool IsModified => !string.Equals(Value, OriginalValue, StringComparison.Ordinal);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified), nameof(BoolValue))]
    private string _value;

    [ObservableProperty] private string? _error;

    public bool BoolValue
    {
        get => string.Equals(Value, "true", StringComparison.OrdinalIgnoreCase);
        set => Value = value ? "true" : "false";
    }

    public void MarkSaved()
    {
        OriginalValue = Value;
        OnPropertyChanged(nameof(IsModified));
    }
}

/// <summary>A titled group of settings (one INI section, or a few related ones).</summary>
public sealed record SettingGroup(string Title, string Description, IReadOnlyList<SettingField> Fields);

/// <summary>
/// Workspace → Settings → Application Settings: edits Application.ini (environment, identity, paths, workspace,
/// import/export, credentials), Branding.ini, Logging.ini, Features.ini, Integrations.ini and Repositories.ini.
/// Values are validated before saving; the configuration snapshot is immutable, so changes apply after a restart.
/// </summary>
public sealed partial class ApplicationSettingsViewModel(IConfigurationDiagnostics diagnostics, IConfigurationFileWriter writer, IDialogService dialogs, IRouteRegistry routes,
    IAdvancedModeService advancedMode)
    : PageViewModel
{
    private bool _syncingAdvanced;

    /// <summary>The Enable Advanced Mode check box. Advanced Mode only turns on when the passcode box also holds the passcode.</summary>
    [ObservableProperty] private bool _advancedModeRequested;

    [ObservableProperty] private bool _advancedModeEnabled;
    [ObservableProperty] private string _advancedModeStatus = string.Empty;

    /// <summary>Set by the view's passcode box (a PasswordBox cannot be bound).</summary>
    public string AdvancedPasscode { get; set; } = string.Empty;

    partial void OnAdvancedModeRequestedChanged(bool value)
    {
        if (_syncingAdvanced) return;
        _ = ApplyAdvancedModeAsync();
    }

    /// <summary>Called when the check box or the passcode changes.</summary>
    public async Task ApplyAdvancedModeAsync()
    {
        if (!AdvancedModeRequested)
        {
            if (advancedMode.IsEnabled) await advancedMode.DisableAsync();
            AdvancedModeStatus = "Advanced Mode is off. The Advanced menu is hidden.";
        }
        else if (advancedMode.IsEnabled)
        {
            AdvancedModeStatus = "Advanced Mode is on. The Advanced menu is shown below Settings.";
        }
        else if (await advancedMode.TryEnableAsync(AdvancedPasscode))
        {
            AdvancedModeStatus = "Advanced Mode is on. The Advanced menu is shown below Settings.";
        }
        else
        {
            AdvancedModeStatus = string.IsNullOrEmpty(AdvancedPasscode)
                ? "Enter the passcode in the box to turn Advanced Mode on."
                : "That passcode is not correct; Advanced Mode stays off.";
        }

        AdvancedModeEnabled = advancedMode.IsEnabled;
    }

    private static readonly string[] Booleans = ["true", "false"];

    public ObservableCollection<SettingGroup> Groups { get; } = [];

    [ObservableProperty] private bool _restartRequired;

    public override async Task OnNavigatedToAsync(RouteDescriptor route, CancellationToken cancellationToken)
    {
        await base.OnNavigatedToAsync(route, cancellationToken);
        _syncingAdvanced = true;
        AdvancedModeRequested = AdvancedModeEnabled = advancedMode.IsEnabled;
        _syncingAdvanced = false;
        AdvancedModeStatus = advancedMode.IsEnabled ? "Advanced Mode is on. The Advanced menu is shown below Settings." : "Advanced Mode is off. The Advanced menu is hidden.";
        Load();
    }

    [RelayCommand]
    private void Reset()
    {
        foreach (var field in Groups.SelectMany(group => group.Fields)) field.Value = field.OriginalValue;
        StatusMessage = "Unsaved changes were discarded.";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var fields = Groups.SelectMany(group => group.Fields).ToArray();
        foreach (var field in fields) field.Error = Validate(field);
        var invalid = fields.Where(field => field.Error is not null).ToArray();
        if (invalid.Length > 0)
        {
            StatusMessage = $"{invalid.Length} setting(s) are not valid and nothing was saved: {string.Join("; ", invalid.Select(field => $"{field.Label}: {field.Error}"))}";
            return;
        }

        var modified = fields.Where(field => field.IsModified).ToArray();
        if (modified.Length == 0)
        {
            StatusMessage = "There are no changes to save.";
            return;
        }

        if (modified.Any(field => field.Key == "Environment" && field.Value == "Production") &&
            !dialogs.Confirm(new ConfirmationRequest("Switch to Production",
                "Production disables development-only behaviour such as automatic database initialization. The change applies after a restart.",
                [new("Environment", "Production")], "Switch to Production")))
            return;

        var saved = new List<string>();
        foreach (var batch in modified.GroupBy(field => (field.File, field.Section)))
        {
            var result = await writer.WriteSectionAsync(batch.Key.File, batch.Key.Section, batch.ToDictionary(field => field.Key, field => (string?)field.Value.Trim()));
            if (!result.Succeeded)
            {
                StatusMessage = $"{batch.Key.File} [{batch.Key.Section}] was not saved: {result.Error}";
                return;
            }

            foreach (var field in batch) field.MarkSaved();
            saved.Add($"{batch.Key.File} [{batch.Key.Section}]");
        }

        RestartRequired = true;
        StatusMessage = $"Saved {modified.Length} setting(s) to {string.Join(", ", saved)}. Restart Secure Plus Agent to apply them.";
    }

    [RelayCommand]
    private void Restart()
    {
        if (Environment.ProcessPath is not { } path) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
        System.Windows.Application.Current.MainWindow?.Close();
    }

    private static string? Validate(SettingField field)
    {
        var value = field.Value.Trim();
        return field.Kind switch
        {
            SettingKind.Integer when !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < 0 => "enter a whole number of 0 or more",
            SettingKind.Choice when field.Options.Count > 0 && !field.Options.Contains(value, StringComparer.OrdinalIgnoreCase) => $"choose one of {string.Join(", ", field.Options)}",
            SettingKind.Boolean when !Booleans.Contains(value, StringComparer.OrdinalIgnoreCase) => "choose true or false",
            SettingKind.Text when value.Contains('\n') || value.Contains('\r') => "line breaks are not allowed",
            _ => field.Key is "Name" or "Title" or "CompanyName" or "ApplicationName" or "ApplicationTitle" && value.Length == 0 ? "a value is required" : null
        };
    }

    private void Load()
    {
        Groups.Clear();
        var entries = diagnostics.Entries.GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(entry => entry.LayerOrder).First(), StringComparer.OrdinalIgnoreCase);
        SettingField Field(string file, string section, string key, string label, string description, SettingKind kind = SettingKind.Text, IReadOnlyList<string>? options = null)
        {
            entries.TryGetValue($"{section}:{key}", out var entry);
            var value = entry?.IsSecret == true ? string.Empty : entry?.EffectiveValue ?? string.Empty;
            var source = entry?.Layer ?? "compiled defaults";
            var overridden = entry is not null && entry.LayerOrder > 10 && !string.Equals(entry.Layer, file, StringComparison.OrdinalIgnoreCase) &&
                             !string.Equals(entry.Layer, "Environment", StringComparison.OrdinalIgnoreCase);
            return new SettingField(file, section, key, label, description, kind, kind == SettingKind.Boolean ? Booleans : options, kind == SettingKind.Boolean ? value.ToLowerInvariant() : value, source, overridden);
        }

        const string app = "Application.ini";
        Groups.Add(new SettingGroup("Application", "Identity and start-up behaviour. Production or Test is a property of the database, chosen at setup.",
        [
            Field(app, "Application", "Name", "Application Name", "Name used by the application host and logs."),
            Field(app, "Application", "Title", "Application Title", "Title used by the application host."),
            Field(app, "Application", "CompanyName", "Company Name", "Company shown in the application."),
            Field(app, "Application", "DefaultRoute", "Start Page", "The page opened at start-up when the last page cannot be restored.", SettingKind.Choice,
                routes.All.Select(route => route.Route).ToArray()),
            Field(app, "Application", "RestoreLastWorkspace", "Restore Last Workspace", "Reopen the last workspace at start-up.", SettingKind.Boolean),
            Field(app, "Application", "AllowMultipleInstances", "Allow Multiple Instances", "Allow more than one window of the application.", SettingKind.Boolean),
        ]));
        Groups.Add(new SettingGroup("Branding", "Names shown in the window title and header (Branding.ini).",
        [
            Field("Branding.ini", "Branding", "ApplicationName", "Header Name", "Name shown in the header."),
            Field("Branding.ini", "Branding", "ApplicationTitle", "Window Title", "Title of the main window."),
            Field("Branding.ini", "Branding", "CompanyName", "Company Name", "Company shown under the header name."),
        ]));
        Groups.Add(new SettingGroup("Workspace", "How workspaces are opened and created.",
        [
            Field(app, "Workspace", "RequireWorkspace", "Require a Workspace", "Pages that need a workspace stay disabled until one is open.", SettingKind.Boolean),
            Field(app, "Workspace", "CreateDirectoryIfMissing", "Create Missing Workspace", "Create the start-up workspace when it does not exist.", SettingKind.Boolean),
            Field(app, "Workspace", "RestoreLastWorkspace", "Restore Last Workspace", "Reopen the last workspace at start-up.", SettingKind.Boolean),
            Field(app, "Workspace", "AllowCustomerlessWorkspace", "Allow Workspaces Without a Customer", "Allow a workspace that is not linked to a customer.", SettingKind.Boolean),
            Field(app, "Workspace", "DefaultWorkspace", "Default Workspace", "Workspace identifier opened when there is no last workspace (empty = none)."),
        ]));
        Groups.Add(new SettingGroup("Paths", "Data locations. Relative paths resolve against the per-user data folder; changes apply after a restart.",
        [
            Field(app, "Paths", "DataDirectory", "Data", "Root of application data."),
            Field(app, "Paths", "WorkspaceDirectory", "Workspaces", "Workspace folders."),
            Field(app, "Paths", "CustomerDirectory", "Customers", "Customer profile folders."),
            Field(app, "Paths", "GoldenTenantDirectory", "Golden Tenants", "Golden Tenant baselines."),
            Field(app, "Paths", "ImportDirectory", "Imports", "Default import folder."),
            Field(app, "Paths", "ExportDirectory", "Exports", "Default export folder."),
            Field(app, "Paths", "LogDirectory", "Logs", "Diagnostic logs."),
            Field(app, "Paths", "AuditDirectory", "Audit", "Audit records."),
            Field(app, "Paths", "AssetDirectory", "Assets", "Branding assets (relative to the installation folder)."),
            Field(app, "Paths", "CredentialDirectory", "Credentials", "DPAPI-protected credential store."),
            Field(app, "Paths", "StateDirectory", "State", "Window and session state."),
        ]));
        Groups.Add(new SettingGroup("Import, Export and Credentials", "Export behaviour and credential expiry warnings. Secrets are never exportable.",
        [
            Field(app, "ImportExport", "DefaultExportFormat", "Export Format", "Only JSON export is implemented.", SettingKind.Choice, ["Json"]),
            Field(app, "ImportExport", "OverwriteExistingFiles", "Overwrite Existing Files", "Overwrite files with the same name when exporting.", SettingKind.Boolean),
            Field(app, "ImportExport", "IncludeMetadata", "Include Metadata", "Include export metadata (version, time, source).", SettingKind.Boolean),
            Field(app, "ImportExport", "TimestampFileNames", "Timestamp File Names", "Add the time to exported file names.", SettingKind.Boolean),
            Field(app, "Credentials", "ExpirationWarningDays", "Credential Expiry Warning (days)", "Warn this many days before a stored credential expires.", SettingKind.Integer),
            Field(app, "Credentials", "SecretStoreProvider", "Secret Store", "Only the Windows DPAPI store is implemented.", SettingKind.Choice, ["Dpapi"]),
        ]));
        Groups.Add(new SettingGroup("Logging", "Diagnostic log files (Logging.ini). Audit events are always written.",
        [
            Field("Logging.ini", "Logging", "MinimumLevel", "Minimum Level", "Least severe level written to the log.", SettingKind.Choice,
                ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"]),
            Field("Logging.ini", "Logging", "RetainedFileCountLimit", "Files Kept", "Number of daily log files kept.", SettingKind.Integer),
            Field("Logging.ini", "Logging", "FileSizeLimitMegabytes", "File Size Limit (MB)", "A log file rolls over at this size.", SettingKind.Integer),
        ]));
        Groups.Add(new SettingGroup("Features", "Turn application areas on or off (Features.ini). A page whose feature is off is shown disabled.",
            FeatureIds.All.Select(id => Field("Features.ini", "Features", id, Spaced(id), $"The {Spaced(id)} feature.", SettingKind.Boolean)).ToArray()));
        Groups.Add(new SettingGroup("Integrations", "Teams, ConnectWise and the app registration assistant (Integrations.ini). Never enter secrets here.",
        [
            Field("Integrations.ini", "Integrations:Teams", "Enabled", "Teams Integration Enabled", "Required for the Teams Integration feature.", SettingKind.Boolean),
            Field("Integrations.ini", "Integrations:ConnectWise", "Enabled", "ConnectWise Integration Enabled", "Required for the ConnectWise Integration feature.", SettingKind.Boolean),
            Field("Integrations.ini", "Integrations:ConnectWise", "Site", "ConnectWise Site", "For example na.myconnectwise.net."),
            Field("Integrations.ini", "Integrations:ConnectWise", "CompanyId", "ConnectWise Company ID", "Your ConnectWise company identifier."),
            Field("Integrations.ini", "Integrations:Microsoft", "AdministratorClientId", "Administrator App Client ID", "Public-client app used by the app registration assistant (empty disables it)."),
            Field("Integrations.ini", "Integrations:Microsoft", "AdministratorRedirectUri", "Administrator Redirect URI", "Redirect URI of that app, normally http://localhost."),
        ]));
        Groups.Add(new SettingGroup("Repositories", "Policy, script and document repositories (Repositories.ini).",
        [
            Field("Repositories.ini", "Repositories", "DocumentsRepository", "Documents Repository", "Folder or clone of the documents repository."),
            Field("Repositories.ini", "Repositories", "PolicyRepository", "Policy Repository", "Folder of policy JSON files."),
            Field("Repositories.ini", "Repositories", "PolicySetsRepository", "Policy Sets Repository", "Folder of policy set definitions."),
            Field("Repositories.ini", "Repositories", "CustomerVariablesRepository", "Customer Variables Repository", "Folder of customer-variables.json files."),
            Field("Repositories.ini", "Repositories", "ScriptsRepository", "Scripts Repository", "Folder of PowerShell scripts."),
            Field("Repositories.ini", "Repositories", "AutoDiscover", "Auto-discover", "Discover repositories automatically.", SettingKind.Boolean),
            Field("Repositories.ini", "Repositories", "ScanOnStartup", "Scan on Start-Up", "Scan repositories when the application starts.", SettingKind.Boolean),
            Field("Repositories.ini", "Repositories", "WatchForChanges", "Watch for Changes", "Rescan when repository files change.", SettingKind.Boolean),
            Field("Repositories.ini", "Repositories", "ValidateOnScan", "Validate on Scan", "Validate files while scanning.", SettingKind.Boolean),
        ]));
        StatusMessage = null;
    }

    private static string Spaced(string identifier) =>
        string.Concat(identifier.Select((character, index) => index > 0 && char.IsUpper(character) && !char.IsUpper(identifier[index - 1]) ? $" {character}" : character.ToString()));
}
