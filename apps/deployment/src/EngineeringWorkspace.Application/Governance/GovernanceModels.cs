namespace EngineeringWorkspace.Application.Governance;

/// <summary>A member of a specification, baseline or change request (see <see cref="Catalog.ProfileItemTypes"/>).</summary>
public sealed record ProfileItem(string ItemType, string ItemKey);

// ----- Customer exceptions -----

public static class ExceptionStatusCodes
{
    public const string Requested = "Requested";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Revoked = "Revoked";

    public static IReadOnlyList<string> All { get; } = [Requested, Approved, Rejected, Revoked];
}

/// <summary>An approved deviation: a control the customer is not required to meet (counts as satisfied while active).</summary>
public sealed record CustomerExceptionRecord(
    int ExceptionId,
    string CustomerKey,
    string ControlId,
    string Reason,
    string? ApprovedBy,
    DateTimeOffset? ExpiresAtUtc,
    string StatusCode,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc)
{
    public bool IsActiveAt(DateTimeOffset now) => StatusCode == ExceptionStatusCodes.Approved && (ExpiresAtUtc is null || ExpiresAtUtc > now);
    public bool IsExpiredAt(DateTimeOffset now) => ExpiresAtUtc is { } expires && expires <= now;
}

public interface ICustomerExceptionRepository
{
    bool IsAvailable { get; }
    Task<IReadOnlyList<CustomerExceptionRecord>> ListExceptionsAsync(string? customerKey, CancellationToken cancellationToken = default);
    Task<CustomerExceptionRecord> SaveExceptionAsync(CustomerExceptionRecord record, CancellationToken cancellationToken = default);
    Task DeleteExceptionAsync(int exceptionId, CancellationToken cancellationToken = default);
}

// ----- Specifications -----

public static class SpecificationStatusCodes
{
    public const string Draft = "Draft";
    public const string Approved = "Approved";
    public const string Retired = "Retired";

    public static IReadOnlyList<string> All { get; } = [Draft, Approved, Retired];
}

/// <summary>
/// A specification: a standard one (not assigned, shipped or built by GDS) or one tailored to a customer. It names the
/// target experiences, frameworks and compliance standards, the controls required, the requirements that satisfy them,
/// and the policy sets and policies used.
/// </summary>
public sealed record SpecificationRecord(
    string SpecificationKey,
    string Name,
    string? Description,
    bool IsStandard,
    bool IsSystem,
    bool IsModified,
    string? BasedOnKey,
    string StatusCode,
    IReadOnlyList<ProfileItem> Items,
    IReadOnlyList<string> AssignedCustomers,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc,
    string? ModifiedBy,
    DateTimeOffset? ModifiedAtUtc)
{
    public IEnumerable<string> ItemsOf(string type) => Items.Where(item => item.ItemType == type).Select(item => item.ItemKey);
    public bool IsAssigned => AssignedCustomers.Count > 0;
}

public interface ISpecificationRepository
{
    bool IsAvailable { get; }
    Task<IReadOnlyList<SpecificationRecord>> ListSpecificationsAsync(CancellationToken cancellationToken = default);

    /// <summary>Inserts or replaces a specification and its items. A system specification saved by a user is marked modified.</summary>
    Task<SpecificationRecord> SaveSpecificationAsync(SpecificationRecord record, string actor, CancellationToken cancellationToken = default);

    Task DeleteSpecificationAsync(string specificationKey, CancellationToken cancellationToken = default);

    /// <summary>Assigns a specification to a customer (one per customer); null unassigns.</summary>
    Task AssignSpecificationAsync(string customerKey, string? specificationKey, string actor, CancellationToken cancellationToken = default);
}

// ----- Baselines -----

/// <summary>A baseline configuration: a named collection of policy sets, controls, frameworks, standards and experiences.</summary>
public sealed record BaselineRecord(
    string BaselineKey,
    string Name,
    string? Description,
    bool IsActive,
    bool IsSystem,
    bool IsModified,
    IReadOnlyList<ProfileItem> Items,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc,
    string? ModifiedBy,
    DateTimeOffset? ModifiedAtUtc)
{
    public IEnumerable<string> ItemsOf(string type) => Items.Where(item => item.ItemType == type).Select(item => item.ItemKey);
}

