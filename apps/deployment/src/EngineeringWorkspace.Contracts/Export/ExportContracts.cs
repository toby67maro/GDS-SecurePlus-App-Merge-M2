namespace EngineeringWorkspace.Contracts.Export;

/// <summary>Schema identifiers of the versioned import/export contracts.</summary>
public static class ContractVersions
{
    public const string CustomerProfileV1 = "secureplus.customer-profile/1";
    public const string WorkspaceConfigurationV1 = "secureplus.workspace-configuration/1";
}

/// <summary>
/// Customer profile export, version 1. By design it has no field that can carry a client secret, token,
/// SQL password or private key: credentials travel only as <see cref="CredentialReferenceV1"/> metadata.
/// Importing a profile whose secret reference does not exist on the workstation reports "Credential Required".
/// </summary>
public sealed record CustomerProfileExportV1(
    string Schema,
    DateTimeOffset ExportedAtUtc,
    string ExportedByApplicationVersion,
    string CustomerId,
    string CustomerName,
    IReadOnlyList<TenantExportV1> Tenants,
    IReadOnlyList<CredentialReferenceV1> Credentials,
    IReadOnlyDictionary<string, string> IntegrationSettings);

public sealed record TenantExportV1(
    string TenantKey,
    string DisplayName,
    string? TenantName,
    string? TenantId,
    string? PrimaryDomain,
    string CloudEnvironment,
    string? ClientId,
    string? ServicePrincipalId,
    string? AuthenticationType,
    string? SecretReference);

/// <summary>Credential metadata that may be exported. Never contains the secret.</summary>
public sealed record CredentialReferenceV1(
    string CredentialId,
    string DisplayName,
    string CredentialType,
    string Provider,
    string SecretReference,
    DateTimeOffset? ExpirationDateUtc,
    string? AssociatedCustomerId,
    string? AssociatedTenantId);

/// <summary>
/// Workspace configuration export, version 1: non-secret settings as section/key/value rows. Secret-bearing keys
/// (passwords, client secrets, certificates, tokens) are never written; an import refuses them.
/// </summary>
public sealed record WorkspaceConfigurationExportV1(
    string Schema,
    DateTimeOffset ExportedAtUtc,
    string ExportedByApplicationVersion,
    string WorkspaceId,
    IReadOnlyList<ConfigurationValueV1> Settings);

/// <summary>One setting. <paramref name="File"/> names the INI file that holds it (optional; inferred from the section when absent).</summary>
public sealed record ConfigurationValueV1(string Section, string Key, string? Value, string? File = null);
