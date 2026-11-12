using System.Security.Claims;
using ArchiTech.Api.Domain;

namespace ArchiTech.Api.Security;

/// <summary>
/// Row-level ownership: salespeople see only the engagements they created; Management and
/// Administrator roles see every engagement.
/// </summary>
public static class OwnerScope
{
    public static bool CanSeeAll(ClaimsPrincipal user) =>
        user.IsInRole(DeploymentRoles.Management) || user.IsInRole(DeploymentRoles.Administrator);

    public static string? Id(ClaimsPrincipal user)
    {
        var ids = user.Claims
            .Where(claim => claim.Type is ClaimTypes.NameIdentifier or "sub" or "oid")
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        // The first identifier wins; Entra supplies several that all refer to one user.
        return ids.Length == 0 ? null : Truncate(ids[0].Trim(), 200);
    }

    public static string? Email(ClaimsPrincipal user)
    {
        var email = user.FindFirst(ClaimTypes.Email)?.Value
            ?? user.FindFirst("email")?.Value
            ?? user.FindFirst("preferred_username")?.Value
            ?? user.FindFirst(ClaimTypes.Upn)?.Value;
        return string.IsNullOrWhiteSpace(email) ? null : Truncate(email.Trim(), 320);
    }

    public static string DisplayName(ClaimsPrincipal user) =>
        user.FindFirst("name")?.Value
        ?? user.FindFirst(ClaimTypes.Name)?.Value
        ?? Email(user)
        ?? "Unknown";

    public static IQueryable<Engagement> Apply(IQueryable<Engagement> query, ClaimsPrincipal user)
    {
        if (CanSeeAll(user)) return query;
        var id = Id(user);
        return id is null
            ? query.Where(engagement => false)
            : query.Where(engagement => engagement.OwnerId == id);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
