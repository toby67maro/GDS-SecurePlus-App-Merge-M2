using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Tenants;

namespace EngineeringWorkspace.Application.Assessments;

// Interfaces only. Assessment providers and the engine are implemented in M4. Planned providers: Entra ID,
// Conditional Access, Intune, Defender, Purview, Exchange Online, SharePoint Online, Teams, Microsoft 365,
// Azure, endpoints, security configuration, licensing, identity, applications and devices.

/// <summary>A finding produced by an assessment provider for one control or setting.</summary>
public sealed record AssessmentFinding(string ProviderId, string ControlId, string? SettingPath, string Status, string? ObservedValueJson, string? Message);

public sealed record AssessmentRequest(CustomerId CustomerId, string TenantKey, IReadOnlyList<string> ProviderIds);

public sealed record AssessmentRunResult(Guid RunId, DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc, IReadOnlyList<AssessmentFinding> Findings);

/// <summary>Collects observations from one workload using an authenticated client from the Graph client factory.</summary>
public interface IAssessmentProvider
{
    string ProviderId { get; }
    IReadOnlyList<TenantCapability> RequiredCapabilities { get; }
    Task<IReadOnlyList<AssessmentFinding>> AssessAsync(AssessmentRequest request, CancellationToken cancellationToken);
}
