using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;

using System.Text.Json;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.GoldenTenant;
using EngineeringWorkspace.Application.Jobs;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Application.Export;
using EngineeringWorkspace.Desktop.Controls;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Desktop.Services;

namespace EngineeringWorkspace.Desktop.Pages.Catalog;

/// <summary>One catalog row: the control plus the selected customer's latest assessment result.</summary>
public sealed partial class CatalogRow(CatalogControl control, bool inGoldenBaseline) : ObservableObject
{
    public CatalogControl Control { get; } = control;
    public string Id => Control.Id;
    public string Kind => Control.KindDisplayName;
    public string Category => Control.Category;
    public string Platform => Control.Platform;
    public string Name => Control.IsVariant ? $"{Control.Name} (alternative of {Control.VariantOf}: {Control.VariantLabel})" : Control.Name;
    public string Setting => Control.Setting;
    public string Value => Control.Value;
    public string Severity => Control.Severity;
    public string UserImpact => Control.UserImpact;
    public string Experience => Control.Experience;
    public string PainPoints => Control.PainPoints;
    public string Frameworks => string.Join("; ", Control.Frameworks);
    public string PolicySets => string.Join(", ", Control.PolicySets);
    public string Source => Control.Source;
    public string Evaluation => Control.IsAutomatic ? "Automatic" : "Manual";
    public bool InGoldenBaseline { get; } = inGoldenBaseline;

    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string? _evidence;
}

public sealed record CatalogDetail(string Name, string Value);

/// <summary>One remediation option shown as a tab: Graph command, PowerShell script, JSON body, GPO or Intune path.</summary>
public sealed record RemediationTab(string Title, string Content, string Language, bool IsGenerated)
{
    public string GeneratedText => IsGenerated ? "Generated – needs validation" : "From the source data";
}

/// <summary>
/// Dashboard → Catalog: every control of the Secure Plus catalog (Conditional Access, Endpoint Security and Intune
/// controls with their Graph bodies, assessment checks, Secure Plus standards and Microsoft security baselines), with
/// baselines, frameworks, user experience and pain points, filters, the selected customer's latest result and export.
/// </summary>
public sealed partial class CatalogViewModel : PageViewModel
{
    private const string All = "(All)";
    private readonly ICatalogService _catalog;
    private readonly IEngagementService _engagements;
    private readonly ICustomerContextService _customers;
    private readonly IGoldenBaselineService _golden;
    private readonly ICatalogSyncService _sync;
    private readonly IJobService _jobs;
    private readonly ExportCoordinator _export;
    private readonly EngineeringWorkspace.Application.Planning.IPlanningRepository? _planning;
    private ICollectionView? _view;

    public CatalogViewModel(ICatalogService catalog, IEngagementService engagements, ICustomerContextService customers, IGoldenBaselineService golden,
        ICatalogSyncService sync, IJobService jobs, ExportCoordinator export, EngineeringWorkspace.Application.Planning.IPlanningRepository? planning = null)
    {
        _planning = planning;
        _export = export;
        FrameworkFilter.Changed += (_, _) => ApplyFilter();
        _catalog = catalog;
        _sync = sync;
        _jobs = jobs;
        _jobs.JobChanged += OnJobChanged;
        _engagements = engagements;
        _customers = customers;
        _golden = golden;
    }

    public ObservableCollection<CatalogRow> Rows { get; } = [];
    public ObservableCollection<CatalogDetail> Details { get; } = [];
    public ObservableCollection<string> Kinds { get; } = [];
    public ObservableCollection<string> Categories { get; } = [];
    public ObservableCollection<string> Platforms { get; } = [];
    public ObservableCollection<string> PolicySetOptions { get; } = [];
    public ObservableCollection<string> Severities { get; } = [];
    public ObservableCollection<string> Statuses { get; } = [];
    public IReadOnlyList<string> Evaluations { get; } = [All, "Automatic", "Manual"];
    public MultiSelectFilter FrameworkFilter { get; } = new("Framework");
    public ObservableCollection<CatalogDetail> ImplementationDetails { get; } = [];
    public ObservableCollection<RemediationTab> RemediationTabs { get; } = [];

