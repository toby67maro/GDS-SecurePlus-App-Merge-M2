namespace EngineeringWorkspace.Domain.Policies;

/// <summary>One policy JSON file discovered in a policy repository (EaaS v3 <c>pol.Artifact</c>).</summary>
public sealed record PolicyArtifact(
    string Id,
    string DisplayName,
    string SourcePath,
    string SourceRepository,
    string ContentHash,
    DateTimeOffset LastModifiedUtc,
    string RawJson,
    string? CompanionPowerShellPath = null);
