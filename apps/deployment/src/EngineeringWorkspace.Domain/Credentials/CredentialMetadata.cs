using EngineeringWorkspace.Domain.Security;

namespace EngineeringWorkspace.Domain.Credentials;

/// <summary>The kind of secret a credential protects.</summary>
public enum CredentialType
{
    EntraClientSecret,
    Certificate,
    SqlLogin,
    ConnectWise,
    TeamsApi,
    ApiKey
}

/// <summary>Health of a credential as shown to engineers. Never derived from the secret value itself.</summary>
public enum CredentialStatus
{
    Configured,
    Missing,
    ExpiresSoon,
    Expired,
    Invalid
}

/// <summary>
/// Identity and lifecycle information about a credential. It never contains secret material; the secret
/// is resolved through <see cref="SecretReference"/> by the secret store only when an operation needs it.
/// </summary>
public sealed record CredentialMetadata(
    string CredentialId,
    string DisplayName,
    CredentialType CredentialType,
    string Provider,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ExpirationDateUtc,
    SecretReference SecretReference,
    string? AssociatedCustomerId = null,
    string? AssociatedTenantId = null)
{
    /// <summary>Expiration-based status; <see cref="CredentialStatus.Missing"/> must be determined by the secret store.</summary>
    public CredentialStatus EvaluateExpiration(DateTimeOffset nowUtc, TimeSpan warningWindow)
    {
        if (ExpirationDateUtc is null) return CredentialStatus.Configured;
        if (ExpirationDateUtc <= nowUtc) return CredentialStatus.Expired;
        return ExpirationDateUtc <= nowUtc.Add(warningWindow) ? CredentialStatus.ExpiresSoon : CredentialStatus.Configured;
    }
}
