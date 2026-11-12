using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Jobs;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Application.Catalog;

/// <summary>What the application database holds of the control catalog.</summary>
public sealed record CatalogDatabaseState(
    bool IsAvailable,
    string? Version,
    string? ContentHash,
    DateTimeOffset? SyncedAtUtc,
    int Controls,
    int PolicySets,
    int Frameworks,
    int Requirements)
{
    public static CatalogDatabaseState Unavailable { get; } = new(false, null, null, null, 0, 0, 0, 0);

    /// <summary>True when the database holds exactly this catalog.</summary>
    public bool IsCurrent(ControlCatalog catalog) =>
        IsAvailable && Controls == catalog.Controls.Count && string.Equals(ContentHash, catalog.ContentHash, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Row counts written by one catalog synchronization.</summary>
public sealed record CatalogSyncSummary(
    bool Skipped,
    string Version,
    int Controls,
    int PolicyTypes,
    int Categories,
    int Settings,
    int PolicySets,
    int PolicySetMembers,
    int Frameworks,
    int Requirements,
    int Mappings,
    int Standards,
    int StandardItems,
    TimeSpan Duration)
{
    public string Describe() => Skipped
        ? $"The database already holds catalog {Version}."
        : $"{Controls:N0} controls, {Settings:N0} expected settings, {PolicySets} policy sets ({PolicySetMembers:N0} members), {Frameworks} frameworks with " +
          $"{Requirements} requirements ({Mappings:N0} mappings), {StandardItems:N0} standard items in {Duration.TotalSeconds:0.#}s.";
}

/// <summary>
/// Writes the embedded control catalog into the application database: the EaaS v3 pol (repository, policy types,
/// categories, artifacts, settings, policy sets, framework mappings), fw (frameworks, versions, requirements) and gds
/// (standards and standard items) tables, plus the flat secureplus.CatalogControl table.
/// </summary>
public interface ICatalogDatabaseStore
{
    bool IsAvailable { get; }

    Task<CatalogDatabaseState> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads the catalog unless the database already holds it (<paramref name="force"/> reloads it anyway). Idempotent.</summary>
    Task<CatalogSyncSummary> SyncAsync(ControlCatalog catalog, bool force, IProgress<(double Progress, string Activity)>? progress, CancellationToken cancellationToken = default);
}

/// <summary>A framework reference of a control ("NIST 800-53 AC-2(7)") split into framework, version and requirement.</summary>
public sealed record FrameworkReference(string FrameworkCode, string FrameworkName, string Publisher, string Version, string RequirementCode, string Label)
{
    /// <summary>Framework codes that are compliance standards (certifiable or regulatory) rather than control frameworks.</summary>
    public static IReadOnlySet<string> ComplianceStandardCodes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ISO-27001", "PCI-DSS", "HIPAA", "CMMC" };

    /// <summary>"Compliance standard" or "Framework".</summary>
    public bool IsComplianceStandard => ComplianceStandardCodes.Contains(FrameworkCode);

    private static readonly (string Prefix, string Code, string Name, string Publisher, string Version)[] Known =
    [
        ("NIST 800-53", "NIST-800-53", "NIST SP 800-53 Security and Privacy Controls", "NIST", "Rev 5"),
        ("NIST CSF", "NIST-CSF", "NIST Cybersecurity Framework", "NIST", "2.0"),
        ("CIS v8.1", "CIS-CONTROLS", "CIS Critical Security Controls", "Center for Internet Security", "v8.1"),
        ("CIS v8", "CIS-CONTROLS", "CIS Critical Security Controls", "Center for Internet Security", "v8"),
        ("ISO 27001:2022", "ISO-27001", "ISO/IEC 27001 Information security management", "ISO/IEC", "2022"),
        ("CMMC 2.0", "CMMC", "Cybersecurity Maturity Model Certification", "US DoD", "2.0"),
        ("PCI DSS 4.0.1", "PCI-DSS", "PCI Data Security Standard", "PCI Security Standards Council", "4.0.1"),
        ("PCI DSS 4.0", "PCI-DSS", "PCI Data Security Standard", "PCI Security Standards Council", "4.0"),
        ("HIPAA", "HIPAA", "HIPAA Security Rule", "US HHS", "45 CFR 164"),
        ("CIS M365", "CIS-M365", "CIS Microsoft 365 Foundations Benchmark", "Center for Internet Security", "Current"),
        ("GLBA", "GLBA", "GLBA / FTC Safeguards Rule", "US FTC", "16 CFR Part 314"),
        ("MITRE ATT&CK", "MITRE-ATTACK", "MITRE ATT&CK Mitigations", "MITRE", "Enterprise"),
        ("Microsoft Security Baseline", "MS-SECURITY-BASELINE", "Microsoft Security Baselines", "Microsoft", "Current"),
        ("Essential Eight", "ESSENTIAL-EIGHT", "ASD Essential Eight", "Australian Signals Directorate", "Current")
    ];

    public static FrameworkReference Parse(string label)
    {
        var text = label.Trim();
        foreach (var known in Known)
        {
            if (!text.StartsWith(known.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = text[known.Prefix.Length..].Trim();
            if (rest.Length > 0 && !char.IsWhiteSpace(text[known.Prefix.Length])) continue;
            return new FrameworkReference(known.Code, known.Name, known.Publisher, known.Version, rest.Length == 0 ? "General" : rest, text);
        }

        var split = text.LastIndexOf(' ');
        var framework = split > 0 ? text[..split] : text;
        var requirement = split > 0 ? text[(split + 1)..] : "General";
        var code = new string(framework.ToUpperInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        return new FrameworkReference(code.Length == 0 ? "OTHER" : code, framework, string.Empty, "Current", requirement, text);
    }
}

/// <summary>Keeps the database copy of the catalog current; runs as a background job shown in the activity panel.</summary>
public interface ICatalogSyncService
{
    /// <summary>Queues a synchronization job (a forced one reloads even when the database is current).</summary>
    JobSnapshot? Queue(bool force);

    /// <summary>Queues a synchronization when the database is ready and does not hold the embedded catalog.</summary>
    Task<bool> EnsureCurrentAsync(CancellationToken cancellationToken = default);

    Task<CatalogDatabaseState> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts watching the database status: when it becomes ready (setup, upgrade, repair) the catalog is synchronized.</summary>
    void Start();

    /// <summary>
    /// Loads the catalog and the reference seed now and waits for it (used by setup to show progress and a summary).
    /// Returns the summary text; throws when the database is not ready or the seed fails.
    /// </summary>
    Task<string> SeedNowAsync(bool force, IProgress<(double Progress, string Activity)>? progress, CancellationToken cancellationToken = default);
}

public sealed class CatalogSyncService(
    ICatalogService catalog,
    ICatalogDatabaseStore store,
    IReferenceSeedStore seeds,
    IDatabaseStatusService database,
    IJobService jobs,
    ILogger<CatalogSyncService> logger) : ICatalogSyncService
{
    public const string JobCategory = "Catalog synchronization";
    private static readonly string[] Steps = ["Check the database", "Write policies and settings", "Write policy sets", "Write frameworks and mappings", "Write standards",
        "Write reference data"];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _started;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        database.StatusChanged += (_, report) =>
        {
            if (report.State == DatabaseOverallState.Ready) _ = EnsureCurrentSafeAsync();
        };
    }

    public Task<CatalogDatabaseState> GetStateAsync(CancellationToken cancellationToken = default) =>
        store.IsAvailable ? store.GetStateAsync(cancellationToken) : Task.FromResult(CatalogDatabaseState.Unavailable);

    public async Task<bool> EnsureCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (!store.IsAvailable || IsRunning) return false;
        var state = await store.GetStateAsync(cancellationToken);
        if (state.IsCurrent(catalog.Catalog) && (!seeds.IsAvailable || await seeds.IsCurrentAsync(catalog.Catalog, cancellationToken))) return false;
        return Queue(false) is not null;
    }

    public JobSnapshot? Queue(bool force)
    {
        if (IsRunning) return null;
        return jobs.Enqueue(force ? "Reload the catalog into the database" : "Load the catalog into the database", JobCategory, catalog.Catalog.Version, Steps,
            context => RunAsync(context, force));
    }

    private bool IsRunning => jobs.Jobs.Any(job => job.Category == JobCategory && !job.IsFinished);

    private async Task EnsureCurrentSafeAsync()
    {
        try
        {
            // Lead edits (4.0 beta.2) are part of the catalogue the database copy reflects.
            if (catalog is IEditableCatalogService editable) await editable.RefreshAsync();
            await EnsureCurrentAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning(exception, "The catalog database state could not be checked");
        }
    }

    public async Task<string> SeedNowAsync(bool force, IProgress<(double Progress, string Activity)>? progress, CancellationToken cancellationToken = default)
    {
        if (!store.IsAvailable) throw new InvalidOperationException("The application database is not ready.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = catalog.Catalog;
            var summary = await store.SyncAsync(current, force, new Progress<(double Progress, string Activity)>(update => progress?.Report((update.Progress * 0.8, update.Activity))),
                cancellationToken);
            var seeded = await seeds.SeedAsync(current, force, new Progress<(double Progress, string Activity)>(update => progress?.Report((80 + update.Progress * 0.2, update.Activity))),
                cancellationToken);
            var text = $"Catalog: {summary.Describe()} Reference data: {seeded.Describe()}";
            logger.LogInformation("Seed {Version} applied: {Summary}", current.SeedVersion, text);
            progress?.Report((100, "Seed data imported."));
            return text;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> RunAsync(IJobContext context, bool force)
    {
        await _gate.WaitAsync(context.CancellationToken);
        try
        {
            return await RunCoreAsync(context, force);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> RunCoreAsync(IJobContext context, bool force)
    {
        var current = catalog.Catalog;
        context.BeginStep("Check the database", $"Catalog {current.Version}, {current.Controls.Count:N0} controls.");
        if (!store.IsAvailable) throw new InvalidOperationException("The application database is not ready. Install it in Settings > Database Configuration.");
        var step = 0;
        var progress = new InlineProgress<(double Progress, string Activity)>(update =>
        {
            var target = update.Progress switch { < 55 => 1, < 70 => 2, < 90 => 3, _ => 4 };
            while (step < target)
            {
                step++;
                context.BeginStep(Steps[step]);
            }

            context.Report(update.Progress, update.Activity);
        });
        var summary = await store.SyncAsync(current, force, progress, context.CancellationToken);
        if (summary.Skipped)
        {
            context.CompleteStep("Already current.");
            for (var index = step + 1; index < Steps.Length - 1; index++) context.SkipStep(Steps[index], "The database already holds this catalog.");
        }
        else
        {
            context.CompleteStep();
        }

        logger.LogInformation("Catalog {Version} synchronized to the database: {Summary}", summary.Version, summary.Describe());
        context.BeginStep("Write reference data", $"Seed version {current.SeedVersion}.");
        var seeded = await seeds.SeedAsync(current, force, null, context.CancellationToken);
        context.CompleteStep(seeded.AnyChanges ? seeded.Describe() : "Already current.");
        logger.LogInformation("Reference seed {Version}: {Summary}", current.SeedVersion, seeded.Describe());
        return $"{summary.Describe()} {(seeded.AnyChanges ? "Reference data updated." : "Reference data current.")}";
    }

    /// <summary>Reports synchronously, in order, on the job's thread (Progress&lt;T&gt; posts asynchronously).</summary>
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        private readonly Lock _gate = new();

        public void Report(T value)
        {
            lock (_gate) handler(value);
        }
    }
}
