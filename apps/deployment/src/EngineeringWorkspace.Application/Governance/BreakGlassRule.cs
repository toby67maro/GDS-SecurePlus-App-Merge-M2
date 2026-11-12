using System.Text.Json;
using EngineeringWorkspace.Application.Catalog;

namespace EngineeringWorkspace.Application.Governance;

/// <summary>
/// Every Conditional Access policy must exclude the emergency-access (break-glass) group, so those accounts can always sign in
/// (user decision, 9 Nov 2026). Exempt: policies that target the break-glass group itself (CA-110) and workload-identity policies
/// (no users). Enforced by the catalogue tests, the policy editor and the deployment pre-check.
/// </summary>
public static class BreakGlassRule
{
    public const string GroupPlaceholder = "<EmergencyAccessAccountsGroup>";

    /// <summary>Why the policy breaks the rule, or null when it complies (or is not a Conditional Access policy with a body).</summary>
    public static string? Problem(CatalogControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.Kind == CatalogKinds.ConditionalAccess ? Problem(control.Id, control.Body) : null;
    }

    public static string? Problem(string name, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("conditions", out var conditions) || conditions.ValueKind != JsonValueKind.Object ||
                !conditions.TryGetProperty("users", out var users) || users.ValueKind != JsonValueKind.Object)
                return null;
            static string[] List(JsonElement parent, string property) =>
                parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
                    ? value.EnumerateArray().Select(item => item.GetString()).OfType<string>().ToArray()
                    : [];
            var includeUsers = List(users, "includeUsers");
            var includeGroups = List(users, "includeGroups");
            var includeRoles = List(users, "includeRoles");
            if (includeGroups.Contains(GroupPlaceholder, StringComparer.OrdinalIgnoreCase)) return null; // the policy for the break-glass accounts
            if (includeUsers.Length > 0 && includeUsers.All(user => string.Equals(user, "None", StringComparison.OrdinalIgnoreCase)) && includeGroups.Length == 0 && includeRoles.Length == 0)
                return null; // workload identities only
            return List(users, "excludeGroups").Contains(GroupPlaceholder, StringComparer.OrdinalIgnoreCase)
                ? null
                : $"{name} does not exclude the emergency-access (break-glass) group: add {GroupPlaceholder} to conditions.users.excludeGroups.";
        }
    }
}
