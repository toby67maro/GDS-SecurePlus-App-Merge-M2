using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Domain.Auditing;
using EngineeringWorkspace.Domain.Security;
using EngineeringWorkspace.Domain.Tenants;

namespace EngineeringWorkspace.Application.Tenants;

/// <summary>A signed-in administrator session. Opaque handle; the token stays inside Infrastructure.</summary>
public sealed record AdministratorSession(string SessionId, Guid TenantId, string Account, CloudEnvironment Cloud, IReadOnlyList<string> GrantedScopes);

public sealed record CreatedApplication(Guid ObjectId, Guid ClientId, string DisplayName);

public sealed record CreatedClientSecret(Guid KeyId, DateTimeOffset? EndDateTimeUtc, SecretReference StoredAs);

/// <summary>A certificate credential: the public key is on the app registration; the private key is only in the secret store.</summary>
public sealed record CreatedCertificate(string Thumbprint, DateTimeOffset EndDateTimeUtc, SecretReference StoredAs, int RemovedClientSecrets);

/// <summary>First-party resources and directory roles used by the Read registration (4.0 beta.2).</summary>
public static class TenantResources
{
    /// <summary>Office 365 Exchange Online (Exchange Online PowerShell, app-only).</summary>
    public const string ExchangeOnlineAppId = "00000002-0000-0ff1-ce00-000000000000";

    /// <summary>Microsoft Exchange Online Protection (Security &amp; Compliance PowerShell, app-only).</summary>
    public const string ExchangeOnlineProtectionAppId = "00000007-0000-0ff1-ce00-000000000000";

    public const string ManageAsApp = "Exchange.ManageAsApp";

    /// <summary>Global Reader (role template ID): read-only access for Exchange Online, Security &amp; Compliance and Teams PowerShell.</summary>
    public const string GlobalReaderRoleId = "f2ef992c-3afb-46b9-b7cf-a126ee74c451";
}

/// <summary>
/// Microsoft Graph administrative operations for app registration (Infrastructure). Each call performs exactly one
/// documented operation; callers must state it and obtain confirmation first.
/// </summary>
public interface IAppRegistrationService
{
    /// <summary>Null when an administrator sign-in can be attempted; otherwise why it cannot.</summary>
    string? UnavailableReason { get; }

    /// <summary>Delegated scopes requested for the administrator sign-in.</summary>
    IReadOnlyList<string> AdministratorScopes { get; }

    /// <summary>Interactive administrator sign-in. With no tenant ID the tenant is taken from the signed-in account.</summary>
    Task<OperationResult<AdministratorSession>> ConnectAdministratorAsync(Guid? tenantId, CloudEnvironment cloud, CancellationToken cancellationToken);

    /// <summary>The signed-in tenant's organization (ID, name, verified and default domains).</summary>
    Task<OperationResult<TenantOrganization>> GetOrganizationAsync(AdministratorSession session, CancellationToken cancellationToken);

