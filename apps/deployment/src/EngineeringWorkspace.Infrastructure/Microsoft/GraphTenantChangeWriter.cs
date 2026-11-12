using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EngineeringWorkspace.Application.Credentials;
using EngineeringWorkspace.Application.Governance;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Application.Tenants;
using EngineeringWorkspace.Domain.Tenants;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;

namespace EngineeringWorkspace.Infrastructure.Microsoft;

/// <summary>Microsoft Graph implementation of <see cref="ITenantChangeWriter"/> (app-only token of the tenant's Deploy registration).</summary>
public sealed class GraphTenantChangeWriter(ISecretStore secrets, IRedactionService redaction, ILogger<GraphTenantChangeWriter> logger) : ITenantChangeWriter
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(100) };

    private async Task<(MicrosoftCloudEndpoints Endpoints, string Token)> TokenAsync(TenantConfiguration tenant, CancellationToken cancellationToken)
    {
        // 4.0 beta.2: writes use only the Deploy registration, which exists during deploy phases; the Read registration cannot write.
        var registration = tenant.DeploymentRegistration ?? throw new InvalidOperationException(
            $"Deployment access is not enabled for {tenant.DisplayName}. Use Enable deployment access on the customer's tenant (Customer Profiles) for this deploy phase, " +
            "and Remove deployment access when it ends.");
        if (tenant.TenantId is not { } tenantId) throw new InvalidOperationException($"{tenant.DisplayName} has no tenant ID.");
        if (registration.SecretReference is not { } secret) throw new InvalidOperationException($"The Deploy registration of {tenant.DisplayName} has no stored certificate.");
        var result = await MsalMicrosoftAuthenticationService.AcquireAsync(secrets, tenantId, registration.ClientId, secret, tenant.CloudEnvironment, cancellationToken);
        return (MicrosoftCloudEndpoints.For(tenant.CloudEnvironment), result.AccessToken);
    }

    private static Uri Url(MicrosoftCloudEndpoints endpoints, string apiVersion, string resource) =>
        new(endpoints.GraphBaseUrl, $"{apiVersion.Trim('/')}/{resource.TrimStart('/')}");

    public async Task<IReadOnlySet<string>> ExistingNamesAsync(TenantConfiguration tenant, string apiVersion, string resource, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (endpoints, token) = await TokenAsync(tenant, cancellationToken);
        Uri? next = Url(endpoints, apiVersion, resource);
        for (var page = 0; next is not null && page < 20; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Listing {Resource} returned HTTP {Status}", resource, (int)response.StatusCode);
                break;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.TryGetProperty("value", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                foreach (var property in new[] { "displayName", "name" })
                    if (item.TryGetProperty(property, out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } text)
                        names.Add(text);
            next = document.RootElement.TryGetProperty("@odata.nextLink", out var link) && Uri.TryCreate(link.GetString(), UriKind.Absolute, out var uri) &&
                   uri.Host == endpoints.GraphBaseUrl.Host ? uri : null;
        }

        return names;
    }

    public async Task<string?> ResolveGroupIdAsync(TenantConfiguration tenant, string displayName, CancellationToken cancellationToken)
    {
        var (endpoints, token) = await TokenAsync(tenant, cancellationToken);
        var filter = Uri.EscapeDataString($"displayName eq '{displayName.Replace("'", "''", StringComparison.Ordinal)}'");
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(endpoints, "v1.0", $"groups?$filter={filter}&$select=id,displayName&$top=2"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var groups = document.RootElement.TryGetProperty("value", out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().ToArray() : [];
        return groups.Length == 1 && groups[0].TryGetProperty("id", out var id) ? id.GetString() : null;
    }

    public async Task<TenantWriteResult> CreateAsync(TenantConfiguration tenant, string apiVersion, string resource, string jsonBody, CancellationToken cancellationToken)
    {
        try
        {
            var (endpoints, token) = await TokenAsync(tenant, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, Url(endpoints, apiVersion, resource))
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Http.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                string? id = null;
                try
                {
                    using var document = JsonDocument.Parse(text);
                    id = document.RootElement.TryGetProperty("id", out var value) ? value.GetString() : null;
                }
                catch (JsonException)
                {
                    // Created without a JSON body.
                }

                return TenantWriteResult.Created(id);
            }

            var message = text;
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.TryGetProperty("error", out var error))
                    message = $"{(error.TryGetProperty("code", out var code) ? code.GetString() : null)}: {(error.TryGetProperty("message", out var detail) ? detail.GetString() : null)}";
            }
            catch (JsonException)
            {
                // Not JSON: keep the text.
            }

            var hint = (int)response.StatusCode == 403
                ? " The app registration lacks the deployment permission or admin consent: update it on Tenant App Registration and consent again."
                : string.Empty;
            return TenantWriteResult.Failed(redaction.RedactText($"HTTP {(int)response.StatusCode} {message}".Trim()) + hint);
        }
        catch (Exception exception) when (exception is MsalException or HttpRequestException or SecretStoreException or InvalidOperationException or TaskCanceledException)
        {
            logger.LogWarning("Creating {Resource} failed: {Type}", resource, exception.GetType().Name);
            return TenantWriteResult.Failed(redaction.RedactText(exception.Message));
        }
    }
}
