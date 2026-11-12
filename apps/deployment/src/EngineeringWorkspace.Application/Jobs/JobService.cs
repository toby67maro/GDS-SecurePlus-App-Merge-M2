using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Application.Jobs;

public enum JobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public enum JobStepState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped
}

/// <summary>One step of a job as shown in the activity panel.</summary>
public sealed record JobStepSnapshot(string Name, JobStepState State, string? Detail, DateTimeOffset? StartedAtUtc, DateTimeOffset? CompletedAtUtc);

/// <summary>Immutable view of a job; a new snapshot is published on every change.</summary>
public sealed record JobSnapshot(
    Guid Id,
    string Title,
    string Category,
    string? Subject,
    JobState State,
    double Progress,
    string? CurrentActivity,
    IReadOnlyList<JobStepSnapshot> Steps,
    DateTimeOffset QueuedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? Result,
    string? Error)
{
    public bool IsFinished => State is JobState.Succeeded or JobState.Failed or JobState.Cancelled;
    public TimeSpan? Duration => StartedAtUtc is { } started ? (CompletedAtUtc ?? DateTimeOffset.UtcNow) - started : null;
}

/// <summary>What a running job uses to report its steps and progress.</summary>
public interface IJobContext
{
    Guid JobId { get; }
    CancellationToken CancellationToken { get; }

    /// <summary>Starts a named step (completing a running one first).</summary>
    void BeginStep(string name, string? detail = null);

    void CompleteStep(string? detail = null);
    void FailStep(string detail);
    void SkipStep(string name, string reason);

    /// <summary>Overall progress 0-100 and an optional activity text.</summary>
    void Report(double progress, string? activity = null);
}

/// <summary>
/// Background jobs (assessments, app registration, synchronization) with steps and progress. Jobs run off the UI
/// thread; <see cref="JobChanged"/> is raised on a background thread with an immutable snapshot.
/// </summary>
public interface IJobService
{
    IReadOnlyList<JobSnapshot> Jobs { get; }
    int ActiveCount { get; }
    event EventHandler<JobSnapshot>? JobChanged;

    /// <summary>Queues a job. The work returns a short result text shown when it completes.</summary>
    JobSnapshot Enqueue(string title, string category, string? subject, IReadOnlyList<string> plannedSteps, Func<IJobContext, Task<string?>> work);

    void Cancel(Guid jobId);
    void ClearFinished();
}

public sealed class JobService(TimeProvider timeProvider, ILogger<JobService> logger) : IJobService, IDisposable
{
    /// <summary>At most this many jobs run at once; the rest wait in the queue.</summary>
    public const int MaximumConcurrency = 3;

    private readonly TimeProvider _time = timeProvider;
    private readonly ConcurrentDictionary<Guid, JobRecord> _jobs = new();
    private readonly SemaphoreSlim _slots = new(MaximumConcurrency, MaximumConcurrency);

    public event EventHandler<JobSnapshot>? JobChanged;

    public IReadOnlyList<JobSnapshot> Jobs => _jobs.Values.Select(job => job.Snapshot).OrderByDescending(job => job.QueuedAtUtc).ToArray();

    public int ActiveCount => _jobs.Values.Count(job => !job.Snapshot.IsFinished);

    public JobSnapshot Enqueue(string title, string category, string? subject, IReadOnlyList<string> plannedSteps, Func<IJobContext, Task<string?>> work)
    {
        var record = new JobRecord(this, Guid.NewGuid(), title, category, subject, plannedSteps, _time.GetUtcNow());
        _jobs[record.Id] = record;
        Publish(record);
        _ = Task.Run(() => RunAsync(record, work));
        return record.Snapshot;
    }

    public void Cancel(Guid jobId)
    {
        if (_jobs.TryGetValue(jobId, out var record)) record.Cancellation.Cancel();
    }

    public void ClearFinished()
    {
        foreach (var record in _jobs.Values.Where(job => job.Snapshot.IsFinished).ToArray()) _jobs.TryRemove(record.Id, out _);
    }

    public void Dispose()
    {
        foreach (var record in _jobs.Values) record.Cancellation.Cancel();
        _slots.Dispose();
    }

