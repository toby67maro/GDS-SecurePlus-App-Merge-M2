using System.Text.RegularExpressions;
using EngineeringWorkspace.Application.Configuration;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Features;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Configuration.Binding;
using EngineeringWorkspace.Configuration.Layering;
using EngineeringWorkspace.Domain.Common;

namespace EngineeringWorkspace.Configuration.Validation;

/// <summary>Stage 3 (options) and stage 4 (cross-section) validation.</summary>
public static partial class SettingsValidators
{
    public static IReadOnlyList<string> AllowedEnvironments { get; } = ["Development", "Test", "Staging", "Production"];
    public static IReadOnlyList<string> AllowedLogLevels { get; } = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico"];

    public static void ValidateOptions(BoundSettings settings, SettingsBinder b)
    {
        const ConfigurationValidationStage stage = ConfigurationValidationStage.Options;
        if (!ApplicationIdPattern().IsMatch(settings.Application.Id))
            b.Error("Application:Id", "InvalidApplicationId", "The application ID must be letters, digits and dots.", "for example SecurePlus.ModernWork", [], stage: stage);
        if (string.IsNullOrWhiteSpace(settings.Application.Name))
            b.Error("Application:Name", "Required", "The application name is required.", null, [], stage: stage);
        if (!AllowedEnvironments.Contains(settings.Application.Environment, StringComparer.Ordinal))
            b.Error("Application:Environment", "InvalidEnvironment", "The environment is not recognised.", null, AllowedEnvironments, stage: stage);
        if (!AllowedLogLevels.Contains(settings.Logging.MinimumLevel, StringComparer.OrdinalIgnoreCase))
            b.Error("Logging:MinimumLevel", "InvalidLogLevel", "The log level is not recognised.", null, AllowedLogLevels, stage: stage);
        if (!string.Equals(settings.ImportExport.DefaultExportFormat, "Json", StringComparison.OrdinalIgnoreCase))
            b.Error("ImportExport:DefaultExportFormat", "UnsupportedExportFormat", "Only JSON export is implemented.", null, ["Json"], stage: stage);
        if (b.Values.ContainsKey("ImportExport:IncludeSensitiveValues"))
            b.Error("ImportExport:IncludeSensitiveValues", "RemovedSetting", "IncludeSensitiveValues was removed: secrets are never exportable. Delete this key.", null, [],
                ConfigurationSeverity.Warning, stage);
        if (!string.Equals(settings.Credentials.SecretStoreProvider, "Dpapi", StringComparison.OrdinalIgnoreCase))
            b.Error("Credentials:SecretStoreProvider", "UnsupportedSecretStore", "Only the Windows DPAPI secret store is implemented (Azure Key Vault is designed for, not implemented).", null, ["Dpapi"], stage: stage);
        if (settings.Workspace.DefaultWorkspace is { } defaultWorkspace && !StableIdentifier.IsValid(defaultWorkspace))
            b.Error("Workspace:DefaultWorkspace", "InvalidWorkspaceId", "The default workspace is not a valid workspace identifier.", "lowercase letters, digits and hyphens", [], stage: stage);

        foreach (var error in DatabaseConfigurationValidator.Validate(settings.Database))
            b.Error($"Database:{error.Key}", "InvalidDatabaseSetting", error.Message, error.ExpectedFormat, [], stage: stage);

        foreach (var (key, path) in new[] { ("Branding:LogoLight", settings.Branding.LogoLight), ("Branding:LogoDark", settings.Branding.LogoDark), ("Branding:WindowIcon", settings.Branding.WindowIcon) })
        {
            if (path is null) continue;
            var allowed = key.EndsWith("WindowIcon", StringComparison.Ordinal) ? [".ico", ".png"] : ImageExtensions;
            if (!allowed.Contains(Path.GetExtension(path).ToLowerInvariant()))
                b.Error(key, "UnsupportedImageType", "The file type is not supported.", null, allowed, stage: stage);
        }

        foreach (var key in b.Values.Keys.Where(key => TenantIdentifierKey().IsMatch(key)))
        {
            var value = b.String(key);
            if (value.Length > 0 && !Guid.TryParseExact(value, "D", out _))
                b.Error(key, "InvalidTenantIdentifier", "Tenant, client and service principal IDs must be GUIDs.", "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx", [], stage: stage);
        }

        ValidateNavigation(settings, b, stage);
    }

