using System.Reflection;
using EngineeringWorkspace.Application;
using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Configuration;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Application.State;
using EngineeringWorkspace.Application.Workspaces;
using EngineeringWorkspace.Configuration;
using EngineeringWorkspace.Configuration.Loading;
using EngineeringWorkspace.Configuration.Snapshot;
using EngineeringWorkspace.Configuration.Upgrade;
using EngineeringWorkspace.Desktop.Composition;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Desktop.Shell;
using EngineeringWorkspace.Desktop.Theming;
using EngineeringWorkspace.Domain.Auditing;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Infrastructure;
using EngineeringWorkspace.Infrastructure.Auditing;
using EngineeringWorkspace.Infrastructure.Database;
using EngineeringWorkspace.Infrastructure.Persistence;
using EngineeringWorkspace.Infrastructure.Security;
using EngineeringWorkspace.Infrastructure.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Events;

namespace EngineeringWorkspace.Desktop.Bootstrap;

/// <summary>Result of startup: a running host and shell, or the actionable problems that stopped it.</summary>
public sealed record StartupResult(IHost? Host, MainWindow? Window, ShellViewModel? Shell, string? RestoredRoute, IReadOnlyList<string> Problems, string? DiagnosticPath);

/// <summary>
/// The composition root. Startup order: (1) bootstrap arguments; (2) application, data and configuration directories
/// and the environment; (3) discover, layer, bind and validate configuration; (4) logging; (5) register Application,
/// Configuration, Infrastructure and Desktop services; (6) build and start the host; (7) load appearance and apply the
/// theme; (8) resolve navigation; (9) restore the last workspace; (10) resolve and show MainWindow, then navigate.
/// Nothing here creates or alters a database.
/// </summary>
public sealed class ApplicationBootstrapper
{
    public static string ApplicationVersion { get; } =
        typeof(ApplicationBootstrapper).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(ApplicationBootstrapper).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public async Task<StartupResult> StartAsync(string[] args, CancellationToken cancellationToken)
    {
        // (1)-(2) Bootstrap arguments and directories.
        var applicationDirectory = AppContext.BaseDirectory;
        var dataRoot = Path.GetFullPath(Argument(args, "--data-dir") ?? Environment.GetEnvironmentVariable("ENGINEERINGWORKSPACE_BOOTSTRAP__DATADIRECTORY") ?? ApplicationDataPaths.GetRoot());
        ApplicationDataPaths.EnsureCreated(dataRoot);
        var configurationDirectory = Path.GetFullPath(Argument(args, "--config-dir") ?? Path.Combine(dataRoot, "config"));
        var defaultsDirectory = Path.Combine(configurationDirectory, "Defaults");
        ConfigurationTemplates.WriteDefaults(applicationDirectory, defaultsDirectory);
        if (Argument(args, "--config-dir") is null) ConfigurationTemplates.SeedIfMissing(applicationDirectory, configurationDirectory);
        try
        {
            // Earlier versions used "Secure Plus - Modern Work" for names and the data folder.
            LegacyConfigurationUpgrade.Apply(configurationDirectory,
                ApplicationDataPaths.LegacyRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), dataRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the application starts with the existing values.
            System.Diagnostics.Trace.TraceWarning($"Legacy configuration upgrade skipped: {exception.Message}");
        }

        var previous = await new JsonApplicationStateStore(ApplicationDataPaths.GetDirectories(dataRoot)["State"], NullLogger<JsonApplicationStateStore>.Instance).LoadAsync(cancellationToken);

        // (3) Configuration: INI → layers → typed settings → validation (all four stages) → immutable snapshot.
        var redaction = new RedactionService();
        var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value, StringComparer.OrdinalIgnoreCase);
        var request = new ConfigurationLoadRequest(applicationDirectory, dataRoot, configurationDirectory, args, environment,
            Argument(args, "--workspace") ?? previous.LastWorkspaceId, Argument(args, "--customer") ?? previous.LastCustomerId, ApplicationVersion, defaultsDirectory);
        var load = ConfigurationLoader.Load(request, redaction);
        var snapshot = load.Snapshot;

        // (3b) Release 2.1: settings live in the SecurePlus database. With a reachable database, this computer's INI
        // settings are imported once, then shared and per-computer settings from the database are loaded as layers.
        var importNote = (string?)null;
        if (!load.HasErrors && snapshot.Database.Provider != DatabaseProvider.None)
        {
            var databaseSettings = await LoadDatabaseSettingsAsync(snapshot, configurationDirectory, defaultsDirectory, redaction, cancellationToken);
            if (databaseSettings is { } loaded)
            {
                importNote = loaded.ImportNote;
                load = ConfigurationLoader.Load(request with { SharedSettings = loaded.Shared, ComputerSettings = loaded.Computer }, redaction);
                snapshot = load.Snapshot;
            }
        }

