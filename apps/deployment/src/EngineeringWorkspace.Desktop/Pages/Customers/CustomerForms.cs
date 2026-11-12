using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using EngineeringWorkspace.Application.Reference;
using EngineeringWorkspace.Domain.Customers;

namespace EngineeringWorkspace.Desktop.Pages.Customers;

/// <summary>Validation and option lists shared by the customer forms.</summary>
public static partial class CustomerFields
{
    public const string NotSet = "(Not Set)";

    /// <summary>The Industry pick-list (4.0; replaces NAICS). Loaded from the database by the Customer Profiles page; the built-in list until then.</summary>
    public static IReadOnlyList<IndustryValue> Industries { get; set; } = IndustryList.Defaults;

    public static IReadOnlyList<string> IndustryOptions => [NotSet, .. Industries.Where(industry => industry.IsActive).Select(industry => industry.Name)];

    public static IReadOnlyList<string> SizeOptions { get; } = [NotSet, .. EaasReferenceData.OrganizationSizes.Select(size => size.Label)];

    public static IReadOnlyList<string> TierOptions { get; } = [NotSet, .. EaasReferenceData.ServiceTiers.Select(tier => tier.Name)];

    public static IReadOnlyList<string> RoleOptions { get; } =
        [NotSet, .. EaasReferenceData.CodeSetList.Single(set => set.Code == CodeSets.ContactRole).Values.Select(role => role.Label)];

    /// <summary>The list name of an industry key (not the free text of Other).</summary>
    public static string IndustryText(string? key) => key is null ? NotSet : Industries.FirstOrDefault(industry => industry.Code == key)?.Name ?? key;

    public static string? IndustryKey(string? text) => Industries.FirstOrDefault(industry => industry.Name == text)?.Code;

    /// <summary>What reports and exports show: the industry name, or the free text for Other (specify).</summary>
    public static string? IndustryDisplay(CustomerOrganization details) =>
        details.IndustryKey is null ? null : IndustryList.Display(Industries, details.IndustryKey, details.IndustryOther);

    public static string SizeText(string? code) => EaasReferenceData.OrganizationSizes.FirstOrDefault(size => size.Code == code)?.Label ?? NotSet;
    public static string? SizeCode(string? text) => EaasReferenceData.OrganizationSizes.FirstOrDefault(size => size.Label == text)?.Code;
    public static string TierText(string? code) => EaasReferenceData.ServiceTiers.FirstOrDefault(tier => tier.Code == code)?.Name ?? NotSet;
    public static string? TierCode(string? text) => EaasReferenceData.ServiceTiers.FirstOrDefault(tier => tier.Name == text)?.Code;

    private static IReadOnlyList<CodeValueDefinition> Roles => EaasReferenceData.CodeSetList.Single(set => set.Code == CodeSets.ContactRole).Values;
    public static string RoleText(string? code) => Roles.FirstOrDefault(role => role.Code == code)?.Label ?? (string.IsNullOrWhiteSpace(code) ? NotSet : code);
    public static string? RoleCode(string? text) => Roles.FirstOrDefault(role => role.Label == text)?.Code ?? (text is null or NotSet || string.IsNullOrWhiteSpace(text) ? null : text);

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^\+?[0-9 ().\-]{5,30}( *(x|ext\.?) *[0-9]{1,6})?$", RegexOptions.IgnoreCase)]
    private static partial Regex PhonePattern();

    public static string? EmailError(string? value) =>
        string.IsNullOrWhiteSpace(value) || EmailPattern().IsMatch(value.Trim()) ? null : $"'{value.Trim()}' is not an e-mail address.";

    public static string? PhoneError(string? value) =>
        string.IsNullOrWhiteSpace(value) || PhonePattern().IsMatch(value.Trim()) ? null : $"'{value.Trim()}' is not a phone number (digits, spaces, + ( ) - and an optional extension).";

