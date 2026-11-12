namespace ArchiTech.Api.Contracts;

public sealed class EstimateRequest
{
    public Dictionary<string, string[]?> Answers { get; init; } = new(StringComparer.Ordinal);
}

public sealed class CreateEngagementRequest
{
    public string? CompanyName { get; init; }
    public string? Salesperson { get; init; }
    public string? ContactName { get; init; }
    public string? ContactEmail { get; init; }
    public DateOnly? MeetingDate { get; init; }
    public int? Users { get; init; }
    public int? Endpoints { get; init; }
    public string? Notes { get; init; }
    public Dictionary<string, string[]?> Answers { get; init; } = new(StringComparer.Ordinal);
    public ExperienceSelection? Experience { get; init; }
}

/// <summary>Secure Plus experience choices captured in the sales form (industry, user groups and per-question answers).</summary>
public sealed class ExperienceSelection
{
    public string? IndustryKey { get; init; }
    public string? IndustryOther { get; init; }
    public Dictionary<string, string>? Answers { get; init; }
    public List<ExperienceGroupSelection>? Groups { get; init; }
    public List<string>? Frameworks { get; init; }
}

public sealed class ExperienceGroupSelection
{
    public string? Name { get; init; }
    public string? Classification { get; init; }
    public int? UserCount { get; init; }
    public string? ExperienceProfile { get; init; }
    public Dictionary<string, string>? Answers { get; init; }
}

public sealed class SaveEngagementDecisionRequest
{
    public string? Status { get; init; }
    public string? Answer { get; init; }
    public string? Notes { get; init; }
}

public sealed class ConfirmPlannerHandoffRequest
{
    public string? PlanId { get; init; }
    public string? EvidenceReference { get; init; }
    public bool ConfirmedTerminalAndComplete { get; init; }
}

public sealed record WbsTaskResponse(
    string WbsId,
    int Sequence,
    string Phase,
    string Workstream,
    string Name,
    string Deliverable,
    decimal MinHours,
    decimal MaxHours,
    string DurationMin,
    string DurationMax,
    bool SupportHandoff);

public sealed record PhaseEstimateResponse(
    string Phase,
    int TaskCount,
    decimal MinHours,
    decimal MaxHours,
    bool IsMonthly);

public sealed record EstimateResponse(
    int TaskCount,
    decimal OneTimeHoursMin,
    decimal OneTimeHoursMax,
    decimal MonthlyHoursMin,
    decimal MonthlyHoursMax,
    IReadOnlyList<PhaseEstimateResponse> ByPhase,
    IReadOnlyList<WbsTaskResponse> Tasks);

public sealed record EngagementListItem(
    Guid Id,
    string CompanyName,
    string Salesperson,
    DateOnly? MeetingDate,
    DateTime CreatedUtc,
    decimal OneTimeHoursMin,
    decimal OneTimeHoursMax,
    decimal MonthlyHoursMin,
    decimal MonthlyHoursMax,
    int TaskCount,
    string PlannerHandoffStatus,
    string DeploymentSyncStatus,
    string? OwnerEmail);

public sealed record EngagementDetailResponse(
    Guid Id,
    Guid CustomerId,
    string CompanyName,
    string? ContactName,
    string? ContactEmail,
    string Salesperson,
    DateOnly? MeetingDate,
    int? Users,
    int? Endpoints,
    string? Notes,
    DateTime CreatedUtc,
    int TaskCount,
    decimal OneTimeHoursMin,
    decimal OneTimeHoursMax,
    decimal MonthlyHoursMin,
    decimal MonthlyHoursMax,
    string PlannerHandoffStatus,
    string? PlannerPlanId,
    string? PlannerHandoffMessage,
    IReadOnlyDictionary<string, string[]> Answers,
    IReadOnlyList<PhaseEstimateResponse> ByPhase,
    IReadOnlyList<WbsTaskResponse> Tasks,
    string DeploymentSyncStatus,
    string? DeploymentSyncMessage,
    string? OwnerEmail,
    System.Text.Json.JsonElement? Experience);
