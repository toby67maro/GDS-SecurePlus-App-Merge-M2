using EngineeringWorkspace.Application.Common;

namespace EngineeringWorkspace.Application.Configuration;

public enum ConfigurationSeverity
{
    Information,
    Warning,
    Error
}

/// <summary>Validation state of one effective configuration key.</summary>
public enum ConfigurationEntryState
{
    Valid,
    Warning,
    Error
}

/// <summary>One row of the effective-configuration view. Secret-bearing values are always redacted.</summary>
public sealed record ConfigurationEntryDiagnostic(
    string Key,
    string? EffectiveValue,
    string? RedactedValue,
    bool IsSecret,
    string SourceFile,
    string Layer,
    int LayerOrder,
    string Section,
    string OriginalKey,
    bool IsOverridden,
    string? PreviousSource,
    ConfigurationEntryState ValidationState,
    string? ValidationMessage);

/// <summary>An actionable configuration problem (file, section, key, safe value, problem, allowed values).</summary>
public sealed record ConfigurationIssue(
    ConfigurationSeverity Severity,
    string ErrorCode,
    string Message,
    string? FilePath,
    string? Layer,
    string? Section,
    string? Key,
    string? SafeValue,
    string? ExpectedFormat,
    IReadOnlyList<string> AllowedValues);

/// <summary>A configuration source considered at startup and whether it was found.</summary>
public sealed record ConfigurationSourceDiagnostic(int Order, string Layer, string Path, bool Exists, int KeyCount);

/// <summary>Read-only diagnostics of the immutable effective configuration snapshot.</summary>
public interface IConfigurationDiagnostics
{
    DateTimeOffset LoadedAtUtc { get; }
    IReadOnlyList<ConfigurationSourceDiagnostic> Sources { get; }
    IReadOnlyList<ConfigurationEntryDiagnostic> Entries { get; }
    IReadOnlyList<ConfigurationIssue> Issues { get; }
    bool HasErrors { get; }
}

/// <summary>
/// Writes non-secret settings to a configuration file in the writable configuration directory, preserving
/// comments and unrelated keys. Refuses secret-bearing keys. Changes take effect at the next start because the
/// effective configuration snapshot is immutable.
/// </summary>
public interface IConfigurationFileWriter
{
    Task<OperationResult<string>> WriteSectionAsync(
        string fileName,
        string section,
        IReadOnlyDictionary<string, string?> values,
        CancellationToken cancellationToken = default);
}

/// <summary>Scopes of a setting stored in the database.</summary>
public static class SettingScopes
{
    /// <summary>Applies to every computer using the database.</summary>
    public const string Shared = "Shared";

    /// <summary>Applies to one computer (folders and logging, which are local to each machine).</summary>
    public const string Computer = "Computer";

    private static readonly string[] ComputerSections = ["Paths", "Logging"];

    /// <summary>The scope a setting of <paramref name="sectionOrKey"/> (for example "Paths" or "Paths:LogDirectory") is saved in.</summary>
    public static string For(string sectionOrKey)
    {
        var root = sectionOrKey.Split(':')[0];
        return ComputerSections.Contains(root, StringComparer.OrdinalIgnoreCase) ? Computer : Shared;
    }
}

/// <summary>One setting to write (or remove) in the database.</summary>
public sealed record SettingWrite(string Scope, string Key, string? Value, bool Remove = false, DateTimeOffset? ModifiedAtUtc = null);

/// <summary>A setting stored in the database.</summary>
public sealed record StoredSetting(string Scope, string ComputerName, string Key, string? Value, string ModifiedBy, DateTimeOffset ModifiedAtUtc);

/// <summary>Settings and database properties in the SecurePlus database (secureplus.Setting, secureplus.DatabaseProperty).</summary>
public interface ISharedSettingsStore
{
    bool IsAvailable { get; }
    string ComputerName { get; }
    Task WriteAsync(IReadOnlyList<SettingWrite> writes, string actor, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredSetting>> ListAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, string?>> GetPropertiesAsync(CancellationToken cancellationToken = default);
    Task SetPropertyAsync(string key, string? value, string actor, CancellationToken cancellationToken = default);
}
