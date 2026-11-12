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
using EngineeringWorkspace.Desktop.Controls;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Desktop.Services;
using EngineeringWorkspace.Domain.Customers;

namespace EngineeringWorkspace.Desktop.Pages.Specifications;

public sealed record SpecificationRow(SpecificationRecord Record, string Customers, int Controls)
{
    public string Name => Record.Name;
    public string Type => Record.IsStandard ? "Standard" : "Tailored";
    public string Status => Record.StatusCode;
    public string BasedOn => Record.BasedOnKey ?? "—";
    public string Modified => (Record.ModifiedAtUtc ?? Record.CreatedAtUtc).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
}

/// <summary>A selectable control in the builder.</summary>
public sealed partial class ControlChoice(CatalogControl control) : ObservableObject
{
    public CatalogControl Control { get; } = control;
    public string Id => Control.Id;
    public string Name => Control.Name;
    public string Kind => Control.KindDisplayName;
    public string Severity => Control.Severity;

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// Workspace → Customer Profiles → Specification Profiles: every specification with search and a Customer filter
/// (including Unassigned); a guided builder to add or edit one (experiences, frameworks and compliance standards,
/// policy sets, controls and the requirements that satisfy them); clone, assign to or unassign from customers, and
/// delete (blocked while assigned).
/// </summary>
public sealed partial class SpecificationProfilesViewModel : PageViewModel
{
    public const string UnassignedKey = "(unassigned)";
    private readonly ISpecificationRepository _repository;
    private readonly ICatalogService _catalog;
    private readonly ICustomerProfileStore _profiles;
    private readonly IDialogService _dialogs;
    private readonly IEntityAuditRepository _auditStore;
    private readonly AuditContext _audit;
    private readonly ExportCoordinator _export;
    private IReadOnlyList<CustomerProfile> _customers = [];
    private bool _loadingEditor;

    public SpecificationProfilesViewModel(ISpecificationRepository repository, ICatalogService catalog, ICustomerProfileStore profiles, IDialogService dialogs,
        IEntityAuditRepository auditStore, AuditContext audit, ExportCoordinator export)
    {
        _repository = repository;
        _catalog = catalog;
        _profiles = profiles;
        _dialogs = dialogs;
        _auditStore = auditStore;
        _audit = audit;
        _export = export;
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = item => item is SpecificationRow row && Matches(row);
        CustomerFilter.Changed += (_, _) => View.Refresh();
        var current = catalog.Catalog;
        Experiences.SetOptions(current.ExperienceAreas.Select(area => (area.Code, area.Name)));
        var references = current.Controls.SelectMany(control => control.FrameworkReferences).ToArray();
        Frameworks.SetOptions(references.Where(reference => !reference.IsComplianceStandard).GroupBy(reference => reference.FrameworkCode)
            .Select(group => (group.Key, group.First().FrameworkName)).OrderBy(option => option.Item2));
        Standards.SetOptions(references.Where(reference => reference.IsComplianceStandard).GroupBy(reference => reference.FrameworkCode)
            .Select(group => (group.Key, group.First().FrameworkName)).OrderBy(option => option.Item2));
        PolicySets.SetOptions(current.PolicySets.OrderBy(set => set.DeploymentOrder).Select(set => (set.Id, set.DisplayName)));
        foreach (var control in current.Controls.Where(control => !control.IsVariant)) Controls.Add(new ControlChoice(control));
        ControlsView = CollectionViewSource.GetDefaultView(Controls);
        ControlsView.Filter = item => item is ControlChoice choice && MatchesControl(choice);
        foreach (var filter in new[] { Experiences, Frameworks, Standards, PolicySets }) filter.Changed += (_, _) => OnScopeChanged();
    }

