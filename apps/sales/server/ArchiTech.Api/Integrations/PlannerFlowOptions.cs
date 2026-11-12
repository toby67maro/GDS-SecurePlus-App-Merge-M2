using Microsoft.AspNetCore.WebUtilities;

namespace ArchiTech.Api.Integrations;

public sealed class PlannerFlowOptions
{
    public string? Endpoint { get; set; }
    public string AuthenticationMode { get; set; } = "Disabled";
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? CertificatePath { get; set; }
    public string? CertificatePassword { get; set; }
    public string? CertificateThumbprint { get; set; }
    public string? AllowedHost { get; set; }

    public bool IsConfigured => Validate(out _) && !string.IsNullOrWhiteSpace(Endpoint);

    public bool Validate(out string error)
    {
        error = "";
        if (AuthenticationMode == "Disabled")
        {
            if (string.IsNullOrWhiteSpace(Endpoint)) return true;
            error = "PlannerFlow:Endpoint requires an explicit authentication mode.";
            return false;
        }

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            error = "PlannerFlow:Endpoint must be an absolute HTTPS URL without user info or a fragment.";
            return false;
        }
        if (AuthenticationMode == "SignedUrlTest") return true;
        if (AuthenticationMode != "EntraCertificate")
        {
            error = "PlannerFlow:AuthenticationMode must be Disabled, SignedUrlTest, or EntraCertificate.";
            return false;
        }
        if (QueryHelpers.ParseQuery(endpoint.Query).ContainsKey("sig"))
        {
            error = "EntraCertificate cannot use a signed trigger URL; configure the authenticated trigger URL.";
            return false;
        }
        if (!Guid.TryParse(TenantId, out var tenant) || tenant == Guid.Empty ||
            !Guid.TryParse(ClientId, out var client) || client == Guid.Empty ||
            (string.IsNullOrWhiteSpace(CertificatePath) == string.IsNullOrWhiteSpace(CertificateThumbprint)))
        {
            error = "EntraCertificate requires TenantId, ClientId, and exactly one of CertificatePath or CertificateThumbprint.";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(CertificateThumbprint) &&
            (CertificateThumbprint.Length != 40 || !CertificateThumbprint.All(Uri.IsHexDigit)))
        {
            error = "PlannerFlow:CertificateThumbprint must be a 40-character hexadecimal certificate thumbprint.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(AllowedHost) ||
            !string.Equals(endpoint.IdnHost, AllowedHost, StringComparison.OrdinalIgnoreCase) ||
            !endpoint.IdnHost.EndsWith(".environment.api.powerplatform.com", StringComparison.OrdinalIgnoreCase) ||
            !endpoint.IsDefaultPort)
        {
            error = "EntraCertificate requires the exact Power Platform environment trigger host in PlannerFlow:AllowedHost.";
            return false;
        }
        return true;
    }
}
