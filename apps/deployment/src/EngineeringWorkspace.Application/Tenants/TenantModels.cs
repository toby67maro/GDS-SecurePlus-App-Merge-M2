using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Domain.Security;
using EngineeringWorkspace.Domain.Tenants;

namespace EngineeringWorkspace.Application.Tenants;

/// <summary>Tenant connection state. Acquiring a token never implies that permissions are valid.</summary>
public enum TenantConnectionState
{
    NotConfigured,
    CredentialMissing,
    CredentialExpired,
    AuthenticationFailed,
    Authenticated,
    GraphConnectionFailed,
    PermissionValidationFailed,
    Connected
}

/// <summary>Result of acquiring an app-only token. The token itself never leaves Infrastructure.</summary>
public sealed record TenantAuthenticationResult(
    bool Succeeded,
    Guid? AuthenticatedTenantId,
    IReadOnlyList<string> GrantedApplicationPermissions,
    DateTimeOffset? ExpiresOnUtc,
    OperationError? Error);

/// <summary>The organization returned by Microsoft Graph.</summary>
public sealed record TenantOrganization(Guid TenantId, string? DisplayName, IReadOnlyList<string> VerifiedDomains, string? DefaultDomain);

/// <summary>Required permission row: Capability | Required Permission | Permission Type | Configured | Validated | Status.</summary>
public sealed record CapabilityPermissionStatus(
    TenantCapability Capability,
    string? RequiredPermission,
    string PermissionType,
    bool Configured,
    CheckState Validated,
    string Status);

/// <summary>Structured readiness checklist of a tenant connection test.</summary>
public sealed record TenantReadiness(
    CheckState Configuration,
    CheckState Credential,
    CheckState CredentialExpiration,
    CheckState Authentication,
    CheckState MicrosoftGraph,
    CheckState TenantIdentity,
    CheckState Permissions)
{
    public static TenantReadiness NotRun { get; } = new(CheckState.NotRun, CheckState.NotRun, CheckState.NotRun, CheckState.NotRun,
        CheckState.NotRun, CheckState.NotRun, CheckState.NotRun);

    public CheckState Overall =>
        new[] { Configuration, Credential, CredentialExpiration, Authentication, MicrosoftGraph, TenantIdentity, Permissions } switch
        {
            var checks when checks.Any(check => check == CheckState.Failed) => CheckState.Failed,
            var checks when checks.All(check => check is CheckState.Passed or CheckState.NotApplicable) => CheckState.Passed,
            var checks when checks.Any(check => check == CheckState.Warning) && checks.All(check => check != CheckState.NotRun) => CheckState.Warning,
            _ => CheckState.NotRun
        };
}

/// <summary>Sanitized outcome of a tenant connection test, saved and audited.</summary>
public sealed record TenantConnectionReport(
    TenantConnectionState State,
    TenantReadiness Readiness,
    IReadOnlyList<CapabilityPermissionStatus> Permissions,
    TenantOrganization? Organization,
    DateTimeOffset AttemptedAtUtc,
    DateTimeOffset? LastSuccessfulConnectionUtc,
    DateTimeOffset? LastValidationUtc,
    OperationError? Error);

/// <summary>Microsoft endpoints for a cloud, from Microsoft's national-cloud documentation.</summary>
public sealed record MicrosoftCloudEndpoints(CloudEnvironment Cloud, Uri Authority, Uri GraphBaseUrl)
{
    public string GraphDefaultScope => new Uri(GraphBaseUrl, ".default").ToString();

    /// <summary>
    /// Commercial and GCC use the global endpoints; GCC High uses Azure Government and graph.microsoft.us; DoD uses
    /// Azure Government and dod-graph.microsoft.us (learn.microsoft.com/graph/deployments).
    /// </summary>
    public static MicrosoftCloudEndpoints For(CloudEnvironment cloud) => cloud switch
    {
        CloudEnvironment.Commercial or CloudEnvironment.Gcc => new(cloud, new Uri("https://login.microsoftonline.com/"), new Uri("https://graph.microsoft.com/")),
        CloudEnvironment.GccHigh => new(cloud, new Uri("https://login.microsoftonline.us/"), new Uri("https://graph.microsoft.us/")),
        CloudEnvironment.DoD => new(cloud, new Uri("https://login.microsoftonline.us/"), new Uri("https://dod-graph.microsoft.us/")),
        _ => throw new ArgumentOutOfRangeException(nameof(cloud), cloud, "Unsupported cloud environment.")
    };
}

/// <summary>Acquires tokens through MSAL. Tokens are never returned to Application or shown.</summary>
public interface IMicrosoftAuthenticationService
{
    Task<TenantAuthenticationResult> AcquireAppTokenAsync(Guid tenantId, Guid clientId, SecretReference clientSecret, CloudEnvironment cloud, CancellationToken cancellationToken);
}

/// <summary>Minimal Microsoft Graph operations needed by the current features.</summary>
public interface ITenantGraphClient
{
    Task<OperationResult<TenantOrganization>> GetOrganizationAsync(CancellationToken cancellationToken);
}

/// <summary>Creates authenticated Graph clients. Consumers never see how credentials are stored.</summary>
public interface IMicrosoftGraphClientFactory
{
    ITenantGraphClient CreateForApplication(Guid tenantId, Guid clientId, SecretReference clientSecret, CloudEnvironment cloud);
}

/// <summary>Capability-to-permission catalog and validation against the permissions granted in the token.</summary>
public interface IPermissionValidationService
{
    IReadOnlyList<CapabilityPermissionStatus> Describe(IReadOnlyCollection<TenantCapability> configured);
    IReadOnlyList<CapabilityPermissionStatus> Validate(IReadOnlyCollection<TenantCapability> configured, IReadOnlyCollection<string> grantedApplicationPermissions);

    /// <summary>The least-privileged Graph application permissions the configured capabilities need (only defined ones).</summary>
    IReadOnlyList<string> RequiredApplicationPermissions(IReadOnlyCollection<TenantCapability> configured);
}

/// <summary>Stores a tenant client secret straight into the secret store and returns only its reference.</summary>
public interface ITenantCredentialService
{
    Task<OperationResult<SecretReference>> StoreClientSecretAsync(
        string customerId,
        TenantConfiguration tenant,
        string clientSecret,
        DateTimeOffset? expiresOnUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a certificate credential (the opaque value produced by the certificate service, private key included) in the secret store.
    /// <paramref name="purpose"/> is "graph" for the Read registration or "deploy" for the Deploy registration.
    /// </summary>
    Task<OperationResult<SecretReference>> StoreCertificateAsync(
        string customerId,
        TenantConfiguration tenant,
        string purpose,
        string storedValue,
        DateTimeOffset? expiresOnUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult<SecretReference>.Failure("NotSupported", "Certificate credentials are not supported by this credential service."));

    /// <summary>Removes a stored credential and its metadata (used when a Deploy registration is removed).</summary>
    Task<OperationResult> DeleteCredentialAsync(SecretReference reference, CancellationToken cancellationToken = default) =>
        Task.FromResult(OperationResult.Success());
}

/// <summary>Tests an existing app registration end to end.</summary>
public interface ITenantConnectionService
{
    Task<TenantConnectionReport> TestConnectionAsync(string customerId, TenantConfiguration tenant, CancellationToken cancellationToken = default);
    TenantConnectionReport? GetLastReport(string customerId, string tenantKey);
    event EventHandler<TenantConnectionReport>? ConnectionTested;
}
