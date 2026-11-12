using System.Text.Json;
using System.Text.RegularExpressions;
using EngineeringWorkspace.Application.Credentials;
using EngineeringWorkspace.Domain.Credentials;
using EngineeringWorkspace.Domain.Security;
using EngineeringWorkspace.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Infrastructure.Security;

/// <summary>Credential metadata as JSON files under data/Credentials/metadata. Never contains secret values.</summary>
public sealed partial class JsonCredentialMetadataStore(string credentialDirectory, ILogger<JsonCredentialMetadataStore> logger) : ICredentialMetadataStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _directory = Path.Combine(credentialDirectory, "metadata");

    public async Task<IReadOnlyList<CredentialMetadata>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_directory)) return [];
        var results = new List<CredentialMetadata>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            if (await ReadAsync(path, cancellationToken) is { } metadata) results.Add(metadata);
        }

        return results;
    }

    public async Task<CredentialMetadata?> GetAsync(string credentialId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(credentialId);
        return File.Exists(path) ? await ReadAsync(path, cancellationToken) : null;
    }

    public async Task SaveAsync(CredentialMetadata metadata, CancellationToken cancellationToken = default)
    {
        var record = new CredentialRecord(metadata.CredentialId, metadata.DisplayName, metadata.CredentialType.ToString(), metadata.Provider,
            metadata.CreatedAtUtc, metadata.UpdatedAtUtc, metadata.ExpirationDateUtc, metadata.SecretReference.ToString(),
            metadata.AssociatedCustomerId, metadata.AssociatedTenantId);
        await AtomicFile.WriteAllBytesAsync(PathFor(metadata.CredentialId), JsonSerializer.SerializeToUtf8Bytes(record, Options), cancellationToken);
    }

    public Task<bool> DeleteAsync(string credentialId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(credentialId);
        if (!File.Exists(path)) return Task.FromResult(false);
        File.Delete(path);
        return Task.FromResult(true);
    }

    private async Task<CredentialMetadata?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var record = await JsonSerializer.DeserializeAsync<CredentialRecord>(stream, Options, cancellationToken);
            if (record is null || !SecretReference.TryParse(record.SecretReference, out var reference) ||
                !Enum.TryParse<CredentialType>(record.CredentialType, out var type)) return null;
            return new CredentialMetadata(record.CredentialId, record.DisplayName, type, record.Provider, record.CreatedAtUtc, record.UpdatedAtUtc,
                record.ExpirationDateUtc, reference, record.AssociatedCustomerId, record.AssociatedTenantId);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Credential metadata {Path} could not be read", path);
            return null;
        }
    }

    private string PathFor(string credentialId)
    {
        if (!CredentialIdPattern().IsMatch(credentialId)) throw new ArgumentException("The credential ID contains unsupported characters.", nameof(credentialId));
        return Path.Combine(_directory, $"{credentialId}.json");
    }

    private sealed record CredentialRecord(
        string CredentialId,
        string DisplayName,
        string CredentialType,
        string Provider,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        DateTimeOffset? ExpirationDateUtc,
        string SecretReference,
        string? AssociatedCustomerId,
        string? AssociatedTenantId);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialIdPattern();
}
