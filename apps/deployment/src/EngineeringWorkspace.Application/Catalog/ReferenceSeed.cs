namespace EngineeringWorkspace.Application.Catalog;

/// <summary>Stable names of the seed datasets recorded in secureplus.SeedVersion.</summary>
public static class SeedDatasets
{
    public const string Catalog = "catalog";
    public const string ReferenceValues = "reference-values";
    public const string ControlDetails = "control-details";
    public const string ControlAliases = "control-aliases";
    public const string Specifications = "specifications";
    public const string Baselines = "baselines";
    public const string EaasReference = "eaas-reference";

    public static IReadOnlyList<string> All { get; } = [Catalog, EaasReference, ReferenceValues, ControlDetails, ControlAliases, Specifications, Baselines];
}

/// <summary>Item types used by specifications, baselines and change requests.</summary>
public static class ProfileItemTypes
{
    public const string Experience = "Experience";
    public const string Framework = "Framework";
    public const string ComplianceStandard = "ComplianceStandard";
    public const string Requirement = "Requirement";
    public const string PolicySet = "PolicySet";
    public const string Control = "Control";
    public const string Policy = "Policy";

    public static IReadOnlyList<string> All { get; } = [Experience, Framework, ComplianceStandard, Requirement, PolicySet, Control, Policy];
}

/// <summary>What one seed run did to one dataset.</summary>
public sealed record SeedDatasetResult(string Dataset, string Version, bool Skipped, int Inserted, int Updated, int Deleted, int Protected, int Total)
{
    public string Describe() => Skipped
        ? $"{Dataset}: current (version {Version}, {Total:N0} rows)"
        : $"{Dataset}: {Inserted:N0} added, {Updated:N0} updated, {Deleted:N0} removed{(Protected > 0 ? $", {Protected:N0} kept (changed by users)" : string.Empty)} – {Total:N0} rows";
}

/// <summary>The outcome of one seed run.</summary>
public sealed record SeedSummary(IReadOnlyList<SeedDatasetResult> Datasets, TimeSpan Duration)
{
    public bool AnyChanges => Datasets.Any(dataset => !dataset.Skipped);

    public string Describe() => string.Join("; ", Datasets.Select(dataset => dataset.Describe())) + $" ({Duration.TotalSeconds:0.#}s).";
}

/// <summary>A row of secureplus.SeedVersion.</summary>
public sealed record SeedVersionInfo(string Dataset, string Version, string ContentHash, int Rows, DateTimeOffset AppliedAtUtc, string AppliedBy, string? Summary);

/// <summary>
/// Writes the reference data shipped with the application (solution and experience areas, control details, aliases,
/// standard specifications and system baselines) into the secureplus extension tables. Idempotent and versioned:
/// each dataset is skipped when secureplus.SeedVersion already records its version and content hash; rows are
/// upserted by their stable keys; rows changed by users (IsModified) and customer-specific rows are never overwritten.
/// </summary>
public interface IReferenceSeedStore
{
    bool IsAvailable { get; }

    /// <summary>True when every dataset of <paramref name="catalog"/> is recorded with its current hash.</summary>
    Task<bool> IsCurrentAsync(ControlCatalog catalog, CancellationToken cancellationToken = default);

    Task<SeedSummary> SeedAsync(ControlCatalog catalog, bool force, IProgress<(double Progress, string Activity)>? progress, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SeedVersionInfo>> ListVersionsAsync(CancellationToken cancellationToken = default);
}
