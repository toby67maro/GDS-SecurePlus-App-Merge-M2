using System.Text.RegularExpressions;

namespace EngineeringWorkspace.Domain.Common;

/// <summary>Raised when a domain invariant is violated.</summary>
public sealed class DomainException(string message) : Exception(message);

/// <summary>
/// Stable identifier rules shared by workspace and customer identifiers: lowercase letters, digits and
/// hyphens, starting with a letter or digit, at most 64 characters. Identifiers are used as directory
/// names, so this rule also prevents path traversal.
/// </summary>
public static partial class StableIdentifier
{
    public const int MaximumLength = 64;

    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value);

    /// <summary>Creates a stable identifier from a display name (for example "Contoso Ltd" becomes "contoso-ltd").</summary>
    public static string FromDisplayName(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        var slug = NonIdentifierCharacters().Replace(displayName.Trim().ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > MaximumLength) slug = slug[..MaximumLength].TrimEnd('-');
        if (!IsValid(slug)) throw new DomainException($"'{displayName}' cannot be converted to a stable identifier.");
        return slug;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonIdentifierCharacters();
}

/// <summary>Identifies a workspace. Enforces the <see cref="StableIdentifier"/> rules.</summary>
public readonly record struct WorkspaceId
{
    public WorkspaceId(string value)
    {
        if (!StableIdentifier.IsValid(value))
            throw new DomainException($"'{value}' is not a valid workspace identifier. Use lowercase letters, digits and hyphens (max {StableIdentifier.MaximumLength}).");
        Value = value;
    }

    public string Value { get; }

    public static bool TryCreate(string? value, out WorkspaceId id)
    {
        id = default;
        if (!StableIdentifier.IsValid(value)) return false;
        id = new WorkspaceId(value!);
        return true;
    }

    public override string ToString() => Value;
}

/// <summary>Identifies a customer. Enforces the <see cref="StableIdentifier"/> rules.</summary>
public readonly record struct CustomerId
{
    public CustomerId(string value)
    {
        if (!StableIdentifier.IsValid(value))
            throw new DomainException($"'{value}' is not a valid customer identifier. Use lowercase letters, digits and hyphens (max {StableIdentifier.MaximumLength}).");
        Value = value;
    }

    public string Value { get; }

    public static bool TryCreate(string? value, out CustomerId id)
    {
        id = default;
        if (!StableIdentifier.IsValid(value)) return false;
        id = new CustomerId(value!);
        return true;
    }

    public override string ToString() => Value;
}
