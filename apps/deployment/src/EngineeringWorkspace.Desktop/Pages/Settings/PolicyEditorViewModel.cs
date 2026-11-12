using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Desktop.Navigation;

namespace EngineeringWorkspace.Desktop.Pages.Settings;

/// <summary>A control in the editor list.</summary>
public sealed record PolicyListRow(CatalogControl Control, bool IsHidden = false)
{
    public string Id => Control.Id;
    public string Kind => Control.KindDisplayName;
    public string Name => Control.Name;
    public string Origin => IsHidden ? "GDS, hidden" : Control.IsRetired ? $"{Control.Origin}, retired" : Control.IsUpdateAvailable ? $"{Control.Origin}, update available" : Control.Origin;
    public string Sets => string.Join(", ", Control.PolicySets);
}

/// <summary>A policy set in the editor list.</summary>
public sealed record PolicySetListRow(CatalogPolicySet Set, bool IsHidden = false)
{
    public string Id => Set.Id;
    public string Name => Set.DisplayName;
    public int Members => Set.ControlIds.Count;
    public string Origin => IsHidden ? "GDS, hidden" : Set.IsUpdateAvailable ? $"{Set.Origin}, update available" : Set.Origin;
}

/// <summary>A member of the policy set being edited.</summary>
public sealed record PolicySetMemberRow(string ControlId, string Name, string Kind, bool IsRetired)
{
    public string Display => IsRetired ? $"{ControlId} – no longer in the catalogue (retired)" : $"{ControlId} – {Name} ({Kind})";
}

/// <summary>
/// Settings > Policies and Policy Sets (4.0 beta.2): Leads create, edit, hide and delete policies (controls of any kind) and policy sets.
/// Edits are kept apart from the GDS catalogue (secureplus.CatalogControlEdit / CatalogPolicySetEdit), so a catalogue reload never deletes
/// or overwrites them; every change is in an append-only history. GDS items can be edited or hidden and reverted to the GDS version.
/// </summary>
public sealed partial class PolicyEditorViewModel(IEditableCatalogService catalog, ICatalogEditRepository repository, ICatalogSyncService? sync = null) : PageViewModel
{
    public ObservableCollection<PolicyListRow> Policies { get; } = [];
    public ObservableCollection<PolicySetListRow> Sets { get; } = [];
    public ObservableCollection<PolicySetMemberRow> Members { get; } = [];
    public ObservableCollection<PolicyListRow> MemberCandidates { get; } = [];
    public ObservableCollection<CatalogEditHistoryEntry> History { get; } = [];

    public IReadOnlyList<string> Kinds { get; } =
        [CatalogKinds.ConditionalAccess, CatalogKinds.EndpointSecurity, CatalogKinds.Intune, CatalogKinds.MicrosoftBaseline, CatalogKinds.AssessmentCheck, CatalogKinds.Standard];

    public IReadOnlyList<string> Severities { get; } = ["Critical", "High", "Medium", "Low"];
    public IReadOnlyList<string> AssessMethods { get; } = ["", "Graph", "DNS", "PowerShell", "ARM", "Manual"];
    public IReadOnlyList<string> DeployMethods { get; } = ["", "Graph", "PowerShell", "Azure Policy", "ARM", "GPO", "Manual", "None"];

    private ICollectionView? _policiesView;
    public ICollectionView PoliciesView => _policiesView ??= Filtered(Policies, () => PolicySearch);
    private ICollectionView? _candidatesView;
    public ICollectionView CandidatesView => _candidatesView ??= Filtered(MemberCandidates, () => MemberSearch);

    [ObservableProperty] private string _policySearch = string.Empty;
    [ObservableProperty] private string _memberSearch = string.Empty;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private bool _canEdit;

