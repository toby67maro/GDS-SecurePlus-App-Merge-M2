namespace EngineeringWorkspace.Application.Planning;

/// <summary>ATT&amp;CK coverage of one tactic by one column (a service tier, or all Microsoft capabilities).</summary>
public sealed record ThreatCoverageCell(string Tactic, int TacticOrder, string Column, int Techniques, int Covered, IReadOnlyList<string> Gaps)
{
    public double Ratio => Techniques == 0 ? 0 : (double)Covered / Techniques;
}

/// <summary>A configuration setting from the knowledge base (cfg.Setting) with every way to set it, and baseline procedures.</summary>
public sealed record KnowledgeSetting(
    string SettingKey,
    string Name,
    string? GpoPath,
    string? RegistryHive,
    string? RegistryKey,
    string? RegistryValueName,
    string? RegistryValueType,
    string? OmaUri,
    string? IntuneDefinitionId,
    string? PowerShellCmdlet,
    string? PortalPath,
    string? DefaultValue,
    IReadOnlyList<string> RecommendedValues,
    IReadOnlyList<KnowledgeProcedure> Procedures);

/// <summary>An audit or remediation step of a baseline recommendation (cfg.RecommendationProcedure).</summary>
public sealed record KnowledgeProcedure(string Recommendation, string Type, string Method, string Text, string? Script);

public sealed record WbsPhaseInfo(string Code, string Name, int Order);

/// <summary>A task of the GDS work breakdown structure (dlv.WbsTask) with its predecessors.</summary>
public sealed record WbsTaskInfo(
    string Code,
    string Name,
    string? SubName,
    string? PhaseCode,
    string? Workstream,
    string? Bucket,
    string? ControlCode,
    double HoursMin,
    double HoursLikely,
    double HoursMax,
    bool IsParallelizable,
    string? Priority,
    string? Deliverable,
    string? DependencyText,
    string? SortKey,
    IReadOnlyList<string> Predecessors)
{
    public string DisplayName => string.IsNullOrWhiteSpace(SubName) ? Name : $"{Name}: {SubName}";
}

public sealed record WorkBreakdown(IReadOnlyList<WbsPhaseInfo> Phases, IReadOnlyList<WbsTaskInfo> Tasks)
{
    public static WorkBreakdown Empty { get; } = new([], []);
}

/// <summary>A statement of work template (dlv.SowTemplate) and its sections (dlv.SowSection). Content holds {{placeholders}}.</summary>
public sealed record SowTemplate(string Code, string Name, string Version, string? EngagementType, string? ServiceTierCode, IReadOnlyList<SowSection> Sections, bool IsBuiltIn = false);

public sealed record SowSection(string Code, string Title, int Order, string Content, bool IsRequired);

/// <summary>A customer project plan (dlv.ProjectPlan / dlv.ProjectTask / dlv.ProjectTaskDependency).</summary>
public sealed record ProjectPlan(int? Id, string Name, DateOnly Start, DateOnly Finish, string Status, IReadOnlyList<ProjectTask> Tasks);

public sealed record ProjectTask(
    string Key,
    string Outline,
    string Name,
    string? Bucket,
    string? Role,
    string? AssignedTo,
    DateOnly Start,
    DateOnly Finish,
    double DurationDays,
    double EffortHours,
    double PercentComplete,
    bool IsMilestone,
    string Status,
    string? Priority,
    string? WbsTaskCode,
    IReadOnlyList<string> Predecessors,
    string? Notes = null,
    DateOnly? ActualFinish = null,
    IReadOnlyList<int>? ChangeRequestIds = null,
    IReadOnlyList<string>? ControlIds = null,
    double? ScheduleRank = null,
    DateOnly? PinnedStart = null)
{
    /// <summary>Linked change requests (never null).</summary>
    public IReadOnlyList<int> ChangeRequests => ChangeRequestIds ?? [];

    /// <summary>Linked controls and policies (never null).</summary>
    public IReadOnlyList<string> Controls => ControlIds ?? [];
}

/// <summary>Project task states. Staged: a remediation was created but is not enforced yet (report-only CA, unassigned Intune policy).</summary>
public static class ProjectTaskStatuses
{
    public const string NotStarted = nameof(NotStarted);
    public const string InProgress = nameof(InProgress);
    public const string Staged = nameof(Staged);
    public const string Completed = nameof(Completed);
}

/// <summary>One entry in a task's history: edits, completion, remediation attempts, attested manual steps (secureplus.ProjectTaskEvent, append-only).</summary>
public sealed record ProjectTaskEvent(long Id, int PlanId, string TaskKey, string EventType, string Summary, string? DetailJson, string? AttachmentName, string CreatedBy,
    DateTimeOffset CreatedAtUtc);

public static class ProjectTaskEventTypes
{
    public const string Edited = nameof(Edited);
    public const string Completed = nameof(Completed);
    public const string Reopened = nameof(Reopened);
    public const string Staged = nameof(Staged);
    public const string RemediationSucceeded = nameof(RemediationSucceeded);
    public const string RemediationFailed = nameof(RemediationFailed);

    /// <summary>A manual (GPO, registry, PowerShell) step the engineer attests was done; never counted as verified.</summary>
    public const string ManualAttested = "Manual – attested";
}

/// <summary>
/// Knowledge base and delivery data for planning pages: threat coverage, setting remediation, the WBS, SOW templates and
/// project plans. Reads and writes the EaaS dlv.* tables.
/// </summary>
public interface IPlanningRepository
{
    bool IsAvailable { get; }
    Task<IReadOnlyList<ThreatCoverageCell>> GetThreatCoverageAsync(CancellationToken cancellationToken = default);

    /// <summary>Settings whose key or alias (Intune setting definition ID, OMA-URI, registry path...) is one of <paramref name="keys"/>.</summary>
    Task<IReadOnlyList<KnowledgeSetting>> FindSettingsAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default);

    Task<WorkBreakdown> GetWorkBreakdownAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SowTemplate>> GetSowTemplatesAsync(CancellationToken cancellationToken = default);
    Task SaveSowTemplateAsync(SowTemplate template, CancellationToken cancellationToken = default);
    Task DeleteSowTemplateAsync(string code, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectPlan>> GetProjectPlansAsync(string customerKey, CancellationToken cancellationToken = default);
    Task<ProjectPlan> SaveProjectPlanAsync(string customerKey, ProjectPlan plan, CancellationToken cancellationToken = default);
    Task DeleteProjectPlanAsync(string customerKey, int planId, CancellationToken cancellationToken = default);

    /// <summary>Appends a task history entry (with an optional attachment of at most 10 MB).</summary>
    Task AddTaskEventAsync(ProjectTaskEvent entry, byte[]? attachment = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectTaskEvent>> GetTaskEventsAsync(int planId, string taskKey, CancellationToken cancellationToken = default);
}
