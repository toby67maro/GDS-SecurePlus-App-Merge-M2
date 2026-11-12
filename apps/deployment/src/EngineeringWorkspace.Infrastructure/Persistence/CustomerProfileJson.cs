using System.Text.Json;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Customers;
using EngineeringWorkspace.Domain.Security;
using EngineeringWorkspace.Domain.Tenants;

namespace EngineeringWorkspace.Infrastructure.Persistence;

/// <summary>
/// The JSON document of a customer profile in secureplus.CustomerProfile (schema 1). It carries identifiers and secret
/// references only; secrets live encrypted in secureplus.TenantSecret.
/// </summary>
public static class CustomerProfileJson
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static string Serialize(CustomerProfile profile) => JsonSerializer.Serialize(ToDocument(profile), Options);

    public static CustomerProfile Deserialize(string customerKey, string json)
    {
        var document = JsonSerializer.Deserialize<ProfileDocument>(json, Options) ?? throw new JsonException("The customer profile document is empty.");
        return FromDocument(customerKey, document);
    }

    private static ProfileDocument ToDocument(CustomerProfile profile) => new(
        SchemaVersion,
        profile.Name,
        profile.Environment,
        profile.Tenants.Select(tenant => new TenantDocument(tenant.TenantKey, tenant.DisplayName, tenant.TenantName, tenant.TenantId, tenant.PrimaryDomain,
            tenant.CloudEnvironment.ToString(), ToDocument(tenant.ApplicationRegistration), ToDocument(tenant.DeploymentRegistration))).ToArray(),
        profile.AdditionalDomains.ToArray(),
        profile.Contacts.Select(contact => new ContactDocument(contact.Name, contact.Email, contact.Title, contact.Role, contact.IsDecisionOwner, contact.Phone, contact.MobilePhone,
            contact.IsPrimary, contact.IsChangeApprover)).ToArray(),
        profile.Tags.ToArray(),
        profile.Notes,
        new Dictionary<string, string>(profile.IntegrationSettings, StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(profile.WorkspaceSettings, StringComparer.OrdinalIgnoreCase),
        profile.Organization,
        profile.Groups.Select(group => new UserGroupDocument(group.Key, group.Name, group.Classification, group.Description, group.UserCount, group.ExperienceProfile,
            new Dictionary<string, string>(group.EffectiveAnswers, StringComparer.OrdinalIgnoreCase), group.EntraGroup, group.Notes)).ToArray(),
        new Dictionary<string, string>(profile.DefaultExperienceAnswers, StringComparer.OrdinalIgnoreCase));

    private static CustomerProfile FromDocument(string customerKey, ProfileDocument document) => new(
        new CustomerId(customerKey),
        document.Name,
        document.Environment,
        (document.Tenants ?? []).Select(tenant => new TenantConfiguration(tenant.TenantKey, tenant.DisplayName ?? tenant.TenantKey, tenant.TenantName, tenant.TenantId,
            tenant.PrimaryDomain, Enum.TryParse<CloudEnvironment>(tenant.CloudEnvironment, true, out var cloud) ? cloud : CloudEnvironment.Commercial,
            FromDocument(tenant.Registration)) { DeploymentRegistration = FromDocument(tenant.DeploymentRegistration) }).ToArray(),
        document.AdditionalDomains ?? [],
        (document.Contacts ?? []).Select(contact => new CustomerContact(contact.Name, contact.Email, contact.Title, contact.Role, contact.IsDecisionOwner, contact.Phone,
            contact.MobilePhone, contact.IsPrimary, contact.IsChangeApprover)).ToArray(),
        document.Tags ?? [],
        document.Notes,
        new Dictionary<string, string>(document.IntegrationSettings ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(document.WorkspaceSettings ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
        document.Organization,
        (document.UserGroups ?? []).Take(CustomerProfile.MaxUserGroups).Select(group => new UserGroup(group.Key, group.Name, group.Classification ?? "Custom", group.Description,
            group.UserCount, group.ExperienceProfile, new Dictionary<string, string>(group.Answers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase), group.EntraGroup,
            group.Notes)).ToArray(),
        new Dictionary<string, string>(document.ExperienceAnswers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase));

    private static RegistrationDocument? ToDocument(ApplicationRegistration? registration) => registration is null
        ? null
        : new RegistrationDocument(registration.ClientId, registration.ServicePrincipalId, registration.AuthenticationType.ToString(), registration.SecretReference?.ToString(),
            registration.CertificateReference, registration.CredentialExpirationUtc, registration.RequiredCapabilities.Select(capability => capability.ToString()).ToArray());

    private static ApplicationRegistration? FromDocument(RegistrationDocument? registration) => registration is null
        ? null
        : new ApplicationRegistration(registration.ClientId, registration.ServicePrincipalId,
            Enum.TryParse<TenantAuthenticationType>(registration.AuthenticationType, true, out var authentication) ? authentication : TenantAuthenticationType.ClientSecret,
            SecretReference.TryParse(registration.SecretReference, out var reference) ? reference : null, registration.CertificateReference,
            registration.CredentialExpirationUtc,
            (registration.RequiredCapabilities ?? []).Select(value => Enum.TryParse<TenantCapability>(value, true, out var capability) ? capability : (TenantCapability?)null)
            .OfType<TenantCapability>().ToArray());

    private sealed record ProfileDocument(
        int Schema,
        string Name,
        string? Environment,
        TenantDocument[]? Tenants,
        string[]? AdditionalDomains,
        ContactDocument[]? Contacts,
        string[]? Tags,
        string? Notes,
        Dictionary<string, string>? IntegrationSettings,
        Dictionary<string, string>? WorkspaceSettings,
        CustomerOrganization? Organization,
        UserGroupDocument[]? UserGroups = null,
        Dictionary<string, string>? ExperienceAnswers = null);

    private sealed record UserGroupDocument(string Key, string Name, string? Classification, string? Description, int? UserCount, string? ExperienceProfile,
        Dictionary<string, string>? Answers, string? EntraGroup, string? Notes);

    private sealed record TenantDocument(string TenantKey, string? DisplayName, string? TenantName, Guid? TenantId, string? PrimaryDomain, string? CloudEnvironment,
        RegistrationDocument? Registration, RegistrationDocument? DeploymentRegistration = null);

    private sealed record RegistrationDocument(Guid ClientId, Guid? ServicePrincipalId, string? AuthenticationType, string? SecretReference, string? CertificateReference,
        DateTimeOffset? CredentialExpirationUtc, string[]? RequiredCapabilities);

    private sealed record ContactDocument(string Name, string? Email, string? Title, string? Role, bool IsDecisionOwner, string? Phone, string? MobilePhone, bool IsPrimary,
        bool IsChangeApprover);
}
