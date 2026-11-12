using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Export;
using EngineeringWorkspace.Application.Reference;
using EngineeringWorkspace.Application.Governance;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Desktop.Controls;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Desktop.Services;
using EngineeringWorkspace.Domain.Customers;
using EngineeringWorkspace.Domain.Tenants;

namespace EngineeringWorkspace.Desktop.Pages.Customers;

/// <summary>A profile in the list.</summary>
public sealed record ProfileListRow(CustomerProfile Profile, string? Specification)
{
    public string Name => Profile.Name;
    public string Domain => Profile.PrimaryTenant?.PrimaryDomain ?? "—";
    public int Tenants => Profile.Tenants.Count;
    public bool IsConnected => Profile.Tenants.Any(tenant => tenant.ApplicationRegistration?.SecretReference is not null);
    public string Connection => IsConnected ? "Connected" : "Not connected";
    public string Tags => string.Join(", ", Profile.Tags);
}

/// <summary>One property of the selected profile, editable in place with the pencil.</summary>
public sealed partial class ProfileField : ObservableObject
{
    private readonly Func<CustomerProfile, string, (CustomerProfile? Updated, string? Error)>? _apply;

    public ProfileField(string section, string label, string value, Func<CustomerProfile, string, (CustomerProfile? Updated, string? Error)>? apply, bool multiline = false,
        IReadOnlyList<string>? options = null, string? editValue = null)
    {
        Section = section;
        Label = label;
        Value = value;
        _apply = apply;
        IsMultiline = multiline;
        Options = options ?? [];
        _editText = editValue ?? value;
    }

    public string Section { get; }
    public string Label { get; }
    public string Value { get; }
    public bool IsEditable => _apply is not null;
    public bool IsMultiline { get; }
    public IReadOnlyList<string> Options { get; }
    public bool HasOptions => Options.Count > 0;
    public bool IsTextEditor => !HasOptions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReading))]
    private bool _isEditing;

    [ObservableProperty] private string _editText;
    [ObservableProperty] private string? _error;

    public bool IsReading => !IsEditing;

    public (CustomerProfile? Updated, string? Error) Apply(CustomerProfile profile) => _apply is null ? (null, "This property cannot be edited here.") : _apply(profile, EditText);
}

/// <summary>An editable row of the Industry pick-list.</summary>
public sealed partial class IndustryRow : ObservableObject
{
    public IndustryRow(IndustryValue value)
    {
        Code = value.Code;
        _name = value.Name;
        _sortOrder = value.SortOrder;
        _isActive = value.IsActive;
        Origin = value.IsSystem ? value.IsModified ? "GDS (edited)" : "GDS" : "Added";
    }

    public string Code { get; }
    public string Origin { get; }
    public bool IsChanged { get; private set; }
    [ObservableProperty] private string _name;
    [ObservableProperty] private int _sortOrder;
    [ObservableProperty] private bool _isActive;
    partial void OnNameChanged(string value) => IsChanged = true;
    partial void OnSortOrderChanged(int value) => IsChanged = true;
    partial void OnIsActiveChanged(bool value) => IsChanged = true;
}

/// <summary>A change recorded for the selected profile.</summary>
public sealed record ProfileChangeRow(EntityAuditRecord Record)
{
    public string When => Record.ChangedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    public string Who => Record.ChangedBy;
    public string Property => Record.Property;
    public string OldValue => Record.OldValue ?? "—";
    public string NewValue => Record.NewValue ?? "—";
}

/// <summary>
/// Workspace → Customer Profiles: searchable, filterable list of customer profiles; the selected profile's properties
/// with in-place editing (pencil, validation, Save, Cancel) and a history of who changed what and when; export of one
/// or all profiles to .txt, .csv, .html and .json. Secrets (client secrets, certificates, passwords) are never shown or
/// exported – only whether one is stored.
/// </summary>
public sealed partial class CustomerProfilesViewModel : PageViewModel
{
    public const string EntityType = "CustomerProfile";
    private readonly IIndustryRepository? _industries;

    /// <summary>The Industry pick-list, editable by Leads (4.0).</summary>
    public ObservableCollection<IndustryRow> IndustryRows { get; } = [];

    [ObservableProperty] private string? _industryMessage;
    private readonly ICustomerProfileStore _profiles;
    private readonly ICustomerContextService _context;
    private readonly ISpecificationRepository _specifications;
    private readonly IEntityAuditRepository _auditStore;
    private readonly AuditContext _audit;
    private readonly ExportCoordinator _export;
    private IReadOnlyList<SpecificationRecord> _specs = [];

