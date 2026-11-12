namespace EngineeringWorkspace.Configuration.Layering;

public enum ConfigurationSourceKind
{
    CompiledDefaults,
    IniFile,
    Database,
    EnvironmentVariables,
    CommandLine
}

/// <summary>Where a file layer's path is resolved from.</summary>
public enum ConfigurationFileRoot
{
    None,
    ConfigurationDirectory,
    WorkspaceDirectory,
    CustomerDirectory
}

/// <summary>
/// One configuration layer. Higher <see cref="Order"/> wins. <see cref="PathTemplate"/> may contain
/// {Environment}, {Workspace} or {Customer}; a layer whose token cannot be resolved is skipped.
/// </summary>
public sealed record ConfigurationLayer(
    int Order,
    string Name,
    ConfigurationSourceKind Kind,
    ConfigurationFileRoot Root,
    string? PathTemplate,
    bool Required);

/// <summary>A file layer resolved to a concrete path.</summary>
public sealed record ConfigurationFileDescriptor(ConfigurationLayer Layer, string Path);

/// <summary>
/// The single definition of configuration precedence, lowest to highest. Add a layer by adding a row; nothing
/// else needs to change. Repositories.ini is a Secure Plus file kept from the foundation milestone and sits
/// with the other application-level files.
/// </summary>
public static class ConfigurationPrecedence
{
    public static IReadOnlyList<ConfigurationLayer> Layers { get; } =
    [
        new(10, "Compiled defaults", ConfigurationSourceKind.CompiledDefaults, ConfigurationFileRoot.None, null, false),
        new(20, "Application.ini", ConfigurationSourceKind.IniFile, ConfigurationFileRoot.ConfigurationDirectory, "Application.ini", true),
        new(30, "Branding.ini", ConfigurationSourceKind.IniFile, ConfigurationFileRoot.ConfigurationDirectory, "Branding.ini", false),
        new(40, "Features.ini", ConfigurationSourceKind.IniFile, ConfigurationFileRoot.ConfigurationDirectory, "Features.ini", false),
        new(50, "Navigation.ini", ConfigurationSourceKind.IniFile, ConfigurationFileRoot.ConfigurationDirectory, "Navigation.ini", false),
        new(55, "Repositories.ini", ConfigurationSourceKind.IniFile, ConfigurationFileRoot.ConfigurationDirectory, "Repositories.ini", false),
        new(60, "Integrations.ini", ConfigurationSourceKind.IniFile, ConfigurationFileRoot.ConfigurationDirectory, "Integrations.ini", false),
        new(70, "Database.ini", ConfigurationSourceKind.IniFile, ConfigurationFileRoot.ConfigurationDirectory, "Database.ini", false),
        new(80, "Logging.ini", ConfigurationSourceKind.IniFile, ConfigurationFileRoot.ConfigurationDirectory, "Logging.ini", false),
        new(90, "GoldenTenant.ini", ConfigurationSourceKind.IniFile, ConfigurationFileRoot.ConfigurationDirectory, "GoldenTenant.ini", false),
        new(100, "Environment", ConfigurationSourceKind.IniFile, ConfigurationFileRoot.ConfigurationDirectory, "Environments/{Environment}.ini", false),
        // Release 2.1: settings live in the SecurePlus database. Shared settings apply to every computer; this computer's
        // settings (paths, logging) override them. The INI files above then hold only the shipped defaults.
        new(104, "Shared settings", ConfigurationSourceKind.Database, ConfigurationFileRoot.None, null, false),
        new(106, "This computer's settings", ConfigurationSourceKind.Database, ConfigurationFileRoot.None, null, false),
        new(110, "Workspace.ini", ConfigurationSourceKind.IniFile, ConfigurationFileRoot.WorkspaceDirectory, "{Workspace}/Workspace.ini", false),
        new(120, "Customer.ini", ConfigurationSourceKind.IniFile, ConfigurationFileRoot.CustomerDirectory, "{Customer}/Customer.ini", false),
        new(130, "Overrides.ini", ConfigurationSourceKind.IniFile, ConfigurationFileRoot.CustomerDirectory, "{Customer}/Overrides.ini", false),
        new(140, "Environment variables", ConfigurationSourceKind.EnvironmentVariables, ConfigurationFileRoot.None, null, false),
        new(150, "Command line", ConfigurationSourceKind.CommandLine, ConfigurationFileRoot.None, null, false),
    ];

    /// <summary>Prefix of environment variables. ENGINEERINGWORKSPACE_DATABASE__PROVIDER maps to Database:Provider.</summary>
    public const string EnvironmentVariablePrefix = "ENGINEERINGWORKSPACE_";
}

public enum CollectionMergeStrategy
{
    /// <summary>A higher layer that defines any element replaces the whole collection (default).</summary>
    Replace,

    /// <summary>Elements of higher layers are added after lower-layer elements.</summary>
    Append,

    /// <summary>Elements are keyed by stable ID; a higher layer overrides keys of the same ID. An element with Removed=true is removed (RemoveById).</summary>
    MergeById
}

/// <summary>Merge strategy of each configuration collection, keyed by collection path.</summary>
public static class CollectionMergePolicy
{
    public const string RemovedKey = "Removed";

    public static IReadOnlyDictionary<string, CollectionMergeStrategy> Collections { get; } =
        new Dictionary<string, CollectionMergeStrategy>(StringComparer.OrdinalIgnoreCase)
        {
            ["Navigation:Sections"] = CollectionMergeStrategy.MergeById,
            ["Navigation:Groups"] = CollectionMergeStrategy.MergeById,
            ["Navigation:Items"] = CollectionMergeStrategy.MergeById,
        };
}
