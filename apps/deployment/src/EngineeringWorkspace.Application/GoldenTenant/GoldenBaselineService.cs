using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Configuration;
using EngineeringWorkspace.Application.Settings;

namespace EngineeringWorkspace.Application.GoldenTenant;

/// <summary>
/// The current Golden Tenant baseline: which catalog policy sets it is made of and which customer (if any) is the
/// reference tenant. Changes are written to GoldenTenant.ini and take effect immediately for scoring and comparison.
/// </summary>
public interface IGoldenBaselineService
{
    IReadOnlyList<string> PolicySets { get; }
    string? ReferenceCustomerId { get; }
    bool Enabled { get; }

    /// <summary>IDs of every catalog control in the baseline.</summary>
    IReadOnlySet<string> ControlIds { get; }

    event EventHandler? Changed;
    Task<OperationResult> SaveAsync(bool enabled, IReadOnlyList<string> policySets, string? referenceCustomerId, CancellationToken cancellationToken = default);
}

public sealed class GoldenBaselineService : IGoldenBaselineService
{
    public const string FileName = "GoldenTenant.ini";
    private readonly ICatalogService _catalog;
    private readonly IConfigurationFileWriter _writer;
    private IReadOnlySet<string>? _controlIds;

    public GoldenBaselineService(GoldenTenantSettings settings, ICatalogService catalog, IConfigurationFileWriter writer)
    {
        _catalog = catalog;
        _writer = writer;
        Enabled = settings.Enabled || settings.BaselineId is null;
        PolicySets = settings.PolicySets.Count > 0 ? settings.PolicySets : ["Golden-Tenant"];
        ReferenceCustomerId = settings.ReferenceCustomerId;
    }

    public IReadOnlyList<string> PolicySets { get; private set; }
    public string? ReferenceCustomerId { get; private set; }
    public bool Enabled { get; private set; }
    public event EventHandler? Changed;

    public IReadOnlySet<string> ControlIds => _controlIds ??= _catalog.Catalog.ControlsOf(PolicySets).Select(control => control.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task<OperationResult> SaveAsync(bool enabled, IReadOnlyList<string> policySets, string? referenceCustomerId, CancellationToken cancellationToken = default)
    {
        if (policySets.Count == 0) return OperationResult.Failure("PolicySetsRequired", "Select at least one policy set for the Golden Tenant baseline.");
        var unknown = policySets.Where(id => _catalog.Catalog.FindPolicySet(id) is null).ToArray();
        if (unknown.Length > 0) return OperationResult.Failure("PolicySetUnknown", $"Unknown policy set(s): {string.Join(", ", unknown)}.");
        var written = await _writer.WriteSectionAsync(FileName, "GoldenTenant", new Dictionary<string, string?>
        {
            ["Enabled"] = enabled ? "true" : "false",
            ["BaselineId"] = policySets.Count == 1 ? policySets[0] : "Custom",
            ["PolicySets"] = string.Join(",", policySets),
            ["ReferenceCustomerId"] = referenceCustomerId ?? string.Empty
        }, cancellationToken);
        if (!written.Succeeded) return OperationResult.Failure(written.Error!);
        Enabled = enabled;
        PolicySets = policySets.ToArray();
        ReferenceCustomerId = string.IsNullOrWhiteSpace(referenceCustomerId) ? null : referenceCustomerId;
        _controlIds = null;
        Changed?.Invoke(this, EventArgs.Empty);
        return OperationResult.Success();
    }
}
