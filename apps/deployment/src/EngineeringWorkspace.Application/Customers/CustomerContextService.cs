using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.State;
using EngineeringWorkspace.Application.Workspaces;
using EngineeringWorkspace.Domain.Auditing;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Customers;
using EngineeringWorkspace.Domain.Tenants;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Application.Customers;

/// <summary>Raised when the current customer or tenant changes.</summary>
public sealed record CustomerContextChanged(
    CustomerProfile? Previous,
    CustomerProfile? Current,
    TenantConfiguration? CurrentTenant,
    DateTimeOffset ChangedAtUtc,
    string Reason);

/// <summary>Persists customer profiles (data/Customers/{id}/Customer.ini). Holds references, never secrets.</summary>
public interface ICustomerProfileStore
{
    Task<IReadOnlyList<CustomerProfile>> ListAsync(CancellationToken cancellationToken = default);
    Task<CustomerProfile?> LoadAsync(CustomerId id, CancellationToken cancellationToken = default);
    Task SaveAsync(CustomerProfile profile, CancellationToken cancellationToken = default);
}

/// <summary>Owns the current customer, its profile and tenant context.</summary>
public interface ICustomerContextService
{
    CustomerProfile? CurrentCustomer { get; }
    TenantConfiguration? CurrentTenant { get; }
    event EventHandler<CustomerContextChanged>? CustomerChanged;
    Task<OperationResult<CustomerProfile>> SelectAsync(CustomerId id, string? tenantKey, string reason, CancellationToken cancellationToken = default);
    Task ClearAsync(string reason, CancellationToken cancellationToken = default);

    /// <summary>Replaces the in-memory current profile after it was saved, without changing the selection.</summary>
    void Refresh(CustomerProfile profile);
}

public sealed class CustomerContextService(
    ICustomerProfileStore store,
    IWorkspaceService workspaces,
    IApplicationStateStore stateStore,
    IAuditService audit,
    AuditContext auditContext,
    TimeProvider timeProvider,
    ILogger<CustomerContextService> logger) : ICustomerContextService
{
    private CustomerProfile? _current;
    private TenantConfiguration? _tenant;

    public CustomerProfile? CurrentCustomer => _current;
    public TenantConfiguration? CurrentTenant => _tenant;
    public event EventHandler<CustomerContextChanged>? CustomerChanged;

    public async Task<OperationResult<CustomerProfile>> SelectAsync(CustomerId id, string? tenantKey, string reason, CancellationToken cancellationToken = default)
    {
        var profile = await store.LoadAsync(id, cancellationToken);
        if (profile is null)
            return OperationResult<CustomerProfile>.Failure("CustomerMissing", $"Customer '{id}' does not exist.");

        var previous = _current;
        _current = profile;
        _tenant = tenantKey is null
            ? profile.PrimaryTenant
            : profile.Tenants.FirstOrDefault(tenant => string.Equals(tenant.TenantKey, tenantKey, StringComparison.OrdinalIgnoreCase)) ?? profile.PrimaryTenant;
        auditContext.CustomerId = profile.Id.Value;
        var state = await stateStore.LoadAsync(cancellationToken);
        await stateStore.SaveAsync(state with { LastCustomerId = profile.Id.Value }, cancellationToken);
        await workspaces.SetCurrentCustomerAsync(profile.Id, _tenant?.TenantId, cancellationToken);
        CustomerChanged?.Invoke(this, new CustomerContextChanged(previous, profile, _tenant, timeProvider.GetUtcNow(), reason));
        await audit.RecordAsync(AuditEventTypes.CustomerSelected, AuditOutcome.Success, $"Customer '{profile.Name}' selected.",
            new Dictionary<string, string?> { ["CustomerId"] = profile.Id.Value, ["TenantKey"] = _tenant?.TenantKey, ["Reason"] = reason }, cancellationToken);
        logger.LogInformation("Customer {CustomerId} selected ({Reason})", profile.Id.Value, reason);
        return OperationResult<CustomerProfile>.Success(profile);
    }

    public async Task ClearAsync(string reason, CancellationToken cancellationToken = default)
    {
        var previous = _current;
        if (previous is null) return;
        _current = null;
        _tenant = null;
        auditContext.CustomerId = null;
        await workspaces.SetCurrentCustomerAsync(null, null, cancellationToken);
        CustomerChanged?.Invoke(this, new CustomerContextChanged(previous, null, null, timeProvider.GetUtcNow(), reason));
        logger.LogInformation("Customer context cleared ({Reason})", reason);
    }

    public void Refresh(CustomerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (_current is null || _current.Id != profile.Id) return;
        var tenantKey = _tenant?.TenantKey;
        _current = profile;
        _tenant = profile.Tenants.FirstOrDefault(tenant => string.Equals(tenant.TenantKey, tenantKey, StringComparison.OrdinalIgnoreCase)) ?? profile.PrimaryTenant;
        CustomerChanged?.Invoke(this, new CustomerContextChanged(profile, profile, _tenant, timeProvider.GetUtcNow(), "Updated"));
    }
}
