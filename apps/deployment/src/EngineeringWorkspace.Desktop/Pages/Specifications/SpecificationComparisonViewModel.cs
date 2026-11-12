using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Export;
using EngineeringWorkspace.Application.Governance;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Application.Reporting;
using EngineeringWorkspace.Desktop.Controls;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Desktop.Pages.Catalog;
using EngineeringWorkspace.Desktop.Pages.Reporting;
using EngineeringWorkspace.Desktop.Services;
using EngineeringWorkspace.Domain.Customers;

namespace EngineeringWorkspace.Desktop.Pages.Specifications;

/// <summary>Comparison outcomes.</summary>
public static class ComparisonOutcomes
{
    public const string Has = "Has";
    public const string Missing = "Missing";
    public const string Different = "Different Settings";
    public const string Extra = "Not Required";
    public const string NotEvaluated = "Not Evaluated";

    public static IReadOnlyList<string> All { get; } = [Has, Missing, Different, Extra, NotEvaluated];
}

/// <summary>One compared control: the target's status, whether the specification requires it, and the other tenants.</summary>
public sealed record ComparisonRow(CatalogControl Control, string Outcome, string TargetStatus, bool Required, string References, string? TargetEvidence,
    IReadOnlyList<(string Tenant, string Status, string? Evidence)> Others)
{
    public string Id => Control.Id;
    public string Name => Control.Name;
    public string Severity => Control.Severity;
    public string Kind => Control.KindDisplayName;
    public string RequiredText => Required ? "Yes" : "No";
}

/// <summary>
/// Reporting → Specification Comparison: compares a target tenant (the customer drop-down) with a specification
/// (a standard one, another customer's, or none) and/or one or more other tenants, from stored assessment results.
/// Each control is Has, Missing, Different Settings (partial, or the compared tenants disagree) or Not Required; select a
/// row for policy-level detail (expected settings, evidence per tenant and the Microsoft Graph body).
/// </summary>
public sealed partial class SpecificationComparisonViewModel : CustomerScopedPageViewModel
{
    private const string NoSpecification = "(No Specification)";
    private readonly IReportingService _reporting;
    private readonly ICatalogService _catalog;
    private readonly ISpecificationRepository _specifications;
    private readonly ExportCoordinator _export;
    private IReadOnlyList<SpecificationRecord> _specs = [];

    public SpecificationComparisonViewModel(ICustomerContextService context, ICustomerProfileStore store, IReportingService reporting, ICatalogService catalog,
        ISpecificationRepository specifications, ExportCoordinator export) : base(context, store)
    {
        _reporting = reporting;
        _catalog = catalog;
        _specifications = specifications;
        _export = export;
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = item => item is ComparisonRow row && Matches(row);
        OutcomeFilter.Changed += (_, _) => RefreshView();
        SeverityFilter.Changed += (_, _) => RefreshView();
        CompareTenants.Changed += (_, _) =>
        {
            if (!_suppress) _ = CompareAsync();
        };
    }

    public ObservableCollection<Choice<string>> SpecificationOptions { get; } = [];
    public MultiSelectFilter CompareTenants { get; } = new("Compare With Tenants", emptyMeansAll: false);
    public MultiSelectFilter OutcomeFilter { get; } = new("Outcome", options: ComparisonOutcomes.All.Select(value => (value, value)));
    public MultiSelectFilter SeverityFilter { get; } = new("Severity", options: new[] { "Critical", "High", "Medium", "Low" }.Select(value => (value, value)));
    public ObservableCollection<ComparisonRow> Rows { get; } = [];
    public ICollectionView RowsView { get; }
    public ObservableCollection<KpiTile> Kpis { get; } = [];
    public ObservableCollection<CatalogDetail> Detail { get; } = [];

    [ObservableProperty] private Choice<string>? _specification;
    [ObservableProperty] private ComparisonRow? _selectedRow;
    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private string _countText = string.Empty;
    [ObservableProperty] private string? _graphBody;
    [ObservableProperty] private bool _hasRows;

    private bool _suppress;

    partial void OnSpecificationChanged(Choice<string>? value)
    {
        if (!_suppress) _ = CompareAsync();
    }
    partial void OnSearchChanged(string value) => RefreshView();
    partial void OnSelectedRowChanged(ComparisonRow? value) => ShowDetail(value);

    public override async Task OnNavigatedToAsync(RouteDescriptor route, CancellationToken cancellationToken)
    {
        await LoadSpecificationsAsync();
        await base.OnNavigatedToAsync(route, cancellationToken);
    }

