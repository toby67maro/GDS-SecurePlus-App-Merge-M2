using System.Text.Json;
using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Governance;
using EngineeringWorkspace.Domain.Auditing;

namespace EngineeringWorkspace.Application.Planning;

/// <summary>Whether a task can be remediated now, and with which change request.</summary>
public sealed record RemediationGate(bool Allowed, string Reason, ChangeRequestRecord? ChangeRequest);

/// <summary>The outcome of a remediation attempt and the task's new status.</summary>
public sealed record RemediationOutcome(bool Succeeded, string NewStatus, string Summary, int Created, int Skipped, int Failed);

/// <summary>
/// Remediate from a project task (4.0, C2g). Guardrails:
/// <list type="bullet">
/// <item>Only when a linked change request is Approved (Scheduled also counts: it was approved). From 3.2 beta.5 the engagement must also allow deployment.</item>
/// <item>The confirmation (with passcode) shows tenant, exact changes with their before state, method and rollback note; the page asks for it.</item>
/// <item>Pilot first: Conditional Access is created report-only and Intune policies unassigned, so a successful remediation leaves the task
/// <see cref="ProjectTaskStatuses.Staged"/>, not complete; it can be completed once the change is enforced.</item>
/// <item>Every attempt (success or failure, who, when, before and after) is written to the task history and the audit ledger.</item>
/// </list>
/// Manual (GPO, registry, PowerShell) steps are attested with a required note and an optional attachment, recorded as "Manual – attested".
/// </summary>
public sealed class TaskRemediationService(IPlanningRepository planning, IAuditService audit, AuditContext context, TimeProvider timeProvider)
{
    public const int MaxAttachmentBytes = 10 * 1024 * 1024;

