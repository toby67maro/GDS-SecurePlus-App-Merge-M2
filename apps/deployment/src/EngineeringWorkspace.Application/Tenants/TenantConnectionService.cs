using System.Collections.Concurrent;
using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Credentials;
using EngineeringWorkspace.Domain.Auditing;
using EngineeringWorkspace.Domain.Credentials;
using EngineeringWorkspace.Domain.Security;
using EngineeringWorkspace.Domain.Tenants;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Application.Tenants;

/// <summary>
/// Orchestrates the existing-app-registration test: credential, MSAL token, minimal Graph call, tenant identity
/// and permission validation. Saves a sanitized report in memory for the session and audits every outcome.
/// </summary>
public sealed class TenantConnectionService(
    ICredentialService credentials,
    IMicrosoftAuthenticationService authentication,
    IMicrosoftGraphClientFactory graphFactory,
    IPermissionValidationService permissions,
    IAuditService audit,
    TimeProvider timeProvider,
    ILogger<TenantConnectionService> logger,
    Customers.ICustomerProfileRepository? profiles = null) : ITenantConnectionService
{
    private readonly ConcurrentDictionary<string, TenantConnectionReport> _reports = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<TenantConnectionReport>? ConnectionTested;

    public TenantConnectionReport? GetLastReport(string customerId, string tenantKey) =>
        _reports.TryGetValue(Key(customerId, tenantKey), out var report) ? report : null;

    public async Task<TenantConnectionReport> TestConnectionAsync(string customerId, TenantConfiguration tenant, CancellationToken cancellationToken = default)
    {
        var report = await RunAsync(tenant, cancellationToken);
        var previous = GetLastReport(customerId, tenant.TenantKey);
        report = report with
        {
            LastSuccessfulConnectionUtc = report.State == TenantConnectionState.Connected ? report.AttemptedAtUtc : previous?.LastSuccessfulConnectionUtc,
            LastValidationUtc = report.Readiness.Permissions is CheckState.Passed or CheckState.Failed ? report.AttemptedAtUtc : previous?.LastValidationUtc
        };
        _reports[Key(customerId, tenant.TenantKey)] = report;
        await AuditAsync(customerId, tenant, report, cancellationToken);
        await RecordConsentAsync(customerId, tenant, report, cancellationToken);
        logger.LogInformation("Tenant connection test for customer {CustomerId} tenant {TenantKey}: {State}", customerId, tenant.TenantKey, report.State);
        ConnectionTested?.Invoke(this, report);
        return report;
    }

    private async Task<TenantConnectionReport> RunAsync(TenantConfiguration tenant, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var registration = tenant.ApplicationRegistration;
        var configured = registration?.RequiredCapabilities.Count > 0 ? registration.RequiredCapabilities : [TenantCapability.TenantDiscovery];
        var described = permissions.Describe(configured);
        var readiness = TenantReadiness.NotRun;

        TenantConnectionReport Report(TenantConnectionState state, TenantReadiness checks, OperationError? error,
            IReadOnlyList<CapabilityPermissionStatus>? rows = null, TenantOrganization? organization = null) =>
            new(state, checks, rows ?? described, organization, now, null, null, error);

        if (tenant.TenantId is not { } tenantId || registration is null || registration.ClientId == Guid.Empty)
            return Report(TenantConnectionState.NotConfigured, readiness with { Configuration = CheckState.Failed },
                new OperationError("Configuration", "The tenant is not configured.", null, "Enter the Tenant ID and Client ID."));
        if (registration.AuthenticationType is not (TenantAuthenticationType.ClientSecret or TenantAuthenticationType.Certificate))
            return Report(TenantConnectionState.NotConfigured, readiness with { Configuration = CheckState.Failed },
                new OperationError("Configuration", $"{registration.AuthenticationType} authentication is not available on a workstation.", null,
                    "Use a certificate (Connect tenant or Update permissions creates one)."));
        readiness = readiness with { Configuration = CheckState.Passed };

        if (registration.SecretReference is not { } secretReference)
            return Report(TenantConnectionState.CredentialMissing, readiness with { Credential = CheckState.Failed },
                new OperationError("Credential", "No certificate or client secret is stored for this app registration.", null, "Use Update permissions to create a certificate."));
        var credential = await credentials.FindByReferenceAsync(secretReference, cancellationToken);
        if (credential is null || credential.Status is CredentialStatus.Missing or CredentialStatus.Invalid)
            return Report(TenantConnectionState.CredentialMissing, readiness with { Credential = CheckState.Failed },
                new OperationError("Credential", "Credential Required: the app credential is not available on this workstation.", credential?.StatusMessage,
                    "Use Update permissions to create a new certificate."));
        readiness = readiness with { Credential = CheckState.Passed };

        var expiration = registration.CredentialExpirationUtc ?? credential.Metadata.ExpirationDateUtc;
        if (expiration is { } expires && expires <= now)
            return Report(TenantConnectionState.CredentialExpired, readiness with { CredentialExpiration = CheckState.Failed },
                new OperationError("Credential", "The app credential has expired.", $"Expired on {expires:yyyy-MM-dd}.", "Use Update permissions to create a new certificate."));
        readiness = readiness with { CredentialExpiration = credential.Status == CredentialStatus.ExpiresSoon ? CheckState.Warning : CheckState.Passed };

        var token = await authentication.AcquireAppTokenAsync(tenantId, registration.ClientId, secretReference, tenant.CloudEnvironment, cancellationToken);
        if (!token.Succeeded)
            return Report(TenantConnectionState.AuthenticationFailed, readiness with { Authentication = CheckState.Failed }, token.Error);
        readiness = readiness with { Authentication = CheckState.Passed };

        var graph = graphFactory.CreateForApplication(tenantId, registration.ClientId, secretReference, tenant.CloudEnvironment);
        var organization = await graph.GetOrganizationAsync(cancellationToken);
        var validated = permissions.Validate(configured, token.GrantedApplicationPermissions);
        var permissionCheck = validated.Any(row => row.Validated == CheckState.Failed) ? CheckState.Failed : CheckState.Passed;
        if (!organization.Succeeded)
            return Report(TenantConnectionState.GraphConnectionFailed, readiness with { MicrosoftGraph = CheckState.Failed, Permissions = permissionCheck },
                organization.Error, validated);
        readiness = readiness with { MicrosoftGraph = CheckState.Passed };

        var identityMatches = organization.Value!.TenantId == tenantId && (token.AuthenticatedTenantId is null || token.AuthenticatedTenantId == tenantId);
        if (!identityMatches)
            return Report(TenantConnectionState.GraphConnectionFailed, readiness with { TenantIdentity = CheckState.Failed, Permissions = permissionCheck },
                new OperationError("TenantIdentity", "The authenticated tenant does not match the configured Tenant ID.", null, "Check the Tenant ID and the app registration's home tenant."),
                validated, organization.Value);
        readiness = readiness with { TenantIdentity = CheckState.Passed, Permissions = permissionCheck };

        return permissionCheck == CheckState.Failed
            ? Report(TenantConnectionState.PermissionValidationFailed, readiness,
                new OperationError("Permissions", "A required application permission is missing.", null, "Grant the listed permissions and admin consent."), validated, organization.Value)
            : Report(TenantConnectionState.Connected, readiness, null, validated, organization.Value);
    }

    private async Task AuditAsync(string customerId, TenantConfiguration tenant, TenantConnectionReport report, CancellationToken cancellationToken)
    {
        var properties = new Dictionary<string, string?>
        {
            ["CustomerId"] = customerId,
            ["TenantKey"] = tenant.TenantKey,
            ["TenantId"] = tenant.TenantId?.ToString(),
            ["ClientId"] = tenant.ApplicationRegistration?.ClientId.ToString(),
            ["Cloud"] = tenant.CloudEnvironment.ToString(),
            ["State"] = report.State.ToString(),
            ["ErrorCategory"] = report.Error?.Category
        };
        var success = report.State == TenantConnectionState.Connected;
        await audit.RecordAsync(AuditEventTypes.TenantConnectionTested, success ? AuditOutcome.Success : AuditOutcome.Failure,
            $"Tenant connection test: {report.State}.", properties, cancellationToken);
        if (report.Readiness.Authentication == CheckState.Passed)
            await audit.RecordAsync(AuditEventTypes.TenantAuthenticationSucceeded, AuditOutcome.Success, "Tenant authentication succeeded.", properties, cancellationToken);
        else if (report.Readiness.Authentication == CheckState.Failed)
            await audit.RecordAsync(AuditEventTypes.TenantAuthenticationFailed, AuditOutcome.Failure, "Tenant authentication failed.", properties, cancellationToken);
        if (report.Readiness.Permissions == CheckState.Passed)
            await audit.RecordAsync(AuditEventTypes.TenantPermissionValidationSucceeded, AuditOutcome.Success, "Tenant permission validation succeeded.", properties, cancellationToken);
        else if (report.Readiness.Permissions == CheckState.Failed)
            await audit.RecordAsync(AuditEventTypes.TenantPermissionValidationFailed, AuditOutcome.Failure, "Tenant permission validation failed.", properties, cancellationToken);
    }

    /// <summary>The permission check fills audit.TenantConnection (consent status and last verification) when the database is ready.</summary>
    private async Task RecordConsentAsync(string customerId, TenantConfiguration tenant, TenantConnectionReport report, CancellationToken cancellationToken)
    {
        if (profiles is not { IsAvailable: true } || report.Readiness.Permissions is not (CheckState.Passed or CheckState.Failed)) return;
        var consent = report.Readiness.Permissions == CheckState.Passed ? "Granted"
            : report.Permissions.Any(row => row.Validated == CheckState.Passed) ? "Partial" : "NotGranted";
        try
        {
            await profiles.RecordTenantVerificationAsync(customerId, tenant.TenantKey, consent, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
        {
            logger.LogWarning(exception, "The consent status of {TenantKey} could not be recorded", tenant.TenantKey);
        }
    }

    private static string Key(string customerId, string tenantKey) => $"{customerId}/{tenantKey}";
}

/// <summary>Stores a tenant client secret immediately in the secret store and returns only its reference.</summary>
public sealed class TenantCredentialService(ICredentialService credentials) : ITenantCredentialService
{
    public async Task<OperationResult<SecretReference>> StoreClientSecretAsync(
        string customerId,
        TenantConfiguration tenant,
        string clientSecret,
        DateTimeOffset? expiresOnUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(clientSecret))
            return OperationResult<SecretReference>.Failure("SecretRequired", "Enter the client secret.");
        var reference = new SecretReference(SecretReference.OperatingSystemProvider, $"customer/{customerId}/{tenant.TenantKey}/graph-client-secret");
        var existing = await credentials.FindByReferenceAsync(reference, cancellationToken);
        var result = existing is null
            ? await credentials.AddAsync(new NewCredential($"{tenant.DisplayName} Graph client secret", CredentialType.EntraClientSecret, reference,
                expiresOnUtc, customerId, tenant.TenantId?.ToString()), clientSecret, cancellationToken)
            : await credentials.ReplaceAsync(existing.Metadata.CredentialId, clientSecret, expiresOnUtc, cancellationToken);
        return result.Succeeded ? OperationResult<SecretReference>.Success(reference) : OperationResult<SecretReference>.Failure(result.Error!);
    }

    public async Task<OperationResult<SecretReference>> StoreCertificateAsync(
        string customerId,
        TenantConfiguration tenant,
        string purpose,
        string storedValue,
        DateTimeOffset? expiresOnUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(storedValue))
            return OperationResult<SecretReference>.Failure("SecretRequired", "No certificate was produced.");
        var label = purpose == "deploy" ? "Deploy" : "Read";
        var reference = new SecretReference(SecretReference.OperatingSystemProvider, $"customer/{customerId}/{tenant.TenantKey}/{purpose}-certificate");
        var existing = await credentials.FindByReferenceAsync(reference, cancellationToken);
        var result = existing is null
            ? await credentials.AddAsync(new NewCredential($"{tenant.DisplayName} {label} registration certificate", CredentialType.Certificate, reference,
                expiresOnUtc, customerId, tenant.TenantId?.ToString()), storedValue, cancellationToken)
            : await credentials.ReplaceAsync(existing.Metadata.CredentialId, storedValue, expiresOnUtc, cancellationToken);
        return result.Succeeded ? OperationResult<SecretReference>.Success(reference) : OperationResult<SecretReference>.Failure(result.Error!);
    }

    public async Task<OperationResult> DeleteCredentialAsync(SecretReference reference, CancellationToken cancellationToken = default)
    {
        var existing = await credentials.FindByReferenceAsync(reference, cancellationToken);
        if (existing is null) return OperationResult.Success();
        var result = await credentials.DeleteAsync(existing.Metadata.CredentialId, cancellationToken);
        return result.Succeeded ? OperationResult.Success() : OperationResult.Failure(result.Error!);
    }
}
