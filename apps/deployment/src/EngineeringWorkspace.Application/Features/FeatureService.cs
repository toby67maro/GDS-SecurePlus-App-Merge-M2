namespace EngineeringWorkspace.Application.Features;

/// <summary>Stable feature identifiers (keys of [Features] in Features.ini).</summary>
public static class FeatureIds
{
    public const string Dashboard = nameof(Dashboard);
    public const string Assessment = nameof(Assessment);
    public const string LiveAssessment = nameof(LiveAssessment);
    public const string Planning = nameof(Planning);
    public const string ScriptGenerator = nameof(ScriptGenerator);
    public const string ConfigFileGenerator = nameof(ConfigFileGenerator);
    public const string Deployment = nameof(Deployment);
    public const string Reporting = nameof(Reporting);
    public const string GoldenTenant = nameof(GoldenTenant);
    public const string CustomerProfiles = nameof(CustomerProfiles);
    public const string WorkspaceConfiguration = nameof(WorkspaceConfiguration);
    public const string TeamsIntegration = nameof(TeamsIntegration);
    public const string ConnectWiseIntegration = nameof(ConnectWiseIntegration);
    public const string DatabaseConfiguration = nameof(DatabaseConfiguration);
    public const string CredentialManagement = nameof(CredentialManagement);
    public const string Auditing = nameof(Auditing);
    public const string RepositoryCatalog = nameof(RepositoryCatalog);
    public const string JsonAutomation = nameof(JsonAutomation);
    public const string Appearance = nameof(Appearance);
    public const string ApplicationSettings = nameof(ApplicationSettings);

    public static IReadOnlyList<string> All { get; } =
    [
        Dashboard, Assessment, LiveAssessment, Planning, ScriptGenerator, ConfigFileGenerator, Deployment, Reporting,
        GoldenTenant, CustomerProfiles, WorkspaceConfiguration, TeamsIntegration, ConnectWiseIntegration,
        DatabaseConfiguration, CredentialManagement, Auditing, RepositoryCatalog, JsonAutomation, Appearance, ApplicationSettings
    ];
}

/// <summary>Resolved feature switches.</summary>
public sealed record FeatureSettings(IReadOnlyDictionary<string, bool> Features);

/// <summary>
/// Resolves stable feature IDs from typed configuration. Consumers depend only on this interface, so
/// configuration reload (planned for M2) can be added here without consumers touching configuration.
/// </summary>
public interface IFeatureService
{
    bool IsEnabled(string featureId);
    bool IsKnown(string featureId);
    IReadOnlyDictionary<string, bool> Snapshot { get; }
}

public sealed class FeatureService(FeatureSettings settings) : IFeatureService
{
    private readonly IReadOnlyDictionary<string, bool> _features =
        new Dictionary<string, bool>(settings.Features, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, bool> Snapshot => _features;

    public bool IsEnabled(string featureId) => _features.TryGetValue(featureId, out var enabled) && enabled;

    public bool IsKnown(string featureId) => _features.ContainsKey(featureId);
}
