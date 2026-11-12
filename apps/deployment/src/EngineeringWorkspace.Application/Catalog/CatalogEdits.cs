using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EngineeringWorkspace.Application.Catalog;

/// <summary>Where a control or policy set comes from (4.0 beta.2).</summary>
public static class CatalogOrigins
{
    /// <summary>Shipped in the GDS catalogue, unchanged.</summary>
    public const string Gds = "GDS";

    /// <summary>Shipped in the GDS catalogue and changed by a Lead.</summary>
    public const string GdsEdited = "GDS (edited)";

    /// <summary>Created by a Lead.</summary>
    public const string Lead = "Lead";
}

/// <summary>
/// A Lead's change to the catalogue, stored in secureplus.CatalogControlEdit (never in the GDS catalogue tables), so a catalogue reload
/// can never delete or overwrite it. <see cref="BasedOnControlId"/> is set when a GDS control is edited or hidden; <see cref="BaseHash"/> is
/// the hash of the GDS control the edit started from (a different hash later means the GDS version changed: update available).
/// </summary>
public sealed record CatalogControlEdit(
    string ControlId,
    string? BasedOnControlId,
    bool IsHidden,
    CatalogControlContent? Content,
    string? BaseHash,
    int Version,
    string ModifiedBy,
    DateTimeOffset ModifiedAtUtc)
{
    public bool IsLeadControl => BasedOnControlId is null;
}

/// <summary>The editable fields of a control.</summary>
public sealed record CatalogControlContent(
    string Kind,
    string Category,
    string Platform,
    string Name,
    string Setting,
    string Value,
    string Description,
    string Severity,
    string UserImpact,
    IReadOnlyList<string> Frameworks,
    string Evaluation,
    string GraphResource,
    string OdataType,
    string Body,
    string Product = "",
    string FeatureArea = "",
    string DefaultValue = "",
    string AssessMethod = "",
    string DeployMethod = "",
    string SourceName = "",
    string SourceVersion = "",
    string SourceUrl = "")
{
    public static CatalogControlContent From(CatalogControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return new CatalogControlContent(control.Kind, control.Category, control.Platform, control.Name, control.Setting, control.Value, control.Description, control.Severity,
            control.UserImpact, control.Frameworks, control.Evaluation, control.GraphResource, control.OdataType, control.Body, control.Product, control.FeatureArea,
            control.DefaultValue, control.AssessMethod, control.DeployMethod, control.Source, control.SourceVersion, control.SourceUrl);
    }
}

/// <summary>A Lead's policy set, or a Lead's change to a GDS policy set (<see cref="BasedOnSetId"/>), in secureplus.CatalogPolicySetEdit.</summary>
public sealed record CatalogPolicySetEdit(
    string SetId,
    string? BasedOnSetId,
    bool IsHidden,
    string DisplayName,
    string Description,
    int DeploymentOrder,
    IReadOnlyList<string> ControlIds,
    IReadOnlyList<string> Includes,
    string? BaseHash,
    int Version,
    string ModifiedBy,
    DateTimeOffset ModifiedAtUtc)
{
    public bool IsLeadSet => BasedOnSetId is null;
}

/// <summary>One recorded change (append-only history in secureplus.CatalogEditHistory).</summary>
public sealed record CatalogEditHistoryEntry(long Id, string ItemType, string ItemId, string Action, string? BeforeJson, string? AfterJson, string ChangedBy, DateTimeOffset ChangedAtUtc);

public static class CatalogEditActions
{
    public const string Created = "Created";
    public const string Edited = "Edited";
    public const string Hidden = "Hidden";
    public const string Restored = "Restored";
    public const string Deleted = "Deleted";
}

/// <summary>Stores Lead edits (Leads only; enforced by the repository when users are managed).</summary>
public interface ICatalogEditRepository
{
    bool IsAvailable { get; }

    Task<CatalogEdits> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveControlAsync(CatalogControlEdit edit, CancellationToken cancellationToken = default);

    /// <summary>Deletes a Lead control, or removes a Lead's edit of a GDS control (the GDS version shows again).</summary>
    Task DeleteControlAsync(string controlId, CancellationToken cancellationToken = default);

    Task SavePolicySetAsync(CatalogPolicySetEdit edit, CancellationToken cancellationToken = default);

    Task DeletePolicySetAsync(string setId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CatalogEditHistoryEntry>> HistoryAsync(string itemId, CancellationToken cancellationToken = default);
}

/// <summary>Every Lead edit.</summary>
public sealed record CatalogEdits(IReadOnlyList<CatalogControlEdit> Controls, IReadOnlyList<CatalogPolicySetEdit> PolicySets)
{
    public static CatalogEdits None { get; } = new([], []);

