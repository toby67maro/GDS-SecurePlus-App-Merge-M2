using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Configuration;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.KnowledgeBase;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Desktop.Navigation;

namespace EngineeringWorkspace.Desktop.Pages.Advanced;

/// <summary>One name/value line of the Diagnostics page.</summary>
public sealed record DiagnosticItem(string Name, string Value, string? Path = null)
{
    public bool HasPath => Path is not null;
}

/// <summary>Advanced > Diagnostics: versions, folders, knowledge base, database and configuration issues, with maintenance actions.</summary>
public sealed partial class DiagnosticsViewModel(ApplicationSettings application, PathSettings paths, IKnowledgeBaseService knowledgeBase,
    IDatabaseStatusService database, IConfigurationDiagnostics configuration) : PageViewModel
{
    public ObservableCollection<DiagnosticItem> Environment { get; } = [];
    public ObservableCollection<DiagnosticItem> Folders { get; } = [];
    public ObservableCollection<DiagnosticItem> KnowledgeBase { get; } = [];
    public ObservableCollection<ConfigurationIssue> Issues { get; } = [];

    [ObservableProperty] private string _issueSummary = string.Empty;

    public override async Task OnNavigatedToAsync(RouteDescriptor route, CancellationToken cancellationToken)
    {
        await base.OnNavigatedToAsync(route, cancellationToken);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        Environment.Clear();
        Environment.Add(new("Application version", application.ApplicationVersion));
        Environment.Add(new("Environment", application.Environment));
        Environment.Add(new(".NET runtime", RuntimeInformation.FrameworkDescription));
        Environment.Add(new("Operating system", RuntimeInformation.OSDescription));
        Environment.Add(new("Process", $"{RuntimeInformation.ProcessArchitecture}, {System.Environment.ProcessorCount} logical processors, " +
                                       $"{Process.GetCurrentProcess().WorkingSet64 / 1048576.0:N0} MB working set"));
        Environment.Add(new("Computer / user", $"{System.Environment.MachineName} / {System.Environment.UserName}"));
        var db = database.LatestStatus;
        Environment.Add(new("Database", $"{database.Settings.Provider}: {database.Settings.ServerName} {database.Settings.DatabaseName} — {db?.State.ToString() ?? "not checked"}".Trim()));

        Folders.Clear();
        foreach (var (name, path) in new[]
                 {
                     ("Application", paths.ApplicationDirectory), ("Configuration", paths.ConfigurationDirectory), ("Data", paths.DataDirectory),
                     ("Workspaces", paths.WorkspaceDirectory), ("Customers", paths.CustomerDirectory), ("Import", paths.ImportDirectory),
                     ("Export", paths.ExportDirectory), ("Logs", paths.LogDirectory), ("Audit", paths.AuditDirectory), ("State", paths.StateDirectory)
                 })
            Folders.Add(new(name, path, path));

        KnowledgeBase.Clear();
        try
        {
            if (await knowledgeBase.GetStateAsync() is { } state)
            {
                KnowledgeBase.Add(new("Embedded version", state.EmbeddedVersion));
                KnowledgeBase.Add(new("Loaded version", state.LoadedVersion ?? "not loaded"));
                KnowledgeBase.Add(new("Loaded", state.LoadedAtUtc?.ToLocalTime().ToString("g") ?? "never"));
                KnowledgeBase.Add(new("Current", state.IsCurrent ? "yes" : "no — a load is queued when the database is ready"));
                if (state.Summary is { Length: > 0 } summary) KnowledgeBase.Add(new("Last load", summary));
            }
            else
            {
                KnowledgeBase.Add(new("State", "The database is not ready."));
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
        {
            KnowledgeBase.Add(new("State", exception.Message));
        }

        Issues.Clear();
        foreach (var issue in configuration.Issues.OrderByDescending(issue => issue.Severity)) Issues.Add(issue);
        IssueSummary = Issues.Count == 0 ? "No configuration issues." : $"{Issues.Count} configuration issue(s).";
    }

    [RelayCommand]
    private void ReloadKnowledgeBase()
    {
        StatusMessage = knowledgeBase.Queue(force: true) is null
            ? "The knowledge base could not be queued (is the database ready?)."
            : "The knowledge base reload is queued; progress is shown in the Activity panel.";
    }

    [RelayCommand]
    private void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            StatusMessage = $"{path} could not be opened: {exception.Message}";
        }
    }
}
