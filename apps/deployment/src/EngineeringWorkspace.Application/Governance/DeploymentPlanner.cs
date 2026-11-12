using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Domain.Customers;

namespace EngineeringWorkspace.Application.Governance;

/// <summary>JSON output for policy bodies: indented, and placeholders such as &lt;RING&gt; kept as written (not \u003C escaped).</summary>
public static class DeploymentJson
{
    public static JsonSerializerOptions Options { get; } = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

/// <summary>One policy to create: a Conditional Access policy, or the catalog controls of one source policy merged together.</summary>
public sealed record DeploymentUnit(string Key, string Name, string Kind, string ApiVersion, string Resource, string Body, IReadOnlyList<CatalogControl> Controls, string? FollowUp)
{
    public IReadOnlyList<string> Placeholders => DeploymentVariables.In(Body);
}

/// <summary>
/// Turns catalog controls into the policies to create, following the policy repository's merge rules
/// (PolicyRepository/Intune/README.md): Conditional Access policies one by one in report-only state; Intune and endpoint
/// security controls grouped by source policy (repository folder) and Graph resource into one policy each. Settings
/// catalog settings are combined by setting definition (first value wins) and renumbered; other Graph objects have their
/// properties unioned onto one object.
/// </summary>
public static class DeploymentPlanner
{
    private static readonly string[] ServerManaged = ["id", "createdDateTime", "lastModifiedDateTime", "modifiedDateTime", "version", "settingCount", "isAssigned", "creationSource"];

    public static IReadOnlyList<DeploymentUnit> Build(IEnumerable<CatalogControl> controls)
    {
        var units = new List<DeploymentUnit>();
        foreach (var control in controls.Where(control => control.Body.Length > 0 && control.GraphResource.Length > 0 && control.Kind == CatalogKinds.ConditionalAccess))
        {
            var body = ChangeExecutionService.ReportOnly(control.Body);
            units.Add(new DeploymentUnit(control.Id, ChangeExecutionService.DisplayName(body) ?? control.Name, control.Kind, "v1.0", control.GraphResource, body, [control], null));
        }

        foreach (var group in controls.Where(control => control.Body.Length > 0 && control.GraphResource.Length > 0 && control.Kind != CatalogKinds.ConditionalAccess)
                     .GroupBy(control => (control.GraphResource, Folder: Folder(control.Path))))
        {
            var members = group.OrderBy(control => control.Id, StringComparer.Ordinal).ToArray();
            var isSettingsCatalog = group.Key.GraphResource.EndsWith("configurationPolicies", StringComparison.OrdinalIgnoreCase);
            var nameProperty = isSettingsCatalog ? "name" : "displayName";
            JsonObject body;
            if (members.Length == 1)
            {
                body = Clean(members[0].Body);
            }
            else
            {
                body = Clean(members[0].Body);
                var leaf = group.Key.Folder.Split('/').Last();
                body[nameProperty] = $"<RING>-{leaf}";
                body["description"] = $"Merged by Secure Plus from {members.Length} catalog controls ({string.Join(", ", members.Select(control => control.Id))}) of the source policy '{leaf}'.";
                if (isSettingsCatalog) MergeSettings(body, members.Skip(1));
                else MergeProperties(body, members.Skip(1));
            }

            var text = body.ToJsonString(DeploymentJson.Options);
            var followUp = group.Key.GraphResource.Contains("ManagedAppProtections", StringComparison.OrdinalIgnoreCase)
                ? "App protection: choose the targeted apps and assign the policy in Intune after it is created."
                : "Created unassigned: assign it to the change's ring groups in Intune.";
            units.Add(new DeploymentUnit($"{group.Key.GraphResource}|{group.Key.Folder}", body[nameProperty]?.GetValue<string>() ?? group.Key.Folder, members[0].Kind, "beta",
                group.Key.GraphResource, text, members, followUp));
        }

        return units;
    }

    private static string Folder(string path)
    {
        var normalized = path.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? normalized : normalized[..slash];
    }

    private static JsonObject Clean(string body)
    {
        var node = JsonNode.Parse(body)!.AsObject();
        foreach (var name in ServerManaged) node.Remove(name);
        return node;
    }

    private static void MergeSettings(JsonObject body, IEnumerable<CatalogControl> others)
    {
        var settings = body["settings"] as JsonArray ?? [];
        body["settings"] = settings;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in settings) seen.Add(DefinitionId(setting) ?? Guid.NewGuid().ToString());
        foreach (var other in others)
        {
            if (Clean(other.Body)["settings"] is not JsonArray more) continue;
            foreach (var setting in more.ToArray())
            {
                var id = DefinitionId(setting);
                if (id is not null && !seen.Add(id)) continue;
                more.Remove(setting);
                settings.Add(setting);
            }
        }