    public bool IsEmpty => Controls.Count == 0 && PolicySets.Count == 0;
}

/// <summary>
/// Builds the catalogue the app uses: the GDS catalogue with Lead edits applied. Pure and deterministic (unit-tested).
/// <list type="bullet">
/// <item>An edited GDS control replaces the GDS one (origin "GDS (edited)"); when the GDS version has changed since the edit, it is flagged Update available.</item>
/// <item>A hidden GDS control or set is left out; a Lead control or set is added (origin "Lead").</item>
/// <item>Policy-set members that no longer exist (a GDS control removed by a catalogue update) are reported as retired, never silently dropped.</item>
/// </list>
/// </summary>
public static class CatalogComposer
{
    public static ControlCatalog Compose(ControlCatalog gds, CatalogEdits edits)
    {
        ArgumentNullException.ThrowIfNull(gds);
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.IsEmpty) return gds;

        var controlEdits = edits.Controls.ToDictionary(edit => edit.BasedOnControlId ?? edit.ControlId, StringComparer.OrdinalIgnoreCase);
        var controls = new List<CatalogControl>(gds.Controls.Count + edits.Controls.Count);
        foreach (var control in gds.Controls)
        {
            if (!controlEdits.TryGetValue(control.Id, out var edit) || edit.IsLeadControl)
            {
                controls.Add(control);
                continue;
            }

            if (edit.IsHidden) continue;
            controls.Add(Apply(control, edit.Content!) with
            {
                Origin = CatalogOrigins.GdsEdited,
                IsUpdateAvailable = edit.BaseHash is { Length: > 0 } hash && hash != Hash(control),
                EditVersion = edit.Version, ModifiedBy = edit.ModifiedBy, ModifiedAtUtc = edit.ModifiedAtUtc
            });
        }

        var gdsIds = gds.Controls.Select(control => control.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var edit in edits.Controls.Where(edit => edit.IsLeadControl && !edit.IsHidden && edit.Content is not null))
        {
            if (gdsIds.Contains(edit.ControlId)) continue; // a Lead ID can never shadow a GDS control
            controls.Add(New(edit.ControlId, edit.Content!) with
            {
                Origin = CatalogOrigins.Lead, EditVersion = edit.Version, ModifiedBy = edit.ModifiedBy, ModifiedAtUtc = edit.ModifiedAtUtc
            });
        }

        // Edits whose GDS control no longer exists: kept (and shown retired) so nothing a Lead did disappears.
        foreach (var edit in edits.Controls.Where(edit => !edit.IsLeadControl && !edit.IsHidden && edit.Content is not null && !gdsIds.Contains(edit.BasedOnControlId!)))
            controls.Add(New(edit.BasedOnControlId!, edit.Content!) with
            {
                Origin = CatalogOrigins.GdsEdited, IsRetired = true, EditVersion = edit.Version, ModifiedBy = edit.ModifiedBy, ModifiedAtUtc = edit.ModifiedAtUtc
            });

