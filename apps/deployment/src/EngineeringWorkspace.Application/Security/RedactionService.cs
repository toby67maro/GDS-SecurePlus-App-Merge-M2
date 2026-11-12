using System.Text.RegularExpressions;

namespace EngineeringWorkspace.Application.Security;

/// <summary>
/// The single redaction service used by configuration diagnostics, logging, audit, exports, exception
/// reporting and database diagnostics (including connection strings).
/// </summary>
public interface IRedactionService
{
    /// <summary>True when the key name denotes secret material (references such as <c>ClientSecretReference</c> are not secret).</summary>
    bool IsSecretKey(string key);

    /// <summary>Returns <see cref="RedactionService.Mask"/> for secret keys, otherwise the value with embedded secrets redacted.</summary>
    string? RedactValue(string key, string? value);

    /// <summary>Removes passwords, tokens, client secrets, authorization headers and private keys from free text.</summary>
    string RedactText(string? text);
}

public sealed partial class RedactionService : IRedactionService
{
    public const string Mask = "********";

    private static readonly string[] SecretKeySuffixes =
    [
        "password", "pwd", "secret", "clientsecret", "token", "accesstoken", "refreshtoken", "apikey",
        "connectionstring", "privatekey", "certificatepassword", "sharedkey"
    ];

    public bool IsSecretKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var leaf = key[(key.LastIndexOfAny([':', '.']) + 1)..].Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        if (leaf.EndsWith("reference", StringComparison.Ordinal) || leaf.EndsWith("ref", StringComparison.Ordinal)) return false;
        return SecretKeySuffixes.Any(suffix => leaf.EndsWith(suffix, StringComparison.Ordinal));
    }

    public string? RedactValue(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return IsSecretKey(key) ? Mask : RedactText(value);
    }

    public string RedactText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var result = PrivateKeyBlock().Replace(text, "-----BEGIN PRIVATE KEY-----" + Mask + "-----END PRIVATE KEY-----");
        result = ConnectionStringSecret().Replace(result, match => $"{match.Groups["key"].Value}={Mask}");
        result = QuerySecret().Replace(result, match => $"{match.Groups["key"].Value}={Mask}");
        result = JsonSecret().Replace(result, match => $"\"{match.Groups["key"].Value}\":\"{Mask}\"");
        result = BearerToken().Replace(result, "Bearer " + Mask);
        result = JsonWebToken().Replace(result, Mask);
        return result;
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(@"(?<key>\b(?:Password|Pwd|AccessToken|Access Token|Client ?Secret|Shared ?Access ?Key|AccountKey))\s*=\s*(?:'[^']*'|""[^""]*""|[^;]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringSecret();

    [GeneratedRegex(@"(?<key>\b(?:client_secret|access_token|refresh_token|id_token|code|assertion))=[^&\s]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuerySecret();

    [GeneratedRegex(@"""(?<key>(?:client_secret|access_token|refresh_token|id_token|secretText|password))""\s*:\s*""[^""]*""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JsonSecret();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*", RegexOptions.CultureInvariant)]
    private static partial Regex JsonWebToken();
}
