using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Features;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Configuration.Layering;

namespace EngineeringWorkspace.Configuration.Binding;

/// <summary>All typed settings bound from the effective configuration.</summary>
public sealed record BoundSettings(
    ApplicationSettings Application,
    PathSettings Paths,
    WorkspaceSettings Workspace,
    ImportExportSettings ImportExport,
    CredentialSettings Credentials,
    BrandingSettings Branding,
    FeatureSettings Features,
    NavigationDefinition Navigation,
    IntegrationSettings Integrations,
    DatabaseSettings Database,
    LoggingSettings Logging,
    GoldenTenantSettings GoldenTenant,
    RepositorySettings Repositories);

/// <summary>Stage 2: binds every settings record. Paths are resolved against the per-user data root.</summary>
public static class SettingsBinding
{
    public static BoundSettings Bind(SettingsBinder b, string applicationDirectory, string dataRoot, string configurationDirectory, string applicationVersion)
    {
        var application = new ApplicationSettings(
            b.String("Application:Id"), b.String("Application:Name"), b.String("Application:Title"), b.String("Application:CompanyName"),
            b.String("Application:Environment"), b.String("Application:DefaultRoute"), b.Bool("Application:RestoreLastWorkspace"),
            b.Bool("Application:AllowMultipleInstances"), applicationVersion);

        var paths = new PathSettings(
            applicationDirectory,
            configurationDirectory,
            b.Path("Paths:DataDirectory", dataRoot),
            b.Path("Paths:WorkspaceDirectory", dataRoot),
            b.Path("Paths:CustomerDirectory", dataRoot),
            b.Path("Paths:GoldenTenantDirectory", dataRoot),
            b.Path("Paths:ImportDirectory", dataRoot),
            b.Path("Paths:ExportDirectory", dataRoot),
            b.Path("Paths:LogDirectory", dataRoot),
            b.Path("Paths:AuditDirectory", dataRoot),
            b.Path("Paths:AssetDirectory", applicationDirectory),
            b.Path("Paths:CredentialDirectory", dataRoot),
            b.Path("Paths:StateDirectory", dataRoot));

        var workspace = new WorkspaceSettings(b.Bool("Workspace:RequireWorkspace"), b.Bool("Workspace:CreateDirectoryIfMissing"),
            b.Bool("Workspace:RestoreLastWorkspace") && application.RestoreLastWorkspace, b.Bool("Workspace:AllowCustomerlessWorkspace"),
            b.OptionalString("Workspace:DefaultWorkspace"));

        var importExport = new ImportExportSettings(b.String("ImportExport:DefaultExportFormat"), b.Bool("ImportExport:OverwriteExistingFiles"),
            b.Bool("ImportExport:IncludeMetadata"), b.Bool("ImportExport:TimestampFileNames"));

        var credentials = new CredentialSettings(b.Int("Credentials:ExpirationWarningDays", 1, 365), b.String("Credentials:SecretStoreProvider"));

        var branding = new BrandingSettings(b.String("Branding:ApplicationName"), b.String("Branding:ApplicationTitle"), b.String("Branding:CompanyName"),
            b.OptionalPath("Branding:LogoLight", applicationDirectory), b.OptionalPath("Branding:LogoDark", applicationDirectory),
            b.OptionalPath("Branding:WindowIcon", applicationDirectory));

        var features = new FeatureSettings(b.ChildIds("Features").ToDictionary(id => id, id => b.Bool($"Features:{id}"), StringComparer.OrdinalIgnoreCase));

        var integrations = new IntegrationSettings(b.Bool("Integrations:Teams:Enabled"), b.Bool("Integrations:ConnectWise:Enabled"),
            b.OptionalString("Integrations:ConnectWise:Site"), b.OptionalString("Integrations:ConnectWise:CompanyId"),
            b.OptionalString("Integrations:Microsoft:AdministratorClientId"), b.Uri("Integrations:Microsoft:AdministratorRedirectUri", UriKind.Absolute).ToString());

        var database = new DatabaseSettings
        {
            Provider = b.Enum<DatabaseProvider>("Database:Provider"),
            ServerName = b.String("Database:ServerName"),
            SqlInstance = b.OptionalString("Database:SqlInstance"),
            Port = b.OptionalInt("Database:Port", 1, 65535),
            DatabaseName = b.String("Database:DatabaseName"),
            Authentication = b.Enum<DatabaseAuthenticationMethod>("Database:Authentication"),
            UserName = b.OptionalString("Database:UserName"),
            PasswordSecretReference = b.OptionalSecretReference("Database:PasswordSecretReference"),
            EntraClientId = b.OptionalString("Database:EntraClientId"),
            EntraTenantId = b.OptionalString("Database:EntraTenantId"),
            EntraClientSecretReference = b.OptionalSecretReference("Database:EntraClientSecretReference"),
            ManagedIdentityClientId = b.OptionalString("Database:ManagedIdentityClientId"),
            EncryptConnection = b.Bool("Database:EncryptConnection"),
            TrustServerCertificate = b.Bool("Database:TrustServerCertificate"),
            ConnectionTimeoutSeconds = b.Int("Database:ConnectionTimeoutSeconds", 1, 300),
            CommandTimeoutSeconds = b.Int("Database:CommandTimeoutSeconds", 1, 3600),
            RetryCount = b.Int("Database:RetryCount", 0, 255),
            RetryDelaySeconds = b.Int("Database:RetryDelaySeconds", 1, 60),
            ConnectionStringSecretReference = b.OptionalSecretReference("Database:ConnectionStringSecretReference"),
            AutomaticDatabaseInitialization = b.Bool("Database:AutomaticDatabaseInitialization"),
            AutomaticSchemaUpgrade = b.Bool("Database:AutomaticSchemaUpgrade"),
        };

        var logging = new LoggingSettings(b.String("Logging:MinimumLevel"), b.Int("Logging:RetainedFileCountLimit", 1, 365), b.Int("Logging:FileSizeLimitMegabytes", 1, 1024));

        var goldenTenant = new GoldenTenantSettings(b.Bool("GoldenTenant:Enabled"), b.OptionalString("GoldenTenant:BaselineId"),
            b.OptionalString("GoldenTenant:ConditionalAccessBaselineRepository"), b.OptionalString("GoldenTenant:PolicySetsDirectory"),
            (b.OptionalString("GoldenTenant:PolicySets") ?? "Golden-Tenant").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            b.OptionalString("GoldenTenant:ReferenceCustomerId"));

        var repositories = new RepositorySettings(b.OptionalString("Repositories:DocumentsRepository"), b.OptionalString("Repositories:PolicyRepository"),
            b.OptionalString("Repositories:PolicySetsRepository"), b.OptionalString("Repositories:CustomerVariablesRepository"),
            b.OptionalString("Repositories:ScriptsRepository"), b.Bool("Repositories:AutoDiscover"), b.Bool("Repositories:ScanOnStartup"));

        return new BoundSettings(application, paths, workspace, importExport, credentials, branding, features, BindNavigation(b), integrations,
            database, logging, goldenTenant, repositories);
    }