        var known = controls.Select(control => control.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var setEdits = edits.PolicySets.ToDictionary(edit => edit.BasedOnSetId ?? edit.SetId, StringComparer.OrdinalIgnoreCase);
        var sets = new List<CatalogPolicySet>();
        foreach (var set in gds.PolicySets)
        {
            if (!setEdits.TryGetValue(set.Id, out var edit) || edit.IsLeadSet)
            {
                sets.Add(set with { ControlIds = set.ControlIds.Where(known.Contains).ToArray() });
                continue;
            }

            if (edit.IsHidden) continue;
            sets.Add(Set(set.Id, edit, known) with { Origin = CatalogOrigins.GdsEdited, IsUpdateAvailable = edit.BaseHash is { Length: > 0 } hash && hash != Hash(set) });
        }

        var gdsSets = gds.PolicySets.Select(set => set.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var edit in edits.PolicySets.Where(edit => edit.IsLeadSet && !edit.IsHidden && !gdsSets.Contains(edit.SetId)))
            sets.Add(Set(edit.SetId, edit, known) with { Origin = CatalogOrigins.Lead });

        // Controls list the sets they belong to (the GDS list is rebuilt from the composed sets).
        var membership = sets.SelectMany(set => set.ControlIds.Select(id => (id, set.Id))).GroupBy(pair => pair.id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(pair => pair.Item2).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        controls = controls.Select(control => control with { PolicySets = membership.GetValueOrDefault(control.Id) ?? [] }).ToList();

        var editHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(edits)))).ToLowerInvariant();
        return new ControlCatalog(gds.Source, gds.GeneratedAtUtc, gds.DerivedFieldsNote, sets, controls, gds.Aliases)
        {
            ContentHash = $"{gds.ContentHash}+{editHash[..16]}",
            SeedVersion = gds.SeedVersion,
            SolutionAreas = gds.SolutionAreas,
            ExperienceAreas = gds.ExperienceAreas,
            Specifications = gds.Specifications,
            Baselines = gds.Baselines
        };
    }

    /// <summary>Policy-set members (as stored by the Lead) that are not in the catalogue any more.</summary>
    public static IReadOnlyList<string> RetiredMembers(CatalogPolicySetEdit edit, ControlCatalog composed) =>
        edit.ControlIds.Where(id => composed.Find(id) is null).ToArray();

    /// <summary>Hash of a GDS control's editable content (detects a changed GDS version after a Lead edit).</summary>
    public static string Hash(CatalogControl control) => HashOf(CatalogControlContent.From(control with { Origin = CatalogOrigins.Gds }));

    public static string Hash(CatalogPolicySet set) => HashOf(new { set.DisplayName, set.Description, set.DeploymentOrder, set.ControlIds, set.Includes });

    private static string HashOf(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant()[..32];

    private static CatalogPolicySet Set(string id, CatalogPolicySetEdit edit, HashSet<string> known) =>
        new(id, edit.DisplayName, edit.Description, edit.DeploymentOrder, [], edit.Includes, edit.ControlIds.Where(known.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
        {
            EditVersion = edit.Version, ModifiedBy = edit.ModifiedBy, ModifiedAtUtc = edit.ModifiedAtUtc,
            RetiredMembers = edit.ControlIds.Where(member => !known.Contains(member)).ToArray()
        };

    private static CatalogControl Apply(CatalogControl control, CatalogControlContent content) => control with
    {
        Kind = content.Kind, Category = content.Category, Platform = content.Platform, Name = content.Name, Setting = content.Setting, Value = content.Value,
        Description = content.Description, Severity = content.Severity, UserImpact = content.UserImpact, Frameworks = content.Frameworks, Evaluation = content.Evaluation,
        GraphResource = content.GraphResource, OdataType = content.OdataType, Body = content.Body, Product = content.Product, FeatureArea = content.FeatureArea,
        DefaultValue = content.DefaultValue, AssessMethodOverride = content.AssessMethod, DeployMethodOverride = content.DeployMethod,
        Source = string.IsNullOrWhiteSpace(content.SourceName) ? control.Source : content.SourceName, SourceVersion = content.SourceVersion, SourceUrl = content.SourceUrl
    };

    private static CatalogControl New(string id, CatalogControlContent content) =>
        Apply(new CatalogControl(id, string.Empty, content.Kind, content.Category, content.Platform, content.Name, content.Setting, content.Value, content.Description,
            CatalogOrigins.Lead, content.Frameworks, content.Severity, content.UserImpact, string.Empty, string.Empty, string.Empty, content.GraphResource, content.OdataType,
            string.Empty, content.Evaluation, [], string.Empty, [], new Dictionary<string, string>(), content.Body), content);
}

/// <summary>The catalogue service with Lead edits (4.0 beta.2): <see cref="ICatalogService.Catalog"/> is the GDS catalogue with every edit applied.</summary>
public interface IEditableCatalogService : ICatalogService
{
    /// <summary>The GDS catalogue as shipped, without Lead edits.</summary>
    ControlCatalog GdsCatalog { get; }

    CatalogEdits Edits { get; }

    /// <summary>Reloads the Lead edits from the database (when it becomes ready, after an edit).</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised after the composed catalogue changed.</summary>
    event EventHandler? Changed;
}

public sealed class EditableCatalogService(ControlCatalog gds, ICatalogEditRepository repository, Microsoft.Extensions.Logging.ILogger<EditableCatalogService> logger)
    : IEditableCatalogService
{
    private volatile ControlCatalog _composed = gds;

    public ControlCatalog GdsCatalog { get; } = gds;
    public ControlCatalog Catalog => _composed;
    public CatalogEdits Edits { get; private set; } = CatalogEdits.None;

    public event EventHandler? Changed;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!repository.IsAvailable) return;
        try
        {
            var edits = await repository.LoadAsync(cancellationToken);
            Edits = edits;
            _composed = CatalogComposer.Compose(GdsCatalog, edits);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException or JsonException)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger, exception, "Lead catalogue edits could not be loaded; the GDS catalogue is used");
        }
    }
}
