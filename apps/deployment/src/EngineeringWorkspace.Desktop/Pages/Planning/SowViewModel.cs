using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Experience;
using EngineeringWorkspace.Application.Planning;
using EngineeringWorkspace.Application.Reporting;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Desktop.Pages.Customers;
using EngineeringWorkspace.Desktop.Services;
using EngineeringWorkspace.Domain.Customers;

namespace EngineeringWorkspace.Desktop.Pages.Planning;

/// <summary>An editable SOW section.</summary>
public sealed partial class SowSectionRow : ObservableObject
{
    [ObservableProperty] private string _code = string.Empty;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _content = string.Empty;
    [ObservableProperty] private bool _isRequired = true;
}

/// <summary>A filled section of the generated SOW.</summary>
public sealed record SowPreviewSection(string Title, string Text);

/// <summary>
/// Assessment > SOW: statement of work templates (built-in GDS drafts, or created and saved in dlv.SowTemplate /
/// dlv.SowSection) filled in from the customer profile, user groups and their experience, the latest assessment, the
/// roadmap and a project plan. The result is previewed and saved as HTML or a Word document.
/// </summary>
public sealed partial class SowViewModel(ICustomerContextService context, ICustomerProfileStore store, IPlanningRepository planning, IReportingService reporting,
    ICatalogService catalog, IDialogService dialogs) : CustomerScopedPageViewModel(context, store)
{
    public ObservableCollection<SowTemplate> Templates { get; } = [];
    public ObservableCollection<SowSectionRow> Sections { get; } = [];
    public ObservableCollection<ProjectPlan> Plans { get; } = [];
    public ObservableCollection<SowPreviewSection> Preview { get; } = [];
    public IReadOnlyList<(string Name, string Description)> Placeholders => SowTemplates.Placeholders;
    public IReadOnlyList<string> PlaceholderText { get; } = SowTemplates.Placeholders.Select(item => $"{{{{{item.Name}}}}} – {item.Description}").ToArray();
    public IReadOnlyList<string> TierOptions => CustomerFields.TierOptions;

    [ObservableProperty] private SowTemplate? _selectedTemplate;
    [ObservableProperty] private SowSectionRow? _selectedSection;
    [ObservableProperty] private ProjectPlan? _selectedPlan;
    [ObservableProperty] private string _templateCode = string.Empty;
    [ObservableProperty] private string _templateName = string.Empty;
    [ObservableProperty] private string _templateVersion = "1.0";
    [ObservableProperty] private string _templateTier = CustomerFields.NotSet;
    [ObservableProperty] private string _deliverables =
        "• Hardened Microsoft 365 tenant configured to the agreed experience for each user group\n• Configuration documentation and change records\n" +
        "• Re-assessment report showing the improvement\n• Experience Map and user communication templates\n• Knowledge transfer session";
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private string _documentTitle = string.Empty;

    public override async Task OnNavigatedToAsync(EngineeringWorkspace.Application.Navigation.RouteDescriptor route, CancellationToken cancellationToken)
    {
        await LoadTemplatesAsync();
        await base.OnNavigatedToAsync(route, cancellationToken);
    }

    private async Task LoadTemplatesAsync(string? select = null)
    {
        Templates.Clear();
        var saved = planning.IsAvailable ? await SafeAsync(() => planning.GetSowTemplatesAsync(), (IReadOnlyList<SowTemplate>)[]) : [];
        foreach (var template in saved) Templates.Add(template);
        foreach (var template in SowTemplates.BuiltIn.Where(builtIn => saved.All(item => item.Code != builtIn.Code))) Templates.Add(template);
        SelectedTemplate = Templates.FirstOrDefault(template => template.Code == select) ?? Templates.FirstOrDefault();
    }

    private async Task<T> SafeAsync<T>(Func<Task<T>> action, T fallback)
    {
        try
        {
            return await action();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
        {
            StatusMessage = exception.Message;
            return fallback;
        }
    }

    partial void OnSelectedTemplateChanged(SowTemplate? value)
    {
        Sections.Clear();
        if (value is null) return;
        TemplateCode = value.Code;
        TemplateName = value.Name;
        TemplateVersion = value.Version;
        TemplateTier = CustomerFields.TierText(value.ServiceTierCode);
        foreach (var section in value.Sections.OrderBy(section => section.Order))
            Sections.Add(new SowSectionRow { Code = section.Code, Title = section.Title, Content = section.Content, IsRequired = section.IsRequired });
        SelectedSection = Sections.FirstOrDefault();
    }

    protected override async Task OnCustomerChangedAsync(CustomerProfile? customer)
    {
        Plans.Clear();
        Preview.Clear();
        HasPreview = false;
        if (customer is null || !planning.IsAvailable) return;
        foreach (var plan in await SafeAsync(() => planning.GetProjectPlansAsync(customer.Id.Value), (IReadOnlyList<ProjectPlan>)[])) Plans.Add(plan);
        SelectedPlan = Plans.FirstOrDefault();
    }

    [RelayCommand]
    private void NewTemplate()
    {
        SelectedTemplate = null;
        TemplateCode = $"CUSTOM-{DateTime.Now:yyyyMMddHHmm}";
        TemplateName = "New SOW template";
        TemplateVersion = "1.0";
        Sections.Clear();
        Sections.Add(new SowSectionRow { Code = "OVERVIEW", Title = "1. Overview", Content = "GDS will deliver ... to {{Customer.Name}}." });
        SelectedSection = Sections[0];
    }

    [RelayCommand]
    private void DuplicateTemplate()
    {
        TemplateCode = $"{TemplateCode}-COPY".Length > 40 ? $"COPY-{DateTime.Now:yyyyMMddHHmmss}" : $"{TemplateCode}-COPY";
        TemplateName = $"{TemplateName} (copy)";
        SelectedTemplate = null;
        StatusMessage = "Copy ready: change it and select Save Template.";
    }

    [RelayCommand]
    private void AddSection()
    {
        var row = new SowSectionRow { Code = $"S{Sections.Count + 1}", Title = $"{Sections.Count + 1}. New section", Content = string.Empty };
        Sections.Insert(SelectedSection is null ? Sections.Count : Sections.IndexOf(SelectedSection) + 1, row);
        SelectedSection = row;
    }

    [RelayCommand]
    private void RemoveSection()
    {
        if (SelectedSection is { } section) Sections.Remove(section);
        SelectedSection = Sections.FirstOrDefault();
    }

    [RelayCommand]
    private void MoveSectionUp() => Move(-1);

    [RelayCommand]
    private void MoveSectionDown() => Move(1);

    private void Move(int delta)
    {
        if (SelectedSection is not { } section) return;
        var index = Sections.IndexOf(section);
        var target = index + delta;
        if (target < 0 || target >= Sections.Count) return;
        Sections.Move(index, target);
        SelectedSection = section;
    }

    [RelayCommand]
    private async Task SaveTemplateAsync()
    {
        if (!planning.IsAvailable)
        {
            StatusMessage = "The application database is not ready; templates are stored in it.";
            return;
        }

        var code = new string((TemplateCode ?? string.Empty).Trim().ToUpperInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray());
        if (code.Length == 0 || string.IsNullOrWhiteSpace(TemplateName) || Sections.Count == 0)
        {
            StatusMessage = "A template needs a code, a name and at least one section.";
            return;
        }

        var template = new SowTemplate(code.Length > 40 ? code[..40] : code, TemplateName.Trim(), string.IsNullOrWhiteSpace(TemplateVersion) ? "1.0" : TemplateVersion.Trim(), "SecurePlus",
            CustomerFields.TierCode(TemplateTier), Sections.Select((section, index) => new SowSection(string.IsNullOrWhiteSpace(section.Code) ? $"S{index + 1}" : section.Code.Trim(),
                section.Title.Trim(), index + 1, section.Content, section.IsRequired)).ToArray());
        await SafeAsync(async () =>
        {
            await planning.SaveSowTemplateAsync(template);
            return true;
        }, false);
        await LoadTemplatesAsync(template.Code);
        StatusMessage = $"Template '{template.Name}' saved.";
    }

    [RelayCommand]
    private async Task DeleteTemplateAsync()
    {
        if (SelectedTemplate is not { } template) return;
        if (template.IsBuiltIn)
        {
            StatusMessage = "Built-in templates can't be deleted; duplicate one to change it.";
            return;
        }

        if (!dialogs.Confirm(new ConfirmationRequest("Delete SOW Template", "The template is deleted from the application database.", [new("Template", template.Name)], "Delete Template",
                IsDestructive: true)))
            return;
        await SafeAsync(async () =>
        {
            await planning.DeleteSowTemplateAsync(template.Code);
            return true;
        }, false);
        await LoadTemplatesAsync();
        StatusMessage = $"Template '{template.Name}' deleted.";
    }

    [RelayCommand]
    private async Task GenerateAsync()
    {
        if (SelectedCustomer is not { } customer)
        {
            StatusMessage = "Choose a customer.";
            return;
        }

        IsBusy = true;
        try
        {
            var values = await BuildValuesAsync(customer);
            var template = new SowTemplate(TemplateCode, TemplateName, TemplateVersion, null, null,
                Sections.Select((section, index) => new SowSection(section.Code, section.Title, index + 1, section.Content, section.IsRequired)).ToArray());
            Preview.Clear();
            foreach (var (title, text) in SowTemplates.Render(template, values)) Preview.Add(new SowPreviewSection(title, text));
            DocumentTitle = $"{TemplateName} – {customer.Name}";
            HasPreview = Preview.Count > 0;
            var missing = Preview.Sum(section => section.Text.Split('[').Length - 1);
            StatusMessage = missing > 0
                ? $"Generated. {missing} placeholder(s) shown in [brackets] had no data (for example, no assessment or project plan yet)."
                : "Generated. Review it, then save as HTML or Word.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The filled document as HTML (used for both .html and the Word-compatible .doc).</summary>
    public string? Html => HasPreview ? SowTemplates.ToHtml(DocumentTitle, SelectedCustomer?.Name ?? string.Empty, Preview.Select(section => (section.Title, section.Text)).ToArray()) : null;

    public string PlainText => string.Join("\n\n", Preview.Select(section => $"{section.Title}\n{section.Text}"));

    private async Task<Dictionary<string, string>> BuildValuesAsync(CustomerProfile customer)
    {
        var details = customer.Details;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Date"] = DateTime.Today.ToString("D", CultureInfo.CurrentCulture),
            ["Customer.Name"] = customer.Name,
            ["Customer.Address"] = string.Join(", ", new[] { details.AddressLine1, details.AddressLine2, details.City, details.StateRegion, details.PostalCode, details.Country }
                .Where(part => !string.IsNullOrWhiteSpace(part))),
            ["Customer.Industry"] = CustomerFields.IndustryDisplay(details) ?? string.Empty,
            ["Customer.Size"] = details.OrganizationSizeCode is null ? string.Empty : CustomerFields.SizeText(details.OrganizationSizeCode),
            ["Customer.PrimaryContact"] = customer.Contacts.FirstOrDefault(contact => contact.IsPrimary) is { } primary
                ? $"{primary.Name}{(primary.Title is null ? string.Empty : $", {primary.Title}")}{(primary.Email is null ? string.Empty : $" ({primary.Email})")}"
                : string.Empty,
            ["Customer.Approvers"] = string.Join("; ", customer.ChangeApprovers.Select(contact => contact.Name)),
            ["Customer.Tenants"] = string.Join("; ", customer.Tenants.Select(tenant => tenant.PrimaryDomain ?? tenant.TenantName ?? tenant.DisplayName)),
            ["Customer.ServiceTier"] = details.ServiceTierCode is null ? string.Empty : CustomerFields.TierText(details.ServiceTierCode),
            ["Customer.UserCount"] = customer.Groups.Sum(group => group.UserCount ?? 0) is > 0 and var users ? users.ToString("N0", CultureInfo.CurrentCulture) : string.Empty,
            ["GDS.SalesLead"] = details.SalesLead ?? string.Empty,
            ["GDS.TechnicalLead"] = details.TechnicalLead ?? string.Empty,
            ["GDS.ProjectManager"] = details.ProjectManager ?? string.Empty,
            ["Deliverables"] = Deliverables,
        };

        var frameworks = (customer.WorkspaceSettings.GetValueOrDefault(Experience.ExperienceConfigurationViewModel.FrameworksSetting) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        values["Frameworks"] = string.Join(", ", frameworks.Select(code => ExperienceCatalog.Framework(code)?.Name).OfType<string>());

        // Experience: each group, its profile and what changes for compliance.
        var experiences = ExperienceEngine.EvaluateAll(customer, frameworks);
        var groups = new StringBuilder();
        foreach (var experience in experiences)
        {
            groups.AppendLine($"{experience.Name} ({experience.Classification}{(experience.UserCount is { } count ? $", {count:N0} users" : string.Empty)}) – {experience.Profile.Name} experience");
            foreach (var answer in experience.WithFrameworks.Where(answer => answer.Question.Code is "ID-MFA-METHOD" or "ID-SIGNIN-FREQUENCY" or "ID-LOCATIONS" or "DEV-PERSONAL" or "FILES-SHARING"))
            {
                groups.AppendLine($"   • {answer.Question.Topic}: {answer.Option.Label}");
                if (answer.Option.Code == ExperienceCatalog.SharedDeviceQrCode)
                    groups.AppendLine("     This sign-in method is single-factor and is not multifactor authentication (MFA). It is enabled only for the frontline QR code group " +
                                      "and only on compliant shared devices (Conditional Access CA-213 and CA-214).");
            }
        }

        values["Experience.Groups"] = groups.ToString().TrimEnd();
        values["Experience.Summary"] =
            $"Average security {experiences.Average(item => item.SecurityWithFrameworks):0}/100 with average user friction {experiences.Average(item => item.FrictionWithFrameworks):0}/100 across {experiences.Count} experience(s)." +
            (experiences.Sum(item => item.Conflicts) is > 0 and var conflicts ? $" {conflicts} choice(s) were adjusted to meet the compliance targets." : string.Empty);

        // Assessment and roadmap.
        if (reporting.IsAvailable)
        {
            var results = await SafeAsync<CustomerResults?>(async () => await reporting.GetCustomerResultsAsync(customer.Id.Value, ReportScope.ActiveBaselines), null);
            if (results is { HasResults: true })
            {
                var overall = results.Overall;
                values["Assessment.Summary"] =
                    $"The assessment of {(results.Latest!.CompletedAtUtc ?? results.Latest.RequestedAtUtc).ToLocalTime():d} found {overall.Controls} controls in scope: {overall.PercentText} complete, " +
                    $"{overall.Gaps} gap(s) ({results.Facts.Count(fact => fact.IsGap && fact.Control.Severity is "Critical" or "High")} critical or high).";
                var plan = RoadmapPlanner.Plan(RoadmapBuilder.Build(results.Facts, catalog.Catalog), catalog.Catalog, new RoadmapPlanOptions(DateOnly.FromDateTime(DateTime.Today)));
                values["Roadmap.Summary"] = $"{plan.Items.Count} remediation item(s), about {plan.TotalHours:N0} hours over {plan.WorkDays} work day(s).";
                values["Roadmap.Phases"] = string.Join("\n", plan.Phases.Where(phase => phase.Items.Count > 0).Select(phase =>
                    $"{phase.Name}: {phase.Items.Count} item(s), {phase.Hours:N0} h – " +
                    string.Join(", ", phase.Items.Select(item => item.Item.Control.Name).Distinct().Take(6)) + (phase.Items.Count > 6 ? ", …" : string.Empty)));
            }
        }

        if (SelectedPlan is { } projectPlan)
        {
            values["ProjectPlan.Name"] = projectPlan.Name;
            values["ProjectPlan.Start"] = projectPlan.Start.ToString("D", CultureInfo.CurrentCulture);
            values["ProjectPlan.Finish"] = projectPlan.Finish.ToString("D", CultureInfo.CurrentCulture);
            values["ProjectPlan.Hours"] = projectPlan.Tasks.Sum(task => task.EffortHours).ToString("N0", CultureInfo.CurrentCulture);
            values["ProjectPlan.Tasks"] = string.Join("\n", projectPlan.Tasks.Take(60).Select(task =>
                $"{task.Outline}. {task.Name} – {task.Start:d MMM} to {task.Finish:d MMM} ({task.EffortHours:0.#} h, {task.Role})")) +
                (projectPlan.Tasks.Count > 60 ? $"\n… and {projectPlan.Tasks.Count - 60} more task(s)" : string.Empty);
        }

        return values;
    }
}
