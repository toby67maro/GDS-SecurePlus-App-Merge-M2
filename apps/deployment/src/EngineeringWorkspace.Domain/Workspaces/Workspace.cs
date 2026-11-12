using EngineeringWorkspace.Domain.Common;

namespace EngineeringWorkspace.Domain.Workspaces;

/// <summary>
/// An engineer's working context for one engagement: its directory, current customer and references to the
/// database and Golden Tenant baseline in use. Assessment, planning, deployment and reporting data are
/// attached in later milestones (M2-M8).
/// </summary>
public sealed record Workspace(
    WorkspaceId Id,
    string Name,
    string Directory,
    CustomerId? CurrentCustomerId,
    Guid? TenantId,
    string Environment,
    string? DatabaseReference,
    string? GoldenTenantReference,
    IReadOnlyDictionary<string, string> IntegrationSettings,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ModifiedAtUtc,
    string? ThemeKey = null,
    string? Description = null)
{
    public static Workspace Create(WorkspaceId id, string name, string directory, string environment, DateTimeOffset nowUtc) =>
        new(id, name, directory, null, null, environment, null, null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), nowUtc, nowUtc);
}
