using System.ComponentModel;
using System.Windows;
using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Application.State;
using EngineeringWorkspace.Application.Tenants;
using EngineeringWorkspace.Application.Workspaces;
using EngineeringWorkspace.Configuration;
using EngineeringWorkspace.Configuration.Appearance;
using EngineeringWorkspace.Desktop.Bootstrap;
using EngineeringWorkspace.Desktop.Shell;
using EngineeringWorkspace.Desktop.Setup;
using EngineeringWorkspace.Desktop.Theming;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Desktop;

/// <summary>WPF entry point. Startup and shutdown are delegated to the composition root.</summary>
public partial class App : System.Windows.Application
{
    private IHost? _host;
    private ShellViewModel? _shell;
    private bool _shutdownStarted;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        Controls.SelectableText.Install();
        try
        {
            var result = await new ApplicationBootstrapper().StartAsync(e.Args, CancellationToken.None);
            if (result.Host is null || result.Window is null || result.Shell is null)
            {
                ShowStartupError("Configuration problems prevent Secure Plus Agent from starting.", result.Problems, result.DiagnosticPath);
                return;
            }

            _host = result.Host;
            _shell = result.Shell;
            if (e.Args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
            {
                Shutdown(await SmokeTestAsync(result.Window));
                return;
            }

            if (e.Args.Contains("--run-schedules", StringComparer.OrdinalIgnoreCase))
            {
                Shutdown(await RunSchedulesAsync());
                return;
            }

            if (e.Args.Contains("--provision-database", StringComparer.OrdinalIgnoreCase))
            {
                Shutdown(await ProvisionDatabaseAsync());
                return;
            }

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            var savedState = await _host.Services.GetRequiredService<IApplicationStateStore>().LoadAsync();
            if (savedState.ZoomLevel is { } zoom) Services.WindowZoom.Level = zoom;
            if (savedState.ThemePreset is not null) _host.Services.GetRequiredService<Theming.IThemeService>().RestorePreset(savedState.ThemePreset);

            // The application is not installed until its backend database is installed: no menu is shown until then.
            if (await NeedsSetupAsync(_host.Services))
            {
                var setup = _host.Services.GetRequiredService<SetupWindow>();
                if (setup.ShowDialog() != true)
                {
                    await ApplicationBootstrapper.ShutdownAsync(_host, _shell);
                    Shutdown(1);
                    return;
                }
            }

            _ = _host.Services.GetRequiredService<IEngagementService>().SynchronizeAsync();
            MainWindow = result.Window;
            MainWindow.Closing += OnMainWindowClosing;
            MainWindow.Show();
            await _shell.StartAsync(result.RestoredRoute, CancellationToken.None);
            _shell.RestorePinned((await _host.Services.GetRequiredService<IApplicationStateStore>().LoadAsync()).PinnedNavigationIds);
            _shell.Activity.Start();
            _host.Services.GetRequiredService<IAssessmentScheduler>().Start();
            var catalogSync = _host.Services.GetRequiredService<ICatalogSyncService>();
            catalogSync.Start();
            _host.Services.GetRequiredService<Application.KnowledgeBase.IKnowledgeBaseService>().Start();
            _host.Services.GetRequiredService<Application.Governance.IBaselineService>().Start();
            try
            {
                // The embedded catalog (policies, policy sets, frameworks, standards) is loaded into SQL when the database lacks it.
                if (await catalogSync.EnsureCurrentAsync())
                    _shell.Activity.Enqueue(new TickerMessage("Loading the control catalog into the application database.", "Information"));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _host.Services.GetRequiredService<ILogger<App>>().LogWarning(exception, "The catalog database state could not be checked");
            }

            try
            {
                // Pending assessments (for example the initial assessment requested during setup) run now.
                var pending = await _host.Services.GetRequiredService<IAssessmentService>().QueuePendingAsync();
                if (pending > 0) _shell.Activity.Enqueue(new TickerMessage($"Running {pending} pending assessment(s).", "Information"));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _host.Services.GetRequiredService<ILogger<App>>().LogWarning(exception, "Pending assessments could not be queued");
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Last-resort boundary: a failure before or during startup is reported safely and the app exits.
            var path = BootstrapDiagnostics.Write(ApplicationDataPaths.GetRoot(), "Unexpected startup failure.", [exception.GetType().FullName ?? "Exception"], exception);
            ShowStartupError("Secure Plus Agent could not start.", [new RedactionService().RedactText(exception.Message)], path);
        }
    }

    /// <summary>
    /// Setup is shown when the database is not ready, or when first-run setup has never completed (installations that
    /// already have customers and workspaces from an earlier version count as completed).
    /// </summary>
    private static async Task<bool> NeedsSetupAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<IDatabaseStatusService>();
        if (database.Settings.Provider != DatabaseProvider.None)
        {
            try
            {
                await database.RefreshAsync();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                services.GetRequiredService<ILogger<App>>().LogWarning(exception, "Initial database readiness check failed; first-run setup will be shown.");
            }
        }

        // An older schema (for example from the previous release) is upgraded in place; migrations only add objects.
        if (database.LatestStatus is { State: DatabaseOverallState.MigrationRequired } outdated && outdated.SchemaState == SchemaState.MigrationRequired)
        {
            try
            {
                var upgraded = await services.GetRequiredService<IDatabaseProvisioningService>().InitializeSchemaAsync(database.Settings, null, CancellationToken.None);
                database.Publish(upgraded);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                services.GetRequiredService<ILogger<App>>().LogWarning(exception, "Automatic schema upgrade failed; first-run setup will be shown.");
            }
        }

        if (database.LatestStatus?.State != DatabaseOverallState.Ready) return true;
        var stateStore = services.GetRequiredService<IApplicationStateStore>();
        var state = await stateStore.LoadAsync();
        if (state.SetupCompletedVersion is not null) return false;
        var customers = await services.GetRequiredService<ITenantRegistrationService>().ListCustomersAsync();
        var workspaces = await services.GetRequiredService<IWorkspaceStore>().ListAsync();
        if (customers.Count == 0 || workspaces.Count == 0) return true;
        await stateStore.SaveAsync(state with { SetupCompletedVersion = services.GetRequiredService<ApplicationSettings>().ApplicationVersion });
        return false;
    }