    private static void ValidateNavigation(BoundSettings settings, SettingsBinder b, ConfigurationValidationStage stage)
    {
        var nodes = settings.Navigation.Nodes;
        foreach (var duplicate in nodes.GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            b.Error($"Navigation:Items:{duplicate.Key}", "DuplicateNavigationId", $"The navigation ID '{duplicate.Key}' is used by more than one node.", null, [], stage: stage);

        var containers = nodes.Where(node => node.Kind != NavigationNodeKind.Item).Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var known = RouteCatalog.All.Select(route => route.Route).ToHashSet(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var prefix = node.Kind switch
            {
                NavigationNodeKind.Section => $"Navigation:Sections:{node.Id}",
                NavigationNodeKind.Group => $"Navigation:Groups:{node.Id}",
                _ => $"Navigation:Items:{node.Id}"
            };
            if (node.Kind != NavigationNodeKind.Section && (node.ParentId is null || !containers.Contains(node.ParentId)))
                b.Error($"{prefix}:Parent", "UnknownNavigationParent", $"'{node.Id}' must have a Parent that is a configured section or group.", null, containers.Order().ToArray(), stage: stage);
            if (node.Kind == NavigationNodeKind.Section && node.ParentId is not null)
                b.Error($"{prefix}:Parent", "SectionWithParent", "Sections are top-level and cannot have a Parent.", null, [], stage: stage);
            if (node.Kind == NavigationNodeKind.Item)
            {
                if (node.Route is null)
                    b.Error($"{prefix}:Route", "MissingRoute", $"Navigation item '{node.Id}' has no Route.", "lowercase/slash-separated route", [], stage: stage);
                else if (!RoutePattern().IsMatch(node.Route))
                    b.Error($"{prefix}:Route", "InvalidRoute", "Routes are lowercase, slash-separated words.", "for example settings/appearance", [], stage: stage);
                else if (!known.Contains(node.Route))
                    // A route removed or renamed in a later version must never stop the application: the item is skipped.
                    b.Error($"{prefix}:Route", "UnknownRoute", $"The route '{node.Route}' is not registered by the application; the menu item is not shown.", null, [],
                        ConfigurationSeverity.Warning, stage);
            }

            if (node.RequiredFeature is not null && !settings.Features.Features.ContainsKey(node.RequiredFeature))
                b.Error($"{prefix}:RequiredFeature", "UnknownFeature", $"The feature '{node.RequiredFeature}' is not defined in Features.ini.", null, FeatureIds.All, stage: stage);
        }

        foreach (var duplicate in nodes.Where(node => node.Route is not null).GroupBy(node => node.Route!, StringComparer.Ordinal).Where(group => group.Count() > 1))
            b.Error($"Navigation:Items:{duplicate.Skip(1).First().Id}:Route", "DuplicateRoute", $"The route '{duplicate.Key}' is used by more than one navigation item.", null, [], stage: stage);
    }

    public static void ValidateCrossSection(BoundSettings settings, SettingsBinder b, IRedactionService redaction)
    {
        const ConfigurationValidationStage stage = ConfigurationValidationStage.CrossSection;
        var features = new FeatureService(settings.Features);
        var tree = NavigationResolver.Resolve(settings.Navigation, new RouteRegistry(RouteCatalog.All), features, hasWorkspace: true, hasCustomer: true);
        var defaultItem = NavigationResolver.Items(tree).FirstOrDefault(item => item.Route == settings.Application.DefaultRoute);
        if (defaultItem is null)
            b.Error("Application:DefaultRoute", "DefaultRouteUnavailable", $"The default route '{settings.Application.DefaultRoute}' is not a registered, visible navigation item.", null,
                NavigationResolver.Items(tree).Select(item => item.Route).ToArray(), stage: stage);
        else if (!defaultItem.IsEnabled)
            b.Error("Application:DefaultRoute", "DefaultRouteDisabled", $"The default route is disabled: {defaultItem.DisabledReason}", null, [], stage: stage);

        foreach (var featureKey in b.Values.Keys.Where(key => key.StartsWith("Features:", StringComparison.OrdinalIgnoreCase)))
        {
            var id = featureKey["Features:".Length..];
            if (!FeatureIds.All.Contains(id, StringComparer.OrdinalIgnoreCase))
                b.Error(featureKey, "UnknownFeature", $"'{id}' is not a feature this application knows.", null, FeatureIds.All, ConfigurationSeverity.Warning, stage);
        }

        if (features.IsEnabled(FeatureIds.TeamsIntegration) && !settings.Integrations.TeamsEnabled)
            b.Error("Features:TeamsIntegration", "IntegrationDisabled", "The Teams Integration feature is on but [Integrations:Teams] Enabled=false.", null, [], stage: stage);
        if (features.IsEnabled(FeatureIds.ConnectWiseIntegration) && !settings.Integrations.ConnectWiseEnabled)
            b.Error("Features:ConnectWiseIntegration", "IntegrationDisabled", "The ConnectWise Integration feature is on but [Integrations:ConnectWise] Enabled=false.", null, [], stage: stage);
        if (settings.GoldenTenant.Enabled && settings.GoldenTenant.BaselineId is null)
            b.Error("GoldenTenant:BaselineId", "GoldenTenantIncomplete", "Golden Tenant is enabled but no BaselineId is configured.", null, [], stage: stage);

        foreach (var value in b.Values.Values.Where(value => value.Layer.Kind != ConfigurationSourceKind.CompiledDefaults && !string.IsNullOrEmpty(value.Value)))
        {
            if (redaction.IsSecretKey(value.Key) || PlaintextConnectionSecret().IsMatch(value.Value!))
                b.Error(value.Key, "PlaintextSecret", "Secret values are not allowed in configuration. Store the secret in Credential Management and configure its reference (for example ClientSecretReference=os:customer/contoso/graph-client-secret).",
                    "a secret reference in a *Reference key", [], stage: stage);
        }

        if (settings.Workspace.DefaultWorkspace is { } startup && StableIdentifier.IsValid(startup) && !settings.Workspace.CreateDirectoryIfMissing &&
            !Directory.Exists(Path.Combine(settings.Paths.WorkspaceDirectory, startup)))
            b.Error("Workspace:DefaultWorkspace", "StartupWorkspaceMissing", $"The startup workspace '{startup}' does not exist and CreateDirectoryIfMissing=false.", null, [], stage: stage);

        if (IsInsideGitWorkingTree(settings.Paths.CredentialDirectory))
            b.Error("Paths:CredentialDirectory", "CredentialStoreInRepository", "The credential store directory is inside a Git working tree. Keep secrets outside source control.", "a per-user path such as ./data/Credentials under %LOCALAPPDATA%", [], stage: stage);

        if (string.Equals(settings.Application.Environment, "Production", StringComparison.Ordinal) &&
            (settings.Database.AutomaticDatabaseInitialization || settings.Database.AutomaticSchemaUpgrade))
            b.Error("Database:AutomaticSchemaUpgrade", "IgnoredInProduction", "Automatic database initialization and schema upgrade are never honoured in Production.", null, [], ConfigurationSeverity.Warning, stage);

        foreach (var moved in new[] { "Branding:DefaultTheme", "Branding:AllowThemeSelection" }.Where(b.Values.ContainsKey)
                     .Concat(b.Values.Keys.Where(key => key.StartsWith("Branding.Colors:", StringComparison.OrdinalIgnoreCase)).Take(1)))
            b.Error(moved, "MovedToApplicationProperties", "Theme mode, user selection and colors moved to ApplicationProperties.json; this Branding.ini value is ignored.", null, [],
                ConfigurationSeverity.Warning, stage);

        foreach (var (key, path) in new[] { ("Branding:LogoLight", settings.Branding.LogoLight), ("Branding:LogoDark", settings.Branding.LogoDark), ("Branding:WindowIcon", settings.Branding.WindowIcon) })
        {
            if (path is not null && !File.Exists(path))
                b.Error(key, "BrandingAssetMissing", "The branding image does not exist; the application continues without it.", null, [], ConfigurationSeverity.Warning, stage);
        }
    }

    private static bool IsInsideGitWorkingTree(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git"))) return true;
        }

        return false;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9.]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex ApplicationIdPattern();

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*(?:/[a-z0-9]+(?:-[a-z0-9]+)*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex RoutePattern();

    [GeneratedRegex(@"^Tenant:[^:]+:(TenantId|ClientId|ServicePrincipalId)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TenantIdentifierKey();

    [GeneratedRegex(@"\b(Password|Pwd|client_secret)\s*=\s*[^;\s]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaintextConnectionSecret();
}
