using System.Text.Json;

namespace EngineeringWorkspace.Application.Catalog;

/// <summary>Kinds of catalog entries.</summary>
public static class CatalogKinds
{
    public const string ConditionalAccess = "ConditionalAccess";
    public const string EndpointSecurity = "EndpointSecurity";
    public const string Intune = "Intune";
    public const string AssessmentCheck = "AssessmentCheck";
    public const string Standard = "Standard";
    public const string MicrosoftBaseline = "MicrosoftBaseline";

    /// <summary>A Microsoft 365 / Azure tenant setting (4.0 beta.2): assessed by a rule over Graph, DNS or read-only PowerShell data, or manually.</summary>
    public const string TenantSetting = "TenantSetting";

    public static string DisplayName(string kind) => kind switch
    {
        ConditionalAccess => "Conditional Access",
        EndpointSecurity => "Endpoint Security",
        Intune => "Intune",
        AssessmentCheck => "Assessment check",
        Standard => "Secure Plus standard",
        MicrosoftBaseline => "Microsoft security baseline",
        TenantSetting => "Tenant setting",
        _ => kind
    };
}

/// <summary>How the assessment engine evaluates an entry.</summary>
public static class CatalogEvaluations
{
    public const string ConditionalAccess = "ConditionalAccess";
    public const string SettingsCatalog = "SettingsCatalog";
    public const string GraphObject = "GraphObject";
    public const string Script = "Script";
    public const string TenantCheck = "TenantCheck";
    public const string TenantSetting = "TenantSetting";
    public const string Manual = "Manual";

    public static bool IsAutomatic(string evaluation) => evaluation != Manual;
}

/// <summary>One expected setting of a control: a settings catalog definition ID or a Graph property, and its value.</summary>
public sealed record CatalogExpectation(string Path, JsonElement Value);

/// <summary>
/// One control of the Secure Plus catalog: a Conditional Access policy, Endpoint Security or Intune setting (with its
/// Microsoft Graph body), an assessment check, a Secure Plus standard or a Microsoft security baseline setting.
/// </summary>
public sealed record CatalogControl(
    string Id,
    string Number,
    string Kind,
    string Category,
    string Platform,
    string Name,
    string Setting,
    string Value,
    string Description,
    string Source,
    IReadOnlyList<string> Frameworks,
    string Severity,
    string UserImpact,
    string Experience,
    string PainPoints,
    string Licensing,
    string GraphResource,
    string OdataType,
    string TemplateFamily,
    string Evaluation,
    IReadOnlyList<CatalogExpectation> Expected,
    string Path,
    IReadOnlyList<string> PolicySets,
    IReadOnlyDictionary<string, string> Attributes,
    string Body)
{
    public string KindDisplayName => CatalogKinds.DisplayName(Kind);
    public bool IsAutomatic => CatalogEvaluations.IsAutomatic(Evaluation);

    /// <summary>Solution area code (see <see cref="ControlCatalog.SolutionAreas"/>).</summary>
    public string SolutionArea { get; init; } = string.Empty;

    /// <summary>Experience area code (see <see cref="ControlCatalog.ExperienceAreas"/>).</summary>
    public string ExperienceArea { get; init; } = string.Empty;

    /// <summary>For an alternative of the same policy number: the base control ID; otherwise empty.</summary>
    public string VariantOf { get; init; } = string.Empty;

    /// <summary>For an alternative: what distinguishes it (its configured value).</summary>
    public string VariantLabel { get; init; } = string.Empty;

    public bool IsVariant => VariantOf.Length > 0;

    public ControlImplementation Implementation { get; init; } = ControlImplementation.Empty;

    public ControlRemediation Remediation { get; init; } = ControlRemediation.Empty;

    /// <summary>Fields produced by GDS rules rather than taken from a source document ("Generated – needs validation").</summary>
    public IReadOnlyList<string> Generated { get; init; } = [];

    public bool IsGenerated(string field) => Generated.Contains(field, StringComparer.OrdinalIgnoreCase);

    /// <summary>Compliance standards among the framework references (ISO 27001, PCI DSS, HIPAA, CMMC...).</summary>
    public IEnumerable<FrameworkReference> FrameworkReferences => Frameworks.Where(label => !string.IsNullOrWhiteSpace(label)).Select(FrameworkReference.Parse);

    // ---- 4.0 beta.2: origin, Lead edits and the extra control fields ------------------------------------------------

    /// <summary><see cref="CatalogOrigins"/>: GDS, GDS (edited) or Lead.</summary>
    public string Origin { get; init; } = CatalogOrigins.Gds;

    public bool IsLeadEdited => Origin != CatalogOrigins.Gds;

    /// <summary>A Lead edited this GDS control and the GDS version has changed since (review and apply or ignore in 4.2).</summary>
    public bool IsUpdateAvailable { get; init; }

    /// <summary>A Lead's edit of a GDS control that is no longer in the GDS catalogue (kept, never deleted).</summary>
    public bool IsRetired { get; init; }

    public int EditVersion { get; init; }
    public string? ModifiedBy { get; init; }
    public DateTimeOffset? ModifiedAtUtc { get; init; }

    /// <summary>Microsoft product (Entra ID, Intune, Exchange Online...) and feature area, as in docs/catalog/COVERAGE.md.</summary>
    public string Product { get; init; } = string.Empty;
    public string FeatureArea { get; init; } = string.Empty;

    /// <summary>Microsoft's default value of the setting (the recommended value is <see cref="Value"/>).</summary>
    public string DefaultValue { get; init; } = string.Empty;

    public string SourceVersion { get; init; } = string.Empty;
    public string SourceUrl { get; init; } = string.Empty;
    public string AssessMethodOverride { get; init; } = string.Empty;
    public string DeployMethodOverride { get; init; } = string.Empty;

    /// <summary>How the control is assessed: Graph, DNS, PowerShell, ARM or Manual.</summary>
    public string AssessMethod => AssessMethodOverride.Length > 0 ? AssessMethodOverride : IsAutomatic ? "Graph" : "Manual";

    /// <summary>Assessed like a check and scored with the checks (assessment checks and tenant settings).</summary>
    public bool IsScoredWithChecks => Kind is CatalogKinds.AssessmentCheck or CatalogKinds.TenantSetting;

    /// <summary>How the control is deployed: Graph, PowerShell, Azure Policy, ARM, GPO, Manual or None.</summary>
    public string DeployMethod => DeployMethodOverride.Length > 0 ? DeployMethodOverride : Kind switch
    {
        CatalogKinds.ConditionalAccess or CatalogKinds.EndpointSecurity or CatalogKinds.Intune => "Graph",
        CatalogKinds.MicrosoftBaseline => "GPO",
        CatalogKinds.AssessmentCheck => "None",
        CatalogKinds.TenantSetting => "Manual",
        _ => "Manual"
    };
}

