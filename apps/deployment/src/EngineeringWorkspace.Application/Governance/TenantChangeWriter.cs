using EngineeringWorkspace.Domain.Tenants;

namespace EngineeringWorkspace.Application.Governance;

/// <summary>Result of creating one object in a tenant.</summary>
public sealed record TenantWriteResult(bool Succeeded, string? CreatedId, string? Error)
{
    public static TenantWriteResult Created(string? id) => new(true, id, null);
    public static TenantWriteResult Failed(string error) => new(false, null, error);
}

/// <summary>
/// Writes approved policies to a customer tenant through Microsoft Graph with the tenant's own app registration
/// (app-only). Only <see cref="ChangeExecutionService.ApplyToTenantAsync"/> calls it, after the passcode and the
/// confirmation prompt; every write is audited.
/// </summary>
public interface ITenantChangeWriter
{
    /// <summary>Display names (displayName or name) of the objects already at <paramref name="resource"/>, to avoid duplicates.</summary>
    Task<IReadOnlySet<string>> ExistingNamesAsync(TenantConfiguration tenant, string apiVersion, string resource, CancellationToken cancellationToken);

    /// <summary>The object ID of the group with this display name, or null when there is none (or several).</summary>
    Task<string?> ResolveGroupIdAsync(TenantConfiguration tenant, string displayName, CancellationToken cancellationToken);

    /// <summary>POSTs <paramref name="jsonBody"/> to <paramref name="resource"/>.</summary>
    Task<TenantWriteResult> CreateAsync(TenantConfiguration tenant, string apiVersion, string resource, string jsonBody, CancellationToken cancellationToken);
}
