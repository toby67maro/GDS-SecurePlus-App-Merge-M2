using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Domain.Common;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Application.Governance;

/// <summary>One step of a change execution as shown in the progress list.</summary>
public sealed record ExecutionStep(string Name, string Status, string Detail);

/// <summary>One line of an Apply preview: what will be created, the method, and the tenant's state before.</summary>
public sealed record ApplyPreviewItem(string Item, string? PolicyName, string Method, string Before, string After);

public static class StepStatus
{
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Skipped = "Skipped";
    public const string Manual = "Manual";
    public const string Failed = "Failed";
    public const string Warning = "Warning";
}

/// <summary>A file of a deployment package (script or Graph body).</summary>
public sealed record DeploymentFile(string FileName, string Content);

public sealed record ChangeExecutionResult(int ExecutionId, string Status, IReadOnlyList<ExecutionStep> Steps, string Log, int? ReassessmentRunId, IReadOnlyList<DeploymentFile> Package);

/// <summary>
/// Runs an approved change request without writing to the tenant: pre-checks (status, window, tenant, controls,
/// unresolved placeholders, policies that already exist in the latest assessment), a per-control plan (Microsoft Graph
/// request or manual remediation), and a deployment package (Deploy.ps1 plus one JSON body per policy, Conditional
/// Access always in report-only state) for an engineer to run. After the engineer applies it, "Record As Applied"
/// re-assesses the tenant and reports the affected controls. Every run is logged and stored.
/// </summary>
public sealed class ChangeExecutionService(
    IChangeRequestRepository repository,
    ICatalogService catalog,
    ICustomerProfileStore customers,
    IAssessmentService assessments,
    IEngagementRepository results,
    AuditContext audit,
    TimeProvider timeProvider,
    ILogger<ChangeExecutionService> logger,
    ITenantChangeWriter? writer = null,
    IAuditService? auditLog = null)
{
    public static bool CanRun(ChangeRequestRecord request) => request.StatusCode is ChangeStatusCodes.Approved or ChangeStatusCodes.Scheduled;

    /// <summary>Simulates the change and builds its deployment package.</summary>
    public async Task<ChangeExecutionResult> SimulateAsync(ChangeRequestRecord request, IProgress<ExecutionStep>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var run = new Run(timeProvider, progress);
        var package = new List<DeploymentFile>();
        var executionId = await repository.StartExecutionAsync(request.ChangeRequestId, ExecutionModes.Simulate, audit.Actor, cancellationToken);
        try
        {
            var (profile, controls) = await PreCheckAsync(request, run, cancellationToken);
            var latest = await LatestResultsAsync(profile.Id, cancellationToken);
            var script = new StringBuilder();
            script.AppendLine("#Requires -Version 7");
            script.AppendLine("#Requires -Modules Microsoft.Graph.Authentication");
            script.AppendLine($"# Change request #{request.ChangeRequestId}: {request.Title}");
            script.AppendLine($"# Customer: {profile.Name}; tenant: {profile.PrimaryTenant?.PrimaryDomain ?? profile.PrimaryTenant?.DisplayName}");
            script.AppendLine($"# Generated {timeProvider.GetUtcNow().ToLocalTime():yyyy-MM-dd HH:mm} by Secure Plus. Review before running. Conditional Access is created in report-only state.");
            script.AppendLine("Connect-MgGraph -Scopes Policy.ReadWrite.ConditionalAccess, DeviceManagementConfiguration.ReadWrite.All -NoWelcome");
            var pending = new List<CatalogControl>();
            foreach (var control in controls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = $"{control.Id} {control.Name}";
                if (latest.TryGetValue(control.Id, out var result) && result == AssessmentResultValues.Pass)
                {
                    run.Step(name, StepStatus.Skipped, "Already in place according to the latest assessment.");
                    continue;
                }

                if (control.Body.Length == 0 || control.GraphResource.Length == 0)
                {
                    var how = control.Remediation.SettingPath.Length > 0 ? control.Remediation.SettingPath : control.Remediation.GpoPath;
                    run.Step(name, StepStatus.Manual, $"Apply manually{(how.Length > 0 ? $": {how}" : ".")}");
                    if (control.Remediation.PowerShell.Length > 0) package.Add(new DeploymentFile($"{control.Id}.manual.ps1", control.Remediation.PowerShell));
                    continue;
                }

                pending.Add(control);
            }

            // The same plan Apply to Tenant uses: Conditional Access one by one, Intune controls merged per source policy.
            var variables = DeploymentVariables.Load(profile);
            var number = 0;
            foreach (var unit in DeploymentPlanner.Build(pending))
            {
                var file = $"{++number:000}-{SafeFileName(unit.Controls.Count == 1 ? unit.Controls[0].Id : unit.Name)}.json";
                var body = DeploymentVariables.Apply(unit.Body, variables, profile.PrimaryTenant?.TenantId);
                var placeholders = DeploymentVariables.In(body);
                package.Add(new DeploymentFile(file, body));
                script.AppendLine();
                script.AppendLine($"# {unit.Name} ({string.Join(", ", unit.Controls.Select(control => control.Id))})");
                script.AppendLine($"Invoke-MgGraphRequest -Method POST -Uri 'https://graph.microsoft.com/{unit.ApiVersion}/{unit.Resource}' -Body (Get-Content -Raw '.\\{file}') -ContentType 'application/json'");
                var label = unit.Controls.Count == 1 ? $"{unit.Controls[0].Id} {unit.Controls[0].Name}" : $"{unit.Name} ({unit.Controls.Count} controls merged)";
                run.Step(label, placeholders.Count > 0 ? StepStatus.Warning : StepStatus.Succeeded, placeholders.Count > 0
                    ? $"POST {unit.Resource}; set the deployment variable(s) {string.Join(", ", placeholders)} first (group variables can be group names; Apply to Tenant resolves them)."
                    : $"POST {unit.Resource}{(unit.Kind == CatalogKinds.ConditionalAccess ? " in report-only state" : string.Empty)}.{(unit.FollowUp is null ? string.Empty : " " + unit.FollowUp)}");
            }

            package.Insert(0, new DeploymentFile("Deploy.ps1", script.ToString()));
            run.Step("Deployment package", StepStatus.Succeeded, $"{package.Count} file(s): Deploy.ps1 and the request bodies. Save it, review it and run it, then select Record As Applied.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            run.Fail(exception);
            logger.LogWarning(exception, "Simulation of change request {Id} stopped", request.ChangeRequestId);
        }

        await repository.CompleteExecutionAsync(executionId, run.Status, run.Log, null, CancellationToken.None);
        return new ChangeExecutionResult(executionId, run.Status, run.Steps, run.Log, null, package);
    }

    /// <summary>
    /// Applies an approved change request to the customer's tenant: creates each policy through Microsoft Graph
    /// (Conditional Access always in report-only state), skips policies already in place or already present by name,
    /// leaves controls with unresolved placeholders or no Graph definition for the engineer, audits every write, then
    /// re-assesses the tenant. The caller must have checked the passcode and shown the confirmation prompt.
    /// </summary>
    public async Task<ChangeExecutionResult> ApplyToTenantAsync(ChangeRequestRecord request, IProgress<ExecutionStep>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (writer is null) throw new InvalidOperationException("Live deployment is not available in this build.");
        var run = new Run(timeProvider, progress);
        var executionId = await repository.StartExecutionAsync(request.ChangeRequestId, ExecutionModes.Apply, audit.Actor, cancellationToken);
        int? reassessment = null;
        var created = 0;
        var failed = 0;
        try
        {
            var (profile, controls) = await PreCheckAsync(request, run, cancellationToken);
            var tenant = profile.PrimaryTenant;
            if (tenant?.ApplicationRegistration?.SecretReference is null || tenant.TenantId is null)
                throw new InvalidOperationException($"{profile.Name} has no connected tenant: connect it on Tenant App Registration first.");
            if (tenant.DeploymentRegistration?.SecretReference is null)
            {
                run.Step("Deployment access", StepStatus.Failed, "The Deploy registration (write permissions) is not enabled for this tenant.");
                throw new InvalidOperationException($"Deployment access is not enabled for {profile.Name}. Use Enable deployment access on Customer Profiles for this " +
                                                    "deploy phase, and Remove deployment access when it ends.");
            }

            run.Step("Deployment access", StepStatus.Succeeded,
                $"Deploy registration {tenant.DeploymentRegistration.ClientId} (certificate expires {tenant.DeploymentRegistration.CredentialExpirationUtc:yyyy-MM-dd}).");
            var latest = await LatestResultsAsync(profile.Id, cancellationToken);
            var existing = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
            var pending = new List<CatalogControl>();
            foreach (var control in controls)
            {
                var name = $"{control.Id} {control.Name}";
                if (latest.TryGetValue(control.Id, out var result) && result == AssessmentResultValues.Pass)
                    run.Step(name, StepStatus.Skipped, "Already in place according to the latest assessment.");
                else if (control.Body.Length == 0 || control.GraphResource.Length == 0)
                {
                    var how = control.Remediation.SettingPath.Length > 0 ? control.Remediation.SettingPath : control.Remediation.GpoPath;
                    run.Step(name, StepStatus.Manual, $"No Microsoft Graph definition: apply manually{(how.Length > 0 ? $": {how}" : ".")}");
                }
                else pending.Add(control);
            }

            // Deployment variables: group names become object IDs in this tenant.
            var variables = DeploymentVariables.Load(profile);
            foreach (var (key, value) in variables.ToArray())
            {
                if (!DeploymentVariables.IsGroup(key) || Guid.TryParse(value, out _)) continue;
                var id = await writer.ResolveGroupIdAsync(tenant, value, cancellationToken);
                if (id is null)
                {
                    run.Step($"Variable {key}", StepStatus.Warning, $"No single group named '{value}' was found in the tenant; policies that use <{key}> are not created.");
                    variables.Remove(key);
                }
                else variables[key] = id;
            }

            foreach (var unit in DeploymentPlanner.Build(pending))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var label = unit.Controls.Count == 1 ? $"{unit.Controls[0].Id} {unit.Controls[0].Name}" : $"{unit.Name} ({unit.Controls.Count} controls)";
                var body = DeploymentVariables.Apply(unit.Body, variables, tenant.TenantId);
                var missing = DeploymentVariables.In(body);
                if (missing.Count > 0)
                {
                    run.Step(label, StepStatus.Manual, $"Not created: set the deployment variable(s) {string.Join(", ", missing)} for {profile.Name} first.");
                    continue;
                }

                var displayName = DisplayName(body);
                if (displayName is not null)
                {
                    if (!existing.TryGetValue(unit.Resource, out var names))
                        existing[unit.Resource] = names = await writer.ExistingNamesAsync(tenant, unit.ApiVersion, unit.Resource, cancellationToken);
                    if (names.Contains(displayName))
                    {
                        run.Step(label, StepStatus.Skipped, $"A policy named '{displayName}' already exists in the tenant; it was not changed.");
                        continue;
                    }
                }

                var write = await writer.CreateAsync(tenant, unit.ApiVersion, unit.Resource, body, cancellationToken);
                if (write.Succeeded)
                {
                    created++;
                    run.Step(label, StepStatus.Succeeded, $"Created '{displayName}'{(unit.Kind == CatalogKinds.ConditionalAccess ? " in report-only state" : string.Empty)} " +
                                                          $"(id {write.CreatedId ?? "not returned"}).{(unit.FollowUp is null ? string.Empty : " " + unit.FollowUp)}");
                }
                else
                {
                    failed++;
                    run.Step(label, StepStatus.Warning, $"Not created: {write.Error}");
                }

                if (auditLog is not null)
                    await auditLog.RecordAsync(write.Succeeded ? Domain.Auditing.AuditEventTypes.TenantChangeApplied : Domain.Auditing.AuditEventTypes.TenantChangeFailed,
                        write.Succeeded ? Domain.Auditing.AuditOutcome.Success : Domain.Auditing.AuditOutcome.Failure,
                        $"{(write.Succeeded ? "Created" : "Could not create")} '{displayName}' in {tenant.PrimaryDomain ?? tenant.DisplayName} (change request #{request.ChangeRequestId}).",
                        new Dictionary<string, string?>
                        {
                            ["ChangeRequest"] = request.ChangeRequestId.ToString(System.Globalization.CultureInfo.InvariantCulture), ["Customer"] = profile.Id.Value,
                            ["Tenant"] = tenant.TenantId?.ToString(), ["Controls"] = string.Join(", ", unit.Controls.Select(control => control.Id)), ["Resource"] = unit.Resource,
                            ["Policy"] = displayName, ["CreatedId"] = write.CreatedId, ["Error"] = write.Error
                        }, CancellationToken.None);
            }

            run.Step("Tenant changes", failed == 0 ? StepStatus.Succeeded : StepStatus.Warning, $"{created} policy(ies) created, {failed} failed.");
            if (created > 0)
            {
                run.Step("Re-assessment", StepStatus.Running, "Assessing the tenant again...");
                var outcome = await assessments.RunAsync(new AssessmentRunRequest(profile.Id, AssessmentRunSources.Manual,
                    Reason: $"Re-assessment after applying change request #{request.ChangeRequestId}"), null, cancellationToken);
                reassessment = outcome.RunId;
                run.Step("Re-assessment", outcome.Succeeded ? StepStatus.Succeeded : StepStatus.Warning, outcome.Summary);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            run.Fail(exception);
            logger.LogWarning(exception, "Applying change request {Id} stopped", request.ChangeRequestId);
        }

        await repository.CompleteExecutionAsync(executionId, run.Status, run.Log, reassessment, CancellationToken.None);
        return new ChangeExecutionResult(executionId, run.Status, run.Steps, run.Log, reassessment, []);
    }

    /// <summary>
    /// Read-only preview of <see cref="ApplyToTenantAsync"/> for the confirmation window (4.0): each policy that would be created, with its
    /// "before" state in the tenant (already present by name, or not present). Nothing is written.
    /// </summary>
    public async Task<IReadOnlyList<ApplyPreviewItem>> PreviewApplyAsync(ChangeRequestRecord request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (writer is null) throw new InvalidOperationException("Live deployment is not available in this build.");
        var run = new Run(timeProvider, null);
        var (profile, controls) = await PreCheckAsync(request, run, cancellationToken);
        var tenant = profile.PrimaryTenant;
        if (tenant?.ApplicationRegistration?.SecretReference is null || tenant.TenantId is null)
            throw new InvalidOperationException($"{profile.Name} has no connected tenant: connect it on Tenant App Registration first.");
        var items = new List<ApplyPreviewItem>();
        var pending = new List<CatalogControl>();
        foreach (var control in controls)
        {
            if (control.Body.Length == 0 || control.GraphResource.Length == 0)
                items.Add(new ApplyPreviewItem($"{control.Id} {control.Name}", null, "Manual", "No Microsoft Graph definition", "Apply manually and attest it on the task"));
            else pending.Add(control);
        }

        var variables = DeploymentVariables.Load(profile);
        var existing = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in DeploymentPlanner.Build(pending))
        {
            var label = unit.Controls.Count == 1 ? $"{unit.Controls[0].Id} {unit.Controls[0].Name}" : $"{unit.Name} ({unit.Controls.Count} controls)";
            var body = DeploymentVariables.Apply(unit.Body, variables, tenant.TenantId);
            var name = DisplayName(body);
            var state = unit.Kind == CatalogKinds.ConditionalAccess ? "Created in report-only state" : "Created unassigned";
            if (name is null)
            {
                items.Add(new ApplyPreviewItem(label, null, "Microsoft Graph", "Unknown (no policy name)", state));
                continue;
            }

            if (!existing.TryGetValue(unit.Resource, out var names))
                existing[unit.Resource] = names = await writer.ExistingNamesAsync(tenant, unit.ApiVersion, unit.Resource, cancellationToken);
            items.Add(names.Contains(name)
                ? new ApplyPreviewItem(label, name, "Microsoft Graph", "Already present in the tenant", "Skipped: not changed")
                : new ApplyPreviewItem(label, name, "Microsoft Graph", "Not present in the tenant", state));
        }

        return items;
    }

    private static string SafeFileName(string text)
    {
        var clean = new string(text.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-').ToArray()).Trim('-');
        return clean.Length > 60 ? clean[..60] : clean;
    }

    /// <summary>The body to POST: server-managed properties removed.</summary>
    public static string CreateBody(string body)
    {
        var node = JsonNode.Parse(body)!.AsObject();
        foreach (var name in new[] { "id", "createdDateTime", "lastModifiedDateTime", "modifiedDateTime", "version", "settingCount", "isAssigned", "creationSource",
                     "roleScopeTagIds@odata.type" })
            node.Remove(name);
        return node.ToJsonString(DeploymentJson.Options);
    }

    /// <summary>The policy's display name (displayName or name), used to avoid creating it twice.</summary>
    public static string? DisplayName(string body)
    {
        var node = JsonNode.Parse(body)?.AsObject();
        return (node?["displayName"] ?? node?["name"])?.GetValue<string>();
    }

    /// <summary>
    /// Records that the engineer applied the change: re-assesses the tenant, reports the affected controls and marks the
    /// request Completed when they are all in place.
    /// </summary>
    public async Task<ChangeExecutionResult> RecordAppliedAsync(ChangeRequestRecord request, IProgress<ExecutionStep>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var run = new Run(timeProvider, progress);
        var executionId = await repository.StartExecutionAsync(request.ChangeRequestId, ExecutionModes.Execute, audit.Actor, cancellationToken);
        int? reassessment = null;
        try
        {
            var (profile, controls) = await PreCheckAsync(request, run, cancellationToken);
            if (profile.PrimaryTenant?.ApplicationRegistration?.SecretReference is null) throw new InvalidOperationException($"{profile.Name} has no connected tenant to re-assess.");
            run.Step("Re-assessment", StepStatus.Running, "Assessing the tenant again...");
            var outcome = await assessments.RunAsync(new AssessmentRunRequest(profile.Id, AssessmentRunSources.Manual, Reason: $"Re-assessment after change request #{request.ChangeRequestId}"),
                null, cancellationToken);
            reassessment = outcome.RunId;
            if (!outcome.Succeeded) throw new InvalidOperationException($"The re-assessment did not complete: {outcome.Summary}");
            run.Step("Re-assessment", StepStatus.Succeeded, outcome.Summary);
            var latest = await LatestResultsAsync(profile.Id, cancellationToken);
            var inPlace = 0;
            foreach (var control in controls)
            {
                var result = latest.GetValueOrDefault(control.Id, AssessmentResultValues.NotEvaluated);
                var ok = result == AssessmentResultValues.Pass;
                inPlace += ok ? 1 : 0;
                run.Step($"{control.Id} {control.Name}", ok ? StepStatus.Succeeded : result is AssessmentResultValues.Manual or AssessmentResultValues.NotEvaluated ? StepStatus.Manual : StepStatus.Warning,
                    ok ? "In place." : $"Result after the change: {result}.");
            }

            if (inPlace == controls.Length)
            {
                await repository.SaveChangeRequestAsync(request with { StatusCode = ChangeStatusCodes.Completed, CompletedAtUtc = timeProvider.GetUtcNow() }, CancellationToken.None);
                run.Step("Change request", StepStatus.Succeeded, "Every control is in place: the request is Completed.");
            }
            else
            {
                run.Step("Change request", StepStatus.Warning, $"{inPlace} of {controls.Length} control(s) verified; the request stays {request.StatusCode}.");
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            run.Fail(exception);
            logger.LogWarning(exception, "Recording change request {Id} as applied stopped", request.ChangeRequestId);
        }

        await repository.CompleteExecutionAsync(executionId, run.Status, run.Log, reassessment, CancellationToken.None);
        return new ChangeExecutionResult(executionId, run.Status, run.Steps, run.Log, reassessment, []);
    }

    private async Task<(Domain.Customers.CustomerProfile Profile, CatalogControl[] Controls)> PreCheckAsync(ChangeRequestRecord request, Run run, CancellationToken cancellationToken)
    {
        if (!CanRun(request)) throw new InvalidOperationException($"Change request #{request.ChangeRequestId} is {request.StatusCode}; only Approved or Scheduled requests can run.");
        run.Step("Change request", StepStatus.Succeeded, $"#{request.ChangeRequestId} '{request.Title}' is {request.StatusCode}; risk {request.Risk}.");
        var now = timeProvider.GetUtcNow();
        run.Step("Change window", request.WindowStartUtc is { } start && request.WindowEndUtc is { } end && (now < start || now > end) ? StepStatus.Warning : StepStatus.Succeeded,
            request.WindowStartUtc is null ? "No window set." : $"{request.WindowStartUtc.Value.ToLocalTime():g} – {request.WindowEndUtc?.ToLocalTime():g}.");
        var profile = CustomerId.TryCreate(request.CustomerKey, out var id) ? await customers.LoadAsync(id, cancellationToken) : null;
        if (profile is null) throw new InvalidOperationException($"Customer '{request.CustomerKey}' does not exist.");
        var tenant = profile.PrimaryTenant;
        var connected = tenant?.ApplicationRegistration?.SecretReference is not null;
        run.Step("Tenant", connected ? StepStatus.Succeeded : StepStatus.Warning, connected ? tenant!.PrimaryDomain ?? tenant.DisplayName : "No connected tenant: the change cannot be verified.");
        var controls = request.Items.Select(item => catalog.Catalog.Find(item.ItemKey)).OfType<CatalogControl>().DistinctBy(control => control.Id).ToArray();
        if (controls.Length == 0) throw new InvalidOperationException("The change request lists no catalog controls or policies.");
        run.Step("Controls", StepStatus.Succeeded, $"{controls.Length} control(s), {controls.Count(control => control.Body.Length > 0)} with a Microsoft Graph definition.");
        var breakGlass = controls.Select(BreakGlassRule.Problem).OfType<string>().ToArray();
        if (breakGlass.Length > 0)
        {
            run.Step("Break-glass exclusion", StepStatus.Failed, string.Join(" ", breakGlass));
            throw new InvalidOperationException($"Not run: {breakGlass.Length} Conditional Access polic(ies) do not exclude the emergency-access (break-glass) group. " +
                                                "Fix them on Settings > Policies and Policy Sets.");
        }

        if (controls.Any(control => control.Kind == CatalogKinds.ConditionalAccess))
            run.Step("Break-glass exclusion", StepStatus.Succeeded, "Every Conditional Access policy excludes the emergency-access (break-glass) group.");
        return (profile, controls);
    }

    private async Task<Dictionary<string, string>> LatestResultsAsync(CustomerId customerId, CancellationToken cancellationToken)
    {
        if (!results.IsAvailable) return [];
        var latest = (await results.ListAssessmentsAsync(customerId, cancellationToken)).Where(run => AssessmentStatusCodes.HasResults(run.StatusCode))
            .OrderByDescending(run => run.CompletedAtUtc ?? run.RequestedAtUtc).FirstOrDefault();
        if (latest is null) return [];
        return (await results.ListResultsAsync(latest.AssessmentRunId, cancellationToken))
            .GroupBy(result => catalog.Catalog.ResolveId(result.ControlId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Result, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The policy body with server-managed properties removed and the state forced to report-only.</summary>
    public static string ReportOnly(string body)
    {
        var node = JsonNode.Parse(body)!.AsObject();
        foreach (var name in new[] { "id", "createdDateTime", "modifiedDateTime", "templateId" }) node.Remove(name);
        node["state"] = "enabledForReportingButNotEnforced";
        return node.ToJsonString(DeploymentJson.Options);
    }

    private static string Pretty(string body) => JsonNode.Parse(body)!.ToJsonString(DeploymentJson.Options);

    private sealed class Run(TimeProvider timeProvider, IProgress<ExecutionStep>? progress)
    {
        private readonly StringBuilder _log = new();
        public List<ExecutionStep> Steps { get; } = [];
        public bool Failed { get; private set; }
        public string Status => Failed ? ExecutionStatusCodes.Failed : ExecutionStatusCodes.Succeeded;
        public string Log => _log.ToString();

        public void Step(string name, string status, string detail)
        {
            var step = new ExecutionStep(name, status, detail);
            Steps.Add(step);
            _log.AppendLine($"[{timeProvider.GetUtcNow().ToLocalTime():HH:mm:ss}] {status,-9} {name}: {detail}");
            progress?.Report(step);
        }

        public void Fail(Exception exception)
        {
            Failed = true;
            Step("Stopped", StepStatus.Failed, exception is OperationCanceledException ? "Cancelled." : exception.Message);
        }
    }
}