    // Expanded state of the Control Details sections, remembered for the session.
    private static bool s_overviewExpanded = true;
    private static bool s_graphExpanded;
    private static bool s_remediationExpanded;
    private static bool s_implementationExpanded = true;

    public bool OverviewExpanded { get => s_overviewExpanded; set => SetProperty(ref s_overviewExpanded, value); }
    public bool GraphExpanded { get => s_graphExpanded; set => SetProperty(ref s_graphExpanded, value); }
    public bool RemediationExpanded { get => s_remediationExpanded; set => SetProperty(ref s_remediationExpanded, value); }
    public bool ImplementationExpanded { get => s_implementationExpanded; set => SetProperty(ref s_implementationExpanded, value); }
    public ICollectionView? View => _view;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _kind = All;
    [ObservableProperty] private string _category = All;
    [ObservableProperty] private string _platform = All;
    [ObservableProperty] private string _policySet = All;
    [ObservableProperty] private string _severity = All;
    [ObservableProperty] private string _evaluation = All;
    [ObservableProperty] private string _status = All;
    [ObservableProperty] private bool _goldenOnly;
    [ObservableProperty] private bool _showVariants;
    [ObservableProperty] private string? _graphRequest;
    [ObservableProperty] private RemediationTab? _selectedRemediation;
    [ObservableProperty] private string _countText = string.Empty;
    [ObservableProperty] private string _sourceText = string.Empty;
    [ObservableProperty] private string _assessmentText = string.Empty;
    [ObservableProperty] private string? _graphBody;
    [ObservableProperty] private string _databaseText = "Checking the application database...";
    [ObservableProperty] private bool _canSyncDatabase;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private CatalogRow? _selectedRow;

    public bool HasSelection => SelectedRow is not null;

    public override async Task OnNavigatedToAsync(RouteDescriptor route, CancellationToken cancellationToken)
    {
        await base.OnNavigatedToAsync(route, cancellationToken);
        var catalog = _catalog.Catalog;
        var golden = _golden.ControlIds;
        Rows.Clear();
        foreach (var control in catalog.Controls) Rows.Add(new CatalogRow(control, golden.Contains(control.Id)));
        Fill(Kinds, catalog.Controls.Select(control => control.KindDisplayName));
        Fill(Platforms, catalog.Controls.Select(control => control.Platform));
        Fill(PolicySetOptions, catalog.PolicySets.Select(set => set.Id).Concat(catalog.Controls.SelectMany(control => control.PolicySets)));
        Fill(Severities, catalog.Controls.Select(control => control.Severity));
        FrameworkFilter.SetOptions(catalog.Controls.SelectMany(control => control.FrameworkReferences)
            .GroupBy(reference => reference.FrameworkCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => (group.Key, group.First().IsComplianceStandard ? $"{group.First().FrameworkName} (compliance standard)" : group.First().FrameworkName))
            .OrderBy(option => option.Item2, StringComparer.CurrentCultureIgnoreCase), []);
        ResetSelections();
        UpdateCategories();
        SourceText = $"{catalog.Controls.Count:N0} controls and {catalog.PolicySets.Count} policy sets from {catalog.Source} (generated {catalog.GeneratedAtUtc}). {catalog.DerivedFieldsNote}";
        _view = CollectionViewSource.GetDefaultView(Rows);
        _view.Filter = Matches;
        OnPropertyChanged(nameof(View));
        await LoadStatusAsync(cancellationToken);
        ApplyFilter();
        await LoadDatabaseStateAsync();
    }

    public override void OnNavigatedFrom()
    {
        _jobs.JobChanged -= OnJobChanged;
        base.OnNavigatedFrom();
    }

    private void OnJobChanged(object? sender, JobSnapshot job)
    {
        if (job.Category == CatalogSyncService.JobCategory && job.IsFinished) Shell.ShellViewModel.OnUi(() => _ = LoadDatabaseStateAsync());
    }

