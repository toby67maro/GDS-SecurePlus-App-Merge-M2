using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Domain.Auditing;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Credentials;
using EngineeringWorkspace.Domain.Security;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Application.Credentials;

public enum SecretStoreFailure
{
    UnsupportedPlatform,
    Unreadable,
    AccessDenied,
    InvalidReference,
    UnsupportedProvider,
    WriteFailed
}

/// <summary>A secret-store failure. Its message never contains secret material.</summary>
public sealed class SecretStoreException(SecretStoreFailure failure, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public SecretStoreFailure Failure { get; } = failure;
}

/// <summary>
/// Holds secret material. Consumers address secrets only by <see cref="SecretReference"/> and never see the
/// storage mechanism, so another provider (for example Azure Key Vault) can be registered without changing them.
/// </summary>
public interface ISecretStore
{
    /// <summary>Human-readable name of the store shown on the Credential Management page.</summary>
    string DisplayName { get; }

    bool SupportsProvider(string provider);
    Task<string?> GetSecretAsync(SecretReference reference, CancellationToken cancellationToken = default);
    Task SetSecretAsync(SecretReference reference, string secret, CancellationToken cancellationToken = default);
    Task<bool> DeleteSecretAsync(SecretReference reference, CancellationToken cancellationToken = default);
    Task<bool> SecretExistsAsync(SecretReference reference, CancellationToken cancellationToken = default);
}

/// <summary>Persists credential metadata (never the secret).</summary>
public interface ICredentialMetadataStore
{
    Task<IReadOnlyList<CredentialMetadata>> ListAsync(CancellationToken cancellationToken = default);
    Task<CredentialMetadata?> GetAsync(string credentialId, CancellationToken cancellationToken = default);
    Task SaveAsync(CredentialMetadata metadata, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string credentialId, CancellationToken cancellationToken = default);
}

/// <summary>A credential as shown to engineers: metadata and status only.</summary>
public sealed record CredentialView(CredentialMetadata Metadata, CredentialStatus Status, string StatusMessage, string SecretStoreName);

/// <summary>Input for a new credential. The secret is passed separately and is never stored in this record.</summary>
public sealed record NewCredential(
    string DisplayName,
    CredentialType CredentialType,
    SecretReference? SecretReference,
    DateTimeOffset? ExpirationDateUtc,
    string? AssociatedCustomerId,
    string? AssociatedTenantId);

/// <summary>Add, replace, delete and validate credentials. Secrets are write-only through this service.</summary>
public interface ICredentialService
{
    string SecretStoreName { get; }
    Task<IReadOnlyList<CredentialView>> ListAsync(CancellationToken cancellationToken = default);
    Task<CredentialView?> FindByReferenceAsync(SecretReference reference, CancellationToken cancellationToken = default);
    Task<OperationResult<CredentialMetadata>> AddAsync(NewCredential credential, string secret, CancellationToken cancellationToken = default);
    Task<OperationResult<CredentialMetadata>> ReplaceAsync(string credentialId, string secret, DateTimeOffset? expirationDateUtc, CancellationToken cancellationToken = default);
    Task<OperationResult> DeleteAsync(string credentialId, CancellationToken cancellationToken = default);
    Task<CredentialView?> ValidateAsync(string credentialId, CancellationToken cancellationToken = default);
}

