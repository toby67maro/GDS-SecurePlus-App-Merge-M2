using System.Text;
using EngineeringWorkspace.Configuration.Ini;

namespace EngineeringWorkspace.Configuration.Upgrade;

/// <summary>
/// Upgrades per-user configuration written by earlier versions. User files are otherwise never overwritten, so
/// values that were the old defaults (the "Secure Plus - Modern Work" names) are replaced with the current defaults,
/// and absolute paths that pointed into the legacy data folder are pointed at the current one. Values the user
/// changed are left alone.
/// </summary>
public static class LegacyConfigurationUpgrade
{
    public const string CurrentName = "Secure Plus Agent";

    private static readonly HashSet<string> LegacyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Secure Plus - Modern Work",
        "Secure Plus - Modern Work (Global Data Systems)"
    };

    private static readonly (string File, string Section, string Key)[] NameKeys =
    [
        ("Application.ini", "Application", "Name"),
        ("Application.ini", "Application", "Title"),
        ("Branding.ini", "Branding", "ApplicationName"),
        ("Branding.ini", "Branding", "ApplicationTitle")
    ];

    /// <summary>Applies the upgrade and returns the files that changed.</summary>
    public static IReadOnlyList<string> Apply(string configurationDirectory, string? legacyRoot, string currentRoot)
    {
        var changed = new List<string>();
        foreach (var file in new[] { "Application.ini", "Branding.ini", "Database.ini" })
        {
            var path = Path.Combine(configurationDirectory, file);
            if (!File.Exists(path)) continue;
            var document = IniDocument.Load(File.ReadAllBytes(path));
            if (document.Errors.Count > 0) continue;
            var updates = new List<(string Section, string Key, string Value)>();
            foreach (var entry in document.Entries)
            {
                if (NameKeys.Any(key => key.File.Equals(file, StringComparison.OrdinalIgnoreCase) && key.Section.Equals(entry.Section, StringComparison.OrdinalIgnoreCase) &&
                                        key.Key.Equals(entry.Key, StringComparison.OrdinalIgnoreCase)) && LegacyNames.Contains(entry.Value.Trim()))
                {
                    updates.Add((entry.Section, entry.Key, CurrentName));
                    continue;
                }

                if (legacyRoot is not null && entry.Value.StartsWith(legacyRoot, StringComparison.OrdinalIgnoreCase) &&
                    (entry.Section.Equals("Paths", StringComparison.OrdinalIgnoreCase) || (file == "Database.ini" && entry.Key.Equals("DatabaseName", StringComparison.OrdinalIgnoreCase))))
                    updates.Add((entry.Section, entry.Key, currentRoot + entry.Value[legacyRoot.Length..]));
            }

            if (updates.Count == 0) continue;
            foreach (var (section, key, value) in updates) document.Set(section, key, value);
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporary, document.ToText(), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
            changed.Add(path);
        }

        UpgradeAppearance(configurationDirectory, changed);
        return changed;
    }

    /// <summary>
    /// Version 1.2 made the default typography smaller. A per-user ApplicationProperties.json seeded by an earlier version
    /// pins the old default base font size (14); that value is replaced with the new default (13). Other values are kept.
    /// </summary>
    private static void UpgradeAppearance(string configurationDirectory, List<string> changed)
    {
        var path = Path.Combine(configurationDirectory, "ApplicationProperties.json");
        if (!File.Exists(path)) return;
        System.Text.Json.Nodes.JsonNode? root;
        try
        {
            root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path), documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }

        if (root?["appearance"]?["typography"] is not System.Text.Json.Nodes.JsonObject typography || typography["baseFontSize"] is not { } size) return;
        if (size.GetValueKind() != System.Text.Json.JsonValueKind.Number || size.GetValue<double>() != 14) return;
        typography["baseFontSize"] = 13;
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
        changed.Add(path);
    }
}