/// <summary>Implementation details of a control (seeded; generated by GDS rules unless stated otherwise).</summary>
public sealed record ControlImplementation(int EstimatedMinutes, string Difficulty, string Effort, string RiskLevel, string Benefits, string ExperienceImpact)
{
    public static ControlImplementation Empty { get; } = new(0, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

    public string EstimatedTimeText => EstimatedMinutes switch
    {
        <= 0 => "Not estimated",
        < 60 => $"{EstimatedMinutes} minutes",
        60 => "1 hour",
        _ when EstimatedMinutes % 60 == 0 => $"{EstimatedMinutes / 60} hours",
        _ => $"{EstimatedMinutes / 60.0:0.#} hours"
    };
}

/// <summary>Remediation content of a control. Empty strings mean "not applicable".</summary>
public sealed record ControlRemediation(
    string GraphRequest,
    string GraphCommand,
    string PowerShell,
    string JsonBody,
    string SettingPath,
    string GpoPath,
    string Registry,
    string Steps,
    string DeploymentMethod,
    string Validation,
    string Rollback,
    string Evidence)
{
    public static ControlRemediation Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
        string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

    public bool HasAny => new[] { GraphRequest, GraphCommand, PowerShell, JsonBody, SettingPath, GpoPath, Registry, Steps, DeploymentMethod, Validation, Rollback, Evidence }
        .Any(value => value.Length > 0);
}

/// <summary>A named reference value (solution area or experience area).</summary>
public sealed record CatalogReferenceValue(string Code, string Name, string Description);

/// <summary>A specification shipped with the application (not assigned to any customer).</summary>
public sealed record CatalogSpecification(string Code, string Name, string Description, IReadOnlyList<string> PolicySets, IReadOnlyList<string> ControlIds,
    IReadOnlyList<string> Experiences, IReadOnlyList<string> Frameworks);

/// <summary>A baseline shipped with the application: policy sets plus individual controls.</summary>
public sealed record CatalogBaseline(string Code, string Name, string Description, bool IsActive, IReadOnlyList<string> PolicySets, IReadOnlyList<string> ControlIds);

/// <summary>An old control ID that now resolves to another control (identical duplicates merged).</summary>
public sealed record CatalogAlias(string Alias, string ControlId, string SourcePath, string Reason);

/// <summary>A baseline: a named, ordered composition of controls (and of other policy sets).</summary>
public sealed record CatalogPolicySet(
    string Id,
    string DisplayName,
    string Description,
    int DeploymentOrder,
    IReadOnlyList<string> Requires,
    IReadOnlyList<string> Includes,
    IReadOnlyList<string> ControlIds)
{
    /// <summary><see cref="CatalogOrigins"/> (4.0 beta.2).</summary>
    public string Origin { get; init; } = CatalogOrigins.Gds;

    public bool IsUpdateAvailable { get; init; }
    public int EditVersion { get; init; }
    public string? ModifiedBy { get; init; }
    public DateTimeOffset? ModifiedAtUtc { get; init; }

    /// <summary>Members a Lead added that are no longer in the catalogue (shown, never silently dropped).</summary>
    public IReadOnlyList<string> RetiredMembers { get; init; } = [];
}

/// <summary>The complete catalog shipped with the application.</summary>
public sealed class ControlCatalog
{
    private readonly Dictionary<string, CatalogControl> _byId;
    private readonly Dictionary<string, string> _aliases;

