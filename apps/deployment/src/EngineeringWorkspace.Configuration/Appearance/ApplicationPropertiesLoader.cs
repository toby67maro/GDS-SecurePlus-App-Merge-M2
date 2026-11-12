using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Configuration;

namespace EngineeringWorkspace.Configuration.Appearance;

public enum AppearanceSource
{
    BuiltInDefaults,
    CustomFile
}

/// <summary>An appearance problem: JSON path, offending value, expected format. Appearance problems never block startup.</summary>
public sealed record AppearanceDiagnostic(ConfigurationSeverity Severity, string Path, string? Value, string Message, string? ExpectedFormat)
{
    public override string ToString() =>
        string.Join(" ", new[] { Path.Length == 0 ? null : $"{Path}:", Value is null ? null : $"'{Value}'", Message, ExpectedFormat is null ? null : $"Expected {ExpectedFormat}." }
            .Where(part => part is not null));
}

public sealed record AppearanceLoadResult(ApplicationProperties Properties, string PropertiesPath, AppearanceSource Source, IReadOnlyList<AppearanceDiagnostic> Diagnostics);

/// <summary>
/// Reads config/ApplicationProperties.json. Fallback policy: a missing file is supported (built-in GDS defaults);
/// malformed JSON or an unsupported schemaVersion ignores the file; an invalid value falls back to its built-in
/// default; unknown properties are ignored. Every case produces a diagnostic and none blocks startup.
/// </summary>
public static partial class ApplicationPropertiesLoader
{
    public const string FileName = "ApplicationProperties.json";

    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 };

    public static AppearanceLoadResult Load(string propertiesPath)
    {
        var defaults = ApplicationProperties.BuiltInDefaults;
        if (!File.Exists(propertiesPath))
            return new(defaults, propertiesPath, AppearanceSource.BuiltInDefaults,
                [new(ConfigurationSeverity.Information, string.Empty, null, $"{FileName} was not found; the built-in GDS defaults are active.", null)]);

        string text;
        try
        {
            text = File.ReadAllText(propertiesPath, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(defaults, propertiesPath, AppearanceSource.BuiltInDefaults,
                [new(ConfigurationSeverity.Warning, string.Empty, null, $"The file could not be read ({exception.GetType().Name}). The built-in GDS defaults are active.", null)]);
        }

        return Parse(text, propertiesPath);
    }

    /// <summary>Parses appearance JSON (a file's content or a theme stored in the database); <paramref name="source"/> names it in diagnostics.</summary>
    public static AppearanceLoadResult Parse(string json, string source)
    {
        var defaults = ApplicationProperties.BuiltInDefaults;
        var propertiesPath = source;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocumentOptions);
        }
        catch (Exception exception) when (exception is JsonException)
        {
            var detail = exception is JsonException malformed ? $"Malformed JSON at line {malformed.LineNumber + 1}." : $"The file could not be read ({exception.GetType().Name}).";
            return new(defaults, propertiesPath, AppearanceSource.BuiltInDefaults,
                [new(ConfigurationSeverity.Warning, string.Empty, null, $"{detail} The file was ignored and the built-in GDS defaults are active.", "standard JSON without comments")]);
        }

        using (document)
        {
            var diagnostics = new List<AppearanceDiagnostic>();
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Ignored(propertiesPath, "The root must be a JSON object.");
            if (!root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schemaVersion))
                return Ignored(propertiesPath, "schemaVersion is required and must be a number.");
            if (schemaVersion != AppearanceSchema.SchemaVersion)
                return Ignored(propertiesPath, $"schemaVersion {schemaVersion} is not supported (supported: {AppearanceSchema.SchemaVersion}).");

            ReportUnknown(root, string.Empty, ["schemaVersion", "appearance"], diagnostics);
            var values = new Dictionary<string, object>(StringComparer.Ordinal);
            var light = new Dictionary<string, string>(defaults.Appearance.Light.Colors.Tokens, StringComparer.Ordinal);
            var dark = new Dictionary<string, string>(defaults.Appearance.Dark.Colors.Tokens, StringComparer.Ordinal);
            if (Object(root, "appearance", "appearance", diagnostics) is { } appearance)
            {
                ReportUnknown(appearance, "appearance", ["theme", "typography", "layout", "light", "dark"], diagnostics);
                BindSection(appearance, "theme", AppearanceSchema.Theme, values, diagnostics);
                BindSection(appearance, "typography", AppearanceSchema.Typography, values, diagnostics);
                BindSection(appearance, "layout", AppearanceSchema.Layout, values, diagnostics);
                BindColors(appearance, "light", light, diagnostics);
                BindColors(appearance, "dark", dark, diagnostics);
            }

            CheckContrast("light", light, defaults.Appearance.Light.Colors, diagnostics);
            CheckContrast("dark", dark, defaults.Appearance.Dark.Colors, diagnostics);
            var properties = new ApplicationProperties(schemaVersion, new AppearanceProperties(
                new ThemeProperties(
                    values.TryGetValue("appearance.theme.mode", out var mode) ? (ThemeMode)mode : defaults.Appearance.Theme.Mode,
                    values.TryGetValue("appearance.theme.allowUserSelection", out var allow) ? (bool)allow : defaults.Appearance.Theme.AllowUserSelection),
                TypographyProperties.FromValues(values),
                LayoutProperties.FromValues(values),
                new ThemeVariantProperties(new ThemeColorProperties(light)),
                new ThemeVariantProperties(new ThemeColorProperties(dark))));
            return new(properties, propertiesPath, AppearanceSource.CustomFile, diagnostics);
        }
    }

    /// <summary>Relative WCAG 2.x contrast ratio of two #RRGGBB/#AARRGGBB colors (alpha ignored).</summary>
    public static double ContrastRatio(string foreground, string background)
    {
        static double Luminance(string color)
        {
            var hex = color.TrimStart('#');
            if (hex.Length == 8) hex = hex[2..];
            double Channel(int offset)
            {
                var value = int.Parse(hex.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
                return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
        }

        var (a, b) = (Luminance(foreground), Luminance(background));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    public static bool IsColor(string? value) => value is not null && ColorPattern().IsMatch(value);

    /// <summary>Writes the built-in GDS defaults as standard JSON without comments. Never overwrites unless asked.</summary>
    /// <summary>Theme JSON (schema 1) of a mode and light/dark colors; typography and layout keep their defaults.</summary>
    public static string Serialize(ThemeMode mode, bool allowUserSelection, IReadOnlyDictionary<string, string> light, IReadOnlyDictionary<string, string> dark)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", AppearanceSchema.SchemaVersion);
            writer.WriteStartObject("appearance");
            writer.WriteStartObject("theme");
            writer.WriteString("mode", mode.ToString());
            writer.WriteBoolean("allowUserSelection", allowUserSelection);
            writer.WriteEndObject();
            foreach (var (variant, colors) in new[] { ("light", light), ("dark", dark) })
            {
                writer.WriteStartObject(variant);
                writer.WriteStartObject("colors");
                foreach (var (token, value) in colors.OrderBy(pair => pair.Key, StringComparer.Ordinal)) writer.WriteString(token, value);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static OperationResult WriteDefaults(string propertiesPath, bool overwrite)
    {
        if (File.Exists(propertiesPath) && !overwrite)
            return OperationResult.Failure("FileExists", $"{FileName} already exists and was not overwritten.", null, "Confirm overwriting to replace it.");
        var defaults = ApplicationProperties.BuiltInDefaults;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(propertiesPath)!);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", AppearanceSchema.SchemaVersion);
                writer.WriteStartObject("appearance");
                writer.WriteStartObject("theme");
                writer.WriteString("mode", defaults.Appearance.Theme.Mode.ToString());
                writer.WriteBoolean("allowUserSelection", defaults.Appearance.Theme.AllowUserSelection);
                writer.WriteEndObject();
                WriteGroup(writer, "typography", AppearanceSchema.Typography);
                WriteGroup(writer, "layout", AppearanceSchema.Layout);
                foreach (var (variant, colors) in new[] { ("light", defaults.Appearance.Light.Colors), ("dark", defaults.Appearance.Dark.Colors) })
                {
                    writer.WriteStartObject(variant);
                    writer.WriteStartObject("colors");
                    foreach (var token in AppearanceSchema.Colors) writer.WriteString(token.Path, colors[token.Path]);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            var temporary = $"{propertiesPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(temporary, stream.ToArray());
                File.Move(temporary, propertiesPath, overwrite);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }

            return OperationResult.Success();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return OperationResult.Failure("WriteFailed", $"{FileName} could not be written.", exception.GetType().Name, "Check that the configuration directory is writable.");
        }
    }

    private static void WriteGroup(Utf8JsonWriter writer, string name, IReadOnlyList<AppearanceProperty> properties)
    {
        writer.WriteStartObject(name);
        foreach (var property in properties)
        {
            var leaf = property.Path[(property.Path.LastIndexOf('.') + 1)..];
            if (property.Default is double number) writer.WriteNumber(leaf, number);
            else writer.WriteString(leaf, Convert.ToString(property.Default, CultureInfo.InvariantCulture));
        }

        writer.WriteEndObject();
    }

    private static AppearanceLoadResult Ignored(string path, string reason) =>
        new(ApplicationProperties.BuiltInDefaults, path, AppearanceSource.BuiltInDefaults,
            [new(ConfigurationSeverity.Warning, string.Empty, null, $"{reason} The file was ignored and the built-in GDS defaults are active.", null)]);

    private static JsonElement? Object(JsonElement parent, string name, string path, ICollection<AppearanceDiagnostic> diagnostics)
    {
        if (!parent.TryGetProperty(name, out var element)) return null;
        if (element.ValueKind == JsonValueKind.Object) return element;
        diagnostics.Add(new(ConfigurationSeverity.Warning, path, element.ToString(), "Expected an object; the built-in defaults are used for this section.", "a JSON object"));
        return null;
    }

    private static void ReportUnknown(JsonElement element, string path, IReadOnlyCollection<string> known, ICollection<AppearanceDiagnostic> diagnostics)
    {
        foreach (var property in element.EnumerateObject().Where(property => !known.Contains(property.Name)))
            diagnostics.Add(new(ConfigurationSeverity.Information, Join(path, property.Name), null, "Unknown property ignored.", $"one of: {string.Join(", ", known)}"));
    }

    private static void BindSection(JsonElement appearance, string section, IReadOnlyList<AppearanceProperty> schema, IDictionary<string, object> values, ICollection<AppearanceDiagnostic> diagnostics)
    {
        if (Object(appearance, section, $"appearance.{section}", diagnostics) is not { } element) return;
        var byLeaf = schema.ToDictionary(property => property.Path[(property.Path.LastIndexOf('.') + 1)..], StringComparer.Ordinal);
        ReportUnknown(element, $"appearance.{section}", byLeaf.Keys.ToArray(), diagnostics);
        foreach (var property in element.EnumerateObject())
        {
            if (!byLeaf.TryGetValue(property.Name, out var definition)) continue;
            if (TryConvert(definition, property.Value, out var value, out var expected)) values[definition.Path] = value!;
            else diagnostics.Add(new(ConfigurationSeverity.Warning, definition.Path, property.Value.ToString(), $"Invalid value; the built-in default '{Convert.ToString(definition.Default, CultureInfo.InvariantCulture)}' is used.", expected));
        }
    }

    private static void BindColors(JsonElement appearance, string variant, IDictionary<string, string> colors, ICollection<AppearanceDiagnostic> diagnostics)
    {
        if (Object(appearance, variant, $"appearance.{variant}", diagnostics) is not { } element) return;
        ReportUnknown(element, $"appearance.{variant}", ["colors"], diagnostics);
        if (Object(element, "colors", $"appearance.{variant}.colors", diagnostics) is not { } colorElement) return;
        var tokens = AppearanceSchema.Colors.Select(token => token.Path).ToArray();
        ReportUnknown(colorElement, $"appearance.{variant}.colors", tokens, diagnostics);
        foreach (var property in colorElement.EnumerateObject().Where(property => tokens.Contains(property.Name)))
        {
            var text = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            if (IsColor(text)) colors[property.Name] = text!.ToUpperInvariant();
            else diagnostics.Add(new(ConfigurationSeverity.Warning, $"appearance.{variant}.colors.{property.Name}", property.Value.ToString(),
                $"Invalid color; the built-in default {colors[property.Name]} is used.", "#RRGGBB or #AARRGGBB"));
        }
    }

    private static bool TryConvert(AppearanceProperty definition, JsonElement element, out object? value, out string expected)
    {
        value = null;
        switch (definition.Kind)
        {
            case AppearanceValueKind.Mode:
                expected = "Light, Dark or System";
                if (element.ValueKind == JsonValueKind.String && Enum.TryParse<ThemeMode>(element.GetString(), ignoreCase: true, out var mode) && Enum.IsDefined(mode) && !element.GetString()!.Any(char.IsAsciiDigit))
                    value = mode;
                break;
            case AppearanceValueKind.Boolean:
                expected = "true or false";
                if (element.ValueKind is JsonValueKind.True or JsonValueKind.False) value = element.GetBoolean();
                break;
            case AppearanceValueKind.FontFamily:
                expected = "a font family name of 1-200 characters";
                if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: >= 1 and <= 200 } family && family.Trim().Length > 0 && !family.Any(char.IsControl))
                    value = family.Trim();
                break;
            case AppearanceValueKind.FontWeight:
                expected = $"one of {string.Join(", ", AppearanceSchema.FontWeights)}";
                if (element.ValueKind == JsonValueKind.String && AppearanceSchema.FontWeights.FirstOrDefault(weight => string.Equals(weight, element.GetString(), StringComparison.OrdinalIgnoreCase)) is { } match)
                    value = match;
                break;
            default:
                expected = $"a number from {definition.Minimum.ToString(CultureInfo.InvariantCulture)} to {definition.Maximum.ToString(CultureInfo.InvariantCulture)}";
                if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number) && double.IsFinite(number) && number >= definition.Minimum && number <= definition.Maximum)
                    value = number;
                break;
        }

        return value is not null;
    }

    private static void CheckContrast(string variant, IDictionary<string, string> colors, ThemeColorProperties defaults, ICollection<AppearanceDiagnostic> diagnostics)
    {
        foreach (var (foreground, background) in AppearanceSchema.ContrastPairs)
        {
            var ratio = ContrastRatio(colors[foreground], colors[background]);
            if (ratio >= 4.5) continue;
            var path = $"appearance.{variant}.colors.{foreground}";
            if (ratio < 1.5)
            {
                // Nearly invisible text would make the application impractical to use: fall back for this pair only.
                colors[foreground] = defaults[foreground];
                colors[background] = defaults[background];
                diagnostics.Add(new(ConfigurationSeverity.Warning, path, null,
                    $"Contrast with {background} is {ratio:0.0}:1, which makes text unreadable; both colors were reset to the built-in defaults.", "a contrast ratio of at least 4.5:1"));
            }
            else
            {
                diagnostics.Add(new(ConfigurationSeverity.Warning, path, null, $"Low contrast with {background} ({ratio:0.0}:1). The colors were kept as configured.", "a contrast ratio of at least 4.5:1"));
            }
        }
    }

    private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    [GeneratedRegex("^#(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.CultureInvariant)]
    private static partial Regex ColorPattern();
}
