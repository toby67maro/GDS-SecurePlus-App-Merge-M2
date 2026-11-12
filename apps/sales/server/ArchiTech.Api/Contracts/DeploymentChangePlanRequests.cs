namespace ArchiTech.Api.Contracts;

public sealed class CreateDeploymentChangePlanRequest
{
    public Guid AssessmentRunId { get; init; }
    public string? Title { get; init; }
    public string? Notes { get; init; }
    public string[]? ControlIds { get; init; }
}

public sealed class DecideDeploymentChangePlanRequest
{
    public string? Decision { get; init; }
    public string? Notes { get; init; }
}
