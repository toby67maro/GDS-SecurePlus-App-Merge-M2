using EngineeringWorkspace.Domain.Security;

namespace EngineeringWorkspace.Domain.Tenants;

/// <summary>Microsoft cloud a tenant lives in. Values are persisted by name; add new clouds at the end.</summary>
public enum CloudEnvironment
{
    Commercial,
    Gcc,
    GccHigh,
    DoD
}

/// <summary>How the application authenticates to a customer tenant.</summary>
public enum TenantAuthenticationType
{
    /// <summary>Client credentials with a client secret held in the secret store (registrations created before 4.0 beta.2).</summary>
    ClientSecret,

    /// <summary>Client credentials with a certificate whose private key is held in the encrypted secret store (default from 4.0 beta.2).</summary>
    Certificate,

    /// <summary>Delegated administrator sign-in used only by the app-registration setup assistant.</summary>
    InteractiveAdministrator,

    /// <summary>Managed identity for future hosted use (not available on a workstation — M3).</summary>
    ManagedIdentity
}

/// <summary>A unit of functionality that needs Microsoft Graph application permissions.</summary>
public enum TenantCapability
{
    TenantDiscovery,
    EntraAssessment,
    ConditionalAccessAssessment,
    IntuneAssessment,
    DefenderAssessment,
    PurviewAssessment,
    ExchangeAssessment,
    SharePointAssessment,
    TeamsAssessment,
    LicenseAssessment,
    ApplicationAssessment,
    DeviceAssessment,

    /// <summary>
    /// Creates the approved change requests' policies in the tenant (Conditional Access in report-only state). From 4.0 beta.2 these
    /// write permissions belong only to the separate Deploy registration, which exists during deploy phases.
    /// </summary>
    PolicyDeployment
}

/// <summary>The application registration used to reach one customer tenant. Holds references, never secrets.</summary>
public sealed record ApplicationRegistration(
    Guid ClientId,
    Guid? ServicePrincipalId,
    TenantAuthenticationType AuthenticationType,
    SecretReference? SecretReference,
    string? CertificateReference,
    DateTimeOffset? CredentialExpirationUtc,
    IReadOnlyList<TenantCapability> RequiredCapabilities);

/// <summary>
/// One Microsoft 365 tenant of a customer. A customer can have several tenants (for example production and
/// test, or commercial and GCC High), identified by <see cref="TenantKey"/> within the customer.
/// </summary>
public sealed record TenantConfiguration(
    string TenantKey,
    string DisplayName,
    string? TenantName,
    Guid? TenantId,
    string? PrimaryDomain,
    CloudEnvironment CloudEnvironment,
    ApplicationRegistration? ApplicationRegistration)
{
    /// <summary>
    /// The Deploy registration (4.0 beta.2): write permissions only, created when a deploy phase starts and removed when it ends.
    /// <see cref="ApplicationRegistration"/> is the Read registration used by scans, the inventory and the connection test.
    /// </summary>
    public ApplicationRegistration? DeploymentRegistration { get; init; }
}
