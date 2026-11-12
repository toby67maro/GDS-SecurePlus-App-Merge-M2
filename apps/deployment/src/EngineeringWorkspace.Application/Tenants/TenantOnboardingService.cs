using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Jobs;
using EngineeringWorkspace.Domain.Auditing;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Tenants;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Application.Tenants;

/// <summary>
/// Fully automatic tenant onboarding (4.0 beta.2: separate Read and Deploy registrations, certificate credentials).
/// <para>
/// <b>Read registration</b> ("GDS Secure Plus Read"; the older "GDS Secure Plus Agent" app is renamed): read-only Microsoft Graph
/// permissions, Exchange.ManageAsApp and the Global Reader role for the read-only PowerShell collector, and a certificate whose private
/// key is kept only in the encrypted secret store. Write permissions are removed from it.
/// </para>
/// <para>
/// <b>Deploy registration</b> ("GDS Secure Plus Deploy"): the write permissions that Change Execution uses, nothing else. It is created
/// when a deploy phase starts (Enable deployment access) and deleted when it ends (Remove deployment access).
/// </para>
/// An administrator signs in once per job; every step is visible in the activity panel.
/// </summary>
public interface ITenantOnboardingService
{
    public const string ApplicationDisplayName = "GDS Secure Plus Read";
    public const string LegacyApplicationDisplayName = "GDS Secure Plus Agent";
    public const string DeployApplicationDisplayName = "GDS Secure Plus Deploy";

    /// <summary>
    /// Queues the full registration for a customer. With no tenant ID the tenant of the signed-in administrator is used. With
    /// <paramref name="startQuickScan"/> a quick scan (users, groups, roles, licences, posture) starts once the connection test passes (4.0 beta.2).
    /// </summary>
    JobSnapshot RegisterAutomatically(CustomerId customerId, Guid? tenantId, CloudEnvironment cloud, bool startQuickScan = false);

    /// <summary>
    /// Queues bringing the customer's Read registration up to date: missing read permissions, Exchange and Purview access, the Global Reader
    /// role, a certificate in place of a client secret, and removal of write permissions an older registration still holds.
    /// </summary>
    JobSnapshot UpdatePermissions(CustomerId customerId);

    /// <summary>Queues creating the Deploy registration (write permissions, certificate) for a deploy phase.</summary>
    JobSnapshot EnableDeployment(CustomerId customerId) => throw new NotSupportedException();

    /// <summary>Queues deleting the Deploy registration and its stored certificate when the deploy phase ends.</summary>
    JobSnapshot RemoveDeployment(CustomerId customerId) => throw new NotSupportedException();
}