    public static string? WebsiteError(string? value) =>
        string.IsNullOrWhiteSpace(value) || Uri.TryCreate(Website(value), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Contains('.', StringComparison.Ordinal)
            ? null
            : $"'{value.Trim()}' is not a web address.";

    /// <summary>Adds https:// when the scheme is missing.</summary>
    public static string? Website(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Contains("://", StringComparison.Ordinal) ? value.Trim() : $"https://{value.Trim()}";

    public static string? Clean(string? value, int max = 200) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length > max ? value.Trim()[..max] : value.Trim();
}

/// <summary>One editable contact of the contacts editor.</summary>
public sealed partial class ContactRow : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _role = CustomerFields.NotSet;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _phone = string.Empty;
    [ObservableProperty] private string _mobilePhone = string.Empty;
    [ObservableProperty] private bool _isPrimary;
    [ObservableProperty] private bool _isChangeApprover;
    [ObservableProperty] private bool _isDecisionOwner;

    public static ContactRow From(CustomerContact contact) => new()
    {
        Name = contact.Name, Title = contact.Title ?? string.Empty, Role = CustomerFields.RoleText(contact.Role), Email = contact.Email ?? string.Empty,
        Phone = contact.Phone ?? string.Empty, MobilePhone = contact.MobilePhone ?? string.Empty, IsPrimary = contact.IsPrimary, IsChangeApprover = contact.IsChangeApprover,
        IsDecisionOwner = contact.IsDecisionOwner
    };

    public bool IsBlank => string.IsNullOrWhiteSpace(Name) && string.IsNullOrWhiteSpace(Email) && string.IsNullOrWhiteSpace(Phone);

    /// <summary>The contact, or the first problem with it.</summary>
    public (CustomerContact? Contact, string? Error) Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return (null, "Every contact needs a name.");
        var error = CustomerFields.EmailError(Email) ?? CustomerFields.PhoneError(Phone) ?? CustomerFields.PhoneError(MobilePhone);
        if (error is not null) return (null, $"{Name.Trim()}: {error}");
        if (IsChangeApprover && string.IsNullOrWhiteSpace(Email) && string.IsNullOrWhiteSpace(Phone) && string.IsNullOrWhiteSpace(MobilePhone))
            return (null, $"{Name.Trim()} approves changes: add an e-mail address or a phone number.");
        return (new CustomerContact(Name.Trim(), CustomerFields.Clean(Email, 320), CustomerFields.Clean(Title), CustomerFields.RoleCode(Role), IsDecisionOwner,
            CustomerFields.Clean(Phone, 50), CustomerFields.Clean(MobilePhone, 50), IsPrimary, IsChangeApprover), null);
    }

    /// <summary>Validates all non-blank rows; at most one primary contact.</summary>
    public static (IReadOnlyList<CustomerContact>? Contacts, string? Error) ValidateAll(IEnumerable<ContactRow> rows)
    {
        var contacts = new List<CustomerContact>();
        foreach (var row in rows.Where(row => !row.IsBlank))
        {
            var (contact, error) = row.Validate();
            if (error is not null) return (null, error);
            contacts.Add(contact!);
        }

        if (contacts.Count(contact => contact.IsPrimary) > 1) return (null, "Choose only one primary contact.");
        return (contacts, null);
    }
}

/// <summary>Organization details and GDS team of a customer, as a form (used by New Customer).</summary>
public sealed partial class OrganizationForm : ObservableObject
{
    [ObservableProperty] private string _industry = CustomerFields.NotSet;
    [ObservableProperty] private string _organizationSize = CustomerFields.NotSet;
    [ObservableProperty] private string _addressLine1 = string.Empty;
    [ObservableProperty] private string _addressLine2 = string.Empty;
    [ObservableProperty] private string _city = string.Empty;
    [ObservableProperty] private string _stateRegion = string.Empty;
    [ObservableProperty] private string _postalCode = string.Empty;
    [ObservableProperty] private string _country = "United States";
    [ObservableProperty] private string _phone = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _website = string.Empty;
    [ObservableProperty] private string _crmAccountId = string.Empty;
    [ObservableProperty] private string _salesLead = string.Empty;
    [ObservableProperty] private string _technicalLead = string.Empty;
    [ObservableProperty] private string _projectManager = string.Empty;
    [ObservableProperty] private string _serviceTier = CustomerFields.NotSet;
    [ObservableProperty] private string _industryOther = string.Empty;

    public IReadOnlyList<string> IndustryOptions => CustomerFields.IndustryOptions;
    public IReadOnlyList<string> SizeOptions => CustomerFields.SizeOptions;
    public IReadOnlyList<string> TierOptions => CustomerFields.TierOptions;

    public (CustomerOrganization? Organization, string? Error) Validate()
    {
        var error = CustomerFields.EmailError(Email) ?? CustomerFields.PhoneError(Phone) ?? CustomerFields.WebsiteError(Website);
        if (error is not null) return (null, error);
        var industry = CustomerFields.IndustryKey(Industry);
        if (industry == IndustryList.Other && string.IsNullOrWhiteSpace(IndustryOther)) return (null, "Describe the industry for Other (specify).");
        return (new CustomerOrganization(null, CustomerFields.SizeCode(OrganizationSize), CustomerFields.Clean(AddressLine1),
            CustomerFields.Clean(AddressLine2), CustomerFields.Clean(City, 100), CustomerFields.Clean(StateRegion, 60), CustomerFields.Clean(PostalCode, 20),
            CustomerFields.Clean(Country, 60), CustomerFields.Clean(Phone, 50), CustomerFields.Clean(Email, 320), CustomerFields.Clean(CustomerFields.Website(Website), 300),
            CustomerFields.Clean(CrmAccountId, 50), CustomerFields.Clean(SalesLead), CustomerFields.Clean(TechnicalLead), CustomerFields.Clean(ProjectManager),
            CustomerFields.TierCode(ServiceTier), industry, industry == IndustryList.Other ? CustomerFields.Clean(IndustryOther, 200) : null), null);
    }
}