    /// <summary>Finds an existing app registration by client ID, or (when <paramref name="clientId"/> is null) by display name.</summary>
    Task<OperationResult<CreatedApplication?>> FindApplicationAsync(AdministratorSession session, Guid? clientId, string displayName, CancellationToken cancellationToken);
    Task<OperationResult<CreatedApplication>> CreateApplicationAsync(AdministratorSession session, string displayName, CancellationToken cancellationToken);
    Task<OperationResult<Guid>> EnsureServicePrincipalAsync(AdministratorSession session, Guid clientId, CancellationToken cancellationToken);
    /// <summary>Adds the permissions to the application's Microsoft Graph requiredResourceAccess, keeping any it already has.</summary>
    Task<OperationResult> ConfigureGraphApplicationPermissionsAsync(AdministratorSession session, Guid applicationObjectId, IReadOnlyList<string> permissions, CancellationToken cancellationToken);
    Task<OperationResult<IReadOnlyList<string>>> GrantAdminConsentAsync(AdministratorSession session, Guid servicePrincipalId, IReadOnlyList<string> permissions, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a client secret. Microsoft Graph returns its value once; it is handed straight to
    /// <paramref name="protect"/> (which stores it in the secret store) and is never logged or kept.
    /// </summary>
    Task<OperationResult<CreatedClientSecret>> CreateClientSecretAsync(
        AdministratorSession session,
        Guid applicationObjectId,
        string displayName,
        TimeSpan lifetime,
        Func<string, DateTimeOffset?, CancellationToken, Task<OperationResult<SecretReference>>> protect,
        CancellationToken cancellationToken);

    Task SignOutAsync(AdministratorSession session);

    // ---- 4.0 beta.2: Read/Deploy registrations, other resources, roles and certificates ----

    /// <summary>Adds application permissions of another resource (for example Exchange.ManageAsApp) to requiredResourceAccess. Null when the
    /// resource's service principal does not exist in the tenant (the step is then skipped).</summary>
    Task<OperationResult<bool>> ConfigureResourcePermissionsAsync(AdministratorSession session, Guid applicationObjectId, string resourceAppId, IReadOnlyList<string> permissions,
        CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult<bool>.Failure("NotSupported", "Not supported by this registration service."));

    /// <summary>Grants admin consent (app role assignments) for another resource's application permissions.</summary>
    Task<OperationResult<IReadOnlyList<string>>> GrantResourceConsentAsync(AdministratorSession session, Guid servicePrincipalId, string resourceAppId, IReadOnlyList<string> permissions,
        CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult<IReadOnlyList<string>>.Failure("NotSupported", "Not supported by this registration service."));

    /// <summary>Removes Microsoft Graph application permissions (and their consent) the registration should no longer have.</summary>
    Task<OperationResult<IReadOnlyList<string>>> RevokeGraphPermissionsAsync(AdministratorSession session, Guid applicationObjectId, Guid servicePrincipalId,
        IReadOnlyList<string> keep, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult<IReadOnlyList<string>>.Success([]));

    /// <summary>Assigns a built-in directory role (by template ID) to the service principal at tenant scope, if not already assigned.</summary>
    Task<OperationResult<bool>> AssignDirectoryRoleAsync(AdministratorSession session, Guid servicePrincipalId, string roleTemplateId, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult<bool>.Failure("NotSupported", "Not supported by this registration service."));

    /// <summary>
    /// Creates a self-signed certificate, puts its public key on the application (replacing older certificates), hands the private key
    /// straight to <paramref name="protect"/> (the secret store) and then removes the application's client secrets.
    /// </summary>
    Task<OperationResult<CreatedCertificate>> CreateCertificateCredentialAsync(AdministratorSession session, Guid applicationObjectId, string subjectName, TimeSpan lifetime,
        Func<string, DateTimeOffset?, CancellationToken, Task<OperationResult<SecretReference>>> protect, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult<CreatedCertificate>.Failure("NotSupported", "Not supported by this registration service."));

    Task<OperationResult> RenameApplicationAsync(AdministratorSession session, Guid applicationObjectId, string displayName, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Success());

    /// <summary>Deletes an app registration (the Deploy registration when a deploy phase ends).</summary>
    Task<OperationResult> DeleteApplicationAsync(AdministratorSession session, Guid applicationObjectId, CancellationToken cancellationToken) =>
        Task.FromResult(OperationResult.Failure("NotSupported", "Not supported by this registration service."));
}

public enum AssistantStepId
{
    ConnectAdministrator,
    ValidateAdministratorContext,
    CreateAppRegistration,
    CreateOrLocateServicePrincipal,
    ConfigurePermissions,
    GrantAdminConsent,
    CreateCredential,
    StoreCredential,
    AssociateWithCustomer,
    TestAuthentication,
    ValidatePermissions
}

public enum AssistantStepState
{
    Pending,
    Completed,
    Failed,
    Skipped
}

/// <summary>One assistant step: what will happen, whether it needs a real administrator session, and its result.</summary>
public sealed record AssistantStep(AssistantStepId Id, string Title, string WhatWillHappen, bool RequiresAdministratorSession, AssistantStepState State, string? Result);

/// <summary>
/// Create / Configure App Registration assistant. Runs one step at a time; never grants permissions or creates
/// secrets silently and never reports success that the Graph API did not return. Create one per run.
/// </summary>
public sealed class AppRegistrationAssistant(
    IAppRegistrationService registrations,
    IPermissionValidationService permissions,
    ITenantCredentialService tenantCredentials,
    IAuditService audit)
{
    private readonly Dictionary<AssistantStepId, AssistantStep> _steps = new();
    private AdministratorSession? _session;
    private CreatedApplication? _application;
    private Guid? _servicePrincipalId;
    private CreatedClientSecret? _secret;

    public string? UnavailableReason => registrations.UnavailableReason;
    public AdministratorSession? Session => _session;
    public CreatedApplication? Application => _application;
    public Guid? ServicePrincipalId => _servicePrincipalId;
    public CreatedClientSecret? ClientSecret => _secret;

    public IReadOnlyList<AssistantStep> Describe(string customerId, TenantConfiguration tenant, string applicationName)
    {
        var required = permissions.RequiredApplicationPermissions(Capabilities(tenant));
        var permissionText = required.Count == 0 ? "(none defined)" : string.Join(", ", required);
        AssistantStep Step(AssistantStepId id, string title, string what, bool admin) =>
            _steps.TryGetValue(id, out var existing)
                ? new AssistantStep(id, title, what, admin, existing.State, existing.Result)
                : new AssistantStep(id, title, what, admin, AssistantStepState.Pending, null);
        return
        [
            Step(AssistantStepId.ConnectAdministrator, "Connect as administrator",
                $"Open a Microsoft sign-in window for tenant {tenant.TenantId} ({tenant.CloudEnvironment}) and request the delegated scopes {string.Join(", ", registrations.AdministratorScopes)}.", true),
            Step(AssistantStepId.ValidateAdministratorContext, "Validate administrator context",
                "Confirm the signed-in tenant is the configured tenant and that the granted scopes allow creating applications and granting app roles.", true),
            Step(AssistantStepId.CreateAppRegistration, "Create the app registration",
                $"POST /applications to create the single-tenant application '{applicationName}'.", true),
            Step(AssistantStepId.CreateOrLocateServicePrincipal, "Create or locate the service principal",
                "Look up the service principal for the new application's client ID and create it with POST /servicePrincipals if it does not exist.", true),
            Step(AssistantStepId.ConfigurePermissions, "Configure Graph application permissions",
                $"PATCH /applications/{{id}} requiredResourceAccess for Microsoft Graph application permissions: {permissionText}.", true),
            Step(AssistantStepId.GrantAdminConsent, "Grant admin consent",
                $"POST /servicePrincipals/{{Microsoft Graph}}/appRoleAssignedTo to grant tenant-wide admin consent for: {permissionText}. Requires a Privileged Role Administrator or Global Administrator.", true),
            Step(AssistantStepId.CreateCredential, "Create a client secret",
                "POST /applications/{id}/addPassword to create a client secret valid for the selected lifetime. Its value is returned once.", true),
            Step(AssistantStepId.StoreCredential, "Store the credential",
                $"Protect the returned secret immediately with the secret store as os:customer/{customerId}/{tenant.TenantKey}/graph-client-secret. It is never shown, logged or saved elsewhere.", false),
            Step(AssistantStepId.AssociateWithCustomer, "Associate with the customer",
                $"Save the client ID, service principal ID and secret reference to customer '{customerId}' tenant '{tenant.TenantKey}'.", false),
            Step(AssistantStepId.TestAuthentication, "Test authentication",
                "Acquire an app-only token with the new credential. New secrets and consent can take a few minutes to propagate.", false),
            Step(AssistantStepId.ValidatePermissions, "Validate permissions",
                "Call GET /organization and compare the token's granted roles with the required permissions.", false)
        ];
    }

    public async Task<OperationResult> ConnectAsync(TenantConfiguration tenant, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId) return Fail(AssistantStepId.ConnectAdministrator, "Configuration", "Enter the Tenant ID first.");
        var result = await registrations.ConnectAdministratorAsync(tenantId, tenant.CloudEnvironment, cancellationToken);
        if (!result.Succeeded) return Fail(AssistantStepId.ConnectAdministrator, result.Error!);
        _session = result.Value;
        return Complete(AssistantStepId.ConnectAdministrator, $"Signed in as {_session!.Account}.");
    }

