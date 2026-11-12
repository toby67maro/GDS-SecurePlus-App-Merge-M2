using System.Data.Common;
using System.Security.Cryptography;
using EngineeringWorkspace.Application.Credentials;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Domain.Security;
using EngineeringWorkspace.Infrastructure.Database;
using EngineeringWorkspace.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Infrastructure.Security;

/// <summary>This computer's own secret store (Windows DPAPI): the database connection's secrets and the organization key.</summary>
public sealed class LocalSecrets(ISecretStore store)
{
    public ISecretStore Store { get; } = store;
}

/// <summary>
/// Secrets shared by every computer: tenant client secrets are stored in secureplus.TenantSecret encrypted with the
/// organization key (AES-256-GCM, bound to the secret reference). The database connection's own secrets stay in this
/// computer's DPAPI store, because they are needed to reach the database. A secret still held only locally (from an
/// earlier version) is moved into the database the first time it is read on a computer that holds the organization key.
/// Without the key (or a database), secrets stay in the local store as before.
/// </summary>
public sealed class SharedSecretStore(
    LocalSecrets local,
    OrganizationKeyService keys,
    Lazy<IDatabaseStatusService> status,
    DatabaseConnectionFactory connections,
    Application.Auditing.AuditContext audit,
    ILogger<SharedSecretStore> logger,
    Lazy<Application.Auditing.IAuditService>? auditLog = null,
    Lazy<Application.Security.IUserDirectory>? users = null) : ISecretStore
{
    private IDatabaseStatusService Database => status.Value;

    public string DisplayName => $"SecurePlus database, organization key{(keys.KeyId is null ? string.Empty : $" {keys.KeyId}")} (connection secrets: {local.Store.DisplayName})";

    public bool SupportsProvider(string provider) => local.Store.SupportsProvider(provider);

    public async Task<string?> GetSecretAsync(SecretReference reference, CancellationToken cancellationToken = default)
    {
        if (IsLocalOnly(reference)) return await local.Store.GetSecretAsync(reference, cancellationToken);
        await EnsureRoleAsync(reference, cancellationToken);
        var key = await SharedKeyAsync(cancellationToken);
        if (key is not null)
        {
            var stored = await ReadAsync(reference, cancellationToken);
            if (stored is not null)
            {
                try
                {
                    var plain = OrganizationKeyService.Decrypt(key, stored, reference.ToString());
                    await RecordUseAsync(reference, cancellationToken);
                    return plain;
                }
                catch (CryptographicException exception)
                {
                    throw new SecretStoreException(SecretStoreFailure.Unreadable, $"The shared secret '{reference}' could not be decrypted with this computer's organization key.", exception);
                }
            }
        }

        var value = await local.Store.GetSecretAsync(reference, cancellationToken);
        if (value is not null && key is not null)
        {
            await WriteAsync(reference, OrganizationKeyService.Encrypt(key, value, reference.ToString()), cancellationToken);
            logger.LogInformation("Secret {Reference} moved from this computer into the shared database store", reference.ToString());
        }

        return value;
    }

    public async Task SetSecretAsync(SecretReference reference, string secret, CancellationToken cancellationToken = default)
    {
        var key = IsLocalOnly(reference) ? null : await SharedKeyAsync(cancellationToken);
        if (key is null)
        {
            await local.Store.SetSecretAsync(reference, secret, cancellationToken);
            return;
        }

        await WriteAsync(reference, OrganizationKeyService.Encrypt(key, secret, reference.ToString()), cancellationToken);
        await local.Store.DeleteSecretAsync(reference, cancellationToken);
    }

    public async Task<bool> DeleteSecretAsync(SecretReference reference, CancellationToken cancellationToken = default)
    {
        var deleted = await local.Store.DeleteSecretAsync(reference, cancellationToken);
        if (!IsLocalOnly(reference) && IsDatabaseReady)
        {
            await using var session = await OpenAsync(cancellationToken);
            await using var command = session.Command(null, $"DELETE FROM {session.Table("secureplus", "TenantSecret")} WHERE SecretReference = @reference;");
            DatabaseConnectionFactory.Parameter(command, "@reference", reference.ToString());
            deleted |= await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }

        return deleted;
    }

    public async Task<bool> SecretExistsAsync(SecretReference reference, CancellationToken cancellationToken = default)
    {
        if (!IsLocalOnly(reference) && IsDatabaseReady && await ReadAsync(reference, cancellationToken) is not null) return true;
        return await local.Store.SecretExistsAsync(reference, cancellationToken);
    }

    /// <summary>Once users are defined, only Engineer, Technician/Deployer and Lead may use tenant credentials (the database also denies the others).</summary>
    private async Task EnsureRoleAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        if (users is null || !IsDatabaseReady || !await users.Value.IsEnforcedAsync(cancellationToken)) return;
        var me = await users.Value.CurrentAsync(cancellationToken);
        if (Application.Security.AppRoles.CanUseTenantCredentials(me?.RoleCode)) return;
        throw new SecretStoreException(SecretStoreFailure.Unreadable,
            $"{users.Value.CurrentAccount} cannot use tenant credentials ({(me is null ? "not a Secure Plus user" : Application.Security.AppRoles.DisplayName(me.RoleCode))}). Ask a Lead in Settings > Users & Roles.");
    }

    /// <summary>Every use of a tenant credential is written to the audit ledger (who, which credential, from which computer).</summary>
    private async Task RecordUseAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        if (auditLog is null) return;
        await auditLog.Value.RecordAsync(Domain.Auditing.AuditEventTypes.TenantCredentialUsed, Domain.Auditing.AuditOutcome.Information,
            $"Tenant credential {reference.Name} used on {Environment.MachineName}.",
            new Dictionary<string, string?> { ["Credential"] = reference.ToString(), ["Computer"] = Environment.MachineName }, cancellationToken);
    }

    private bool IsDatabaseReady => Database.Settings.Provider != DatabaseProvider.None && Database.LatestStatus?.State == DatabaseOverallState.Ready;

    /// <summary>The database connection's secrets and the organization key itself never leave this computer.</summary>
    private bool IsLocalOnly(SecretReference reference)
    {
        if (reference.Name == "secureplus-organization-key") return true;
        var settings = Database.Settings;
        return reference == settings.PasswordSecretReference || reference == settings.EntraClientSecretReference || reference == settings.ConnectionStringSecretReference;
    }

    private async Task<byte[]?> SharedKeyAsync(CancellationToken cancellationToken)
    {
        if (!IsDatabaseReady) return null;
        try
        {
            return await keys.KeyAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            logger.LogWarning(exception, "The organization key could not be checked; secrets use this computer's store");
            return null;
        }
    }

    private async Task<string?> ReadAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        await using var session = await OpenAsync(cancellationToken);
        await using var command = session.Command(null, $"SELECT CipherText FROM {session.Table("secureplus", "TenantSecret")} WHERE SecretReference = @reference;");
        DatabaseConnectionFactory.Parameter(command, "@reference", reference.ToString());
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private async Task WriteAsync(SecretReference reference, string cipherText, CancellationToken cancellationToken)
    {
        await using var session = await OpenAsync(cancellationToken);
        var table = session.Table("secureplus", "TenantSecret");
        var now = DateTimeOffset.UtcNow;
        await using (var delete = session.Command(null, $"DELETE FROM {table} WHERE SecretReference = @reference;"))
        {
            DatabaseConnectionFactory.Parameter(delete, "@reference", reference.ToString());
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var insert = session.Command(null,
            $"INSERT INTO {table} (SecretReference, CipherText, KeyId, CreatedBy, CreatedAtUtc, ModifiedBy, ModifiedAtUtc) VALUES (@reference, @cipher, @key, @by, @now, @by, @now);");
        DatabaseConnectionFactory.Parameter(insert, "@reference", reference.ToString());
        DatabaseConnectionFactory.Parameter(insert, "@cipher", cipherText);
        DatabaseConnectionFactory.Parameter(insert, "@key", keys.KeyId ?? string.Empty);
        DatabaseConnectionFactory.Parameter(insert, "@by", audit.Actor.Length > 256 ? audit.Actor[..256] : audit.Actor);
        DatabaseConnectionFactory.Parameter(insert, "@now", session.Date(now));
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private Task<SqlSession> OpenAsync(CancellationToken cancellationToken) =>
        SqlSession.OpenAsync(Database, connections, message => new SecretStoreException(SecretStoreFailure.Unreadable, message), cancellationToken);
}
