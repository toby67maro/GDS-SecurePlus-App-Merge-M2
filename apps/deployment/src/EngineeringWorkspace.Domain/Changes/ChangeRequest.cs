using EngineeringWorkspace.Domain.Common;

namespace EngineeringWorkspace.Domain.Changes;

public enum ChangeRequestStatus
{
    Draft,
    Submitted,
    Approved,
    Rejected,
    Scheduled,
    InProgress,
    Completed,
    RolledBack,
    Cancelled
}

public enum ChangeRisk
{
    Low,
    Medium,
    High,
    Critical
}

public sealed record ChangeTask(string TaskId, string Title, string? Owner, bool IsComplete);

public sealed record ChangeApproval(string Approver, DateTimeOffset DecidedAtUtc, bool Approved, string? Comment);

public sealed record ChangeExecutionRecord(string ExecutionId, DateTimeOffset StartedAtUtc, DateTimeOffset? CompletedAtUtc, string Outcome, string? Notes);

public sealed record RollbackPlan(string Description, IReadOnlyList<string> Steps);

/// <summary>
/// A requested change to a customer environment. Modelled minimally now; the workflow and execution engine
/// arrive in M7. Maps to EaaS v3 <c>deploy.ChangeRequest</c>.
/// </summary>
public sealed record ChangeRequest(
    string ChangeRequestId,
    CustomerId CustomerId,
    string Title,
    string Description,
    string RequestedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PlannedAtUtc,
    ChangeRequestStatus Status,
    ChangeRisk Risk,
    IReadOnlyList<string> AffectedControls,
    IReadOnlyList<ChangeTask> Tasks,
    IReadOnlyList<string> ScriptIds,
    IReadOnlyList<ChangeApproval> Approvals,
    IReadOnlyList<ChangeExecutionRecord> ExecutionRecords,
    RollbackPlan? Rollback);
