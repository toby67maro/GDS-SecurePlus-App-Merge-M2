using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Jobs;
using EngineeringWorkspace.Application.Workspaces;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Workspaces;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Application.Assessments;

/// <summary>What to assess and why.</summary>
public sealed record AssessmentRunRequest(CustomerId CustomerId, string Source = AssessmentRunSources.Manual, int? PendingRunId = null, int? ScheduleId = null, string? Reason = null,
    string ScanType = ScanTypes.InDepth);

/// <summary>Stages reported while an assessment runs.</summary>
public static class AssessmentStages
{
    public const string Connecting = "Connecting";
    public const string Running = "Running";
    public const string Saving = "Saving";
}

public sealed record AssessmentProgressUpdate(string Stage, double Percent, string Activity);

/// <summary>The result of one assessment run. <see cref="StatusCode"/> is an <see cref="AssessmentStatusCodes"/> value.</summary>
public sealed record AssessmentRunOutcome(CustomerId CustomerId, int? RunId, string StatusCode, decimal? Score, int ResultsSaved, int Failing, string Summary, string? Error)
{
    public bool Succeeded => StatusCode == AssessmentStatusCodes.Completed;

    /// <summary>What was read from the tenant (live runs show it); null when the tenant was not read.</summary>
    public TenantSnapshot? Snapshot { get; init; }
}

/// <summary>Runs assessments: directly (live and scheduled runs) or as background jobs (visible in the activity panel).</summary>
public interface IAssessmentService
{
    /// <summary>Queues an assessment of one customer; a pending run (for example the initial assessment) is completed instead of creating a new one.</summary>
    JobSnapshot Queue(CustomerId customerId, int? pendingRunId = null, string? reason = null);

    /// <summary>Queues a quick or in-depth scan (<see cref="ScanTypes"/>).</summary>
    JobSnapshot QueueScan(CustomerId customerId, string scanType, string? reason = null) => Queue(customerId, (int?)null, reason);

    IReadOnlyList<JobSnapshot> QueueMany(IEnumerable<CustomerId> customerIds, string? reason = null);

    /// <summary>Queues every Pending assessment in the database. Returns how many were queued.</summary>
    Task<int> QueuePendingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs one assessment now and stores it: run header, per-control results (written in chunks), evidence and errors.
    /// Never throws for tenant or database failures: the outcome is Failed, or Partial when some results were saved.
    /// Cancellation stops the run gracefully (status Cancelled or Partial).
    /// </summary>
    Task<AssessmentRunOutcome> RunAsync(AssessmentRunRequest request, IProgress<AssessmentProgressUpdate>? progress, CancellationToken cancellationToken);

    /// <summary>Raised when an assessment completes (successfully or not).</summary>
    event EventHandler<AssessmentCompletedEventArgs>? Completed;
}

public sealed record AssessmentCompletedEventArgs(CustomerId CustomerId, bool Succeeded, decimal? Score, string Summary);

