using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using EngineeringWorkspace.Application.Credentials;
using EngineeringWorkspace.Domain.Security;
using EngineeringWorkspace.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Infrastructure.Security;

/// <summary>
/// Windows DPAPI secret store (CurrentUser scope). Only the same Windows user on the same machine profile can
/// decrypt; after a profile or machine change secrets must be entered again. Each secret is one file named by
/// the SHA-256 of its reference, so a reference can never address a path outside the store. The reference is
/// also bound as DPAPI entropy, so files cannot be swapped between references. Writes are atomic, the store
/// directory is restricted to the current user, plaintext buffers are zeroed and secret values never appear in
/// logs or exception messages.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Header = "EWDPAPI1"u8.ToArray();
    private readonly string _directory;
    private readonly ILogger<DpapiSecretStore> _logger;

    public DpapiSecretStore(string credentialDirectory, ILogger<DpapiSecretStore> logger)
    {
        _directory = Path.GetFullPath(Path.Combine(credentialDirectory, "secrets"));
        _logger = logger;
    }

    public string DisplayName => "Windows DPAPI (current user)";

    public bool SupportsProvider(string provider) => string.Equals(provider, SecretReference.OperatingSystemProvider, StringComparison.Ordinal);

    public async Task<string?> GetSecretAsync(SecretReference reference, CancellationToken cancellationToken = default)
    {
        var path = PathFor(reference);
        if (!File.Exists(path)) return null;
        byte[] content;
        try
        {
            content = await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new SecretStoreException(SecretStoreFailure.AccessDenied, "The secret file cannot be read by this user.", exception);
        }

        if (content.Length <= Header.Length || !content.AsSpan(0, Header.Length).SequenceEqual(Header))
            throw new SecretStoreException(SecretStoreFailure.Unreadable, "The secret file is not in the expected format.");

        byte[]? clear = null;
        try
        {
            clear = ProtectedData.Unprotect(content[Header.Length..], Entropy(reference), DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }
        catch (CryptographicException exception)
        {
            _logger.LogWarning("A secret could not be decrypted for reference {SecretReference}", reference.ToString());
            throw new SecretStoreException(SecretStoreFailure.Unreadable, "The secret cannot be decrypted by this Windows user. Enter it again.", exception);
        }
        finally
        {
            if (clear is not null) CryptographicOperations.ZeroMemory(clear);
        }
    }

    public async Task SetSecretAsync(SecretReference reference, string secret, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        if (!SupportsProvider(reference.Provider))
            throw new SecretStoreException(SecretStoreFailure.UnsupportedProvider, $"The '{reference.Provider}' provider is not handled by the DPAPI store.");
        EnsureDirectory();
        var clear = Encoding.UTF8.GetBytes(secret);
        try
        {
            var protectedBytes = ProtectedData.Protect(clear, Entropy(reference), DataProtectionScope.CurrentUser);
            var content = new byte[Header.Length + protectedBytes.Length];
            Header.CopyTo(content, 0);
            protectedBytes.CopyTo(content, Header.Length);
            await AtomicFile.WriteAllBytesAsync(PathFor(reference), content, cancellationToken);
            _logger.LogInformation("Secret stored for reference {SecretReference}", reference.ToString());
        }
        catch (CryptographicException exception)
        {
            throw new SecretStoreException(SecretStoreFailure.WriteFailed, "Windows DPAPI could not protect the secret.", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SecretStoreException(SecretStoreFailure.WriteFailed, "The secret file could not be written.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public Task<bool> DeleteSecretAsync(SecretReference reference, CancellationToken cancellationToken = default)
    {
        var path = PathFor(reference);
        if (!File.Exists(path)) return Task.FromResult(false);
        try
        {
            File.Delete(path);
            _logger.LogInformation("Secret deleted for reference {SecretReference}", reference.ToString());
            return Task.FromResult(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SecretStoreException(SecretStoreFailure.WriteFailed, "The secret file could not be deleted.", exception);
        }
    }

    public Task<bool> SecretExistsAsync(SecretReference reference, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(PathFor(reference)));

    private string PathFor(SecretReference reference)
    {
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reference.ToString()))).ToLowerInvariant();
        var path = Path.GetFullPath(Path.Combine(_directory, $"{name}.secret"));
        if (!path.StartsWith(_directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new SecretStoreException(SecretStoreFailure.InvalidReference, "The secret reference resolves outside the store.");
        return path;
    }

    private static byte[] Entropy(SecretReference reference) => Encoding.UTF8.GetBytes($"EngineeringWorkspace.SecretStore.v1|{reference}");

    /// <summary>Creates the store directory with an ACL that grants only the current user, without inheritance.</summary>
    private void EnsureDirectory()
    {
        var directory = new DirectoryInfo(_directory);
        if (!directory.Exists) directory.Create();
        var user = WindowsIdentity.GetCurrent().User ?? throw new SecretStoreException(SecretStoreFailure.AccessDenied, "The current Windows user could not be determined.");
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        try
        {
            directory.SetAccessControl(security);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException)
        {
            _logger.LogWarning(exception, "The credential store ACL could not be restricted to the current user");
        }
    }
}

/// <summary>Used where DPAPI does not exist (non-Windows build agents). Never stores or returns secrets.</summary>
public sealed class UnavailableSecretStore : ISecretStore
{
    public string DisplayName => "Unavailable on this platform";

    public bool SupportsProvider(string provider) => string.Equals(provider, SecretReference.OperatingSystemProvider, StringComparison.Ordinal);

    public Task<string?> GetSecretAsync(SecretReference reference, CancellationToken cancellationToken = default) =>
        throw new SecretStoreException(SecretStoreFailure.UnsupportedPlatform, "Secrets are protected with Windows DPAPI and can only be read on Windows.");

    public Task SetSecretAsync(SecretReference reference, string secret, CancellationToken cancellationToken = default) =>
        throw new SecretStoreException(SecretStoreFailure.UnsupportedPlatform, "Secrets are protected with Windows DPAPI and can only be stored on Windows.");

    public Task<bool> DeleteSecretAsync(SecretReference reference, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> SecretExistsAsync(SecretReference reference, CancellationToken cancellationToken = default) => Task.FromResult(false);
}
