using System.Data.Common;
using EngineeringWorkspace.Domain.Credentials;
using EngineeringWorkspace.Domain.Customers;
using EngineeringWorkspace.Domain.Workspaces;

namespace EngineeringWorkspace.Application.Persistence;

/// <summary>EaaS v3 cust.Tenant row.</summary>
public sealed record TenantRecord(int TenantId, int CustomerId, Guid TenantGuid, string PrimaryDomain, string CloudEnvironmentCode, string? Notes = null);

/// <summary>EaaS v3 pol.Repository row.</summary>
public sealed record RepositoryMetadata(short RepositoryId, string Code, string Name, string StorageTypeCode, string? RootUrl = null,
    string? DefaultBranch = null, string? Description = null, bool IsActive = true);

/// <summary>secureplus.AuditEvent row (Secure Plus extension).</summary>
public sealed record PersistenceAuditEvent(long AuditEventId, string EventType, string Summary, DateTimeOffset OccurredAtUtc, string? CustomerId = null, string? TenantId = null);

/// <summary>
/// Relational persistence of the EaaS v3 core slice (customer, tenant, repository) and the Secure Plus extension
/// tables. Workspace and customer persistence through the database is M2; the file stores are used today.
/// </summary>
public interface ISecurePlusPersistence
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<CustomerProfile?> GetCustomerAsync(int customerId, CancellationToken cancellationToken = default);
    Task UpsertCustomerAsync(CustomerProfile customer, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TenantRecord>> ListTenantsAsync(int customerId, CancellationToken cancellationToken = default);
    Task UpsertTenantAsync(TenantRecord tenant, CancellationToken cancellationToken = default);
    Task UpsertWorkspaceAsync(Workspace workspace, CancellationToken cancellationToken = default);
    Task UpsertCredentialMetadataAsync(CredentialMetadata metadata, CancellationToken cancellationToken = default);
    Task UpsertRepositoryAsync(RepositoryMetadata repository, CancellationToken cancellationToken = default);
    Task AppendAuditEventAsync(PersistenceAuditEvent auditEvent, CancellationToken cancellationToken = default);
    Task ExecuteTransactionAsync(Func<DbConnection, DbTransaction, CancellationToken, Task> operation, CancellationToken cancellationToken = default);
}
