using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using EngineeringWorkspace.Domain.Common;

namespace EngineeringWorkspace.Domain.Security;

/// <summary>
/// Points at secret material held by a secret store without revealing the store mechanism or the secret.
/// Text form: <c>provider:name</c> or <c>provider:name@version</c>, for example
/// <c>os:customer/contoso/graph-client-secret</c>. The name is a slash-separated path of safe segments;
/// empty segments and <c>.</c>/<c>..</c> are rejected so a reference can never escape a store directory.
/// </summary>
public sealed partial record SecretReference
{
    /// <summary>The provider handled by the local operating-system secret store (DPAPI on Windows).</summary>
    public const string OperatingSystemProvider = "os";

    public SecretReference(string provider, string name, string? version = null)
    {
        if (!ProviderPattern().IsMatch(provider ?? string.Empty))
            throw new DomainException("A secret reference provider must be lowercase letters, digits or hyphens (for example 'os').");
        if (!IsValidName(name))
            throw new DomainException("A secret reference name must be slash-separated segments of letters, digits, '.', '_' or '-' (max 256 characters, no '..').");
        if (version is not null && !VersionPattern().IsMatch(version))
            throw new DomainException("A secret reference version must be 1-64 letters, digits, '.', '_' or '-'.");
        Provider = provider!;
        Name = name;
        Version = version;
    }

    public string Provider { get; }
    public string Name { get; }
    public string? Version { get; }

    public static SecretReference Parse(string value) =>
        TryParse(value, out var reference)
            ? reference
            : throw new DomainException("Secret reference must use the form provider:name[@version], for example os:customer/contoso/graph-client-secret.");

    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out SecretReference? reference)
    {
        reference = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0) return false;
        var provider = value[..separator];
        var remainder = value[(separator + 1)..];
        string? version = null;
        var at = remainder.LastIndexOf('@');
        if (at >= 0)
        {
            version = remainder[(at + 1)..];
            remainder = remainder[..at];
        }

        if (!ProviderPattern().IsMatch(provider) || !IsValidName(remainder) ||
            (version is not null && !VersionPattern().IsMatch(version)))
            return false;
        reference = new SecretReference(provider, remainder, version);
        return true;
    }

    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 256) return false;
        foreach (var segment in name.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || !SegmentPattern().IsMatch(segment)) return false;
        }

        return true;
    }

    public override string ToString() => Version is null ? $"{Provider}:{Name}" : $"{Provider}:{Name}@{Version}";

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
