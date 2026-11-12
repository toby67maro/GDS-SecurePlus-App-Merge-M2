using System.Security.Cryptography;
using System.Text;
using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Configuration;
using EngineeringWorkspace.Application.Credentials;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Domain.Auditing;
using EngineeringWorkspace.Domain.Security;

namespace EngineeringWorkspace.Infrastructure.Security;

/// <summary>
/// AES-256 organization key. Text form: "SPK1-" + Base64Url(32 bytes). The database stores OrganizationKeyId (first 16 hex
/// characters of SHA-256 of the key) and OrganizationKeyCheck (HMAC-SHA256 of a fixed label); this computer stores the key
/// in the DPAPI store under "os:secureplus-organization-key".
/// </summary>
public sealed class OrganizationKeyService(ISharedSettingsStore settings, LocalSecrets localSecrets, IAuditService audit, Lazy<Application.Security.IUserDirectory>? users = null)
    : IOrganizationKeyService
{
    /// <summary>Once users are defined, only Engineer, Technician/Deployer and Lead may hold the organization key (BLUEPRINT §10.1).</summary>
    private async Task<string?> RoleRefusalAsync(CancellationToken cancellationToken)
    {
        if (users is null || !await users.Value.IsEnforcedAsync(cancellationToken)) return null;
        var me = await users.Value.CurrentAsync(cancellationToken);
        return Application.Security.AppRoles.CanUseTenantCredentials(me?.RoleCode)
            ? null
            : $"{users.Value.CurrentAccount} ({(me is null ? "not a Secure Plus user" : Application.Security.AppRoles.DisplayName(me.RoleCode))}) cannot hold the organization key. Only Engineers, Technicians/Deployers and Leads can.";
    }

    public const string KeyIdProperty = "OrganizationKeyId";
    public const string KeyCheckProperty = "OrganizationKeyCheck";
    private const string Prefix = "SPK1-";
    private static readonly SecretReference LocalReference = new(SecretReference.OperatingSystemProvider, "secureplus-organization-key");
    private static readonly byte[] CheckLabel = "SecurePlus organization key check v1"u8.ToArray();

    private byte[]? _key;

    public string? KeyId { get; private set; }

    public async Task<OrganizationKeyState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        if (!settings.IsAvailable) return OrganizationKeyState.Unavailable;
        var properties = await settings.GetPropertiesAsync(cancellationToken);
        if (!properties.TryGetValue(KeyCheckProperty, out var check) || string.IsNullOrEmpty(check)) return OrganizationKeyState.NotCreated;
        var key = await LocalKeyAsync(cancellationToken);
        if (key is null || !string.Equals(Check(key), check, StringComparison.Ordinal)) return OrganizationKeyState.NotOnThisComputer;
        _key = key;
        KeyId = properties.GetValueOrDefault(KeyIdProperty);
        return OrganizationKeyState.Ready;
    }

    public async Task<string> CreateAsync(CancellationToken cancellationToken = default)
    {
        if (await RoleRefusalAsync(cancellationToken) is { } refusal) throw new UnauthorizedAccessException(refusal);
        if (await GetStateAsync(cancellationToken) is not OrganizationKeyState.NotCreated)
            throw new InvalidOperationException("The database already has an organization key (or is not ready).");
        var key = RandomNumberGenerator.GetBytes(32);
        await localSecrets.Store.SetSecretAsync(LocalReference, Text(key), cancellationToken);
        await settings.SetPropertyAsync(KeyIdProperty, Id(key), "Secure Plus", cancellationToken);
        await settings.SetPropertyAsync(KeyCheckProperty, Check(key), "Secure Plus", cancellationToken);
        _key = key;
        KeyId = Id(key);
        await audit.RecordAsync(AuditEventTypes.CredentialCreated, AuditOutcome.Success, $"Organization key {KeyId} created.", new Dictionary<string, string?> { ["KeyId"] = KeyId },
            cancellationToken);
        return Text(key);
    }

    public async Task<string?> EnrolAsync(string keyText, CancellationToken cancellationToken = default)
    {
        if (await RoleRefusalAsync(cancellationToken) is { } refusal) return refusal;
        if (!TryParse(keyText, out var key)) return "That is not an organization key (it starts with SPK1-).";
        var properties = await settings.GetPropertiesAsync(cancellationToken);
        if (!properties.TryGetValue(KeyCheckProperty, out var check) || string.IsNullOrEmpty(check)) return "This database has no organization key yet.";
        if (!string.Equals(Check(key), check, StringComparison.Ordinal)) return "The key does not belong to this database.";
        await localSecrets.Store.SetSecretAsync(LocalReference, Text(key), cancellationToken);
        _key = key;
        KeyId = Id(key);
        await audit.RecordAsync(AuditEventTypes.CredentialUpdated, AuditOutcome.Success, $"Organization key {KeyId} entered on {Environment.MachineName}.",
            new Dictionary<string, string?> { ["KeyId"] = KeyId }, cancellationToken);
        return null;
    }

    public async Task<string?> ExportAsync(CancellationToken cancellationToken = default) =>
        await GetStateAsync(cancellationToken) == OrganizationKeyState.Ready ? Text(_key!) : null;

    /// <summary>The key when this computer holds a valid one; otherwise null.</summary>
    internal async Task<byte[]?> KeyAsync(CancellationToken cancellationToken)
    {
        if (_key is not null) return _key;
        return await GetStateAsync(cancellationToken) == OrganizationKeyState.Ready ? _key : null;
    }

    /// <summary>AES-256-GCM: Base64(nonce 12 | tag 16 | cipher), bound to <paramref name="context"/> (the secret reference).</summary>
    public static string Encrypt(byte[] key, string plaintext, string context)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(context));
        CryptographicOperations.ZeroMemory(plain);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public static string Decrypt(byte[] key, string payload, string context)
    {
        var data = Convert.FromBase64String(payload);
        if (data.Length < 28) throw new CryptographicException("The encrypted secret is damaged.");
        var plain = new byte[data.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(context));
        var text = Encoding.UTF8.GetString(plain);
        CryptographicOperations.ZeroMemory(plain);
        return text;
    }

    public static string Id(byte[] key) => Convert.ToHexString(SHA256.HashData(key))[..16];
    public static string Check(byte[] key) => Convert.ToBase64String(HMACSHA256.HashData(key, CheckLabel));
    public static string Text(byte[] key) => Prefix + Convert.ToBase64String(key).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryParse(string? text, out byte[] key)
    {
        key = [];
        var value = text?.Trim();
        if (value is null || !value.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var body = value[Prefix.Length..].Replace('-', '+').Replace('_', '/');
        body = body.PadRight(body.Length + (4 - body.Length % 4) % 4, '=');
        try
        {
            key = Convert.FromBase64String(body);
            return key.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task<byte[]?> LocalKeyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return TryParse(await localSecrets.Store.GetSecretAsync(LocalReference, cancellationToken), out var key) ? key : null;
        }
        catch (SecretStoreException)
        {
            return null;
        }
    }
}