public interface IBaselineRepository
{
    bool IsAvailable { get; }
    Task<IReadOnlyList<BaselineRecord>> ListBaselinesAsync(CancellationToken cancellationToken = default);
    Task<BaselineRecord> SaveBaselineAsync(BaselineRecord record, string actor, CancellationToken cancellationToken = default);
    Task SetBaselineActiveAsync(string baselineKey, bool isActive, string actor, CancellationToken cancellationToken = default);
    Task DeleteBaselineAsync(string baselineKey, CancellationToken cancellationToken = default);
}

// ----- Change requests -----

public static class ChangeStatusCodes
{
    public const string Draft = "Draft";
    public const string Submitted = "Submitted";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Scheduled = "Scheduled";
    public const string Completed = "Completed";

    public static IReadOnlyList<string> All { get; } = [Draft, Submitted, Approved, Rejected, Scheduled, Completed];

    /// <summary>Draft → Submitted → Approved/Rejected → Scheduled → Completed (a rejected request can be reopened as a draft).</summary>
    public static IReadOnlyList<string> NextStates(string status) => status switch
    {
        Draft => [Submitted],
        Submitted => [Approved, Rejected, Draft],
        Approved => [Scheduled, Completed],
        Rejected => [Draft],
        Scheduled => [Completed, Approved],
        _ => []
    };

    public static bool CanMove(string from, string to) => NextStates(from).Contains(to);
}

public static class ChangeRisk
{
    public static IReadOnlyList<string> All { get; } = ["Low", "Medium", "High", "Critical"];
}

public sealed record ChangeRequestRecord(
    int ChangeRequestId,
    string CustomerKey,
    string Title,
    string? Description,
    string Risk,
    string? Impact,
    string? RollbackPlan,
    string RequestedBy,
    string? ApprovedBy,
    string? Owner,
    DateTimeOffset? WindowStartUtc,
    DateTimeOffset? WindowEndUtc,
    string StatusCode,
    string? DecisionNote,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ModifiedAtUtc,
    DateTimeOffset? DecidedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    IReadOnlyList<ProfileItem> Items);

public static class ExecutionModes
{
    public const string Simulate = "Simulate";
    public const string Execute = "Execute";

    /// <summary>Policies created in the tenant by Secure Plus (passcode and confirmation required).</summary>
    public const string Apply = "Apply";
}

public static class ExecutionStatusCodes
{
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}

public sealed record ChangeExecutionRecord(
    int ChangeExecutionId,
    int ChangeRequestId,
    string ModeCode,
    string StatusCode,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string ExecutedBy,
    string? Log,
    int? ReassessmentRunId);

public interface IChangeRequestRepository
{
    bool IsAvailable { get; }
    Task<IReadOnlyList<ChangeRequestRecord>> ListChangeRequestsAsync(string? customerKey, CancellationToken cancellationToken = default);
    Task<ChangeRequestRecord> SaveChangeRequestAsync(ChangeRequestRecord record, CancellationToken cancellationToken = default);
    Task DeleteChangeRequestAsync(int changeRequestId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChangeExecutionRecord>> ListExecutionsAsync(int? changeRequestId, CancellationToken cancellationToken = default);
    Task<int> StartExecutionAsync(int changeRequestId, string mode, string actor, CancellationToken cancellationToken = default);
    Task CompleteExecutionAsync(int executionId, string status, string log, int? reassessmentRunId, CancellationToken cancellationToken = default);
}

// ----- Audit of edits -----

public sealed record EntityAuditRecord(long EntityAuditId, string EntityType, string EntityKey, string Property, string? OldValue, string? NewValue, string ChangedBy,
    DateTimeOffset ChangedAtUtc);

/// <summary>Who changed what and when (customer profiles, specifications, baselines, change requests).</summary>
public interface IEntityAuditRepository
{
    bool IsAvailable { get; }
    Task RecordChangesAsync(IReadOnlyList<EntityAuditRecord> changes, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EntityAuditRecord>> ListChangesAsync(string entityType, string? entityKey, int take = 200, CancellationToken cancellationToken = default);
}
