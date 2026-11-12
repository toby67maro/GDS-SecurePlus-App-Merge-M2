using System.Reflection;
using EngineeringWorkspace.Configuration;

namespace EngineeringWorkspace.Desktop.Bootstrap;

/// <summary>
/// Seeds the per-user configuration directory with the package templates. Each missing file is copied from the
/// <c>config</c> folder next to the executable when present (ZIP, MSIX), otherwise written from the copy embedded in
/// this assembly, so a single-file executable also starts with a complete configuration. Existing user files are
/// never overwritten.
/// </summary>
public static class ConfigurationTemplates
{
    private const string ResourcePrefix = "EngineeringWorkspace.ConfigTemplates/";

    /// <summary>
    /// Files a user keeps locally: the database connection (needed to reach the database) and the appearance file used
    /// before a database exists. Every other setting lives in the database from release 2.1.
    /// </summary>
    public static IReadOnlyList<string> LocalFiles { get; } = ["Database.ini", "ApplicationProperties.json"];

    /// <summary>
    /// Writes the shipped defaults (all templates) into <paramref name="defaultsDirectory"/>, replacing older copies.
    /// They are the application's own read-only defaults, never user data; the loader uses them where no user file exists.
    /// </summary>
    public static void WriteDefaults(string applicationDirectory, string defaultsDirectory)
    {
        var packageDirectory = Path.Combine(applicationDirectory, "config");
        var assembly = typeof(ConfigurationTemplates).Assembly;
        var embedded = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .ToDictionary(name => name[ResourcePrefix.Length..].Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
        foreach (var template in ApplicationDataPaths.ConfigurationTemplates)
        {
            var target = Path.Combine(defaultsDirectory, template);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var packaged = Path.Combine(packageDirectory, template);
                if (File.Exists(packaged))
                {
                    File.Copy(packaged, target, overwrite: true);
                    continue;
                }

                if (!embedded.TryGetValue(template.Replace('\\', '/'), out var resource)) continue;
                using var source = assembly.GetManifestResourceStream(resource)!;
                using var destination = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                source.CopyTo(destination);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning($"Default {template} could not be written: {exception.Message}");
            }
        }
    }

    /// <summary>Copies the local files (<see cref="LocalFiles"/>) when missing; existing user files are never overwritten.</summary>
    public static IReadOnlyList<string> SeedIfMissing(string applicationDirectory, string configurationDirectory)
    {
        var written = new List<string>();
        var packageDirectory = Path.Combine(applicationDirectory, "config");
        var assembly = typeof(ConfigurationTemplates).Assembly;
        var embedded = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .ToDictionary(name => name[ResourcePrefix.Length..].Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);

        UpgradeVersionedTemplates(packageDirectory, configurationDirectory, assembly, embedded);
        foreach (var template in ApplicationDataPaths.ConfigurationTemplates.Where(template => LocalFiles.Contains(template, StringComparer.OrdinalIgnoreCase)))
        {
            var target = Path.Combine(configurationDirectory, template);
            if (File.Exists(target)) continue;
            var packaged = Path.Combine(packageDirectory, template);
            if (File.Exists(packaged))
            {
                ApplicationDataPaths.CopyTemplateIfMissing(packageDirectory, configurationDirectory, template);
                written.Add(target);
                continue;
            }

            if (!embedded.TryGetValue(template.Replace('\\', '/'), out var resource)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = assembly.GetManifestResourceStream(resource)!;
            using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            source.CopyTo(destination);
            written.Add(target);
        }

        return written;
    }

    /// <summary>
    /// Templates whose structure changes between releases carry a marker line ("; NavigationVersion=2"). A user copy
    /// written by an earlier version (without the current marker) is kept as "&lt;name&gt;.v1.bak" and replaced, so
    /// renamed or removed pages do not linger in the menu.
    /// </summary>
    private static readonly (string File, string Marker)[] VersionedTemplates = [("Navigation.ini", "; NavigationVersion=3")];

    private static void UpgradeVersionedTemplates(string packageDirectory, string configurationDirectory, Assembly assembly, Dictionary<string, string> embedded)
    {
        foreach (var (file, marker) in VersionedTemplates)
        {
            var target = Path.Combine(configurationDirectory, file);
            if (!File.Exists(target)) continue;
            try
            {
                if (File.ReadLines(target).Take(5).Any(line => line.Trim().Equals(marker, StringComparison.OrdinalIgnoreCase))) continue;
                var backup = Path.Combine(configurationDirectory, $"{Path.GetFileNameWithoutExtension(file)}.v1.bak");
                File.Copy(target, backup, overwrite: true);
                var packaged = Path.Combine(packageDirectory, file);
                if (File.Exists(packaged))
                {
                    File.Copy(packaged, target, overwrite: true);
                }
                else if (embedded.TryGetValue(file, out var resource))
                {
                    using var source = assembly.GetManifestResourceStream(resource)!;
                    using var destination = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                    source.CopyTo(destination);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning($"{file} could not be upgraded: {exception.Message}");
            }
        }
    }

    /// <summary>The embedded template names (for diagnostics).</summary>
    public static IReadOnlyList<string> EmbeddedTemplates(Assembly assembly) =>
        assembly.GetManifestResourceNames().Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(name => name[ResourcePrefix.Length..].Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
}