    /// <summary>Loads (or reloads) the catalog into the application database as a background job.</summary>
    [RelayCommand]
    private void SyncDatabase()
    {
        DatabaseText = _sync.Queue(force: true) is null
            ? "The catalog is already being loaded into the database; see the activity panel."
            : "Loading the catalog into the database; progress is shown in the activity panel.";
    }

    private async Task LoadDatabaseStateAsync()
    {
        try
        {
            var state = await Task.Run(() => _sync.GetStateAsync());
            CanSyncDatabase = state.IsAvailable;
            DatabaseText = !state.IsAvailable
                ? "Database: not ready. The catalog is loaded into SQL automatically once the database is installed (Settings > Database Configuration)."
                : state.Version is null
                    ? "Database: the catalog has not been loaded yet. It loads automatically in the background, or select Load into database."
                    : $"Database: catalog {state.Version} loaded {state.SyncedAtUtc?.ToLocalTime():g}: {state.Controls:N0} controls, {state.PolicySets} policy sets, " +
                      $"{state.Frameworks} frameworks, {state.Requirements} requirements (pol, fw, gds and secureplus.CatalogControl tables)" +
                      (state.IsCurrent(_catalog.Catalog) ? "." : ". A newer catalog is waiting to be loaded.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            CanSyncDatabase = false;
            DatabaseText = $"Database: the catalog state could not be read ({exception.Message}).";
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnShowVariantsChanged(bool value) => ApplyFilter();
    partial void OnCategoryChanged(string value) => ApplyFilter();
    partial void OnPlatformChanged(string value) => ApplyFilter();
    partial void OnPolicySetChanged(string value) => ApplyFilter();
    partial void OnSeverityChanged(string value) => ApplyFilter();
    partial void OnEvaluationChanged(string value) => ApplyFilter();
    partial void OnStatusChanged(string value) => ApplyFilter();
    partial void OnGoldenOnlyChanged(bool value) => ApplyFilter();

    partial void OnKindChanged(string value)
    {
        UpdateCategories();
        ApplyFilter();
    }

    private async Task AddKnowledgeRemediationAsync(CatalogControl control)
    {
        if (_planning is not { IsAvailable: true } planning) return;
        try
        {
            var keys = RemediationOptions.KnowledgeKeys(control);
            if (keys.Count == 0) return;
            var settings = await planning.FindSettingsAsync(keys);
            if (SelectedRow?.Control.Id != control.Id) return;
            foreach (var tab in RemediationOptions.FromKnowledge(control, settings)) RemediationTabs.Add(tab);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
        {
            // The knowledge base is optional here; the control's own options are already shown.
        }
    }

    partial void OnSelectedRowChanged(CatalogRow? value)
    {
        Details.Clear();
        ImplementationDetails.Clear();
        RemediationTabs.Clear();
        GraphBody = null;
        GraphRequest = null;
        if (value is null) return;
        var control = value.Control;
        var catalog = _catalog.Catalog;
        static void Add(ObservableCollection<CatalogDetail> target, string name, string? text)
        {
            if (!string.IsNullOrWhiteSpace(text)) target.Add(new CatalogDetail(name, text));
        }

        // 1. Overview / Description
        Add(Details, "ID", control.Id);
        Add(Details, "Name", control.Name);
        Add(Details, "Description", control.Description);
        Add(Details, "Alternative Of", control.IsVariant ? $"{control.VariantOf} ({control.VariantLabel})" : null);
        Add(Details, "Kind", control.KindDisplayName);
        Add(Details, "Category", control.Category);
        Add(Details, "Platform", control.Platform);
        Add(Details, "Setting", control.Setting);
        Add(Details, "Target Value", control.Value);
        Add(Details, "Assessment Result", string.IsNullOrEmpty(value.Status) ? null : $"{value.Status}{(value.Evidence is null ? string.Empty : $": {value.Evidence}")}");
        Add(Details, "Solution Area", catalog.SolutionAreaName(control.SolutionArea));
        Add(Details, "Experience", catalog.ExperienceAreaName(control.ExperienceArea));
        Add(Details, "Baselines (Policy Sets)", string.Join(", ", control.PolicySets));
        Add(Details, "Golden Tenant Baseline", value.InGoldenBaseline ? "Yes" : null);
        Add(Details, "Frameworks", string.Join("; ", control.FrameworkReferences.Where(reference => !reference.IsComplianceStandard).Select(reference => reference.Label)));
        Add(Details, "Compliance Standards", string.Join("; ", control.FrameworkReferences.Where(reference => reference.IsComplianceStandard).Select(reference => reference.Label)));
        Add(Details, "Source", control.Source);
        Add(Details, "Evaluation", control.IsAutomatic ? $"Automatic ({control.Evaluation})" : "Manual");
        Add(Details, "Graph Type", control.OdataType);
        Add(Details, "Template Family", control.TemplateFamily);
        foreach (var expected in control.Expected.Take(20)) Add(Details, $"Expected: {expected.Path}", expected.Value.ToString());
        foreach (var (name, text) in control.Attributes) Add(Details, name, text);
        Add(Details, "Repository Path", control.Path);

        // 2. Microsoft Graph Request
        var remediation = control.Remediation;
        if (!string.IsNullOrEmpty(control.Body))
        {
            using var document = JsonDocument.Parse(control.Body);
            GraphBody = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            GraphRequest = string.IsNullOrEmpty(remediation.GraphRequest) ? $"POST https://graph.microsoft.com/beta/{control.GraphResource}" : remediation.GraphRequest.Split('\n')[0];
        }

        // 3. Remediation Options: every type the control's data supports, then the knowledge base's GPO, registry, OMA-URI and procedures.
        foreach (var tab in RemediationOptions.FromControl(control)) RemediationTabs.Add(tab);
        _ = AddKnowledgeRemediationAsync(control);
        SelectedRemediation = RemediationTabs.FirstOrDefault();

        // 4. Implementation Details
        var implementation = control.Implementation;
        var generated = control.IsGenerated("implementation") ? " (generated – needs validation)" : string.Empty;
        Add(ImplementationDetails, "Estimated Time to Remediate", implementation.EstimatedMinutes > 0 ? implementation.EstimatedTimeText + generated : null);
        Add(ImplementationDetails, "Pain Points", control.PainPoints);
        Add(ImplementationDetails, "Difficulty", implementation.Difficulty);
        Add(ImplementationDetails, "Effort", implementation.Effort);
        Add(ImplementationDetails, "Risk Level", implementation.RiskLevel);
        Add(ImplementationDetails, "Severity", control.Severity);
        Add(ImplementationDetails, "Benefits", implementation.Benefits);
        Add(ImplementationDetails, "Experience Impact", implementation.ExperienceImpact);
        Add(ImplementationDetails, "User Impact", control.UserImpact);
        Add(ImplementationDetails, "Description", control.Description);
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = string.Empty;
        FrameworkFilter.Reset();
        ShowVariants = false;
        Kind = All;
        Category = All;
        Platform = All;
        PolicySet = All;
        Severity = All;
        Evaluation = All;
        Status = All;
        GoldenOnly = false;
    }

    [RelayCommand]
    private async Task ExportAsync(object? format)
    {
        if (_view is null) return;
        var rows = _view.Cast<CatalogRow>().ToArray();
        var document = new ExportDocument("Control Catalog", _customers.CurrentCustomer?.Name, DateTimeOffset.UtcNow,
            [new("Controls", rows.Length.ToString("N0", CultureInfo.CurrentCulture)), new("Catalog", _catalog.Catalog.Version)],
            [new ExportTable("Controls",
                ["ID", "Kind", "Category", "Platform", "Name", "Setting", "Value", "Severity", "Solution Area", "Experience", "User Impact", "Pain Points", "Policy Sets",
                 "Frameworks", "Source", "Evaluation", "Estimated Time", "Difficulty", "Risk Level", "Result", "Evidence"],
                rows.Select(row => (IReadOnlyList<string?>)new string?[]
                {
                    row.Id, row.Kind, row.Category, row.Platform, row.Name, row.Setting, row.Value, row.Severity, _catalog.Catalog.SolutionAreaName(row.Control.SolutionArea),
                    _catalog.Catalog.ExperienceAreaName(row.Control.ExperienceArea), row.UserImpact, row.PainPoints, row.PolicySets, row.Frameworks, row.Source,
                    row.Evaluation, row.Control.Implementation.EstimatedTimeText, row.Control.Implementation.Difficulty, row.Control.Implementation.RiskLevel, row.Status, row.Evidence
                }).ToArray())]);
        try
        {
            StatusMessage = await _export.ExportAsync(document, ExportCoordinator.Parse(format)) ?? StatusMessage;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"The export could not be written: {exception.Message}";
        }
    }

    private async Task LoadStatusAsync(CancellationToken cancellationToken)
    {
        Fill(Statuses, []);
        Status = All;
        if (_customers.CurrentCustomer is not { } customer || !_engagements.IsDatabaseAvailable)
        {
            AssessmentText = "Select a customer with a completed assessment to see each control's result.";
            return;
        }

        var history = await _engagements.ListAssessmentsAsync(customer.Id, cancellationToken);
        var latest = history.Succeeded ? history.Value!.FirstOrDefault(item => AssessmentStatusCodes.HasResults(item.StatusCode)) : null;
        if (latest is null)
        {
            AssessmentText = $"{customer.Name} has no completed assessment yet.";
            return;
        }

        var findings = await _engagements.ListFindingsAsync(latest.AssessmentRunId, cancellationToken);
        if (!findings.Succeeded) return;
        var byId = findings.Value!.Where(finding => finding.ControlId is not null).GroupBy(finding => _catalog.Catalog.ResolveId(finding.ControlId!))
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows)
            if (byId.TryGetValue(row.Id, out var finding))
            {
                row.Status = finding.Result;
                row.Evidence = finding.Evidence;
            }

        Fill(Statuses, byId.Values.Select(finding => finding.Result));
        Status = All;
        AssessmentText = $"Results of {customer.Name}'s assessment of {latest.CompletedAtUtc?.ToLocalTime():g} (score {latest.Score:0.#}%).";
    }

    private bool Matches(object item)
    {
        if (item is not CatalogRow row) return false;
        var control = row.Control;
        if (Kind != All && row.Kind != Kind) return false;
        if (Category != All && control.Category != Category) return false;
        if (Platform != All && control.Platform != Platform) return false;
        if (PolicySet != All && !control.PolicySets.Contains(PolicySet)) return false;
        if (Severity != All && control.Severity != Severity) return false;
        if (Evaluation != All && row.Evaluation != Evaluation) return false;
        if (Status != All && row.Status != Status) return false;
        if (GoldenOnly && !row.InGoldenBaseline) return false;
        if (control.IsVariant && !ShowVariants) return false;
        if (FrameworkFilter.IsActive && !FrameworkFilter.MatchesAny(control.FrameworkReferences.Select(reference => reference.FrameworkCode))) return false;
        if (SearchText.Length == 0) return true;
        return SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(term =>
            control.Id.Contains(term, StringComparison.OrdinalIgnoreCase) || control.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            control.Setting.Contains(term, StringComparison.OrdinalIgnoreCase) || control.Value.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            control.Category.Contains(term, StringComparison.OrdinalIgnoreCase) || control.Description.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            control.PainPoints.Contains(term, StringComparison.OrdinalIgnoreCase) || control.Experience.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyFilter()
    {
        if (_view is null) return;
        _view.Refresh();
        CountText = $"Showing {_view.Cast<object>().Count():N0} of {Rows.Count:N0} controls";
    }

    /// <summary>Every drop-down starts on "(All)".</summary>
    private void ResetSelections()
    {
        Kind = All;
        Category = All;
        Platform = All;
        PolicySet = All;
        Severity = All;
        Evaluation = All;
        Status = All;
    }

    private void UpdateCategories()
    {
        var controls = _catalog.Catalog.Controls.Where(control => Kind == All || control.KindDisplayName == Kind);
        Fill(Categories, controls.Select(control => control.Category));
        if (!Categories.Contains(Category)) Category = All;
    }

    private static void Fill(ObservableCollection<string> target, IEnumerable<string> values)
    {
        target.Clear();
        target.Add(All);
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Order(StringComparer.CurrentCulture))
            target.Add(value);
    }

}
