using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Export;
using EngineeringWorkspace.Application.Governance;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Application.Reporting;
using EngineeringWorkspace.Desktop.Controls;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Desktop.Pages.Specifications;
using EngineeringWorkspace.Desktop.Services;

namespace EngineeringWorkspace.Desktop.Pages.Settings;

/// <summary>A baseline row: Active saves immediately; "Show Compliance" adds it to the customer panel.</summary>
public sealed partial class BaselineRow : ObservableObject
{
    private readonly Func<BaselineRow, bool, Task> _setActive;
    private readonly Action _selectionChanged;
    private bool _loading;

    public BaselineRow(BaselineRecord record, int controls, Func<BaselineRow, bool, Task> setActive, Action selectionChanged, bool showCompliance)
    {
        Record = record;
        Controls = controls;
        _setActive = setActive;
        _selectionChanged = selectionChanged;
        _loading = true;
        IsActive = record.IsActive;
        ShowCompliance = showCompliance;
        _loading = false;
    }

    public BaselineRecord Record { get; }
    public string Name => Record.Name;
    public string Type => Record.IsSystem ? Record.IsModified ? "GDS (customised)" : "GDS" : "Custom";
    public int Controls { get; }
    public string Modified => (Record.ModifiedAtUtc ?? Record.CreatedAtUtc).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _showCompliance;

    partial void OnIsActiveChanged(bool value)
    {
        if (!_loading) _ = _setActive(this, value);
    }

    partial void OnShowComplianceChanged(bool value)
    {
        if (!_loading) _selectionChanged();
    }
}

public sealed record CustomerComplianceRow(string Customer, string Assessed, double? Percent, int Satisfied, int Gaps, int NotEvaluated, string Status)
{
    public string PercentText => Percent is { } value ? $"{value:0.#}%" : "–";
}

/// <summary>
/// Settings → Baseline Configurations: the baselines customers are measured against. Activate or deactivate them
/// (the score covers the active ones), build new ones, clone, edit and delete; configure their frameworks, compliance
/// standards, experiences, policy sets, controls and policies. The bottom panel shows, for the baselines ticked under
/// Show Compliance, every customer's compliance from its latest stored assessment.
/// </summary>
public sealed partial class BaselineConfigurationsViewModel : PageViewModel
{
    private readonly IBaselineService _baselines;
    private readonly IReportingService _reporting;
    private readonly ICustomerProfileStore _profiles;
    private readonly ICatalogService _catalog;
    private readonly IDialogService _dialogs;
    private readonly AuditContext _audit;
    private readonly ExportCoordinator _export;
    private bool _loadingEditor;