    public ControlCatalog(string source, string generatedAtUtc, string derivedFieldsNote, IReadOnlyList<CatalogPolicySet> policySets, IReadOnlyList<CatalogControl> controls,
        IReadOnlyList<CatalogAlias>? aliases = null)
    {
        Aliases = aliases ?? [];
        _aliases = Aliases.ToDictionary(alias => alias.Alias, alias => alias.ControlId, StringComparer.OrdinalIgnoreCase);
        Source = source;
        GeneratedAtUtc = generatedAtUtc;
        DerivedFieldsNote = derivedFieldsNote;
        PolicySets = policySets;
        Controls = controls;
        _byId = controls.ToDictionary(control => control.Id, StringComparer.OrdinalIgnoreCase);
    }

    public string Source { get; }
    public string GeneratedAtUtc { get; }
    public string DerivedFieldsNote { get; }

    /// <summary>Version label of the catalog (its generation time).</summary>
    public string Version => GeneratedAtUtc.Length == 0 ? "unversioned" : GeneratedAtUtc;

    /// <summary>SHA-256 (hex) of the catalog content; the database copy is reloaded when it changes.</summary>
    public string ContentHash { get; init; } = string.Empty;
    public IReadOnlyList<CatalogPolicySet> PolicySets { get; }
    public IReadOnlyList<CatalogControl> Controls { get; }

    /// <summary>Seed content version (for the database seed-version table); "1" for catalogs without one.</summary>
    public string SeedVersion { get; init; } = "1";

    public IReadOnlyList<CatalogReferenceValue> SolutionAreas { get; init; } = [];
    public IReadOnlyList<CatalogReferenceValue> ExperienceAreas { get; init; } = [];
    public IReadOnlyList<CatalogSpecification> Specifications { get; init; } = [];
    public IReadOnlyList<CatalogBaseline> Baselines { get; init; } = [];
    public IReadOnlyList<CatalogAlias> Aliases { get; }

    /// <summary>Finds a control by ID or by an alias of a merged duplicate.</summary>
    public CatalogControl? Find(string id) => _byId.GetValueOrDefault(id) ?? (_aliases.TryGetValue(id, out var target) ? _byId.GetValueOrDefault(target) : null);

    /// <summary>The current ID for an ID or alias (unknown IDs are returned unchanged).</summary>
    public string ResolveId(string id) => _aliases.TryGetValue(id, out var target) ? target : id;

    public string SolutionAreaName(string code) => SolutionAreas.FirstOrDefault(area => area.Code == code)?.Name ?? (code.Length == 0 ? "Unassigned" : code);

    public string ExperienceAreaName(string code) => ExperienceAreas.FirstOrDefault(area => area.Code == code)?.Name ?? (code.Length == 0 ? "Unassigned" : code);

    public CatalogPolicySet? FindPolicySet(string id) => PolicySets.FirstOrDefault(set => string.Equals(set.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The controls of the given policy sets (each set already includes the sets it composes).</summary>
    public IReadOnlyList<CatalogControl> ControlsOf(IEnumerable<string> policySetIds)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in policySetIds.Select(FindPolicySet).OfType<CatalogPolicySet>())
            ids.UnionWith(set.ControlIds);
        return Controls.Where(control => ids.Contains(control.Id)).ToArray();
    }
}

/// <summary>Provides the catalog embedded in the application (generated from the GDS Documents repository).</summary>
public interface ICatalogService
{
    ControlCatalog Catalog { get; }
}
