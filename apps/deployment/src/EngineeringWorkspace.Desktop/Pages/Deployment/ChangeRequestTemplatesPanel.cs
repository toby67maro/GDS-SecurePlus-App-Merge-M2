using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Governance;
using EngineeringWorkspace.Domain.Customers;

namespace EngineeringWorkspace.Desktop.Pages.Deployment;

public sealed record TemplateRow(ChangeRequestTemplate Template, string? SourceName)
{
    public string Name => Template.Name;
    public string Kind => Template.IsSystem ? "GDS default (built in)" : Template.IsGlobal ? "Global" : "This customer";
    public string Source => SourceName is null ? "—" : $"{SourceName} v{Template.SourceVersion}";
    public string Version => $"v{Template.Version}";
}

/// <summary>
/// Deployment > Change Request > Templates (4.0): each customer's change request templates (sections, wording, fields, approval text, approval
/// email), copied from the global list with a link to the source, and "Save as Global" to share one. The GDS default always exists.
/// </summary>
public sealed partial class ChangeRequestTemplatesPanel(IChangeRequestTemplateRepository repository) : ObservableObject
{
    private CustomerProfile? _customer;
    private IReadOnlyList<ChangeRequestTemplate> _globals = [];

    public ObservableCollection<TemplateRow> CustomerTemplates { get; } = [];
    public ObservableCollection<TemplateRow> GlobalTemplates { get; } = [];
    public string PlaceholderHelp => "Placeholders: " + string.Join(", ", ChangeRequestTemplates.Placeholders.Select(name => "{{" + name + "}}")) + ", {{Sections}} (email only).";

    [ObservableProperty] private TemplateRow? _selectedCustomerTemplate;
    [ObservableProperty] private TemplateRow? _selectedGlobalTemplate;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _sectionsText = string.Empty;
    [ObservableProperty] private string _fieldsText = string.Empty;
    [ObservableProperty] private string _approvalText = string.Empty;
    [ObservableProperty] private string _emailSubject = string.Empty;
    [ObservableProperty] private string _emailBody = string.Empty;
    [ObservableProperty] private string _globalName = string.Empty;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private string _preview = string.Empty;

    public bool HasCustomer => _customer is not null;

    public async Task LoadAsync(CustomerProfile? customer)
    {
        _customer = customer;
        OnPropertyChanged(nameof(HasCustomer));
        CustomerTemplates.Clear();
        GlobalTemplates.Clear();
        Message = null;
        if (!repository.IsAvailable)
        {
            Message = "Templates are stored in the application database, which is not ready.";
            return;
        }

        try
        {
            _globals = await repository.ListGlobalAsync();
            foreach (var template in _globals) GlobalTemplates.Add(new TemplateRow(template, null));
            if (customer is null) return;
            foreach (var template in await repository.ListForCustomerAsync(customer.Id.Value))
                CustomerTemplates.Add(new TemplateRow(template, _globals.FirstOrDefault(global => global.Key == template.SourceKey)?.Name));
            SelectedCustomerTemplate = CustomerTemplates.FirstOrDefault();
            if (SelectedCustomerTemplate is null) Message = "This customer has no template yet. Pick a global template and select Use for This Customer.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
        {
            Message = $"Templates could not be loaded: {exception.Message}";
        }
    }

    partial void OnSelectedCustomerTemplateChanged(TemplateRow? value)
    {
        var content = value?.Template.Content;
        Name = value?.Name ?? string.Empty;
        SectionsText = content is null ? string.Empty : ChangeRequestTemplates.SectionsToText(content.Sections);
        FieldsText = content is null ? string.Empty : string.Join(", ", content.Fields);
        ApprovalText = content?.ApprovalText ?? string.Empty;
        EmailSubject = content?.EmailSubject ?? string.Empty;
        EmailBody = content?.EmailBody ?? string.Empty;
        GlobalName = value?.Name ?? string.Empty;
        Preview = string.Empty;
    }

    private ChangeRequestTemplateContent Content() => new(ChangeRequestTemplates.SectionsFromText(SectionsText),
        FieldsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), ApprovalText.Trim(), EmailSubject.Trim(), EmailBody);

