using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Credentials;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Application.Tenants;
using EngineeringWorkspace.Domain.Security;
using EngineeringWorkspace.Domain.Tenants;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions.Authentication;

namespace EngineeringWorkspace.Infrastructure.Microsoft;

/// <summary>Supplies bearer tokens to the Graph SDK from a delegate; tokens never leave this boundary.</summary>
internal sealed class DelegateAccessTokenProvider(Uri graphBaseUrl, Func<CancellationToken, Task<string>> acquire) : IAccessTokenProvider
{
    public AllowedHostsValidator AllowedHostsValidator { get; } = new([graphBaseUrl.Host]);

    public Task<string> GetAuthorizationTokenAsync(Uri uri, Dictionary<string, object>? additionalAuthenticationContext = null, CancellationToken cancellationToken = default) =>
        acquire(cancellationToken);
}

/// <summary>Creates Graph clients for the configured cloud's Graph endpoint.</summary>
public sealed class MicrosoftGraphClientFactory(ISecretStore secrets, IRedactionService redaction, ILoggerFactory loggers) : IMicrosoftGraphClientFactory
{
    public ITenantGraphClient CreateForApplication(Guid tenantId, Guid clientId, SecretReference clientSecret, CloudEnvironment cloud)
    {
        var endpoints = MicrosoftCloudEndpoints.For(cloud);
        var provider = new DelegateAccessTokenProvider(endpoints.GraphBaseUrl, async cancellationToken =>
            (await MsalMicrosoftAuthenticationService.AcquireAsync(secrets, tenantId, clientId, clientSecret, cloud, cancellationToken)).AccessToken);
        return new TenantGraphClient(Create(endpoints, provider), redaction, loggers.CreateLogger<TenantGraphClient>());
    }

    internal static GraphServiceClient Create(MicrosoftCloudEndpoints endpoints, IAccessTokenProvider provider) =>
        new(new BaseBearerTokenAuthenticationProvider(provider), new Uri(endpoints.GraphBaseUrl, "v1.0").ToString());

    /// <summary>Translates Graph SDK errors without exposing response bodies that could carry sensitive data.</summary>
    internal static OperationError Translate(Exception exception, IRedactionService redaction) => exception switch
    {
        ODataError { ResponseStatusCode: 401 } error => new("GraphUnauthorized", "Microsoft Graph rejected the token.", Detail(error, redaction), "Check the cloud environment and that the token audience matches Microsoft Graph."),
        ODataError { ResponseStatusCode: 403 } error => new("GraphForbidden", "Authentication succeeded but Microsoft Graph rejected the operation.", Detail(error, redaction),
            "Grant the required application permission and admin consent."),
        ODataError error => new("GraphError", "Microsoft Graph returned an error.", Detail(error, redaction), "Review the details and retry."),
        MsalException msal => MsalErrorTranslator.Translate(msal, redaction),
        HttpRequestException => new("Network", "Microsoft Graph could not be reached.", exception.GetType().Name, "Check the network connection and proxy settings."),
        _ => new("GraphUnexpected", "The Microsoft Graph request failed.", exception.GetType().Name, "Review the application log.")
    };

    private static string Detail(ODataError error, IRedactionService redaction) =>
        redaction.RedactText($"HTTP {error.ResponseStatusCode} {error.Error?.Code}: {error.Error?.Message}".Trim());
}

/// <summary>Minimal Graph operations for tenant validation.</summary>
public sealed class TenantGraphClient(GraphServiceClient client, IRedactionService redaction, ILogger<TenantGraphClient> logger) : ITenantGraphClient
{
    public async Task<OperationResult<TenantOrganization>> GetOrganizationAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.Organization.GetAsync(request => request.QueryParameters.Select = ["id", "displayName", "verifiedDomains"], cancellationToken);
            var organization = response?.Value?.FirstOrDefault();
            if (organization is null || !Guid.TryParse(organization.Id, out var id))
                return OperationResult<TenantOrganization>.Failure("GraphEmpty", "Microsoft Graph returned no organization.", null, "Check the Tenant ID.");
            var domains = organization.VerifiedDomains?.Select(domain => domain.Name).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
            var defaultDomain = organization.VerifiedDomains?.FirstOrDefault(domain => domain.IsDefault == true)?.Name;
            return OperationResult<TenantOrganization>.Success(new TenantOrganization(id, organization.DisplayName, domains, defaultDomain));
        }
        catch (Exception exception) when (exception is ODataError or MsalException or HttpRequestException or SecretStoreException)
        {
            var error = exception is SecretStoreException secret
                ? new OperationError("Credential", "The client secret could not be read from the secret store.", secret.Message, "Re-enter the client secret.")
                : MicrosoftGraphClientFactory.Translate(exception, redaction);
            logger.LogWarning("Microsoft Graph organization request failed: {Category}", error.Category);
            return OperationResult<TenantOrganization>.Failure(error);
        }
    }
}
