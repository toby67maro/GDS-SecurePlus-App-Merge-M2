using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Export;
using EngineeringWorkspace.Application.Governance;
using EngineeringWorkspace.Desktop.Controls;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Desktop.Services;
using EngineeringWorkspace.Domain.Customers;

namespace EngineeringWorkspace.Desktop.Pages.Customers;

public sealed record ExceptionRow(CustomerExceptionRecord Record, string ControlName, bool IsActive)
{
    public int Id => Record.ExceptionId;
    public string ControlId => Record.ControlId;
    public string Reason => Record.Reason;
    public string Status => IsActive ? "Active" : Record.StatusCode == ExceptionStatusCodes.Approved ? "Expired" : Record.StatusCode;
    public string ApprovedBy => Record.ApprovedBy ?? "—";
    public string Expires => Record.ExpiresAtUtc?.ToLocalTime().ToString("yyyy-MM-dd") ?? "Never";
    public string CreatedBy => Record.CreatedBy;
}

/// <summary>
/// Workspace → Customer Profiles → Exceptions: controls a customer is not required to meet, with reason, approver,
/// expiry and status. Active (approved, not expired) exceptions count as satisfied in every report.
/// </summary>
public sealed partial class ExceptionsViewModel : CustomerScopedPageViewModel
{
    private readonly ICustomerExceptionRepository _repository;
    private readonly ICatalogService _catalog;
    private readonly IDialogService _dialogs;
    private readonly AuditContext _audit;
    private readonly ExportCoordinator _export;

    public ExceptionsViewModel(ICustomerContextService context, ICustomerProfileStore store, ICustomerExceptionRepository repository, ICatalogService catalog,
        IDialogService dialogs, AuditContext audit, ExportCoordinator export) : base(context, store)
    {
        _repository = repository;
        _catalog = catalog;
        _dialogs = dialogs;
        _audit = audit;
        _export = export;
        ControlOptionsView = CollectionViewSource.GetDefaultView(ControlOptions);
        ControlOptionsView.Filter = item => item is Choice<string> choice &&
                                            (string.IsNullOrWhiteSpace(ControlSearch) || choice.Label.Contains(ControlSearch.Trim(), StringComparison.CurrentCultureIgnoreCase));
    }

    public ObservableCollection<ExceptionRow> Rows { get; } = [];
    public ObservableCollection<Choice<string>> ControlOptions { get; } = [];
    public ICollectionView ControlOptionsView { get; }
    public IReadOnlyList<string> Statuses { get; } = ExceptionStatusCodes.All;

    [ObservableProperty] private ExceptionRow? _selectedRow;
    [ObservableProperty] private string _controlSearch = string.Empty;
    [ObservableProperty] private Choice<string>? _control;
    [ObservableProperty] private string _reason = string.Empty;
    [ObservableProperty] private string _approvedBy = string.Empty;
    [ObservableProperty] private DateTime? _expires;
    [ObservableProperty] private string _status = ExceptionStatusCodes.Approved;
    [ObservableProperty] private int _editingId;

    partial void OnControlSearchChanged(string value)
    {
        ControlOptionsView.Refresh();
        if (Control is null || !ControlOptionsView.Cast<Choice<string>>().Contains(Control)) Control = ControlOptionsView.Cast<Choice<string>>().FirstOrDefault();
    }

    partial void OnSelectedRowChanged(ExceptionRow? value)
    {
        if (value is null) return;
        EditingId = value.Id;
        Control = ControlOptions.FirstOrDefault(option => option.Value == _catalog.Catalog.ResolveId(value.ControlId));
        Reason = value.Reason;
        ApprovedBy = value.Record.ApprovedBy ?? string.Empty;
        Expires = value.Record.ExpiresAtUtc?.ToLocalTime().DateTime;
        Status = value.Record.StatusCode;
    }

    protected override async Task OnCustomerChangedAsync(CustomerProfile? customer)
    {
        if (ControlOptions.Count == 0)
            foreach (var control in _catalog.Catalog.Controls.Where(control => !control.IsVariant).OrderBy(control => control.Id, StringComparer.Ordinal))
                ControlOptions.Add(new Choice<string>(control.Id, $"{control.Id} – {control.Name}"));
        New();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Rows.Clear();
        if (SelectedCustomer is not { } customer) return;
        if (!_repository.IsAvailable)
        {
            StatusMessage = "The application database is not ready; exceptions are stored in it.";
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var record in await _repository.ListExceptionsAsync(customer.Id.Value))
            Rows.Add(new ExceptionRow(record, _catalog.Catalog.Find(record.ControlId)?.Name ?? "(not in the catalog)", record.IsActiveAt(now)));
        StatusMessage = Rows.Count == 0 ? $"{customer.Name} has no exceptions." : null;
    }

    [RelayCommand]
    private void New()
    {
        SelectedRow = null;
        EditingId = 0;
        Reason = string.Empty;
        ApprovedBy = string.Empty;
        Expires = DateTime.Today.AddYears(1);
        Status = ExceptionStatusCodes.Approved;
        ControlSearch = string.Empty;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (SelectedCustomer is not { } customer || Control is null || string.IsNullOrWhiteSpace(Reason))
        {
            StatusMessage = "Choose the control and enter the reason.";
            return;
        }

        if (Status == ExceptionStatusCodes.Approved && string.IsNullOrWhiteSpace(ApprovedBy))
        {
            StatusMessage = "An approved exception needs the approver's name.";
            return;
        }

        var existing = Rows.FirstOrDefault(row => row.Id == EditingId)?.Record;
        var record = new CustomerExceptionRecord(EditingId, customer.Id.Value, Control.Value, Reason.Trim(), string.IsNullOrWhiteSpace(ApprovedBy) ? null : ApprovedBy.Trim(),
            Expires is { } date ? new DateTimeOffset(date.Date.AddDays(1).AddTicks(-1), TimeZoneInfo.Local.GetUtcOffset(date)) : null, Status,
            existing?.CreatedBy ?? _audit.Actor, existing?.CreatedAtUtc ?? DateTimeOffset.UtcNow);
        await _repository.SaveExceptionAsync(record);
        StatusMessage = $"Exception for {Control.Value} saved.";
        await LoadAsync();
        New();
    }

    [RelayCommand]
    private async Task DeleteAsync(ExceptionRow? row)
    {
        if (row is null) return;
        if (!_dialogs.Confirm(new ConfirmationRequest("Delete Exception", $"Delete the exception for {row.ControlId}? Reports will count the control again.",
                [new("Control", $"{row.ControlId} – {row.ControlName}"), new("Reason", row.Reason)], "Delete", IsDestructive: true))) return;
        await _repository.DeleteExceptionAsync(row.Id);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ExportAsync(object? format)
    {
        var document = new ExportDocument("Exceptions", SelectedCustomer?.Name, DateTimeOffset.UtcNow, [new("Exceptions", Rows.Count.ToString())],
            [new ExportTable("Exceptions", ["Control", "Name", "Status", "Reason", "Approved By", "Expires", "Created By"],
                Rows.Select(row => (IReadOnlyList<string?>)new string?[] { row.ControlId, row.ControlName, row.Status, row.Reason, row.ApprovedBy, row.Expires, row.CreatedBy }).ToArray())]);
        StatusMessage = await _export.ExportAsync(document, ExportCoordinator.Parse(format)) ?? StatusMessage;
    }
}
