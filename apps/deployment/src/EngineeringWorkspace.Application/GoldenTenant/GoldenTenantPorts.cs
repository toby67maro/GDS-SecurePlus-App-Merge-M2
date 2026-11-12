using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.GoldenTenant;

namespace EngineeringWorkspace.Application.GoldenTenant;

/// <summary>Comparison of one expectation; implemented in M5.</summary>
public sealed record GoldenTenantComparisonItem(GoldenTenantExpectation Expectation, ComparisonStatus Status, string? ObservedValueJson, string? ExceptionId, string? Message);

public sealed record GoldenTenantComparisonResult(string BaselineId, string BaselineVersion, DateTimeOffset ComparedAtUtc, IReadOnlyList<GoldenTenantComparisonItem> Items);

/// <summary>Compares a tenant with the Golden Tenant baseline, honouring approved customer exceptions (M5).</summary>
public interface IGoldenTenantComparisonService
{
    Task<GoldenTenantComparisonResult> CompareAsync(CustomerId customerId, string tenantKey, GoldenTenantBaseline baseline, CancellationToken cancellationToken);
}