/// <summary>Contacts being edited, with add and remove.</summary>
public sealed class ContactsEditor
{
    public ObservableCollection<ContactRow> Rows { get; } = [];
    public IReadOnlyList<string> RoleOptions => CustomerFields.RoleOptions;

    public void Load(IEnumerable<CustomerContact> contacts)
    {
        Rows.Clear();
        foreach (var contact in contacts) Rows.Add(ContactRow.From(contact));
    }
}

/// <summary>A user group being edited (name, classification, size, experience profile).</summary>
public sealed partial class UserGroupRow : ObservableObject
{
    [ObservableProperty] private string _key = string.Empty;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _classification = Application.Experience.ExperienceCatalog.Classifications[1].Name;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _userCount = string.Empty;
    [ObservableProperty] private string _experienceProfile = string.Empty;
    [ObservableProperty] private string _entraGroup = string.Empty;

    /// <summary>Member count of a picked Entra group (from the last scan); fills an empty user count.</summary>
    internal Func<string, long?>? CountLookup { get; set; }

    partial void OnEntraGroupChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(UserCount) && CountLookup?.Invoke(value) is { } count && count <= 1_000_000)
            UserCount = count.ToString(System.Globalization.CultureInfo.CurrentCulture);
    }

    /// <summary>Experience answers carried over unchanged (edited on Experience Configuration).</summary>
    public IReadOnlyDictionary<string, string>? Answers { get; set; }

    partial void OnClassificationChanged(string value)
    {
        // Suggest the classification's profile when none was picked yet.
        if (string.IsNullOrWhiteSpace(ExperienceProfile))
            ExperienceProfile = Application.Experience.ExperienceCatalog.Profile(Application.Experience.ExperienceCatalog.Classification(value).DefaultProfile).Name;
    }

    public static UserGroupRow From(UserGroup group) => new()
    {
        Key = group.Key, Name = group.Name, Classification = Application.Experience.ExperienceCatalog.Classification(group.Classification).Name, Description = group.Description ?? string.Empty,
        UserCount = group.UserCount?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty,
        ExperienceProfile = Application.Experience.ExperienceCatalog.Profile(group.ExperienceProfile ?? Application.Experience.ExperienceCatalog.Classification(group.Classification).DefaultProfile).Name,
        EntraGroup = group.EntraGroup ?? string.Empty, Answers = group.Answers
    };

    public bool IsBlank => string.IsNullOrWhiteSpace(Name);

    public static (IReadOnlyList<UserGroup>? Groups, string? Error) ValidateAll(IEnumerable<UserGroupRow> rows)
    {
        var groups = new List<UserGroup>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Where(row => !row.IsBlank))
        {
            var name = row.Name.Trim();
            if (name.Length > 100) return (null, $"{name[..20]}…: a group name has at most 100 characters.");
            int? count = null;
            if (!string.IsNullOrWhiteSpace(row.UserCount))
            {
                if (!int.TryParse(row.UserCount.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.CurrentCulture, out var value) || value < 0 || value > 1_000_000)
                    return (null, $"{name}: the number of users must be a whole number from 0 to 1,000,000.");
                count = value;
            }

            var key = string.IsNullOrWhiteSpace(row.Key) ? Slug(name) : row.Key;
            while (!keys.Add(key)) key += "-2";
            var classification = Application.Experience.ExperienceCatalog.Classification(row.Classification);
            var profile = Application.Experience.ExperienceCatalog.Profiles.FirstOrDefault(item => item.Name == row.ExperienceProfile)?.Code ?? classification.DefaultProfile;
            groups.Add(new UserGroup(key, name, classification.Code, CustomerFields.Clean(row.Description, 500), count, profile, row.Answers, CustomerFields.Clean(row.EntraGroup, 200)));
        }

        if (groups.Count > CustomerProfile.MaxUserGroups) return (null, $"A customer can have at most {CustomerProfile.MaxUserGroups} user groups.");
        if (groups.Select(group => group.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != groups.Count) return (null, "Two groups have the same name.");
        return (groups, null);
    }

    private static string Slug(string name)
    {
        var slug = new string(name.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray()).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Length == 0 ? "group" : slug.Length > 40 ? slug[..40] : slug;
    }
}