        if (load.HasErrors)
        {
            var problems = snapshot.ValidationErrors.Where(error => error.Severity == ConfigurationSeverity.Error).Select(error => error.Describe(redaction)).ToArray();
            var path = BootstrapDiagnostics.Write(dataRoot, "Configuration validation failed.", snapshot.ValidationErrors.Select(error => $"[{error.Severity}] {error.Stage} {error.ErrorCode}: {error.Describe(redaction)}"));
            await RecordValidationFailureAsync(snapshot, redaction, problems.Length, cancellationToken);
            return new StartupResult(null, null, null, null, problems, path);
        }

        // (4) Logging: Serilog is only a provider behind ILogger<T>.
        Directory.CreateDirectory(snapshot.Paths.LogDirectory);
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Is(Enum.TryParse<LogEventLevel>(snapshot.Logging.MinimumLevel, true, out var level) ? level : LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.File(new RedactingTextFormatter(redaction), Path.Combine(snapshot.Paths.LogDirectory, "secureplus-.log"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: snapshot.Logging.RetainedFileCountLimit, fileSizeLimitBytes: snapshot.Logging.FileSizeLimitMegabytes * 1024L * 1024L,
                rollOnFileSizeLimit: true)
            .CreateLogger();

        // (5)-(6) Services and host.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            Args = args,
            ApplicationName = snapshot.Application.Name,
            EnvironmentName = snapshot.Application.Environment,
            ContentRootPath = applicationDirectory
        });
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(serilog, dispose: true);
        builder.Services.AddEngineeringWorkspaceConfiguration(snapshot);
        builder.Services.AddEngineeringWorkspaceApplication();
        builder.Services.AddEngineeringWorkspaceInfrastructure(Actor());
        builder.Services.AddEngineeringWorkspaceDesktop();
        var host = builder.Build();
        await host.StartAsync(cancellationToken);
        var services = host.Services;
        var logger = services.GetRequiredService<ILogger<ApplicationBootstrapper>>();
        var audit = services.GetRequiredService<IAuditService>();
        logger.LogInformation("Secure Plus Agent {Version} starting in {Environment}; settings from {Source}",
            ApplicationVersion, snapshot.Application.Environment, snapshot.Sources.Any(source => source.Layer == "Shared settings") ? "the SecurePlus database" : configurationDirectory);
        if (importNote is not null) logger.LogInformation("{ImportNote}", importNote);
        foreach (var issue in snapshot.Issues.Where(issue => issue.Severity == ConfigurationSeverity.Warning))
            logger.LogWarning("Configuration warning {Code}: {Message} ({File} [{Section}] {Key})", issue.ErrorCode, issue.Message, issue.FilePath, issue.Section, issue.Key);
        await audit.RecordAsync(AuditEventTypes.ApplicationStarted, AuditOutcome.Success, $"Application {ApplicationVersion} started.",
            new Dictionary<string, string?> { ["Environment"] = snapshot.Application.Environment, ["Version"] = ApplicationVersion }, cancellationToken);
        await audit.RecordAsync(AuditEventTypes.ConfigurationLoaded, AuditOutcome.Success, $"Configuration loaded with {snapshot.Issues.Count} diagnostic(s).",
            new Dictionary<string, string?> { ["ConfigurationDirectory"] = configurationDirectory, ["Warnings"] = snapshot.Issues.Count(issue => issue.Severity == ConfigurationSeverity.Warning).ToString(System.Globalization.CultureInfo.InvariantCulture) },
            cancellationToken);

        // (7) Appearance and theme.
        services.GetRequiredService<IThemeService>().ApplyTheme();
        if (snapshot.Database.Provider != DatabaseProvider.None) await services.GetRequiredService<IDatabaseStatusService>().RefreshAsync(cancellationToken);
        await services.GetRequiredService<WorkspaceThemeCoordinator>().StartAsync(cancellationToken);
        await services.GetRequiredService<ClientRegistration>().RegisterAsync(Actor(), ApplicationVersion, cancellationToken);

        // (8) Navigation.
        PageRegistry.EnsureComplete(services.GetRequiredService<IRouteRegistry>());
        await services.GetRequiredService<IAdvancedModeService>().InitializeAsync(cancellationToken);
        services.GetRequiredService<INavigationService>();

        // (9) Workspace and customer restore.
        var workspaces = services.GetRequiredService<IWorkspaceService>();
        var state = await services.GetRequiredService<IApplicationStateStore>().LoadAsync(cancellationToken);
        if (snapshot.Application.RestoreLastWorkspace || snapshot.Workspace.DefaultWorkspace is not null)
        {
            var restored = await workspaces.RestoreLastAsync(cancellationToken);
            if (!restored.Succeeded) logger.LogWarning("Workspace was not restored: {Reason}", restored.Error!.Message);
        }

