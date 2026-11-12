using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Configuration;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Desktop.Services;
using EngineeringWorkspace.Domain.Auditing;

namespace EngineeringWorkspace.Desktop.Pages.Auditing;

/// <summary>
/// Workspace → Settings → Auditing and Logs: the effective-configuration view (from the immutable configuration
/// snapshot, never raw IConfiguration; secrets always redacted), configuration issues and sources, and recent audit events.
/// </summary>
public sealed partial class AuditingAndLogsViewModel : PageViewModel
{
    private const string All = "(all)";
    private readonly IConfigurationDiagnostics _diagnostics;
    private readonly IAuditReader _audit;
    private readonly IShellLauncher _shell;
    private readonly PathSettings _paths;

    public AuditingAndLogsViewModel(IConfigurationDiagnostics diagnostics, IAuditReader audit, IShellLauncher shell, PathSettings paths)
    {
        _diagnostics = diagnostics;
        _audit = audit;
        _shell = shell;
        _paths = paths;
        Layers = [All, .. diagnostics.Entries.OrderBy(entry => entry.LayerOrder).Select(entry => entry.Layer).Distinct()];
        Sections = [All, .. diagnostics.Entries.Select(entry => entry.Section).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
        States = [All, .. Enum.GetNames<ConfigurationEntryState>()];
        foreach (var issue in diagnostics.Issues.OrderByDescending(issue => issue.Severity)) Issues.Add(issue);
        foreach (var source in diagnostics.Sources) Sources.Add(source);
        ApplyFilter();
    }

    public IReadOnlyList<string> Layers { get; }
    public IReadOnlyList<string> Sections { get; }
    public IReadOnlyList<string> States { get; }
    public ObservableCollection<ConfigurationEntryDiagnostic> Entries { get; } = [];
    public ObservableCollection<ConfigurationIssue> Issues { get; } = [];
    public ObservableCollection<ConfigurationSourceDiagnostic> Sources { get; } = [];
    public ObservableCollection<AuditEvent> AuditEvents { get; } = [];
    public string LoadedAt => _diagnostics.LoadedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);
    public string LogDirectory => _paths.LogDirectory;
    public string AuditDirectory => _paths.AuditDirectory;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _selectedLayer = All;
    [ObservableProperty] private string _selectedSection = All;
    [ObservableProperty] private string _selectedState = All;
    [ObservableProperty] private string _entrySummary = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedLayerChanged(string value) => ApplyFilter();
    partial void OnSelectedSectionChanged(string value) => ApplyFilter();
    partial void OnSelectedStateChanged(string value) => ApplyFilter();

    public override async Task OnNavigatedToAsync(RouteDescriptor route, CancellationToken cancellationToken)
    {
        await base.OnNavigatedToAsync(route, cancellationToken);
        await RefreshAuditAsync();
    }

    [RelayCommand]
    private async Task RefreshAuditAsync()
    {
        AuditEvents.Clear();
        foreach (var auditEvent in await _audit.ReadRecentAsync(200)) AuditEvents.Add(auditEvent);
    }

    [RelayCommand]
    private void OpenLogs()
    {
        if (!_shell.OpenFolder(_paths.LogDirectory)) StatusMessage = $"{_paths.LogDirectory} does not exist yet.";
    }

    [RelayCommand]
    private void OpenAudit()
    {
        if (!_shell.OpenFolder(_paths.AuditDirectory)) StatusMessage = $"{_paths.AuditDirectory} does not exist yet.";
    }

    private void ApplyFilter()
    {
        var search = SearchText.Trim();
        var filtered = _diagnostics.Entries.Where(entry =>
            (SelectedLayer == All || entry.Layer == SelectedLayer) &&
            (SelectedSection == All || string.Equals(entry.Section, SelectedSection, StringComparison.OrdinalIgnoreCase)) &&
            (SelectedState == All || entry.ValidationState.ToString() == SelectedState) &&
            (search.Length == 0 || entry.Key.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             (entry.RedactedValue?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
             entry.SourceFile.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        Entries.Clear();
        foreach (var entry in filtered) Entries.Add(entry);
        EntrySummary = $"{filtered.Length} of {_diagnostics.Entries.Count} keys";
    }
}