    private static NavigationDefinition BindNavigation(SettingsBinder b)
    {
        var nodes = new List<NavigationNodeDefinition>();
        foreach (var (collection, kind) in new[] { ("Navigation:Sections", NavigationNodeKind.Section), ("Navigation:Groups", NavigationNodeKind.Group), ("Navigation:Items", NavigationNodeKind.Item) })
        {
            foreach (var id in b.ChildIds(collection))
            {
                var prefix = $"{collection}:{id}";
                if (b.Values.ContainsKey($"{prefix}:{CollectionMergePolicy.RemovedKey}") && b.Bool($"{prefix}:{CollectionMergePolicy.RemovedKey}")) continue;
                nodes.Add(new NavigationNodeDefinition
                {
                    Kind = kind,
                    Id = id,
                    ParentId = b.OptionalString($"{prefix}:Parent"),
                    DisplayName = b.OptionalString($"{prefix}:DisplayName") ?? id,
                    Description = b.OptionalString($"{prefix}:Description"),
                    IconKey = b.OptionalString($"{prefix}:IconKey"),
                    SortOrder = b.Values.ContainsKey($"{prefix}:SortOrder") ? b.Int($"{prefix}:SortOrder", 0, 100000) : 0,
                    IsVisible = !b.Values.ContainsKey($"{prefix}:IsVisible") || b.Bool($"{prefix}:IsVisible"),
                    IsEnabled = !b.Values.ContainsKey($"{prefix}:IsEnabled") || b.Bool($"{prefix}:IsEnabled"),
                    RequiredFeature = b.OptionalString($"{prefix}:RequiredFeature"),
                    ExpandedByDefault = !b.Values.ContainsKey($"{prefix}:ExpandedByDefault") || b.Bool($"{prefix}:ExpandedByDefault"),
                    CanCollapse = !b.Values.ContainsKey($"{prefix}:CanCollapse") || b.Bool($"{prefix}:CanCollapse"),
                    ShowHeader = !b.Values.ContainsKey($"{prefix}:ShowHeader") || b.Bool($"{prefix}:ShowHeader"),
                    Route = b.OptionalString($"{prefix}:Route"),
                    IsDefault = b.Values.ContainsKey($"{prefix}:IsDefault") && b.Bool($"{prefix}:IsDefault"),
                    RequiresWorkspace = b.Values.ContainsKey($"{prefix}:RequiresWorkspace") && b.Bool($"{prefix}:RequiresWorkspace"),
                    RequiresCustomer = b.Values.ContainsKey($"{prefix}:RequiresCustomer") && b.Bool($"{prefix}:RequiresCustomer"),
                    RequiredCapability = b.OptionalString($"{prefix}:RequiredCapability"),
                    RequiresAdvancedMode = b.Values.ContainsKey($"{prefix}:RequiresAdvancedMode") && b.Bool($"{prefix}:RequiresAdvancedMode"),
                });
            }
        }

        return new NavigationDefinition(nodes);
    }
}