    public static RemediationGate Evaluate(ProjectTask task, IReadOnlyList<ChangeRequestRecord> changeRequests)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Status == ProjectTaskStatuses.Completed) return new RemediationGate(false, "The task is already complete.", null);
        if (task.ChangeRequests.Count == 0) return new RemediationGate(false, "Link the task to an approved change request first.", null);
        var linked = changeRequests.Where(request => task.ChangeRequests.Contains(request.ChangeRequestId)).ToArray();
        if (linked.Length == 0) return new RemediationGate(false, "The linked change request no longer exists.", null);
        var approved = linked.FirstOrDefault(request => request.StatusCode is ChangeStatusCodes.Approved or ChangeStatusCodes.Scheduled);
        return approved is null
            ? new RemediationGate(false, $"Change request #{linked[0].ChangeRequestId} is {linked[0].StatusCode}; only an approved change request can be deployed.", null)
            : new RemediationGate(true, $"Change request #{approved.ChangeRequestId} is approved.", approved);
    }

    /// <summary>The change request limited to the task's controls (when the task names any that are in it).</summary>
    public static ChangeRequestRecord Scope(ProjectTask task, ChangeRequestRecord request)
    {
        var items = request.Items.Where(item => task.Controls.Contains(item.ItemKey, StringComparer.OrdinalIgnoreCase)).ToArray();
        return items.Length == 0 ? request : request with { Items = items };
    }

    public async Task<RemediationOutcome> RemediateAsync(int planId, ProjectTask task, ChangeRequestRecord approved, IReadOnlyList<ApplyPreviewItem> before,
        Func<ChangeRequestRecord, Task<ChangeExecutionResult>> apply, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(apply);
        var gate = Evaluate(task, [approved]);
        if (!gate.Allowed) throw new InvalidOperationException(gate.Reason);
        var scoped = Scope(task, approved);
        ChangeExecutionResult result;
        try
        {
            result = await apply(scoped);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            result = new ChangeExecutionResult(0, "Failed", [new ExecutionStep("Remediate", StepStatus.Failed, exception.Message)], exception.Message, null, []);
        }

        // Only the per-policy steps count (pre-checks also report Succeeded).
        var steps = result.Steps;
        var created = steps.Count(step => step.Status == StepStatus.Succeeded && step.Detail.StartsWith("Created '", StringComparison.Ordinal));
        var skipped = steps.Count(step => step.Status == StepStatus.Skipped);
        var failed = steps.Count(step => (step.Status == StepStatus.Warning && step.Detail.StartsWith("Not created", StringComparison.Ordinal)) || step.Status == StepStatus.Failed);
        var succeeded = created > 0 && failed == 0;
        var status = created > 0 ? ProjectTaskStatuses.Staged : task.Status;
        var summary = created > 0
            ? $"{created} policy(ies) created (report-only / unassigned){(failed > 0 ? $", {failed} failed" : string.Empty)}: the task is Staged until the change is enforced."
            : failed > 0 ? $"Remediation failed: {steps.First(step => step.Status == StepStatus.Failed || step.Detail.StartsWith("Not created", StringComparison.Ordinal)).Detail}"
            : "Nothing was created (already present or manual).";
        var detail = JsonSerializer.Serialize(new
        {
            ChangeRequest = approved.ChangeRequestId, Controls = scoped.Items.Select(item => item.ItemKey), ExecutionId = result.ExecutionId, result.Status,
            Before = before.Select(item => new { item.Item, item.PolicyName, item.Before }),
            After = steps.Select(step => new { step.Name, step.Status, step.Detail })
        });
        await planning.AddTaskEventAsync(new ProjectTaskEvent(0, planId, task.Key,
            created > 0 ? ProjectTaskEventTypes.RemediationSucceeded : ProjectTaskEventTypes.RemediationFailed, summary, detail, null, context.Actor, timeProvider.GetUtcNow()),
            null, cancellationToken);
        await audit.RecordAsync(created > 0 ? AuditEventTypes.TenantChangeApplied : AuditEventTypes.TenantChangeFailed,
            created > 0 ? AuditOutcome.Success : AuditOutcome.Failure, $"Remediate from task '{task.Name}': {summary}",
            new Dictionary<string, string?> { ["ChangeRequest"] = approved.ChangeRequestId.ToString(System.Globalization.CultureInfo.InvariantCulture), ["Task"] = task.Key, ["Plan"] = planId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            cancellationToken);
        return new RemediationOutcome(succeeded, status, summary, created, skipped, failed);
    }

    /// <summary>Records a manual step the engineer attests was done. Never counted as a verified change.</summary>
    public async Task AttestAsync(int planId, ProjectTask task, string note, string? attachmentName, byte[]? attachment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (string.IsNullOrWhiteSpace(note) || note.Trim().Length < 10)
            throw new ArgumentException("Describe what was done, where and by whom (at least 10 characters).", nameof(note));
        if (attachment is { Length: > MaxAttachmentBytes }) throw new ArgumentException("The attachment is larger than 10 MB.", nameof(attachment));
        await planning.AddTaskEventAsync(new ProjectTaskEvent(0, planId, task.Key, ProjectTaskEventTypes.ManualAttested, note.Trim(), null, attachmentName, context.Actor,
            timeProvider.GetUtcNow()), attachment, cancellationToken);
        await audit.RecordAsync("TaskManualAttested", AuditOutcome.Information, $"Manual step attested on task '{task.Name}' (not verified).",
            new Dictionary<string, string?> { ["Task"] = task.Key, ["Plan"] = planId.ToString(System.Globalization.CultureInfo.InvariantCulture), ["Attachment"] = attachmentName },
            cancellationToken);
    }

    /// <summary>
    /// A Staged task can be completed only when every linked control passes in the latest assessment (the change is enforced) and, when it
    /// includes Conditional Access, the report-only monitoring period has passed since it was staged
    /// (<see cref="ConditionalAccessRollout.MinimumReportOnlyDays"/>).
    /// </summary>
    public static (bool CanComplete, string Reason) CanComplete(ProjectTask task, IReadOnlyDictionary<string, string> latestResults, DateTimeOffset? stagedAtUtc = null,
        DateTimeOffset? nowUtc = null, bool includesConditionalAccess = false)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Status != ProjectTaskStatuses.Staged) return (true, string.Empty);
        if (includesConditionalAccess && stagedAtUtc is { } staged &&
            ConditionalAccessRollout.MonitoringEndsUtc(staged) is var ends && (nowUtc ?? DateTimeOffset.UtcNow) < ends)
            return (false, $"Conditional Access is monitored in report-only mode for at least {ConditionalAccessRollout.MinimumReportOnlyDays} days " +
                           $"({ConditionalAccessRollout.RecommendedReportOnlyDays} recommended): it was staged on {staged.ToLocalTime():d}, so it can be switched on and the task " +
                           $"completed from {ends.ToLocalTime():d}. Review the sign-in logs (report-only results) before then.");
        if (task.Controls.Count == 0) return (false, "This task is Staged; link its controls so enforcement can be checked, or switch the policy on and re-assess.");
        var pending = task.Controls.Where(control => !latestResults.TryGetValue(control, out var result) || result != "Pass").ToArray();
        return pending.Length == 0
            ? (true, string.Empty)
            : (false, $"Still staged: {string.Join(", ", pending)} not enforced yet (switch Conditional Access on / assign the policy, then re-assess).");
    }
}

/// <summary>
/// Conditional Access rollout rule (user decision, 9 Nov 2026): every policy is deployed in audit (report-only) mode and monitored for
/// 2–3 weeks before it is switched on.
/// </summary>
public static class ConditionalAccessRollout
{
    public const string ReportOnlyState = "enabledForReportingButNotEnforced";
    public const int MinimumReportOnlyDays = 14;
    public const int RecommendedReportOnlyDays = 21;

    public static DateTimeOffset MonitoringEndsUtc(DateTimeOffset stagedAtUtc) => stagedAtUtc.AddDays(MinimumReportOnlyDays);
}
