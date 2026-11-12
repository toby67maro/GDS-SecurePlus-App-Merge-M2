using System.Text.RegularExpressions;

namespace ArchiTech.Api.Domain;

public sealed record DiscoveryCrosswalkLink(
    string DecisionId,
    string Reference,
    string BaselineId,
    string ControlId,
    string MatchType,
    bool AssessmentControlExists);

public sealed record DiscoveryCrosswalkResult(
    IReadOnlyList<DiscoveryCrosswalkLink> Links,
    IReadOnlyList<string> UnresolvedReferences);

public static partial class DiscoveryCrosswalk
{
    public static DiscoveryCrosswalkResult Build(
        IEnumerable<SecurePlusDecision> decisions,
        IEnumerable<SecurePlusBaselineSetting> settings,
        IEnumerable<DeploymentControl> controls)
    {
        var controlIds = controls.Where(item => item.IsActive)
            .Select(item => item.ControlId).ToHashSet(StringComparer.Ordinal);
        var baselines = settings
            .GroupBy(item => item.BaselineReference, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Control)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var candidates = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var baseline in baselines.Keys)
        {
            var match = ExpandedReference().Match(baseline);
            if (!match.Success) continue;
            var shortened = $"GBL-{match.Groups["phase"].Value}-{match.Groups["control"].Value}";
            if (!candidates.TryGetValue(shortened, out var matches))
                candidates.Add(shortened, matches = []);
            matches.Add(baseline);
        }

        var links = new List<DiscoveryCrosswalkLink>();
        var unresolved = new List<string>();
        foreach (var decision in decisions.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var references = decision.BaselineReferences.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal);
            foreach (var reference in references)
            {
                var matching = baselines.ContainsKey(reference) ? [reference] :
                    candidates.GetValueOrDefault(reference)?.Order(StringComparer.Ordinal).ToArray() ?? [];
                if (matching.Length == 0)
                {
                    unresolved.Add($"{decision.Id}: {reference}");
                    continue;
                }
                foreach (var baseline in matching)
                foreach (var control in baselines[baseline])
                    links.Add(new(decision.Id, reference, baseline, control,
                        reference == baseline ? "ExactReference" : "FormatCandidate",
                        controlIds.Contains(control)));
            }
        }
        return new(links, unresolved);
    }

    [GeneratedRegex(@"^GBL-(?<phase>P\d+)-T\d+-(?<control>[A-Z0-9]+-\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ExpandedReference();
}