    /// <summary>
    /// --smoke-test (used by the release workflow on Windows): opens the setup and main windows off screen and renders
    /// every page view, so XAML, style and template errors fail the build instead of reaching users. Exit code 0 on
    /// success, 1 on any failure (written to standard error and the log). Use with --data-dir pointing at a temporary folder.
    /// </summary>
    private async Task<int> SmokeTestAsync(Window main)
    {
        var logger = _host!.Services.GetRequiredService<ILogger<App>>();
        try
        {
            void Render(Window window)
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -20000;
                window.Top = -20000;
                window.ShowActivated = false;
                window.Show();
                window.UpdateLayout();
                Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }

            var setup = _host.Services.GetRequiredService<SetupWindow>();
            Render(setup);
            setup.Close();

            MainWindow = main;
            Render(main);
            _shell!.Activity.Start();
            _shell.Activity.IsPanelOpen = true;
            foreach (var pageType in Navigation.PageRegistry.ViewModels.Values.Distinct())
            {
                var page = (Navigation.PageViewModel)_host.Services.GetRequiredService(pageType);
                page.Title = pageType.Name;
                _shell.CurrentPage = page;
                main.UpdateLayout();
                Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Console.WriteLine($"Rendered {pageType.Name}");
            }

            // Second pass with data: a SQLite database with the schema, the reference seed and the sample customer,
            // then every route is navigated through the shell so page loads, bindings and templates run with rows.
            await SmokeTestWithDataAsync(main);

            main.Close();
            await ApplicationBootstrapper.ShutdownAsync(_host, _shell);
            Console.WriteLine("Smoke test passed.");
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogError(exception, "Smoke test failed");
            Console.Error.WriteLine($"Smoke test failed: {exception}");
            return 1;
        }
    }

    private async Task SmokeTestWithDataAsync(Window main)
    {
        var services = _host!.Services;
        var database = services.GetRequiredService<IDatabaseStatusService>();
        var provisioning = services.GetRequiredService<IDatabaseProvisioningService>();
        var path = System.IO.Path.Combine(services.GetRequiredService<PathSettings>().DataDirectory, "smoke-test.db");
        var settings = new DatabaseSettings { Provider = DatabaseProvider.SQLite, DatabaseName = path, CommandTimeoutSeconds = 120 };
        var saved = await database.SaveSettingsAsync(settings);
        if (!saved.Succeeded) throw new InvalidOperationException($"Smoke database settings: {saved.Error}");
        var report = await provisioning.CheckDatabaseAsync(database.Settings, null, CancellationToken.None);
        if (report.State == DatabaseOverallState.DatabaseMissing) report = await provisioning.CreateDatabaseAsync(database.Settings, null, CancellationToken.None);
        if (report.SchemaState is SchemaState.Missing or SchemaState.MigrationRequired) report = await provisioning.InitializeSchemaAsync(database.Settings, null, CancellationToken.None);
        report = await database.RefreshAsync();
        if (report.State != DatabaseOverallState.Ready) throw new InvalidOperationException($"Smoke database is {report.State}: {report.SanitizedError}");
        Console.WriteLine($"Smoke database ready: schema version {report.CurrentSchemaVersion}");
        Console.WriteLine(await services.GetRequiredService<ICatalogSyncService>().SeedNowAsync(false, null));
        Console.WriteLine((await services.GetRequiredService<Application.KnowledgeBase.IKnowledgeBaseStore>().LoadAsync(false, null)).Describe());
        var sample = await services.GetRequiredService<IEngagementService>().LoadSampleDataAsync();
        if (!sample.Succeeded) throw new InvalidOperationException($"Sample data: {sample.Error}");

        var navigation = services.GetRequiredService<Application.Navigation.INavigationService>();
        foreach (var route in services.GetRequiredService<Application.Navigation.IRouteRegistry>().All)
        {
            _shell!.NavigationMessage = null;
            var result = await navigation.NavigateAsync(route.Route);
            if (!result.Succeeded)
            {
                Console.WriteLine($"Skipped {route.Route}: {result.Message}");
                continue;
            }

            for (var settle = 0; settle < 10; settle++)
            {
                await Task.Delay(150);
                main.UpdateLayout();
                Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }

            if (_shell.NavigationMessage is { } message && message.Contains("could not be loaded", StringComparison.Ordinal))
                throw new InvalidOperationException($"{route.Route}: {message}");
            Console.WriteLine($"Navigated {route.Route} ({_shell.CurrentPage?.GetType().Name}){(_shell.CurrentPage?.StatusMessage is { } status ? $": {status}" : string.Empty)}");
        }
    }

    /// <summary>
    /// --run-schedules (started by the Windows Task Scheduler task of the signed-in user): no window is shown; due
    /// schedules run, their results are stored, and the process exits. 0 = success (including nothing due), 1 = failure.
    /// </summary>
    private async Task<int> RunSchedulesAsync()
    {
        var logger = _host!.Services.GetRequiredService<ILogger<App>>();
        try
        {
            var database = _host.Services.GetRequiredService<IDatabaseStatusService>();
            if (database.Settings.Provider == DatabaseProvider.None)
            {
                logger.LogInformation("Scheduled run skipped: no application database is configured");
                await ApplicationBootstrapper.ShutdownAsync(_host, _shell!);
                return 0;
            }

            await database.RefreshAsync(CancellationToken.None);
            await _host.Services.GetRequiredService<Application.Governance.IBaselineService>().RefreshAsync();
            var count = await _host.Services.GetRequiredService<IAssessmentScheduler>().RunDueAsync(AssessmentScheduler.TaskSchedulerHost, waitForCompletion: true);
            logger.LogInformation("Scheduled run finished: {Count} schedule(s) ran", count);
            await ApplicationBootstrapper.ShutdownAsync(_host, _shell!);
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogError(exception, "Scheduled run failed");
            return 1;
        }
    }

    /// <summary>
    /// --provision-database (run by the Server and All-in-one installs): with no window, connects to the SQL Server from the install
    /// profile (Windows Authentication), creates the database if it is missing, applies the schema migrations and loads the reference
    /// catalog. Idempotent. 0 = ready, 1 = failed, 2 = refused (Workstation install or no server).
    /// </summary>
    private async Task<int> ProvisionDatabaseAsync()
    {
        var services = _host!.Services;
        var logger = services.GetRequiredService<ILogger<App>>();
        try
        {
            var install = services.GetRequiredService<InstallProfile>();
            if (!install.CanProvision)
            {
                logger.LogWarning("Database provisioning refused: {Reason}", InstallProfile.WorkstationRefusal);
                Console.WriteLine(InstallProfile.WorkstationRefusal);
                return 2;
            }

            var database = services.GetRequiredService<IDatabaseStatusService>();
            var settings = database.Settings;
            if (install.SqlServer is { } server && (settings.Provider == DatabaseProvider.None || !settings.IsServerProvider))
            {
                settings = new DatabaseSettings
                {
                    Provider = DatabaseProvider.SqlServer, ServerName = server, DatabaseName = install.DatabaseName ?? InstallProfile.DefaultDatabaseName,
                    Authentication = DatabaseAuthenticationMethod.WindowsIntegrated, TrustServerCertificate = true, CommandTimeoutSeconds = 600
                };
                var saved = await database.SaveSettingsAsync(settings, CancellationToken.None);
                if (!saved.Succeeded) throw new InvalidOperationException(saved.Error?.Message ?? "The database settings could not be saved.");
            }

            if (!settings.IsServerProvider)
            {
                Console.WriteLine("No SQL Server is set. Run the installer again and enter the SQL Server, or use Settings > Database Configuration.");
                return 2;
            }

            var provisioning = services.GetRequiredService<IDatabaseProvisioningService>();
            var progress = new Progress<string>(message => Console.WriteLine(message));
            var report = await provisioning.CheckDatabaseAsync(settings, progress, CancellationToken.None);
            if (report.DatabaseExists == false) report = await provisioning.CreateDatabaseAsync(settings, progress, CancellationToken.None);
            if (report.FailureCategory == DatabaseFailureCategory.None || report.SchemaState is SchemaState.Missing or SchemaState.MigrationRequired)
                report = await provisioning.InitializeSchemaAsync(settings, progress, CancellationToken.None);
            report = await provisioning.GetStatusAsync(settings, progress, CancellationToken.None);
            database.Publish(report);
            Console.WriteLine($"Database {settings.DatabaseName} on {settings.DisplayServer}: {report.State}, schema version {report.CurrentSchemaVersion}. {report.SanitizedError}");
            if (report.State != DatabaseOverallState.Ready) return 1;

            var catalog = services.GetRequiredService<Application.Catalog.ICatalogSyncService>();
            if (catalog.Queue(force: false) is { } job)
            {
                var jobs = services.GetRequiredService<Application.Jobs.IJobService>();
                Application.Jobs.JobSnapshot? Current() => jobs.Jobs.FirstOrDefault(item => item.Id == job.Id);
                while (Current() is { State: Application.Jobs.JobState.Queued or Application.Jobs.JobState.Running }) await Task.Delay(1000);
                Console.WriteLine($"Reference catalog: {Current()?.State} {Current()?.Result}");
            }

            await ApplicationBootstrapper.ShutdownAsync(_host, _shell!);
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogError(exception, "Database provisioning failed");
            Console.WriteLine($"Database provisioning failed: {exception.Message}");
            return 1;
        }
    }

    private async void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownStarted || _host is null || _shell is null) return;
        e.Cancel = true;
        _shutdownStarted = true;
        try
        {
            await ApplicationBootstrapper.ShutdownAsync(_host, _shell);
        }
        finally
        {
            Shutdown();
        }
    }

    /// <summary>Logs (redacted) and shows a safe message instead of terminating on an unexpected UI exception.</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _host?.Services.GetRequiredService<ILogger<App>>().LogError(e.Exception, "Unhandled exception on the UI thread");
        const string message = "An unexpected error occurred. The operation was not completed. Details were written to the application log.";
        if (EngineeringWorkspace.Desktop.Services.DialogOwner.Current is { } owner) MessageBox.Show(owner, message, "Secure Plus Agent", MessageBoxButton.OK, MessageBoxImage.Error);
        else MessageBox.Show(message, "Secure Plus Agent", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _host?.Services.GetRequiredService<ILogger<App>>().LogError(e.Exception, "Unobserved background task exception");
        e.SetObserved();
    }

    private void ShowStartupError(string heading, IReadOnlyList<string> problems, string? diagnosticPath)
    {
        if (!Resources.MergedDictionaries.Any(dictionary => dictionary.Contains("AppBrush.TextPrimary")))
            Resources.MergedDictionaries.Insert(0, ThemeResourceBuilder.Build(ApplicationProperties.BuiltInDefaults.Appearance, EngineeringWorkspace.Configuration.Appearance.ThemeMode.Light));
        var window = new StartupErrorWindow(heading, problems, diagnosticPath);
        window.Closed += (_, _) => Shutdown(1);
        MainWindow = window;
        window.Show();
    }
}