    public OperationResult ValidateAdministratorContext(TenantConfiguration tenant)
    {
        if (_session is null) return Fail(AssistantStepId.ValidateAdministratorContext, "Session", "Connect as an administrator first.");
        if (_session.TenantId != tenant.TenantId)
            return Fail(AssistantStepId.ValidateAdministratorContext, "TenantIdentity", $"The administrator signed in to tenant {_session.TenantId}, not {tenant.TenantId}.");
        var scopes = new HashSet<string>(_session.GrantedScopes, StringComparer.OrdinalIgnoreCase);
        var missing = registrations.AdministratorScopes.Where(scope => !scopes.Contains(scope)).ToArray();
        return missing.Length > 0
            ? Fail(AssistantStepId.ValidateAdministratorContext, "Consent", $"The sign-in did not grant: {string.Join(", ", missing)}.")
            : Complete(AssistantStepId.ValidateAdministratorContext, "Tenant matches and the required delegated scopes were granted.");
    }

    public async Task<OperationResult> CreateApplicationAsync(string customerId, TenantConfiguration tenant, string displayName, CancellationToken cancellationToken)
    {
        if (_session is null) return Fail(AssistantStepId.CreateAppRegistration, "Session", "Connect as an administrator first.");
        var result = await registrations.CreateApplicationAsync(_session, displayName, cancellationToken);
        if (!result.Succeeded) return Fail(AssistantStepId.CreateAppRegistration, result.Error!);
        _application = result.Value;
        await audit.RecordAsync(AuditEventTypes.TenantAppRegistrationCreated, AuditOutcome.Success, $"App registration '{displayName}' created.",
            new Dictionary<string, string?> { ["CustomerId"] = customerId, ["TenantKey"] = tenant.TenantKey, ["TenantId"] = tenant.TenantId?.ToString(), ["ClientId"] = _application!.ClientId.ToString() },
            cancellationToken);
        return Complete(AssistantStepId.CreateAppRegistration, $"Created client ID {_application.ClientId}.");
    }

