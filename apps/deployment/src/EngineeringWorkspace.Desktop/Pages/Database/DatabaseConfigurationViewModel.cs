using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Credentials;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Desktop.Services;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Credentials;
using EngineeringWorkspace.Domain.Security;

namespace EngineeringWorkspace.Desktop.Pages.Database;

/// <summary>One row of the readiness checklist.</summary>
public sealed record ReadinessRow(string Name, CheckState State, string Detail);

/// <summary>
/// Workspace → Settings → Database Configuration. Each operation is a separate command shown only when valid for
/// the current state. All database work runs off the UI thread with progress, timeout and cancellation.
/// </summary>
public sealed partial class DatabaseConfigurationViewModel : PageViewModel
{
    private readonly IDatabaseStatusService _status;
    private readonly IDatabaseProvisioningService _provisioning;
    private readonly ICredentialService _credentials;
    private readonly IDialogService _dialogs;
    private readonly ApplicationSettings _application;
    private CancellationTokenSource? _operation;
    private readonly IDatabaseMoveService? _mover;

    public DatabaseConfigurationViewModel(IDatabaseStatusService status, IDatabaseProvisioningService provisioning, ICredentialService credentials,
        IDialogService dialogs, ApplicationSettings application, InstallProfile? install = null, IDatabaseMoveService? mover = null)
    {
        _mover = mover;
        _status = status;
        _provisioning = provisioning;
        _credentials = credentials;
        _dialogs = dialogs;
        _application = application;
        Install = install ?? InstallProfile.None;
        MoveServer = Install.SqlServer ?? string.Empty;
        MoveDatabase = Install.DatabaseName ?? InstallProfile.DefaultDatabaseName;
        // SQLite is no longer offered (3.2: SQL Server only); an existing SQLite configuration stays selectable until it is moved.
        Providers = Enum.GetValues<DatabaseProvider>().Where(provider => provider != DatabaseProvider.SQLite || status.Settings.Provider == DatabaseProvider.SQLite).ToArray();
        Load(status.Settings.Provider == DatabaseProvider.None && Install.SqlServer is { } server
            // The server address entered during setup (or passed with /SERVER=), using Windows Authentication.
            ? status.Settings with
            {
                Provider = DatabaseProvider.SqlServer, ServerName = server, DatabaseName = Install.DatabaseName ?? InstallProfile.DefaultDatabaseName,
                Authentication = DatabaseAuthenticationMethod.WindowsIntegrated
            }
            : status.Settings);
        if (status.LatestStatus is { } latest) Show(latest);
    }

    /// <summary>Raised after a secret was stored so the view clears its password boxes.</summary>
    public event EventHandler? SecretInputsCleared;

    public IReadOnlyList<DatabaseProvider> Providers { get; }

    /// <summary>The installer's choice for this computer.</summary>
    public InstallProfile Install { get; }

    public string InstallDescription => Install.Type switch
    {
        InstallType.Workstation => "Workstation install: this computer uses the shared database but never creates or upgrades it. The Server install does that.",
        InstallType.Server => "Server install: this computer creates the database if it is missing, upgrades its schema and loads reference data.",
        InstallType.AllInOne => "All-in-one install (lab/test): this computer can create and upgrade the database and use it.",
        _ => "Install type not recorded (installed before 3.2): this computer can create and upgrade the database."
    };

    public bool CanProvision => Install.CanProvision;

    // Move to SQL Server (3.2): SQLite is no longer supported for production; its data moves once into the shared SQL Server.
    [ObservableProperty] private string _moveServer = string.Empty;
    [ObservableProperty] private string _moveDatabase = InstallProfile.DefaultDatabaseName;
    public ObservableCollection<TableMoveResult> MoveResults { get; } = [];
    [ObservableProperty] private string _moveSummary = string.Empty;

