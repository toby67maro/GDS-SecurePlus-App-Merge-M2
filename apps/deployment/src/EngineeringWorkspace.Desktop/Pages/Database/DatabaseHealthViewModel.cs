using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using System.Windows.Data;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Export;
using EngineeringWorkspace.Desktop.Services;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Desktop.Pages.Reporting;

namespace EngineeringWorkspace.Desktop.Pages.Database;

public sealed record HealthAlertRow(string Severity, string Area, string Message, Brush Fill);

/// <summary>
/// Settings → Database Health: size, disk, CPU, memory, sessions, blocking, waits, growth and backups of the SQL Server
/// or Azure SQL database, refreshed every 45 seconds while the page is visible (paused when the page is left or the
/// window is minimised). Metrics that need VIEW SERVER STATE degrade gracefully; the grant script can be copied for an
/// administrator. For SQLite the file sizes and free disk space are shown.
/// </summary>
public sealed partial class DatabaseHealthViewModel : PageViewModel
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(45);

    private readonly IDatabaseHealthService _health;
    private readonly DispatcherTimer _timer;
    private bool _active;
    private bool _capturing;
    private Window? _window;

    private readonly ExportCoordinator _export;
    private readonly Application.KnowledgeBase.IKnowledgeBaseService _knowledgeBase;

    private readonly Application.Security.ISchedulerHostService? _scheduling;
    private readonly Application.Auditing.IAuditService? _audit;

    public DatabaseHealthViewModel(IDatabaseHealthService health, ExportCoordinator export, Application.KnowledgeBase.IKnowledgeBaseService knowledgeBase,
        Application.Security.ISchedulerHostService? scheduling = null, Application.Auditing.IAuditService? audit = null)
    {
        _scheduling = scheduling;
        _audit = audit;
        _health = health;
        _export = export;
        _knowledgeBase = knowledgeBase;
        CoverageView = CollectionViewSource.GetDefaultView(Coverage);
        CoverageView.Filter = item => item is TableCoverage table && (!CoverageAttentionOnly || (table.ShouldHaveRows && table.Rows == 0)) &&
                                      (CoverageOwner == AllOwners || table.OwnerName == CoverageOwner) &&
                                      (string.IsNullOrWhiteSpace(CoverageSearch) || table.Name.Contains(CoverageSearch.Trim(), StringComparison.OrdinalIgnoreCase) ||
                                       table.Purpose.Contains(CoverageSearch.Trim(), StringComparison.OrdinalIgnoreCase));
        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public ObservableCollection<KpiTile> Kpis { get; } = [];
    public ObservableCollection<HealthAlertRow> Alerts { get; } = [];
    public ObservableCollection<DatabaseFileHealth> Files { get; } = [];
    public ObservableCollection<VolumeHealth> Volumes { get; } = [];
    public ObservableCollection<WaitHealth> Waits { get; } = [];
    public ObservableCollection<BlockingHealth> Blocking { get; } = [];
    public ObservableCollection<ChartBar> Growth { get; } = [];
    public ObservableCollection<string> Limitations { get; } = [];
    public ObservableCollection<TableCoverage> Coverage { get; } = [];
    public ICollectionView CoverageView { get; }
    public const string AllOwners = "(All)";
    public IReadOnlyList<string> CoverageOwners { get; } =
        [AllOwners, "Secure Plus (when used)", "Secure Plus (seed and knowledge base)", "Knowledge base (later phase)", "Other EaaS component"];

    // Scheduling PC (BLUEPRINT §10.1): the one computer that runs recurring assessments and drift checks.
    [ObservableProperty] private string _schedulingCaption = string.Empty;
    [ObservableProperty] private bool _schedulingStale;
    [ObservableProperty] private string _schedulingComputer = string.Empty;
    public string ThisComputer => _scheduling?.ThisComputer ?? Environment.MachineName;

    [ObservableProperty] private string _coverageCaption = "Counting tables...";
    [ObservableProperty] private string _knowledgeBaseCaption = string.Empty;
    [ObservableProperty] private bool _coverageAttentionOnly;
    [ObservableProperty] private string _coverageOwner = AllOwners;
    [ObservableProperty] private string _coverageSearch = string.Empty;

    partial void OnCoverageAttentionOnlyChanged(bool value) => CoverageView.Refresh();
    partial void OnCoverageOwnerChanged(string value) => CoverageView.Refresh();
    partial void OnCoverageSearchChanged(string value) => CoverageView.Refresh();

    [ObservableProperty] private string _title = "Database Health";
    [ObservableProperty] private string _serverCaption = string.Empty;
    [ObservableProperty] private string _overall = string.Empty;
    [ObservableProperty] private Brush? _overallFill;
    [ObservableProperty] private string _lastUpdated = "Not yet refreshed";
    [ObservableProperty] private string _refreshState = string.Empty;
    [ObservableProperty] private string? _grantScript;
    [ObservableProperty] private bool _isServer;
    [ObservableProperty] private bool _isBusy;

    public string ThresholdsCaption =>
        $"Thresholds: disk free below {HealthThresholds.Default.DiskFreeWarningPercent:0}% warning, below {HealthThresholds.Default.DiskFreeCriticalPercent:0}% critical; " +
        $"CPU {HealthThresholds.Default.CpuWarningPercent:0}% / {HealthThresholds.Default.CpuCriticalPercent:0}%; log used {HealthThresholds.Default.LogUsedWarningPercent:0}% / " +
        $"{HealthThresholds.Default.LogUsedCriticalPercent:0}%; full backup older than {HealthThresholds.Default.FullBackupWarningAge.TotalHours:0} h / {HealthThresholds.Default.FullBackupCriticalAge.TotalDays:0} days.";

    public override async Task OnNavigatedToAsync(RouteDescriptor route, CancellationToken cancellationToken)
    {
        await base.OnNavigatedToAsync(route, cancellationToken);
        _active = true;
        _window = System.Windows.Application.Current?.MainWindow;
        if (_window is not null) _window.StateChanged += OnWindowStateChanged;
        await RefreshAsync();
        UpdateTimer();
        await CountTablesAsync();
    }

    private async Task ShowSchedulingAsync()
    {
        if (_scheduling is null) return;
        try
        {
            var host = await _scheduling.GetAsync();
            if (host is null)
            {
                SchedulingCaption = "The scheduling computer is shown when the database is ready.";
                SchedulingStale = false;
                return;
            }

            if (string.IsNullOrEmpty(SchedulingComputer)) SchedulingComputer = host.ComputerName ?? string.Empty;
            SchedulingStale = host.IsStale(DateTimeOffset.UtcNow);
            SchedulingCaption = !host.IsDesignated
                ? "No scheduling computer is set: recurring assessments run on any computer where Secure Plus is open or its Windows task runs. Set one always-on computer (for example the NOC PC)."
                : string.Join(" ", new[]
                {
                    $"{host.ComputerName} runs recurring assessments (set by {host.DesignatedBy} on {host.DesignatedAtUtc?.ToLocalTime():yyyy-MM-dd HH:mm}).",
                    host.HeartbeatUtc is { } beat ? $"Last seen {beat.ToLocalTime():yyyy-MM-dd HH:mm} (version {host.AppVersion})." : "Not seen yet: open Secure Plus on that computer once.",
                    host.LastRunUtc is { } run ? $"Last run {run.ToLocalTime():yyyy-MM-dd HH:mm}: {host.LastMessage}" : string.Empty,
                    SchedulingStale ? $"WARNING: no heartbeat for more than {Application.Security.SchedulerHostStatus.StaleAfter.TotalMinutes:0} minutes. Recurring assessments are not running; check that computer is on and Secure Plus (or its scheduled task) runs there." : string.Empty
                }.Where(part => part.Length > 0));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SchedulingCaption = $"The scheduling computer could not be read: {exception.Message}";
        }
    }

    [RelayCommand]
    private void UseThisComputer() => SchedulingComputer = ThisComputer;

    [RelayCommand]
    private async Task SetSchedulingComputerAsync()
    {
        if (_scheduling is null) return;
        try
        {
            var name = string.IsNullOrWhiteSpace(SchedulingComputer) ? null : SchedulingComputer.Trim();
            await _scheduling.DesignateAsync(name);
            if (_audit is not null)
                await _audit.RecordAsync(Domain.Auditing.AuditEventTypes.SchedulingComputerChanged, Domain.Auditing.AuditOutcome.Success,
                    name is null ? "Scheduling computer cleared: any computer runs recurring assessments." : $"Scheduling computer set to {name}.",
                    new Dictionary<string, string?> { ["Computer"] = name });
            StatusMessage = name is null ? "Any computer now runs recurring assessments." : $"Only {name} runs recurring assessments now.";
            await ShowSchedulingAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or System.Data.Common.DbException)
        {
            StatusMessage = exception.Message;
        }
    }

    [RelayCommand]
    private void ReloadKnowledgeBase()
    {
        KnowledgeBaseCaption = _knowledgeBase.Queue(force: true) is null
            ? "The knowledge base is already loading; see the activity panel."
            : "Reloading the knowledge base in the background; see the activity panel. Count Again when it finishes.";
    }

    private async Task ShowKnowledgeBaseAsync()
    {
        try
        {
            var state = await _knowledgeBase.GetStateAsync();
            KnowledgeBaseCaption = state is null
                ? "Knowledge base: available once the database is ready."
                : state.IsCurrent
                    ? $"Knowledge base {state.LoadedVersion} (SCF, NIST, MITRE ATT&CK; CIS and Microsoft baselines, WBS, question bank; licensing, CTID, tiers, scoring) " +
                      $"loaded {state.LoadedAtUtc?.ToLocalTime():yyyy-MM-dd HH:mm}. {state.Summary}"
                    : state.LoadedVersion is null
                        ? $"Knowledge base {state.EmbeddedVersion} is not loaded yet; it loads in the background (activity panel)."
                        : $"Knowledge base {state.LoadedVersion} is loaded; {state.EmbeddedVersion} is available and loads in the background. {state.Summary}";
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or InvalidOperationException)
        {
            KnowledgeBaseCaption = $"Knowledge base state could not be read: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task CountTablesAsync()
    {
        CoverageCaption = "Counting tables...";
        await ShowKnowledgeBaseAsync();
        try
        {
            var tables = await _health.CountTablesAsync();
            Coverage.Clear();
            foreach (var table in tables.OrderBy(table => table.Owner).ThenBy(table => table.Name, StringComparer.OrdinalIgnoreCase)) Coverage.Add(table);
            var ours = tables.Where(table => table.ShouldHaveRows).ToArray();
            var seedEmpty = ours.Count(table => table.Owner == DataOwner.SecurePlusSeed && table.Rows == 0);
            CoverageCaption = tables.Count == 0
                ? "No tables found: create and initialize the database in Database Configuration."
                : $"{tables.Count} tables, {tables.Count(table => table.Rows > 0)} with data. Secure Plus fills {ours.Length}: {ours.Count(table => table.Rows > 0)} have data" +
                  (seedEmpty > 0 ? $", {seedEmpty} seeded table(s) are empty – wait for the knowledge base to load or run Repair Database." : ".") +
                  $" {tables.Count(table => table.Owner == DataOwner.EaasKnowledgeBase)} are filled by later knowledge-base phases and {tables.Count(table => table.Owner == DataOwner.EaasOtherComponent)} by other EaaS components.";
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or InvalidOperationException)
        {
            CoverageCaption = $"Tables could not be counted: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportCoverageAsync(object? format)
    {
        var rows = CoverageView.Cast<TableCoverage>().Select(table => (IReadOnlyList<string?>)new string?[]
            { table.Name, table.Rows?.ToString(CultureInfo.InvariantCulture), table.State, table.OwnerName, table.Purpose }).ToArray();
        var document = new ExportDocument("Database Data Coverage", null, DateTimeOffset.UtcNow, [new("Database", ServerCaption), new("Summary", CoverageCaption)],
            [new ExportTable("Tables", ["Table", "Rows", "State", "Filled By", "Purpose"], rows)]);
        StatusMessage = await _export.ExportAsync(document, ExportCoordinator.Parse(format)) ?? StatusMessage;
    }

    public override void OnNavigatedFrom()
    {
        _active = false;
        if (_window is not null) _window.StateChanged -= OnWindowStateChanged;
        _window = null;
        UpdateTimer();
        base.OnNavigatedFrom();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        var wasPaused = !_timer.IsEnabled;
        UpdateTimer();
        if (wasPaused && _timer.IsEnabled) _ = RefreshAsync();
    }

    private void UpdateTimer()
    {
        var visible = _active && _window?.WindowState != WindowState.Minimized;
        if (visible && !_timer.IsEnabled) _timer.Start();
        else if (!visible && _timer.IsEnabled) _timer.Stop();
        RefreshState = visible ? $"Refreshes every {RefreshInterval.TotalSeconds:0} seconds while this page is visible." : "Refresh paused while the page is hidden.";
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_capturing) return;
        _capturing = true;
        IsBusy = true;
        try
        {
            Show(await _health.CaptureAsync());
            await ShowSchedulingAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StatusMessage = $"Health could not be read: {exception.Message}";
        }
        finally
        {
            _capturing = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CopyGrantScript()
    {
        if (GrantScript is null) return;
        Clipboard.SetText(GrantScript);
        StatusMessage = "The grant script was copied. An administrator runs it once; the page then shows every metric.";
    }

    private void Show(DatabaseHealthSnapshot snapshot)
    {
        IsServer = snapshot.Provider is DatabaseProvider.SqlServer or DatabaseProvider.AzureSql;
        ServerCaption = snapshot.Error is null
            ? $"{ProviderName(snapshot.Provider)} · {snapshot.Server} · {snapshot.Database}" + (snapshot.Version is null ? string.Empty : $" · {snapshot.Version}") +
              (snapshot.Edition is null ? string.Empty : $" · {snapshot.Edition}")
            : $"{ProviderName(snapshot.Provider)} · {snapshot.Server} · {snapshot.Database}";
        LastUpdated = $"Last updated {snapshot.CapturedAtUtc.ToLocalTime():HH:mm:ss}";
        var alerts = HealthEvaluator.Evaluate(snapshot);
        var overall = HealthEvaluator.Overall(alerts);
        Overall = overall switch
        {
            HealthSeverity.Critical => "Critical",
            HealthSeverity.Warning => "Warning",
            _ => "Healthy"
        };
        OverallFill = Fill(overall);

        Alerts.Clear();
        foreach (var alert in alerts.OrderByDescending(alert => alert.Severity))
            Alerts.Add(new HealthAlertRow(alert.Severity.ToString(), alert.Area, alert.Message, Fill(alert.Severity)));
        Limitations.Clear();
        foreach (var limitation in snapshot.Limitations) Limitations.Add(limitation);
        GrantScript = IsServer && snapshot.Error is null && !snapshot.HasServerState ? HealthEvaluator.GrantScript(snapshot) : null;

        Kpis.Clear();
        if (snapshot.Error is null)
        {
            Kpis.Add(new KpiTile("Data Size", $"{snapshot.DataSizeMb:N0} MB", $"Log {snapshot.LogSizeMb:N0} MB in {snapshot.Files.Count} file(s)"));
            var tightest = snapshot.Volumes.OrderBy(volume => volume.FreePercent).FirstOrDefault();
            Kpis.Add(tightest is null
                ? new KpiTile("Disk Free", "–", $"Needs {snapshot.PermissionName ?? "VIEW SERVER STATE"}")
                : new KpiTile("Disk Free", $"{tightest.FreePercent:0.#}%", $"{tightest.FreeGb:N1} of {tightest.TotalGb:N1} GB on {tightest.Volume}"));
            if (IsServer)
            {
                Kpis.Add(new KpiTile("CPU", Percent(snapshot.SqlCpuPercent), snapshot.SystemCpuPercent is { } system ? $"Server total {system:0}%" : "SQL Server process"));
                Kpis.Add(new KpiTile("Memory", snapshot.SqlMemoryMb is { } memory ? $"{memory:N0} MB" : Percent(snapshot.MemoryPercent),
                    snapshot.TargetMemoryMb is { } target ? $"Target {target:N0} MB ({Percent(snapshot.MemoryPercent)})" : "Memory in use"));
                Kpis.Add(new KpiTile("Sessions", snapshot.UserSessions?.ToString("N0", CultureInfo.CurrentCulture) ?? "–",
                    snapshot.ActiveRequests is { } active ? $"{active} active request(s), {snapshot.Blocking.Count} blocked" : "User sessions"));
                Kpis.Add(new KpiTile("Last Full Backup", snapshot.BackupsManagedByPlatform ? "Automatic" : snapshot.LastFullBackupUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? "None",
                    snapshot.BackupsManagedByPlatform ? "Managed by Azure SQL" : $"Recovery model {snapshot.RecoveryModel ?? "unknown"}" +
                                                                                (snapshot.LastLogBackupUtc is { } log ? $"; log {log.ToLocalTime():yyyy-MM-dd HH:mm}" : string.Empty)));
            }
        }

        Replace(Files, snapshot.Files);
        Replace(Volumes, snapshot.Volumes);
        Replace(Waits, snapshot.TopWaits);
        Replace(Blocking, snapshot.Blocking);
        Growth.Clear();
        var points = snapshot.Growth.OrderBy(point => point.Date).TakeLast(30).ToArray();
        var max = points.Select(point => point.SizeMb).DefaultIfEmpty(0).Max();
        var brush = (Brush?)System.Windows.Application.Current?.TryFindResource("AppBrush.Accent") ?? Brushes.SteelBlue;
        foreach (var point in points)
            Growth.Add(new ChartBar(point.Date.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture), max <= 0 ? 0 : 100 * point.SizeMb / max, $"{point.SizeMb:N1} MB ({point.Source})", brush));
        StatusMessage = null;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    private static string Percent(double? value) => value is { } number ? $"{number:0}%" : "–";

    private static string ProviderName(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer => "SQL Server",
        DatabaseProvider.AzureSql => "Azure SQL",
        DatabaseProvider.SQLite => "SQLite",
        _ => "Not configured"
    };

    private static Brush Fill(HealthSeverity severity) =>
        (Brush?)System.Windows.Application.Current?.TryFindResource(severity switch
        {
            HealthSeverity.Critical => "AppBrush.Error",
            HealthSeverity.Warning => "AppBrush.Warning",
            HealthSeverity.Information => "AppBrush.TextDisabled",
            _ => "AppBrush.Success"
        }) ?? Brushes.Gray;
}
