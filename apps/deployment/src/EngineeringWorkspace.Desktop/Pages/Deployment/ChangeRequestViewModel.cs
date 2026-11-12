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
using EngineeringWorkspace.Domain.Customers;

namespace EngineeringWorkspace.Desktop.Pages.Deployment;

/// <summary>Carries a change request selection from the Tracker to the Change Request or Change Execution page.</summary>
public sealed class DeploymentSelection
{
    public int? ChangeRequestId { get; set; }

    public int? Take()
    {
        var id = ChangeRequestId;
        ChangeRequestId = null;
        return id;
    }
}

public sealed record ChangeRequestRow(ChangeRequestRecord Record, string CustomerName)
{
    public int Id => Record.ChangeRequestId;
    public string Title => Record.Title;
    public string Status => Record.StatusCode;
    public string Risk => Record.Risk;
    public string Owner => Record.Owner ?? "—";
    public string RequestedBy => Record.RequestedBy;
    public string ApprovedBy => Record.ApprovedBy ?? "—";
    public int Items => Record.Items.Count;
    public string Window => Record.WindowStartUtc is { } start
        ? $"{start.ToLocalTime():yyyy-MM-dd HH:mm} – {Record.WindowEndUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? "?"}"
        : "Not scheduled";
    public string Modified => Record.ModifiedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
}

/// <summary>
/// Deployment → Change Request: change requests of the selected customer tied to the controls and policies they
/// change, with description, risk, impact, rollback plan, requester and approver, scheduled window and the workflow
/// Draft → Submitted → Approved/Rejected → Scheduled → Completed. Status changes are audited.
/// </summary>
public sealed partial class ChangeRequestViewModel : CustomerScopedPageViewModel
{
    private readonly IChangeRequestRepository _repository;
    private readonly ICatalogService _catalog;
    private readonly IReportingService _reporting;
    private readonly IEntityAuditRepository _auditStore;
    private readonly AuditContext _audit;
    private readonly IDialogService _dialogs;
    private readonly DeploymentSelection _selection;
    private readonly ExportCoordinator _export;
    private bool _loadingEditor;