public sealed class AssessmentService(
    IJobService jobs,
    ICustomerProfileStore customers,
    IWorkspaceStore workspaces,
    IEngagementRepository repository,
    ITenantDataCollector collector,
    IAssessmentEvaluator evaluator,
    ICatalogService catalog,
    IScoringScope scoring,
    TimeProvider timeProvider,
    ILogger<AssessmentService> logger,
    Tenants.ITenantInventoryRepository? inventory = null) : IAssessmentService
{
    private static readonly string[] Steps = [AssessmentStages.Connecting, AssessmentStages.Running, AssessmentStages.Saving];

    /// <summary>Licences read from the tenant fill kb.LicenseSku and cust.TenantLicense. A failure never fails the assessment.</summary>
    private async Task RecordLicensesAsync(Domain.Customers.CustomerProfile profile, Domain.Tenants.TenantConfiguration tenant, TenantSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (repository is not ICustomerProfileRepository profiles || !snapshot.Datasets.TryGetValue(TenantDatasets.SubscribedSkus, out var skus) || !skus.IsCollected) return;
        try
        {
            await profiles.RecordTenantLicensesAsync(profile.Id.Value, tenant.TenantKey, TenantLicenses.FromSubscribedSkus(skus.Items),
                DateOnly.FromDateTime(snapshot.CollectedAtUtc.UtcDateTime), cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
        {
            logger.LogWarning(exception, "Licences of {Customer} could not be recorded", profile.Name);
        }
    }

    public event EventHandler<AssessmentCompletedEventArgs>? Completed;

    public JobSnapshot QueueScan(CustomerId customerId, string scanType, string? reason = null) =>
        Queue(new AssessmentRunRequest(customerId, AssessmentRunSources.Manual, null, null, reason, ScanTypes.Normalize(scanType)));

    /// <summary>
    /// Entra group IDs the scan reads members of: groups linked to user groups (Entra ID Group) and group deployment variables. Free-text
    /// names from earlier versions are ignored until they are picked from the list.
    /// </summary>
    public static IReadOnlyCollection<Guid> LinkedGroupIds(Domain.Customers.CustomerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var values = profile.Groups.Select(group => group.EntraGroup)
            .Concat(Governance.DeploymentVariables.Load(profile).Where(pair => Governance.DeploymentVariables.IsGroup(pair.Key)).Select(pair => pair.Value));
        return values.Select(value => Guid.TryParse(value?.Trim(), out var id) ? id : (Guid?)null).OfType<Guid>().Distinct().ToArray();
    }

    /// <summary>Saves users, groups, members and devices from the scan. A failure never fails the assessment.</summary>
    private async Task RecordInventoryAsync(Domain.Customers.CustomerProfile profile, TenantSnapshot snapshot, int runId, CancellationToken cancellationToken)
    {
        if (inventory is not { IsAvailable: true }) return;
        try
        {
            var counts = await inventory.SaveAsync(profile.Id.Value, Tenants.TenantInventoryBuilder.Build(snapshot), runId, cancellationToken);
            logger.LogInformation("Inventory of {Customer}: {Users} users, {Groups} groups, {Members} members, {Devices} devices, {Removed} removed", profile.Name, counts.Users,
                counts.Groups, counts.Members, counts.Devices, counts.Removed);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException or FormatException or KeyNotFoundException)
        {
            logger.LogWarning(exception, "Inventory of {Customer} could not be recorded", profile.Name);
        }
    }

    public JobSnapshot Queue(CustomerId customerId, int? pendingRunId = null, string? reason = null) =>
        Queue(new AssessmentRunRequest(customerId, reason?.StartsWith("Scheduled", StringComparison.Ordinal) == true ? AssessmentRunSources.Scheduled : AssessmentRunSources.Manual,
            pendingRunId, null, reason));

    private JobSnapshot Queue(AssessmentRunRequest request) =>
        jobs.Enqueue($"{ScanTypes.Display(request.ScanType)} {request.CustomerId}", "Assessment", request.CustomerId.Value, Steps, async context =>
        {
            var stage = string.Empty;
            var progress = new SynchronousProgress<AssessmentProgressUpdate>(update =>
            {
                if (update.Stage != stage)
                {
                    stage = update.Stage;
                    context.BeginStep(stage);
                }

                context.Report(update.Percent, update.Activity);
            });
            var outcome = await RunAsync(request, progress, context.CancellationToken);
            if (outcome.StatusCode == AssessmentStatusCodes.Cancelled) throw new OperationCanceledException(outcome.Summary);
            if (!outcome.Succeeded) throw new InvalidOperationException(outcome.Summary);
            context.CompleteStep($"Saved as assessment run {outcome.RunId}.");
            return outcome.Summary;
        });

    public IReadOnlyList<JobSnapshot> QueueMany(IEnumerable<CustomerId> customerIds, string? reason = null) =>
        customerIds.Distinct().Select(id => Queue(id, null, reason)).ToArray();

    public async Task<int> QueuePendingAsync(CancellationToken cancellationToken = default)
    {
        if (!repository.IsAvailable) return 0;
        var pending = (await repository.ListAssessmentsAsync(null, cancellationToken))
            .Where(item => item.StatusCode is AssessmentStatusCodes.Pending or AssessmentStatusCodes.Running && !item.IsSample).ToArray();
        var running = jobs.Jobs.Where(job => !job.IsFinished && job.Category == "Assessment").Select(job => job.Subject).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var queued = 0;
        foreach (var item in pending.GroupBy(item => item.CustomerKey).Select(group => group.First()))
        {
            if (running.Contains(item.CustomerKey) || !CustomerId.TryCreate(item.CustomerKey, out var id)) continue;
            Queue(new AssessmentRunRequest(id, AssessmentRunSources.Setup, item.AssessmentRunId, null, "Pending assessment"));
            queued++;
        }

        return queued;
    }

    public async Task<AssessmentRunOutcome> RunAsync(AssessmentRunRequest request, IProgress<AssessmentProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var customerId = request.CustomerId;
        int? runId = null;
        AssessmentResultWriter? writer = null;
        void Report(string stage, double percent, string activity) => progress?.Report(new AssessmentProgressUpdate(stage, percent, activity));
        try
        {
            Report(AssessmentStages.Connecting, 0, request.Reason ?? "Loading the customer...");
            var profile = await customers.LoadAsync(customerId, cancellationToken) ?? throw new InvalidOperationException($"Customer '{customerId}' does not exist.");
            var tenant = profile.PrimaryTenant;
            if (tenant?.ApplicationRegistration?.SecretReference is null)
                throw new InvalidOperationException($"{profile.Name} has no connected tenant. Open Workspace > Customer Profiles > Tenant App Registration and select Register automatically.");
            if (!repository.IsAvailable) throw new InvalidOperationException("The application database is not ready.");
            await repository.UpsertCustomerAsync(profile, false, cancellationToken);
            var workspace = (await workspaces.ListAsync(cancellationToken)).FirstOrDefault(item => item.CurrentCustomerId == customerId);
            var scanType = ScanTypes.Normalize(request.ScanType);
            var kind = ScanTypes.Display(scanType);
            var name = request.Source == AssessmentRunSources.Manual ? $"{kind} {DateTime.Now:yyyy-MM-dd HH:mm}" : $"{request.Source} {kind.ToLowerInvariant()} {DateTime.Now:yyyy-MM-dd HH:mm}";
            runId = await repository.StartAssessmentAsync(customerId, workspace?.Id, name, request.PendingRunId, request.Source, request.ScheduleId, cancellationToken);
            await repository.SetScanTypeAsync(runId.Value, scanType, cancellationToken);
            writer = new AssessmentResultWriter(repository, runId.Value, timeProvider);

            Report(AssessmentStages.Connecting, 5, $"Signing in to {tenant.PrimaryDomain ?? tenant.TenantId?.ToString()} with the app registration...");
            var collect = new SynchronousProgress<(double Progress, string Activity)>(update => Report(AssessmentStages.Running, 5 + update.Progress * 0.7, update.Activity));
            var snapshot = await collector.CollectAsync(tenant, new TenantScanOptions(scanType, LinkedGroupIds(profile)), collect, cancellationToken);
            await RecordLicensesAsync(profile, tenant, snapshot, cancellationToken);
            Report(AssessmentStages.Running, 74, "Saving users, groups and devices...");
            await RecordInventoryAsync(profile, snapshot, runId.Value, cancellationToken);

            // Datasets that could not be read are evidence of why controls were not evaluated.
            var datasetEvidence = snapshot.Datasets.Values.Where(dataset => dataset.Status != DatasetStatus.Collected)
                .Select(dataset => new AssessmentResultItem($"DATASET-{dataset.Name}", "Data collection", $"Read {dataset.Name}", AssessmentResultValues.NotEvaluated, "Info",
                    $"{dataset.Status}: {dataset.Message ?? dataset.Source}", dataset.Status == DatasetStatus.Forbidden ? "Grant the missing Microsoft Graph permission and consent again." : null))
                .ToArray();
            await writer.AddAsync(datasetEvidence, cancellationToken);

            Report(AssessmentStages.Running, 76, "Evaluating controls and checks...");
            var results = evaluator.Evaluate(catalog.Catalog, snapshot);
            // A quick scan reads fewer datasets: controls that need data it did not read are left out rather than saved as "not evaluated".
            if (scanType == ScanTypes.Quick)
                results = results.Where(result => !(result.Result == AssessmentResultValues.NotEvaluated &&
                                                    result.Evidence?.Contains($"({TenantSnapshot.NotCollectedMessage})", StringComparison.Ordinal) == true)).ToArray();
            for (var offset = 0; offset < results.Count; offset += writer.BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.AddAsync(results.Skip(offset).Take(writer.BatchSize), cancellationToken);
                Report(AssessmentStages.Saving, 80 + 19.0 * Math.Min(results.Count, offset + writer.BatchSize) / Math.Max(1, results.Count),
                    $"Saving results: {writer.Saved:N0} of {results.Count + datasetEvidence.Length:N0}...");
            }

            await writer.FlushAsync(cancellationToken);
            var scope = scoring.ControlIds;
            var scored = results.Where(result => AssessmentResultValues.IsScored(result.Result) && (result.ControlId.StartsWith("CHK-", StringComparison.Ordinal) || result.ControlId.StartsWith("TS-", StringComparison.Ordinal) || scope.Contains(result.ControlId)))
                .ToArray();
            var score = AssessmentScoring.Score(scored);
            var passed = scored.Count(result => result.Result == AssessmentResultValues.Pass);
            var failing = scored.Count(result => result.Result is AssessmentResultValues.Fail or AssessmentResultValues.Missing);
            await repository.FinishAssessmentAsync(runId.Value, new AssessmentRunTotals(AssessmentStatusCodes.Completed, score, scored.Length, passed, writer.Saved, null),
                CancellationToken.None);
            var summary = $"{profile.Name}: {score:0.#}% ({failing} gap(s) against the active baselines)";
            Report(AssessmentStages.Saving, 100, summary);
            Completed?.Invoke(this, new AssessmentCompletedEventArgs(customerId, true, score, summary));
            return new AssessmentRunOutcome(customerId, runId, AssessmentStatusCodes.Completed, score, writer.Saved, failing, summary, null) { Snapshot = snapshot };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var cancelled = exception is OperationCanceledException;
            logger.LogWarning(exception, "Assessment of {CustomerId} {Outcome}", customerId.Value, cancelled ? "was cancelled" : "failed");
            var status = cancelled ? AssessmentStatusCodes.Cancelled : AssessmentStatusCodes.Failed;
            var saved = 0;
            if (runId is { } id && repository.IsAvailable)
            {
                try
                {
                    // Final flush: whatever was evaluated is kept; the run is Partial when anything was saved.
                    if (writer is not null)
                    {
                        await writer.FlushAsync(CancellationToken.None);
                        saved = writer.Saved;
                    }

                    if (saved > 0) status = AssessmentStatusCodes.Partial;
                    await repository.AppendResultsAsync(id,
                        [new AssessmentResultItem("ERROR", "Assessment", "Assessment run", AssessmentResultValues.NotEvaluated, "High", exception.Message, null)], CancellationToken.None);
                    await repository.FinishAssessmentAsync(id, new AssessmentRunTotals(status, null, 0, 0, saved + 1, exception.Message), CancellationToken.None);
                }
                catch (Exception inner) when (inner is not OutOfMemoryException)
                {
                    logger.LogWarning(inner, "The assessment run {RunId} could not be marked {Status}", id, status);
                }
            }

            var text = cancelled ? $"{customerId}: cancelled{(saved > 0 ? $" ({saved:N0} results saved)" : string.Empty)}." : $"{customerId}: {exception.Message}";
            Completed?.Invoke(this, new AssessmentCompletedEventArgs(customerId, false, null, text));
            return new AssessmentRunOutcome(customerId, runId, status, null, saved, 0, text, cancelled ? null : exception.Message);
        }
    }

    /// <summary>Reports synchronously on the caller's thread (Progress&lt;T&gt; posts to the thread pool or UI).</summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}

/// <summary>The controls that count towards the assessment score: the union of the active baselines.</summary>
public interface IScoringScope
{
    IReadOnlySet<string> ControlIds { get; }
}

/// <summary>
/// Recurring assessments: editing, enabling, running now, and running due schedules. Due schedules run in the
/// application while it is open (checked every minute) and from the Windows Task Scheduler host
/// (<c>--run-schedules</c>) while it is closed. A schedule occurrence is claimed atomically in the database, so the two
/// hosts never run it twice.
/// </summary>
public interface IAssessmentScheduler
{
    void Start();
    Task<IReadOnlyList<AssessmentSchedule>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Validates, computes the next run and saves. Throws <see cref="ArgumentException"/> when the recurrence is invalid.</summary>
    Task<AssessmentSchedule> SaveAsync(AssessmentSchedule schedule, CancellationToken cancellationToken = default);

    /// <summary>Turns a schedule on (next run recomputed) or off (no next run). Saves immediately.</summary>
    Task SetEnabledAsync(AssessmentSchedule schedule, bool enabled, CancellationToken cancellationToken = default);

    Task DeleteAsync(int scheduleId, CancellationToken cancellationToken = default);

    /// <summary>Starts the schedule now in the background; false when it is already running.</summary>
    Task<bool> RunNowAsync(int scheduleId, CancellationToken cancellationToken = default);

    /// <summary>Runs every due schedule. With <paramref name="waitForCompletion"/> (command-line host) it returns when they finish.</summary>
    Task<int> RunDueAsync(string host, bool waitForCompletion, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScheduleRunInfo>> ListRunsAsync(int scheduleId, int take = 20, CancellationToken cancellationToken = default);

    /// <summary>Schedule IDs running in this process.</summary>
    IReadOnlyCollection<int> RunningInProcess { get; }

    /// <summary>Raised (on a background thread) when a schedule starts or finishes running in this process.</summary>
    event EventHandler<int>? StatusChanged;
}

public sealed class AssessmentScheduler(
    IEngagementRepository repository,
    IAssessmentService assessments,
    ICustomerProfileStore customers,
    TimeProvider timeProvider,
    ILogger<AssessmentScheduler> logger,
    Security.ISchedulerHostService? schedulingComputer = null) : IAssessmentScheduler, IDisposable
{
    public const string AppHost = "Application";
    public const string TaskSchedulerHost = "Task Scheduler";

    /// <summary>A Running status older than this is treated as interrupted (the host stopped).</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(6);
    private const int ConcurrentCustomers = 3;

    private readonly CancellationTokenSource _stop = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Task> _running = new();
    private int _started;

    public event EventHandler<int>? StatusChanged;
    public IReadOnlyCollection<int> RunningInProcess => _running.Keys.ToArray();

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), timeProvider);
            do
            {
                try
                {
                    await RunDueAsync(AppHost, waitForCompletion: false, _stop.Token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
                {
                    logger.LogWarning(exception, "Assessment schedule check failed");
                }
            }
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false));
        });
    }

    public Task<IReadOnlyList<AssessmentSchedule>> ListAsync(CancellationToken cancellationToken = default) => repository.ListSchedulesAsync(cancellationToken);

    public Task<IReadOnlyList<ScheduleRunInfo>> ListRunsAsync(int scheduleId, int take = 20, CancellationToken cancellationToken = default) =>
        repository.ListScheduleRunsAsync(scheduleId, take, cancellationToken);

    public Task<AssessmentSchedule> SaveAsync(AssessmentSchedule schedule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (string.IsNullOrWhiteSpace(schedule.Name)) throw new ArgumentException("Enter a name for the schedule.", nameof(schedule));
        if (schedule.CustomerKeys.Count == 0) throw new ArgumentException("Choose the customer to assess.", nameof(schedule));
        var errors = Scheduling.NextRunCalculator.Validate(schedule.Recurrence).Where(message => !message.IsWarning).ToArray();
        if (errors.Length > 0) throw new ArgumentException(string.Join(" ", errors.Select(error => error.Message)), nameof(schedule));
        var recurrence = schedule.Recurrence.AnchorDate is null
            ? schedule.Recurrence with { AnchorDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), schedule.Zone).DateTime) }
            : schedule.Recurrence;
        var updated = schedule with { Recurrence = recurrence };
        var next = updated.IsEnabled ? Scheduling.NextRunCalculator.Next(recurrence, timeProvider.GetUtcNow(), updated.Zone) : null;
        return repository.SaveScheduleAsync(updated with { NextRunUtc = next }, cancellationToken);
    }

    public Task SetEnabledAsync(AssessmentSchedule schedule, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var next = enabled ? Scheduling.NextRunCalculator.Next(schedule.Recurrence, timeProvider.GetUtcNow(), schedule.Zone) : null;
        return repository.SetScheduleEnabledAsync(schedule.ScheduleId, enabled, next, cancellationToken);
    }

    public Task DeleteAsync(int scheduleId, CancellationToken cancellationToken = default) => repository.DeleteScheduleAsync(scheduleId, cancellationToken);

    public async Task<bool> RunNowAsync(int scheduleId, CancellationToken cancellationToken = default)
    {
        if (_running.ContainsKey(scheduleId)) return false;
        var schedule = (await repository.ListSchedulesAsync(cancellationToken)).FirstOrDefault(item => item.ScheduleId == scheduleId)
                       ?? throw new InvalidOperationException("The schedule no longer exists.");
        if (!await repository.TryClaimScheduleAsync(scheduleId, null, null, StaleAfter, cancellationToken)) return false;
        _ = Track(schedule, AppHost, "Run now");
        return true;
    }

    public async Task<int> RunDueAsync(string host, bool waitForCompletion, CancellationToken cancellationToken = default)
    {
        if (!repository.IsAvailable) return 0;
        // Only the designated scheduling PC runs due schedules (any computer when none is designated).
        if (schedulingComputer is not null)
        {
            try
            {
                if (!await schedulingComputer.MayRunSchedulesAsync(cancellationToken)) return 0;
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                logger.LogWarning(exception, "The scheduling computer could not be checked; due schedules are not run");
                return 0;
            }
        }

        var now = timeProvider.GetUtcNow();
        var started = new List<Task>();
        foreach (var schedule in await repository.ListSchedulesAsync(cancellationToken))
        {
            if (!schedule.IsEnabled || schedule.NextRunUtc is not { } due || due > now || _running.ContainsKey(schedule.ScheduleId)) continue;
            var next = Scheduling.NextRunCalculator.Next(schedule.Recurrence, now, schedule.Zone);
            if (!await repository.TryClaimScheduleAsync(schedule.ScheduleId, due, next, StaleAfter, cancellationToken)) continue;
            started.Add(Track(schedule, host, $"Scheduled ({due.ToLocalTime():yyyy-MM-dd HH:mm})"));
        }

        if (schedulingComputer is not null)
        {
            try
            {
                await schedulingComputer.HeartbeatAsync(started.Count > 0 ? "Started" : null, started.Count > 0 ? $"{started.Count} schedule(s) started by {host}." : null,
                    started.Count > 0, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                logger.LogWarning(exception, "The scheduling computer heartbeat could not be written");
            }
        }

        if (waitForCompletion) await Task.WhenAll(started);
        return started.Count;
    }

    private Task Track(AssessmentSchedule schedule, string host, string reason)
    {
        var task = Task.Run(() => ExecuteAsync(schedule, host, reason, _stop.Token));
        _running[schedule.ScheduleId] = task;
        StatusChanged?.Invoke(this, schedule.ScheduleId);
        _ = task.ContinueWith(_ =>
        {
            _running.TryRemove(schedule.ScheduleId, out Task? _);
            StatusChanged?.Invoke(this, schedule.ScheduleId);
        }, TaskScheduler.Default);
        return task;
    }

    private async Task ExecuteAsync(AssessmentSchedule schedule, string host, string reason, CancellationToken cancellationToken)
    {
        var runId = 0;
        var outcomes = new System.Collections.Concurrent.ConcurrentBag<AssessmentRunOutcome>();
        try
        {
            runId = await repository.StartScheduleRunAsync(schedule.ScheduleId, host, CancellationToken.None);
            var targets = schedule.AllCustomers
                ? (await customers.ListAsync(cancellationToken)).Where(customer => customer.PrimaryTenant?.ApplicationRegistration?.SecretReference is not null).Select(customer => customer.Id).ToArray()
                : schedule.CustomerKeys.Select(key => CustomerId.TryCreate(key, out var id) ? id : (CustomerId?)null).OfType<CustomerId>().ToArray();
            logger.LogInformation("Schedule {Schedule} started by {Host} ({Reason}) for {Count} customer(s)", schedule.Name, host, reason, targets.Length);
            await ThrottledRunner.RunAsync(targets, ConcurrentCustomers, async (id, token) =>
                outcomes.Add(await assessments.RunAsync(new AssessmentRunRequest(id, AssessmentRunSources.Scheduled, null, schedule.ScheduleId, $"Scheduled: {schedule.Name}", schedule.ScanType), null, token)),
                cancellationToken);
            var list = outcomes.ToArray();
            var failed = list.Where(outcome => !outcome.Succeeded).ToArray();
            var status = targets.Length > 0 && failed.Length == 0 ? ScheduleStatusCodes.Succeeded : ScheduleStatusCodes.Failed;
            var message = targets.Length == 0
                ? "No customer with a connected tenant to assess."
                : failed.Length == 0
                    ? $"{list.Length} assessment(s) completed{(list.Length == 1 ? $": {list[0].Summary}" : ".")}"
                    : $"{failed.Length} of {list.Length} failed: {string.Join("; ", failed.Select(outcome => outcome.Summary))}";
            await FinishAsync(schedule.ScheduleId, runId, status, message, list);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning(exception, "Schedule {Schedule} failed", schedule.Name);
            await FinishAsync(schedule.ScheduleId, runId, ScheduleStatusCodes.Failed,
                exception is OperationCanceledException ? "Stopped because the application closed." : exception.Message, outcomes.ToArray());
        }
    }

    private async Task FinishAsync(int scheduleId, int runId, string status, string message, IReadOnlyList<AssessmentRunOutcome> outcomes)
    {
        try
        {
            if (runId > 0)
                await repository.CompleteScheduleRunAsync(runId, status, message, outcomes.Where(outcome => outcome.RunId is not null).Select(outcome => outcome.RunId!.Value).ToArray(),
                    CancellationToken.None);
            await repository.UpdateScheduleStatusAsync(scheduleId, status, message, timeProvider.GetUtcNow(), CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning(exception, "The result of schedule {ScheduleId} could not be saved", scheduleId);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}

/// <summary>Runs work items with at most N at a time; cancellation stops queued items and is passed to running ones.</summary>
public static class ThrottledRunner
{
    public static async Task RunAsync<T>(IEnumerable<T> items, int maxConcurrency, Func<T, CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // A slot freed by a cancelled item can be handed to a waiter before it sees the cancellation.
                cancellationToken.ThrowIfCancellationRequested();
                await work(item, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}

/// <summary>The "Max Concurrent Assessments" rule: default 5, minimum 1, maximum 20.</summary>
public static class ConcurrencyLimit
{
    public const int Default = 5;
    public const int Minimum = 1;
    public const int Maximum = 20;

    /// <summary>Clamps a requested value; <c>Corrected</c> is true when it had to change.</summary>
    public static (int Value, bool Corrected) Clamp(int requested)
    {
        var value = Math.Clamp(requested, Minimum, Maximum);
        return (value, value != requested);
    }
}
