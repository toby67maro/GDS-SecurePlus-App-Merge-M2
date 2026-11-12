using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Customers;
using EngineeringWorkspace.Domain.Workspaces;
using FluentAssertions;

namespace EngineeringWorkspace.Application.Tests;

public sealed class AssessmentResultWriterTests
{
    private static AssessmentResultItem Item(int index) => new($"C-{index}", "Area", "Control", AssessmentResultValues.Pass, "Low", null, null);

    [Fact]
    public async Task Writes_in_chunks_of_the_batch_size_and_flushes_the_rest()
    {
        var repository = new RecordingRepository();
        var writer = new AssessmentResultWriter(repository, 7, new ManualTime(), batchSize: 100, flushInterval: TimeSpan.FromHours(1));

        await writer.AddAsync(Enumerable.Range(0, 250).Select(Item), CancellationToken.None);
        repository.Chunks.Select(chunk => chunk.Count).Should().Equal(100, 100);
        writer.Saved.Should().Be(200);

        await writer.FlushAsync(CancellationToken.None);
        repository.Chunks.Select(chunk => chunk.Count).Should().Equal(100, 100, 50);
        writer.Saved.Should().Be(250);
        writer.All.Should().HaveCount(250);
    }

    [Fact]
    public async Task Flushes_when_the_interval_elapses()
    {
        var time = new ManualTime();
        var repository = new RecordingRepository();
        var writer = new AssessmentResultWriter(repository, 7, time, batchSize: 1000, flushInterval: TimeSpan.FromSeconds(5));

        await writer.AddAsync([Item(1)], CancellationToken.None);
        repository.Chunks.Should().BeEmpty();
        time.Advance(TimeSpan.FromSeconds(6));
        await writer.AddAsync([Item(2)], CancellationToken.None);
        repository.Chunks.Should().ContainSingle().Which.Should().HaveCount(2);
    }

    [Fact]
    public async Task Keeps_saved_chunks_when_a_later_chunk_fails()
    {
        var repository = new RecordingRepository { FailOnChunk = 2 };
        var writer = new AssessmentResultWriter(repository, 7, new ManualTime(), batchSize: 10);

        var act = () => writer.AddAsync(Enumerable.Range(0, 25).Select(Item), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
        writer.Saved.Should().Be(10);
        repository.Chunks.Should().ContainSingle();

        repository.FailOnChunk = null;
        await writer.FlushAsync(CancellationToken.None);
        writer.Saved.Should().Be(20);
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    private sealed class RecordingRepository : IEngagementRepository
    {
        public List<IReadOnlyList<AssessmentResultItem>> Chunks { get; } = [];
        public int? FailOnChunk { get; set; }
        public bool IsAvailable => true;

        public Task AppendResultsAsync(int runId, IReadOnlyList<AssessmentResultItem> results, CancellationToken cancellationToken = default)
        {
            if (FailOnChunk == Chunks.Count + 1) throw new InvalidOperationException("database unavailable");
            Chunks.Add(results.ToArray());
            return Task.CompletedTask;
        }

        public Task UpsertCustomerAsync(CustomerProfile customer, bool isSample, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertWorkspaceAsync(Workspace workspace, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CustomerEngagementState> GetCustomerStateAsync(CustomerId customerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AssessmentSummary>> ListAssessmentsAsync(CustomerId? customerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<StoredAssessmentFinding>> ListFindingsAsync(int assessmentRunId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AssessmentSummary> RecordAssessmentAsync(RecordAssessmentRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> StartAssessmentAsync(CustomerId customerId, WorkspaceId? workspaceId, string name, int? pendingRunId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<int> StartAssessmentAsync(CustomerId customerId, WorkspaceId? workspaceId, string name, int? pendingRunId, string source, int? scheduleId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task FinishAssessmentAsync(int runId, AssessmentRunTotals totals, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CompleteAssessmentAsync(int runId, string statusCode, decimal? score, IReadOnlyList<AssessmentResultItem> results, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<AssessmentResultItem>> ListResultsAsync(int runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AssessmentSchedule>> ListSchedulesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AssessmentSchedule> SaveScheduleAsync(AssessmentSchedule schedule, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteScheduleAsync(int scheduleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryClaimScheduleAsync(int scheduleId, DateTimeOffset? expectedNextRunUtc, DateTimeOffset? nextRunUtc, TimeSpan staleAfter,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateScheduleStatusAsync(int scheduleId, string status, string? message, DateTimeOffset? lastRunUtc, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task SetScheduleEnabledAsync(int scheduleId, bool enabled, DateTimeOffset? nextRunUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> StartScheduleRunAsync(int scheduleId, string host, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CompleteScheduleRunAsync(int scheduleRunId, string status, string? message, IReadOnlyList<int> assessmentRunIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ScheduleRunInfo>> ListScheduleRunsAsync(int scheduleId, int take, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