    private async Task RunAsync(JobRecord record, Func<IJobContext, Task<string?>> work)
    {
        try
        {
            await _slots.WaitAsync(record.Cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            record.Finish(JobState.Cancelled, null, "Cancelled before it started.");
            return;
        }

        try
        {
            record.Start();
            var result = await work(record);
            record.Finish(JobState.Succeeded, result, null);
            logger.LogInformation("Job {Title} completed: {Result}", record.Title, result);
        }
        catch (OperationCanceledException)
        {
            record.Finish(JobState.Cancelled, null, "Cancelled.");
            logger.LogInformation("Job {Title} cancelled", record.Title);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            record.Finish(JobState.Failed, null, exception.Message);
            logger.LogWarning(exception, "Job {Title} failed", record.Title);
        }
        finally
        {
            _slots.Release();
        }
    }

    private void Publish(JobRecord record)
    {
        try
        {
            JobChanged?.Invoke(this, record.Snapshot);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning(exception, "A job listener failed");
        }
    }

    private sealed class JobRecord : IJobContext
    {
        private readonly JobService _owner;
        private readonly object _gate = new();
        private readonly List<JobStepSnapshot> _steps;
        private JobSnapshot _snapshot;
        private int _currentStep = -1;

        public JobRecord(JobService owner, Guid id, string title, string category, string? subject, IReadOnlyList<string> plannedSteps, DateTimeOffset queuedAt)
        {
            _owner = owner;
            Id = id;
            Title = title;
            _steps = plannedSteps.Select(name => new JobStepSnapshot(name, JobStepState.Pending, null, null, null)).ToList();
            _snapshot = new JobSnapshot(id, title, category, subject, JobState.Queued, 0, "Waiting to start", _steps.ToArray(), queuedAt, null, null, null, null);
        }

        public Guid Id { get; }
        public string Title { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public Guid JobId => Id;
        public CancellationToken CancellationToken => Cancellation.Token;

        public JobSnapshot Snapshot
        {
            get
            {
                lock (_gate) return _snapshot;
            }
        }

        public void Start() => Update(snapshot => snapshot with { State = JobState.Running, StartedAtUtc = Now, CurrentActivity = "Starting" });

        public void BeginStep(string name, string? detail = null)
        {
            lock (_gate)
            {
                if (_currentStep >= 0 && _steps[_currentStep].State == JobStepState.Running)
                    _steps[_currentStep] = _steps[_currentStep] with { State = JobStepState.Succeeded, CompletedAtUtc = Now };
                var index = _steps.FindIndex(step => step.Name == name && step.State == JobStepState.Pending);
                if (index < 0)
                {
                    _steps.Add(new JobStepSnapshot(name, JobStepState.Pending, null, null, null));
                    index = _steps.Count - 1;
                }

                _steps[index] = _steps[index] with { State = JobStepState.Running, Detail = detail, StartedAtUtc = Now };
                _currentStep = index;
                var done = _steps.Count(step => step.State is JobStepState.Succeeded or JobStepState.Skipped or JobStepState.Failed);
                _snapshot = _snapshot with { Steps = _steps.ToArray(), CurrentActivity = name, Progress = Math.Max(_snapshot.Progress, 100.0 * done / Math.Max(1, _steps.Count)) };
            }

            _owner.Publish(this);
        }

        public void CompleteStep(string? detail = null) => SetCurrent(JobStepState.Succeeded, detail);

        public void FailStep(string detail) => SetCurrent(JobStepState.Failed, detail);

        public void SkipStep(string name, string reason)
        {
            lock (_gate)
            {
                var index = _steps.FindIndex(step => step.Name == name && step.State == JobStepState.Pending);
                if (index < 0)
                {
                    _steps.Add(new JobStepSnapshot(name, JobStepState.Skipped, reason, Now, Now));
                }
                else
                {
                    _steps[index] = _steps[index] with { State = JobStepState.Skipped, Detail = reason, CompletedAtUtc = Now };
                }

                _snapshot = _snapshot with { Steps = _steps.ToArray() };
            }

            _owner.Publish(this);
        }

        public void Report(double progress, string? activity = null) =>
            Update(snapshot => snapshot with { Progress = Math.Clamp(progress, 0, 100), CurrentActivity = activity ?? snapshot.CurrentActivity });

        public void Finish(JobState state, string? result, string? error)
        {
            lock (_gate)
            {
                if (_currentStep >= 0 && _steps[_currentStep].State == JobStepState.Running)
                    _steps[_currentStep] = _steps[_currentStep] with
                    {
                        State = state == JobState.Succeeded ? JobStepState.Succeeded : JobStepState.Failed,
                        Detail = state == JobState.Succeeded ? _steps[_currentStep].Detail : error,
                        CompletedAtUtc = Now
                    };
                for (var index = 0; index < _steps.Count; index++)
                    if (_steps[index].State == JobStepState.Pending)
                        _steps[index] = _steps[index] with { State = JobStepState.Skipped, Detail = state == JobState.Succeeded ? "Not needed" : "Not run" };
                _snapshot = _snapshot with
                {
                    State = state,
                    Steps = _steps.ToArray(),
                    Progress = state == JobState.Succeeded ? 100 : _snapshot.Progress,
                    CompletedAtUtc = Now,
                    Result = result,
                    Error = error,
                    CurrentActivity = state switch { JobState.Succeeded => "Completed", JobState.Cancelled => "Cancelled", _ => "Failed" }
                };
            }

            _owner.Publish(this);
        }

        private DateTimeOffset Now => _owner._time.GetUtcNow();

        private void SetCurrent(JobStepState state, string? detail)
        {
            lock (_gate)
            {
                if (_currentStep < 0) return;
                _steps[_currentStep] = _steps[_currentStep] with { State = state, Detail = detail ?? _steps[_currentStep].Detail, CompletedAtUtc = Now };
                var done = _steps.Count(step => step.State is JobStepState.Succeeded or JobStepState.Skipped or JobStepState.Failed);
                _snapshot = _snapshot with { Steps = _steps.ToArray(), Progress = Math.Max(_snapshot.Progress, 100.0 * done / Math.Max(1, _steps.Count)) };
            }

            _owner.Publish(this);
        }

        private void Update(Func<JobSnapshot, JobSnapshot> change)
        {
            lock (_gate) _snapshot = change(_snapshot);
            _owner.Publish(this);
        }
    }
}