public sealed class CredentialService(
    ISecretStore secretStore,
    ICredentialMetadataStore metadataStore,
    IAuditService audit,
    CredentialSettings settings,
    TimeProvider timeProvider,
    ILogger<CredentialService> logger) : ICredentialService
{
    public string SecretStoreName => secretStore.DisplayName;

    public async Task<IReadOnlyList<CredentialView>> ListAsync(CancellationToken cancellationToken = default)
    {
        var metadata = await metadataStore.ListAsync(cancellationToken);
        var views = new List<CredentialView>(metadata.Count);
        foreach (var item in metadata.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.CredentialId, StringComparer.Ordinal))
            views.Add(await EvaluateAsync(item, cancellationToken));
        return views;
    }

    public async Task<CredentialView?> FindByReferenceAsync(SecretReference reference, CancellationToken cancellationToken = default)
    {
        var metadata = (await metadataStore.ListAsync(cancellationToken)).FirstOrDefault(item => item.SecretReference == reference);
        return metadata is null ? null : await EvaluateAsync(metadata, cancellationToken);
    }

    public async Task<OperationResult<CredentialMetadata>> AddAsync(NewCredential credential, string secret, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credential.DisplayName))
            return OperationResult<CredentialMetadata>.Failure("CredentialNameRequired", "A display name is required.");
        if (string.IsNullOrEmpty(secret))
            return OperationResult<CredentialMetadata>.Failure("SecretRequired", "The secret value is required.");

        SecretReference reference;
        try
        {
            reference = credential.SecretReference ?? new SecretReference(
                SecretReference.OperatingSystemProvider,
                $"credentials/{credential.CredentialType.ToString().ToLowerInvariant()}/{StableIdentifier.FromDisplayName(credential.DisplayName)}");
        }
        catch (DomainException exception)
        {
            return OperationResult<CredentialMetadata>.Failure("SecretReferenceInvalid", exception.Message, null, "Use letters and digits in the display name, or enter a secret reference.");
        }

        if (!secretStore.SupportsProvider(reference.Provider))
            return OperationResult<CredentialMetadata>.Failure("SecretProviderUnsupported", $"The secret store does not handle the '{reference.Provider}' provider.", null, "Use a reference that starts with 'os:'.");
        if ((await metadataStore.ListAsync(cancellationToken)).Any(item => item.SecretReference == reference))
            return OperationResult<CredentialMetadata>.Failure("CredentialExists", $"A credential already uses the reference '{reference}'.", null, "Replace the existing credential instead.");

        var now = timeProvider.GetUtcNow();
        var metadata = new CredentialMetadata($"cred-{Guid.NewGuid():N}", credential.DisplayName.Trim(), credential.CredentialType, reference.Provider,
            now, now, credential.ExpirationDateUtc, reference, credential.AssociatedCustomerId, credential.AssociatedTenantId);
        var stored = await StoreAsync(metadata, secret, cancellationToken);
        if (!stored.Succeeded) return OperationResult<CredentialMetadata>.Failure(stored.Error!);
        await audit.RecordAsync(AuditEventTypes.CredentialCreated, AuditOutcome.Success, $"Credential '{metadata.DisplayName}' created.", Properties(metadata), cancellationToken);
        return OperationResult<CredentialMetadata>.Success(metadata);
    }

    public async Task<OperationResult<CredentialMetadata>> ReplaceAsync(string credentialId, string secret, DateTimeOffset? expirationDateUtc, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(secret))
            return OperationResult<CredentialMetadata>.Failure("SecretRequired", "The replacement secret value is required.");
        var existing = await metadataStore.GetAsync(credentialId, cancellationToken);
        if (existing is null)
            return OperationResult<CredentialMetadata>.Failure("CredentialMissing", "The credential no longer exists.");
        var updated = existing with { UpdatedAtUtc = timeProvider.GetUtcNow(), ExpirationDateUtc = expirationDateUtc ?? existing.ExpirationDateUtc };
        var stored = await StoreAsync(updated, secret, cancellationToken);
        if (!stored.Succeeded) return OperationResult<CredentialMetadata>.Failure(stored.Error!);
        await audit.RecordAsync(AuditEventTypes.CredentialUpdated, AuditOutcome.Success, $"Credential '{updated.DisplayName}' replaced.", Properties(updated), cancellationToken);
        return OperationResult<CredentialMetadata>.Success(updated);
    }

    public async Task<OperationResult> DeleteAsync(string credentialId, CancellationToken cancellationToken = default)
    {
        var existing = await metadataStore.GetAsync(credentialId, cancellationToken);
        if (existing is null) return OperationResult.Failure("CredentialMissing", "The credential no longer exists.");
        try
        {
            await secretStore.DeleteSecretAsync(existing.SecretReference, cancellationToken);
            await metadataStore.DeleteAsync(credentialId, cancellationToken);
        }
        catch (SecretStoreException exception)
        {
            logger.LogError(exception, "Secret for credential {CredentialId} could not be deleted ({Failure})", credentialId, exception.Failure);
            return OperationResult.Failure("SecretDeleteFailed", "The secret could not be deleted.", exception.Message, "Check access to the credential store directory.");
        }

        await audit.RecordAsync(AuditEventTypes.CredentialDeleted, AuditOutcome.Success, $"Credential '{existing.DisplayName}' deleted.", Properties(existing), cancellationToken);
        return OperationResult.Success();
    }

    public async Task<CredentialView?> ValidateAsync(string credentialId, CancellationToken cancellationToken = default)
    {
        var metadata = await metadataStore.GetAsync(credentialId, cancellationToken);
        if (metadata is null) return null;
        var view = await EvaluateAsync(metadata, cancellationToken, readSecret: true);
        if (view.Status == CredentialStatus.ExpiresSoon)
            await audit.RecordAsync(AuditEventTypes.CredentialExpiring, AuditOutcome.Warning, $"Credential '{metadata.DisplayName}' expires soon.", Properties(metadata), cancellationToken);
        else if (view.Status == CredentialStatus.Expired)
            await audit.RecordAsync(AuditEventTypes.CredentialExpired, AuditOutcome.Warning, $"Credential '{metadata.DisplayName}' has expired.", Properties(metadata), cancellationToken);
        return view;
    }

    private async Task<OperationResult> StoreAsync(CredentialMetadata metadata, string secret, CancellationToken cancellationToken)
    {
        try
        {
            await secretStore.SetSecretAsync(metadata.SecretReference, secret, cancellationToken);
            await metadataStore.SaveAsync(metadata, cancellationToken);
            return OperationResult.Success();
        }
        catch (SecretStoreException exception)
        {
            logger.LogError(exception, "Credential {CredentialId} could not be stored ({Failure})", metadata.CredentialId, exception.Failure);
            return OperationResult.Failure("SecretStoreFailed", "The secret could not be stored securely.", exception.Message,
                exception.Failure == SecretStoreFailure.UnsupportedPlatform ? "Use the application on Windows, where secrets are protected with DPAPI." : "Check access to the credential store directory.");
        }
        catch (IOException exception)
        {
            logger.LogError(exception, "Credential metadata {CredentialId} could not be written", metadata.CredentialId);
            return OperationResult.Failure("CredentialMetadataFailed", "The credential metadata could not be saved.", exception.GetType().Name, "Check access to the credential store directory.");
        }
    }

    private async Task<CredentialView> EvaluateAsync(CredentialMetadata metadata, CancellationToken cancellationToken, bool readSecret = false)
    {
        try
        {
            var exists = readSecret
                ? await secretStore.GetSecretAsync(metadata.SecretReference, cancellationToken) is { Length: > 0 }
                : await secretStore.SecretExistsAsync(metadata.SecretReference, cancellationToken);
            if (!exists)
                return new(metadata, CredentialStatus.Missing, "The secret is not present in the secret store on this workstation. Replace it to continue.", secretStore.DisplayName);
        }
        catch (SecretStoreException exception)
        {
            return new(metadata, CredentialStatus.Invalid, exception.Failure switch
            {
                SecretStoreFailure.Unreadable => "The secret exists but cannot be decrypted by this Windows user. Re-enter it.",
                SecretStoreFailure.UnsupportedPlatform => "Secrets can only be read on Windows.",
                _ => exception.Message
            }, secretStore.DisplayName);
        }

        var status = metadata.EvaluateExpiration(timeProvider.GetUtcNow(), settings.ExpirationWarningWindow);
        var message = status switch
        {
            CredentialStatus.Expired => $"Expired on {metadata.ExpirationDateUtc:yyyy-MM-dd}.",
            CredentialStatus.ExpiresSoon => $"Expires on {metadata.ExpirationDateUtc:yyyy-MM-dd} (within {settings.ExpirationWarningDays} days).",
            _ => "Configured"
        };
        return new(metadata, status, message, secretStore.DisplayName);
    }

    private static IReadOnlyDictionary<string, string?> Properties(CredentialMetadata metadata) => new Dictionary<string, string?>
    {
        ["CredentialId"] = metadata.CredentialId,
        ["CredentialType"] = metadata.CredentialType.ToString(),
        ["SecretReference"] = metadata.SecretReference.ToString(),
        ["CustomerId"] = metadata.AssociatedCustomerId,
        ["TenantId"] = metadata.AssociatedTenantId
    };
}
