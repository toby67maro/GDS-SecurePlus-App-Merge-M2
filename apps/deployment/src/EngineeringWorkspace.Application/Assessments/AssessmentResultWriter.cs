namespace EngineeringWorkspace.Application.Assessments;

/// <summary>
/// Buffers assessment results and writes them in chunks (every <see cref="BatchSize"/> results or every
/// <see cref="FlushInterval"/>), each chunk in its own transaction, so a run that fails part-way keeps what was saved.
/// Not thread-safe: one writer per run.
/// </summary>
public sealed class AssessmentResultWriter(IEngagementRepository repository, int runId, TimeProvider timeProvider, int batchSize = 250, TimeSpan? flushInterval = null)
{
    private readonly List<AssessmentResultItem> _buffer = [];
    private readonly List<AssessmentResultItem> _all = [];
    private long _lastFlush = timeProvider.GetTimestamp();

    public int BatchSize { get; } = Math.Max(1, batchSize);
    public TimeSpan FlushInterval { get; } = flushInterval ?? TimeSpan.FromSeconds(5);
    public int RunId { get; } = runId;

    /// <summary>Results written to the database so far.</summary>
    public int Saved { get; private set; }

    /// <summary>Every result added (saved or still buffered).</summary>
    public IReadOnlyList<AssessmentResultItem> All => _all;

    public async Task AddAsync(IEnumerable<AssessmentResultItem> results, CancellationToken cancellationToken)
    {
        foreach (var result in results)
        {
            _buffer.Add(result);
            _all.Add(result);
            if (_buffer.Count >= BatchSize) await FlushAsync(cancellationToken);
        }

        if (_buffer.Count > 0 && timeProvider.GetElapsedTime(_lastFlush) >= FlushInterval) await FlushAsync(cancellationToken);
    }

    /// <summary>Writes the buffered results. Call it once more on completion, failure or cancellation (with a token that is not cancelled).</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        _lastFlush = timeProvider.GetTimestamp();
        if (_buffer.Count == 0) return;
        var chunk = _buffer.ToArray();
        await repository.AppendResultsAsync(RunId, chunk, cancellationToken);
        _buffer.Clear();
        Saved += chunk.Length;
    }
}
