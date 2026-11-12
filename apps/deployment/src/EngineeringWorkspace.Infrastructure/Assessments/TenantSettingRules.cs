using System.Globalization;
using System.Text.Json;
using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Catalog;

namespace EngineeringWorkspace.Infrastructure.Assessments;

/// <summary>
/// Evaluates tenant-setting controls (4.0 beta.2): each control carries a rule (attribute "Rule", JSON) naming a dataset the scan
/// read through Microsoft Graph, DNS or read-only PowerShell, a property path, an operator and the expected value.
/// <code>{"dataset":"authenticationMethodsPolicy","path":"authenticationMethodConfigurations[id=Sms].state","op":"eq","value":"disabled"}</code>
/// Path syntax: dot-separated property names (case-insensitive); <c>[name=value]</c> keeps array items whose property equals the value;
/// <c>[*]</c> or an array without a filter expands every item. <c>where</c> (optional) filters the dataset's items first, the same way.
/// <c>where</c> may hold several filters separated by ";" (all must match). <c>requires</c> (optional) is a rule of the same shape for the licence the setting needs.
/// <c>all</c> (instead of dataset/path) is a list of rules that must all pass. A list value for contains/notcontains means every one / none of them.
/// Operators: eq, ne, in, notin, gte, lte, contains, notcontains, exists, notexists, empty, notempty. <c>match</c>: "all" (default) or "any".
/// </summary>
public static class TenantSettingRules
{
    public const string RuleAttribute = "Rule";

    /// <summary>A rule; <see cref="Requires"/> (optional, same shape) is the licence or feature the setting needs: when the tenant does not
    /// meet it, the control is reported as Info (not applicable) and not scored.</summary>
    public sealed record Rule(string Dataset, string Path, string Op, JsonElement? Value, string Match = "all", string? Where = null, Rule? Requires = null,
        IReadOnlyList<Rule>? All = null);

    public static Rule? Parse(CatalogControl control)
    {
        if (!control.Attributes.TryGetValue(RuleAttribute, out var json) || string.IsNullOrWhiteSpace(json)) return null;
        using var document = JsonDocument.Parse(json);
        return Parse(document.RootElement);
    }

    private static Rule Parse(JsonElement root)
    {
        string Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
        return new Rule(Text("dataset"), Text("path"), Text("op").ToLowerInvariant() is { Length: > 0 } op ? op : "eq",
            root.TryGetProperty("value", out var expected) ? expected.Clone() : null, Text("match") is { Length: > 0 } match ? match.ToLowerInvariant() : "all",
            Text("where") is { Length: > 0 } where ? where : null,
            root.TryGetProperty("requires", out var requires) && requires.ValueKind == JsonValueKind.Object ? Parse(requires) : null,
            root.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.Array
                ? all.EnumerateArray().Where(part => part.ValueKind == JsonValueKind.Object).Select(Parse).ToArray()
                : null);
    }

    public static AssessmentResultItem Evaluate(CatalogControl control, TenantSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(snapshot);
        Rule? rule;
        try
        {
            rule = Parse(control);
        }
        catch (JsonException exception)
        {
            return AssessmentEvaluator.Item(control, AssessmentResultValues.NotEvaluated, $"The control's rule is not valid JSON: {exception.Message}", null);
        }

        if (rule is null || (rule.Dataset.Length == 0 && rule.All is not { Count: > 0 }))
            return AssessmentEvaluator.Item(control, AssessmentResultValues.Manual, Manual(control), $"Required: {control.Value}");
        if (rule.Requires is { Dataset.Length: > 0 } requires && snapshot.Get(requires.Dataset) is { IsCollected: true } && !Test(requires, snapshot, out _))
        {
            var needs = control.Attributes.GetValueOrDefault("License requirement") is { Length: > 0 } license ? license : $"{requires.Path} {requires.Op} {Display(requires.Value)}";
            return AssessmentEvaluator.Item(control, AssessmentResultValues.Info, $"Not applicable: the tenant does not have {needs}.", null);
        }

        if (rule is { All.Count: > 0 } && rule.Dataset.Length == 0)
        {
            if (rule.All.Select(part => snapshot.Get(part.Dataset)).FirstOrDefault(dataset => !dataset.IsCollected) is { } missing)
                return AssessmentEvaluator.NotEvaluated(control, missing);
            var results = rule.All.Select(part => (Passed: Test(part, snapshot, out var shownPart), Text: $"{part.Dataset} {part.Path}: {shownPart}")).ToArray();
            var allPassed = results.All(result => result.Passed);
            return AssessmentEvaluator.Item(control, allPassed ? AssessmentResultValues.Pass : AssessmentResultValues.Fail,
                string.Join("; ", results.Select(result => (result.Passed ? "OK " : "NOT MET ") + result.Text)), allPassed ? null : $"Required: {control.Value}");
        }

        var dataset = snapshot.Get(rule.Dataset);
        if (!dataset.IsCollected) return AssessmentEvaluator.NotEvaluated(control, dataset);
        var passed = Test(rule, snapshot, out var shown);
        return AssessmentEvaluator.Item(control, passed ? AssessmentResultValues.Pass : AssessmentResultValues.Fail,
            $"{rule.Dataset} {rule.Path}: {shown}", passed ? null : $"Required: {control.Value}");
    }