    public BaselineConfigurationsViewModel(IBaselineService baselines, IReportingService reporting, ICustomerProfileStore profiles, ICatalogService catalog, IDialogService dialogs,
        AuditContext audit, ExportCoordinator export)
    {
        _baselines = baselines;
        _reporting = reporting;
        _profiles = profiles;
        _catalog = catalog;
        _dialogs = dialogs;
        _audit = audit;
        _export = export;
        var current = catalog.Catalog;
        Experiences.SetOptions(current.ExperienceAreas.Select(area => (area.Code, area.Name)));
        var references = current.Controls.SelectMany(control => control.FrameworkReferences).ToArray();
        Frameworks.SetOptions(references.Where(reference => !reference.IsComplianceStandard).GroupBy(reference => reference.FrameworkCode).Select(group => (group.Key, group.First().FrameworkName)));
        Standards.SetOptions(references.Where(reference => reference.IsComplianceStandard).GroupBy(reference => reference.FrameworkCode).Select(group => (group.Key, group.First().FrameworkName)));
        PolicySets.SetOptions(current.PolicySets.OrderBy(set => set.DeploymentOrder).Select(set => (set.Id, set.DisplayName)));
        foreach (var control in current.Controls.Where(control => !control.IsVariant)) Controls.Add(new ControlChoice(control));
        ControlsView = CollectionViewSource.GetDefaultView(Controls);
        ControlsView.Filter = item => item is ControlChoice choice && (!SelectedOnly || choice.IsSelected) &&
                                      (string.IsNullOrWhiteSpace(ControlSearch) || choice.Id.Contains(ControlSearch.Trim(), StringComparison.OrdinalIgnoreCase) ||
                                       choice.Name.Contains(ControlSearch.Trim(), StringComparison.CurrentCultureIgnoreCase));
        foreach (var choice in Controls) choice.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ControlChoice.IsSelected) && !_loadingEditor) UpdateSummary();
        };
        foreach (var filter in new[] { Experiences, Frameworks, Standards, PolicySets }) filter.Changed += (_, _) => UpdateSummary();
    }

    public ObservableCollection<BaselineRow> Rows { get; } = [];
    public ObservableCollection<CustomerComplianceRow> Compliance { get; } = [];
    public MultiSelectFilter Experiences { get; } = new("Experiences", emptyMeansAll: false);
    public MultiSelectFilter Frameworks { get; } = new("Frameworks", emptyMeansAll: false);
    public MultiSelectFilter Standards { get; } = new("Compliance Standards", emptyMeansAll: false);
    public MultiSelectFilter PolicySets { get; } = new("Policy Sets", emptyMeansAll: false);
    public ObservableCollection<ControlChoice> Controls { get; } = [];
    public ICollectionView ControlsView { get; }
    public IReadOnlyList<int> Thresholds { get; } = [100, 95, 90, 80, 70];

    [ObservableProperty] private int _threshold = 90;
    [ObservableProperty] private string _complianceCaption = "Tick Show Compliance on one or more baselines.";
    [ObservableProperty] private bool _isEditorOpen;
    [ObservableProperty] private string? _editingKey;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _controlSearch = string.Empty;
    [ObservableProperty] private bool _selectedOnly;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string? _editorMessage;

    public string EditorTitle => EditingKey is null ? "Build New Baseline" : "Edit Baseline";

    partial void OnEditingKeyChanged(string? value) => OnPropertyChanged(nameof(EditorTitle));
    partial void OnThresholdChanged(int value) => _ = LoadComplianceAsync();
    partial void OnControlSearchChanged(string value) => ControlsView.Refresh();
    partial void OnSelectedOnlyChanged(bool value) => ControlsView.Refresh();

    public override async Task OnNavigatedToAsync(RouteDescriptor route, CancellationToken cancellationToken)
    {
        await base.OnNavigatedToAsync(route, cancellationToken);
        await LoadAsync();
    }

    private async Task LoadAsync(string? select = null)
    {
        var shown = Rows.Where(row => row.ShowCompliance).Select(row => row.Record.BaselineKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Rows.Clear();
        try
        {
            await _baselines.RefreshAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
        {
            StatusMessage = $"The baselines could not be read: {exception.Message}";
            return;
        }

        if (_baselines.Baselines.Count == 0)
        {
            StatusMessage = "No baselines yet: they are imported with the reference data once the database is ready (Settings > Database Configuration).";
            return;
        }

        foreach (var record in _baselines.Baselines)
            Rows.Add(new BaselineRow(record, _baselines.ControlIdsOf(record).Count, SetActiveAsync, () => _ = LoadComplianceAsync(),
                shown.Count == 0 ? record.IsActive : shown.Contains(record.BaselineKey) || record.BaselineKey == select));
        StatusMessage = $"{Rows.Count(row => row.IsActive)} active baseline(s) cover {_baselines.ControlIds.Count:N0} control(s); assessment scores use them.";
        await LoadComplianceAsync();
    }

    private async Task SetActiveAsync(BaselineRow row, bool active)
    {
        await _baselines.SetActiveAsync(row.Record.BaselineKey, active);
        StatusMessage = $"'{row.Name}' {(active ? "activated" : "deactivated")}. {Rows.Count(item => item.IsActive)} active baseline(s) cover {_baselines.ControlIds.Count:N0} control(s).";
    }

    private async Task LoadComplianceAsync()
    {
        Compliance.Clear();
        var selected = Rows.Where(row => row.ShowCompliance).ToArray();
        if (selected.Length == 0)
        {
            ComplianceCaption = "Tick Show Compliance on one or more baselines.";
            return;
        }

        var ids = selected.SelectMany(row => _baselines.ControlIdsOf(row.Record)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var compliant = 0;
        foreach (var profile in (await _profiles.ListAsync()).OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!_reporting.IsAvailable) break;
            var results = await _reporting.GetCustomerResultsAsync(profile.Id.Value, ReportScope.AllEvaluated);
            if (!results.HasResults)
            {
                Compliance.Add(new CustomerComplianceRow(profile.Name, "Never", null, 0, 0, 0, "Not assessed"));
                continue;
            }

            var facts = results.Facts.Where(fact => ids.Contains(fact.ControlId)).ToArray();
            var row = Completion.Summarize(profile.Id.Value, profile.Name, facts);
            var status = row.Percent is null ? "Not evaluated" : row.Percent >= Threshold ? "Compliant" : "Not compliant";
            compliant += status == "Compliant" ? 1 : 0;
            Compliance.Add(new CustomerComplianceRow(profile.Name, (results.Latest!.CompletedAtUtc ?? results.Latest.RequestedAtUtc).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture),
                row.Percent, row.Satisfied + row.Excepted, row.Gaps, row.NotEvaluated, status));
        }

        ComplianceCaption = $"{compliant} of {Compliance.Count} customer(s) compliant ({Threshold}% or more) with {string.Join(" + ", selected.Select(row => row.Name))} ({ids.Count:N0} controls).";
    }

    // ----- Editor -----

    [RelayCommand]
    private void New() => LoadEditor(null, null);

    [RelayCommand]
    private void Edit(BaselineRow? row)
    {
        if (row is not null) LoadEditor(row.Record, row.Record.BaselineKey);
    }

    [RelayCommand]
    private void Clone(BaselineRow? row)
    {
        if (row is null) return;
        LoadEditor(row.Record with { Name = $"{row.Name} (copy)" }, null);
        EditorMessage = $"Cloned from '{row.Name}'. Adjust it, then save.";
    }

    [RelayCommand]
    private void CloseEditor() => IsEditorOpen = false;

    private void LoadEditor(BaselineRecord? record, string? key)
    {
        _loadingEditor = true;
        EditingKey = key;
        Name = record?.Name ?? string.Empty;
        Description = record?.Description ?? string.Empty;
        Experiences.Select(record?.ItemsOf(ProfileItemTypes.Experience) ?? []);
        Frameworks.Select(record?.ItemsOf(ProfileItemTypes.Framework) ?? []);
        Standards.Select(record?.ItemsOf(ProfileItemTypes.ComplianceStandard) ?? []);
        PolicySets.Select(record?.ItemsOf(ProfileItemTypes.PolicySet) ?? []);
        var ids = (record?.ItemsOf(ProfileItemTypes.Control) ?? []).Concat(record?.ItemsOf(ProfileItemTypes.Policy) ?? []).Select(_catalog.Catalog.ResolveId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var choice in Controls) choice.IsSelected = ids.Contains(choice.Id);
        _loadingEditor = false;
        SelectedOnly = ids.Count > 0;
        ControlSearch = string.Empty;
        EditorMessage = null;
        UpdateSummary();
        IsEditorOpen = true;
    }

    private IReadOnlyList<ProfileItem> EditorItems() =>
        Experiences.SelectedValues.Select(value => new ProfileItem(ProfileItemTypes.Experience, value))
            .Concat(Frameworks.SelectedValues.Select(value => new ProfileItem(ProfileItemTypes.Framework, value)))
            .Concat(Standards.SelectedValues.Select(value => new ProfileItem(ProfileItemTypes.ComplianceStandard, value)))
            .Concat(PolicySets.SelectedValues.Select(value => new ProfileItem(ProfileItemTypes.PolicySet, value)))
            .Concat(Controls.Where(choice => choice.IsSelected).Select(choice => new ProfileItem(choice.Control.Body.Length > 0 ? ProfileItemTypes.Policy : ProfileItemTypes.Control, choice.Id)))
            .ToArray();

    private void UpdateSummary()
    {
        var items = EditorItems();
        Summary = $"{Experiences.SelectedValues.Count} experience(s), {Frameworks.SelectedValues.Count} framework(s), {Standards.SelectedValues.Count} standard(s), " +
                  $"{PolicySets.SelectedValues.Count} policy set(s), {Controls.Count(choice => choice.IsSelected):N0} individual control(s) and policies – " +
                  $"{ProfileResolver.ControlIds(_catalog.Catalog, items).Count:N0} control(s) in the baseline.";
        ControlsView.Refresh();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var items = EditorItems();
        if (string.IsNullOrWhiteSpace(Name))
        {
            EditorMessage = "Enter a name for the baseline.";
            return;
        }

        if (ProfileResolver.ControlIds(_catalog.Catalog, items).Count == 0)
        {
            EditorMessage = "The baseline covers no controls: add policy sets, controls, or frameworks, standards and experiences.";
            return;
        }

        var existing = Rows.FirstOrDefault(row => row.Record.BaselineKey == EditingKey)?.Record;
        var key = EditingKey ?? $"BL-{Guid.NewGuid():N}"[..19].ToUpperInvariant();
        await _baselines.SaveAsync(new BaselineRecord(key, Name.Trim(), string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(), existing?.IsActive ?? false,
            existing?.IsSystem ?? false, existing?.IsModified ?? false, items, existing?.CreatedBy ?? _audit.Actor, existing?.CreatedAtUtc ?? DateTimeOffset.UtcNow, _audit.Actor,
            DateTimeOffset.UtcNow));
        IsEditorOpen = false;
        await LoadAsync(key);
        StatusMessage = $"Baseline '{Name.Trim()}' saved{(existing?.IsActive == true ? string.Empty : "; activate it to include it in scores")}.";
    }

    [RelayCommand]
    private async Task DeleteAsync(BaselineRow? row)
    {
        if (row is null) return;
        var warning = row.Record.IsSystem ? " It is shipped with the application and is imported again only when the reference data is reinstalled." : string.Empty;
        if (!_dialogs.Confirm(new ConfirmationRequest("Delete Baseline", $"Delete the baseline '{row.Name}'?{warning}",
                [new("Type", row.Type), new("Active", row.IsActive ? "Yes" : "No"), new("Controls", row.Controls.ToString(CultureInfo.CurrentCulture))], "Delete", IsDestructive: true)))
            return;
        await _baselines.DeleteAsync(row.Record.BaselineKey);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ExportAsync(object? format)
    {
        var document = new ExportDocument("Baseline Compliance", null, DateTimeOffset.UtcNow, [new("Baselines", ComplianceCaption)],
        [
            new ExportTable("Baselines", ["Baseline", "Type", "Active", "Controls", "Modified"],
                Rows.Select(row => (IReadOnlyList<string?>)new string?[] { row.Name, row.Type, row.IsActive ? "Yes" : "No", row.Controls.ToString(CultureInfo.InvariantCulture), row.Modified }).ToArray()),
            new ExportTable("Customers", ["Customer", "Assessed", "Compliance", "Satisfied", "Gaps", "Not Evaluated", "Status"],
                Compliance.Select(row => (IReadOnlyList<string?>)new string?[]
                {
                    row.Customer, row.Assessed, row.PercentText, row.Satisfied.ToString(CultureInfo.InvariantCulture), row.Gaps.ToString(CultureInfo.InvariantCulture),
                    row.NotEvaluated.ToString(CultureInfo.InvariantCulture), row.Status
                }).ToArray())
        ]);
        StatusMessage = await _export.ExportAsync(document, ExportCoordinator.Parse(format)) ?? StatusMessage;
    }
}
