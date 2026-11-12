using System.Data.Common;
using System.Globalization;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Persistence;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Credentials;
using EngineeringWorkspace.Domain.Customers;
using EngineeringWorkspace.Domain.Workspaces;
using EngineeringWorkspace.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace EngineeringWorkspace.Infrastructure.Persistence;

/// <summary>SQLite adapter for the EaaS v3 core slice and the Secure Plus extension tables.</summary>
public sealed class SQLiteEaasPersistence(string databasePath) : ISecurePlusPersistence
{
    /// <summary>Applies the same versioned migrations as Database Configuration → Initialize Schema.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await SchemaMigrator.ApplyAsync(connection, 60, SchemaMigrations.For(DatabaseProvider.SQLite), Environment.UserName, "persistence", TimeProvider.System, null, cancellationToken);
    }

    public async Task<CustomerProfile?> GetCustomerAsync(int customerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CustomerId, Name
            FROM "cust.Customer"
            WHERE CustomerId = $id;
            """;
        command.Parameters.AddWithValue("$id", customerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var customer = CustomerProfile.Create(new CustomerId(reader.GetInt64(0).ToString(CultureInfo.InvariantCulture)), reader.GetString(1));
        await reader.DisposeAsync();

        await using var tenantCommand = connection.CreateCommand();
        tenantCommand.CommandText = """
            SELECT TenantGuid, PrimaryDomain
            FROM "cust.Tenant"
            WHERE CustomerId = $customerId
            ORDER BY TenantId
            LIMIT 1;
            """;
        tenantCommand.Parameters.AddWithValue("$customerId", customerId);
        await using var tenantReader = await tenantCommand.ExecuteReaderAsync(cancellationToken);
        if (!await tenantReader.ReadAsync(cancellationToken)) return customer;
        var tenantGuid = Guid.TryParse(tenantReader.GetString(0), out var parsed) ? parsed : (Guid?)null;
        return customer with
        {
            Tenants = [new Domain.Tenants.TenantConfiguration("primary", tenantReader.GetString(1), tenantReader.GetString(1), tenantGuid, tenantReader.GetString(1),
                Domain.Tenants.CloudEnvironment.Commercial, null)]
        };
    }

    public async Task UpsertCustomerAsync(CustomerProfile customer, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "cust.Customer"(CustomerId, Name, IsActive)
            VALUES ($id, $name, 1)
            ON CONFLICT(CustomerId) DO UPDATE SET Name = excluded.Name, IsActive = 1;
            """;
        command.Parameters.AddWithValue("$id", long.Parse(customer.Id.Value, CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$name", customer.Name);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TenantRecord>> ListTenantsAsync(int customerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TenantId, CustomerId, TenantGuid, PrimaryDomain, CloudEnvironmentCode, Notes
            FROM "cust.Tenant"
            WHERE CustomerId = $customerId
            ORDER BY TenantId;
            """;
        command.Parameters.AddWithValue("$customerId", customerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tenants = new List<TenantRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tenants.Add(new TenantRecord(
                reader.GetInt32(0),
                reader.GetInt32(1),
                Guid.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return tenants;
    }

    public async Task UpsertTenantAsync(TenantRecord tenant, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "cust.Tenant"(TenantId, CustomerId, TenantGuid, PrimaryDomain, CloudEnvironmentCode, Notes)
            VALUES ($id, $customerId, $guid, $domain, $cloud, $notes)
            ON CONFLICT(TenantId) DO UPDATE SET
                CustomerId = excluded.CustomerId,
                TenantGuid = excluded.TenantGuid,
                PrimaryDomain = excluded.PrimaryDomain,
                CloudEnvironmentCode = excluded.CloudEnvironmentCode,
                Notes = excluded.Notes;
            """;
        command.Parameters.AddWithValue("$id", tenant.TenantId);
        command.Parameters.AddWithValue("$customerId", tenant.CustomerId);
        command.Parameters.AddWithValue("$guid", tenant.TenantGuid.ToString());
        command.Parameters.AddWithValue("$domain", tenant.PrimaryDomain);
        command.Parameters.AddWithValue("$cloud", tenant.CloudEnvironmentCode);
        command.Parameters.AddWithValue("$notes", (object?)tenant.Notes ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertWorkspaceAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "secureplus.Workspace"(WorkspaceId, Name, Directory, CustomerId, CreatedAtUtc, ModifiedAtUtc)
            VALUES ($id, $name, $directory, $customerId, $created, $modified)
            ON CONFLICT(WorkspaceId) DO UPDATE SET
                Name = excluded.Name,
                Directory = excluded.Directory,
                CustomerId = excluded.CustomerId,
                ModifiedAtUtc = excluded.ModifiedAtUtc;
            """;
        command.Parameters.AddWithValue("$id", workspace.Id.Value);
        command.Parameters.AddWithValue("$name", workspace.Name);
        command.Parameters.AddWithValue("$directory", workspace.Directory);
        command.Parameters.AddWithValue("$customerId", TryParseCustomerId(workspace.CurrentCustomerId?.Value));
        command.Parameters.AddWithValue("$created", workspace.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$modified", workspace.ModifiedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertCredentialMetadataAsync(CredentialMetadata metadata, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "secureplus.CredentialMetadata"
                (CredentialId, DisplayName, CredentialType, Provider, CreatedAtUtc, UpdatedAtUtc, ExpirationDateUtc, SecretReference, CustomerId, TenantId)
            VALUES ($id, $displayName, $type, $provider, $created, $updated, $expiration, $reference, $customer, $tenant)
            ON CONFLICT(CredentialId) DO UPDATE SET
                DisplayName = excluded.DisplayName,
                CredentialType = excluded.CredentialType,
                Provider = excluded.Provider,
                UpdatedAtUtc = excluded.UpdatedAtUtc,
                ExpirationDateUtc = excluded.ExpirationDateUtc,
                SecretReference = excluded.SecretReference,
                CustomerId = excluded.CustomerId,
                TenantId = excluded.TenantId;
            """;
        command.Parameters.AddWithValue("$id", metadata.CredentialId);
        command.Parameters.AddWithValue("$displayName", metadata.DisplayName);
        command.Parameters.AddWithValue("$type", metadata.CredentialType.ToString());
        command.Parameters.AddWithValue("$provider", metadata.Provider);
        command.Parameters.AddWithValue("$created", metadata.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", metadata.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$expiration", (object?)metadata.ExpirationDateUtc?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$reference", metadata.SecretReference.ToString());
        command.Parameters.AddWithValue("$customer", (object?)metadata.AssociatedCustomerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$tenant", (object?)metadata.AssociatedTenantId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertRepositoryAsync(RepositoryMetadata repository, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "pol.Repository"(RepositoryId, Code, Name, StorageTypeCode, RootUrl, DefaultBranch, Description, IsActive)
            VALUES ($id, $code, $name, $storage, $url, $branch, $description, $active)
            ON CONFLICT(RepositoryId) DO UPDATE SET
                Code = excluded.Code,
                Name = excluded.Name,
                StorageTypeCode = excluded.StorageTypeCode,
                RootUrl = excluded.RootUrl,
                DefaultBranch = excluded.DefaultBranch,
                Description = excluded.Description,
                IsActive = excluded.IsActive;
            """;
        command.Parameters.AddWithValue("$id", repository.RepositoryId);
        command.Parameters.AddWithValue("$code", repository.Code);
        command.Parameters.AddWithValue("$name", repository.Name);
        command.Parameters.AddWithValue("$storage", repository.StorageTypeCode);
        command.Parameters.AddWithValue("$url", (object?)repository.RootUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$branch", (object?)repository.DefaultBranch ?? DBNull.Value);
        command.Parameters.AddWithValue("$description", (object?)repository.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$active", repository.IsActive ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AppendAuditEventAsync(PersistenceAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "secureplus.AuditEvent"
                (AuditEventId, EventType, Summary, OccurredAtUtc, CustomerId, TenantId)
            VALUES ($id, $type, $summary, $occurred, $customer, $tenant);
            """;
        command.Parameters.AddWithValue("$id", auditEvent.AuditEventId);
        command.Parameters.AddWithValue("$type", auditEvent.EventType);
        command.Parameters.AddWithValue("$summary", auditEvent.Summary);
        command.Parameters.AddWithValue("$occurred", auditEvent.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$customer", (object?)auditEvent.CustomerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$tenant", (object?)auditEvent.TenantId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ExecuteTransactionAsync(
        Func<DbConnection, DbTransaction, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await operation(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static object TryParseCustomerId(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : DBNull.Value;
}