    /// <summary>Whether the tenant meets the rule (the dataset must have been collected); <paramref name="shown"/> is what was observed.</summary>
    public static bool Test(Rule rule, TenantSnapshot snapshot, out string shown)
    {
        IEnumerable<JsonElement> items = snapshot.Get(rule.Dataset).Items;
        if (rule.Where is { } where) items = items.Where(item => where.Split(';').All(part => Matches(item, part)));
        var observed = items.SelectMany(item => Select(item, rule.Path)).ToArray();
        shown = observed.Length == 0 ? "(not set)" : string.Join(", ", observed.Take(5).Select(Display)) + (observed.Length > 5 ? $" … ({observed.Length} values)" : string.Empty);
        return rule.Op switch
        {
            "exists" => observed.Any(value => value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)),
            "notexists" => observed.All(value => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined),
            "empty" => observed.All(IsEmpty),
            "notempty" => observed.Any(value => !IsEmpty(value)),
            _ => observed.Length > 0 && (rule.Match == "any" ? observed.Any(value => Compare(value, rule)) : observed.All(value => Compare(value, rule)))
        };
    }

    private static string Manual(CatalogControl control)
    {
        var portal = control.Attributes.GetValueOrDefault("Admin portal");
        var path = control.Attributes.GetValueOrDefault("Click path");
        return string.IsNullOrWhiteSpace(path) ? "Verify manually; there is no read-only API for this setting." : $"Verify manually in {portal}: {path}".Trim();
    }

    private static bool Compare(JsonElement actual, Rule rule)
    {
        var expected = rule.Value;
        return rule.Op switch
        {
            "eq" => expected is { } value && Same(actual, value),
            "ne" => expected is not { } value || !Same(actual, value),
            "in" => expected is { ValueKind: JsonValueKind.Array } list && list.EnumerateArray().Any(value => Same(actual, value)),
            "notin" => expected is not { ValueKind: JsonValueKind.Array } list || !list.EnumerateArray().Any(value => Same(actual, value)),
            "gte" => Number(actual) is { } a && expected is { } e && Number(e) is { } b && a >= b,
            "lte" => Number(actual) is { } a && expected is { } e && Number(e) is { } b && a <= b,
            // A list of expected values: "contains" needs every one, "notcontains" needs none of them.
            "contains" => expected is { } value && (value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().All(item => Contains(actual, item)) : Contains(actual, value)),
            "notcontains" => expected is not { } value || (value.ValueKind == JsonValueKind.Array ? !value.EnumerateArray().Any(item => Contains(actual, item)) : !Contains(actual, value)),
            _ => false
        };
    }

    private static bool Contains(JsonElement actual, JsonElement value) => actual.ValueKind switch
    {
        JsonValueKind.Array => actual.EnumerateArray().Any(item => Same(item, value)),
        JsonValueKind.String => (actual.GetString() ?? string.Empty).Contains(Normalize(value), StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static bool IsEmpty(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.Array => value.GetArrayLength() == 0,
        JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()),
        _ => false
    };

    private static bool Same(JsonElement actual, JsonElement expected) =>
        string.Equals(Normalize(actual), Normalize(expected), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => (value.GetString() ?? string.Empty).Trim(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetDecimal().ToString(CultureInfo.InvariantCulture),
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => value.GetRawText()
    };

    private static decimal? Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDecimal(),
        JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null
    };

    private static string Display(JsonElement? value) => value is { } element ? Display(element) : string.Empty;

    private static string Display(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Array => $"[{string.Join(", ", value.EnumerateArray().Take(5).Select(Display))}]",
        _ => Normalize(value)
    };

    /// <summary>"isEnabled=true" or "Identity=Default" style filter on an item.</summary>
    private static bool Matches(JsonElement item, string filter)
    {
        var parts = filter.Split('=', 2);
        if (parts.Length != 2) return true;
        if (parts[0].Trim().Equals("@odata.type", StringComparison.OrdinalIgnoreCase))
            return item.ValueKind == JsonValueKind.Object && item.TryGetProperty("@odata.type", out var type) &&
                   string.Equals(type.GetString(), parts[1].Trim(), StringComparison.OrdinalIgnoreCase);
        return Select(item, parts[0].Trim()).Any(value => string.Equals(Normalize(value), parts[1].Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Values at a path (see the class summary for the syntax).</summary>
    public static IReadOnlyList<JsonElement> Select(JsonElement root, string path)
    {
        IEnumerable<JsonElement> current = [root];
        if (string.IsNullOrWhiteSpace(path)) return current.ToArray();
        // "@odata.type" contains a dot: protect it while splitting.
        foreach (var raw in path.Replace("@odata.type", "@odata~type", StringComparison.OrdinalIgnoreCase).Split('.'))
        {
            var segment = raw.Trim().Replace("@odata~type", "@odata.type", StringComparison.OrdinalIgnoreCase);
            string? filter = null;
            var bracket = segment.IndexOf('[', StringComparison.Ordinal);
            if (bracket >= 0)
            {
                filter = segment[(bracket + 1)..].TrimEnd(']');
                segment = segment[..bracket];
            }

            var next = new List<JsonElement>();
            foreach (var element in current)
            {
                var value = element;
                if (segment.Length > 0)
                {
                    if (element.ValueKind != JsonValueKind.Object) continue;
                    var property = element.EnumerateObject().FirstOrDefault(candidate => string.Equals(candidate.Name, segment, StringComparison.OrdinalIgnoreCase));
                    if (property.Value.ValueKind == JsonValueKind.Undefined) continue;
                    value = property.Value;
                }

                if (value.ValueKind == JsonValueKind.Array && (filter is not null || segment.Length == 0))
                    next.AddRange(value.EnumerateArray().Where(item => filter is null or "*" || Matches(item, filter)));
                else
                    next.Add(value);
            }

            current = next;
        }

        return current.ToArray();
    }
}
