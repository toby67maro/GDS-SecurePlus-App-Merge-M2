namespace EngineeringWorkspace.Application.Settings;

// Typed, immutable settings records. They are bound and validated by EngineeringWorkspace.Configuration from
// the layered INI files and registered in DI; consumers never read IConfiguration. Default values live in
// one place: EngineeringWorkspace.Configuration.Loading.CompiledDefaults.

/// <summary>[Application] in Application.ini.</summary>
public sealed record ApplicationSettings(
    string Id,
    string Name,
    string Title,
    string CompanyName,
    string Environment,
    string DefaultRoute,
    bool RestoreLastWorkspace,
    bool AllowMultipleInstances,
    string ApplicationVersion);

/// <summary>[Paths] in Application.ini, resolved to absolute, writable per-user locations.</summary>
public sealed record PathSettings(
    string ApplicationDirectory,
    string ConfigurationDirectory,
    string DataDirectory,
    string WorkspaceDirectory,
    string CustomerDirectory,
    string GoldenTenantDirectory,
    string ImportDirectory,
    string ExportDirectory,
    string LogDirectory,
    string AuditDirectory,
    string AssetDirectory,
    string CredentialDirectory,
    string StateDirectory);

/// <summary>[Workspace] in Application.ini.</summary>
public sealed record WorkspaceSettings(
    bool RequireWorkspace,
    bool CreateDirectoryIfMissing,
    bool RestoreLastWorkspace,
    bool AllowCustomerlessWorkspace,
    string? DefaultWorkspace);

/// <summary>[ImportExport] in Application.ini. Secrets are never exportable, so there is no switch for them.</summary>
public sealed record ImportExportSettings(
    string DefaultExportFormat,
    bool OverwriteExistingFiles,
    bool IncludeMetadata,
    bool TimestampFileNames);

/// <summary>[Credentials] in Application.ini.</summary>
public sealed record CredentialSettings(int ExpirationWarningDays, string SecretStoreProvider)
{
    public TimeSpan ExpirationWarningWindow => TimeSpan.FromDays(ExpirationWarningDays);
}

/// <summary>[Branding] in Branding.ini: product identity only. The look lives in ApplicationProperties.json.</summary>
public sealed record BrandingSettings(
    string ApplicationName,
    string ApplicationTitle,
    string CompanyName,
    string? LogoLight,
    string? LogoDark,
    string? WindowIcon);

/// <summary>[Logging] in Logging.ini.</summary>
public sealed record LoggingSettings(string MinimumLevel, int RetainedFileCountLimit, int FileSizeLimitMegabytes);

/// <summary>[Integrations:*] in Integrations.ini. Teams and ConnectWise API calls are M8.</summary>
public sealed record IntegrationSettings(
    bool TeamsEnabled,
    bool ConnectWiseEnabled,
    string? ConnectWiseSite,
    string? ConnectWiseCompanyId,
    string? AdministratorClientId,
    string AdministratorRedirectUri);

/// <summary>
/// [GoldenTenant] in GoldenTenant.ini: the policy sets of the control catalog that make up the Golden Tenant baseline
/// (default: the catalog's Golden-Tenant composition) and an optional reference customer tenant.
/// </summary>
public sealed record GoldenTenantSettings(
    bool Enabled,
    string? BaselineId,
    string? ConditionalAccessBaselineRepository,
    string? PolicySetsDirectory,
    IReadOnlyList<string> PolicySets,
    string? ReferenceCustomerId);

/// <summary>[Repositories] in Repositories.ini.</summary>
public sealed record RepositorySettings(
    string? DocumentsRepository,
    string? PolicyRepository,
    string? PolicySetsRepository,
    string? CustomerVariablesRepository,
    string? ScriptsRepository,
    bool AutoDiscover,
    bool ScanOnStartup);