    [RelayCommand]
    private async Task MoveToSqlServerAsync()
    {
        if (_mover is null || _status.Settings.Provider != DatabaseProvider.SQLite) return;
        if (string.IsNullOrWhiteSpace(MoveServer))
        {
            StatusMessage = "Enter the SQL Server to move to.";
            return;
        }

        var source = _status.Settings;
        var target = new DatabaseSettings
        {
            Provider = DatabaseProvider.SqlServer, ServerName = MoveServer.Trim(),
            DatabaseName = string.IsNullOrWhiteSpace(MoveDatabase) ? InstallProfile.DefaultDatabaseName : MoveDatabase.Trim(),
            Authentication = DatabaseAuthenticationMethod.WindowsIntegrated, TrustServerCertificate = true, CommandTimeoutSeconds = 600
        };
        if (!_dialogs.Confirm(new ConfirmationRequest("Move to SQL Server",
                "Your SQLite data is copied into the SQL Server database. The SQLite file is backed up first and is not changed. Tables that have data in SQLite replace the same tables in SQL Server; the copy is kept only if every table's row count matches.",
                [new("From", $"SQLite file {source.DatabaseName}"), new("To", $"{target.DatabaseName} on {target.DisplayServer} (Windows Authentication)")], "Move Data")))
            return;

        MoveResults.Clear();
        MoveSummary = string.Empty;
        _operation?.Cancel();
        _operation = new CancellationTokenSource();
        var token = _operation.Token;
        try
        {
            IsBusy = true;
            var progress = new Progress<string>(message => StatusMessage = message);
            var report = await _provisioning.CheckDatabaseAsync(target, progress, token);
            if (report.DatabaseExists == false) report = await _provisioning.CreateDatabaseAsync(target, progress, token);
            if (report.SchemaState is SchemaState.Missing or SchemaState.MigrationRequired || report.State is DatabaseOverallState.SchemaMissing or DatabaseOverallState.MigrationRequired)
                report = await _provisioning.InitializeSchemaAsync(target, progress, token);
            report = await _provisioning.GetStatusAsync(target, progress, token);
            if (report.State != DatabaseOverallState.Ready)
            {
                StatusMessage = $"The SQL Server database is not ready ({report.State}): {report.SanitizedError} {report.RecommendedAction}";
                return;
            }

            var result = await _mover.MoveAsync(source, target, progress, token);
            foreach (var table in result.Tables.Where(table => table.SourceRows > 0 || !table.Matches)) MoveResults.Add(table);
            MoveSummary = $"{result.Message} Backup: {result.BackupPath}";
            StatusMessage = result.Message;
            if (!result.Succeeded) return;
            if (_dialogs.Confirm(new ConfirmationRequest("Use SQL Server now?",
                    "The data was moved and every table matches. Switch this computer to the SQL Server database now? The SQLite file stays where it is, unchanged, until you delete it.",
                    [new("Database", $"{target.DatabaseName} on {target.DisplayServer}")], "Switch to SQL Server")))
            {
                var saved = await _status.SaveSettingsAsync(target, token);
                if (!saved.Succeeded)
                {
                    StatusMessage = saved.Error?.Message ?? "The settings could not be saved.";
                    return;
                }

                _status.Publish(report);
                Load(target);
                StatusMessage = "This computer now uses SQL Server. Restart Secure Plus to reload everything from it.";
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            StatusMessage = $"The move failed and nothing was kept in SQL Server: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
    public ObservableCollection<DatabaseAuthenticationMethod> AuthenticationMethods { get; } = [];
    public ObservableCollection<ReadinessRow> Checks { get; } = [];
    public ObservableCollection<string> ValidationErrors { get; } = [];
    public ObservableCollection<string> SchemaFindings { get; } = [];
    public string WrittenFileDescription => $"Saved settings are written to {DatabaseStatusService.FileName} in the per-user configuration directory; passwords and secrets are stored only in Credential Management.";
    public bool IsProduction => string.Equals(_application.Environment, "Production", StringComparison.Ordinal);

    /// <summary>Test databases apply schema upgrades automatically (Production needs a confirmation).</summary>
    public bool IsTest => string.Equals(_application.Environment, Application.Database.DatabaseModes.Test, StringComparison.Ordinal);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerProvider), nameof(IsSqlite), nameof(IsConfigured), nameof(DatabaseNameLabel))]
    private DatabaseProvider _provider;