    public async Task<OperationResult> EnsureServicePrincipalAsync(CancellationToken cancellationToken)
    {
        if (_session is null || _application is null) return Fail(AssistantStepId.CreateOrLocateServicePrincipal, "Order", "Create the app registration first.");
        var result = await registrations.EnsureServicePrincipalAsync(_session, _application.ClientId, cancellationToken);
        if (!result.Succeeded) return Fail(AssistantStepId.CreateOrLocateServicePrincipal, result.Error!);
        _servicePrincipalId = result.Value;
        return Complete(AssistantStepId.CreateOrLocateServicePrincipal, $"Service principal {_servicePrincipalId}.");
    }

    public async Task<OperationResult> ConfigurePermissionsAsync(TenantConfiguration tenant, CancellationToken cancellationToken)
    {
        if (_session is null || _application is null) return Fail(AssistantStepId.ConfigurePermissions, "Order", "Create the app registration first.");
        var required = permissions.RequiredApplicationPermissions(Capabilities(tenant));
        var result = await registrations.ConfigureGraphApplicationPermissionsAsync(_session, _application.ObjectId, required, cancellationToken);
        return result.Succeeded ? Complete(AssistantStepId.ConfigurePermissions, $"Configured: {string.Join(", ", required)}.") : Fail(AssistantStepId.ConfigurePermissions, result.Error!);
    }

    public async Task<OperationResult> GrantAdminConsentAsync(TenantConfiguration tenant, CancellationToken cancellationToken)
    {
        if (_session is null || _servicePrincipalId is null) return Fail(AssistantStepId.GrantAdminConsent, "Order", "Create the service principal first.");
        var required = permissions.RequiredApplicationPermissions(Capabilities(tenant));
        var result = await registrations.GrantAdminConsentAsync(_session, _servicePrincipalId.Value, required, cancellationToken);
        return result.Succeeded
            ? Complete(AssistantStepId.GrantAdminConsent, $"Granted: {string.Join(", ", result.Value!)}.")
            : Fail(AssistantStepId.GrantAdminConsent, result.Error!);
    }