    public ChangeRequestViewModel(ICustomerContextService context, ICustomerProfileStore store, IChangeRequestRepository repository, ICatalogService catalog, IReportingService reporting,
        IEntityAuditRepository auditStore, AuditContext audit, IDialogService dialogs, DeploymentSelection selection, ExportCoordinator export,
        IChangeRequestTemplateRepository? templates = null) : base(context, store)
    {
        if (templates is not null) Templates = new ChangeRequestTemplatesPanel(templates);
        _repository = repository;
        _catalog = catalog;
        _reporting = reporting;
        _auditStore = auditStore;
        _audit = audit;
        _dialogs = dialogs;
        _selection = selection;
        _export = export;
        foreach (var control in catalog.Catalog.Controls.Where(control => !control.IsVariant)) Controls.Add(new ControlChoice(control));
        ControlsView = CollectionViewSource.GetDefaultView(Controls);
        ControlsView.Filter = item => item is ControlChoice choice && MatchesControl(choice);
        foreach (var choice in Controls) choice.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ControlChoice.IsSelected) && !_loadingEditor) UpdateSummary();
        };
    }

    public ObservableCollection<ChangeRequestRow> Rows { get; } = [];

    /// <summary>The customer's change request templates (4.0).</summary>
    public ChangeRequestTemplatesPanel? Templates { get; }

    [RelayCommand]
    private void PreviewApprovalEmail() => Templates?.BuildPreview(SelectedRow?.Record,
        SelectedRow is null ? string.Empty : string.Join("\n", SelectedRow.Record.Items.Select(item => $"- {item.ItemKey} {_catalog.Catalog.Controls.FirstOrDefault(control => control.Id == item.ItemKey)?.Name}")));
    public ObservableCollection<ControlChoice> Controls { get; } = [];
    public ICollectionView ControlsView { get; }
    public ObservableCollection<string> NextStatuses { get; } = [];
    public ObservableCollection<EntityAuditRecord> History { get; } = [];
    public IReadOnlyList<string> Risks { get; } = ChangeRisk.All;

    [ObservableProperty] private ChangeRequestRow? _selectedRow;
    [ObservableProperty] private int _editingId;
    [ObservableProperty] private string _status = ChangeStatusCodes.Draft;
    [ObservableProperty] private string _requestTitle = string.Empty;
    [ObservableProperty] private string _requestDescription = string.Empty;
    [ObservableProperty] private string _risk = "Medium";
    [ObservableProperty] private string _impact = string.Empty;
    [ObservableProperty] private string _rollbackPlan = string.Empty;
    [ObservableProperty] private string _requestedBy = string.Empty;
    [ObservableProperty] private string _approvedBy = string.Empty;
    [ObservableProperty] private string _owner = string.Empty;
    [ObservableProperty] private DateTime? _windowStartDate;
    [ObservableProperty] private string _windowStartTime = "18:00";
    [ObservableProperty] private DateTime? _windowEndDate;
    [ObservableProperty] private string _windowEndTime = "22:00";
    [ObservableProperty] private string _decisionNote = string.Empty;
    [ObservableProperty] private string _controlSearch = string.Empty;
    [ObservableProperty] private bool _selectedOnly;
    [ObservableProperty] private string _selectionSummary = "No controls selected.";
    [ObservableProperty] private string? _editorMessage;

    public string EditorTitle => EditingId > 0 ? $"Change Request #{EditingId}" : "New Change Request";
    public bool IsEditable => Status is ChangeStatusCodes.Draft or ChangeStatusCodes.Rejected;

    partial void OnEditingIdChanged(int value) => OnPropertyChanged(nameof(EditorTitle));
    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(IsEditable));
    partial void OnControlSearchChanged(string value) => ControlsView.Refresh();
    partial void OnSelectedOnlyChanged(bool value) => ControlsView.Refresh();
    partial void OnSelectedRowChanged(ChangeRequestRow? value)
    {
        if (value is not null) LoadEditor(value.Record);
    }

    protected override async Task OnCustomerChangedAsync(CustomerProfile? customer)
    {
        await LoadAsync(_selection.Take());
        if (SelectedRow is null) New();
        if (Templates is not null) await Templates.LoadAsync(customer);
    }

    private bool MatchesControl(ControlChoice choice)
    {
        if (SelectedOnly && !choice.IsSelected) return false;
        if (string.IsNullOrWhiteSpace(ControlSearch)) return true;
        var term = ControlSearch.Trim();
        return choice.Id.Contains(term, StringComparison.OrdinalIgnoreCase) || choice.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase);
    }

    private async Task LoadAsync(int? select)
    {
        Rows.Clear();
        if (SelectedCustomer is not { } customer) return;
        if (!_repository.IsAvailable)
        {
            StatusMessage = "The application database is not ready; change requests are stored in it.";
            return;
        }

        foreach (var record in await _repository.ListChangeRequestsAsync(customer.Id.Value)) Rows.Add(new ChangeRequestRow(record, customer.Name));
        SelectedRow = Rows.FirstOrDefault(row => row.Id == select) ?? (select is null ? null : SelectedRow);
        StatusMessage = Rows.Count == 0 ? $"{customer.Name} has no change requests yet." : null;
    }

    [RelayCommand]
    private void New()
    {
        SelectedRow = null;
        LoadEditor(null);
    }

    private void LoadEditor(ChangeRequestRecord? record)
    {
        _loadingEditor = true;
        EditingId = record?.ChangeRequestId ?? 0;
        Status = record?.StatusCode ?? ChangeStatusCodes.Draft;
        RequestTitle = record?.Title ?? string.Empty;
        RequestDescription = record?.Description ?? string.Empty;
        Risk = record?.Risk ?? "Medium";
        Impact = record?.Impact ?? string.Empty;
        RollbackPlan = record?.RollbackPlan ?? string.Empty;
        RequestedBy = record?.RequestedBy ?? _audit.Actor;
        ApprovedBy = record?.ApprovedBy ?? string.Empty;
        Owner = record?.Owner ?? _audit.Actor;
        WindowStartDate = record?.WindowStartUtc?.ToLocalTime().Date;
        WindowStartTime = record?.WindowStartUtc?.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) ?? "18:00";
        WindowEndDate = record?.WindowEndUtc?.ToLocalTime().Date;
        WindowEndTime = record?.WindowEndUtc?.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) ?? "22:00";
        DecisionNote = record?.DecisionNote ?? string.Empty;
        var ids = (record?.Items ?? []).Select(item => _catalog.Catalog.ResolveId(item.ItemKey)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var choice in Controls) choice.IsSelected = ids.Contains(choice.Id);
        _loadingEditor = false;
        SelectedOnly = ids.Count > 0;
        ControlSearch = string.Empty;
        EditorMessage = null;
        NextStatuses.Clear();
        foreach (var next in ChangeStatusCodes.NextStates(Status)) NextStatuses.Add(next);
        UpdateSummary();
        _ = LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        History.Clear();
        if (EditingId <= 0 || !_auditStore.IsAvailable) return;
        foreach (var change in await _auditStore.ListChangesAsync("ChangeRequest", EditingId.ToString(CultureInfo.InvariantCulture), 50)) History.Add(change);
    }

    private void UpdateSummary()
    {
        var selected = Controls.Where(choice => choice.IsSelected).ToArray();
        SelectionSummary = selected.Length == 0 ? "No controls selected." : $"{selected.Length} control(s) and policies: {string.Join(", ", selected.Take(8).Select(choice => choice.Id))}" +
                                                                             (selected.Length > 8 ? "…" : string.Empty);
        ControlsView.Refresh();
    }

    /// <summary>Selects the controls the customer's latest assessment shows as not met (Phase 1 of the roadmap first).</summary>
    [RelayCommand]
    private async Task AddRoadmapGapsAsync()
    {
        if (SelectedCustomer is not { } customer || !_reporting.IsAvailable) return;
        var results = await _reporting.GetCustomerResultsAsync(customer.Id.Value, ReportScope.ActiveBaselines);
        var phases = RoadmapBuilder.Build(results.Facts, _catalog.Catalog);
        var ids = phases.First().Items.Select(item => item.Control.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _loadingEditor = true;
        foreach (var choice in Controls.Where(choice => ids.Contains(choice.Id))) choice.IsSelected = true;
        _loadingEditor = false;
        SelectedOnly = true;
        UpdateSummary();
        EditorMessage = ids.Count == 0 ? "The roadmap's first phase has no gaps." : $"{ids.Count} gap(s) from roadmap phase 1 selected.";
    }

    private static DateTimeOffset? Combine(DateTime? date, string time) =>
        date is { } day && TimeOnly.TryParse(time, CultureInfo.InvariantCulture, out var at)
            ? new DateTimeOffset(day.Date + at.ToTimeSpan(), TimeZoneInfo.Local.GetUtcOffset(day.Date + at.ToTimeSpan()))
            : null;

    private ChangeRequestRecord? Build(string status)
    {
        if (SelectedCustomer is not { } customer) return null;
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(RequestTitle)) errors.Add("Enter a title.");
        if (!Controls.Any(choice => choice.IsSelected)) errors.Add("Select at least one control or policy to change.");
        var start = Combine(WindowStartDate, WindowStartTime);
        var end = Combine(WindowEndDate, WindowEndTime);
        if ((WindowStartDate is null) != (WindowEndDate is null)) errors.Add("Set both the start and the end of the window, or neither.");
        if (start is not null && end is not null && end <= start) errors.Add("The window must end after it starts.");
        if (status is ChangeStatusCodes.Submitted or ChangeStatusCodes.Approved && (string.IsNullOrWhiteSpace(Impact) || string.IsNullOrWhiteSpace(RollbackPlan)))
            errors.Add("Describe the impact and the rollback plan before submitting.");
        if (status == ChangeStatusCodes.Approved && string.IsNullOrWhiteSpace(ApprovedBy)) errors.Add("Enter who approved the change.");
        if (status == ChangeStatusCodes.Approved && string.Equals(ApprovedBy.Trim(), RequestedBy.Trim(), StringComparison.OrdinalIgnoreCase))
            errors.Add("The approver must be someone other than the requester.");
        if (status == ChangeStatusCodes.Scheduled && start is null) errors.Add("Set the change window before scheduling.");
        if (errors.Count > 0)
        {
            EditorMessage = string.Join(" ", errors);
            return null;
        }

        var existing = Rows.FirstOrDefault(row => row.Id == EditingId)?.Record;
        var decided = status is ChangeStatusCodes.Approved or ChangeStatusCodes.Rejected ? DateTimeOffset.UtcNow : existing?.DecidedAtUtc;
        return new ChangeRequestRecord(EditingId, customer.Id.Value, RequestTitle.Trim(), NullIf(RequestDescription), Risk, NullIf(Impact), NullIf(RollbackPlan),
            string.IsNullOrWhiteSpace(RequestedBy) ? _audit.Actor : RequestedBy.Trim(), NullIf(ApprovedBy), NullIf(Owner), start, end, status, NullIf(DecisionNote),
            existing?.CreatedAtUtc ?? DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, decided, status == ChangeStatusCodes.Completed ? DateTimeOffset.UtcNow : existing?.CompletedAtUtc,
            Controls.Where(choice => choice.IsSelected).Select(choice => new ProfileItem(choice.Control.Body.Length > 0 ? ProfileItemTypes.Policy : ProfileItemTypes.Control, choice.Id)).ToArray());
    }

    private static string? NullIf(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [RelayCommand]
    private Task SaveAsync() => PersistAsync(Status);

    [RelayCommand]
    private Task MoveToAsync(string? status) => status is null ? Task.CompletedTask : PersistAsync(status);

    private async Task PersistAsync(string status)
    {
        if (!_repository.IsAvailable)
        {
            EditorMessage = "The application database is not ready.";
            return;
        }

        if (status != Status && !ChangeStatusCodes.CanMove(Status, status))
        {
            EditorMessage = $"A {Status} request cannot move to {status}.";
            return;
        }

        if (Build(status) is not { } record) return;
        if (status == ChangeStatusCodes.Rejected && string.IsNullOrWhiteSpace(DecisionNote))
        {
            EditorMessage = "Enter the reason for rejecting the change in Decision Note.";
            return;
        }

        var previous = Status;
        var saved = await _repository.SaveChangeRequestAsync(record);
        if (_auditStore.IsAvailable)
            await _auditStore.RecordChangesAsync([new EntityAuditRecord(0, "ChangeRequest", saved.ChangeRequestId.ToString(CultureInfo.InvariantCulture),
                record.ChangeRequestId == 0 ? "Created" : previous == status ? "Edited" : "Status", previous, status, _audit.Actor, DateTimeOffset.UtcNow)]);
        StatusMessage = $"Change request #{saved.ChangeRequestId} saved as {saved.StatusCode}.";
        await LoadAsync(saved.ChangeRequestId);
    }

    [RelayCommand]
    private async Task DeleteAsync(ChangeRequestRow? row)
    {
        if (row is null) return;
        if (row.Status is not (ChangeStatusCodes.Draft or ChangeStatusCodes.Rejected))
        {
            _dialogs.ShowInformation("Change Request In Progress", $"Only Draft or Rejected requests can be deleted; #{row.Id} is {row.Status}.");
            return;
        }

        if (!_dialogs.Confirm(new ConfirmationRequest("Delete Change Request", $"Delete change request #{row.Id} '{row.Title}'?", [new("Status", row.Status)], "Delete", IsDestructive: true)))
            return;
        await _repository.DeleteChangeRequestAsync(row.Id);
        await LoadAsync(null);
        New();
    }

    [RelayCommand]
    private async Task ExportAsync(object? format)
    {
        var document = new ExportDocument("Change Requests", SelectedCustomer?.Name, DateTimeOffset.UtcNow, [new("Requests", Rows.Count.ToString(CultureInfo.InvariantCulture))],
            [new ExportTable("Change Requests", ["ID", "Title", "Status", "Risk", "Window", "Requested By", "Approved By", "Owner", "Controls", "Impact", "Rollback Plan"],
                Rows.Select(row => (IReadOnlyList<string?>)new string?[]
                {
                    row.Id.ToString(CultureInfo.InvariantCulture), row.Title, row.Status, row.Risk, row.Window, row.RequestedBy, row.ApprovedBy, row.Owner,
                    string.Join(", ", row.Record.Items.Select(item => item.ItemKey)), row.Record.Impact, row.Record.RollbackPlan
                }).ToArray())]);
        StatusMessage = await _export.ExportAsync(document, ExportCoordinator.Parse(format)) ?? StatusMessage;
    }
}