    [ObservableProperty] private string _serverName = string.Empty;
    [ObservableProperty] private string _sqlInstance = string.Empty;
    [ObservableProperty] private string _port = string.Empty;
    [ObservableProperty] private string _databaseName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSqlLogin), nameof(IsEntraInteractive), nameof(IsEntraServicePrincipal), nameof(IsManagedIdentity), nameof(NeedsEntraClientId))]
    private DatabaseAuthenticationMethod _authentication;

    [ObservableProperty] private string _userName = string.Empty;
    [ObservableProperty] private string _passwordSecretReference = string.Empty;
    [ObservableProperty] private string _entraClientId = string.Empty;
    [ObservableProperty] private string _entraTenantId = string.Empty;
    [ObservableProperty] private string _entraClientSecretReference = string.Empty;
    [ObservableProperty] private string _managedIdentityClientId = string.Empty;
    [ObservableProperty] private bool _encryptConnection = true;
    [ObservableProperty] private bool _trustServerCertificate;
    [ObservableProperty] private string _connectionTimeoutSeconds = "30";
    [ObservableProperty] private string _commandTimeoutSeconds = "60";
    [ObservableProperty] private string _retryCount = "3";
    [ObservableProperty] private string _retryDelaySeconds = "10";
    [ObservableProperty] private string _connectionStringSecretReference = string.Empty;
    [ObservableProperty] private bool _automaticDatabaseInitialization;
    [ObservableProperty] private bool _automaticSchemaUpgrade;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreateDatabase), nameof(CanValidateSchema), nameof(CanInitializeSchema), nameof(CanRepairSchema), nameof(HasStatus), nameof(IsReady))]
    private DatabaseStatusReport? _currentReport;

    [ObservableProperty] private string? _progressText;

    /// <summary>Transient SQL login password typed in the view; cleared as soon as it is stored.</summary>
    public string SqlPasswordInput { private get; set; } = string.Empty;

    /// <summary>Transient service principal secret typed in the view; cleared as soon as it is stored.</summary>
    public string ServicePrincipalSecretInput { private get; set; } = string.Empty;

    public bool HasStatus => CurrentReport is not null;
    public bool IsServerProvider => Provider is DatabaseProvider.SqlServer or DatabaseProvider.AzureSql;
    public bool IsSqlite => Provider == DatabaseProvider.SQLite;
    public bool IsConfigured => Provider != DatabaseProvider.None;
    public string DatabaseNameLabel => IsSqlite ? "Database file path (relative paths resolve against the data directory)" : "Database name";
    public bool IsSqlLogin => IsServerProvider && Authentication == DatabaseAuthenticationMethod.SqlLogin;
    public bool IsEntraInteractive => IsServerProvider && Authentication == DatabaseAuthenticationMethod.EntraInteractive;
    public bool IsEntraServicePrincipal => IsServerProvider && Authentication == DatabaseAuthenticationMethod.EntraServicePrincipal;
    public bool IsManagedIdentity => IsServerProvider && Authentication == DatabaseAuthenticationMethod.EntraManagedIdentity;
    public bool NeedsEntraClientId => IsEntraInteractive || IsEntraServicePrincipal;
    public bool CanCreateDatabase => !IsBusy && CurrentReport is { State: DatabaseOverallState.DatabaseMissing };
    public bool CanValidateSchema => !IsBusy && CurrentReport is { DatabaseExists: true, DatabaseAccessCheck: CheckState.Passed };
    public bool CanInitializeSchema => !IsBusy && CurrentReport is { SchemaState: SchemaState.Missing or SchemaState.MigrationRequired };
    public bool CanRepairSchema => !IsBusy && CurrentReport is { SchemaState: SchemaState.Incompatible, DatabaseAccessCheck: CheckState.Passed };
    public bool IsReady => CurrentReport is { State: DatabaseOverallState.Ready };

    /// <summary>Raised after every completed database operation with its report.</summary>
    public event EventHandler<DatabaseStatusReport>? ReportChanged;

    partial void OnProviderChanged(DatabaseProvider value)
    {
        var current = Authentication;
        AuthenticationMethods.Clear();
        foreach (var method in DatabaseAuthenticationMethods.For(value)) AuthenticationMethods.Add(method);
        Authentication = AuthenticationMethods.Contains(current) ? current : AuthenticationMethods.FirstOrDefault();
        OnPropertyChanged(nameof(IsSqlLogin));
        OnPropertyChanged(nameof(IsEntraInteractive));
        OnPropertyChanged(nameof(IsEntraServicePrincipal));
        OnPropertyChanged(nameof(IsManagedIdentity));
        OnPropertyChanged(nameof(NeedsEntraClientId));
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != nameof(IsBusy)) return;
        OnPropertyChanged(nameof(CanCreateDatabase));
        OnPropertyChanged(nameof(CanValidateSchema));
        OnPropertyChanged(nameof(CanInitializeSchema));
        OnPropertyChanged(nameof(CanRepairSchema));
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (BuildSettings() is not { } settings) return;
        var result = await _status.SaveSettingsAsync(settings);
        StatusMessage = result.Succeeded ? $"Settings saved to {result.Value}." : result.Error!.ToString();
    }

    [RelayCommand]
    private Task TestConnectionAsync() => RunAsync("Testing the server connection...", _provisioning.TestServerConnectionAsync, TimeSpan.FromMinutes(2));

    [RelayCommand]
    private Task CheckDatabaseAsync() => RunAsync("Checking the database...", _provisioning.CheckDatabaseAsync, TimeSpan.FromMinutes(2));

    [RelayCommand]
    private Task RefreshStatusAsync() => RunAsync("Refreshing status...", _provisioning.GetStatusAsync, TimeSpan.FromMinutes(2));

    [RelayCommand]
    private async Task ValidateSchemaAsync()
    {
        var report = await RunAsync("Validating the schema...", _provisioning.ValidateSchemaAsync, TimeSpan.FromMinutes(5));
        if (report is { SchemaState: SchemaState.MigrationRequired } && (AutomaticSchemaUpgrade || IsTest) && !IsProduction)
            await RunInitializeAsync("Automatic schema upgrade is enabled (non-Production): applying pending migrations...");
    }

    [RelayCommand]
    private async Task CreateDatabaseAsync()
    {
        if (BuildSettings() is not { } settings) return;
        var confirmed = _dialogs.Confirm(new ConfirmationRequest(
            "Create database",
            IsSqlite
                ? "A new, empty SQLite database file will be created. No existing file is overwritten."
                : "CREATE DATABASE will run on the server with the settings below. No existing database is modified.",
            [
                new("Provider", settings.Provider.ToString()),
                new("Server", settings.DisplayServer),
                new("Database", IsSqlite ? Path.GetFileName(settings.DatabaseName) : settings.DatabaseName),
                new("Authentication", settings.Authentication.ToString()),
                new("Operation", IsSqlite ? "Create the database file" : $"CREATE DATABASE [{settings.DatabaseName}]")
            ],
            "Create database"));
        if (!confirmed) return;
        var report = await RunAsync("Creating the database...", _provisioning.CreateDatabaseAsync, TimeSpan.FromMinutes(5));
        if (report is { SchemaState: SchemaState.Missing } && AutomaticDatabaseInitialization && !IsProduction)
            await RunInitializeAsync("Automatic initialization is enabled (non-Production): initializing the schema...");
    }

    [RelayCommand]
    private async Task InitializeSchemaAsync()
    {
        if (BuildSettings() is not { } settings || CurrentReport is null) return;
        var confirmed = _dialogs.Confirm(new ConfirmationRequest(
            "Initialize schema",
            "The pending Secure Plus schema migrations will be applied in one transaction. Existing objects are never dropped, altered or overwritten; on any error everything is rolled back.",
            [
                new("Server", settings.DisplayServer),
                new("Database", settings.DatabaseName),
                new("Authentication", settings.Authentication.ToString()),
                new("Current schema version", CurrentReport.CurrentSchemaVersion?.ToString(CultureInfo.InvariantCulture) ?? "none"),
                new("Target schema version", _provisioning.RequiredSchemaVersion.ToString(CultureInfo.InvariantCulture)),
                new("Operation", "Create the EaaS v3 baseline (204 tables) and the Secure Plus extension tables")
            ],
            "Initialize schema"));
        if (confirmed) await RunInitializeAsync("Initializing the schema...");
    }

    [RelayCommand]
    private async Task RepairSchemaAsync()
    {
        if (BuildSettings() is not { } settings) return;
        var confirmed = _dialogs.Confirm(new ConfirmationRequest(
            "Repair schema",
            "Every Secure Plus migration script is re-applied in one transaction. Each statement only creates objects that are missing, and missing optional columns are added. Nothing is dropped, altered or overwritten; on any error everything is rolled back.",
            [
                new("Server", settings.DisplayServer),
                new("Database", settings.DatabaseName),
                new("Findings", (CurrentReport?.SchemaFindings.Count ?? 0).ToString(CultureInfo.InvariantCulture))
            ],
            "Repair schema"));
        if (confirmed) await RunAsync("Repairing the schema...", _provisioning.RepairSchemaAsync, TimeSpan.FromMinutes(30));
    }

    /// <summary>Saves the settings, then creates the database, installs or upgrades the schema and repairs anything missing.</summary>
    [RelayCommand]
    private async Task InstallOrRepairAsync()
    {
        if (BuildSettings() is not { } settings) return;
        var confirmed = _dialogs.Confirm(new ConfirmationRequest(
            "Install or repair database",
            "The settings are saved, the database is created if it does not exist, and the Secure Plus schema is installed, upgraded or repaired. Existing objects and data are never dropped or overwritten.",
            [new("Server", settings.DisplayServer), new("Database", settings.DatabaseName), new("Authentication", settings.Authentication.ToString())],
            "Install or repair"));
        if (confirmed) await InstallAsync();
    }

    /// <summary>
    /// The first-run pipeline (no confirmation: the caller has already asked). Stores typed secrets, saves the settings,
    /// then check → create database → initialize or upgrade schema → deep validation → repair. Returns the final report.
    /// </summary>
    public async Task<DatabaseStatusReport?> InstallAsync()
    {
        if (IsBusy) return null;
        if (IsSqlLogin && !string.IsNullOrEmpty(SqlPasswordInput)) await StoreSqlPasswordAsync();
        if (IsEntraServicePrincipal && !string.IsNullOrEmpty(ServicePrincipalSecretInput)) await StoreServicePrincipalSecretAsync();
        if (BuildSettings() is not { } settings) return null;
        var saved = await _status.SaveSettingsAsync(settings);
        if (!saved.Succeeded)
        {
            StatusMessage = saved.Error!.ToString();
            return null;
        }

        var report = await RunAsync("Checking the database...", async (current, progress, token) =>
        {
            var result = await _provisioning.CheckDatabaseAsync(current, progress, token);
            if (result.State == DatabaseOverallState.DatabaseMissing) result = await _provisioning.CreateDatabaseAsync(current, progress, token);
            if (result.SchemaState is SchemaState.Missing or SchemaState.MigrationRequired) result = await _provisioning.InitializeSchemaAsync(current, progress, token);
            if (result.DatabaseAccessCheck == CheckState.Passed && result.SchemaState is SchemaState.Current or SchemaState.Incompatible)
            {
                result = await _provisioning.ValidateSchemaAsync(current, progress, token);
                if (result.SchemaState == SchemaState.Incompatible) result = await _provisioning.RepairSchemaAsync(current, progress, token);
            }

            return result;
        }, TimeSpan.FromMinutes(30));
        if (report is not null)
            StatusMessage = report.State == DatabaseOverallState.Ready
                ? $"The database is installed and ready (schema version {report.CurrentSchemaVersion})."
                : Join(report.SanitizedError, report.RecommendedAction) ?? $"The database is not ready: {report.State}.";
        return report;
    }

    [RelayCommand]
    private void Cancel() => _operation?.Cancel();

    [RelayCommand]
    private async Task StoreSqlPasswordAsync()
    {
        var reference = await StoreSecretAsync(SqlPasswordInput, "sql-login", CredentialType.SqlLogin, "SQL login password");
        SqlPasswordInput = string.Empty;
        if (reference is not null) PasswordSecretReference = reference.ToString();
    }

    [RelayCommand]
    private async Task StoreServicePrincipalSecretAsync()
    {
        var reference = await StoreSecretAsync(ServicePrincipalSecretInput, "entra-client-secret", CredentialType.EntraClientSecret, "Database service principal secret");
        ServicePrincipalSecretInput = string.Empty;
        if (reference is not null) EntraClientSecretReference = reference.ToString();
    }

    public override void OnNavigatedFrom() => _operation?.Cancel();

    private async Task RunInitializeAsync(string message) => await RunAsync(message, _provisioning.InitializeSchemaAsync, TimeSpan.FromMinutes(30));

    private async Task<DatabaseStatusReport?> RunAsync(string message, Func<DatabaseSettings, IProgress<string>?, CancellationToken, Task<DatabaseStatusReport>> operation, TimeSpan timeout)
    {
        if (IsBusy || BuildSettings() is not { } settings) return null;
        IsBusy = true;
        ProgressText = message;
        StatusMessage = null;
        _operation = new CancellationTokenSource(timeout);
        var progress = new Progress<string>(text => ProgressText = text);
        Show(CurrentReport is null ? null : CurrentReport with { State = DatabaseOverallState.Testing });
        try
        {
            var token = _operation.Token;
            var report = await Task.Run(() => operation(settings, progress, token), token);
            Show(report);
            _status.Publish(report);
            ReportChanged?.Invoke(this, report);
            return report;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "The operation was cancelled or timed out. Nothing further was changed.";
            if (CurrentReport is { State: DatabaseOverallState.Testing } previous) Show(previous with { State = DatabaseOverallState.Disconnected });
            return null;
        }
        finally
        {
            ProgressText = null;
            IsBusy = false;
            _operation.Dispose();
            _operation = null;
        }
    }

    private async Task<SecretReference?> StoreSecretAsync(string secret, string kind, CredentialType type, string description)
    {
        if (string.IsNullOrEmpty(secret))
        {
            StatusMessage = $"Enter the {description.ToLowerInvariant()} first.";
            return null;
        }

        SecretReference reference;
        try
        {
            var scope = StableIdentifier.FromDisplayName($"{ServerName} {SqlInstance} {DatabaseName}");
            reference = new SecretReference(SecretReference.OperatingSystemProvider, $"database/{scope}/{kind}");
        }
        catch (DomainException)
        {
            StatusMessage = "Enter the server and database name before storing a secret.";
            return null;
        }

        var existing = await _credentials.FindByReferenceAsync(reference);
        var result = existing is null
            ? await _credentials.AddAsync(new NewCredential($"{description} ({DatabaseName})", type, reference, null, null, null), secret)
            : await _credentials.ReplaceAsync(existing.Metadata.CredentialId, secret, null);
        SecretInputsCleared?.Invoke(this, EventArgs.Empty);
        StatusMessage = result.Succeeded ? $"{description} stored securely as {reference}. Save the settings to keep the reference." : result.Error!.ToString();
        return result.Succeeded ? reference : null;
    }

    private DatabaseSettings? BuildSettings()
    {
        ValidationErrors.Clear();
        int? ParseInt(string text, string name)
        {
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
            ValidationErrors.Add($"{name}: enter a whole number.");
            return null;
        }

        SecretReference? ParseReference(string text, string name)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (SecretReference.TryParse(text.Trim(), out var reference)) return reference;
            ValidationErrors.Add($"{name}: use the form provider:name, for example os:database/contoso/sql-login.");
            return null;
        }

        int? port = null;
        if (!string.IsNullOrWhiteSpace(Port)) port = ParseInt(Port, "Port");
        var settings = new DatabaseSettings
        {
            Provider = Provider,
            ServerName = ServerName.Trim(),
            SqlInstance = Blank(SqlInstance),
            Port = port,
            DatabaseName = DatabaseName.Trim(),
            Authentication = Authentication,
            UserName = Blank(UserName),
            PasswordSecretReference = ParseReference(PasswordSecretReference, "Password secret reference"),
            EntraClientId = Blank(EntraClientId),
            EntraTenantId = Blank(EntraTenantId),
            EntraClientSecretReference = ParseReference(EntraClientSecretReference, "Client secret reference"),
            ManagedIdentityClientId = Blank(ManagedIdentityClientId),
            EncryptConnection = EncryptConnection,
            TrustServerCertificate = TrustServerCertificate,
            ConnectionTimeoutSeconds = ParseInt(ConnectionTimeoutSeconds, "Connection timeout") ?? 0,
            CommandTimeoutSeconds = ParseInt(CommandTimeoutSeconds, "Command timeout") ?? 0,
            RetryCount = ParseInt(RetryCount, "Connect retry count") ?? -1,
            RetryDelaySeconds = ParseInt(RetryDelaySeconds, "Connect retry delay") ?? 0,
            ConnectionStringSecretReference = ParseReference(ConnectionStringSecretReference, "Connection string secret reference"),
            AutomaticDatabaseInitialization = AutomaticDatabaseInitialization,
            AutomaticSchemaUpgrade = AutomaticSchemaUpgrade
        };
        foreach (var error in DatabaseConfigurationValidator.Validate(settings)) ValidationErrors.Add($"{error.Key}: {error.Message}");
        return ValidationErrors.Count == 0 ? settings : null;
    }

    private void Load(DatabaseSettings settings)
    {
        Provider = settings.Provider;
        OnProviderChanged(settings.Provider);
        ServerName = settings.ServerName;
        SqlInstance = settings.SqlInstance ?? string.Empty;
        Port = settings.Port?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        DatabaseName = settings.DatabaseName;
        Authentication = settings.Authentication;
        UserName = settings.UserName ?? string.Empty;
        PasswordSecretReference = settings.PasswordSecretReference?.ToString() ?? string.Empty;
        EntraClientId = settings.EntraClientId ?? string.Empty;
        EntraTenantId = settings.EntraTenantId ?? string.Empty;
        EntraClientSecretReference = settings.EntraClientSecretReference?.ToString() ?? string.Empty;
        ManagedIdentityClientId = settings.ManagedIdentityClientId ?? string.Empty;
        EncryptConnection = settings.EncryptConnection;
        TrustServerCertificate = settings.TrustServerCertificate;
        ConnectionTimeoutSeconds = settings.ConnectionTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        CommandTimeoutSeconds = settings.CommandTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        RetryCount = settings.RetryCount.ToString(CultureInfo.InvariantCulture);
        RetryDelaySeconds = settings.RetryDelaySeconds.ToString(CultureInfo.InvariantCulture);
        ConnectionStringSecretReference = settings.ConnectionStringSecretReference?.ToString() ?? string.Empty;
        AutomaticDatabaseInitialization = settings.AutomaticDatabaseInitialization;
        AutomaticSchemaUpgrade = settings.AutomaticSchemaUpgrade;
    }

    private void Show(DatabaseStatusReport? report)
    {
        CurrentReport = report;
        Checks.Clear();
        SchemaFindings.Clear();
        if (report is null) return;
        Checks.Add(new("Configuration", report.ConfigurationCheck, report.ConfigurationCheck == CheckState.Failed ? report.SanitizedError ?? string.Empty : "Settings are valid"));
        Checks.Add(new("Server connection", report.ServerCheck, report.ServerVersion ?? string.Empty));
        Checks.Add(new("Authentication", report.AuthenticationCheck, report.Authentication));
        Checks.Add(new("Database exists", report.DatabaseCheck, report.DatabaseExists switch { true => "Exists", false => "Missing", _ => "Unknown" }));
        Checks.Add(new("Database access", report.DatabaseAccessCheck, string.Empty));
        Checks.Add(new("Schema present", report.SchemaPresentCheck, report.CurrentSchemaVersion is { } version ? $"Version {version}" : string.Empty));
        Checks.Add(new("Schema compatible", report.SchemaCompatibleCheck, report.MigrationState));
        var missing = report.Permissions.Where(pair => !pair.Value).Select(pair => pair.Key).ToArray();
        Checks.Add(new("Permissions", report.PermissionCheck, report.Permissions.Count == 0 ? string.Empty
            : missing.Length == 0 ? "Read, write and schema permissions granted" : $"Missing: {string.Join(", ", missing)}"));
        Checks.Add(new("Overall", report.OverallCheck, report.State.ToString()));
        foreach (var finding in report.SchemaFindings) SchemaFindings.Add(finding);
    }

    private static string? Join(string? first, string? second)
    {
        var text = string.Join(" ", new[] { first, second }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return text.Length == 0 ? null : text;
    }

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
