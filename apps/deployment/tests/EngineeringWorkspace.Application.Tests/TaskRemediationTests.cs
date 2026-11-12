using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Governance;
using EngineeringWorkspace.Application.Planning;
using EngineeringWorkspace.Domain.Auditing;
using FluentAssertions;

namespace EngineeringWorkspace.Application.Tests;

/// <summary>4.0 C2g guardrails for Remediate from a project task.</summary>
public sealed class TaskRemediationTests
{
    private static ProjectTask Task(string status = ProjectTaskStatuses.NotStarted, int[]? crs = null, string[]? controls = null) =>
        new("k1", "1", "Remediate CA", "Remediation", "Engineer", null, new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 9), 1, 8, 0, false, status, null, null, [],
            ChangeRequestIds: crs ?? [7], ControlIds: controls ?? ["CA-100"]);

    private static ChangeRequestRecord Request(string status) => new(7, "contoso", "MFA", null, "Medium", null, "Delete the policy", "eng", "approver", null, null, null, status, null,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, [new ProfileItem("Control", "CA-100"), new ProfileItem("Control", "CA-200")]);

    [Fact]
    public void Remediation_needs_a_linked_approved_change_request_and_an_open_task()
    {
        TaskRemediationService.Evaluate(Task(crs: []), [Request(ChangeStatusCodes.Approved)]).Allowed.Should().BeFalse();
        TaskRemediationService.Evaluate(Task(), [Request(ChangeStatusCodes.Draft)]).Reason.Should().Contain("only an approved change request");
        TaskRemediationService.Evaluate(Task(), [Request(ChangeStatusCodes.Submitted)]).Allowed.Should().BeFalse();
        TaskRemediationService.Evaluate(Task(ProjectTaskStatuses.Completed), [Request(ChangeStatusCodes.Approved)]).Allowed.Should().BeFalse();
        TaskRemediationService.Evaluate(Task(), [Request(ChangeStatusCodes.Approved)]).Allowed.Should().BeTrue();
        TaskRemediationService.Scope(Task(), Request(ChangeStatusCodes.Approved)).Items.Should().ContainSingle(item => item.ItemKey == "CA-100");
    }

    [Fact]
    public async Task A_successful_remediation_stages_the_task_and_records_before_and_after()
    {
        var planning = new Planning();
        var audit = new Audit();
        var service = new TaskRemediationService(planning, audit, new AuditContext("GDS\\tech"), TimeProvider.System);
        ChangeRequestRecord? applied = null;
        var outcome = await service.RemediateAsync(5, Task(), Request(ChangeStatusCodes.Approved),
            [new ApplyPreviewItem("CA-100 MFA", "GDS - CA100", "Microsoft Graph", "Not present in the tenant", "Created in report-only state")],
            request =>
            {
                applied = request;
                return System.Threading.Tasks.Task.FromResult(new ChangeExecutionResult(1, "Succeeded",
                [
                    new ExecutionStep("Change request", StepStatus.Succeeded, "Approved."),
                    new ExecutionStep("CA-100 MFA", StepStatus.Succeeded, "Created 'GDS - CA100' in report-only state (id abc).")
                ], "log", 9, []));
            });
        applied!.Items.Should().ContainSingle("only the task's controls are applied");
        outcome.NewStatus.Should().Be(ProjectTaskStatuses.Staged, "report-only / unassigned is not complete");
        outcome.Created.Should().Be(1);
        planning.Events.Should().ContainSingle(entry => entry.EventType == ProjectTaskEventTypes.RemediationSucceeded && entry.DetailJson!.Contains("Not present in the tenant"));
        audit.Events.Should().ContainSingle(type => type == AuditEventTypes.TenantChangeApplied);
    }

    [Fact]
    public async Task A_failed_remediation_leaves_the_task_open_and_is_logged()
    {
        var planning = new Planning();
        var audit = new Audit();
        var service = new TaskRemediationService(planning, audit, new AuditContext("GDS\\tech"), TimeProvider.System);
        var outcome = await service.RemediateAsync(5, Task(), Request(ChangeStatusCodes.Approved), [], _ => throw new InvalidOperationException("403 Forbidden"));
        outcome.NewStatus.Should().Be(ProjectTaskStatuses.NotStarted);
        outcome.Summary.Should().Contain("403");
        planning.Events.Should().ContainSingle(entry => entry.EventType == ProjectTaskEventTypes.RemediationFailed);
        audit.Events.Should().ContainSingle(type => type == AuditEventTypes.TenantChangeFailed);
        var draft = () => service.RemediateAsync(5, Task(), Request(ChangeStatusCodes.Draft), [], _ => throw new InvalidOperationException("must not run"));
        await draft.Should().ThrowAsync<InvalidOperationException>().WithMessage("*approved*");
    }

    [Fact]
    public async Task Manual_steps_are_attested_with_a_note_and_staged_tasks_complete_only_when_enforced()
    {
        var planning = new Planning();
        var service = new TaskRemediationService(planning, new Audit(), new AuditContext("GDS\\tech"), TimeProvider.System);
        var noNote = () => service.AttestAsync(5, Task(), " ", null, null);
        await noNote.Should().ThrowAsync<ArgumentException>();
        var tooBig = () => service.AttestAsync(5, Task(), "GPO linked to Workstations OU by J. Smith", "gpo.zip", new byte[TaskRemediationService.MaxAttachmentBytes + 1]);
        await tooBig.Should().ThrowAsync<ArgumentException>();
        await service.AttestAsync(5, Task(), "GPO linked to Workstations OU by J. Smith", "gpo.zip", [1, 2, 3]);
        planning.Events.Should().ContainSingle(entry => entry.EventType == ProjectTaskEventTypes.ManualAttested && entry.AttachmentName == "gpo.zip");

        var staged = Task(ProjectTaskStatuses.Staged, controls: ["CA-100", "CA-200"]);
        TaskRemediationService.CanComplete(staged, new Dictionary<string, string> { ["CA-100"] = "Pass", ["CA-200"] = "Fail" }).CanComplete.Should().BeFalse();
        TaskRemediationService.CanComplete(staged, new Dictionary<string, string> { ["CA-100"] = "Pass", ["CA-200"] = "Pass" }).CanComplete.Should().BeTrue();
        TaskRemediationService.CanComplete(Task(), new Dictionary<string, string>()).CanComplete.Should().BeTrue("only Staged tasks need enforcement");
    }

    private sealed class Audit : IAuditService
    {
        public List<string> Events { get; } = [];

        public System.Threading.Tasks.Task RecordAsync(string eventType, AuditOutcome outcome, string summary, IReadOnlyDictionary<string, string?>? properties = null,
            CancellationToken cancellationToken = default)
        {
            Events.Add(eventType);
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    private sealed class Planning : IPlanningRepository
    {
        public List<ProjectTaskEvent> Events { get; } = [];
        public bool IsAvailable => true;

        public System.Threading.Tasks.Task AddTaskEventAsync(ProjectTaskEvent entry, byte[]? attachment = null, CancellationToken cancellationToken = default)
        {
            Events.Add(entry);
            return System.Threading.Tasks.Task.CompletedTask;
        }

        public Task<IReadOnlyList<ProjectTaskEvent>> GetTaskEventsAsync(int planId, string taskKey, CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<ProjectTaskEvent>>(Events);

        public Task<IReadOnlyList<ThreatCoverageCell>> GetThreatCoverageAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<KnowledgeSetting>> FindSettingsAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkBreakdown> GetWorkBreakdownAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SowTemplate>> GetSowTemplatesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public System.Threading.Tasks.Task SaveSowTemplateAsync(SowTemplate template, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public System.Threading.Tasks.Task DeleteSowTemplateAsync(string code, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProjectPlan>> GetProjectPlansAsync(string customerKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProjectPlan> SaveProjectPlanAsync(string customerKey, ProjectPlan plan, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public System.Threading.Tasks.Task DeleteProjectPlanAsync(string customerKey, int planId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
