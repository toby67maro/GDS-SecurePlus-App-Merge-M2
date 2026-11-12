using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Jobs;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Application.KnowledgeBase;

/// <summary>The knowledge base shipped with the application and the copy loaded in the database.</summary>
public sealed record KnowledgeBaseState(string EmbeddedVersion, string EmbeddedHash, string? LoadedVersion, string? LoadedHash, DateTimeOffset? LoadedAtUtc, string? Summary)
{
    public bool IsCurrent => LoadedHash == EmbeddedHash;
}

/// <summary>What one load wrote (rows per table) or that it was skipped because the database is current.</summary>
public sealed record KnowledgeBaseLoadResult(bool Skipped, string Version, IReadOnlyDictionary<string, int> Rows, TimeSpan Duration)
{
    public int TotalRows => Rows.Values.Sum();

    public string Describe() => Skipped
        ? $"Knowledge base {Version} is current."
        : $"Knowledge base {Version}: {TotalRows:N0} rows in {Rows.Count} tables ({Duration.TotalSeconds:0}s) – " +
          string.Join(", ", Rows.Where(pair => pair.Value > 0).OrderByDescending(pair => pair.Value).Take(6).Select(pair => $"{pair.Key} {pair.Value:N0}")) + ".";
}

/// <summary>
/// Loads the knowledge base (phase 1: SCF 2026.3, NIST SP 800-53 / 800-53A Rev 5, MITRE ATT&amp;CK Enterprise) into the
/// EaaS v3 tables fw.*, threat.* and ref.*. Versioned and idempotent: skipped when the database holds the same content.
/// </summary>
public interface IKnowledgeBaseStore
{
    bool IsAvailable { get; }
    Task<KnowledgeBaseState> GetStateAsync(CancellationToken cancellationToken = default);
    Task<KnowledgeBaseLoadResult> LoadAsync(bool force, IProgress<(double Progress, string Activity)>? progress, CancellationToken cancellationToken = default);
}

/// <summary>
/// The knowledge base in parts loaded one after the other (phase 1: SCF, NIST, MITRE ATT&amp;CK; phase 2: baselines, WBS,
/// question bank, deployment method matrix). Current when every part is current; a load skips the parts that are current
/// unless forced. Each part records its own version, so changing one part does not reload the others.
/// </summary>
public sealed class CompositeKnowledgeBaseStore(IReadOnlyList<IKnowledgeBaseStore> parts) : IKnowledgeBaseStore
{
    public IReadOnlyList<IKnowledgeBaseStore> Parts => parts;
    public bool IsAvailable => parts.All(part => part.IsAvailable);

    public async Task<KnowledgeBaseState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var states = new List<KnowledgeBaseState>();
        foreach (var part in parts) states.Add(await part.GetStateAsync(cancellationToken));
        return Combine(states);
    }

    public static KnowledgeBaseState Combine(IReadOnlyList<KnowledgeBaseState> states) => new(
        string.Join(" + ", states.Select(state => state.EmbeddedVersion)),
        string.Join("|", states.Select(state => state.EmbeddedHash)),
        states.All(state => state.LoadedVersion is null) ? null : string.Join(" + ", states.Select(state => state.LoadedVersion ?? "not loaded")),
        states.All(state => state.LoadedHash is null) ? null : string.Join("|", states.Select(state => state.LoadedHash ?? string.Empty)),
        states.Any(state => state.LoadedAtUtc is null) ? null : states.Max(state => state.LoadedAtUtc),
        string.Join(" ", states.Select(state => state.Summary).Where(summary => !string.IsNullOrWhiteSpace(summary))) is { Length: > 0 } text ? text : null);

    public async Task<KnowledgeBaseLoadResult> LoadAsync(bool force, IProgress<(double Progress, string Activity)>? progress, CancellationToken cancellationToken = default)
    {
        var rows = new Dictionary<string, int>(StringComparer.Ordinal);
        var versions = new List<string>();
        var skipped = true;
        var duration = TimeSpan.Zero;
        for (var index = 0; index < parts.Count; index++)
        {
            // Phase 1 is the larger load: it gets the first 65% of the progress bar.
            var (from, to) = parts.Count == 1 ? (0d, 100d) : index == 0 ? (0d, 65d) : (65d + 35d * (index - 1) / (parts.Count - 1), 65d + 35d * index / (parts.Count - 1));
            var scaled = progress is null ? null : new ScaledProgress(progress, from, to);
            var result = await parts[index].LoadAsync(force, scaled, cancellationToken);
            skipped &= result.Skipped;
            duration += result.Duration;
            versions.Add(result.Version);
            foreach (var (table, count) in result.Rows) rows[table] = rows.GetValueOrDefault(table) + count;
        }

        return new KnowledgeBaseLoadResult(skipped, string.Join(" + ", versions), rows, duration);
    }

    private sealed class ScaledProgress(IProgress<(double Progress, string Activity)> inner, double from, double to) : IProgress<(double Progress, string Activity)>
    {
        public void Report((double Progress, string Activity) value) => inner.Report((from + (to - from) * value.Progress / 100d, value.Activity));
    }
}

