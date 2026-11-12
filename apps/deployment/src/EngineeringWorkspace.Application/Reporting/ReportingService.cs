using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Governance;
using EngineeringWorkspace.Domain.Common;

namespace EngineeringWorkspace.Application.Reporting;

/// <summary>
/// Reads persisted assessment results for Reporting, Fleet View, Heat Map, Roadmap and Specification Comparison:
/// the latest run with results (Completed or Partial) per customer, joined with the catalog (aliases of merged
/// duplicates resolved), approved exceptions applied, and scoped to the active baselines, the customer's
/// specification or every evaluated control.
/// </summary>
public interface IReportingService
{
    bool IsAvailable { get; }

    Task<CustomerResults> GetCustomerResultsAsync(string customerKey, ReportScope scope, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FleetRow>> GetFleetAsync(CancellationToken cancellationToken = default);

    /// <summary>The controls a scope covers for a customer (null = every evaluated control).</summary>
    Task<IReadOnlySet<string>?> ScopeControlIdsAsync(string customerKey, ReportScope scope, CancellationToken cancellationToken = default);
}

public sealed class ReportingService(
    IEngagementRepository repository,
    ICatalogService catalog,
    IScoringScope scoring,
    ICustomerProfileStore customers,
    ICustomerExceptionRepository exceptions,
    ISpecificationRepository specifications,
    IAssessmentScheduler scheduler,
    TimeProvider timeProvider) : IReportingService
{
    public bool IsAvailable => repository.IsAvailable;

    public async Task<IReadOnlySet<string>?> ScopeControlIdsAsync(string customerKey, ReportScope scope, CancellationToken cancellationToken = default)
    {
        switch (scope)
        {
            case ReportScope.ActiveBaselines:
                var ids = new HashSet<string>(scoring.ControlIds, StringComparer.OrdinalIgnoreCase);
                ids.UnionWith(catalog.Catalog.Controls.Where(control => control.IsScoredWithChecks).Select(control => control.Id));
                return ids;
            case ReportScope.AssignedSpecification:
                var specification = (await specifications.ListSpecificationsAsync(cancellationToken)).FirstOrDefault(spec => spec.AssignedCustomers.Contains(customerKey, StringComparer.OrdinalIgnoreCase));
                return specification is null ? new HashSet<string>() : ProfileResolver.ControlIds(catalog.Catalog, specification.Items);
            default:
                return null;
        }
    }

    public async Task<CustomerResults> GetCustomerResultsAsync(string customerKey, ReportScope scope, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null,
        CancellationToken cancellationToken = default)
    {
        var name = customerKey;
        if (CustomerId.TryCreate(customerKey, out var id) && await customers.LoadAsync(id, cancellationToken) is { } profile) name = profile.Name;
        var scopeIds = await ScopeControlIdsAsync(customerKey, scope, cancellationToken);
        var description = scope switch
        {
            ReportScope.ActiveBaselines => "Controls of the active baseline configurations and the assessment checks",
            ReportScope.AssignedSpecification => scopeIds is { Count: > 0 } ? "Controls of the specification assigned to the customer" : "No specification is assigned to the customer",
            _ => "Every control the assessment evaluated"
        };
        if (!repository.IsAvailable || !CustomerId.TryCreate(customerKey, out var customerId))
            return new CustomerResults(customerKey, name, null, null, [], scope, description);

        var runs = (await repository.ListAssessmentsAsync(customerId, cancellationToken))
            .Where(run => AssessmentStatusCodes.HasResults(run.StatusCode) && !run.IsSample || run.IsSample && run.StatusCode == AssessmentStatusCodes.Completed)
            .Where(run => (fromUtc is null || (run.CompletedAtUtc ?? run.RequestedAtUtc) >= fromUtc) && (toUtc is null || (run.CompletedAtUtc ?? run.RequestedAtUtc) <= toUtc))
            .OrderByDescending(run => run.CompletedAtUtc ?? run.RequestedAtUtc)
            .ToArray();
        // Quick scans evaluate fewer controls: results come from the latest in-depth scan, or the latest quick scan when there is none.
        runs = RunSelection.PreferInDepth(runs);
        var latest = runs.FirstOrDefault();
        if (latest is null) return new CustomerResults(customerKey, name, null, null, [], scope, description);
        var facts = await FactsAsync(customerKey, latest, scopeIds, cancellationToken);
        return new CustomerResults(customerKey, name, latest, runs.Skip(1).FirstOrDefault(), facts, scope, description);
    }

    public async Task<IReadOnlyList<FleetRow>> GetFleetAsync(CancellationToken cancellationToken = default)
    {
        var profiles = await customers.ListAsync(cancellationToken);
        var runs = repository.IsAvailable ? await repository.ListAssessmentsAsync(null, cancellationToken) : [];
        var schedules = repository.IsAvailable ? await scheduler.ListAsync(cancellationToken) : [];
        var scopeIds = await ScopeControlIdsAsync(string.Empty, ReportScope.ActiveBaselines, cancellationToken);
        var rows = new List<FleetRow>();
        foreach (var profile in profiles)
        {
            var key = profile.Id.Value;
            var withResults = RunSelection.PreferInDepth(runs.Where(run => run.CustomerKey == key && AssessmentStatusCodes.HasResults(run.StatusCode))
                .OrderByDescending(run => run.CompletedAtUtc ?? run.RequestedAtUtc).ToArray());
            var latest = withResults.FirstOrDefault();
            var latestAny = runs.Where(run => run.CustomerKey == key).OrderByDescending(run => run.RequestedAtUtc).FirstOrDefault();
            double? percent = null, previousPercent = null;
            int critical = 0, high = 0, medium = 0, low = 0;
            if (latest is not null)
            {
                var facts = await FactsAsync(key, latest, scopeIds, cancellationToken);
                percent = Completion.Summarize("all", "all", facts).Percent;
                foreach (var gap in facts.Where(fact => fact.Status is FactStatus.Failed or FactStatus.Missing))
                {
                    switch (gap.Control.Severity)
                    {
                        case "Critical": critical++; break;
                        case "High": high++; break;
                        case "Medium": medium++; break;
                        default: low++; break;
                    }
                }

                if (withResults.Skip(1).FirstOrDefault() is { } previous)
                    previousPercent = Completion.Summarize("all", "all", await FactsAsync(key, previous, scopeIds, cancellationToken)).Percent;
            }

            var mine = schedules.Where(schedule => schedule.CustomerKeys.Contains(key, StringComparer.OrdinalIgnoreCase) || schedule.AllCustomers).ToArray();
            var enabled = mine.Where(schedule => schedule.IsEnabled).ToArray();
            var next = enabled.Select(schedule => schedule.NextRunUtc).Where(value => value is not null).Min();
            var scheduleStatus = mine.Length == 0
                ? "Not scheduled"
                : mine.Any(schedule => schedule.LastStatus == ScheduleStatusCodes.Running)
                    ? "Running"
                    : enabled.Length == 0
                        ? "Off"
                        : mine.Any(schedule => schedule.LastStatus == ScheduleStatusCodes.Failed) ? "Last run failed" : "Scheduled";
            var connected = profile.PrimaryTenant?.ApplicationRegistration?.SecretReference is not null;
            rows.Add(new FleetRow(key, profile.Name, profile.PrimaryTenant?.PrimaryDomain ?? (connected ? "Connected" : "Not connected"), connected,
                latestAny?.CompletedAtUtc ?? latestAny?.RequestedAtUtc, latestAny?.StatusCode, percent, previousPercent, critical, high, medium, low, enabled.Length, next,
                scheduleStatus, profile.Details.IndustryKey is null ? null
                    : Reference.IndustryList.Display(Reference.IndustryList.Defaults, profile.Details.IndustryKey, profile.Details.IndustryOther)));
        }

        return rows.OrderBy(row => row.CustomerName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private async Task<IReadOnlyList<ResultFact>> FactsAsync(string customerKey, AssessmentSummary run, IReadOnlySet<string>? scope, CancellationToken cancellationToken)
    {
        var current = catalog.Catalog;
        var now = timeProvider.GetUtcNow();
        var excepted = exceptions.IsAvailable
            ? (await exceptions.ListExceptionsAsync(customerKey, cancellationToken)).Where(item => item.IsActiveAt(now)).Select(item => current.ResolveId(item.ControlId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var results = await repository.ListResultsAsync(run.AssessmentRunId, cancellationToken);
        if (results.Count == 0)
        {
            // Earlier versions and the sample stored findings only.
            var findings = await repository.ListFindingsAsync(run.AssessmentRunId, cancellationToken);
            results = findings.Where(finding => finding.ControlId is not null)
                .Select(finding => new AssessmentResultItem(finding.ControlId!, finding.Area, finding.Control, finding.Result, finding.Severity, finding.Evidence, finding.Recommendation))
                .ToArray();
        }

        var assessedAt = run.CompletedAtUtc ?? run.RequestedAtUtc;
        return results
            .Select(result => (Result: result, Control: current.Find(result.ControlId)))
            .Where(pair => pair.Control is not null && (scope is null || scope.Contains(pair.Control.Id)))
            .GroupBy(pair => pair.Control!.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(pair => new ResultFact(pair.Control!, pair.Result.Result, FactStatus.From(pair.Result.Result, excepted.Contains(pair.Control!.Id)), pair.Result.Evidence, assessedAt))
            .ToArray();
    }
}

/// <summary>Builds the remediation roadmap from the controls and policies a customer does not yet meet.</summary>
public static class RoadmapBuilder
{
    public static readonly (int Number, string Name, string Window)[] Phases =
    [
        (1, "Phase 1 – Foundation and Critical Gaps", "first"),
        (2, "Phase 2 – High Priority", "second"),
        (3, "Phase 3 – Medium Priority", "third"),
        (4, "Phase 4 – Hardening and Optimisation", "last")
    ];

    /// <summary>
    /// Places every gap (not satisfied, missing or partial) in a phase by priority, severity, effort and dependencies:
    /// Phase 1 = foundation policy sets, critical severity, or high severity with low effort; Phase 2 = remaining high
    /// severity or early policy sets (deployment order 1–3); Phase 3 = medium; Phase 4 = the rest. Within a phase items
    /// are ordered by deployment order (dependencies first), severity and effort.
    /// </summary>
    public static IReadOnlyList<RoadmapPhase> Build(IEnumerable<ResultFact> facts, ControlCatalog catalog, IReadOnlySet<string>? plannedControlIds = null)
    {
        var items = new List<RoadmapItem>();
        foreach (var fact in facts.Where(fact => fact.IsGap))
        {
            var control = fact.Control;
            var sets = control.PolicySets.Select(catalog.FindPolicySet).OfType<CatalogPolicySet>().Where(set => set.Includes.Count == 0).ToArray();
            var order = sets.Length == 0 ? 99 : sets.Min(set => set.DeploymentOrder);
            var severity = ReportDimensions.SeverityRank(control.Severity);
            var effort = control.Implementation.Effort;
            var phase = order == 0 || severity == 0 || (severity == 1 && effort == "Low")
                ? 1
                : severity == 1 || order is >= 1 and <= 3
                    ? 2
                    : severity == 2 ? 3 : 4;
            var priority = phase switch { 1 => "Critical", 2 => "High", 3 => "Medium", _ => "Low" };
            var dependsOn = string.Join("; ", sets.SelectMany(set => set.Requires).Distinct(StringComparer.OrdinalIgnoreCase).Take(4));
            var status = plannedControlIds?.Contains(control.Id) == true ? "Planned" : fact.Status == FactStatus.Partial ? "In Progress" : "Not Started";
            var (_, name, _) = Phases[phase - 1];
            items.Add(new RoadmapItem(phase, name, fact, priority, status, Math.Max(5, control.Implementation.EstimatedMinutes), dependsOn, order));
        }

        return Phases.Select(phase => new RoadmapPhase(phase.Number, phase.Name, phase.Window,
                items.Where(item => item.Phase == phase.Number)
                    .OrderBy(item => item.DeploymentOrder)
                    .ThenBy(item => ReportDimensions.SeverityRank(item.Control.Severity))
                    .ThenBy(item => item.Control.Implementation.EstimatedMinutes)
                    .ThenBy(item => item.Control.Id, StringComparer.Ordinal)
                    .ToArray()))
            .ToArray();
    }
}

/// <summary>Which runs reports use (4.0 beta.2).</summary>
public static class RunSelection
{
    /// <summary>Newest first; when any in-depth run exists only in-depth runs are returned, otherwise the quick scans.</summary>
    public static AssessmentSummary[] PreferInDepth(IReadOnlyList<AssessmentSummary> newestFirst) =>
        newestFirst.Any(run => !run.IsQuickScan) ? newestFirst.Where(run => !run.IsQuickScan).ToArray() : newestFirst.ToArray();
}