public sealed class TenantOnboardingService(
    IJobService jobs,
    IAppRegistrationService registrations,
    IPermissionValidationService permissions,
    ITenantCredentialService tenantCredentials,
    ITenantRegistrationService tenants,
    ICustomerProfileStore profiles,
    ITenantConnectionService connections,
    IAuditService audit,
    ILogger<TenantOnboardingService> logger,
    Assessments.IAssessmentService? assessments = null) : ITenantOnboardingService
{
    /// <summary>Certificates are renewed with Update permissions; the expiry shows on Credential Management and the tenant card.</summary>
    public static readonly TimeSpan CertificateLifetime = TimeSpan.FromDays(365);

    private static readonly string[] RegistrationSteps =
    [
        "Sign in as administrator", "Read tenant", "Find or create the Read registration", "Create the service principal",
        "Configure read-only Microsoft Graph permissions", "Grant admin consent", "Remove write permissions", "Exchange Online and Purview access",
        "Assign the Global Reader role", "Create and store the certificate", "Save to the customer profile", "Test the connection"
    ];

    private static readonly string[] DeploySteps =
    [
        "Sign in as administrator", "Read tenant", "Find or create the Deploy registration", "Create the service principal",
        "Configure write permissions", "Grant admin consent", "Create and store the certificate", "Save to the customer profile", "Test the connection"
    ];

    private static readonly string[] RemoveSteps = ["Sign in as administrator", "Delete the Deploy registration", "Delete the stored certificate", "Save to the customer profile"];

    public JobSnapshot RegisterAutomatically(CustomerId customerId, Guid? tenantId, CloudEnvironment cloud, bool startQuickScan = false) =>
        jobs.Enqueue("Register the Secure Plus app", "Tenant registration", customerId.Value, RegistrationSteps, async context =>
        {
            var result = await RegisterAsync(context, customerId, tenantId, cloud) ?? string.Empty;
            if (startQuickScan && assessments is not null && result.Contains(" is connected ", StringComparison.Ordinal))
            {
                assessments.QueueScan(customerId, Assessments.ScanTypes.Quick, "Quick scan after connecting the tenant");
                result += " A quick scan of users, groups, roles and licences has started.";
            }

            return result;
        });

    public JobSnapshot UpdatePermissions(CustomerId customerId) =>
        jobs.Enqueue("Update app permissions", "Tenant registration", customerId.Value, RegistrationSteps, async context =>
        {
            var profile = await profiles.LoadAsync(customerId, context.CancellationToken) ?? throw new InvalidOperationException($"Customer '{customerId}' does not exist.");
            var tenant = profile.PrimaryTenant;
            return await RegisterAsync(context, customerId, tenant?.TenantId, tenant?.CloudEnvironment ?? CloudEnvironment.Commercial);
        });

    public JobSnapshot EnableDeployment(CustomerId customerId) =>
        jobs.Enqueue("Enable deployment access", "Tenant registration", customerId.Value, DeploySteps, context => EnableDeploymentAsync(context, customerId));

    public JobSnapshot RemoveDeployment(CustomerId customerId) =>
        jobs.Enqueue("Remove deployment access", "Tenant registration", customerId.Value, RemoveSteps, context => RemoveDeploymentAsync(context, customerId));

    private async Task<string?> RegisterAsync(IJobContext context, CustomerId customerId, Guid? tenantId, CloudEnvironment cloud)
    {
        var token = context.CancellationToken;
        var profile = await profiles.LoadAsync(customerId, token) ?? throw new InvalidOperationException($"Customer '{customerId}' does not exist.");
        var existingTenant = profile.PrimaryTenant;

        context.BeginStep("Sign in as administrator", "A Microsoft sign-in window opens in your browser. Sign in as a Global Administrator (or Privileged Role Administrator and Application Administrator).");
        var session = Require(await registrations.ConnectAdministratorAsync(tenantId ?? existingTenant?.TenantId, cloud, token));
        try
        {
            context.CompleteStep($"Signed in as {session.Account}.");

            context.BeginStep("Read tenant");
            var organization = Require(await registrations.GetOrganizationAsync(session, token));
            if (tenantId is { } expected && expected != organization.TenantId)
                throw new InvalidOperationException($"The administrator signed in to tenant {organization.TenantId}, not {expected}.");
            context.CompleteStep($"{organization.DisplayName} ({organization.DefaultDomain ?? organization.TenantId.ToString()}).");

            context.BeginStep("Find or create the Read registration");
            var sameTenant = existingTenant?.TenantId == organization.TenantId;
            var knownClientId = sameTenant ? existingTenant?.ApplicationRegistration?.ClientId : null;
            var application = knownClientId is { } clientId && clientId != Guid.Empty
                ? Require(await registrations.FindApplicationAsync(session, clientId, ITenantOnboardingService.ApplicationDisplayName, token))
                : null;
            application ??= Require(await registrations.FindApplicationAsync(session, null, ITenantOnboardingService.ApplicationDisplayName, token));
            application ??= Require(await registrations.FindApplicationAsync(session, null, ITenantOnboardingService.LegacyApplicationDisplayName, token));
            var created = false;
            if (application is null)
            {
                application = Require(await registrations.CreateApplicationAsync(session, ITenantOnboardingService.ApplicationDisplayName, token));
                created = true;
            }
            else if (!string.Equals(application.DisplayName, ITenantOnboardingService.ApplicationDisplayName, StringComparison.Ordinal))
            {
                Require(await registrations.RenameApplicationAsync(session, application.ObjectId, ITenantOnboardingService.ApplicationDisplayName, token));
                application = application with { DisplayName = ITenantOnboardingService.ApplicationDisplayName };
            }

            context.CompleteStep(created ? $"Created '{application.DisplayName}' (client ID {application.ClientId})." : $"Using '{application.DisplayName}' (client ID {application.ClientId}).");

            context.BeginStep("Create the service principal");
            var servicePrincipalId = Require(await registrations.EnsureServicePrincipalAsync(session, application.ClientId, token));
            context.CompleteStep($"Service principal {servicePrincipalId}.");

            var required = permissions.RequiredApplicationPermissions(PermissionValidationService.ReadCapabilities);
            context.BeginStep("Configure read-only Microsoft Graph permissions", string.Join(", ", required));
            Require(await registrations.ConfigureGraphApplicationPermissionsAsync(session, application.ObjectId, required, token));
            context.CompleteStep($"{required.Count} read-only application permissions configured.");

            context.BeginStep("Grant admin consent");
            var granted = Require(await registrations.GrantAdminConsentAsync(session, servicePrincipalId, required, token));
            context.CompleteStep($"Consent granted for {granted.Count} permissions.");

            context.BeginStep("Remove write permissions", "Write permissions belong to the Deploy registration, which exists only during deploy phases.");
            var removed = Require(await registrations.RevokeGraphPermissionsAsync(session, application.ObjectId, servicePrincipalId, required, token));
            if (removed.Count == 0) context.CompleteStep("The Read registration holds no write permissions.");
            else context.CompleteStep($"Removed {removed.Count}: {string.Join(", ", removed)}.");

            context.BeginStep("Exchange Online and Purview access", "Exchange.ManageAsApp lets the read-only PowerShell collector sign in; the Global Reader role limits it to reading.");
            var exchangeNotes = new List<string>();
            foreach (var (resource, name) in new[] { (TenantResources.ExchangeOnlineAppId, "Exchange Online"), (TenantResources.ExchangeOnlineProtectionAppId, "Security & Compliance") })
            {
                var configured = Optional(await registrations.ConfigureResourcePermissionsAsync(session, application.ObjectId, resource, [TenantResources.ManageAsApp], token), out var error);
                if (configured == true && Optional(await registrations.GrantResourceConsentAsync(session, servicePrincipalId, resource, [TenantResources.ManageAsApp], token), out error) is not null)
                    exchangeNotes.Add($"{name}: Exchange.ManageAsApp granted");
                else exchangeNotes.Add($"{name}: {(error ?? "not available in this tenant")}");
            }

            context.CompleteStep(string.Join("; ", exchangeNotes) + ".");

            context.BeginStep("Assign the Global Reader role");
            var role = Optional(await registrations.AssignDirectoryRoleAsync(session, servicePrincipalId, TenantResources.GlobalReaderRoleId, token), out var roleError);
            if (role is null) context.FailStep($"{roleError} Exchange, Purview and Teams settings will be reported as not collected until the role is assigned.");
            else context.CompleteStep(role.Value ? "Global Reader assigned (read-only)." : "Global Reader was already assigned.");

            var tenant = (sameTenant ? existingTenant : null) ?? new TenantConfiguration(
                "primary", organization.DisplayName ?? profile.Name, organization.DisplayName, organization.TenantId, organization.DefaultDomain, cloud, null);
            tenant = tenant with
            {
                TenantId = organization.TenantId,
                TenantName = tenant.TenantName ?? organization.DisplayName,
                PrimaryDomain = tenant.PrimaryDomain ?? organization.DefaultDomain,
                CloudEnvironment = cloud
            };
            var previous = tenant.ApplicationRegistration?.ClientId == application.ClientId ? tenant.ApplicationRegistration : null;
            var registration = new ApplicationRegistration(application.ClientId, servicePrincipalId, previous?.AuthenticationType ?? TenantAuthenticationType.Certificate,
                previous?.SecretReference, previous?.CertificateReference, previous?.CredentialExpirationUtc, PermissionValidationService.ReadCapabilities);

            var renew = created || registration.SecretReference is null || registration.AuthenticationType != TenantAuthenticationType.Certificate ||
                        registration.CredentialExpirationUtc is { } expires && expires <= DateTimeOffset.UtcNow.AddDays(30);
            if (renew)
            {
                context.BeginStep("Create and store the certificate", "The private key is created on this computer and kept only in the encrypted secret store.");
                var target = tenant with { ApplicationRegistration = registration };
                var certificate = Require(await registrations.CreateCertificateCredentialAsync(session, application.ObjectId,
                    $"{ITenantOnboardingService.ApplicationDisplayName} - {organization.DefaultDomain ?? organization.TenantId.ToString()}", CertificateLifetime,
                    (value, expiresOn, cancellation) => tenantCredentials.StoreCertificateAsync(customerId.Value, target, "graph", value, expiresOn, cancellation), token));
                if (previous?.SecretReference is { } oldReference && oldReference != certificate.StoredAs)
                    await tenantCredentials.DeleteCredentialAsync(oldReference, token);
                registration = registration with
                {
                    AuthenticationType = TenantAuthenticationType.Certificate,
                    SecretReference = certificate.StoredAs,
                    CertificateReference = certificate.Thumbprint,
                    CredentialExpirationUtc = certificate.EndDateTimeUtc
                };
                context.CompleteStep($"Certificate {certificate.Thumbprint[..8]}… expires {certificate.EndDateTimeUtc:yyyy-MM-dd}" +
                                     (certificate.RemovedClientSecrets > 0 ? $"; {certificate.RemovedClientSecrets} client secret(s) removed." : "."));
            }
            else
            {
                context.SkipStep("Create and store the certificate", $"The stored certificate is kept (expires {registration.CredentialExpirationUtc:yyyy-MM-dd}).");
            }

            context.BeginStep("Save to the customer profile");
            tenant = tenant with { ApplicationRegistration = registration };
            Require(await tenants.SaveTenantAsync(customerId, tenant, null, null, token));
            context.CompleteStep($"Tenant '{tenant.TenantKey}' saved to {profile.Name}.");
            await audit.RecordAsync(AuditEventTypes.TenantAppRegistrationCreated, AuditOutcome.Success,
                $"Automatic Read registration for '{profile.Name}' ({(created ? "created" : "updated")}).",
                new Dictionary<string, string?>
                {
                    ["CustomerId"] = customerId.Value, ["TenantId"] = organization.TenantId.ToString(), ["ClientId"] = application.ClientId.ToString(),
                    ["Registration"] = "Read", ["RemovedPermissions"] = string.Join(",", removed)
                }, token);

            var report = await TestAsync(context, customerId, tenant, token);
            if (report?.State != TenantConnectionState.Connected)
            {
                context.FailStep($"{report?.State}: {report?.Error}. The registration was saved; run Test Connection again in a few minutes.");
                return $"{profile.Name}: app registered; the connection test is still {report?.State}.";
            }

            context.CompleteStep("Connected; all read permissions are granted.");
            logger.LogInformation("Read registration completed for customer {CustomerId} tenant {TenantId}", customerId.Value, organization.TenantId);
            return $"{profile.Name} is connected ({organization.DefaultDomain}).";
        }
        finally
        {
            await registrations.SignOutAsync(session);
        }
    }

    private async Task<string?> EnableDeploymentAsync(IJobContext context, CustomerId customerId)
    {
        var token = context.CancellationToken;
        var profile = await profiles.LoadAsync(customerId, token) ?? throw new InvalidOperationException($"Customer '{customerId}' does not exist.");
        var tenant = profile.PrimaryTenant ?? throw new InvalidOperationException($"{profile.Name} has no connected tenant. Connect the tenant first.");
        if (tenant.TenantId is not { } tenantId) throw new InvalidOperationException($"{profile.Name} has no tenant ID. Connect the tenant first.");

        context.BeginStep("Sign in as administrator", "Sign in as a Global Administrator (or Privileged Role Administrator and Application Administrator).");
        var session = Require(await registrations.ConnectAdministratorAsync(tenantId, tenant.CloudEnvironment, token));
        try
        {
            context.CompleteStep($"Signed in as {session.Account}.");
            context.BeginStep("Read tenant");
            var organization = Require(await registrations.GetOrganizationAsync(session, token));
            if (organization.TenantId != tenantId) throw new InvalidOperationException($"The administrator signed in to tenant {organization.TenantId}, not {tenantId}.");
            context.CompleteStep($"{organization.DisplayName} ({organization.DefaultDomain}).");

            context.BeginStep("Find or create the Deploy registration");
            var known = tenant.DeploymentRegistration?.ClientId;
            var application = known is { } clientId && clientId != Guid.Empty
                ? Require(await registrations.FindApplicationAsync(session, clientId, ITenantOnboardingService.DeployApplicationDisplayName, token))
                : null;
            application ??= Require(await registrations.FindApplicationAsync(session, null, ITenantOnboardingService.DeployApplicationDisplayName, token));
            var created = application is null;
            application ??= Require(await registrations.CreateApplicationAsync(session, ITenantOnboardingService.DeployApplicationDisplayName, token));
            context.CompleteStep(created ? $"Created '{application.DisplayName}' (client ID {application.ClientId})." : $"Using '{application.DisplayName}' (client ID {application.ClientId}).");

            context.BeginStep("Create the service principal");
            var servicePrincipalId = Require(await registrations.EnsureServicePrincipalAsync(session, application.ClientId, token));
            context.CompleteStep($"Service principal {servicePrincipalId}.");

            var required = permissions.RequiredApplicationPermissions(PermissionValidationService.DeployCapabilities);
            context.BeginStep("Configure write permissions", string.Join(", ", required));
            Require(await registrations.ConfigureGraphApplicationPermissionsAsync(session, application.ObjectId, required, token));
            Require(await registrations.RevokeGraphPermissionsAsync(session, application.ObjectId, servicePrincipalId, required, token));
            context.CompleteStep($"{required.Count} application permissions configured.");

            context.BeginStep("Grant admin consent");
            var granted = Require(await registrations.GrantAdminConsentAsync(session, servicePrincipalId, required, token));
            context.CompleteStep($"Consent granted for {granted.Count} permissions.");

            var registration = new ApplicationRegistration(application.ClientId, servicePrincipalId, TenantAuthenticationType.Certificate, null, null, null,
                PermissionValidationService.DeployCapabilities);
            context.BeginStep("Create and store the certificate", "The private key is created on this computer and kept only in the encrypted secret store.");
            var target = tenant with { DeploymentRegistration = registration };
            var certificate = Require(await registrations.CreateCertificateCredentialAsync(session, application.ObjectId,
                $"{ITenantOnboardingService.DeployApplicationDisplayName} - {organization.DefaultDomain ?? tenantId.ToString()}", CertificateLifetime,
                (value, expiresOn, cancellation) => tenantCredentials.StoreCertificateAsync(customerId.Value, target, "deploy", value, expiresOn, cancellation), token));
            registration = registration with { SecretReference = certificate.StoredAs, CertificateReference = certificate.Thumbprint, CredentialExpirationUtc = certificate.EndDateTimeUtc };
            context.CompleteStep($"Certificate {certificate.Thumbprint[..8]}… expires {certificate.EndDateTimeUtc:yyyy-MM-dd}.");

            context.BeginStep("Save to the customer profile");
            await SaveDeploymentAsync(customerId, tenant.TenantKey, registration, token);
            context.CompleteStep("Deployment access saved. Remove it when the deploy phase ends.");
            await audit.RecordAsync(AuditEventTypes.TenantAppRegistrationCreated, AuditOutcome.Success, $"Deployment access enabled for '{profile.Name}'.",
                new Dictionary<string, string?>
                {
                    ["CustomerId"] = customerId.Value, ["TenantId"] = tenantId.ToString(), ["ClientId"] = application.ClientId.ToString(), ["Registration"] = "Deploy",
                    ["Permissions"] = string.Join(",", required)
                }, token);

            var report = await TestAsync(context, customerId, tenant with { ApplicationRegistration = registration }, token);
            if (report?.State != TenantConnectionState.Connected)
            {
                context.FailStep($"{report?.State}: {report?.Error}. Deployment access was saved; Microsoft Entra can take a few minutes to accept a new certificate.");
                return $"{profile.Name}: deployment access saved; the sign-in test is still {report?.State}.";
            }

            context.CompleteStep("The Deploy registration signs in and holds the write permissions.");
            return $"{profile.Name}: deployment access is enabled. Remove it when the deploy phase ends.";
        }
        finally
        {
            await registrations.SignOutAsync(session);
        }
    }

    private async Task<string?> RemoveDeploymentAsync(IJobContext context, CustomerId customerId)
    {
        var token = context.CancellationToken;
        var profile = await profiles.LoadAsync(customerId, token) ?? throw new InvalidOperationException($"Customer '{customerId}' does not exist.");
        var tenant = profile.PrimaryTenant ?? throw new InvalidOperationException($"{profile.Name} has no connected tenant.");
        if (tenant.DeploymentRegistration is not { } deployment) return $"{profile.Name} has no deployment access to remove.";

        context.BeginStep("Sign in as administrator", "Sign in as a Global Administrator (or Application Administrator).");
        var session = Require(await registrations.ConnectAdministratorAsync(tenant.TenantId, tenant.CloudEnvironment, token));
        try
        {
            context.CompleteStep($"Signed in as {session.Account}.");
            context.BeginStep("Delete the Deploy registration");
            var application = Require(await registrations.FindApplicationAsync(session, deployment.ClientId, ITenantOnboardingService.DeployApplicationDisplayName, token));
            if (application is null) context.CompleteStep("The Deploy registration no longer exists in the tenant.");
            else
            {
                Require(await registrations.DeleteApplicationAsync(session, application.ObjectId, token));
                context.CompleteStep($"Deleted '{application.DisplayName}' (client ID {application.ClientId}); its write permissions are gone.");
            }

            context.BeginStep("Delete the stored certificate");
            if (deployment.SecretReference is { } reference) Require(await tenantCredentials.DeleteCredentialAsync(reference, token));
            context.CompleteStep("The certificate's private key was deleted from the secret store.");

            context.BeginStep("Save to the customer profile");
            await SaveDeploymentAsync(customerId, tenant.TenantKey, null, token);
            context.CompleteStep("Deployment access removed.");
            await audit.RecordAsync(AuditEventTypes.TenantAppRegistrationUpdated, AuditOutcome.Success, $"Deployment access removed for '{profile.Name}'.",
                new Dictionary<string, string?> { ["CustomerId"] = customerId.Value, ["TenantId"] = tenant.TenantId?.ToString(), ["ClientId"] = deployment.ClientId.ToString() }, token);
            return $"{profile.Name}: deployment access removed.";
        }
        finally
        {
            await registrations.SignOutAsync(session);
        }
    }

    private async Task SaveDeploymentAsync(CustomerId customerId, string tenantKey, ApplicationRegistration? deployment, CancellationToken token)
    {
        var profile = await profiles.LoadAsync(customerId, token) ?? throw new InvalidOperationException($"Customer '{customerId}' does not exist.");
        var updated = profile with
        {
            Tenants = profile.Tenants.Select(tenant => string.Equals(tenant.TenantKey, tenantKey, StringComparison.OrdinalIgnoreCase)
                ? tenant with { DeploymentRegistration = deployment }
                : tenant).ToArray()
        };
        await profiles.SaveAsync(updated, token);
    }

    private async Task<TenantConnectionReport?> TestAsync(IJobContext context, CustomerId customerId, TenantConfiguration tenant, CancellationToken token)
    {
        context.BeginStep("Test the connection", "New certificates and consent can take a minute or two to reach every Microsoft Entra region.");
        TenantConnectionReport? report = null;
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            report = await connections.TestConnectionAsync(customerId.Value, tenant, token);
            if (report.State is TenantConnectionState.Connected) break;
            context.Report(90, $"Waiting for Microsoft Entra to propagate the registration (attempt {attempt} of 8)...");
            await Task.Delay(TimeSpan.FromSeconds(20), token);
        }

        return report;
    }

    /// <summary>Value of an optional step's result; null (with the error text) when it failed.</summary>
    private static T? Optional<T>(OperationResult<T> result, out string? error) where T : struct
    {
        error = result.Succeeded ? null : result.Error?.Message;
        return result.Succeeded ? result.Value : null;
    }

    private static IReadOnlyList<string>? Optional(OperationResult<IReadOnlyList<string>> result, out string? error)
    {
        error = result.Succeeded ? null : result.Error?.Message;
        return result.Succeeded ? result.Value : null;
    }

    private static T Require<T>(OperationResult<T> result) =>
        result.Succeeded ? result.Value! : throw new InvalidOperationException(result.Error!.ToString());

    private static void Require(OperationResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException(result.Error!.ToString());
    }
}
