namespace ArchiTech.Api.Domain;

public sealed class Customer
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }
    public DateTime CreatedUtc { get; set; }
    public ICollection<Engagement> Engagements { get; set; } = [];
    public DeploymentProfile? DeploymentProfile { get; set; }
    public ICollection<AssessmentRun> AssessmentRuns { get; set; } = [];
    public ICollection<DeploymentChangePlan> DeploymentChangePlans { get; set; } = [];
}

public sealed class Engagement
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public required string Salesperson { get; set; }
    public string? OwnerId { get; set; }
    public string? OwnerEmail { get; set; }
    public string DeploymentSyncStatus { get; set; } = "Pending";
    public string? DeploymentSyncMessage { get; set; }
    public string? ExperienceJson { get; set; }
    public DateTime? DeploymentSyncedUtc { get; set; }
    public DateOnly? MeetingDate { get; set; }
    public int? Users { get; set; }
    public int? Endpoints { get; set; }
    public string? Notes { get; set; }
    public decimal OneTimeHoursMin { get; set; }
    public decimal OneTimeHoursMax { get; set; }
    public decimal MonthlyHoursMin { get; set; }
    public decimal MonthlyHoursMax { get; set; }
    public required string PlannerHandoffStatus { get; set; }
    public string? PlannerPlanId { get; set; }
    public string? PlannerHandoffMessage { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? PlannerHandedOffUtc { get; set; }
    public ICollection<EngagementAnswer> Answers { get; set; } = [];
    public ICollection<EngagementTask> Tasks { get; set; } = [];
}

public sealed class EngagementAnswer
{
    public long Id { get; set; }
    public Guid EngagementId { get; set; }
    public Engagement Engagement { get; set; } = null!;
    public required string FieldKey { get; set; }
    public required string Value { get; set; }
}

public sealed class WbsTask
{
    public required string Id { get; set; }
    public int Sequence { get; set; }
    public required string Phase { get; set; }
    public required string Workstream { get; set; }
    public required string Name { get; set; }
    public required string Deliverable { get; set; }
    public required string RuleKey { get; set; }
    public decimal MinHours { get; set; }
    public decimal MaxHours { get; set; }
    public required string DurationMin { get; set; }
    public required string DurationMax { get; set; }
    public bool SupportHandoff { get; set; }
    public ICollection<EngagementTask> EngagementTasks { get; set; } = [];
}

public sealed class EngagementTask
{
    public Guid Id { get; set; }
    public Guid EngagementId { get; set; }
    public Engagement Engagement { get; set; } = null!;
    public required string WbsTaskId { get; set; }
    public WbsTask WbsTask { get; set; } = null!;
    public decimal MinHours { get; set; }
    public decimal MaxHours { get; set; }
}

public sealed class DeploymentProfile
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string? TenantId { get; set; }
    public string? PrimaryDomain { get; set; }
    public string? EntraApplicationClientId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public ICollection<DeploymentFinding> Findings { get; set; } = [];
    public ICollection<DeploymentProfileSetupStep> SetupSteps { get; set; } = [];
}

public sealed class DeploymentProfileSetupStep
{
    public long Id { get; set; }
    public Guid DeploymentProfileId { get; set; }
    public DeploymentProfile DeploymentProfile { get; set; } = null!;
    public required string StepKey { get; set; }
    public bool IsComplete { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class DeploymentFinding
{
    public long Id { get; set; }
    public Guid DeploymentProfileId { get; set; }
    public DeploymentProfile DeploymentProfile { get; set; } = null!;
    public required string ControlId { get; set; }
    public required string Status { get; set; }
    public string? Notes { get; set; }
    public string? Evidence { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class AssessmentRun
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public Guid? EngagementId { get; set; }
    public Engagement? Engagement { get; set; }
    public required string Name { get; set; }
    public required string Assessor { get; set; }
    public required string Source { get; set; }
    public required string CatalogVersion { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public bool IsComplete { get; set; }
    public double OverallScore { get; set; }
    public double OverallPercentage { get; set; }
    public int TotalControls { get; set; }
    public int ImplementedControls { get; set; }
    public int PartialControls { get; set; }
    public int NotImplementedControls { get; set; }
    public int NotAssessedControls { get; set; }
    public int ExceptionControls { get; set; }
    public int NotApplicableControls { get; set; }
    public required string ReadinessRating { get; set; }
    public DateTime CalculatedUtc { get; set; }
    public ICollection<AssessmentResult> Results { get; set; } = [];
    public ICollection<AssessmentScoreBreakdown> Breakdowns { get; set; } = [];
    public ICollection<AssessmentRecommendation> Recommendations { get; set; } = [];
}

public sealed class AssessmentResult
{
    public long Id { get; set; }
    public Guid AssessmentRunId { get; set; }
    public AssessmentRun AssessmentRun { get; set; } = null!;
    public required string ControlId { get; set; }
    public required string Status { get; set; }
    public string? Notes { get; set; }
    public string? Evidence { get; set; }
    public required string Assessor { get; set; }
    public DateTime ReviewedUtc { get; set; }
    public string? ExceptionReason { get; set; }
}

public sealed class AssessmentScoreBreakdown
{
    public long Id { get; set; }
    public Guid AssessmentRunId { get; set; }
    public AssessmentRun AssessmentRun { get; set; } = null!;
    public required string BreakdownType { get; set; }
    public required string Label { get; set; }
    public double Percentage { get; set; }
    public int TotalPoints { get; set; }
    public int EarnedPoints { get; set; }
    public int GapCount { get; set; }
}

public sealed class AssessmentRecommendation
{
    public long Id { get; set; }
    public Guid AssessmentRunId { get; set; }
    public AssessmentRun AssessmentRun { get; set; } = null!;
    public int Sequence { get; set; }
    public required string Severity { get; set; }
    public required string Text { get; set; }
}

public sealed class DeploymentChangePlan
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public Guid AssessmentRunId { get; set; }
    public AssessmentRun AssessmentRun { get; set; } = null!;
    public required string Title { get; set; }
    public string? Notes { get; set; }
    public required string Status { get; set; }
    public required string RequestedById { get; set; }
    public required string RequestedBy { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? SubmittedUtc { get; set; }
    public string? ReviewedById { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedUtc { get; set; }
    public string? ReviewDecision { get; set; }
    public string? ReviewNotes { get; set; }
    public ICollection<DeploymentChangePlanItem> Items { get; set; } = [];
    public ICollection<DeploymentChangePlanEvent> Events { get; set; } = [];
}

public sealed class DeploymentChangePlanItem
{
    public long Id { get; set; }
    public Guid DeploymentChangePlanId { get; set; }
    public DeploymentChangePlan DeploymentChangePlan { get; set; } = null!;
    public required string ControlId { get; set; }
    public required string ControlName { get; set; }
    public required string AssessmentStatus { get; set; }
    public int Sequence { get; set; }
}

public sealed class DeploymentChangePlanEvent
{
    public long Id { get; set; }
    public Guid DeploymentChangePlanId { get; set; }
    public DeploymentChangePlan DeploymentChangePlan { get; set; } = null!;
    public required string EventType { get; set; }
    public required string ActorId { get; set; }
    public required string Actor { get; set; }
    public string? Notes { get; set; }
    public DateTime OccurredUtc { get; set; }
}