        for (var index = 0; index < settings.Count; index++)
            if (settings[index] is JsonObject item) item["id"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? DefinitionId(JsonNode? setting) => setting?["settingInstance"]?["settingDefinitionId"]?.GetValue<string>();

    private static void MergeProperties(JsonObject body, IEnumerable<CatalogControl> others)
    {
        foreach (var other in others)
        foreach (var (name, value) in Clean(other.Body).ToArray())
        {
            if (name is "displayName" or "description" || body.ContainsKey(name)) continue;
            body[name] = value?.DeepClone();
        }
    }
}

/// <summary>
/// Customer deployment variables: the values that replace &lt;Placeholder&gt; tokens in policy bodies (ring, group names or
/// object IDs, terms of use...). Stored in the customer profile's workspace settings as "Deploy.&lt;Name&gt;".
/// Group variables may hold a group's display name; it is resolved to its object ID in the tenant when applying.
/// </summary>
public static partial class DeploymentVariables
{
    public const string Prefix = "Deploy.";
    public const string PolicyNamePrefix = "PolicyNamePrefix";

    [GeneratedRegex(@"<([A-Za-z][A-Za-z0-9_-]*)>")]
    private static partial Regex Pattern();

    public static IReadOnlyDictionary<string, string> Descriptions { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["RING"] = "Deployment ring written into policy names: Pilot, UAT or Production.",
        ["AdministratorGroup"] = "Entra ID group of administrator accounts (name or object ID).",
        ["EmergencyAccessAccountsGroup"] = "Group of the emergency access (break-glass) accounts, excluded from Conditional Access.",
        ["ExclusionTempGroup"] = "Group of users temporarily excluded during the pilot.",
        ["ExclusionPermGroup"] = "Group of users permanently excluded (approved exceptions).",
        ["SynchronizationServiceAccountsGroup"] = "Group of directory synchronisation service accounts.",
        ["AADP2Group"] = "Group of users licensed for Entra ID P2 (risk-based policies).",
        ["FrontlineQrCodeGroup"] = "Frontline group allowed to sign in with QR code + PIN on shared devices (single-factor; CA-213/CA-214 restrict it to compliant shared devices).",
        ["FactoryResetProtectionAccount"] = "Google account allowed to unlock factory-reset Android devices.",
        ["TermsOfUseId"] = "Object ID of the Entra ID terms of use agreement.",
        ["GsaSecurityProfileId"] = "Object ID of the Global Secure Access security profile.",
        ["DialerAppUrlScheme"] = "iOS dialer app URL scheme for app protection L3.",
        [PolicyNamePrefix] = "Optional prefix for every policy name, for example 'GDS - '.",
    };

    public static bool IsGroup(string name) => name.EndsWith("Group", StringComparison.OrdinalIgnoreCase);

    /// <summary>Placeholder names in a body (without the angle brackets).</summary>
    public static IReadOnlyList<string> In(string body) => Pattern().Matches(body).Select(match => match.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static Dictionary<string, string> Load(CustomerProfile customer) =>
        customer.WorkspaceSettings.Where(pair => pair.Key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key[Prefix.Length..], pair => Clean(pair.Key[Prefix.Length..], pair.Value), StringComparer.OrdinalIgnoreCase);

    /// <summary>Values are trimmed, except the name prefix whose trailing space is meaningful ("GDS - ").</summary>
    private static string Clean(string name, string value) => string.Equals(name, PolicyNamePrefix, StringComparison.OrdinalIgnoreCase) ? value.TrimStart() : value.Trim();

    public static CustomerProfile Save(CustomerProfile customer, IReadOnlyDictionary<string, string> values)
    {
        var settings = new Dictionary<string, string>(customer.WorkspaceSettings, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            if (string.IsNullOrWhiteSpace(value)) settings.Remove(Prefix + name);
            else settings[Prefix + name] = Clean(name, value);
        }

        return customer with { WorkspaceSettings = settings };
    }

    /// <summary>Replaces the placeholders that have values (JSON-escaped), %OrganizationId% with the tenant ID, and applies the name prefix.</summary>
    public static string Apply(string body, IReadOnlyDictionary<string, string> values, Guid? tenantId = null)
    {
        var text = Pattern().Replace(body, match => values.TryGetValue(match.Groups[1].Value, out var value) && value.Length > 0
            ? JsonEncodedText.Encode(value, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString()
            : match.Value);
        if (tenantId is { } id) text = text.Replace("%OrganizationId%", id.ToString(), StringComparison.Ordinal);
        if (values.TryGetValue(PolicyNamePrefix, out var prefix) && prefix.Length > 0)
        {
            var node = JsonNode.Parse(text)!.AsObject();
            foreach (var property in new[] { "displayName", "name" })
                if (node[property]?.GetValue<string>() is { } name && !name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    node[property] = prefix + name;
                    break;
                }

            text = node.ToJsonString(DeploymentJson.Options);
        }

        return text;
    }
}