    /// <summary>Creates the client secret and stores it immediately (completes both CreateCredential and StoreCredential).</summary>
    public async Task<OperationResult> CreateAndStoreClientSecretAsync(string customerId, TenantConfiguration tenant, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        if (_session is null || _application is null) return Fail(AssistantStepId.CreateCredential, "Order", "Create the app registration first.");
        var target = tenant with
        {
            ApplicationRegistration = new ApplicationRegistration(_application.ClientId, _servicePrincipalId, TenantAuthenticationType.ClientSecret, null, null, null,
                tenant.ApplicationRegistration?.RequiredCapabilities ?? [TenantCapability.TenantDiscovery])
        };
        var result = await registrations.CreateClientSecretAsync(_session, _application.ObjectId, "Secure Plus Agent", lifetime,
            (secret, expires, token) => tenantCredentials.StoreClientSecretAsync(customerId, target, secret, expires, token), cancellationToken);
        if (!result.Succeeded)
        {
            Fail(AssistantStepId.StoreCredential, result.Error!);
            return Fail(AssistantStepId.CreateCredential, result.Error!);
        }

        _secret = result.Value;
        Complete(AssistantStepId.CreateCredential, $"Client secret created (key {_secret!.KeyId}, expires {_secret.EndDateTimeUtc:yyyy-MM-dd}).");
        return Complete(AssistantStepId.StoreCredential, $"Stored as {_secret.StoredAs}.");
    }

    /// <summary>Returns the tenant with the new registration so the caller can save it to the customer profile.</summary>
    public OperationResult<TenantConfiguration> BuildAssociation(TenantConfiguration tenant)
    {
        if (_application is null || _secret is null)
        {
            Fail(AssistantStepId.AssociateWithCustomer, "Order", "Create the app registration and credential first.");
            return OperationResult<TenantConfiguration>.Failure("Order", "Create the app registration and credential first.");
        }

        var updated = tenant with
        {
            ApplicationRegistration = new ApplicationRegistration(_application.ClientId, _servicePrincipalId, TenantAuthenticationType.ClientSecret,
                _secret.StoredAs, null, _secret.EndDateTimeUtc, tenant.ApplicationRegistration?.RequiredCapabilities ?? [TenantCapability.TenantDiscovery])
        };
        Complete(AssistantStepId.AssociateWithCustomer, "Registration saved to the customer profile.");
        return OperationResult<TenantConfiguration>.Success(updated);
    }

    public void RecordConnectionTest(TenantConnectionReport report)
    {
        if (report.Readiness.Authentication == CheckState.Passed) Complete(AssistantStepId.TestAuthentication, "Authentication succeeded.");
        else Fail(AssistantStepId.TestAuthentication, report.Error ?? new OperationError("Authentication", "Authentication did not succeed."));
        if (report.Readiness.Permissions == CheckState.Passed) Complete(AssistantStepId.ValidatePermissions, "Required permissions are granted.");
        else Fail(AssistantStepId.ValidatePermissions, report.Error ?? new OperationError("Permissions", "Permission validation did not succeed."));
    }

    public async Task SignOutAsync()
    {
        if (_session is not null) await registrations.SignOutAsync(_session);
        _session = null;
    }

    private static IReadOnlyCollection<TenantCapability> Capabilities(TenantConfiguration tenant) =>
        tenant.ApplicationRegistration?.RequiredCapabilities is { Count: > 0 } capabilities ? capabilities : [TenantCapability.TenantDiscovery];

    private OperationResult Complete(AssistantStepId id, string result)
    {
        _steps[id] = Base(id) with { State = AssistantStepState.Completed, Result = result };
        return OperationResult.Success();
    }

    private OperationResult Fail(AssistantStepId id, string category, string message) => Fail(id, new OperationError(category, message));

    private OperationResult Fail(AssistantStepId id, OperationError error)
    {
        _steps[id] = Base(id) with { State = AssistantStepState.Failed, Result = error.ToString() };
        return OperationResult.Failure(error);
    }

    private AssistantStep Base(AssistantStepId id) =>
        _steps.TryGetValue(id, out var step) ? step : new AssistantStep(id, id.ToString(), string.Empty, false, AssistantStepState.Pending, null);
}
