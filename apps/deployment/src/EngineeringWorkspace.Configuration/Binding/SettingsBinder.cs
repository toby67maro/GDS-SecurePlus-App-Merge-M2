using System.Globalization;
using EngineeringWorkspace.Application.Configuration;
using EngineeringWorkspace.Configuration.Loading;
using EngineeringWorkspace.Configuration.Validation;
using EngineeringWorkspace.Domain.Security;

namespace EngineeringWorkspace.Configuration.Binding;

/// <summary>
/// Converts effective string values to typed values. Every conversion failure is recorded with its file,
/// layer, section and key; binding continues so all errors are reported in one pass. Missing values come from
/// <see cref="CompiledDefaults"/>; a malformed explicit value is an error and the compiled default is used.
/// </summary>
public sealed class SettingsBinder(IReadOnlyDictionary<string, EffectiveValue> values, ICollection<ConfigurationValidationError> errors)
{
    public IReadOnlyDictionary<string, EffectiveValue> Values => values;

    public string String(string key) => Raw(key)?.Trim() ?? CompiledDefaults.Values.GetValueOrDefault(key) ?? string.Empty;

    public string? OptionalString(string key) => string.IsNullOrWhiteSpace(String(key)) ? null : String(key);

    public bool Bool(string key)
    {
        var raw = String(key);
        if (bool.TryParse(raw, out var value)) return value;
        Error(key, "InvalidBoolean", "The value is not a boolean.", "true or false", ["true", "false"]);
        return bool.TryParse(CompiledDefaults.Values.GetValueOrDefault(key), out var fallback) && fallback;
    }

    public int Int(string key, int minimum, int maximum)
    {
        var raw = String(key);
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            if (value >= minimum && value <= maximum) return value;
            Error(key, "OutOfRange", $"The value must be between {minimum} and {maximum}.", $"integer {minimum}-{maximum}", []);
        }
        else
        {
            Error(key, "InvalidInteger", "The value is not a whole number.", $"integer {minimum}-{maximum}", []);
        }

        return int.TryParse(CompiledDefaults.Values.GetValueOrDefault(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fallback) ? fallback : minimum;
    }

    public int? OptionalInt(string key, int minimum, int maximum) => string.IsNullOrWhiteSpace(String(key)) ? null : Int(key, minimum, maximum);

    public TEnum Enum<TEnum>(string key) where TEnum : struct, Enum
    {
        var raw = String(key);
        if (System.Enum.TryParse<TEnum>(raw, ignoreCase: true, out var value) && System.Enum.IsDefined(value) && !raw.Any(char.IsAsciiDigit)) return value;
        Error(key, "InvalidEnum", "The value is not one of the allowed values.", null, System.Enum.GetNames<TEnum>());
        return System.Enum.TryParse<TEnum>(CompiledDefaults.Values.GetValueOrDefault(key), true, out var fallback) ? fallback : default;
    }

    public Guid? OptionalGuid(string key)
    {
        var raw = String(key);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (Guid.TryParseExact(raw, "D", out var value)) return value;
        Error(key, "InvalidGuid", "The value is not a GUID.", "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx", []);
        return null;
    }

    public SecretReference? OptionalSecretReference(string key)
    {
        var raw = String(key);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (SecretReference.TryParse(raw, out var reference)) return reference;
        Error(key, "InvalidSecretReference", "The value is not a valid secret reference.", "provider:name[@version], for example os:customer/contoso/graph-client-secret", []);
        return null;
    }

    public Uri Uri(string key, UriKind kind)
    {
        var raw = String(key);
        if (System.Uri.TryCreate(raw, kind, out var value)) return value;
        Error(key, "InvalidUri", "The value is not a valid URI.", kind == UriKind.Absolute ? "absolute URI such as http://localhost" : "URI", []);
        return new Uri(CompiledDefaults.Values.GetValueOrDefault(key) ?? "http://localhost", kind);
    }

    /// <summary>Resolves a path; relative paths are resolved against <paramref name="baseDirectory"/>.</summary>
    public string Path(string key, string baseDirectory)
    {
        var raw = String(key);
        if (raw.Length == 0 || raw.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0)
        {
            Error(key, "InvalidPath", "The value is not a valid path.", "absolute path or path relative to the data root", []);
            raw = CompiledDefaults.Values.GetValueOrDefault(key) ?? ".";
        }

        var expanded = System.Environment.ExpandEnvironmentVariables(raw);
        return System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(expanded) ? expanded : System.IO.Path.Combine(baseDirectory, expanded));
    }

    public string? OptionalPath(string key, string baseDirectory) => string.IsNullOrWhiteSpace(String(key)) ? null : Path(key, baseDirectory);

    /// <summary>Stable IDs of the children of a collection path (for example Navigation:Items).</summary>
    public IReadOnlyList<string> ChildIds(string collection)
    {
        var prefix = collection + ":";
        return values.Keys
            .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(key => key[prefix.Length..].Split(':')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void Error(string key, string code, string message, string? expected, IReadOnlyList<string> allowed,
        ConfigurationSeverity severity = ConfigurationSeverity.Error, ConfigurationValidationStage stage = ConfigurationValidationStage.Binding)
    {
        values.TryGetValue(key, out var value);
        errors.Add(new ConfigurationValidationError(stage, value?.Source, value?.Layer.Name, value?.Section ?? SectionOf(key),
            value?.OriginalKey ?? KeyOf(key), value?.Value, code, message, expected, allowed, severity));
    }

    private string? Raw(string key) => values.TryGetValue(key, out var value) ? value.Value : null;

    private static string SectionOf(string key) => key.Contains(':', StringComparison.Ordinal) ? key[..key.LastIndexOf(':')] : key;

    private static string KeyOf(string key) => key[(key.LastIndexOf(':') + 1)..];
}