public interface IKnowledgeBaseService
{
    /// <summary>Watches the database: when it becomes ready and the knowledge base is not current, a load is queued.</summary>
    void Start();

    JobSnapshot? Queue(bool force);
    Task<KnowledgeBaseState?> GetStateAsync(CancellationToken cancellationToken = default);
}

/// <summary>Runs knowledge-base loads as background jobs (shown in the activity panel), one at a time.</summary>
public sealed class KnowledgeBaseService(IKnowledgeBaseStore store, IDatabaseStatusService database, IJobService jobs, ILogger<KnowledgeBaseService> logger)
    : IKnowledgeBaseService
{
    public const string JobCategory = "Knowledge base";
    private static readonly string[] Steps =
    [
        "Check the database", "Load frameworks and requirements", "Load mappings, objectives and evidence", "Load risks, threats and ATT&CK",
        "Load baselines, the WBS, the question bank, licensing, threat coverage and scoring"
    ];
    private int _started;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        database.StatusChanged += (_, report) =>
        {
            if (report.State == DatabaseOverallState.Ready) _ = EnsureCurrentSafeAsync();
        };
        // The knowledge base links the catalog's framework references to SCF, so it waits for a catalog load to finish.
        jobs.JobChanged += (_, job) =>
        {
            if (job.Category == Catalog.CatalogSyncService.JobCategory && job.State == JobState.Succeeded) _ = EnsureCurrentSafeAsync();
        };
        if (database.LatestStatus?.State == DatabaseOverallState.Ready) _ = EnsureCurrentSafeAsync();
    }

    private bool CatalogLoading => jobs.Jobs.Any(job => job.Category == Catalog.CatalogSyncService.JobCategory && !job.IsFinished);

    public async Task<KnowledgeBaseState?> GetStateAsync(CancellationToken cancellationToken = default) =>
        store.IsAvailable ? await store.GetStateAsync(cancellationToken) : null;

    public JobSnapshot? Queue(bool force)
    {
        if (IsRunning) return null;
        return jobs.Enqueue(force ? "Reload the knowledge base" : "Load the knowledge base", JobCategory,
            "SCF, NIST SP 800-53/53A, MITRE ATT&CK, CIS Benchmarks, Microsoft baselines, GDS WBS and question bank, licensing, CTID, scoring", Steps,
            context => RunAsync(context, force));
    }

    private bool IsRunning => jobs.Jobs.Any(job => job.Category == JobCategory && !job.IsFinished);

    private async Task EnsureCurrentSafeAsync()
    {
        try
        {
            if (!store.IsAvailable || IsRunning || CatalogLoading) return;
            if (!(await store.GetStateAsync()).IsCurrent) Queue(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning(exception, "The knowledge base state could not be checked");
        }
    }

    private async Task<string?> RunAsync(IJobContext context, bool force)
    {
        context.BeginStep(Steps[0]);
        if (!store.IsAvailable) throw new InvalidOperationException("The application database is not ready. Install it in Settings > Database Configuration.");
        var step = 0;
        var progress = new InlineProgress<(double Progress, string Activity)>(update =>
        {
            var target = update.Progress switch { < 23 => 1, < 39 => 2, < 65 => 3, _ => 4 };
            while (step < target)
            {
                step++;
                context.BeginStep(Steps[step]);
            }

            context.Report(update.Progress, update.Activity);
        });
        var result = await store.LoadAsync(force, progress, context.CancellationToken);
        context.CompleteStep(result.Describe());
        logger.LogInformation("{Summary}", result.Describe());
        return result.Describe();
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
