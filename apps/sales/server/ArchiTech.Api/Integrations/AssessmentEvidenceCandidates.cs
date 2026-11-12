using System.Globalization;

namespace ArchiTech.Api.Integrations;

public sealed record AssessmentEvidenceCandidate(string ControlId, string Summary, string Limitations);

public static class AssessmentEvidenceCandidates
{
    public static IReadOnlyList<AssessmentEvidenceCandidate> FromConditionalAccess(LiveEvidenceResult result)
    {
        var evidence = result.Evidence;
        var observed = result.ObservedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var provenance = $"Operator-copied preview (not independently verified once saved); tenant {result.TenantId:D}; observed {observed}; source Microsoft Graph " +
            "GET /v1.0/identity/conditionalAccess/policies.";
        return
        [
            new("AUTH-004",
                $"{provenance} {evidence.EnabledMfaPolicies} enabled policies list MFA, out of {evidence.TotalPolicies} policies.",
                "A count does not establish MFA enforcement or coverage. Review targeting, exclusions, grant operators, security defaults and other enforcement."),
            new("AUTH-002",
                $"{provenance} {evidence.EnabledLegacyBlockPolicies} enabled policies list legacy-client blocking, out of {evidence.TotalPolicies} policies.",
                "A count does not establish that legacy authentication is blocked for all users or clients. Review targeting, exclusions and other enforcement.")
        ];
    }
}