        var customerId = workspaces.CurrentWorkspace?.CurrentCustomerId?.Value ?? state.LastCustomerId;
        if (CustomerId.TryCreate(customerId, out var id))
        {
            var selected = await services.GetRequiredService<ICustomerContextService>().SelectAsync(id, null, "Restored at startup", cancellationToken);
            if (!selected.Succeeded) logger.LogWarning("Customer {CustomerId} was not restored: {Reason}", id.Value, selected.Error!.Message);
        }

        // (10) Main window.
        return new StartupResult(host, services.GetRequiredService<MainWindow>(), services.GetRequiredService<ShellViewModel>(), state.LastRoute, [], null);
    }

    /// <summary>Shutdown: save shell and workspace state, ApplicationStopped audit, stop and dispose the host.</summary>
    public static async Task ShutdownAsync(IHost host, ShellViewModel shell)
    {
        var services = host.Services;
        var logger = services.GetRequiredService<ILogger<ApplicationBootstrapper>>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await shell.SaveStateAsync(timeout.Token);
            await services.GetRequiredService<IWorkspaceService>().SaveAsync(timeout.Token);
            await services.GetRequiredService<IAuditService>().RecordAsync(AuditEventTypes.ApplicationStopped, AuditOutcome.Success, "Application stopped.", null, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Shutdown state saving timed out");
        }

        await host.StopAsync(timeout.Token);
        if (services.GetService<IThemeService>() is IDisposable theme) theme.Dispose();
        host.Dispose();
    }

    private static async Task RecordValidationFailureAsync(ConfigurationSnapshot snapshot, RedactionService redaction, int errorCount, CancellationToken cancellationToken)
    {
        var audit = new JsonLinesAuditService(snapshot.Paths.AuditDirectory, new AuditContext(Actor()), redaction, TimeProvider.System, NullLogger<JsonLinesAuditService>.Instance);
        await audit.RecordAsync(AuditEventTypes.ConfigurationValidationFailed, AuditOutcome.Failure, $"Configuration validation failed with {errorCount} error(s).",
            new Dictionary<string, string?> { ["Codes"] = string.Join(",", snapshot.ValidationErrors.Select(error => error.ErrorCode).Distinct()) }, cancellationToken);
    }

    private sealed record DatabaseSettingsLoad(IReadOnlyDictionary<string, string?> Shared, IReadOnlyDictionary<string, string?> Computer, string? ImportNote);

    /// <summary>
    /// Reads settings from the database (20 seconds at most). Returns null when the database is unreachable or older than
    /// schema 7; the application then starts with the shipped defaults and its local files and offers a repair.
    /// </summary>
    private static async Task<DatabaseSettingsLoad?> LoadDatabaseSettingsAsync(ConfigurationSnapshot snapshot, string configurationDirectory, string defaultsDirectory,
        RedactionService redaction, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            Application.Credentials.ISecretStore secrets = OperatingSystem.IsWindows()
                ? new DpapiSecretStore(snapshot.Paths.CredentialDirectory, NullLogger<DpapiSecretStore>.Instance)
                : new UnavailableSecretStore();
            var store = new SqlSettingsStore(new DatabaseConnectionFactory(secrets, snapshot.Paths), Environment.MachineName);
            var loaded = await store.TryLoadAsync(snapshot.Database, timeout.Token);
            if (loaded is null) return null;
            string? note = null;
            if (!loaded.Properties.ContainsKey(SettingsImport.PropertyPrefix + store.ComputerName))
            {
                var result = await SettingsImport.ImportAsync(store, snapshot.Database, configurationDirectory, defaultsDirectory, snapshot.Paths.DataDirectory, redaction, Actor(),
                    timeout.Token);
                note = $"Settings imported into the database: {result.Imported} value(s), {result.KeptFromDatabase} newer value(s) kept; {result.FilesMoved.Count} file(s) moved to {result.BackupFolder ?? "(none)"}.";
                loaded = await store.TryLoadAsync(snapshot.Database, timeout.Token) ?? loaded;
            }

            var shared = new Dictionary<string, string?>(loaded.Shared, StringComparer.OrdinalIgnoreCase);
            // The database's mode (Production or Test, chosen at setup) is the environment of every computer using it.
            if (loaded.Properties.TryGetValue(DatabaseModes.PropertyKey, out var mode) && DatabaseModes.IsValid(mode)) shared["Application:Environment"] = mode;
            return new DatabaseSettingsLoad(shared, loaded.Computer, note);
        }
        catch (Exception exception) when (exception is OperationCanceledException or System.Data.Common.DbException or InvalidOperationException or IOException)
        {
            System.Diagnostics.Trace.TraceWarning($"Settings could not be read from the database: {exception.Message}");
            return null;
        }
    }

    private static string Actor() => string.IsNullOrEmpty(Environment.UserDomainName) ? Environment.UserName : $"{Environment.UserDomainName}\\{Environment.UserName}";

    private static string? Argument(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return args[index][(name.Length + 1)..];
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length) return args[index + 1];
        }

        return null;
    }
}
