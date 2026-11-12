using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Catalog;

namespace EngineeringWorkspace.Infrastructure.Assessments;

/// <summary>
/// Evaluates every automatically assessable catalog control against a tenant snapshot:
/// <list type="bullet">
/// <item>Conditional Access: matched by baseline name, otherwise by intent (users, targets, conditions, grant and session controls); state decides Pass/Partial/Fail.</item>
/// <item>Settings catalog and endpoint security: every expected settingDefinitionId/value must be configured in some policy.</item>
/// <item>Graph object policies (compliance, device restrictions, update rings, app protection): property values on a policy of the same type.</item>
/// <item>Tenant checks: the GDS Microsoft 365 baseline checks (<see cref="TenantChecks"/>).</item>
/// </list>
/// Manual catalog entries (standards, Microsoft security baselines) are not part of an automatic assessment; manual tenant settings are
/// reported as Manual (not scored) with their click path.
/// </summary>
public sealed partial class AssessmentEvaluator : IAssessmentEvaluator
{
    public IReadOnlyList<AssessmentResultItem> Evaluate(ControlCatalog catalog, TenantSnapshot snapshot)
    {
        var settings = new SettingsIndex(snapshot.Get(TenantDatasets.ConfigurationPolicies));
        var conditionalAccess = snapshot.Get(TenantDatasets.ConditionalAccessPolicies);
        var results = new List<AssessmentResultItem>();
        foreach (var control in catalog.Controls)
        {
            var result = control.Evaluation switch
            {
                CatalogEvaluations.ConditionalAccess => EvaluateConditionalAccess(control, conditionalAccess),
                CatalogEvaluations.SettingsCatalog => EvaluateSettings(control, settings),
                CatalogEvaluations.GraphObject => EvaluateGraphObject(control, snapshot),
                CatalogEvaluations.Script => EvaluateScript(control, snapshot.Get(TenantDatasets.Scripts)),
                CatalogEvaluations.TenantCheck => TenantChecks.Evaluate(control, snapshot),
                CatalogEvaluations.TenantSetting => TenantSettingRules.Evaluate(control, snapshot),
                // Manual tenant settings are listed (not scored) with their portal and click path, so every area has its checklist.
                _ when control.Kind == CatalogKinds.TenantSetting => TenantSettingRules.Evaluate(control, snapshot),
                _ => null
            };
            if (result is not null) results.Add(result);
        }

        results.AddRange(TenantChecks.Informational(snapshot));
        return results;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Conditional Access
    // ------------------------------------------------------------------------------------------------------------

    private static AssessmentResultItem EvaluateConditionalAccess(CatalogControl control, TenantDataset policies)
    {
        if (!policies.IsCollected) return NotEvaluated(control, policies);
        using var document = JsonDocument.Parse(control.Body);
        var expected = document.RootElement;
        var expectedName = NormalizeName(control.Name);
        var expectedFeatures = Features(expected);

        JsonElement? best = null;
        var bestScore = 0.0;
        var matchedBy = string.Empty;
        foreach (var policy in policies.Items)
        {
            var name = NormalizeName(Text(policy, "displayName"));
            if (name.Length > 0 && (name == expectedName || name.EndsWith(expectedName, StringComparison.Ordinal)))
            {
                best = policy;
                matchedBy = "name";
                break;
            }

            var features = Features(policy);
            var grantMatches = expectedFeatures.Where(feature => feature.StartsWith("grant:", StringComparison.Ordinal)).ToHashSet()
                .SetEquals(features.Where(feature => feature.StartsWith("grant:", StringComparison.Ordinal)));
            if (!grantMatches) continue;
            var union = expectedFeatures.Union(features).Count();
            var score = union == 0 ? 0 : (double)expectedFeatures.Intersect(features).Count() / union;
            if (score > bestScore)
            {
                bestScore = score;
                best = policy;
                matchedBy = $"intent ({score:P0} match)";
            }
        }

        if (best is not { } match || (matchedBy != "name" && bestScore < 0.75))
            return Item(control, AssessmentResultValues.Missing, $"No Conditional Access policy matches '{control.Name}'. Expected: {control.Setting}.",
                $"Deploy Conditional Access control {control.Number} in report-only state, review sign-ins, then turn it on.");

        var state = Text(match, "state");
        var display = Text(match, "displayName");
        return state switch
        {
            "enabled" => Item(control, AssessmentResultValues.Pass, $"Policy '{display}' is on (matched by {matchedBy}).", null),
            "enabledForReportingButNotEnforced" => Item(control, AssessmentResultValues.Partial, $"Policy '{display}' is in report-only state (matched by {matchedBy}).",
                "Review the report-only sign-in results, then turn the policy on."),
            _ => Item(control, AssessmentResultValues.Fail, $"Policy '{display}' exists but is off (matched by {matchedBy}).", "Turn the policy on, or report-only first.")
        };
    }

    /// <summary>Intent features of a Conditional Access policy used for matching when names differ.</summary>
    private static HashSet<string> Features(JsonElement policy)
    {
        var features = new HashSet<string>(StringComparer.Ordinal);
        var conditions = Property(policy, "conditions");
        var users = Property(conditions, "users");
        foreach (var value in Strings(users, "includeUsers")) features.Add($"users:{value.ToLowerInvariant()}");
        if (Strings(users, "includeRoles").Any()) features.Add("users:roles");
        if (Property(users, "includeGuestsOrExternalUsers") is { ValueKind: JsonValueKind.Object }) features.Add("users:guests");
        var applications = Property(conditions, "applications");
        foreach (var value in Strings(applications, "includeApplications")) features.Add($"apps:{value.ToLowerInvariant()}");
        foreach (var value in Strings(applications, "includeUserActions")) features.Add($"action:{value.ToLowerInvariant()}");
        foreach (var value in Strings(conditions, "clientAppTypes").Where(value => value != "all")) features.Add($"client:{value.ToLowerInvariant()}");
        foreach (var value in Strings(conditions, "signInRiskLevels")) features.Add($"signinrisk:{value}");
        foreach (var value in Strings(conditions, "userRiskLevels")) features.Add($"userrisk:{value}");
        foreach (var value in Strings(Property(conditions, "platforms"), "includePlatforms")) features.Add($"platform:{value.ToLowerInvariant()}");
        if (Text(Property(conditions, "authenticationFlows"), "transferMethods") is { Length: > 0 } transfer) features.Add($"flow:{transfer}");
        var grant = Property(policy, "grantControls");
        foreach (var value in Strings(grant, "builtInControls")) features.Add($"grant:{value}");
        if (Property(grant, "authenticationStrength") is { ValueKind: JsonValueKind.Object }) features.Add("grant:authenticationStrength");
        var session = Property(policy, "sessionControls");
        if (session.ValueKind == JsonValueKind.Object)
            foreach (var property in session.EnumerateObject().Where(property => property.Value.ValueKind == JsonValueKind.Object))
                features.Add($"session:{property.Name}");
        return features;
    }

    /// <summary>Strips the baseline number and ring ("201-PROD-Base Protection-...") and non-alphanumerics.</summary>
    private static string NormalizeName(string name)
    {
        var withoutNumber = NumberAndRing().Replace(name.Replace("<RING>-", string.Empty, StringComparison.Ordinal), string.Empty);
        var builder = new StringBuilder();
        foreach (var character in withoutNumber.ToLowerInvariant())
            if (char.IsLetterOrDigit(character)) builder.Append(character);
        return builder.ToString();
    }

    [GeneratedRegex(@"^\s*\d+\s*-\s*(?:[A-Za-z0-9]{1,12}\s*-\s*)?", RegexOptions.CultureInvariant)]
    private static partial Regex NumberAndRing();

    // ------------------------------------------------------------------------------------------------------------
    // Settings catalog
    // ------------------------------------------------------------------------------------------------------------

    private sealed class SettingsIndex
    {
        public SettingsIndex(TenantDataset policies)
        {
            Dataset = policies;
            if (!policies.IsCollected) return;
            foreach (var policy in policies.Items)
            {
                var name = Text(policy, "name");
                if (Property(policy, "settings") is not { ValueKind: JsonValueKind.Array } items) continue;
                foreach (var item in items.EnumerateArray())
                {
                    var leaves = new List<(string Path, string Value)>();
                    Flatten(Property(item, "settingInstance"), leaves);
                    foreach (var (path, value) in leaves)
                    {
                        if (!Values.TryGetValue(path, out var list)) Values[path] = list = [];
                        list.Add((value, name));
                    }
                }
            }
        }

        public TenantDataset Dataset { get; }
        public Dictionary<string, List<(string Value, string Policy)>> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Flattens a settings catalog setting instance into (settingDefinitionId, normalized value) leaves (mirrors the catalog generator).</summary>
    private static void Flatten(JsonElement instance, List<(string Path, string Value)> leaves)
    {
        if (instance.ValueKind != JsonValueKind.Object) return;
        var definition = Text(instance, "settingDefinitionId");
        if (Property(instance, "choiceSettingValue") is { ValueKind: JsonValueKind.Object } choice)
        {
            leaves.Add((definition, Normalize(Property(choice, "value"))));
            foreach (var child in Elements(choice, "children")) Flatten(child, leaves);
        }
        else if (Property(instance, "simpleSettingValue") is { ValueKind: JsonValueKind.Object } simple)
        {
            leaves.Add((definition, Normalize(Property(simple, "value"))));
        }
        else if (Property(instance, "simpleSettingCollectionValue") is { ValueKind: JsonValueKind.Array } simpleCollection)
        {
            leaves.Add((definition, string.Join("|", simpleCollection.EnumerateArray().Select(item => Normalize(Property(item, "value"))).Order(StringComparer.Ordinal))));
        }
        else if (Property(instance, "choiceSettingCollectionValue") is { ValueKind: JsonValueKind.Array } choiceCollection)
        {
            foreach (var item in choiceCollection.EnumerateArray())
            {
                leaves.Add((definition, Normalize(Property(item, "value"))));
                foreach (var child in Elements(item, "children")) Flatten(child, leaves);
            }
        }
        else
        {
            var group = Property(instance, "groupSettingCollectionValue");
            if (group.ValueKind != JsonValueKind.Array) group = Property(instance, "groupSettingValue");
            var groups = group.ValueKind switch
            {
                JsonValueKind.Array => group.EnumerateArray().ToArray(),
                JsonValueKind.Object => [group],
                _ => []
            };
            foreach (var item in groups)
            foreach (var child in Elements(item, "children"))
                Flatten(child, leaves);
        }
    }

    private static AssessmentResultItem EvaluateSettings(CatalogControl control, SettingsIndex index)
    {
        if (!index.Dataset.IsCollected) return NotEvaluated(control, index.Dataset);
        var passed = 0;
        var mismatches = new List<string>();
        var missing = 0;
        foreach (var expected in control.Expected)
        {
            var value = Normalize(expected.Value);
            if (!index.Values.TryGetValue(expected.Path, out var found))
            {
                missing++;
                continue;
            }

            if (found.Any(item => item.Value == value)) passed++;
            else mismatches.Add($"'{Short(found[0].Value)}' in '{found[0].Policy}'");
        }

        var total = control.Expected.Count;
        if (passed == total)
            return Item(control, AssessmentResultValues.Pass, $"Configured: {control.Setting} = {control.Value}.", null);
        if (missing == total)
            return Item(control, AssessmentResultValues.Missing, $"'{control.Setting}' is not configured in any Intune policy.",
                $"Deploy control {control.Number} ({control.Category}) with value '{control.Value}'.");
        if (passed > 0)
            return Item(control, AssessmentResultValues.Partial, $"{passed} of {total} expected values configured; differs: {string.Join("; ", mismatches.Take(3))}.",
                $"Set '{control.Setting}' to '{control.Value}'.");
        return Item(control, AssessmentResultValues.Fail, $"Configured differently: {string.Join("; ", mismatches.Take(3))}; expected '{control.Value}'.",
            $"Set '{control.Setting}' to '{control.Value}'.");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Graph object policies
    // ------------------------------------------------------------------------------------------------------------

    private static AssessmentResultItem EvaluateGraphObject(CatalogControl control, TenantSnapshot snapshot)
    {
        var type = control.OdataType;
        var (datasetName, filterByType) = type switch
        {
            _ when type.Contains("androidManagedAppProtection", StringComparison.OrdinalIgnoreCase) => (TenantDatasets.AndroidAppProtections, false),
            _ when type.Contains("iosManagedAppProtection", StringComparison.OrdinalIgnoreCase) => (TenantDatasets.IosAppProtections, false),
            _ when type.Contains("CompliancePolicy", StringComparison.OrdinalIgnoreCase) => (TenantDatasets.CompliancePolicies, true),
            _ when type.Contains("DriverUpdateProfile", StringComparison.OrdinalIgnoreCase) => (TenantDatasets.DriverUpdateProfiles, false),
            _ => (TenantDatasets.DeviceConfigurations, true)
        };
        var dataset = snapshot.Get(datasetName);
        if (!dataset.IsCollected) return NotEvaluated(control, dataset);
        var candidates = dataset.Items.Where(item => !filterByType || string.Equals(Text(item, "@odata.type"), type, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length == 0)
            return Item(control, AssessmentResultValues.Missing, $"No {TypeName(type)} policy exists.", $"Deploy control {control.Number} ({control.Category}).");

        var passed = 0;
        var differs = new List<string>();
        foreach (var expected in control.Expected)
        {
            var value = Normalize(expected.Value);
            var configured = candidates.Where(item => item.TryGetProperty(expected.Path, out _)).ToArray();
            if (configured.Any(item => Normalize(Property(item, expected.Path)) == value)) passed++;
            else if (configured.FirstOrDefault() is { ValueKind: JsonValueKind.Object } first)
                differs.Add($"{expected.Path} = '{Short(Normalize(Property(first, expected.Path)))}' in '{Text(first, "displayName")}'");
        }

        var total = control.Expected.Count;
        if (total == 0 || passed == total)
            return Item(control, AssessmentResultValues.Pass, $"Configured in a {TypeName(type)} policy: {control.Setting} = {control.Value}.", null);
        return passed > 0
            ? Item(control, AssessmentResultValues.Partial, $"{passed} of {total} values configured; {string.Join("; ", differs.Take(3))}.", $"Set '{control.Setting}' to '{control.Value}'.")
            : Item(control, AssessmentResultValues.Fail, differs.Count > 0 ? $"Configured differently: {string.Join("; ", differs.Take(3))}." : $"'{control.Setting}' is not set to '{control.Value}'.",
                $"Set '{control.Setting}' to '{control.Value}'.");
    }

    private static AssessmentResultItem EvaluateScript(CatalogControl control, TenantDataset scripts)
    {
        if (!scripts.IsCollected) return NotEvaluated(control, scripts);
        var expected = NormalizeName(control.Name);
        var found = scripts.Items.FirstOrDefault(item => NormalizeName(Text(item, "displayName")).Contains(expected, StringComparison.Ordinal));
        return found.ValueKind == JsonValueKind.Object
            ? Item(control, AssessmentResultValues.Pass, $"Platform script '{Text(found, "displayName")}' exists.", null)
            : Item(control, AssessmentResultValues.Missing, "The platform script is not deployed.", $"Deploy platform script {control.Number}.");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Shared helpers
    // ------------------------------------------------------------------------------------------------------------

    internal static AssessmentResultItem Item(CatalogControl control, string result, string? evidence, string? recommendation) =>
        new(control.Id, Area(control), string.IsNullOrEmpty(control.Number) ? control.Name : $"{control.Number} {control.Name}", result, control.Severity, evidence, recommendation);

    internal static AssessmentResultItem NotEvaluated(CatalogControl control, TenantDataset dataset) =>
        Item(control, AssessmentResultValues.NotEvaluated,
            dataset.Status == DatasetStatus.Forbidden
                ? $"The app registration is not permitted to read {dataset.Name} ({dataset.Message})."
                : $"{dataset.Name} is not available in this tenant ({dataset.Message}).",
            dataset.Status == DatasetStatus.Forbidden ? "Run Update permissions automatically on the customer's Tenant App Registration page." : null);

    internal static string Area(CatalogControl control) => control.Kind switch
    {
        CatalogKinds.ConditionalAccess => "Conditional Access",
        CatalogKinds.EndpointSecurity => $"Endpoint Security: {control.Category}",
        CatalogKinds.Intune => $"Intune: {control.Platform}",
        CatalogKinds.TenantSetting => control.Product.Length > 0 ? $"{control.Product}: {control.FeatureArea}" : control.Category,
        _ => control.Category
    };

    private static string TypeName(string odataType) => odataType.Replace("#microsoft.graph.", string.Empty, StringComparison.Ordinal);

    internal static string Normalize(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => (value.GetString() ?? string.Empty).Trim().ToLowerInvariant(),
        JsonValueKind.Number => value.TryGetDecimal(out var number) ? number.ToString(CultureInfo.InvariantCulture) : value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.Array => string.Join("|", value.EnumerateArray().Select(Normalize).Order(StringComparer.Ordinal)),
        _ => string.Join(";", value.EnumerateObject().Where(property => !property.Name.StartsWith('@')).OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => $"{property.Name.ToLowerInvariant()}={Normalize(property.Value)}"))
    };

    private static string Short(string value) => value.Length <= 80 ? value : value[..80] + "...";

    internal static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    internal static string Text(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? string.Empty : string.Empty;

    internal static IEnumerable<string> Strings(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Array } array
            ? array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)
            : [];

    internal static IEnumerable<JsonElement> Elements(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];
}
