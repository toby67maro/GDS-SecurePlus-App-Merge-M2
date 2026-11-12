using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Credentials;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Domain.Security;
using EngineeringWorkspace.Infrastructure.Auditing;
using EngineeringWorkspace.Infrastructure.Database;
using EngineeringWorkspace.Infrastructure.Persistence;
using EngineeringWorkspace.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EngineeringWorkspace.Infrastructure.Tests;

public sealed class SharedSecretTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("secureplus-secrets-");

    [Fact]
    public void Organization_key_encryption_round_trips_and_is_bound_to_the_reference()
    {
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        OrganizationKeyService.TryParse(OrganizationKeyService.Text(key), out var parsed).Should().BeTrue();
        parsed.Should().Equal(key);
        OrganizationKeyService.TryParse("SPK1-not-a-key", out _).Should().BeFalse();

        var cipher = OrganizationKeyService.Encrypt(key, "s3cret-value", "os:contoso-primary");
        cipher.Should().NotContain("s3cret");
        OrganizationKeyService.Decrypt(key, cipher, "os:contoso-primary").Should().Be("s3cret-value");
        var moved = () => OrganizationKeyService.Decrypt(key, cipher, "os:fabrikam-primary");
        moved.Should().Throw<System.Security.Cryptography.CryptographicException>("ciphertext copied to another reference must not decrypt");
    }

    [Fact]
    public async Task Tenant_secrets_are_shared_encrypted_and_readable_only_on_computers_holding_the_key()
    {
        var paths = new PathSettings(_root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName,
            _root.FullName, Path.Combine(_root.FullName, "audit"), _root.FullName, _root.FullName, _root.FullName);
        var context = new AuditContext("tester");
        var redaction = new RedactionService();
        var audit = new JsonLinesAuditService(paths.AuditDirectory, context, redaction, TimeProvider.System, NullLogger<JsonLinesAuditService>.Instance);
        var connections = new DatabaseConnectionFactory(new UnavailableSecretStore(), paths);
        var provisioning = new DatabaseProvisioningService(connections, new DatabaseErrorTranslator(redaction), audit, context,
            new ApplicationSettings("id", "name", "title", "company", "Test", "dashboard/overview", false, false, "1.0.0"), TimeProvider.System,
            NullLogger<DatabaseProvisioningService>.Instance);
        var database = new DatabaseSettings { Provider = DatabaseProvider.SQLite, DatabaseName = Path.Combine(_root.FullName, "secureplus.db"), CommandTimeoutSeconds = 30 };
        await provisioning.CreateDatabaseAsync(database, null, CancellationToken.None);
        var ready = await provisioning.InitializeSchemaAsync(database, null, CancellationToken.None);
        var status = new Lazy<IDatabaseStatusService>(new FixedStatus(database, ready));
        var settings = new SharedSettingsStore(status, new SqlSettingsStore(connections, "PC-01"));

        // Computer 1: an earlier version left a secret in its local store; it creates the organization key.
        var localOne = new MemorySecrets();
        var reference = new SecretReference(SecretReference.OperatingSystemProvider, "contoso-primary");
        await localOne.SetSecretAsync(reference, "legacy-secret");
        var keysOne = new OrganizationKeyService(settings, new LocalSecrets(localOne), audit);
        (await keysOne.GetStateAsync()).Should().Be(OrganizationKeyState.NotCreated);
        var keyText = await keysOne.CreateAsync();
        var storeOne = new SharedSecretStore(new LocalSecrets(localOne), keysOne, status, connections, context, NullLogger<SharedSecretStore>.Instance);

        (await storeOne.GetSecretAsync(reference)).Should().Be("legacy-secret");
        await storeOne.SetSecretAsync(new SecretReference(SecretReference.OperatingSystemProvider, "fabrikam-primary"), "new-secret");
        (await localOne.SecretExistsAsync(new SecretReference(SecretReference.OperatingSystemProvider, "fabrikam-primary"))).Should().BeFalse("shared secrets are not kept locally");

        await using (var connection = new global::Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database.DatabaseName}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT group_concat(CipherText, '|') FROM \"secureplus.TenantSecret\";";
            var stored = (string)(await command.ExecuteScalarAsync())!;
            stored.Should().NotContain("legacy-secret").And.NotContain("new-secret");
            command.CommandText = "SELECT COUNT(*) FROM \"secureplus.TenantSecret\";";
            Convert.ToInt32(await command.ExecuteScalarAsync()).Should().Be(2);
        }

        // Computer 2 shares the database but not the key: it cannot read the secret until the key is entered.
        var localTwo = new MemorySecrets();
        var keysTwo = new OrganizationKeyService(settings, new LocalSecrets(localTwo), audit);
        var storeTwo = new SharedSecretStore(new LocalSecrets(localTwo), keysTwo, status, connections, context, NullLogger<SharedSecretStore>.Instance);
        (await keysTwo.GetStateAsync()).Should().Be(OrganizationKeyState.NotOnThisComputer);
        (await storeTwo.GetSecretAsync(reference)).Should().BeNull();
        (await keysTwo.EnrolAsync(OrganizationKeyService.Text(new byte[32]))).Should().Be("The key does not belong to this database.");
        (await keysTwo.EnrolAsync(keyText)).Should().BeNull();
        (await storeTwo.GetSecretAsync(reference)).Should().Be("legacy-secret");
        (await storeTwo.GetSecretAsync(new SecretReference(SecretReference.OperatingSystemProvider, "fabrikam-primary"))).Should().Be("new-secret");
    }

    public void Dispose()
    {
        global::Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _root.Delete(recursive: true);
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _secrets = [];
        public string DisplayName => "memory";
        public bool SupportsProvider(string provider) => true;
        public Task<string?> GetSecretAsync(SecretReference reference, CancellationToken cancellationToken = default) => Task.FromResult(_secrets.GetValueOrDefault(reference.ToString()));
        public Task SetSecretAsync(SecretReference reference, string secret, CancellationToken cancellationToken = default)
        {
            _secrets[reference.ToString()] = secret;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteSecretAsync(SecretReference reference, CancellationToken cancellationToken = default) => Task.FromResult(_secrets.Remove(reference.ToString()));
        public Task<bool> SecretExistsAsync(SecretReference reference, CancellationToken cancellationToken = default) => Task.FromResult(_secrets.ContainsKey(reference.ToString()));
    }

    private sealed class FixedStatus(DatabaseSettings settings, DatabaseStatusReport report) : IDatabaseStatusService
    {
        public DatabaseSettings Settings => settings;
        public DatabaseStatusReport? LatestStatus => report;
        public event EventHandler<DatabaseStatusReport>? StatusChanged { add { } remove { } }
        public void Publish(DatabaseStatusReport value) { }
        public Task<DatabaseStatusReport> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(report);
        public Task<OperationResult<string>> SaveSettingsAsync(DatabaseSettings value, CancellationToken cancellationToken = default) => Task.FromResult(OperationResult<string>.Success("unused"));
    }
}