/// <summary>User groups being edited, with add and remove (at most 25).</summary>
public sealed class UserGroupsEditor
{
    public System.Collections.ObjectModel.ObservableCollection<UserGroupRow> Rows { get; } = [];
    public IReadOnlyList<string> ClassificationOptions { get; } = Application.Experience.ExperienceCatalog.Classifications.Select(item => item.Name).ToArray();
    public IReadOnlyList<string> ProfileOptions { get; } = Application.Experience.ExperienceCatalog.Profiles.Select(item => item.Name).ToArray();

    /// <summary>The customer's Entra groups from the last scan (4.0 beta.2), plus any typed names of earlier versions so they still show.</summary>
    public System.Collections.ObjectModel.ObservableCollection<EntraGroupChoice> EntraGroups { get; } = [];

    public bool HasEntraGroups => EntraGroups.Any(choice => choice.IsFromTenant);

    public UserGroupsEditor() => Rows.CollectionChanged += (_, args) =>
    {
        foreach (var row in args.NewItems?.OfType<UserGroupRow>() ?? []) row.CountLookup = Count;
    };

    private long? Count(string objectId) => EntraGroups.FirstOrDefault(choice => string.Equals(choice.ObjectId, objectId, StringComparison.OrdinalIgnoreCase))?.MemberCount;

    public void Load(IEnumerable<UserGroup> groups)
    {
        Rows.Clear();
        foreach (var group in groups) Rows.Add(UserGroupRow.From(group));
        AddUnlinked();
    }

    /// <summary>Replaces the pick-list with the tenant's groups.</summary>
    public void LoadEntraGroups(IEnumerable<Application.Tenants.TenantGroup> groups)
    {
        EntraGroups.Clear();
        EntraGroups.Add(EntraGroupChoice.None);
        foreach (var group in groups) EntraGroups.Add(new EntraGroupChoice(group.ObjectId, group.Display, group.MemberCount, true));
        AddUnlinked();
    }

    /// <summary>Values typed before 4.0 beta.2 (names, not IDs) stay visible as "not linked" until a real group is picked.</summary>
    private void AddUnlinked()
    {
        if (EntraGroups.Count == 0) EntraGroups.Add(EntraGroupChoice.None);
        foreach (var value in Rows.Select(row => row.EntraGroup).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
            if (EntraGroups.All(choice => !string.Equals(choice.ObjectId, value, StringComparison.OrdinalIgnoreCase)))
                EntraGroups.Add(new EntraGroupChoice(value, Guid.TryParse(value, out _) ? $"{value} (not found in the last scan)" : $"{value} (typed name – pick the group)", null, false));
    }

    public bool CanAdd => Rows.Count < CustomerProfile.MaxUserGroups;
}

/// <summary>An Entra group in a picker; <see cref="ObjectId"/> is what is stored.</summary>
public sealed record EntraGroupChoice(string ObjectId, string Display, long? MemberCount, bool IsFromTenant)
{
    public static EntraGroupChoice None { get; } = new(string.Empty, "(none)", null, false);
}

/// <summary>What Customer Profiles needs to connect a new customer's tenant (4.0 beta.2).</summary>
public sealed record TenantConnectServices(
    EngineeringWorkspace.Application.Tenants.ITenantOnboardingService Onboarding,
    EngineeringWorkspace.Application.Jobs.IJobService Jobs,
    EngineeringWorkspace.Application.Tenants.IPermissionValidationService Permissions,
    EngineeringWorkspace.Application.Tenants.ITenantInventoryRepository? Inventory = null,
    EngineeringWorkspace.Desktop.Services.IDialogService? Dialogs = null);

/// <summary>A tenant user shown on Customer Profiles (read-only).</summary>
public sealed class InventoryUserRow(EngineeringWorkspace.Application.Tenants.TenantUser user)
{
    public string ObjectId => user.ObjectId;
    public string DisplayName => user.DisplayName;
    public string? UserPrincipalName => user.UserPrincipalName;
    public string? Department => user.Department;
    public string? JobTitle => user.JobTitle;
    public string? UserType => user.UserType;
    public bool AccountEnabled => user.AccountEnabled;
    public string MfaText => user.IsMfaRegistered switch { true => "Registered", false => "Not registered", _ => "—" };
    public string LicensesText => string.Join(", ", user.Licenses);
    public string RolesText => string.Join(", ", user.AdminRoles);
    public string LastSignInText => user.LastSignInUtc?.ToLocalTime().ToString("d", System.Globalization.CultureInfo.CurrentCulture) ?? "—";
}
