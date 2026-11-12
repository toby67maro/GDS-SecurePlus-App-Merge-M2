using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Tenants;

namespace EngineeringWorkspace.Domain.Customers;

/// <summary>A customer stakeholder (cust.Contact). <see cref="IsChangeApprover"/> marks who approves changes to the customer's tenants.</summary>
public sealed record CustomerContact(
    string Name,
    string? Email,
    string? Title,
    string? Role,
    bool IsDecisionOwner,
    string? Phone = null,
    string? MobilePhone = null,
    bool IsPrimary = false,
    bool IsChangeApprover = false);

/// <summary>
/// Organization details of a customer (cust.Customer) and the GDS people on the engagement (cust.Engagement).
/// <see cref="IndustryKey"/> is the Secure Plus industry (4.0; <see cref="IndustryOther"/> for "Other (specify)"). <see cref="IndustryCode"/> is the
/// 2022 NAICS sector code of earlier versions, kept but no longer shown or seeded; <see cref="OrganizationSizeCode"/> an employee-count band.
/// </summary>
public sealed record CustomerOrganization(
    string? IndustryCode = null,
    string? OrganizationSizeCode = null,
    string? AddressLine1 = null,
    string? AddressLine2 = null,
    string? City = null,
    string? StateRegion = null,
    string? PostalCode = null,
    string? Country = null,
    string? Phone = null,
    string? Email = null,
    string? Website = null,
    string? CrmAccountId = null,
    string? SalesLead = null,
    string? TechnicalLead = null,
    string? ProjectManager = null,
    string? ServiceTierCode = null,
    string? IndustryKey = null,
    string? IndustryOther = null)
{
    public static CustomerOrganization Empty { get; } = new();
}

/// <summary>
/// A bucket of the customer's users who need their own experience (executives, frontline workers, international
/// travellers...). <see cref="ExperienceProfile"/> is the starting experience; <see cref="Answers"/> override it per
/// experience question (question code → option code). At most <see cref="CustomerProfile.MaxUserGroups"/> per customer.
/// </summary>
public sealed record UserGroup(
    string Key,
    string Name,
    string Classification,
    string? Description = null,
    int? UserCount = null,
    string? ExperienceProfile = null,
    IReadOnlyDictionary<string, string>? Answers = null,
    string? EntraGroup = null,
    string? Notes = null)
{
    public IReadOnlyDictionary<string, string> EffectiveAnswers => Answers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A customer of Global Data Systems and its Microsoft environments. Holds non-secret identifiers and
/// secret references only.
/// </summary>
public sealed record CustomerProfile(
    CustomerId Id,
    string Name,
    string? Environment,
    IReadOnlyList<TenantConfiguration> Tenants,
    IReadOnlyList<string> AdditionalDomains,
    IReadOnlyList<CustomerContact> Contacts,
    IReadOnlyList<string> Tags,
    string? Notes,
    IReadOnlyDictionary<string, string> IntegrationSettings,
    IReadOnlyDictionary<string, string> WorkspaceSettings,
    CustomerOrganization? Organization = null,
    IReadOnlyList<UserGroup>? UserGroups = null,
    IReadOnlyDictionary<string, string>? ExperienceAnswers = null)
{
    /// <summary>The most user groups a customer can have.</summary>
    public const int MaxUserGroups = 25;

    /// <summary>User groups (never null).</summary>
    public IReadOnlyList<UserGroup> Groups => UserGroups ?? [];

    /// <summary>Experience answers that apply to every user unless a group overrides them (never null).</summary>
    public IReadOnlyDictionary<string, string> DefaultExperienceAnswers => ExperienceAnswers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Organization details (never null).</summary>
    public CustomerOrganization Details => Organization ?? CustomerOrganization.Empty;

    /// <summary>Contacts who approve changes; falls back to decision owners.</summary>
    public IReadOnlyList<CustomerContact> ChangeApprovers =>
        Contacts.Any(contact => contact.IsChangeApprover) ? Contacts.Where(contact => contact.IsChangeApprover).ToArray() : Contacts.Where(contact => contact.IsDecisionOwner).ToArray();

    /// <summary>The tenant used when a page needs a single tenant: the one keyed "primary", otherwise the first.</summary>
    public TenantConfiguration? PrimaryTenant =>
        Tenants.FirstOrDefault(tenant => string.Equals(tenant.TenantKey, "primary", StringComparison.OrdinalIgnoreCase))
        ?? Tenants.FirstOrDefault();

    public static CustomerProfile Create(CustomerId id, string name) =>
        new(id, name, null, [], [], [], [], null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>Lifecycle of a customer exception (risk acceptance).</summary>
public enum CustomerExceptionStatus
{
    Requested,
    Approved,
    Rejected,
    Expired,
    Revoked
}

/// <summary>
/// An approved deviation from a control or setting. Consumed later by assessments, reports, Golden Tenant
/// comparison, planning and deployment (M4-M7). Maps to EaaS v3 <c>cust.Exception</c>.
/// </summary>
public sealed record CustomerException(
    string ExceptionId,
    CustomerId CustomerId,
    string ControlId,
    string? SettingId,
    string Reason,
    string BusinessJustification,
    string RequestedBy,
    string? ApprovedBy,
    DateOnly EffectiveDate,
    DateOnly? ExpirationDate,
    CustomerExceptionStatus Status,
    string? Notes)
{
    public bool IsActiveOn(DateOnly date) =>
        Status == CustomerExceptionStatus.Approved && EffectiveDate <= date && (ExpirationDate is null || date <= ExpirationDate);
}