    protected override async Task OnCustomerChangedAsync(CustomerProfile? customer)
    {
        _suppress = true;
        CompareTenants.SetOptions(CustomerOptions.Where(option => option.Id != customer?.Id).Select(option => (option.Id.Value, option.Name)));
        _suppress = false;
        var assigned = customer is null ? null : _specs.FirstOrDefault(spec => spec.AssignedCustomers.Contains(customer.Id.Value, StringComparer.OrdinalIgnoreCase));
        _suppress = true;
        Specification = SpecificationOptions.FirstOrDefault(option => option.Value == assigned?.SpecificationKey)
                        ?? SpecificationOptions.FirstOrDefault(option => option.Value == "SPEC-SP-ADVANCED") ?? SpecificationOptions.FirstOrDefault();
        _suppress = false;
        await CompareAsync();
    }

    private async Task LoadSpecificationsAsync()
    {
        _specs = _specifications.IsAvailable ? await _specifications.ListSpecificationsAsync() : [];
        SpecificationOptions.Clear();
        SpecificationOptions.Add(new Choice<string>(NoSpecification, NoSpecification));
        foreach (var spec in _specs.OrderByDescending(spec => spec.IsStandard).ThenBy(spec => spec.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var owner = spec.AssignedCustomers.Count == 0 ? "unassigned" : $"assigned to {spec.AssignedCustomers.Count} customer(s)";
            SpecificationOptions.Add(new Choice<string>(spec.SpecificationKey, $"{spec.Name} ({(spec.IsStandard ? "standard" : "tailored")}, {owner})"));
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => CompareAsync();

    private async Task CompareAsync()
    {
        Rows.Clear();
        Kpis.Clear();
        HasRows = false;
        if (SelectedCustomer is not { } target)
        {
            StatusMessage = "Choose the target customer.";
            return;
        }

        if (!_reporting.IsAvailable)
        {
            StatusMessage = "The application database is not ready, so stored assessment results are unavailable.";
            return;
        }

        var spec = _specs.FirstOrDefault(item => item.SpecificationKey == Specification?.Value);
        var others = CompareTenants.SelectedValues;
        if (spec is null && others.Count == 0)
        {
            StatusMessage = "Choose a specification and/or at least one tenant to compare with.";
            return;
        }

        IsBusy = true;
        try
        {
            var current = _catalog.Catalog;
            var targetResults = await _reporting.GetCustomerResultsAsync(target.Id.Value, ReportScope.AllEvaluated);
            if (!targetResults.HasResults)
            {
                StatusMessage = $"{target.Name} has no stored assessment with results. Run one on Assessment Scan or Live Assessment.";
                return;
            }

            var references = new List<CustomerResults>();
            foreach (var key in others) references.Add(await _reporting.GetCustomerResultsAsync(key, ReportScope.AllEvaluated));
            var required = spec is null ? new HashSet<string>() : ProfileResolver.ControlIds(current, spec.Items);
            var targetFacts = targetResults.Facts.ToDictionary(fact => fact.ControlId, StringComparer.OrdinalIgnoreCase);
            var referenceFacts = references.Select(result => (result.CustomerName, Facts: result.Facts.ToDictionary(fact => fact.ControlId, StringComparer.OrdinalIgnoreCase))).ToArray();

            // Controls compared: the specification's, plus those any compared tenant satisfies.
            var ids = new HashSet<string>(required, StringComparer.OrdinalIgnoreCase);
            foreach (var (_, facts) in referenceFacts)
                ids.UnionWith(facts.Values.Where(fact => fact.Status is FactStatus.Satisfied or FactStatus.Partial or FactStatus.Excepted).Select(fact => fact.ControlId));
            foreach (var id in ids.Order(StringComparer.Ordinal))
            {
                if (current.Find(id) is not { } control) continue;
                var targetFact = targetFacts.GetValueOrDefault(id);
                var targetStatus = targetFact?.Status ?? FactStatus.NotEvaluated;
                var otherStatuses = referenceFacts.Select(reference => (reference.CustomerName, Status: reference.Facts.GetValueOrDefault(id)?.Status ?? FactStatus.NotEvaluated,
                    Evidence: reference.Facts.GetValueOrDefault(id)?.Evidence)).ToArray();
                var targetHas = targetStatus is FactStatus.Satisfied or FactStatus.Excepted;
                var referenceHas = otherStatuses.Any(other => other.Status is FactStatus.Satisfied or FactStatus.Excepted);
                var isRequired = required.Contains(id);
                var outcome = targetStatus == FactStatus.NotEvaluated
                    ? ComparisonOutcomes.NotEvaluated
                    : targetStatus == FactStatus.Partial
                        ? ComparisonOutcomes.Different
                        : isRequired
                            ? targetHas ? ComparisonOutcomes.Has : ComparisonOutcomes.Missing
                            : referenceHas && !targetHas
                                ? ComparisonOutcomes.Missing
                                : otherStatuses.Any(other => other.Status != targetStatus && other.Status != FactStatus.NotEvaluated)
                                    ? ComparisonOutcomes.Different
                                    : targetHas ? ComparisonOutcomes.Has : ComparisonOutcomes.Extra;
                var referenceText = string.Join("; ", otherStatuses.Select(other => $"{other.CustomerName}: {other.Status}"));
                Rows.Add(new ComparisonRow(control, outcome, targetStatus, isRequired, referenceText.Length == 0 ? "—" : referenceText, targetFact?.Evidence,
                    otherStatuses.Select(other => (other.CustomerName, other.Status, other.Evidence)).ToArray()));
            }

            HasRows = Rows.Count > 0;
            foreach (var outcome in ComparisonOutcomes.All)
                Kpis.Add(new KpiTile(outcome, Rows.Count(row => row.Outcome == outcome).ToString("N0", CultureInfo.CurrentCulture),
                    outcome == ComparisonOutcomes.Missing ? "Required or present elsewhere, not configured" : outcome == ComparisonOutcomes.Different ? "Partial or differs between tenants" : string.Empty));
            if (spec is not null)
            {
                var met = Rows.Count(row => row.Required && row.Outcome == ComparisonOutcomes.Has);
                var total = Rows.Count(row => row.Required);
                Kpis.Insert(0, new KpiTile("Specification Coverage", total == 0 ? "–" : $"{100.0 * met / total:0.#}%", $"{met} of {total} required controls in place"));
            }

            StatusMessage = $"{target.Name} compared with {string.Join(" and ", new[] { spec?.Name, others.Count > 0 ? $"{others.Count} tenant(s)" : null }.Where(text => text is not null))}.";
            RefreshView();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool Matches(ComparisonRow row)
    {
        if (!OutcomeFilter.Matches(row.Outcome) || !SeverityFilter.Matches(row.Severity)) return false;
        if (string.IsNullOrWhiteSpace(Search)) return true;
        var term = Search.Trim();
        return row.Id.Contains(term, StringComparison.OrdinalIgnoreCase) || row.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase);
    }

    private void RefreshView()
    {
        RowsView.Refresh();
        CountText = $"{RowsView.Cast<object>().Count():N0} of {Rows.Count:N0} control(s)";
    }

    private void ShowDetail(ComparisonRow? row)
    {
        Detail.Clear();
        GraphBody = null;
        if (row is null) return;
        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) Detail.Add(new CatalogDetail(name, value));
        }

        Add("Control", $"{row.Id} – {row.Name}");
        Add("Outcome", row.Outcome);
        Add("Required by Specification", row.RequiredText);
        Add($"{SelectedCustomer?.Name ?? "Target"}", $"{row.TargetStatus}{(row.TargetEvidence is null ? string.Empty : $": {row.TargetEvidence}")}");
        foreach (var (tenant, status, evidence) in row.Others) Add(tenant, $"{status}{(evidence is null ? string.Empty : $": {evidence}")}");
        Add("Setting", row.Control.Setting);
        Add("Target Value", row.Control.Value);
        foreach (var expected in row.Control.Expected.Take(25)) Add($"Expected: {expected.Path}", expected.Value.ToString());
        Add("Policy Sets", string.Join(", ", row.Control.PolicySets));
        GraphBody = row.Control.Remediation.JsonBody.Length > 0 ? row.Control.Remediation.JsonBody : null;
    }

    [RelayCommand]
    private async Task ExportAsync(object? format)
    {
        if (!HasRows || SelectedCustomer is null)
        {
            StatusMessage = "There is nothing to export.";
            return;
        }

        var rows = RowsView.Cast<ComparisonRow>().ToArray();
        var document = new ExportDocument("Specification Comparison", SelectedCustomer.Name, DateTimeOffset.UtcNow,
            new[] { new KeyValuePair<string, string>("Specification", Specification?.Label ?? NoSpecification), new("Compared Tenants", CompareTenants.Summary) }
                .Concat(Kpis.Select(kpi => new KeyValuePair<string, string>(kpi.Title, kpi.Value))).ToArray(),
            [new ExportTable("Controls", ["ID", "Control", "Outcome", "Target Status", "Required", "Severity", "Type", "Compared Tenants", "Target Evidence"],
                rows.Select(row => (IReadOnlyList<string?>)new string?[]
                    { row.Id, row.Name, row.Outcome, row.TargetStatus, row.RequiredText, row.Severity, row.Kind, row.References, row.TargetEvidence }).ToArray())]);
        try
        {
            StatusMessage = await _export.ExportAsync(document, ExportCoordinator.Parse(format)) ?? StatusMessage;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"The export could not be written: {exception.Message}";
        }
    }
}