    public ObservableCollection<SpecificationRow> Rows { get; } = [];
    public ICollectionView View { get; }
    public MultiSelectFilter CustomerFilter { get; } = new("Customer");
    public MultiSelectFilter Experiences { get; } = new("Experiences", emptyMeansAll: false);
    public MultiSelectFilter Frameworks { get; } = new("Frameworks", emptyMeansAll: false);
    public MultiSelectFilter Standards { get; } = new("Compliance Standards", emptyMeansAll: false);
    public MultiSelectFilter PolicySets { get; } = new("Policy Sets", emptyMeansAll: false);
    public MultiSelectFilter Requirements { get; } = new("Requirements", emptyMeansAll: false);
    public ObservableCollection<ControlChoice> Controls { get; } = [];
    public ICollectionView ControlsView { get; }
    public ObservableCollection<Choice<string>> AssignOptions { get; } = [];
    public IReadOnlyList<string> Statuses { get; } = SpecificationStatusCodes.All;

    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private SpecificationRow? _selectedRow;
    [ObservableProperty] private Choice<string>? _assignCustomer;

    // Editor
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorTitle))]
    private bool _isEditorOpen;

    [ObservableProperty] private string? _editingKey;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _status = SpecificationStatusCodes.Draft;
    [ObservableProperty] private bool _isStandard = true;
    [ObservableProperty] private string? _basedOnKey;
    [ObservableProperty] private string _controlSearch = string.Empty;
    [ObservableProperty] private bool _selectedControlsOnly;
    [ObservableProperty] private string _selectionSummary = string.Empty;
    [ObservableProperty] private string? _editorMessage;

    public string EditorTitle => EditingKey is null ? "Build New Specification" : $"Edit Specification";

    partial void OnSearchChanged(string value) => View.Refresh();
    partial void OnControlSearchChanged(string value) => ControlsView.Refresh();
    partial void OnSelectedControlsOnlyChanged(bool value) => ControlsView.Refresh();

    public override async Task OnNavigatedToAsync(RouteDescriptor route, CancellationToken cancellationToken)
    {
        await base.OnNavigatedToAsync(route, cancellationToken);
        foreach (var choice in Controls) choice.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ControlChoice.IsSelected) && !_loadingEditor) UpdateSummary();
        };
        await LoadAsync();
    }

    private async Task LoadAsync(string? select = null)
    {
        _customers = await _profiles.ListAsync();
        var names = _customers.ToDictionary(customer => customer.Id.Value, customer => customer.Name, StringComparer.OrdinalIgnoreCase);
        CustomerFilter.SetOptions(new[] { (UnassignedKey, "Unassigned") }.Concat(_customers.OrderBy(customer => customer.Name).Select(customer => (customer.Id.Value, customer.Name))));
        AssignOptions.Clear();
        foreach (var customer in _customers.OrderBy(customer => customer.Name, StringComparer.CurrentCultureIgnoreCase)) AssignOptions.Add(new Choice<string>(customer.Id.Value, customer.Name));
        Rows.Clear();
        if (!_repository.IsAvailable)
        {
            StatusMessage = "The application database is not ready; specifications are stored in it.";
            return;
        }

        foreach (var spec in await _repository.ListSpecificationsAsync())
            Rows.Add(new SpecificationRow(spec, spec.AssignedCustomers.Count == 0 ? "Unassigned" : string.Join(", ", spec.AssignedCustomers.Select(key => names.GetValueOrDefault(key, key))),
                ProfileResolver.ControlIds(_catalog.Catalog, spec.Items).Count));
        View.Refresh();
        SelectedRow = Rows.FirstOrDefault(row => row.Record.SpecificationKey == select) ?? SelectedRow;
    }

    private bool Matches(SpecificationRow row)
    {
        if (CustomerFilter.IsActive && !CustomerFilter.MatchesAny(row.Record.AssignedCustomers.DefaultIfEmpty(UnassignedKey))) return false;
        if (string.IsNullOrWhiteSpace(Search)) return true;
        var term = Search.Trim();
        return row.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase) || row.Customers.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
               (row.Record.Description?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false);
    }

    private bool MatchesControl(ControlChoice choice)
    {
        if (SelectedControlsOnly && !choice.IsSelected) return false;
        if (string.IsNullOrWhiteSpace(ControlSearch)) return true;
        var term = ControlSearch.Trim();
        return choice.Id.Contains(term, StringComparison.OrdinalIgnoreCase) || choice.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase);
    }

    // ----- Builder -----

    [RelayCommand]
    private void New()
    {
        LoadEditor(null, null);
        StatusMessage = null;
    }

    [RelayCommand]
    private void Edit(SpecificationRow? row)
    {
        if (row is null) return;
        LoadEditor(row.Record, row.Record.SpecificationKey);
    }

    [RelayCommand]
    private void Clone(SpecificationRow? row)
    {
        if (row is null) return;
        LoadEditor(row.Record with { Name = $"{row.Record.Name} (copy)", IsSystem = false, IsModified = false }, null);
        BasedOnKey = row.Record.SpecificationKey;
        IsStandard = false;
        Status = SpecificationStatusCodes.Draft;
        EditorMessage = $"Cloned from '{row.Name}'. Adjust it, then save.";
    }

    [RelayCommand]
    private void CloseEditor() => IsEditorOpen = false;

    private void LoadEditor(SpecificationRecord? record, string? key)
    {
        _loadingEditor = true;
        EditingKey = key;
        Name = record?.Name ?? string.Empty;
        Description = record?.Description ?? string.Empty;
        Status = record?.StatusCode ?? SpecificationStatusCodes.Draft;
        IsStandard = record?.IsStandard ?? true;
        BasedOnKey = record?.BasedOnKey;
        Experiences.Select(record?.ItemsOf(ProfileItemTypes.Experience) ?? []);
        Frameworks.Select(record?.ItemsOf(ProfileItemTypes.Framework) ?? []);
        Standards.Select(record?.ItemsOf(ProfileItemTypes.ComplianceStandard) ?? []);
        PolicySets.Select(record?.ItemsOf(ProfileItemTypes.PolicySet) ?? []);
        var controls = (record?.ItemsOf(ProfileItemTypes.Control) ?? []).Concat(record?.ItemsOf(ProfileItemTypes.Policy) ?? [])
            .Select(_catalog.Catalog.ResolveId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var choice in Controls) choice.IsSelected = controls.Contains(choice.Id);
        RefreshRequirements(record?.ItemsOf(ProfileItemTypes.Requirement).ToArray());
        _loadingEditor = false;
        ControlSearch = string.Empty;
        SelectedControlsOnly = controls.Count > 0;
        EditorMessage = null;
        UpdateSummary();
        IsEditorOpen = true;
    }

    private void OnScopeChanged()
    {
        if (_loadingEditor) return;
        RefreshRequirements(null);
        UpdateSummary();
    }

    /// <summary>Adds the controls of the selected policy sets, frameworks, standards and experiences.</summary>
    [RelayCommand]
    private void AddSuggestedControls()
    {
        var items = PolicySets.SelectedValues.Select(id => new ProfileItem(ProfileItemTypes.PolicySet, id)).ToArray();
        var ids = new HashSet<string>(ProfileResolver.ControlIds(_catalog.Catalog, items), StringComparer.OrdinalIgnoreCase);
        var frameworkItems = Frameworks.SelectedValues.Select(code => new ProfileItem(ProfileItemTypes.Framework, code))
            .Concat(Standards.SelectedValues.Select(code => new ProfileItem(ProfileItemTypes.ComplianceStandard, code)))
            .Concat(Experiences.SelectedValues.Select(code => new ProfileItem(ProfileItemTypes.Experience, code))).ToArray();
        if (frameworkItems.Length > 0)
        {
            var mapped = ProfileResolver.ControlIds(_catalog.Catalog, frameworkItems);
            // Frameworks alone map many controls: limit the suggestion to policy-backed controls and checks.
            ids.UnionWith(mapped.Where(id => _catalog.Catalog.Find(id) is { Kind: CatalogKinds.ConditionalAccess or CatalogKinds.EndpointSecurity or CatalogKinds.AssessmentCheck }));
        }

        _loadingEditor = true;
        foreach (var choice in Controls.Where(choice => ids.Contains(choice.Id))) choice.IsSelected = true;
        _loadingEditor = false;
        RefreshRequirements(null);
        UpdateSummary();
        SelectedControlsOnly = true;
        ControlsView.Refresh();
        EditorMessage = $"{ids.Count:N0} control(s) from the selected policy sets, frameworks, standards and experiences are selected.";
    }

    [RelayCommand]
    private void ClearControls()
    {
        _loadingEditor = true;
        foreach (var choice in Controls) choice.IsSelected = false;
        _loadingEditor = false;
        RefreshRequirements(null);
        UpdateSummary();
    }

    /// <summary>Requirements are the framework and standard references of the selected controls (all ticked by default).</summary>
    private void RefreshRequirements(IReadOnlyList<string>? selected)
    {
        var keep = selected ?? (Requirements.Options.Count == 0 ? null : Requirements.SelectedValues);
        var labels = Controls.Where(choice => choice.IsSelected).SelectMany(choice => choice.Control.FrameworkReferences)
            .Where(reference => (Frameworks.SelectedValues.Count == 0 && Standards.SelectedValues.Count == 0) || Frameworks.SelectedValues.Contains(reference.FrameworkCode) ||
                                Standards.SelectedValues.Contains(reference.FrameworkCode))
            .Select(reference => reference.Label).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCulture).ToArray();
        Requirements.SetOptions(labels.Select(label => (label, label)), keep ?? labels);
    }

    private void UpdateSummary()
    {
        var selected = Controls.Where(choice => choice.IsSelected).ToArray();
        var policies = selected.Count(choice => choice.Control.Kind is CatalogKinds.ConditionalAccess or CatalogKinds.EndpointSecurity or CatalogKinds.Intune);
        SelectionSummary = $"{Experiences.SelectedValues.Count} experience(s), {Frameworks.SelectedValues.Count} framework(s), {Standards.SelectedValues.Count} standard(s), " +
                           $"{PolicySets.SelectedValues.Count} policy set(s), {selected.Length:N0} control(s) of which {policies:N0} policies, {Requirements.SelectedValues.Count:N0} requirement(s).";
        ControlsView.Refresh();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            EditorMessage = "Enter a name for the specification.";
            return;
        }

        var items = Experiences.SelectedValues.Select(value => new ProfileItem(ProfileItemTypes.Experience, value))
            .Concat(Frameworks.SelectedValues.Select(value => new ProfileItem(ProfileItemTypes.Framework, value)))
            .Concat(Standards.SelectedValues.Select(value => new ProfileItem(ProfileItemTypes.ComplianceStandard, value)))
            .Concat(PolicySets.SelectedValues.Select(value => new ProfileItem(ProfileItemTypes.PolicySet, value)))
            .Concat(Controls.Where(choice => choice.IsSelected).Select(choice => new ProfileItem(ProfileItemTypes.Control, choice.Id)))
            .Concat(Requirements.SelectedValues.Select(value => new ProfileItem(ProfileItemTypes.Requirement, value)))
            .ToArray();
        if (!items.Any(item => item.ItemType is ProfileItemTypes.Control or ProfileItemTypes.PolicySet))
        {
            EditorMessage = "Select at least one policy set or control.";
            return;
        }

        var existing = Rows.FirstOrDefault(row => row.Record.SpecificationKey == EditingKey)?.Record;
        var key = EditingKey ?? $"SPEC-{Guid.NewGuid():N}"[..21].ToUpperInvariant();
        var record = new SpecificationRecord(key, Name.Trim(), string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(), IsStandard, existing?.IsSystem ?? false,
            existing?.IsModified ?? false, BasedOnKey, Status, items, existing?.AssignedCustomers ?? [], existing?.CreatedBy ?? _audit.Actor, existing?.CreatedAtUtc ?? DateTimeOffset.UtcNow,
            _audit.Actor, DateTimeOffset.UtcNow);
        try
        {
            await _repository.SaveSpecificationAsync(record, _audit.Actor);
            if (_auditStore.IsAvailable)
                await _auditStore.RecordChangesAsync([new EntityAuditRecord(0, "Specification", key, existing is null ? "Created" : "Edited", existing?.Name, record.Name, _audit.Actor,
                    DateTimeOffset.UtcNow)]);
            IsEditorOpen = false;
            StatusMessage = $"Specification '{record.Name}' saved.";
            await LoadAsync(key);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
        {
            EditorMessage = $"The specification could not be saved: {exception.Message}";
        }
    }

    // ----- Assignment and deletion -----

    [RelayCommand]
    private async Task AssignAsync()
    {
        if (SelectedRow is not { } row || AssignCustomer is not { } customer) return;
        var previous = Rows.FirstOrDefault(other => other.Record.AssignedCustomers.Contains(customer.Value, StringComparer.OrdinalIgnoreCase));
        if (previous is not null && previous != row &&
            !_dialogs.Confirm(new ConfirmationRequest("Replace Specification", $"{customer.Label} uses '{previous.Name}'. Assign '{row.Name}' instead?",
                [new("Customer", customer.Label), new("Current", previous.Name), new("New", row.Name)], "Assign"))) return;
        await _repository.AssignSpecificationAsync(customer.Value, row.Record.SpecificationKey, _audit.Actor);
        StatusMessage = $"'{row.Name}' assigned to {customer.Label}.";
        await LoadAsync(row.Record.SpecificationKey);
    }

    [RelayCommand]
    private async Task UnassignAsync()
    {
        if (SelectedRow is not { } row || AssignCustomer is not { } customer) return;
        if (!row.Record.AssignedCustomers.Contains(customer.Value, StringComparer.OrdinalIgnoreCase))
        {
            StatusMessage = $"'{row.Name}' is not assigned to {customer.Label}.";
            return;
        }

        await _repository.AssignSpecificationAsync(customer.Value, null, _audit.Actor);
        StatusMessage = $"'{row.Name}' unassigned from {customer.Label}.";
        await LoadAsync(row.Record.SpecificationKey);
    }

    [RelayCommand]
    private async Task DeleteAsync(SpecificationRow? row)
    {
        if (row is null) return;
        if (row.Record.IsAssigned)
        {
            _dialogs.ShowInformation("Specification In Use", $"'{row.Name}' is assigned to {row.Customers}. Unassign it from every customer before deleting it.");
            return;
        }

        if (!_dialogs.Confirm(new ConfirmationRequest("Delete Specification", $"Delete '{row.Name}'? This cannot be undone.",
                [new("Type", row.Type), new("Controls", row.Controls.ToString(CultureInfo.CurrentCulture))], "Delete", IsDestructive: true))) return;
        await _repository.DeleteSpecificationAsync(row.Record.SpecificationKey);
        StatusMessage = $"'{row.Name}' deleted.";
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ExportAsync(object? format)
    {
        var rows = View.Cast<SpecificationRow>().ToArray();
        var document = new ExportDocument("Specification Profiles", null, DateTimeOffset.UtcNow, [new("Specifications", rows.Length.ToString(CultureInfo.InvariantCulture))],
        [
            new ExportTable("Specifications", ["Name", "Type", "Status", "Customers", "Controls", "Based On", "Modified", "Description"],
                rows.Select(row => (IReadOnlyList<string?>)new string?[] { row.Name, row.Type, row.Status, row.Customers, row.Controls.ToString(CultureInfo.InvariantCulture), row.BasedOn, row.Modified,
                    row.Record.Description }).ToArray()),
            new ExportTable("Items", ["Specification", "Item Type", "Item"],
                rows.SelectMany(row => row.Record.Items.Select(item => (IReadOnlyList<string?>)new string?[] { row.Name, item.ItemType, item.ItemKey })).ToArray())
        ]);
        StatusMessage = await _export.ExportAsync(document, ExportCoordinator.Parse(format)) ?? StatusMessage;
    }
}
