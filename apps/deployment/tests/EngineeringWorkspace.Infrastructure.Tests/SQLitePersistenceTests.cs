using FluentAssertions;
using Microsoft.Data.Sqlite;
using EngineeringWorkspace.Application.Persistence;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Credentials;
using EngineeringWorkspace.Domain.Customers;
using EngineeringWorkspace.Domain.Security;
using EngineeringWorkspace.Domain.Workspaces;
using EngineeringWorkspace.Infrastructure.Persistence;

namespace EngineeringWorkspace.Infrastructure.Tests;

public sealed class SQLitePersistenceTests
{
    [Fact]
    public async Task Initializes_core_eaas_tables_and_persists_customer_tenant_workspace_and_metadata()
    {
        await using var database = TemporaryDatabase.Create();
        var persistence = new SQLiteEaasPersistence(database.Path);

        await persistence.InitializeAsync();
        await persistence.UpsertCustomerAsync(CustomerProfile.Create(new CustomerId("100"), "Contoso"));
        await persistence.UpsertTenantAsync(new TenantRecord(200, 100, Guid.NewGuid(), "contoso.com", "Commercial"));
        await persistence.UpsertWorkspaceAsync(Workspace.Create(new WorkspaceId("workspace-1"), "Contoso Workspace", "/workspace", "Test", DateTimeOffset.UtcNow) with { CurrentCustomerId = new CustomerId("100") });
        await persistence.UpsertCredentialMetadataAsync(new CredentialMetadata(
            "credential-1", "Contoso SQL", CredentialType.SqlLogin, "SQL Server",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, SecretReference.Parse("os:contoso/sql"),
            "100", "tenant-1"));

        var customer = await persistence.GetCustomerAsync(100);
        var tenants = await persistence.ListTenantsAsync(100);

        customer.Should().NotBeNull();
        customer!.Name.Should().Be("Contoso");
        tenants.Should().ContainSingle();
        tenants[0].PrimaryDomain.Should().Be("contoso.com");

        var tables = await ReadTableNamesAsync(database.Path);
        tables.Should().Contain(["cust.Customer", "cust.Tenant", "secureplus.Workspace", "secureplus.CredentialMetadata"]);
        tables.Should().NotContain("secureplus.Secret");
    }

    [Fact]
    public async Task Enforces_foreign_keys_and_unique_tenant_guids()
    {
        await using var database = TemporaryDatabase.Create();
        var persistence = new SQLiteEaasPersistence(database.Path);
        await persistence.InitializeAsync();
        await persistence.UpsertCustomerAsync(CustomerProfile.Create(new CustomerId("1"), "Customer"));
        var tenantGuid = Guid.NewGuid();

        await persistence.UpsertTenantAsync(new TenantRecord(1, 1, tenantGuid, "one.example", "Commercial"));
        var duplicate = () => persistence.UpsertTenantAsync(new TenantRecord(2, 1, tenantGuid, "two.example", "Commercial"));

        await duplicate.Should().ThrowAsync<SqliteException>();
        var missingCustomer = () => persistence.UpsertTenantAsync(new TenantRecord(3, 999, Guid.NewGuid(), "missing.example", "Commercial"));
        await missingCustomer.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task Rolls_back_transaction_when_operation_fails()
    {
        await using var database = TemporaryDatabase.Create();
        var persistence = new SQLiteEaasPersistence(database.Path);
        await persistence.InitializeAsync();

        var action = () => persistence.ExecuteTransactionAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO "cust.Customer"(CustomerId, Name, IsActive) VALUES (10, 'Rollback Customer', 1);
                INSERT INTO "cust.Tenant"(TenantId, CustomerId, TenantGuid, PrimaryDomain, CloudEnvironmentCode)
                VALUES (10, 10, 'not-a-guid-but-storage-is-text', 'rollback.example', 'Commercial');
                """;
            await command.ExecuteNonQueryAsync(token);
            throw new InvalidOperationException("test rollback");
        });

        await action.Should().ThrowAsync<InvalidOperationException>();
        (await persistence.GetCustomerAsync(10)).Should().BeNull();
    }

    private static async Task<IReadOnlyList<string>> ReadTableNamesAsync(string path)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }

    private sealed class TemporaryDatabase : IAsyncDisposable
    {
        private TemporaryDatabase(string path) => Path = path;
        public string Path { get; }
        public static TemporaryDatabase Create() => new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"secureplus-{Guid.NewGuid():N}.db"));
        public ValueTask DisposeAsync()
        {
            if (File.Exists(Path)) File.Delete(Path);
            return ValueTask.CompletedTask;
        }
    }
}
