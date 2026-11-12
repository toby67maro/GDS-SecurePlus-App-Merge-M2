namespace EngineeringWorkspace.Configuration;

/// <summary>
/// The writable per-user data root and its standard layout. The application is installed (read-only) under Program
/// Files and never writes to its install folder: configuration, data, logs, audit, state and the secret store live
/// under %LOCALAPPDATA%\Global Data Systems\Secure Plus Agent. Relative [Paths] values resolve against this root.
/// Data of earlier versions (folder "Secure Plus - Modern Work") is moved there on first start.
/// </summary>
public static class ApplicationDataPaths
{
    public const string CompanyDirectoryName = "Global Data Systems";
    public const string ProductFolderName = "Secure Plus Agent";
    public const string LegacyProductFolderName = "Secure Plus - Modern Work";
    public const string ProductDirectoryName = CompanyDirectoryName + "/" + ProductFolderName;

    /// <summary>Package templates copied into the configuration directory when the user copy does not exist.</summary>
    public static IReadOnlyList<string> ConfigurationTemplates { get; } =
    [
        "Application.ini", "Branding.ini", "Features.ini", "Navigation.ini", "Repositories.ini", "Integrations.ini", "Database.ini",
        "Logging.ini", "GoldenTenant.ini", "Environments/Development.ini", "Environments/Test.ini", "Environments/Production.ini",
        "ApplicationProperties.json"
    ];

    public static string GetRoot(string? rootOverride = null) =>
        rootOverride ?? ResolveRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>The data root under <paramref name="localApplicationData"/>, moving a legacy data folder into place first.</summary>
    public static string ResolveRoot(string localApplicationData)
    {
        var company = Path.Combine(localApplicationData, CompanyDirectoryName);
        var current = Path.Combine(company, ProductFolderName);
        var legacy = Path.Combine(company, LegacyProductFolderName);
        if (Directory.Exists(current) || !Directory.Exists(legacy)) return current;
        try
        {
            Directory.Move(legacy, current);
            return current;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Files are in use (for example another instance is running): keep using the legacy folder this time.
            return legacy;
        }
    }

    /// <summary>The legacy data root under <paramref name="localApplicationData"/> (for rewriting stored absolute paths).</summary>
    public static string LegacyRoot(string localApplicationData) => Path.Combine(localApplicationData, CompanyDirectoryName, LegacyProductFolderName);

    /// <summary>Standard directories under the root (they match the compiled [Paths] defaults).</summary>
    public static IReadOnlyDictionary<string, string> GetDirectories(string root) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Configuration"] = Path.Combine(root, "config"),
            ["Data"] = Path.Combine(root, "data"),
            ["Workspaces"] = Path.Combine(root, "data", "Workspaces"),
            ["Customers"] = Path.Combine(root, "data", "Customers"),
            ["GoldenTenants"] = Path.Combine(root, "data", "GoldenTenants"),
            ["Imports"] = Path.Combine(root, "data", "Imports"),
            ["Exports"] = Path.Combine(root, "data", "Exports"),
            ["Logs"] = Path.Combine(root, "data", "Logs"),
            ["Audit"] = Path.Combine(root, "data", "Audit"),
            ["Credentials"] = Path.Combine(root, "data", "Credentials"),
            ["State"] = Path.Combine(root, "data", "State"),
        };

    public static void EnsureCreated(string root)
    {
        Directory.CreateDirectory(root);
        foreach (var directory in GetDirectories(root).Values)
            Directory.CreateDirectory(directory);
    }

    /// <summary>Copies a template only when the destination does not exist; user files are never overwritten.</summary>
    public static void CopyTemplateIfMissing(string packageBasePath, string userRoot, string relativePath)
    {
        var target = Path.Combine(userRoot, relativePath);
        if (File.Exists(target)) return;
        var source = Path.Combine(packageBasePath, relativePath);
        if (!File.Exists(source)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target);
    }
}
