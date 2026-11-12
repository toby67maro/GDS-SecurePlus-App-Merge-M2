namespace ArchiTech.Api.Contracts;

public sealed class CreateAssessmentRequest
{
    public string? Name { get; init; }
    public Guid? EngagementId { get; init; }
    public bool StartUnassessed { get; init; }
}

public sealed class UpdateAssessmentResultRequest
{
    public string? Status { get; init; }
    public string? Notes { get; init; }
    public string? Evidence { get; init; }
    public string? ExceptionReason { get; init; }
}
