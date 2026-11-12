using EngineeringWorkspace.Application.Configuration;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Features;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Configuration.Validation;

namespace EngineeringWorkspace.Configuration.Snapshot;

/// <summary>The immutable, validated effective configuration registered in DI.</summary>
public interface IConfigurationSnapshot : IConfigurationDiagnostics
{
    ApplicationSettings Application { get; }
    PathSettings Paths { get; }
    WorkspaceSettings Workspace { get; }
    ImportExportSettings ImportExport { get; }
    CredentialSettings Credentials { get; }
    BrandingSettings Branding { get; }
    FeatureSettings Features { get; }
    NavigationDefinition Navigation { get; }
    IntegrationSettings Integrations { get; }
    DatabaseSettings Database { get; }
    LoggingSettings Logging { get; }
    GoldenTenantSettings GoldenTenant { get; }
    RepositorySettings Repositories { get; }
    IReadOnlyList<ConfigurationValidationError> ValidationErrors { get; }
}

public sealed class ConfigurationSnapshot : IConfigurationSnapshot
{
    public required ApplicationSettings Application { get; init; }
    public required PathSettings Paths { get; init; }
    public required WorkspaceSettings Workspace { get; init; }
    public required ImportExportSettings ImportExport { get; init; }
    public required CredentialSettings Credentials { get; init; }
    public required BrandingSettings Branding { get; init; }
    public required FeatureSettings Features { get; init; }
    public required NavigationDefinition Navigation { get; init; }
    public required IntegrationSettings Integrations { get; init; }
    public required DatabaseSettings Database { get; init; }
    public required LoggingSettings Logging { get; init; }
    public required GoldenTenantSettings GoldenTenant { get; init; }
    public required RepositorySettings Repositories { get; init; }
    public required IReadOnlyList<ConfigurationValidationError> ValidationErrors { get; init; }
    public required DateTimeOffset LoadedAtUtc { get; init; }
    public required IReadOnlyList<ConfigurationSourceDiagnostic> Sources { get; init; }
    public required IReadOnlyList<ConfigurationEntryDiagnostic> Entries { get; init; }
    public required IReadOnlyList<ConfigurationIssue> Issues { get; init; }
    public bool HasErrors => Issues.Any(issue => issue.Severity == ConfigurationSeverity.Error);
}
