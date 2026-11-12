using System.Globalization;
using EngineeringWorkspace.Application.Features;
using EngineeringWorkspace.Application.Navigation;

namespace EngineeringWorkspace.Configuration.Loading;

/// <summary>
/// Compiled defaults: the lowest configuration layer and the single place where default values are defined.
/// A missing optional setting takes its value from here; a malformed explicit value is an error.
/// </summary>
public static class CompiledDefaults
{
    public static IReadOnlyDictionary<string, string?> Values { get; } = Build();

    private static Dictionary<string, string?> Build()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Application:Id"] = "SecurePlus.ModernWork",
            ["Application:Name"] = "Secure Plus Agent",
            ["Application:Title"] = "Secure Plus Agent",
            ["Application:CompanyName"] = "Global Data Systems",
            ["Application:Environment"] = "Development",
            ["Application:DefaultRoute"] = Routes.DashboardOverview,
            ["Application:RestoreLastWorkspace"] = "true",
            ["Application:AllowMultipleInstances"] = "false",

            ["Paths:ConfigurationDirectory"] = "./config",
            ["Paths:DataDirectory"] = "./data",
            ["Paths:WorkspaceDirectory"] = "./data/Workspaces",
            ["Paths:CustomerDirectory"] = "./data/Customers",
            ["Paths:GoldenTenantDirectory"] = "./data/GoldenTenants",
            ["Paths:ImportDirectory"] = "./data/Imports",
            ["Paths:ExportDirectory"] = "./data/Exports",
            ["Paths:LogDirectory"] = "./data/Logs",
            ["Paths:AuditDirectory"] = "./data/Audit",
            ["Paths:AssetDirectory"] = "./assets",
            ["Paths:CredentialDirectory"] = "./data/Credentials",
            ["Paths:StateDirectory"] = "./data/State",

            ["Workspace:RequireWorkspace"] = "false",
            ["Workspace:CreateDirectoryIfMissing"] = "true",
            ["Workspace:RestoreLastWorkspace"] = "true",
            ["Workspace:AllowCustomerlessWorkspace"] = "true",
            ["Workspace:DefaultWorkspace"] = string.Empty,

            ["ImportExport:DefaultExportFormat"] = "Json",
            ["ImportExport:OverwriteExistingFiles"] = "false",
            ["ImportExport:IncludeMetadata"] = "true",
            ["ImportExport:TimestampFileNames"] = "true",

            ["Credentials:ExpirationWarningDays"] = "30",
            ["Credentials:SecretStoreProvider"] = "Dpapi",

            ["Branding:ApplicationName"] = "Secure Plus Agent",
            ["Branding:ApplicationTitle"] = "Secure Plus Agent",
            ["Branding:CompanyName"] = "Global Data Systems",
            ["Branding:LogoLight"] = "./assets/branding/logo-light.png",
            ["Branding:LogoDark"] = "./assets/branding/logo-dark.png",
            ["Branding:WindowIcon"] = "./assets/icons/application.ico",

            ["Logging:MinimumLevel"] = "Information",
            ["Logging:RetainedFileCountLimit"] = "14",
            ["Logging:FileSizeLimitMegabytes"] = "20",

            ["Integrations:Teams:Enabled"] = "false",
            ["Integrations:ConnectWise:Enabled"] = "false",
            ["Integrations:ConnectWise:Site"] = string.Empty,
            ["Integrations:ConnectWise:CompanyId"] = string.Empty,
            ["Integrations:Microsoft:AdministratorClientId"] = string.Empty,
            ["Integrations:Microsoft:AdministratorRedirectUri"] = "http://localhost",

            ["GoldenTenant:Enabled"] = "false",
            ["GoldenTenant:BaselineId"] = string.Empty,
            ["GoldenTenant:ConditionalAccessBaselineRepository"] = string.Empty,
            ["GoldenTenant:PolicySetsDirectory"] = string.Empty,
            ["GoldenTenant:PolicySets"] = "Golden-Tenant",
            ["GoldenTenant:ReferenceCustomerId"] = string.Empty,

            ["Repositories:DocumentsRepository"] = string.Empty,
            ["Repositories:PolicyRepository"] = string.Empty,
            ["Repositories:PolicySetsRepository"] = string.Empty,
            ["Repositories:CustomerVariablesRepository"] = string.Empty,
            ["Repositories:ScriptsRepository"] = string.Empty,
            ["Repositories:AutoDiscover"] = "true",
            ["Repositories:ScanOnStartup"] = "false",

            ["Database:Provider"] = "None",
            ["Database:ServerName"] = string.Empty,
            ["Database:SqlInstance"] = string.Empty,
            ["Database:Port"] = string.Empty,
            ["Database:DatabaseName"] = string.Empty,
            ["Database:Authentication"] = "None",
            ["Database:UserName"] = string.Empty,
            ["Database:PasswordSecretReference"] = string.Empty,
            ["Database:EntraClientId"] = string.Empty,
            ["Database:EntraTenantId"] = string.Empty,
            ["Database:EntraClientSecretReference"] = string.Empty,
            ["Database:ManagedIdentityClientId"] = string.Empty,
            ["Database:EncryptConnection"] = "true",
            ["Database:TrustServerCertificate"] = "false",
            ["Database:ConnectionTimeoutSeconds"] = "30",
            ["Database:CommandTimeoutSeconds"] = "60",
            ["Database:RetryCount"] = "3",
            ["Database:RetryDelaySeconds"] = "10",
            ["Database:ConnectionStringSecretReference"] = string.Empty,
            ["Database:AutomaticDatabaseInitialization"] = "false",
            ["Database:AutomaticSchemaUpgrade"] = "false",
        };

        foreach (var feature in FeatureIds.All)
            values[$"Features:{feature}"] = feature is FeatureIds.TeamsIntegration or FeatureIds.ConnectWiseIntegration ? "false" : "true";

        foreach (var node in RouteCatalog.DefaultNavigation)
        {
            var prefix = node.Kind switch
            {
                NavigationNodeKind.Section => $"Navigation:Sections:{node.Id}",
                NavigationNodeKind.Group => $"Navigation:Groups:{node.Id}",
                _ => $"Navigation:Items:{node.Id}"
            };
            values[$"{prefix}:DisplayName"] = node.DisplayName;
            values[$"{prefix}:SortOrder"] = node.SortOrder.ToString(CultureInfo.InvariantCulture);
            if (node.ParentId is not null) values[$"{prefix}:Parent"] = node.ParentId;
            if (node.IconKey is not null) values[$"{prefix}:IconKey"] = node.IconKey;
            if (node.Description is not null) values[$"{prefix}:Description"] = node.Description;
            if (node.RequiredFeature is not null) values[$"{prefix}:RequiredFeature"] = node.RequiredFeature;
            if (node.Route is not null) values[$"{prefix}:Route"] = node.Route;
            if (node.IsDefault) values[$"{prefix}:IsDefault"] = "true";
            if (node.RequiresCustomer) values[$"{prefix}:RequiresCustomer"] = "true";
            if (node.RequiresWorkspace) values[$"{prefix}:RequiresWorkspace"] = "true";
            if (node.RequiresAdvancedMode) values[$"{prefix}:RequiresAdvancedMode"] = "true";
        }

        return values;
    }
}
