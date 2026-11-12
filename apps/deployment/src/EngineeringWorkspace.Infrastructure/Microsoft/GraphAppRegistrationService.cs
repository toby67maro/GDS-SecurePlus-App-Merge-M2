using System.Collections.Concurrent;
using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Application.Tenants;
using EngineeringWorkspace.Domain.Security;
using EngineeringWorkspace.Domain.Tenants;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Applications.Item.AddPassword;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using OperationResult = EngineeringWorkspace.Application.Common.OperationResult;
using OperationError = EngineeringWorkspace.Application.Common.OperationError;
using Prompt = Microsoft.Identity.Client.Prompt;
using Microsoft.Identity.Client;

namespace EngineeringWorkspace.Infrastructure.Microsoft;

/// <summary>
/// Administrative Graph operations for the app-registration assistant, using a delegated administrator session
/// (MSAL interactive sign-in in the system browser). Each method performs one documented Graph call. Nothing is
/// granted or created unless the method is called, and the caller confirms each step first.
/// </summary>
public sealed class GraphAppRegistrationService(
    IntegrationSettings integrations,
    IRedactionService redaction,
    ILogger<GraphAppRegistrationService> logger) : IAppRegistrationService
{
    /// <summary>Application ID of the Microsoft Graph resource (first-party constant, documented in the Graph permissions reference).</summary>
    private const string MicrosoftGraphAppId = "00000003-0000-0000-c000-000000000000";

    private sealed record Session(IPublicClientApplication Application, IAccount Account, MicrosoftCloudEndpoints Endpoints, string[] Scopes);

    /// <summary>
    /// Microsoft Graph Command Line Tools (the first-party public client used by Microsoft Graph PowerShell). Used for
    /// the administrator sign-in when [Integrations:Microsoft] AdministratorClientId is not set, so no app has to be
    /// registered before the automatic registration can run. Its redirect URI is http://localhost.
    /// </summary>
    public const string DefaultAdministratorClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";

    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    private string AdministratorClientId => Guid.TryParse(integrations.AdministratorClientId, out var configured) ? configured.ToString() : DefaultAdministratorClientId;

    private string RedirectUri => string.IsNullOrWhiteSpace(integrations.AdministratorRedirectUri) ? "http://localhost" : integrations.AdministratorRedirectUri;

    public string? UnavailableReason => null;

    /// <summary>RoleManagement.ReadWrite.Directory assigns the Global Reader role to the Read registration (Exchange, Purview and Teams PowerShell).</summary>
    public IReadOnlyList<string> AdministratorScopes { get; } =
        ["Application.ReadWrite.All", "AppRoleAssignment.ReadWrite.All", "RoleManagement.ReadWrite.Directory", "User.Read"];

    public async Task<OperationResult<AdministratorSession>> ConnectAdministratorAsync(Guid? tenantId, CloudEnvironment cloud, CancellationToken cancellationToken)
    {
        var endpoints = MicrosoftCloudEndpoints.For(cloud);
        var scopes = AdministratorScopes.Select(scope => new Uri(endpoints.GraphBaseUrl, scope).ToString()).ToArray();
        var app = PublicClientApplicationBuilder.Create(AdministratorClientId)
            .WithAuthority(new Uri(endpoints.Authority, tenantId?.ToString() ?? "organizations").ToString())
            .WithRedirectUri(RedirectUri)
            .Build();
        try
        {
            var result = await app.AcquireTokenInteractive(scopes).WithPrompt(Prompt.SelectAccount).ExecuteAsync(cancellationToken);
            var sessionId = Guid.NewGuid().ToString("N");
            _sessions[sessionId] = new Session(app, result.Account, endpoints, scopes);
            var granted = result.Scopes.Select(scope => scope[(scope.LastIndexOf('/') + 1)..]).ToArray();
            logger.LogInformation("Administrator signed in to tenant {TenantId} for the app-registration assistant", result.TenantId);
            return OperationResult<AdministratorSession>.Success(new AdministratorSession(sessionId, Guid.TryParse(result.TenantId, out var signedInTenant) ? signedInTenant : Guid.Empty,
                result.Account.Username, cloud, granted));
        }
        catch (MsalException exception)
        {
            return OperationResult<AdministratorSession>.Failure(MsalErrorTranslator.Translate(exception, redaction));
        }
    }

    public Task<OperationResult<CreatedApplication>> CreateApplicationAsync(AdministratorSession session, string displayName, CancellationToken cancellationToken) =>
        RunAsync(session, "create application", async client =>
        {
            var created = await client.Applications.PostAsync(new global::Microsoft.Graph.Models.Application { DisplayName = displayName, SignInAudience = "AzureADMyOrg" }, cancellationToken: cancellationToken);
            if (created?.Id is null || !Guid.TryParse(created.AppId, out var clientId) || !Guid.TryParse(created.Id, out var objectId))
                return OperationResult<CreatedApplication>.Failure("GraphEmpty", "Microsoft Graph did not return the new application.");
            logger.LogInformation("App registration {ClientId} created", clientId);
            return OperationResult<CreatedApplication>.Success(new CreatedApplication(objectId, clientId, displayName));
        });

    public Task<OperationResult<Guid>> EnsureServicePrincipalAsync(AdministratorSession session, Guid clientId, CancellationToken cancellationToken) =>
        RunAsync(session, "create or locate service principal", async client =>
        {
            var existing = await FindServicePrincipalAsync(client, clientId.ToString(), cancellationToken);
            if (existing?.Id is not null && Guid.TryParse(existing.Id, out var existingId)) return OperationResult<Guid>.Success(existingId);
            var created = await client.ServicePrincipals.PostAsync(new ServicePrincipal { AppId = clientId.ToString() }, cancellationToken: cancellationToken);
            return created?.Id is not null && Guid.TryParse(created.Id, out var id)
                ? OperationResult<Guid>.Success(id)
                : OperationResult<Guid>.Failure("GraphEmpty", "Microsoft Graph did not return the service principal.");
        });

    public Task<OperationResult> ConfigureGraphApplicationPermissionsAsync(AdministratorSession session, Guid applicationObjectId, IReadOnlyList<string> permissions, CancellationToken cancellationToken) =>
        RunAsync(session, "configure permissions", async client =>
        {
            var roles = await GraphApplicationRolesAsync(client, permissions, cancellationToken);
            if (!roles.Succeeded) return OperationResult.Failure(roles.Error!);
            var application = await client.Applications[applicationObjectId.ToString()].GetAsync(request => request.QueryParameters.Select = ["id", "requiredResourceAccess"], cancellationToken);
            var resources = application?.RequiredResourceAccess?.ToList() ?? [];
            var graph = resources.FirstOrDefault(resource => string.Equals(resource.ResourceAppId, MicrosoftGraphAppId, StringComparison.OrdinalIgnoreCase));
            if (graph is null)
            {
                graph = new RequiredResourceAccess { ResourceAppId = MicrosoftGraphAppId, ResourceAccess = [] };
                resources.Add(graph);
            }

            graph.ResourceAccess ??= [];
            var existing = graph.ResourceAccess.Where(access => access.Type == "Role").Select(access => access.Id).ToHashSet();
            foreach (var role in roles.Value!.Where(role => !existing.Contains(role.Id)))
                graph.ResourceAccess.Add(new ResourceAccess { Id = role.Id, Type = "Role" });
            await client.Applications[applicationObjectId.ToString()].PatchAsync(new global::Microsoft.Graph.Models.Application { RequiredResourceAccess = resources },
                cancellationToken: cancellationToken);
            return OperationResult.Success();
        });

    public Task<OperationResult<TenantOrganization>> GetOrganizationAsync(AdministratorSession session, CancellationToken cancellationToken) =>
        RunAsync(session, "read organization", async client =>
        {
            var response = await client.Organization.GetAsync(request => request.QueryParameters.Select = ["id", "displayName", "verifiedDomains"], cancellationToken);
            var organization = response?.Value?.FirstOrDefault();
            if (organization is null || !Guid.TryParse(organization.Id, out var id))
                return OperationResult<TenantOrganization>.Failure("GraphEmpty", "Microsoft Graph returned no organization.");
            var domains = organization.VerifiedDomains?.Select(domain => domain.Name).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
            var defaultDomain = organization.VerifiedDomains?.FirstOrDefault(domain => domain.IsDefault == true)?.Name;
            return OperationResult<TenantOrganization>.Success(new TenantOrganization(id, organization.DisplayName, domains, defaultDomain));
        });

    public Task<OperationResult<CreatedApplication?>> FindApplicationAsync(AdministratorSession session, Guid? clientId, string displayName, CancellationToken cancellationToken) =>
        RunAsync(session, "find application", async client =>
        {
            var filter = clientId is { } id ? $"appId eq '{id}'" : $"displayName eq '{displayName.Replace("'", "''", StringComparison.Ordinal)}'";
            var response = await client.Applications.GetAsync(request =>
            {
                request.QueryParameters.Filter = filter;
                request.QueryParameters.Select = ["id", "appId", "displayName"];
            }, cancellationToken);
            var found = response?.Value?.FirstOrDefault();
            return found is not null && Guid.TryParse(found.Id, out var objectId) && Guid.TryParse(found.AppId, out var appId)
                ? OperationResult<CreatedApplication?>.Success(new CreatedApplication(objectId, appId, found.DisplayName ?? displayName))
                : OperationResult<CreatedApplication?>.Success(null);
        });

    public Task<OperationResult<IReadOnlyList<string>>> GrantAdminConsentAsync(AdministratorSession session, Guid servicePrincipalId, IReadOnlyList<string> permissions, CancellationToken cancellationToken) =>
        RunAsync(session, "grant admin consent", async client =>
        {
            var graph = await FindServicePrincipalAsync(client, MicrosoftGraphAppId, cancellationToken);
            var roles = await GraphApplicationRolesAsync(client, permissions, cancellationToken);
            if (graph?.Id is null || !roles.Succeeded)
                return OperationResult<IReadOnlyList<string>>.Failure(roles.Error ?? new OperationError("GraphEmpty", "The Microsoft Graph service principal was not found in the tenant."));
            var assigned = await client.ServicePrincipals[servicePrincipalId.ToString()].AppRoleAssignments.GetAsync(cancellationToken: cancellationToken);
            var existing = assigned?.Value?.Where(assignment => assignment.ResourceId?.ToString() == graph.Id).Select(assignment => assignment.AppRoleId).ToHashSet() ?? [];
            var granted = new List<string>();
            foreach (var role in roles.Value!)
            {
                if (!existing.Contains(role.Id))
                {
                    await client.ServicePrincipals[graph.Id].AppRoleAssignedTo.PostAsync(new AppRoleAssignment
                    {
                        PrincipalId = servicePrincipalId,
                        ResourceId = Guid.Parse(graph.Id),
                        AppRoleId = role.Id
                    }, cancellationToken: cancellationToken);
                    logger.LogInformation("Admin consent granted for {Permission} to service principal {ServicePrincipalId}", role.Value, servicePrincipalId);
                }

                granted.Add(role.Value!);
            }

            return OperationResult<IReadOnlyList<string>>.Success(granted);
        });

    public Task<OperationResult<CreatedClientSecret>> CreateClientSecretAsync(
        AdministratorSession session,
        Guid applicationObjectId,
        string displayName,
        TimeSpan lifetime,
        Func<string, DateTimeOffset?, CancellationToken, Task<OperationResult<SecretReference>>> protect,
        CancellationToken cancellationToken) =>
        RunAsync(session, "create client secret", async client =>
        {
            var credential = await client.Applications[applicationObjectId.ToString()].AddPassword.PostAsync(new AddPasswordPostRequestBody
            {
                PasswordCredential = new PasswordCredential { DisplayName = displayName, EndDateTime = DateTimeOffset.UtcNow.Add(lifetime) }
            }, cancellationToken: cancellationToken);
            if (credential?.SecretText is not { Length: > 0 } || credential.KeyId is not { } keyId)
                return OperationResult<CreatedClientSecret>.Failure("GraphEmpty", "Microsoft Graph did not return the new client secret.");

            // The value is returned once: protect it immediately and keep no other copy.
            var stored = await protect(credential.SecretText, credential.EndDateTime, cancellationToken);
            credential.SecretText = null;
            if (!stored.Succeeded)
                return OperationResult<CreatedClientSecret>.Failure(new OperationError("SecretStoreFailed",
                    "The client secret was created but could not be stored. It was not saved anywhere else.", stored.Error?.Message,
                    $"Delete key {keyId} from the app registration in Microsoft Entra and run this step again."));
            logger.LogInformation("Client secret {KeyId} created and stored as {SecretReference}", keyId, stored.Value!.ToString());
            return OperationResult<CreatedClientSecret>.Success(new CreatedClientSecret(keyId, credential.EndDateTime, stored.Value!));
        });

    public async Task SignOutAsync(AdministratorSession session)
    {
        if (_sessions.TryRemove(session.SessionId, out var removed))
            await removed.Application.RemoveAsync(removed.Account);
    }

    private async Task<OperationResult<T>> RunAsync<T>(AdministratorSession session, string operation, Func<GraphServiceClient, Task<OperationResult<T>>> action)
    {
        if (!_sessions.TryGetValue(session.SessionId, out var state))
            return OperationResult<T>.Failure("Session", "The administrator session has ended.", null, "Connect as administrator again.");
        try
        {
            return await action(Client(state));
        }
        catch (Exception exception) when (exception is ODataError or MsalException or HttpRequestException)
        {
            var error = MicrosoftGraphClientFactory.Translate(exception, redaction);
            logger.LogWarning("App-registration assistant step '{Operation}' failed: {Category}", operation, error.Category);
            return OperationResult<T>.Failure(error);
        }
    }

    private async Task<OperationResult> RunAsync(AdministratorSession session, string operation, Func<GraphServiceClient, Task<OperationResult>> action)
    {
        var result = await RunAsync<bool>(session, operation, async client =>
        {
            var inner = await action(client);
            return inner.Succeeded ? OperationResult<bool>.Success(true) : OperationResult<bool>.Failure(inner.Error!);
        });
        return result.Succeeded ? OperationResult.Success() : OperationResult.Failure(result.Error!);
    }

    private static GraphServiceClient Client(Session session) =>
        MicrosoftGraphClientFactory.Create(session.Endpoints, new DelegateAccessTokenProvider(session.Endpoints.GraphBaseUrl, async cancellationToken =>
            (await session.Application.AcquireTokenSilent(session.Scopes, session.Account).ExecuteAsync(cancellationToken)).AccessToken));

    private static async Task<ServicePrincipal?> FindServicePrincipalAsync(GraphServiceClient client, string appId, CancellationToken cancellationToken)
    {
        var response = await client.ServicePrincipals.GetAsync(request =>
        {
            request.QueryParameters.Filter = $"appId eq '{Guid.Parse(appId)}'";
            request.QueryParameters.Select = ["id", "appId", "appRoles"];
        }, cancellationToken);
        return response?.Value?.FirstOrDefault();
    }

    /// <summary>Resolves permission names to Microsoft Graph app-role IDs from the tenant's Graph service principal (no hard-coded role IDs).</summary>
    private static Task<OperationResult<IReadOnlyList<AppRole>>> GraphApplicationRolesAsync(GraphServiceClient client, IReadOnlyList<string> permissions, CancellationToken cancellationToken) =>
        ApplicationRolesAsync(client, MicrosoftGraphAppId, "Microsoft Graph", permissions, cancellationToken);

    /// <summary>Resolves permission names to app-role IDs from a resource's service principal in the tenant.</summary>
    private static async Task<OperationResult<IReadOnlyList<AppRole>>> ApplicationRolesAsync(GraphServiceClient client, string resourceAppId, string resourceName,
        IReadOnlyList<string> permissions, CancellationToken cancellationToken)
    {
        var resource = await FindServicePrincipalAsync(client, resourceAppId, cancellationToken);
        var roles = new List<AppRole>();
        foreach (var permission in permissions)
        {
            var role = resource?.AppRoles?.FirstOrDefault(candidate => string.Equals(candidate.Value, permission, StringComparison.Ordinal) &&
                                                                      candidate.AllowedMemberTypes?.Contains("Application") == true);
            if (role?.Id is null) return OperationResult<IReadOnlyList<AppRole>>.Failure("PermissionUnknown", $"'{permission}' is not a {resourceName} application permission in this tenant.");
            roles.Add(role);
        }

        return OperationResult<IReadOnlyList<AppRole>>.Success(roles);
    }

    // ------------------------------------------------------------------------------------------------------------
    // 4.0 beta.2: Read/Deploy registrations, Exchange resources, directory roles and certificates
    // ------------------------------------------------------------------------------------------------------------

    public Task<OperationResult<bool>> ConfigureResourcePermissionsAsync(AdministratorSession session, Guid applicationObjectId, string resourceAppId, IReadOnlyList<string> permissions,
        CancellationToken cancellationToken) =>
        RunAsync(session, "configure resource permissions", async client =>
        {
            if (await FindServicePrincipalAsync(client, resourceAppId, cancellationToken) is null) return OperationResult<bool>.Success(false);
            var roles = await ApplicationRolesAsync(client, resourceAppId, resourceAppId, permissions, cancellationToken);
            if (!roles.Succeeded) return OperationResult<bool>.Failure(roles.Error!);
            var application = await client.Applications[applicationObjectId.ToString()].GetAsync(request => request.QueryParameters.Select = ["id", "requiredResourceAccess"], cancellationToken);
            var resources = application?.RequiredResourceAccess?.ToList() ?? [];
            var entry = resources.FirstOrDefault(resource => string.Equals(resource.ResourceAppId, resourceAppId, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                entry = new RequiredResourceAccess { ResourceAppId = resourceAppId, ResourceAccess = [] };
                resources.Add(entry);
            }

            entry.ResourceAccess ??= [];
            var existing = entry.ResourceAccess.Where(access => access.Type == "Role").Select(access => access.Id).ToHashSet();
            foreach (var role in roles.Value!.Where(role => !existing.Contains(role.Id)))
                entry.ResourceAccess.Add(new ResourceAccess { Id = role.Id, Type = "Role" });
            await client.Applications[applicationObjectId.ToString()].PatchAsync(new global::Microsoft.Graph.Models.Application { RequiredResourceAccess = resources },
                cancellationToken: cancellationToken);
            return OperationResult<bool>.Success(true);
        });

    public Task<OperationResult<IReadOnlyList<string>>> GrantResourceConsentAsync(AdministratorSession session, Guid servicePrincipalId, string resourceAppId,
        IReadOnlyList<string> permissions, CancellationToken cancellationToken) =>
        RunAsync(session, "grant resource consent", async client =>
        {
            var resource = await FindServicePrincipalAsync(client, resourceAppId, cancellationToken);
            if (resource?.Id is null) return OperationResult<IReadOnlyList<string>>.Success([]);
            var roles = await ApplicationRolesAsync(client, resourceAppId, resourceAppId, permissions, cancellationToken);
            if (!roles.Succeeded) return OperationResult<IReadOnlyList<string>>.Failure(roles.Error!);
            var assigned = await client.ServicePrincipals[servicePrincipalId.ToString()].AppRoleAssignments.GetAsync(cancellationToken: cancellationToken);
            var existing = assigned?.Value?.Where(assignment => assignment.ResourceId?.ToString() == resource.Id).Select(assignment => assignment.AppRoleId).ToHashSet() ?? [];
            foreach (var role in roles.Value!.Where(role => !existing.Contains(role.Id)))
            {
                await client.ServicePrincipals[resource.Id].AppRoleAssignedTo.PostAsync(new AppRoleAssignment
                {
                    PrincipalId = servicePrincipalId,
                    ResourceId = Guid.Parse(resource.Id),
                    AppRoleId = role.Id
                }, cancellationToken: cancellationToken);
                logger.LogInformation("Admin consent granted for {Permission} on {Resource} to service principal {ServicePrincipalId}", role.Value, resourceAppId, servicePrincipalId);
            }

            return OperationResult<IReadOnlyList<string>>.Success(roles.Value!.Select(role => role.Value!).ToArray());
        });

    public Task<OperationResult<IReadOnlyList<string>>> RevokeGraphPermissionsAsync(AdministratorSession session, Guid applicationObjectId, Guid servicePrincipalId,
        IReadOnlyList<string> keep, CancellationToken cancellationToken) =>
        RunAsync(session, "remove permissions", async client =>
        {
            var graph = await FindServicePrincipalAsync(client, MicrosoftGraphAppId, cancellationToken);
            if (graph?.Id is null) return OperationResult<IReadOnlyList<string>>.Failure("GraphEmpty", "The Microsoft Graph service principal was not found in the tenant.");
            var keepIds = graph.AppRoles?.Where(role => role.Value is not null && keep.Contains(role.Value, StringComparer.Ordinal)).Select(role => role.Id).ToHashSet() ?? [];
            var names = graph.AppRoles?.Where(role => role.Id is not null).ToDictionary(role => role.Id!.Value, role => role.Value ?? role.Id!.Value.ToString()) ?? [];
            var removed = new List<string>();

            var assigned = await client.ServicePrincipals[servicePrincipalId.ToString()].AppRoleAssignments.GetAsync(cancellationToken: cancellationToken);
            foreach (var assignment in assigned?.Value?.Where(assignment => assignment.ResourceId?.ToString() == graph.Id && !keepIds.Contains(assignment.AppRoleId)) ?? [])
            {
                await client.ServicePrincipals[servicePrincipalId.ToString()].AppRoleAssignments[assignment.Id].DeleteAsync(cancellationToken: cancellationToken);
                removed.Add(assignment.AppRoleId is { } id && names.TryGetValue(id, out var name) ? name : assignment.AppRoleId?.ToString() ?? "?");
            }

            var application = await client.Applications[applicationObjectId.ToString()].GetAsync(request => request.QueryParameters.Select = ["id", "requiredResourceAccess"], cancellationToken);
            var resources = application?.RequiredResourceAccess?.ToList() ?? [];
            var entry = resources.FirstOrDefault(resource => string.Equals(resource.ResourceAppId, MicrosoftGraphAppId, StringComparison.OrdinalIgnoreCase));
            if (entry?.ResourceAccess is { } access && access.Any(item => item.Type == "Role" && !keepIds.Contains(item.Id)))
            {
                entry.ResourceAccess = access.Where(item => item.Type != "Role" || keepIds.Contains(item.Id)).ToList();
                await client.Applications[applicationObjectId.ToString()].PatchAsync(new global::Microsoft.Graph.Models.Application { RequiredResourceAccess = resources },
                    cancellationToken: cancellationToken);
            }

            if (removed.Count > 0) logger.LogInformation("Removed {Count} Graph permission(s) from service principal {ServicePrincipalId}", removed.Count, servicePrincipalId);
            return OperationResult<IReadOnlyList<string>>.Success(removed.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        });

    public Task<OperationResult<bool>> AssignDirectoryRoleAsync(AdministratorSession session, Guid servicePrincipalId, string roleTemplateId, CancellationToken cancellationToken) =>
        RunAsync(session, "assign directory role", async client =>
        {
            var existing = await client.RoleManagement.Directory.RoleAssignments.GetAsync(request =>
                request.QueryParameters.Filter = $"principalId eq '{servicePrincipalId}' and roleDefinitionId eq '{Guid.Parse(roleTemplateId)}'", cancellationToken);
            if (existing?.Value?.Count > 0) return OperationResult<bool>.Success(false);
            await client.RoleManagement.Directory.RoleAssignments.PostAsync(new UnifiedRoleAssignment
            {
                PrincipalId = servicePrincipalId.ToString(),
                RoleDefinitionId = roleTemplateId,
                DirectoryScopeId = "/"
            }, cancellationToken: cancellationToken);
            logger.LogInformation("Directory role {RoleId} assigned to service principal {ServicePrincipalId}", roleTemplateId, servicePrincipalId);
            return OperationResult<bool>.Success(true);
        });

    public Task<OperationResult<CreatedCertificate>> CreateCertificateCredentialAsync(AdministratorSession session, Guid applicationObjectId, string subjectName, TimeSpan lifetime,
        Func<string, DateTimeOffset?, CancellationToken, Task<OperationResult<SecretReference>>> protect, CancellationToken cancellationToken) =>
        RunAsync(session, "create certificate", async client =>
        {
            var created = CertificateCredential.Create(subjectName, lifetime, DateTimeOffset.UtcNow);
            // Only the public certificate is sent to Microsoft Entra; it replaces earlier certificates of this application.
            await client.Applications[applicationObjectId.ToString()].PatchAsync(new global::Microsoft.Graph.Models.Application
            {
                KeyCredentials =
                [
                    new KeyCredential
                    {
                        Type = "AsymmetricX509Cert",
                        Usage = "Verify",
                        Key = created.PublicCertificate,
                        DisplayName = $"CN={subjectName}",
                        EndDateTime = created.NotAfterUtc
                    }
                ]
            }, cancellationToken: cancellationToken);

            var stored = await protect(created.StoredValue, created.NotAfterUtc, cancellationToken);
            if (!stored.Succeeded)
                return OperationResult<CreatedCertificate>.Failure(new OperationError("SecretStoreFailed",
                    "The certificate was added to the app registration but could not be stored. Its private key was not saved anywhere.", stored.Error?.Message,
                    "Run Update permissions again to create a new certificate."));

            // The certificate replaces client secrets: remove them so no long-lived secret remains on the registration.
            var application = await client.Applications[applicationObjectId.ToString()].GetAsync(request => request.QueryParameters.Select = ["id", "passwordCredentials"], cancellationToken);
            var removed = 0;
            foreach (var password in application?.PasswordCredentials ?? [])
            {
                if (password.KeyId is not { } keyId) continue;
                await client.Applications[applicationObjectId.ToString()].RemovePassword.PostAsync(
                    new global::Microsoft.Graph.Applications.Item.RemovePassword.RemovePasswordPostRequestBody { KeyId = keyId }, cancellationToken: cancellationToken);
                removed++;
            }

            logger.LogInformation("Certificate {Thumbprint} added to application {ObjectId} and stored as {SecretReference}; {Removed} client secret(s) removed",
                created.Thumbprint, applicationObjectId, stored.Value!.ToString(), removed);
            return OperationResult<CreatedCertificate>.Success(new CreatedCertificate(created.Thumbprint, created.NotAfterUtc, stored.Value!, removed));
        });

    public Task<OperationResult> RenameApplicationAsync(AdministratorSession session, Guid applicationObjectId, string displayName, CancellationToken cancellationToken) =>
        RunAsync(session, "rename application", async client =>
        {
            await client.Applications[applicationObjectId.ToString()].PatchAsync(new global::Microsoft.Graph.Models.Application { DisplayName = displayName },
                cancellationToken: cancellationToken);
            return OperationResult.Success();
        });

    public Task<OperationResult> DeleteApplicationAsync(AdministratorSession session, Guid applicationObjectId, CancellationToken cancellationToken) =>
        RunAsync(session, "delete application", async client =>
        {
            await client.Applications[applicationObjectId.ToString()].DeleteAsync(cancellationToken: cancellationToken);
            logger.LogInformation("Application {ObjectId} deleted", applicationObjectId);
            return OperationResult.Success();
        });
}