    public CustomerProfilesViewModel(ICustomerProfileStore profiles, ICustomerContextService context, ISpecificationRepository specifications, IEntityAuditRepository auditStore,
        AuditContext audit, ExportCoordinator export, IIndustryRepository? industries = null, TenantConnectServices? connect = null)
    {
        _industries = industries;
        _connect = connect;
        _profiles = profiles;
        _context = context;
        _specifications = specifications;
        _auditStore = auditStore;
        _audit = audit;
        _export = export;
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = item => item is ProfileListRow row && Matches(row);
        ConnectionFilter.Changed += (_, _) => RefreshView();
        TagFilter.Changed += (_, _) => RefreshView();
        FieldsView = new CollectionViewSource { Source = Fields }.View;
        FieldsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProfileField.Section)));
    }

    public ICollectionView FieldsView { get; }

    public ObservableCollection<ProfileListRow> Rows { get; } = [];
    public ICollectionView View { get; }
    public ObservableCollection<ProfileField> Fields { get; } = [];
    public ObservableCollection<ProfileChangeRow> History { get; } = [];
    public MultiSelectFilter ConnectionFilter { get; } = new("Tenant", options: [("Connected", "Connected"), ("Not connected", "Not connected")]);
    public MultiSelectFilter TagFilter { get; } = new("Tags");
    public ContactsEditor Contacts { get; } = new();
    public ContactsEditor NewContacts { get; } = new();
    public UserGroupsEditor Groups { get; } = new();
    public UserGroupsEditor NewGroups { get; } = new();

    [ObservableProperty] private string? _groupsMessage;

    [RelayCommand]
    private void AddGroup() => AddGroupTo(Groups, message => GroupsMessage = message);

    [RelayCommand]
    private void AddNewGroup() => AddGroupTo(NewGroups, message => NewCustomerMessage = message);

    [RelayCommand]
    private void RemoveGroup(UserGroupRow? row)
    {
        if (row is not null) Groups.Rows.Remove(row);
    }

    [RelayCommand]
    private void RemoveNewGroup(UserGroupRow? row)
    {
        if (row is not null) NewGroups.Rows.Remove(row);
    }

    private static void AddGroupTo(UserGroupsEditor editor, Action<string> message)
    {
        if (!editor.CanAdd)
        {
            message($"A customer can have at most {CustomerProfile.MaxUserGroups} user groups.");
            return;
        }

        editor.Rows.Add(new UserGroupRow());
    }

    /// <summary>Adds common groups (executives, knowledge workers, frontline, travellers, administrators) to start from.</summary>
    [RelayCommand]
    private void AddSuggestedGroups()
    {
        foreach (var (name, classification) in new[] { ("Executives", "Executive"), ("Knowledge Workers", "Knowledge Worker"), ("Frontline Workers", "Frontline Worker"),
                     ("International Travelers", "International Traveler"), ("IT Administrators", "Privileged Administrator") })
            if (Groups.CanAdd && Groups.Rows.All(row => !string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase)))
                Groups.Rows.Add(new UserGroupRow { Name = name, Classification = classification });
    }

    [RelayCommand]
    private async Task SaveGroupsAsync()
    {
        if (SelectedRow is not { } row) return;
        var (groups, error) = UserGroupRow.ValidateAll(Groups.Rows);
        if (error is not null)
        {
            GroupsMessage = error;
            return;
        }

        var current = await _profiles.LoadAsync(row.Profile.Id) ?? row.Profile;
        var before = string.Join("; ", current.Groups.Select(group => group.Name));
        var updated = current with { UserGroups = groups! };
        await _profiles.SaveAsync(updated);
        if (_context.CurrentCustomer?.Id == updated.Id) _context.Refresh(updated);
        if (_auditStore.IsAvailable)
            await _auditStore.RecordChangesAsync([new EntityAuditRecord(0, EntityType, current.Id.Value, "User Groups", before, string.Join("; ", groups!.Select(group => group.Name)),
                _audit.Actor, DateTimeOffset.UtcNow)]);
        GroupsMessage = null;
        StatusMessage = $"{groups!.Count} user group(s) saved for {updated.Name}. Tailor each group's experience on Assessment > Experience Configuration.";
        await LoadAsync(current.Id.Value);
    }
    public OrganizationForm NewOrganization { get; private set; } = new();

    [ObservableProperty] private string? _contactsMessage;
    [ObservableProperty] private bool _isNewCustomerOpen;
    [ObservableProperty] private string _newCustomerName = string.Empty;
    [ObservableProperty] private string _newCustomerTags = string.Empty;
    [ObservableProperty] private string _newCustomerNotes = string.Empty;
    [ObservableProperty] private string? _newCustomerMessage;

    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private string _countText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(CanManageAccess))]
    [NotifyPropertyChangedFor(nameof(HasDeploymentAccess))]
    [NotifyPropertyChangedFor(nameof(ReadAccessText))]
    [NotifyPropertyChangedFor(nameof(DeploymentAccessText))]
    private ProfileListRow? _selectedRow;

    public bool HasSelection => SelectedRow is not null;

    partial void OnSearchChanged(string value) => RefreshView();
    partial void OnSelectedRowChanged(ProfileListRow? value) => _ = ShowAsync(value);

    public override async Task OnNavigatedToAsync(RouteDescriptor route, CancellationToken cancellationToken)
    {
        await base.OnNavigatedToAsync(route, cancellationToken);
        await LoadAsync(_context.CurrentCustomer?.Id.Value);
    }

    private async Task LoadAsync(string? select)
    {
        if (_industries is not null)
        {
            try
            {
                var list = await _industries.ListAsync(includeInactive: true);
                CustomerFields.Industries = list;
                IndustryRows.Clear();
                foreach (var item in list) IndustryRows.Add(new IndustryRow(item));
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
            {
                IndustryMessage = $"The industry list could not be loaded: {exception.Message}";
            }
        }

        _specs = _specifications.IsAvailable ? await _specifications.ListSpecificationsAsync() : [];
        var profiles = await _profiles.ListAsync();
        Rows.Clear();
        foreach (var profile in profiles.OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase))
            Rows.Add(new ProfileListRow(profile, _specs.FirstOrDefault(spec => spec.AssignedCustomers.Contains(profile.Id.Value, StringComparer.OrdinalIgnoreCase))?.Name));
        TagFilter.SetOptions(profiles.SelectMany(profile => profile.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order().Select(tag => (tag, tag)));
        RefreshView();
        SelectedRow = Rows.FirstOrDefault(row => row.Profile.Id.Value == select) ?? View.Cast<ProfileListRow>().FirstOrDefault();
        StatusMessage = Rows.Count == 0 ? "There are no customer profiles yet. Add one in setup or on Tenant App Registration." : StatusMessage;
    }

    private bool Matches(ProfileListRow row)
    {
        if (!ConnectionFilter.Matches(row.Connection) || !TagFilter.MatchesAny(row.Profile.Tags.DefaultIfEmpty(string.Empty))) return false;
        if (string.IsNullOrWhiteSpace(Search)) return true;
        var term = Search.Trim();
        return row.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase) || row.Domain.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
               row.Tags.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
               row.Profile.Contacts.Any(contact => contact.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)) ||
               (row.Profile.Details.City?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
               (row.Profile.Details.TechnicalLead?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false);
    }

    private void RefreshView()
    {
        View.Refresh();
        CountText = $"{View.Cast<object>().Count()} of {Rows.Count} customer(s)";
    }

    // ---- Tenant users and groups (4.0 beta.2, read-only) ---------------------------------------------------------
    public System.Collections.ObjectModel.ObservableCollection<Application.Tenants.TenantGroup> InventoryGroups { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<InventoryUserRow> InventoryUsers { get; } = [];
    private ICollectionView? _inventoryGroupsView;
    private ICollectionView? _inventoryUsersView;
    public ICollectionView InventoryGroupsView => _inventoryGroupsView ??= InventoryFilter(CollectionViewSource.GetDefaultView(InventoryGroups),
        item => item is Application.Tenants.TenantGroup group ? [group.DisplayName, group.ObjectId, group.Kind, group.Description] : []);
    public ICollectionView InventoryUsersView => _inventoryUsersView ??= InventoryFilter(CollectionViewSource.GetDefaultView(InventoryUsers),
        item => item is InventoryUserRow row ? [row.DisplayName, row.UserPrincipalName, row.Department, row.JobTitle, row.ObjectId] : []);
    [ObservableProperty] private string _inventorySearch = string.Empty;
    [ObservableProperty] private string _inventoryText = string.Empty;

    private ICollectionView InventoryFilter(ICollectionView view, Func<object, string?[]> texts)
    {
        view.Filter = item => InventorySearch.Length == 0 || texts(item).Any(text => text?.Contains(InventorySearch.Trim(), StringComparison.OrdinalIgnoreCase) == true);
        return view;
    }

    partial void OnInventorySearchChanged(string value)
    {
        InventoryGroupsView.Refresh();
        InventoryUsersView.Refresh();
    }

    private async Task LoadInventoryAsync(CustomerProfile profile, IReadOnlyList<Application.Tenants.TenantGroup> groups)
    {
        InventoryGroups.Clear();
        InventoryUsers.Clear();
        foreach (var group in groups) InventoryGroups.Add(group);
        IReadOnlyList<Application.Tenants.TenantUser> users = [];
        if (_connect?.Inventory is { IsAvailable: true } inventory)
        {
            try
            {
                users = await inventory.ListUsersAsync(profile.Id.Value);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
            {
                users = [];
            }
        }

        foreach (var user in users) InventoryUsers.Add(new InventoryUserRow(user));
        InventoryText = groups.Count == 0 && users.Count == 0
            ? "Nothing yet. A quick scan (Assessment > Live Assessment, or Connect Tenant on a new customer) lists the tenant's users and groups here."
            : $"{users.Count:N0} user(s) ({users.Count(user => user.IsGuest):N0} guest(s), {users.Count(user => user.AdminRoles.Count > 0):N0} with admin roles, " +
              $"{users.Count(user => user.IsMfaRegistered == false):N0} not registered for MFA) and {groups.Count:N0} group(s) from the last scan.";
    }

    /// <summary>The customer's Entra groups from the last scan (empty when the database or inventory is not available).</summary>
    private async Task<IReadOnlyList<Application.Tenants.TenantGroup>> EntraGroupsAsync(CustomerProfile profile)
    {
        if (_connect?.Inventory is not { IsAvailable: true } inventory) return [];
        try
        {
            return await inventory.ListGroupsAsync(profile.Id.Value);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
        {
            return [];
        }
    }

    private async Task ShowAsync(ProfileListRow? row)
    {
        Fields.Clear();
        History.Clear();
        Contacts.Load([]);
        Groups.Load([]);
        if (row is null) return;
        var profile = row.Profile;
        static (CustomerProfile?, string?) Ok(CustomerProfile updated) => (updated, null);
        static IReadOnlyList<string> Lines(string text) => text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();

        Fields.Add(new ProfileField("General", "Name", profile.Name, (current, text) =>
            string.IsNullOrWhiteSpace(text) ? (null, "The name cannot be empty.") : text.Trim().Length > 200 ? (null, "Use at most 200 characters.") : Ok(current with { Name = text.Trim() })));
        Fields.Add(new ProfileField("General", "Customer ID", profile.Id.Value, null));
        Fields.Add(new ProfileField("General", "Environment", profile.Environment ?? string.Empty, (current, text) => Ok(current with { Environment = string.IsNullOrWhiteSpace(text) ? null : text.Trim() })));
        Fields.Add(new ProfileField("General", "Tags", string.Join(", ", profile.Tags), (current, text) =>
            Ok(current with { Tags = text.Split(',').Select(tag => tag.Trim()).Where(tag => tag.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() })));
        Fields.Add(new ProfileField("General", "Notes", profile.Notes ?? string.Empty, (current, text) => Ok(current with { Notes = string.IsNullOrWhiteSpace(text) ? null : text.Trim() }),
            multiline: true));
        AddOrganizationFields(profile);
        var specOptions = new[] { "(None)" }.Concat(_specs.Select(spec => spec.Name)).ToArray();
        Fields.Add(new ProfileField("Specification", "Assigned Specification", row.Specification ?? "(None)", (current, text) =>
            specOptions.Contains(text) ? Ok(current) : (null, "Choose a specification from the list."), options: specOptions));

        for (var index = 0; index < profile.Tenants.Count; index++)
        {
            var tenant = profile.Tenants[index];
            var key = tenant.TenantKey;
            var section = $"Tenant: {tenant.DisplayName} ({key})";
            CustomerProfile With(CustomerProfile current, Func<TenantConfiguration, TenantConfiguration> change) =>
                current with { Tenants = current.Tenants.Select(item => item.TenantKey == key ? change(item) : item).ToArray() };
            Fields.Add(new ProfileField(section, "Display Name", tenant.DisplayName, (current, text) =>
                string.IsNullOrWhiteSpace(text) ? (null, "The display name cannot be empty.") : Ok(With(current, item => item with { DisplayName = text.Trim() }))));
            Fields.Add(new ProfileField(section, "Primary Domain", tenant.PrimaryDomain ?? string.Empty, (current, text) =>
                !string.IsNullOrWhiteSpace(text) && !text.Trim().Contains('.', StringComparison.Ordinal)
                    ? (null, "Enter a domain such as contoso.onmicrosoft.com.")
                    : Ok(With(current, item => item with { PrimaryDomain = string.IsNullOrWhiteSpace(text) ? null : text.Trim().ToLowerInvariant() }))));
            Fields.Add(new ProfileField(section, "Tenant ID", tenant.TenantId?.ToString() ?? string.Empty, (current, text) =>
                string.IsNullOrWhiteSpace(text)
                    ? Ok(With(current, item => item with { TenantId = null }))
                    : Guid.TryParse(text.Trim(), out var id) ? Ok(With(current, item => item with { TenantId = id })) : (null, "Enter the tenant ID as a GUID.")));
            Fields.Add(new ProfileField(section, "Cloud", tenant.CloudEnvironment.ToString(), (current, text) =>
                Enum.TryParse<CloudEnvironment>(text, out var cloud) ? Ok(With(current, item => item with { CloudEnvironment = cloud })) : (null, "Choose a cloud."),
                options: Enum.GetNames<CloudEnvironment>()));
            var registration = tenant.ApplicationRegistration;
            Fields.Add(new ProfileField(section, "Read Registration (Client ID)", registration?.ClientId.ToString() ?? "Not registered", null));
            Fields.Add(new ProfileField(section, "Credential", CredentialText(registration), null));
            Fields.Add(new ProfileField(section, "Credential Expires", registration?.CredentialExpirationUtc?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture) ?? "—", null));
            Fields.Add(new ProfileField(section, "Deployment Access", tenant.DeploymentRegistration is { } deploy
                ? $"Enabled: Deploy registration {deploy.ClientId}, certificate expires {deploy.CredentialExpirationUtc?.ToLocalTime():yyyy-MM-dd}. Remove it when the deploy phase ends."
                : "Not enabled (write access is created for deploy phases only)", null));
        }

        Fields.Add(new ProfileField("Domains", "Additional Domains", string.Join("\n", profile.AdditionalDomains), (current, text) =>
            Lines(text).FirstOrDefault(line => !line.Contains('.', StringComparison.Ordinal)) is { } bad
                ? (null, $"'{bad}' is not a domain.")
                : Ok(current with { AdditionalDomains = Lines(text).Select(line => line.ToLowerInvariant()).Distinct().ToArray() }), multiline: true));
        Contacts.Load(profile.Contacts);
        Groups.Load(profile.Groups);
        var entraGroups = await EntraGroupsAsync(profile);
        Groups.LoadEntraGroups(entraGroups);
        await LoadInventoryAsync(profile, entraGroups);
        GroupsMessage = Groups.HasEntraGroups || profile.PrimaryTenant?.ApplicationRegistration is null
            ? null
            : "No Entra groups yet: run a quick scan (Assessment > Live Assessment) to list the tenant's groups here.";
        ContactsMessage = profile.Contacts.Count == 0 ? "No contacts yet. Add the primary contact and whoever approves changes." : null;
        AddSettings("Integration Settings", profile.IntegrationSettings, (current, values) => current with { IntegrationSettings = values },
            "Reference values for integrations (for example ConnectWise.CompanyId=1234). Kept with the profile and exported; no 4.0 feature reads them yet, and secrets never belong here.");
        // Compliance targets and deployment variables live in the workspace settings but are edited on their own pages: shown read-only
        // here and always kept when the other settings are saved.
        foreach (var managed in profile.WorkspaceSettings.Where(pair => IsManagedSetting(pair.Key)).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            Fields.Add(new ProfileField("Workspace Settings", ManagedLabel(managed.Key), managed.Value, null));
        AddSettings("Workspace Settings", profile.WorkspaceSettings, (current, values) => current with { WorkspaceSettings = values },
            "Other per-customer settings (key=value per line). Compliance targets are set on Assessment > Experience Configuration and deployment variables on Deployment > Change Execution.",
            IsManagedSetting);

        if (_auditStore.IsAvailable)
            foreach (var change in await _auditStore.ListChangesAsync(EntityType, profile.Id.Value, 100)) History.Add(new ProfileChangeRow(change));

        void AddSettings(string section, IReadOnlyDictionary<string, string> values, Func<CustomerProfile, IReadOnlyDictionary<string, string>, CustomerProfile> set,
            string help, Func<string, bool>? kept = null)
        {
            bool Keep(string key) => ExportService.IsSecretName(key) || (kept?.Invoke(key) ?? false);
            var visible = values.Where(pair => !Keep(pair.Key)).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key}={pair.Value}");
            var secrets = values.Count(pair => ExportService.IsSecretName(pair.Key));
            Fields.Add(new ProfileField(section, $"Settings (key=value per line{(secrets > 0 ? $"; {secrets} secret value(s) hidden and kept" : string.Empty)})",
                string.Join("\n", visible), (current, text) =>
                {
                    // Start from the values stored now (not when the page opened), keeping hidden and page-managed keys.
                    var stored = section == "Integration Settings" ? current.IntegrationSettings : current.WorkspaceSettings;
                    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pair in stored.Where(pair => Keep(pair.Key))) result[pair.Key] = pair.Value;
                    foreach (var line in Lines(text))
                    {
                        var split = line.IndexOf('=', StringComparison.Ordinal);
                        if (split <= 0) return (null, $"'{line}' must be key=value.");
                        var key = line[..split].Trim();
                        if (key.Length > 100) return (null, $"'{key[..20]}…' is too long for a setting name (at most 100 characters).");
                        if (ExportService.IsSecretName(key)) return (null, $"'{key}' looks like a secret; store secrets in Credential Management, not in the profile.");
                        if (kept?.Invoke(key) ?? false) return (null, $"'{key}' is managed on its own page; change it there.");
                        result[key] = line[(split + 1)..].Trim();
                    }

                    return Ok(set(current, result));
                }, multiline: true));
            Fields.Add(new ProfileField(section, "About", help, null));
        }
    }

    /// <summary>Workspace settings written by other pages: compliance targets (Experience Configuration) and deployment variables (Change Execution).</summary>
    private static bool IsManagedSetting(string key) =>
        key.StartsWith("Experience.", StringComparison.OrdinalIgnoreCase) || key.StartsWith(Application.Governance.DeploymentVariables.Prefix, StringComparison.OrdinalIgnoreCase);

    private static string ManagedLabel(string key) =>
        key.Equals(Experience.ExperienceConfigurationViewModel.FrameworksSetting, StringComparison.OrdinalIgnoreCase) ? "Compliance Targets (Experience Configuration)"
        : key.StartsWith(Application.Governance.DeploymentVariables.Prefix, StringComparison.OrdinalIgnoreCase)
            ? $"Deployment Variable {key[Application.Governance.DeploymentVariables.Prefix.Length..]} (Change Execution)"
            : $"{key} (managed)";

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Organization (cust.Customer) and GDS team (cust.Engagement) fields of the selected profile.</summary>
    private void AddOrganizationFields(CustomerProfile profile)
    {
        var details = profile.Details;
        static (CustomerProfile?, string?) Set(CustomerProfile current, Func<CustomerOrganization, CustomerOrganization> change) =>
            (current with { Organization = change(current.Details) }, null);
        void Text(string section, string label, string? value, Func<CustomerOrganization, string?, CustomerOrganization> change, int max = 200, Func<string?, string?>? validate = null) =>
            Fields.Add(new ProfileField(section, label, value ?? string.Empty, (current, text) =>
                validate?.Invoke(text) is { } error ? (null, error) : Set(current, organization => change(organization, CustomerFields.Clean(text, max)))));
        void Choice(string section, string label, string value, IReadOnlyList<string> options, Func<CustomerOrganization, string, CustomerOrganization> change) =>
            Fields.Add(new ProfileField(section, label, value, (current, text) =>
                options.Contains(text) ? Set(current, organization => change(organization, text)) : (null, "Choose a value from the list."), options: options));

        const string organization = "Organization";
        Choice(organization, "Industry", CustomerFields.IndustryText(details.IndustryKey), CustomerFields.IndustryOptions,
            (current, text) => current with { IndustryKey = CustomerFields.IndustryKey(text), IndustryOther = CustomerFields.IndustryKey(text) == IndustryList.Other ? current.IndustryOther : null });
        if (details.IndustryKey == IndustryList.Other)
            Text(organization, "Industry (Other, specify)", details.IndustryOther, (current, value) => current with { IndustryOther = value });
        Choice(organization, "Organization Size", CustomerFields.SizeText(details.OrganizationSizeCode), CustomerFields.SizeOptions,
            (current, text) => current with { OrganizationSizeCode = CustomerFields.SizeCode(text) });
        Text(organization, "Address Line 1", details.AddressLine1, (current, value) => current with { AddressLine1 = value });
        Text(organization, "Address Line 2", details.AddressLine2, (current, value) => current with { AddressLine2 = value });
        Text(organization, "City", details.City, (current, value) => current with { City = value }, 100);
        Text(organization, "State / Region", details.StateRegion, (current, value) => current with { StateRegion = value }, 60);
        Text(organization, "Postal Code", details.PostalCode, (current, value) => current with { PostalCode = value }, 20);
        Text(organization, "Country", details.Country, (current, value) => current with { Country = value }, 60);
        Text(organization, "Main Phone", details.Phone, (current, value) => current with { Phone = value }, 50, CustomerFields.PhoneError);
        Text(organization, "Main E-mail", details.Email, (current, value) => current with { Email = value }, 320, CustomerFields.EmailError);
        Text(organization, "Website", details.Website, (current, value) => current with { Website = CustomerFields.Website(value) }, 300, CustomerFields.WebsiteError);
        Text(organization, "CRM Account ID", details.CrmAccountId, (current, value) => current with { CrmAccountId = value }, 50);

        const string team = "GDS Team";
        Text(team, "Sales Lead", details.SalesLead, (current, value) => current with { SalesLead = value });
        Text(team, "Technical Lead / Assigned Engineer", details.TechnicalLead, (current, value) => current with { TechnicalLead = value });
        Text(team, "Project Manager", details.ProjectManager, (current, value) => current with { ProjectManager = value });
        Choice(team, "Service Tier", CustomerFields.TierText(details.ServiceTierCode), CustomerFields.TierOptions,
            (current, text) => current with { ServiceTierCode = CustomerFields.TierCode(text) });
        Fields.Add(new ProfileField(team, "Change Approvers", profile.ChangeApprovers.Count == 0
            ? "None – mark a contact as Change Approver below"
            : string.Join("; ", profile.ChangeApprovers.Select(contact => $"{contact.Name} ({string.Join(", ", new[] { contact.Email, contact.Phone ?? contact.MobilePhone }.Where(value => !string.IsNullOrWhiteSpace(value)))})")),
            null));
    }

    [RelayCommand]
    private void AddIndustry() => IndustryRows.Add(new IndustryRow(new IndustryValue(string.Empty, string.Empty, (IndustryRows.Count + 1) * 10, IsSystem: false)));

    [RelayCommand]
    private async Task SaveIndustriesAsync()
    {
        if (_industries is null) return;
        try
        {
            foreach (var row in IndustryRows.Where(row => row.IsChanged))
                await _industries.SaveAsync(new IndustryValue(row.Code, row.Name, row.SortOrder, row.IsActive));
            IndustryMessage = "Industry list saved.";
            await LoadAsync(SelectedRow?.Profile.Id.Value);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            IndustryMessage = exception.Message;
        }
    }

    [RelayCommand]
    private void AddContact() => Contacts.Rows.Add(new ContactRow { IsPrimary = Contacts.Rows.Count == 0 });

    [RelayCommand]
    private void RemoveContact(ContactRow? row)
    {
        if (row is not null) Contacts.Rows.Remove(row);
    }

    [RelayCommand]
    private async Task SaveContactsAsync()
    {
        if (SelectedRow is not { } row) return;
        var (contacts, error) = ContactRow.ValidateAll(Contacts.Rows);
        if (error is not null)
        {
            ContactsMessage = error;
            return;
        }

        var current = await _profiles.LoadAsync(row.Profile.Id) ?? row.Profile;
        var before = string.Join("; ", current.Contacts.Select(contact => contact.Name));
        var updated = current with { Contacts = contacts! };
        await _profiles.SaveAsync(updated);
        if (_context.CurrentCustomer?.Id == updated.Id) _context.Refresh(updated);
        if (_auditStore.IsAvailable)
            await _auditStore.RecordChangesAsync([new EntityAuditRecord(0, EntityType, current.Id.Value, "Contacts", before, string.Join("; ", contacts!.Select(contact => contact.Name)),
                _audit.Actor, DateTimeOffset.UtcNow)]);
        StatusMessage = $"{contacts!.Count} contact(s) saved for {updated.Name}.";
        await LoadAsync(current.Id.Value);
    }

    [RelayCommand]
    private void NewCustomer()
    {
        NewOrganization = new OrganizationForm();
        OnPropertyChanged(nameof(NewOrganization));
        NewContacts.Load([]);
        NewGroups.Load([]);
        NewContacts.Rows.Add(new ContactRow { IsPrimary = true, Role = CustomerFields.RoleText(Application.Reference.ContactRoles.PrimaryContact) });
        NewContacts.Rows.Add(new ContactRow { IsChangeApprover = true, IsDecisionOwner = true, Role = CustomerFields.RoleText(Application.Reference.ContactRoles.ChangeApprover) });
        NewCustomerName = string.Empty;
        NewCustomerTags = string.Empty;
        NewCustomerNotes = string.Empty;
        NewCustomerMessage = null;
        IsNewCustomerOpen = true;
    }

    [RelayCommand]
    private void CancelNewCustomer() => IsNewCustomerOpen = false;

    [RelayCommand]
    private void AddNewContact() => NewContacts.Rows.Add(new ContactRow());

    [RelayCommand]
    private void RemoveNewContact(ContactRow? row)
    {
        if (row is not null) NewContacts.Rows.Remove(row);
    }

    [RelayCommand]
    private async Task CreateCustomerAsync()
    {
        if (string.IsNullOrWhiteSpace(NewCustomerName) || NewCustomerName.Trim().Length > 200)
        {
            NewCustomerMessage = "Enter the customer's name (at most 200 characters).";
            return;
        }

        string key;
        try
        {
            key = EngineeringWorkspace.Domain.Common.StableIdentifier.FromDisplayName(NewCustomerName.Trim());
        }
        catch (EngineeringWorkspace.Domain.Common.DomainException exception)
        {
            NewCustomerMessage = exception.Message;
            return;
        }

        var id = new EngineeringWorkspace.Domain.Common.CustomerId(key);
        if (await _profiles.LoadAsync(id) is not null)
        {
            NewCustomerMessage = $"A customer with the ID '{key}' already exists. Choose another name or edit the existing profile.";
            return;
        }

        var (organization, error) = NewOrganization.Validate();
        var (contacts, contactError) = ContactRow.ValidateAll(NewContacts.Rows);
        var (groups, groupError) = UserGroupRow.ValidateAll(NewGroups.Rows);
        if ((error ?? contactError ?? groupError) is { } problem)
        {
            NewCustomerMessage = problem;
            return;
        }

        var profile = CustomerProfile.Create(id, NewCustomerName.Trim()) with
        {
            Organization = organization,
            Contacts = contacts!,
            UserGroups = groups!,
            Tags = NewCustomerTags.Split(',').Select(tag => tag.Trim()).Where(tag => tag.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Notes = NullIfEmpty(NewCustomerNotes?.Trim())
        };
        await _profiles.SaveAsync(profile);
        if (_auditStore.IsAvailable)
            await _auditStore.RecordChangesAsync([new EntityAuditRecord(0, EntityType, key, "Created", null, profile.Name, _audit.Actor, DateTimeOffset.UtcNow)]);
        IsNewCustomerOpen = false;
        StatusMessage = $"{profile.Name} created.";
        await LoadAsync(key);
        if (_connect is not null)
        {
            // 4.0 beta.2: offer to connect the tenant straight away; a quick scan fills users and groups for the pickers.
            _connectCustomer = profile.Id;
            ConnectCustomerName = profile.Name;
            ConnectTenantId = string.Empty;
            ConnectCloud = Domain.Tenants.CloudEnvironment.Commercial;
            ConnectStatus = null;
            IsConnectOpen = true;
        }
        else
        {
            StatusMessage = $"{profile.Name} created. Connect its tenant on Tenant App Registration.";
        }
    }

    private static string CredentialText(Domain.Tenants.ApplicationRegistration? registration) => registration switch
    {
        null or { SecretReference: null } => "Not stored",
        { AuthenticationType: Domain.Tenants.TenantAuthenticationType.Certificate } =>
            $"Certificate {registration.CertificateReference?[..Math.Min(8, registration.CertificateReference.Length)]}… (private key stored securely; never shown or exported)",
        _ => "Client secret (stored securely). Use Update Read Access to switch to a certificate."
    };

    // ---- Tenant access (4.0 beta.2): Read registration and deploy-phase Deploy registration -----------------------
    [ObservableProperty] private string? _accessStatus;
    [ObservableProperty] private bool _isAccessBusy;

    private Domain.Tenants.TenantConfiguration? SelectedTenant => SelectedRow?.Profile.PrimaryTenant;
    public bool CanManageAccess => _connect is not null && SelectedTenant?.ApplicationRegistration is not null;
    public bool HasDeploymentAccess => SelectedTenant?.DeploymentRegistration is not null;

    public string ReadAccessText => SelectedTenant?.ApplicationRegistration is { } read
        ? $"Read registration {read.ClientId}: read-only Microsoft Graph, Exchange and Purview (Global Reader). {CredentialText(read)}."
        : "Connect the tenant first.";

    public string DeploymentAccessText => SelectedTenant?.DeploymentRegistration is { } deploy
        ? $"Deployment access is ON: Deploy registration {deploy.ClientId} holds write permissions (certificate expires {deploy.CredentialExpirationUtc?.ToLocalTime():yyyy-MM-dd}). " +
          "Remove it when the deploy phase ends."
        : "Deployment access is off. Change Execution can only apply changes while it is on; enable it at the start of a deploy phase.";

    [RelayCommand]
    private void UpdateReadAccess() => RunAccessJob("Update Read Access",
        "Brings the Read registration up to date: read-only permissions, Exchange and Purview access, the Global Reader role and a certificate. Write permissions are removed.",
        "Update", false, customer => _connect!.Onboarding.UpdatePermissions(customer));

    [RelayCommand]
    private void EnableDeploymentAccess() => RunAccessJob("Enable Deployment Access",
        "Creates the 'GDS Secure Plus Deploy' app registration with the write permissions Change Execution uses (Conditional Access, Intune and app protection policies) " +
        "and a certificate. Use it only for the deploy phase, then remove it.",
        "Enable", false, customer => _connect!.Onboarding.EnableDeployment(customer));

    [RelayCommand]
    private void RemoveDeploymentAccess() => RunAccessJob("Remove Deployment Access",
        "Deletes the 'GDS Secure Plus Deploy' app registration from the tenant and its certificate from the secret store. Changes can no longer be applied until it is enabled again.",
        "Remove", true, customer => _connect!.Onboarding.RemoveDeployment(customer));

    private void RunAccessJob(string title, string message, string confirm, bool destructive, Func<Domain.Common.CustomerId, Application.Jobs.JobSnapshot> start)
    {
        if (_connect is null || SelectedRow?.Profile is not { } profile || IsAccessBusy) return;
        if (_connect.Dialogs is { } dialogs && !dialogs.Confirm(new Services.ConfirmationRequest(title, message,
                [new("Customer", profile.Name), new("Tenant", profile.PrimaryTenant?.PrimaryDomain ?? profile.PrimaryTenant?.TenantId?.ToString() ?? "—"),
                 new("Sign-in", "A Global Administrator signs in in the browser")], confirm, destructive)))
            return;
        IsAccessBusy = true;
        AccessStatus = "Waiting for the administrator to sign in in the browser... progress is also shown in the activity panel.";
        var key = profile.Id.Value;
        var job = start(profile.Id);
        void OnChanged(object? sender, Application.Jobs.JobSnapshot changed)
        {
            if (changed.Id != job.Id) return;
            Shell.ShellViewModel.OnUi(async () =>
            {
                AccessStatus = changed.IsFinished
                    ? changed.State == Application.Jobs.JobState.Succeeded ? changed.Result : $"{changed.State}: {changed.Error}"
                    : changed.CurrentActivity;
                if (!changed.IsFinished) return;
                _connect.Jobs.JobChanged -= OnChanged;
                IsAccessBusy = false;
                await LoadAsync(key);
            });
        }

        _connect.Jobs.JobChanged += OnChanged;
    }

    // ---- Connect tenant (4.0 beta.2) ------------------------------------------------------------------------------
    private readonly TenantConnectServices? _connect;
    private Domain.Common.CustomerId? _connectCustomer;
    [ObservableProperty] private bool _isConnectOpen;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private string _connectCustomerName = string.Empty;
    [ObservableProperty] private string _connectTenantId = string.Empty;
    [ObservableProperty] private Domain.Tenants.CloudEnvironment _connectCloud = Domain.Tenants.CloudEnvironment.Commercial;
    [ObservableProperty] private string? _connectStatus;

    public IReadOnlyList<Domain.Tenants.CloudEnvironment> CloudEnvironments { get; } = Enum.GetValues<Domain.Tenants.CloudEnvironment>();

    /// <summary>What the registration grants, shown before the administrator signs in.</summary>
    public string ConnectPermissionsText => _connect is null
        ? string.Empty
        : "Read-only Microsoft Graph application permissions: " + string.Join(", ",
              _connect.Permissions.RequiredApplicationPermissions(Application.Tenants.PermissionValidationService.ReadCapabilities)) +
          ". Also Exchange.ManageAsApp and the Global Reader role (read-only) for Exchange, Purview and Teams settings. Authentication uses a certificate whose private key " +
          "stays in the encrypted secret store. No write permissions: deployment access is created separately for deploy phases.";

    [RelayCommand]
    private void ConnectTenant()
    {
        if (_connect is null || _connectCustomer is not { } customer || IsConnecting) return;
        Guid? tenant = null;
        if (!string.IsNullOrWhiteSpace(ConnectTenantId))
        {
            if (!Guid.TryParse(ConnectTenantId.Trim(), out var parsed))
            {
                ConnectStatus = "Enter the tenant ID (a GUID), or leave it empty to use the tenant of the administrator who signs in.";
                return;
            }

            tenant = parsed;
        }

        IsConnecting = true;
        ConnectStatus = "Waiting for the administrator to sign in in the browser (Global Administrator)... progress is also shown in the activity panel.";
        var job = _connect.Onboarding.RegisterAutomatically(customer, tenant, ConnectCloud, startQuickScan: true);
        void OnChanged(object? sender, Application.Jobs.JobSnapshot changed)
        {
            if (changed.Id != job.Id) return;
            Shell.ShellViewModel.OnUi(async () =>
            {
                ConnectStatus = changed.IsFinished
                    ? changed.State == Application.Jobs.JobState.Succeeded ? changed.Result : $"{changed.State}: {changed.Error} You can retry here or on Tenant App Registration."
                    : changed.CurrentActivity;
                if (!changed.IsFinished) return;
                _connect.Jobs.JobChanged -= OnChanged;
                IsConnecting = false;
                await LoadAsync(customer.Value);
            });
        }

        _connect.Jobs.JobChanged += OnChanged;
    }

    [RelayCommand]
    private void SkipConnect()
    {
        IsConnectOpen = false;
        StatusMessage = $"{ConnectCustomerName} created. Connect its tenant later on Tenant App Registration.";
    }

    [RelayCommand]
    private void Edit(ProfileField? field)
    {
        if (field is null || !field.IsEditable) return;
        foreach (var other in Fields.Where(other => other != field && other.IsEditing)) other.IsEditing = false;
        field.EditText = field.Value;
        field.Error = null;
        field.IsEditing = true;
    }

    [RelayCommand]
    private void Cancel(ProfileField? field)
    {
        if (field is null) return;
        field.IsEditing = false;
        field.Error = null;
    }

    [RelayCommand]
    private async Task SaveFieldAsync(ProfileField? field)
    {
        if (field is null || SelectedRow is not { } row) return;
        try
        {
            await SaveFieldCoreAsync(field, row);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            // Never fail silently: the field stays open with the reason.
            field.Error = $"Not saved: {exception.Message}";
        }
    }

    private async Task SaveFieldCoreAsync(ProfileField field, ProfileListRow row)
    {
        var current = await _profiles.LoadAsync(row.Profile.Id) ?? row.Profile;
        var (updated, error) = field.Apply(current);
        if (error is not null || updated is null)
        {
            field.Error = error ?? "The value is not valid.";
            return;
        }

        if (field.Section == "Specification")
        {
            if (!_specifications.IsAvailable)
            {
                field.Error = "The application database is not ready; specifications are stored in it.";
                return;
            }

            var spec = _specs.FirstOrDefault(item => item.Name == field.EditText);
            await _specifications.AssignSpecificationAsync(current.Id.Value, spec?.SpecificationKey, _audit.Actor);
        }
        else
        {
            await _profiles.SaveAsync(updated);
            if (_context.CurrentCustomer?.Id == updated.Id) _context.Refresh(updated);
        }

        if (_auditStore.IsAvailable && field.Value != field.EditText)
            await _auditStore.RecordChangesAsync([new EntityAuditRecord(0, EntityType, current.Id.Value, $"{field.Section}: {field.Label}", field.Value, field.EditText, _audit.Actor, DateTimeOffset.UtcNow)]);
        StatusMessage = $"{field.Label} saved for {updated.Name}.";
        await LoadAsync(current.Id.Value);
    }

    [RelayCommand]
    private async Task ExportProfileAsync(object? format)
    {
        if (SelectedRow is not { } row) return;
        StatusMessage = await _export.ExportAsync(Document($"Customer Profile – {row.Name}", [row]), ExportCoordinator.Parse(format)) ?? StatusMessage;
    }

    /// <summary>Tenant users and groups from the last scan (directory attributes, unmasked as approved; never credentials).</summary>
    [RelayCommand]
    private async Task ExportInventoryAsync(object? format)
    {
        if (SelectedRow is not { } row) return;
        static IReadOnlyList<string?> R(params string?[] values) => values;
        var document = new ExportDocument($"Tenant Users and Groups – {row.Name}", row.Name, DateTimeOffset.UtcNow,
            [new("Users", InventoryUsers.Count.ToString(CultureInfo.InvariantCulture)), new("Groups", InventoryGroups.Count.ToString(CultureInfo.InvariantCulture))],
            [
                new ExportTable("Users", ["Name", "UPN", "Department", "Job Title", "Type", "Enabled", "MFA", "Licences", "Admin Roles", "Last Sign-in", "Object ID"],
                    InventoryUsers.Select(user => R(user.DisplayName, user.UserPrincipalName, user.Department, user.JobTitle, user.UserType, user.AccountEnabled ? "Yes" : "No",
                        user.MfaText, user.LicensesText, user.RolesText, user.LastSignInText, user.ObjectId)).ToArray()),
                new ExportTable("Groups", ["Group", "Type", "Dynamic", "Members", "Synced", "Object ID"],
                    InventoryGroups.Select(group => R(group.DisplayName, group.Kind, group.IsDynamic ? "Yes" : "No",
                        group.MemberCount?.ToString(CultureInfo.InvariantCulture), group.IsSynced ? "Yes" : "No", group.ObjectId)).ToArray())
            ]);
        StatusMessage = await _export.ExportAsync(document, ExportCoordinator.Parse(format)) ?? StatusMessage;
    }

    [RelayCommand]
    private async Task ExportAllAsync(object? format) =>
        StatusMessage = await _export.ExportAsync(Document("Customer Profiles", Rows.ToArray()), ExportCoordinator.Parse(format)) ?? StatusMessage;

    /// <summary>Profiles as an export document: properties, tenants (secret presence only), contacts and settings (secret keys masked).</summary>
    private static ExportDocument Document(string title, IReadOnlyList<ProfileListRow> rows)
    {
        static IReadOnlyList<string?> R(params string?[] values) => values;
        var single = rows.Count == 1 ? rows[0] : null;
        return new ExportDocument(title, single?.Name, DateTimeOffset.UtcNow,
            single is null
                ? [new("Customers", rows.Count.ToString(CultureInfo.InvariantCulture)), new("Connected", rows.Count(row => row.IsConnected).ToString(CultureInfo.InvariantCulture))]
                : [new("Customer ID", single.Profile.Id.Value), new("Environment", single.Profile.Environment ?? "—"), new("Tags", single.Tags), new("Specification", single.Specification ?? "(None)"),
                   new("Notes", single.Profile.Notes ?? "—")],
            [
                new ExportTable("Customers", ["Customer", "Customer ID", "Industry", "Organization Size", "Address", "City", "State / Region", "Postal Code", "Country", "Phone",
                        "E-mail", "Website", "CRM Account ID", "Sales Lead", "Technical Lead", "Project Manager", "Service Tier", "Tags", "Specification", "Tenants", "Connection",
                        "Additional Domains"],
                    rows.Select(row =>
                    {
                        var details = row.Profile.Details;
                        return R(row.Name, row.Profile.Id.Value, CustomerFields.IndustryDisplay(details),
                            details.OrganizationSizeCode is null ? null : CustomerFields.SizeText(details.OrganizationSizeCode),
                            string.Join(", ", new[] { details.AddressLine1, details.AddressLine2 }.Where(line => !string.IsNullOrWhiteSpace(line))), details.City, details.StateRegion,
                            details.PostalCode, details.Country, details.Phone, details.Email, details.Website, details.CrmAccountId, details.SalesLead, details.TechnicalLead,
                            details.ProjectManager, details.ServiceTierCode is null ? null : CustomerFields.TierText(details.ServiceTierCode), row.Tags, row.Specification,
                            row.Tenants.ToString(CultureInfo.InvariantCulture), row.Connection, string.Join(", ", row.Profile.AdditionalDomains));
                    }).ToArray()),
                new ExportTable("Tenants", ["Customer", "Tenant Key", "Display Name", "Tenant ID", "Primary Domain", "Cloud", "Client ID", "Authentication", "Client Secret", "Credential Expires"],
                    rows.SelectMany(row => row.Profile.Tenants.Select(tenant => R(row.Name, tenant.TenantKey, tenant.DisplayName, tenant.TenantId?.ToString(), tenant.PrimaryDomain,
                        tenant.CloudEnvironment.ToString(), tenant.ApplicationRegistration?.ClientId.ToString(), tenant.ApplicationRegistration?.AuthenticationType.ToString(),
                        tenant.ApplicationRegistration?.SecretReference is null ? "Not stored" : "Stored (not exported)",
                        tenant.ApplicationRegistration?.CredentialExpirationUtc?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))).ToArray()),
                new ExportTable("Contacts", ["Customer", "Name", "Title", "Role", "E-mail", "Phone", "Mobile", "Primary Contact", "Change Approver", "Decision Owner"],
                    rows.SelectMany(row => row.Profile.Contacts.Select(contact => R(row.Name, contact.Name, contact.Title, CustomerFields.RoleText(contact.Role), contact.Email, contact.Phone,
                        contact.MobilePhone, contact.IsPrimary ? "Yes" : "No", contact.IsChangeApprover ? "Yes" : "No", contact.IsDecisionOwner ? "Yes" : "No"))).ToArray()),
                new ExportTable("Settings", ["Customer", "Kind", "Key", "Value"],
                    rows.SelectMany(row => row.Profile.IntegrationSettings.Select(pair => (Kind: "Integration", pair)).Concat(row.Profile.WorkspaceSettings.Select(pair => (Kind: "Workspace", pair)))
                        .Select(item => R(row.Name, item.Kind, item.pair.Key, ExportService.IsSecretName(item.pair.Key) ? ExportService.Masked : item.pair.Value))).ToArray())
            ], "Secrets (client secrets, certificates, passwords) are never exported.");
    }
}
