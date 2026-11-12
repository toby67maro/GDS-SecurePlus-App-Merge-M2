using System.Text;
using System.Text.Json;
using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Credentials;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Application.Tenants;
using EngineeringWorkspace.Domain.Security;
using EngineeringWorkspace.Domain.Tenants;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;

namespace EngineeringWorkspace.Infrastructure.Microsoft;

/// <summary>Reads non-secret claims (tid, roles, scp) from an access token. The token is never logged or stored.</summary>
public static class AccessTokenClaims
{
    public static (Guid? TenantId, IReadOnlyList<string> Roles) Read(string accessToken)
    {
        var parts = accessToken.Split('.');
        if (parts.Length < 2) return (null, []);
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            var root = document.RootElement;
            Guid? tenant = root.TryGetProperty("tid", out var tid) && Guid.TryParse(tid.GetString(), out var parsed) ? parsed : null;
            var roles = root.TryGetProperty("roles", out var roleElement) && roleElement.ValueKind == JsonValueKind.Array
                ? roleElement.EnumerateArray().Select(role => role.GetString()).OfType<string>().ToArray()
                : [];
            return (tenant, roles);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return (null, []);
        }
    }
}

/// <summary>Translates MSAL failures into the fixed, actionable messages.</summary>
public static class MsalErrorTranslator
{
    public static OperationError Translate(MsalException exception, IRedactionService redaction)
    {
        var message = exception.Message;
        var detail = redaction.RedactText($"{exception.ErrorCode}: {message.Split('\n')[0]}");
        if (detail.Length > 400) detail = detail[..400];
        if (message.Contains("AADSTS7000222", StringComparison.Ordinal))
            return new("CredentialExpired", "The client secret has expired.", detail, "Create a new client secret, then replace it in Tenant App Registration.");
        if (message.Contains("AADSTS7000215", StringComparison.Ordinal) || exception.ErrorCode == "invalid_client")
            return new("CredentialRejected", "Microsoft Entra rejected the client credential.", detail, "Check that the client secret belongs to this client ID and re-enter it.");
        if (message.Contains("AADSTS700016", StringComparison.Ordinal))
            return new("ApplicationNotFound", "The application (client ID) was not found in the tenant.", detail, "Check the Client ID and that the app registration exists in this tenant.");
        if (message.Contains("AADSTS90002", StringComparison.Ordinal) || message.Contains("AADSTS90072", StringComparison.Ordinal))
            return new("TenantNotFound", "The tenant was not found in the selected cloud.", detail, "Check the Tenant ID and the cloud environment.");
        if (message.Contains("AADSTS65001", StringComparison.Ordinal) || message.Contains("AADSTS650056", StringComparison.Ordinal))
            return new("ConsentRequired", "Admin consent is required.", detail, "Grant admin consent for the application's permissions.");
        if (exception is MsalServiceException { StatusCode: 0 } || exception.InnerException is HttpRequestException)
            return new("Network", "Microsoft Entra could not be reached.", detail, "Check the network connection and proxy settings.");
        return new("AuthenticationFailed", "Microsoft Entra sign-in failed.", detail, "Review the details and the application log.");
    }
}

/// <summary>App-only (client credential) token acquisition with MSAL. The secret is resolved only when needed.</summary>
public sealed class MsalMicrosoftAuthenticationService(
    ISecretStore secrets,
    IRedactionService redaction,
    ILogger<MsalMicrosoftAuthenticationService> logger) : IMicrosoftAuthenticationService
{
    public async Task<TenantAuthenticationResult> AcquireAppTokenAsync(Guid tenantId, Guid clientId, SecretReference clientSecret, CloudEnvironment cloud, CancellationToken cancellationToken)
    {
        try
        {
            var result = await AcquireAsync(secrets, tenantId, clientId, clientSecret, cloud, cancellationToken);
            var (tokenTenant, roles) = AccessTokenClaims.Read(result.AccessToken);
            logger.LogInformation("App-only token acquired for tenant {TenantId} client {ClientId} ({Cloud}); {RoleCount} application permission(s) granted",
                tenantId, clientId, cloud, roles.Count);
            return new TenantAuthenticationResult(true, tokenTenant ?? (Guid.TryParse(result.TenantId, out var parsed) ? parsed : null), roles, result.ExpiresOn, null);
        }
        catch (MsalException exception)
        {
            var error = MsalErrorTranslator.Translate(exception, redaction);
            logger.LogWarning("App-only token acquisition failed for tenant {TenantId} client {ClientId}: {Category} {ErrorCode}", tenantId, clientId, error.Category, exception.ErrorCode);
            return new TenantAuthenticationResult(false, null, [], null, error);
        }
        catch (SecretStoreException exception)
        {
            return new TenantAuthenticationResult(false, null, [], null, new OperationError("Credential", "The client secret could not be read from the secret store.", exception.Message, "Re-enter the client secret."));
        }
    }

    internal static async Task<AuthenticationResult> AcquireAsync(ISecretStore secrets, Guid tenantId, Guid clientId, SecretReference clientSecret, CloudEnvironment cloud, CancellationToken cancellationToken)
    {
        var secret = await secrets.GetSecretAsync(clientSecret, cancellationToken);
        if (string.IsNullOrEmpty(secret)) throw new MsalClientException("credential_missing", "The app credential is not stored on this workstation.");
        var endpoints = MicrosoftCloudEndpoints.For(cloud);
        var builder = ConfidentialClientApplicationBuilder.Create(clientId.ToString())
            .WithAuthority(new Uri(endpoints.Authority, tenantId.ToString()).ToString());
        // 4.0 beta.2: a stored certificate (PKCS#12, kept only in the encrypted secret store) or, for older registrations, a client secret.
        using var certificate = CertificateCredential.IsCertificate(secret) ? CertificateCredential.Load(secret) : null;
        var app = (certificate is null ? builder.WithClientSecret(secret) : builder.WithCertificate(certificate)).Build();
        return await app.AcquireTokenForClient([endpoints.GraphDefaultScope]).ExecuteAsync(cancellationToken);
    }
}