    // ---- Policy being edited --------------------------------------------------------------------------------------
    [ObservableProperty] private PolicyListRow? _selectedPolicy;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPolicy))] private string _policyId = string.Empty;
    [ObservableProperty] private string _policyOrigin = string.Empty;
    [ObservableProperty] private bool _isNewPolicy;
    [ObservableProperty] private string _kind = CatalogKinds.ConditionalAccess;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _category = string.Empty;
    [ObservableProperty] private string _platform = string.Empty;
    [ObservableProperty] private string _setting = string.Empty;
    [ObservableProperty] private string _value = string.Empty;
    [ObservableProperty] private string _defaultValue = string.Empty;
    [ObservableProperty] private string _policyDescription = string.Empty;
    [ObservableProperty] private string _severity = "Medium";
    [ObservableProperty] private string _userImpact = string.Empty;
    [ObservableProperty] private string _product = string.Empty;
    [ObservableProperty] private string _featureArea = string.Empty;
    [ObservableProperty] private string _assessMethod = string.Empty;
    [ObservableProperty] private string _deployMethod = string.Empty;
    [ObservableProperty] private string _frameworks = string.Empty;
    [ObservableProperty] private string _body = string.Empty;
    [ObservableProperty] private string _sourceName = string.Empty;
    [ObservableProperty] private string _sourceVersion = string.Empty;
    [ObservableProperty] private string _sourceUrl = string.Empty;
    [ObservableProperty] private string? _policyMessage;

    public bool HasPolicy => PolicyId.Length > 0;

    // ---- Policy set being edited ----------------------------------------------------------------------------------
    [ObservableProperty] private PolicySetListRow? _selectedSet;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSet))] private string _setId = string.Empty;
    [ObservableProperty] private string _setOrigin = string.Empty;
    [ObservableProperty] private string _setName = string.Empty;
    [ObservableProperty] private string _setDescription = string.Empty;
    [ObservableProperty] private string _setOrder = "100";
    [ObservableProperty] private PolicySetMemberRow? _selectedMember;
    [ObservableProperty] private PolicyListRow? _selectedCandidate;
    [ObservableProperty] private string? _setMessage;

    public bool HasSet => SetId.Length > 0;

    public override async Task OnNavigatedToAsync(RouteDescriptor route, CancellationToken cancellationToken)
    {
        await base.OnNavigatedToAsync(route, cancellationToken);
        CanEdit = repository.IsAvailable;
        await catalog.RefreshAsync(cancellationToken);
        Load();
    }

    private void Load(string? selectPolicy = null, string? selectSet = null)
    {
        var current = catalog.Catalog;
        Policies.Clear();
        MemberCandidates.Clear();
        foreach (var control in current.Controls.OrderBy(control => control.Kind, StringComparer.Ordinal).ThenBy(control => control.Id, StringComparer.OrdinalIgnoreCase))
        {
            Policies.Add(new PolicyListRow(control));
            MemberCandidates.Add(new PolicyListRow(control));
        }

        var edits = catalog.Edits;
        // Hidden GDS items stay listed so they can be reverted.
        foreach (var hidden in edits.Controls.Where(edit => edit.IsHidden).Select(edit => catalog.GdsCatalog.Find(edit.ControlId)).OfType<CatalogControl>())
            Policies.Add(new PolicyListRow(hidden, IsHidden: true));
        Sets.Clear();
        foreach (var set in current.PolicySets.OrderBy(set => set.DeploymentOrder).ThenBy(set => set.Id, StringComparer.OrdinalIgnoreCase)) Sets.Add(new PolicySetListRow(set));
        foreach (var hidden in edits.PolicySets.Where(edit => edit.IsHidden).Select(edit => catalog.GdsCatalog.FindPolicySet(edit.SetId)).OfType<CatalogPolicySet>())
            Sets.Add(new PolicySetListRow(hidden, IsHidden: true));
        Summary = $"{current.Controls.Count:N0} policies ({current.Controls.Count(control => control.Origin == CatalogOrigins.Lead):N0} by Leads, " +
                  $"{current.Controls.Count(control => control.Origin == CatalogOrigins.GdsEdited):N0} GDS edited, {edits.Controls.Count(edit => edit.IsHidden):N0} GDS hidden; " +
                  $"{current.Controls.Count(control => control.IsUpdateAvailable):N0} with a GDS update available) and {current.PolicySets.Count} policy sets." +
                  (repository.IsAvailable ? string.Empty : " Editing needs the application database.");
        if (selectPolicy is not null) SelectedPolicy = Policies.FirstOrDefault(row => string.Equals(row.Id, selectPolicy, StringComparison.OrdinalIgnoreCase));
        if (selectSet is not null) SelectedSet = Sets.FirstOrDefault(row => string.Equals(row.Id, selectSet, StringComparison.OrdinalIgnoreCase));
    }

    private ICollectionView Filtered<T>(ObservableCollection<T> source, Func<string> search) where T : notnull
    {
        var view = CollectionViewSource.GetDefaultView(source);
        view.Filter = item =>
        {
            var text = search().Trim();
            return text.Length == 0 || item is PolicyListRow row && (row.Id.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                                                                     row.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                                                                     row.Kind.Contains(text, StringComparison.OrdinalIgnoreCase));
        };
        return view;
    }

    partial void OnPolicySearchChanged(string value) => PoliciesView.Refresh();

    partial void OnMemberSearchChanged(string value) => CandidatesView.Refresh();

    partial void OnSelectedPolicyChanged(PolicyListRow? value)
    {
        if (value is null) return;
        var control = value.Control;
        IsNewPolicy = false;
        PolicyId = control.Id;
        PolicyOrigin = value.Origin;
        Kind = control.Kind;
        Name = control.Name;
        Category = control.Category;
        Platform = control.Platform;
        Setting = control.Setting;
        Value = control.Value;
        DefaultValue = control.DefaultValue;
        PolicyDescription = control.Description;
        Severity = control.Severity;
        UserImpact = control.UserImpact;
        Product = control.Product;
        FeatureArea = control.FeatureArea;
        AssessMethod = control.AssessMethodOverride;
        DeployMethod = control.DeployMethodOverride;
        Frameworks = string.Join("; ", control.Frameworks);
        Body = control.Body;
        SourceName = control.Source;
        SourceVersion = control.SourceVersion;
        SourceUrl = control.SourceUrl;
        PolicyMessage = control.IsUpdateAvailable
            ? "GDS has changed this policy since it was edited. Revert to GDS to take the new version, or keep your edit."
            : control.IsRetired ? "GDS removed this policy from the catalogue; your edit is kept." : null;
        _ = LoadHistoryAsync(control.Id);
    }

    private async Task LoadHistoryAsync(string id)
    {
        History.Clear();
        foreach (var entry in await repository.HistoryAsync(id)) History.Add(entry);
    }

    [RelayCommand]
    private void NewPolicy()
    {
        var existing = catalog.Catalog.Controls.Select(control => control.Id).Concat(catalog.Edits.Controls.Select(edit => edit.ControlId))
            .Where(id => id.StartsWith("L-", StringComparison.OrdinalIgnoreCase) && int.TryParse(id[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .Select(id => int.Parse(id[2..], CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();
        SelectedPolicy = null;
        IsNewPolicy = true;
        PolicyId = $"L-{existing + 1:0000}";
        PolicyOrigin = CatalogOrigins.Lead;
        Kind = CatalogKinds.ConditionalAccess;
        Name = Category = Platform = Setting = Value = DefaultValue = PolicyDescription = UserImpact = Product = FeatureArea = AssessMethod = DeployMethod = Frameworks = Body = string.Empty;
        SourceName = CatalogOrigins.Lead;
        SourceVersion = SourceUrl = string.Empty;
        Severity = "Medium";
        PolicyMessage = "New Lead policy: fill in the fields and Save.";
        History.Clear();
    }

    private CatalogControlContent? PolicyContent()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            PolicyMessage = "Enter a name.";
            return null;
        }

        if (Body.Trim().Length > 0)
        {
            try
            {
                using var _ = JsonDocument.Parse(Body);
            }
            catch (JsonException exception)
            {
                PolicyMessage = $"The policy body is not valid JSON: {exception.Message}";
                return null;
            }
        }

        if (Kind == CatalogKinds.ConditionalAccess && Application.Governance.BreakGlassRule.Problem(PolicyId, Body.Trim()) is { } breakGlass)
        {
            PolicyMessage = breakGlass;
            return null;
        }

        if (Kind == CatalogKinds.ConditionalAccess && Body.Trim().Length > 0 && JsonDocument.Parse(Body).RootElement is var root &&
            root.TryGetProperty("state", out var state) && state.GetString() != Application.Planning.ConditionalAccessRollout.ReportOnlyState)
        {
            PolicyMessage = $"Conditional Access policies are kept in report-only mode (\"state\": \"{Application.Planning.ConditionalAccessRollout.ReportOnlyState}\") and monitored for " +
                            $"{Application.Planning.ConditionalAccessRollout.MinimumReportOnlyDays}–{Application.Planning.ConditionalAccessRollout.RecommendedReportOnlyDays} days before they are switched on.";
            return null;
        }

        var evaluation = SelectedPolicy?.Control.Evaluation ?? (Kind == CatalogKinds.ConditionalAccess ? CatalogEvaluations.ConditionalAccess : CatalogEvaluations.Manual);
        return new CatalogControlContent(Kind, Category.Trim(), Platform.Trim(), Name.Trim(), Setting.Trim(), Value.Trim(), PolicyDescription.Trim(), Severity, UserImpact.Trim(),
            Frameworks.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), evaluation, SelectedPolicy?.Control.GraphResource ?? string.Empty,
            SelectedPolicy?.Control.OdataType ?? string.Empty, Body.Trim(), Product.Trim(), FeatureArea.Trim(), DefaultValue.Trim(), AssessMethod, DeployMethod, SourceName.Trim(),
            SourceVersion.Trim(), SourceUrl.Trim());
    }

    [RelayCommand]
    private async Task SavePolicyAsync()
    {
        if (!HasPolicy || PolicyContent() is not { } content) return;
        var gds = catalog.GdsCatalog.Find(PolicyId);
        var edit = gds is null || IsNewPolicy
            ? new CatalogControlEdit(PolicyId, null, false, content, null, 0, string.Empty, DateTimeOffset.UtcNow)
            : new CatalogControlEdit(PolicyId, gds.Id, false, content, CatalogComposer.Hash(gds), 0, string.Empty, DateTimeOffset.UtcNow);
        await RunAsync(() => repository.SaveControlAsync(edit), $"Saved {PolicyId}.", message => PolicyMessage = message, PolicyId, null);
    }

    /// <summary>Hides a GDS policy (kept hidden across catalogue reloads) or deletes a Lead policy.</summary>
    [RelayCommand]
    private async Task RemovePolicyAsync()
    {
        if (!HasPolicy) return;
        if (catalog.GdsCatalog.Find(PolicyId) is { } gds)
            await RunAsync(() => repository.SaveControlAsync(new CatalogControlEdit(PolicyId, gds.Id, true, null, CatalogComposer.Hash(gds), 0, string.Empty, DateTimeOffset.UtcNow)),
                $"{PolicyId} is hidden. Revert to GDS shows it again.", message => PolicyMessage = message, null, null);
        else
            await RunAsync(() => repository.DeleteControlAsync(PolicyId), $"{PolicyId} deleted.", message => PolicyMessage = message, null, null);
        PolicyId = string.Empty;
    }

    /// <summary>Removes the Lead's edit (or hide) of a GDS policy: the GDS version shows again.</summary>
    [RelayCommand]
    private async Task RevertPolicyAsync()
    {
        if (!HasPolicy || catalog.GdsCatalog.Find(PolicyId) is null) return;
        await RunAsync(() => repository.DeleteControlAsync(PolicyId), $"{PolicyId} is back to the GDS version.", message => PolicyMessage = message, PolicyId, null);
    }

    // ---- Policy sets ----------------------------------------------------------------------------------------------

    partial void OnSelectedSetChanged(PolicySetListRow? value)
    {
        if (value is null) return;
        var set = value.Set;
        SetId = set.Id;
        SetOrigin = value.Origin;
        SetName = set.DisplayName;
        SetDescription = set.Description;
        SetOrder = set.DeploymentOrder.ToString(CultureInfo.InvariantCulture);
        Members.Clear();
        foreach (var id in set.ControlIds)
            Members.Add(catalog.Catalog.Find(id) is { } control ? new PolicySetMemberRow(id, control.Name, control.KindDisplayName, false) : new PolicySetMemberRow(id, id, "", true));
        foreach (var id in set.RetiredMembers) Members.Add(new PolicySetMemberRow(id, id, string.Empty, true));
        SetMessage = set.IsUpdateAvailable ? "GDS has changed this policy set since it was edited." : null;
    }

    [RelayCommand]
    private void NewSet()
    {
        SelectedSet = null;
        var number = catalog.Catalog.PolicySets.Count(set => set.Id.StartsWith("L-SET-", StringComparison.OrdinalIgnoreCase)) + 1;
        SetId = $"L-SET-{number:000}";
        SetOrigin = CatalogOrigins.Lead;
        SetName = string.Empty;
        SetDescription = string.Empty;
        SetOrder = "100";
        Members.Clear();
        SetMessage = "New Lead policy set: name it, add policies of any kind, and Save.";
    }

    [RelayCommand]
    private void AddMember()
    {
        if (!HasSet || SelectedCandidate is not { } candidate) return;
        if (Members.Any(member => string.Equals(member.ControlId, candidate.Id, StringComparison.OrdinalIgnoreCase))) return;
        Members.Add(new PolicySetMemberRow(candidate.Id, candidate.Name, candidate.Kind, false));
    }

    [RelayCommand]
    private void RemoveMember(PolicySetMemberRow? member)
    {
        if ((member ?? SelectedMember) is { } row) Members.Remove(row);
    }

    [RelayCommand]
    private async Task SaveSetAsync()
    {
        if (!HasSet) return;
        if (!int.TryParse(SetOrder, NumberStyles.Integer, CultureInfo.CurrentCulture, out var order) || order is < 0 or > 10_000)
        {
            SetMessage = "Deployment order is a number from 0 to 10,000.";
            return;
        }

        var gds = catalog.GdsCatalog.FindPolicySet(SetId);
        var edit = new CatalogPolicySetEdit(SetId, gds?.Id, false, SetName, SetDescription.Trim(), order, Members.Select(member => member.ControlId).ToArray(), gds?.Includes ?? [],
            gds is null ? null : CatalogComposer.Hash(gds), 0, string.Empty, DateTimeOffset.UtcNow);
        await RunAsync(() => repository.SavePolicySetAsync(edit), $"Saved policy set {SetId} ({Members.Count} policies).", message => SetMessage = message, null, SetId);
    }

    [RelayCommand]
    private async Task RemoveSetAsync()
    {
        if (!HasSet) return;
        if (catalog.GdsCatalog.FindPolicySet(SetId) is { } gds)
            await RunAsync(() => repository.SavePolicySetAsync(new CatalogPolicySetEdit(SetId, gds.Id, true, gds.DisplayName, gds.Description, gds.DeploymentOrder, gds.ControlIds,
                    gds.Includes, CatalogComposer.Hash(gds), 0, string.Empty, DateTimeOffset.UtcNow)),
                $"Policy set {SetId} is hidden. Revert to GDS shows it again.", message => SetMessage = message, null, null);
        else
            await RunAsync(() => repository.DeletePolicySetAsync(SetId), $"Policy set {SetId} deleted.", message => SetMessage = message, null, null);
        SetId = string.Empty;
        Members.Clear();
    }

    [RelayCommand]
    private async Task RevertSetAsync()
    {
        if (!HasSet || catalog.GdsCatalog.FindPolicySet(SetId) is null) return;
        await RunAsync(() => repository.DeletePolicySetAsync(SetId), $"Policy set {SetId} is back to the GDS version.", message => SetMessage = message, null, SetId);
    }

    /// <summary>Saves, reloads the composed catalogue and refreshes the database copy (pol.*, CatalogControl) in the background.</summary>
    private async Task RunAsync(Func<Task> action, string done, Action<string> report, string? selectPolicy, string? selectSet)
    {
        try
        {
            await action();
            await catalog.RefreshAsync();
            Load(selectPolicy, selectSet);
            var queued = sync?.Queue(false) is not null;
            report(done + (queued ? " The database copy of the catalogue is being updated (activity panel)." : string.Empty));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Data.Common.DbException)
        {
            report(exception.Message);
        }
    }
}