    [RelayCommand]
    private async Task UseGlobalAsync()
    {
        if (_customer is null || SelectedGlobalTemplate is null) return;
        await RunAsync(async () =>
        {
            var copy = await repository.AssignAsync(_customer.Id.Value, SelectedGlobalTemplate.Template.Key);
            await LoadAsync(_customer);
            SelectedCustomerTemplate = CustomerTemplates.FirstOrDefault(row => row.Template.Key == copy.Key);
            return $"'{copy.Name}' copied to {_customer.Name}; edit it here. Changes to the global template are never applied automatically.";
        });
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_customer is null || SelectedCustomerTemplate is null) return;
        await RunAsync(async () =>
        {
            var saved = await repository.SaveAsync(SelectedCustomerTemplate.Template with { Name = Name, Content = Content() });
            await LoadAsync(_customer);
            SelectedCustomerTemplate = CustomerTemplates.FirstOrDefault(row => row.Template.Key == saved.Key);
            return $"'{saved.Name}' saved (v{saved.Version}).";
        });
    }

    [RelayCommand]
    private async Task SaveAsGlobalAsync()
    {
        if (_customer is null || SelectedCustomerTemplate is null) return;
        if (string.IsNullOrWhiteSpace(GlobalName))
        {
            Message = "Enter a name for the global template.";
            return;
        }

        await RunAsync(async () =>
        {
            var current = SelectedCustomerTemplate.Template with { Name = Name, Content = Content() };
            var global = await repository.SaveAsGlobalAsync(current, GlobalName.Trim());
            await LoadAsync(_customer);
            return $"'{global.Name}' is now a global template that every customer can use.";
        });
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (_customer is null || SelectedCustomerTemplate is null) return;
        await RunAsync(async () =>
        {
            await repository.DeleteAsync(SelectedCustomerTemplate.Template.Key);
            await LoadAsync(_customer);
            return "Template deleted.";
        });
    }

    /// <summary>Fills the template from the selected change request and copies the approval email to the clipboard for Outlook.</summary>
    public void BuildPreview(ChangeRequestRecord? request, string controls)
    {
        if (_customer is null || request is null)
        {
            Message = "Select a change request to preview its approval email.";
            return;
        }

        var approver = _customer.ChangeApprovers.FirstOrDefault();
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Customer"] = _customer.Name,
            ["Tenant"] = _customer.PrimaryTenant?.PrimaryDomain ?? _customer.PrimaryTenant?.DisplayName ?? "not connected",
            ["ChangeRequest.Id"] = request.ChangeRequestId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ChangeRequest.Title"] = request.Title,
            ["ChangeRequest.Description"] = request.Description ?? string.Empty,
            ["ChangeRequest.Risk"] = request.Risk,
            ["ChangeRequest.Impact"] = request.Impact ?? string.Empty,
            ["ChangeRequest.RollbackPlan"] = request.RollbackPlan ?? string.Empty,
            ["ChangeRequest.Window"] = request.WindowStartUtc is { } start
                ? $"{start.ToLocalTime():yyyy-MM-dd HH:mm} to {request.WindowEndUtc?.ToLocalTime():yyyy-MM-dd HH:mm}"
                : "To be agreed",
            ["ChangeRequest.Controls"] = controls,
            ["ChangeRequest.RequestedBy"] = request.RequestedBy,
            ["ChangeRequest.Owner"] = request.Owner ?? request.RequestedBy,
            ["Approver"] = approver?.Name ?? "Change Approver"
        };
        var content = Content();
        var subject = ChangeRequestTemplates.Fill(content.EmailSubject, values);
        var body = ChangeRequestTemplates.Fill(content.EmailBody, values, content.Sections);
        Preview = $"Subject: {subject}\n\n{body}\n\n{ChangeRequestTemplates.Fill(content.ApprovalText, values)}";
        try
        {
            Clipboard.SetText(Preview);
            Message = "Approval email copied: paste it into Outlook. Keep the [GDS CR-…] reference in the subject.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Message = "The preview is below; the clipboard was busy, so copy it by hand.";
        }
    }

    private async Task RunAsync(Func<Task<string>> action)
    {
        try
        {
            Message = await action();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or System.Data.Common.DbException)
        {
            Message = exception.Message;
        }
    }
}
