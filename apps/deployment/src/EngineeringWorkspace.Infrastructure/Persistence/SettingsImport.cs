using EngineeringWorkspace.Application.Configuration;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Configuration;
using EngineeringWorkspace.Configuration.Ini;

namespace EngineeringWorkspace.Infrastructure.Persistence;

/// <summary>What the one-time import of a computer's INI files did.</summary>
public sealed record SettingsImportResult(int Imported, int KeptFromDatabase, IReadOnlyList<string> FilesMoved, string? BackupFolder);

/// <summary>
/// Imports a computer's INI settings into secureplus.Setting once (release 2.1). Only values that differ from the shipped
/// defaults are imported; secret-bearing keys never are. When the database already holds a newer value for the same
/// key, it is kept (the newer edit wins). The files are then moved to data\Backup\&lt;time&gt;\Configuration and never
/// read again; Database.ini and ApplicationProperties.json stay (the connection, and appearance until themes are
/// imported).
/// </summary>
public static class SettingsImport
{
    public const string PropertyPrefix = "IniImported:";

    public static async Task<SettingsImportResult> ImportAsync(SqlSettingsStore store, DatabaseSettings database, string configurationDirectory, string defaultsDirectory,
        string dataDirectory, IRedactionService redaction, string actor, CancellationToken cancellationToken)
    {
        var stored = (await store.ListAsync(database, cancellationToken))
            .Where(row => row.Scope == SettingScopes.Shared || row.ComputerName == store.ComputerName)
            .ToDictionary(row => (row.Scope, row.Key), row => row.ModifiedAtUtc);
        var writes = new List<SettingWrite>();
        var files = new List<string>();
        var kept = 0;
        foreach (var template in ApplicationDataPaths.ConfigurationTemplates.Where(IsImported))
        {
            var path = Path.Combine(configurationDirectory, template);
            if (!File.Exists(path)) continue;
            files.Add(path);
            var user = IniDocument.Load(await File.ReadAllBytesAsync(path, cancellationToken));
            if (user.Errors.Count > 0) continue;
            var defaultsPath = Path.Combine(defaultsDirectory, template);
            var defaults = File.Exists(defaultsPath) ? IniDocument.Load(await File.ReadAllBytesAsync(defaultsPath, cancellationToken)) : IniDocument.Empty();
            var shipped = defaults.Entries.ToDictionary(entry => $"{entry.Section}:{entry.Key}", entry => entry.Value, StringComparer.OrdinalIgnoreCase);
            var fileTime = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            foreach (var entry in user.Entries)
            {
                var key = $"{entry.Section}:{entry.Key}";
                if (redaction.IsSecretKey(key) || string.Equals(entry.Section, "Database", StringComparison.OrdinalIgnoreCase)) continue;
                if (shipped.TryGetValue(key, out var shippedValue) && string.Equals(shippedValue, entry.Value, StringComparison.Ordinal)) continue;
                var scope = SettingScopes.For(entry.Section);
                if (stored.TryGetValue((scope, key), out var databaseTime) && databaseTime >= fileTime)
                {
                    kept++;
                    continue;
                }

                writes.Add(new SettingWrite(scope, key, entry.Value, ModifiedAtUtc: fileTime));
            }
        }

        await store.WriteAsync(database, writes, actor, cancellationToken);

        string? backup = null;
        var moved = new List<string>();
        if (files.Count > 0)
        {
            backup = Path.Combine(dataDirectory, "Backup", DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture), "Configuration");
            foreach (var file in files)
            {
                try
                {
                    var target = Path.Combine(backup, Path.GetRelativePath(configurationDirectory, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: true);
                    File.Delete(file);
                    moved.Add(file);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Left in place: the loader prefers the database values, which are imported already.
                }
            }
        }

        await store.SetPropertyAsync(database, PropertyPrefix + store.ComputerName,
            $"{DateTimeOffset.UtcNow:O}; {writes.Count} setting(s) imported; {kept} kept from the database", actor, cancellationToken);
        return new SettingsImportResult(writes.Count, kept, moved, backup);
    }

    /// <summary>INI templates whose values move to the database (not the connection, not the appearance file).</summary>
    private static bool IsImported(string template) =>
        template.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) && !string.Equals(template, "Database.ini", StringComparison.OrdinalIgnoreCase);
}
