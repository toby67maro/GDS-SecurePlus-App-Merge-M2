using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Customers;
using EngineeringWorkspace.Domain.Workspaces;

namespace EngineeringWorkspace.Application.Assessments;

/// <summary>Lifecycle of a recorded assessment.</summary>
public static class AssessmentStatusCodes
{
    /// <summary>Requested (for example during first-run setup) but not yet run by the assessment engine.</summary>
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Completed = "Completed";

    /// <summary>Stopped part-way (failure or cancellation) after some results were saved; the saved results are kept.</summary>
    public const string Partial = "Partial";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";

    public static bool IsFinished(string status) => status is Completed or Partial or Failed or Cancelled;

    /// <summary>Runs whose results count in reports (complete or partial).</summary>
    public static bool HasResults(string status) => status is Completed or Partial;
}

/// <summary>What started an assessment run.</summary>
public static class AssessmentRunSources
{
    public const string Manual = "Manual";
    public const string Scheduled = "Scheduled";
    public const string Live = "Live";
    public const string Setup = "Setup";
}

/// <summary>One recorded assessment of a customer, as stored in the application database.</summary>
public sealed record AssessmentSummary(
    int AssessmentRunId,
    string CustomerKey,
    string? WorkspaceId,
    string Name,
    string StatusCode,
    decimal? Score,
    int? ControlsTotal,
    int? ControlsPassed,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? RequestedBy,
    bool IsSample)
{
    public string Source { get; init; } = AssessmentRunSources.Manual;
    public int? ScheduleId { get; init; }
    public int? ResultsSaved { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary><see cref="ScanTypes"/> value (runs before 4.0 beta.2 were in-depth).</summary>
    public string ScanType { get; init; } = ScanTypes.InDepth;

    public bool IsQuickScan => ScanType == ScanTypes.Quick;
}

/// <summary>One stored finding of an assessment.</summary>
public sealed record StoredAssessmentFinding(string Area, string Control, string Result, string Severity, string? Recommendation, string? Evidence = null, string? ControlId = null);

/// <summary>A request to record an assessment (and its findings, when it already ran).</summary>
public sealed record RecordAssessmentRequest(
    CustomerId CustomerId,
    WorkspaceId? WorkspaceId,
    string Name,
    string StatusCode,
    string ToolCode,
    decimal? Score,
    IReadOnlyList<StoredAssessmentFinding> Findings,
    bool IsSample,
    string? Notes = null);

/// <summary>What the database knows about a customer's engagement.</summary>
public sealed record CustomerEngagementState(bool CustomerRecorded, int AssessmentCount, int WorkspaceCount, AssessmentSummary? LatestAssessment)
{
    public bool HasAssessment => AssessmentCount > 0;
    public bool HasWorkspace => WorkspaceCount > 0;
}

/// <summary>
/// Customer, workspace and assessment history in the application database (SQL Server, Azure SQL or SQLite).
/// Customers and workspaces are also kept as files; the database is the shared record of engagements and
/// assessments. Every method fails with <see cref="EngagementStoreUnavailableException"/> when the database is
/// not configured or not ready.
/// </summary>
public interface IEngagementRepository
{
    /// <summary>True when a database is configured and its schema is current.</summary>
    bool IsAvailable { get; }

    Task UpsertCustomerAsync(CustomerProfile customer, bool isSample, CancellationToken cancellationToken = default);
    Task UpsertWorkspaceAsync(Workspace workspace, CancellationToken cancellationToken = default);
    Task<CustomerEngagementState> GetCustomerStateAsync(CustomerId customerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AssessmentSummary>> ListAssessmentsAsync(CustomerId? customerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredAssessmentFinding>> ListFindingsAsync(int assessmentRunId, CancellationToken cancellationToken = default);
    Task<AssessmentSummary> RecordAssessmentAsync(RecordAssessmentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Marks a pending run as running, or creates a new running run, and returns its ID.</summary>
    Task<int> StartAssessmentAsync(CustomerId customerId, WorkspaceId? workspaceId, string name, int? pendingRunId, CancellationToken cancellationToken = default);

    /// <summary>Records whether a run is a quick or an in-depth scan (4.0 beta.2).</summary>
    Task SetScanTypeAsync(int runId, string scanType, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>Starts a run recording what started it (manual, scheduled, live) and, for scheduled runs, the schedule.</summary>
    Task<int> StartAssessmentAsync(CustomerId customerId, WorkspaceId? workspaceId, string name, int? pendingRunId, string source, int? scheduleId,
        CancellationToken cancellationToken = default);

    /// <summary>Appends a chunk of results to a running assessment in its own transaction (already-saved chunks survive a later failure).</summary>
    Task AppendResultsAsync(int runId, IReadOnlyList<AssessmentResultItem> results, CancellationToken cancellationToken = default);

    /// <summary>Sets the final status, score and counts of a run whose results were appended with <see cref="AppendResultsAsync"/>.</summary>
    Task FinishAssessmentAsync(int runId, AssessmentRunTotals totals, CancellationToken cancellationToken = default);

    /// <summary>Stores the per-control results (replacing earlier ones of the run) and the final status and score.</summary>
    Task CompleteAssessmentAsync(int runId, string statusCode, decimal? score, IReadOnlyList<AssessmentResultItem> results, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssessmentResultItem>> ListResultsAsync(int runId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssessmentSchedule>> ListSchedulesAsync(CancellationToken cancellationToken = default);
    Task<AssessmentSchedule> SaveScheduleAsync(AssessmentSchedule schedule, CancellationToken cancellationToken = default);

    /// <summary>Deletes a schedule and its run history.</summary>
    Task DeleteScheduleAsync(int scheduleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically marks a schedule Running when it is not running already (a run older than <paramref name="staleAfter"/>
    /// counts as interrupted). With <paramref name="expectedNextRunUtc"/> (scheduled runs) the claim also requires the
    /// stored next run to match and moves it to <paramref name="nextRunUtc"/>, so two hosts never run the same occurrence.
    /// </summary>
    Task<bool> TryClaimScheduleAsync(int scheduleId, DateTimeOffset? expectedNextRunUtc, DateTimeOffset? nextRunUtc, TimeSpan staleAfter,
        CancellationToken cancellationToken = default);

    /// <summary>Records the outcome of a run on the schedule (status, message, last run).</summary>
    Task UpdateScheduleStatusAsync(int scheduleId, string status, string? message, DateTimeOffset? lastRunUtc, CancellationToken cancellationToken = default);

    Task SetScheduleEnabledAsync(int scheduleId, bool enabled, DateTimeOffset? nextRunUtc, CancellationToken cancellationToken = default);

    Task<int> StartScheduleRunAsync(int scheduleId, string host, CancellationToken cancellationToken = default);
    Task CompleteScheduleRunAsync(int scheduleRunId, string status, string? message, IReadOnlyList<int> assessmentRunIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScheduleRunInfo>> ListScheduleRunsAsync(int scheduleId, int take, CancellationToken cancellationToken = default);
}

/// <summary>Final figures of an assessment run.</summary>
public sealed record AssessmentRunTotals(string StatusCode, decimal? Score, int ControlsTotal, int ControlsPassed, int ResultsSaved, string? ErrorMessage);

/// <summary>The application database is not configured or not ready.</summary>
public sealed class EngagementStoreUnavailableException(string message) : Exception(message);
