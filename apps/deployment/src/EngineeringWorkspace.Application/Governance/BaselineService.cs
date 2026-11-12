using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Application.GoldenTenant;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Application.Governance;

/// <summary>Resolves profile items (policy sets, controls, frameworks, standards, experiences) to catalog control IDs.</summary>
public static class ProfileResolver
{
    public static IReadOnlySet<string> ControlIds(ControlCatalog catalog, IEnumerable<ProfileItem> items)
    {
        var list = items.ToArray();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list)
        {
            switch (item.ItemType)
            {
                case ProfileItemTypes.PolicySet:
                    ids.UnionWith(catalog.ControlsOf([item.ItemKey]).Select(control => control.Id));
                    break;
                case ProfileItemTypes.Control:
                case ProfileItemTypes.Policy:
                    if (catalog.Find(item.ItemKey) is { } control) ids.Add(control.Id);
                    break;
            }
        }

        // Frameworks, standards and experiences narrow nothing on their own; when a profile has only them, every
        // control mapped to them is in scope.
        if (ids.Count == 0)
        {
            var frameworks = list.Where(item => item.ItemType is ProfileItemTypes.Framework or ProfileItemTypes.ComplianceStandard).Select(item => item.ItemKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var experiences = list.Where(item => item.ItemType == ProfileItemTypes.Experience).Select(item => item.ItemKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (frameworks.Count > 0 || experiences.Count > 0)
                ids.UnionWith(catalog.Controls.Where(control =>
                        (frameworks.Count == 0 || control.FrameworkReferences.Any(reference => frameworks.Contains(reference.FrameworkCode))) &&
                        (experiences.Count == 0 || experiences.Contains(control.ExperienceArea)))
                    .Select(control => control.Id));
        }

        return ids;
    }
}

/// <summary>The baseline configurations and the scoring scope (union of the active baselines).</summary>
public interface IBaselineService : IScoringScope
{
    IReadOnlyList<BaselineRecord> Baselines { get; }
    event EventHandler? Changed;
    void Start();
    Task RefreshAsync(CancellationToken cancellationToken = default);
    Task<BaselineRecord> SaveAsync(BaselineRecord record, CancellationToken cancellationToken = default);
    Task SetActiveAsync(string baselineKey, bool isActive, CancellationToken cancellationToken = default);
    Task DeleteAsync(string baselineKey, CancellationToken cancellationToken = default);
    IReadOnlySet<string> ControlIdsOf(BaselineRecord baseline);
}

public sealed class BaselineService(IBaselineRepository repository, ICatalogService catalog, IGoldenBaselineService golden, Auditing.AuditContext auditContext,
    Database.IDatabaseStatusService database, ILogger<BaselineService> logger) : IBaselineService
{
    private IReadOnlyList<BaselineRecord> _baselines = [];
    private IReadOnlySet<string>? _scope;

    private int _started;

    public IReadOnlyList<BaselineRecord> Baselines => _baselines;
    public event EventHandler? Changed;

    /// <summary>Loads the baselines now and whenever the database becomes ready (after setup, the seed or a repair).</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        database.StatusChanged += (_, report) =>
        {
            if (report.State == Database.DatabaseOverallState.Ready) _ = RefreshSafeAsync();
        };
        _ = RefreshSafeAsync();
    }

    private async Task RefreshSafeAsync()
    {
        try
        {
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning(exception, "Baselines could not be read; scoring uses the Golden Tenant until they can");
        }
    }

    /// <summary>
    /// Union of the active baselines' controls. Before the database is ready (or when no baseline is active) the
    /// Golden Tenant policy sets are used, as in earlier versions.
    /// </summary>
    public IReadOnlySet<string> ControlIds => _scope ?? golden.ControlIds;

    public IReadOnlySet<string> ControlIdsOf(BaselineRecord baseline) => ProfileResolver.ControlIds(catalog.Catalog, baseline.Items);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!repository.IsAvailable)
        {
            _baselines = [];
            _scope = null;
            return;
        }

        _baselines = await repository.ListBaselinesAsync(cancellationToken).ConfigureAwait(false);
        var active = _baselines.Where(baseline => baseline.IsActive).ToArray();
        _scope = active.Length == 0 ? null : active.SelectMany(ControlIdsOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<BaselineRecord> SaveAsync(BaselineRecord record, CancellationToken cancellationToken = default)
    {
        var saved = await repository.SaveBaselineAsync(record, auditContext.Actor, cancellationToken);
        await RefreshAsync(cancellationToken);
        return saved;
    }

    public async Task SetActiveAsync(string baselineKey, bool isActive, CancellationToken cancellationToken = default)
    {
        await repository.SetBaselineActiveAsync(baselineKey, isActive, auditContext.Actor, cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    public async Task DeleteAsync(string baselineKey, CancellationToken cancellationToken = default)
    {
        await repository.DeleteBaselineAsync(baselineKey, cancellationToken);
        await RefreshAsync(cancellationToken);
    }
}
