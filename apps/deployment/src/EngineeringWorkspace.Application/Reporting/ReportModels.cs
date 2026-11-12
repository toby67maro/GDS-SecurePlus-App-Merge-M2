using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Catalog;

namespace EngineeringWorkspace.Application.Reporting;

/// <summary>Satisfaction of one control in a stored assessment, after exceptions.</summary>
public static class FactStatus
{
    public const string Satisfied = "Satisfied";
    public const string Partial = "Partially Satisfied";
    public const string Failed = "Not Satisfied";
    public const string Missing = "Missing";
    public const string NotEvaluated = "Not Evaluated";
    public const string Excepted = "Exception Approved";

    public static IReadOnlyList<string> All { get; } = [Satisfied, Partial, Failed, Missing, Excepted, NotEvaluated];

    public static string From(string result, bool excepted) => excepted
        ? Excepted
        : result switch
        {
            AssessmentResultValues.Pass => Satisfied,
            AssessmentResultValues.Partial => Partial,
            AssessmentResultValues.Fail => Failed,
            AssessmentResultValues.Missing => Missing,
            _ => NotEvaluated
        };

    public static bool IsScored(string status) => status != NotEvaluated;
    public static bool IsGap(string status) => status is Failed or Missing or Partial;
}

/// <summary>Which controls a report counts.</summary>
public enum ReportScope
{
    /// <summary>The controls of the active baseline configurations (and the assessment checks), as the score.</summary>
    ActiveBaselines,

    /// <summary>The controls of the specification assigned to the customer.</summary>
    AssignedSpecification,

    /// <summary>Every catalog control the assessment evaluated.</summary>
    AllEvaluated
}

/// <summary>One stored result joined with its catalog control.</summary>
public sealed record ResultFact(CatalogControl Control, string Result, string Status, string? Evidence, DateTimeOffset AssessedAtUtc)
{
    public string ControlId => Control.Id;
    public bool IsScored => FactStatus.IsScored(Status);
    public bool IsGap => FactStatus.IsGap(Status);

    /// <summary>Credit towards completion: satisfied or excepted 1, partial ½, otherwise 0.</summary>
    public double Credit => Status switch
    {
        FactStatus.Satisfied or FactStatus.Excepted => 1,
        FactStatus.Partial => 0.5,
        _ => 0
    };
}

/// <summary>A customer's latest stored results (and the previous run for trends).</summary>
public sealed record CustomerResults(
    string CustomerKey,
    string CustomerName,
    AssessmentSummary? Latest,
    AssessmentSummary? Previous,
    IReadOnlyList<ResultFact> Facts,
    ReportScope Scope,
    string ScopeDescription)
{
    public bool HasResults => Latest is not null;
    public CompletionRow Overall => Completion.Summarize("All", "All", Facts);
}

/// <summary>Completion of one group (a framework, a solution area, a severity...).</summary>
public sealed record CompletionRow(string Key, string Label, int Controls, int Satisfied, int Partial, int NotSatisfied, int Excepted, int NotEvaluated, double Credit)
{
    public int Scored => Controls - NotEvaluated;

    /// <summary>Completion percentage of the scored controls; null when nothing was scored.</summary>
    public double? Percent => Scored == 0 ? null : Math.Round(100 * Credit / Scored, 1);

    public string PercentText => Percent is { } value ? $"{value:0.#}%" : "–";
    public int Gaps => Partial + NotSatisfied;
}

/// <summary>A selectable report and heat-map dimension.</summary>
public sealed record ReportDimension(string Name, Func<ResultFact, IEnumerable<string>> Keys);

public static class ReportDimensions
{
    public const string Framework = "Framework";
    public const string ComplianceStandard = "Compliance Standard";
    public const string Experience = "Experience";
    public const string SolutionArea = "Solution Area";
    public const string PolicySet = "Policy Set";
    public const string Severity = "Severity";
    public const string RiskLevel = "Risk Level";
    public const string Effort = "Effort";
    public const string Status = "Status";
    public const string Kind = "Control Type";
    public const string Category = "Category";

    public static IReadOnlyList<string> Names { get; } = [Framework, ComplianceStandard, Experience, SolutionArea, PolicySet, Severity, RiskLevel, Effort, Status, Kind];

    public static ReportDimension Get(string name, ControlCatalog catalog) => name switch
    {
        Framework => new(name, fact => fact.Control.FrameworkReferences.Where(reference => !reference.IsComplianceStandard).Select(reference => reference.FrameworkName).Distinct()),
        ComplianceStandard => new(name, fact => fact.Control.FrameworkReferences.Where(reference => reference.IsComplianceStandard).Select(reference => reference.FrameworkName).Distinct()),
        Experience => new(name, fact => [catalog.ExperienceAreaName(fact.Control.ExperienceArea)]),
        SolutionArea => new(name, fact => [catalog.SolutionAreaName(fact.Control.SolutionArea)]),
        PolicySet => new(name, fact => fact.Control.PolicySets.Select(id => catalog.FindPolicySet(id)?.DisplayName ?? id)),
        Severity => new(name, fact => [fact.Control.Severity]),
        RiskLevel => new(name, fact => [Or(fact.Control.Implementation.RiskLevel)]),
        Effort => new(name, fact => [Or(fact.Control.Implementation.Effort)]),
        Status => new(name, fact => [fact.Status]),
        Kind => new(name, fact => [fact.Control.KindDisplayName]),
        Category => new(name, fact => [Or(fact.Control.Category)]),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown report dimension.")
    };

    private static string Or(string value) => string.IsNullOrWhiteSpace(value) ? "Unspecified" : value;

    /// <summary>Severity order for sorting: Critical first.</summary>
    public static int SeverityRank(string severity) => severity switch
    {
        "Critical" => 0,
        "High" => 1,
        "Medium" => 2,
        "Low" => 3,
        _ => 4
    };
}

public static class Completion
{
    public static CompletionRow Summarize(string key, string label, IEnumerable<ResultFact> facts)
    {
        int controls = 0, satisfied = 0, partial = 0, failed = 0, excepted = 0, notEvaluated = 0;
        double credit = 0;
        foreach (var fact in facts)
        {
            controls++;
            credit += fact.Credit;
            switch (fact.Status)
            {
                case FactStatus.Satisfied: satisfied++; break;
                case FactStatus.Partial: partial++; break;
                case FactStatus.Failed or FactStatus.Missing: failed++; break;
                case FactStatus.Excepted: excepted++; break;
                default: notEvaluated++; break;
            }
        }

        return new CompletionRow(key, label, controls, satisfied, partial, failed, excepted, notEvaluated, credit);
    }

    /// <summary>Completion per key of a dimension (a control counts in each of its keys, for example each framework).</summary>
    public static IReadOnlyList<CompletionRow> By(IEnumerable<ResultFact> facts, ReportDimension dimension) =>
        facts.SelectMany(fact => dimension.Keys(fact).DefaultIfEmpty("Unspecified").Select(key => (key, fact)))
            .GroupBy(pair => pair.key, StringComparer.OrdinalIgnoreCase)
            .Select(group => Summarize(group.Key, group.Key, group.Select(pair => pair.fact)))
            .OrderByDescending(row => row.Controls)
            .ThenBy(row => row.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
}

/// <summary>A heat-map cell: the controls behind it and their completion.</summary>
public sealed record HeatMapCell(string Row, string Column, CompletionRow Completion, IReadOnlyList<ResultFact> Facts);

public sealed record HeatMap(IReadOnlyList<string> Rows, IReadOnlyList<string> Columns, IReadOnlyDictionary<(string Row, string Column), HeatMapCell> Cells)
{
    public HeatMapCell? Cell(string row, string column) => Cells.GetValueOrDefault((row, column));

    public static HeatMap Build(IReadOnlyList<ResultFact> facts, ReportDimension rows, ReportDimension columns, int maxRows = 40, int maxColumns = 25)
    {
        var pairs = facts.SelectMany(fact => rows.Keys(fact).DefaultIfEmpty("Unspecified")
                .SelectMany(row => columns.Keys(fact).DefaultIfEmpty("Unspecified").Select(column => (row, column, fact))))
            .ToArray();
        var rowKeys = pairs.GroupBy(pair => pair.row).OrderByDescending(group => group.Count()).Take(maxRows).Select(group => group.Key).OrderBy(key => key, StringComparer.CurrentCulture)
            .ToArray();
        var columnKeys = pairs.GroupBy(pair => pair.column).OrderByDescending(group => group.Count()).Take(maxColumns).Select(group => group.Key)
            .OrderBy(key => key, StringComparer.CurrentCulture).ToArray();
        var rowSet = rowKeys.ToHashSet();
        var columnSet = columnKeys.ToHashSet();
        var cells = pairs.Where(pair => rowSet.Contains(pair.row) && columnSet.Contains(pair.column))
            .GroupBy(pair => (pair.row, pair.column))
            .ToDictionary(group => group.Key, group =>
            {
                var cellFacts = group.Select(pair => pair.fact).DistinctBy(fact => fact.ControlId).ToArray();
                return new HeatMapCell(group.Key.row, group.Key.column, Completion.Summarize($"{group.Key.row}|{group.Key.column}", group.Key.column, cellFacts), cellFacts);
            });
        return new HeatMap(rowKeys, columnKeys, cells);
    }
}

/// <summary>A fleet-view row: a customer's latest stored assessment and its trend.</summary>
public sealed record FleetRow(
    string CustomerKey,
    string CustomerName,
    string Tenant,
    bool IsConnected,
    DateTimeOffset? LatestAssessmentUtc,
    string? LatestStatus,
    double? CompliancePercent,
    double? PreviousPercent,
    int Critical,
    int High,
    int Medium,
    int Low,
    int EnabledSchedules,
    DateTimeOffset? NextScheduledRunUtc,
    string ScheduleStatus,
    string? Industry = null)
{
    public double? Trend => CompliancePercent is { } current && PreviousPercent is { } previous ? Math.Round(current - previous, 1) : null;
    public int FailingTotal => Critical + High + Medium + Low;
}

/// <summary>A roadmap item: a control or policy not yet met, placed in a phase.</summary>
public sealed record RoadmapItem(
    int Phase,
    string PhaseName,
    ResultFact Fact,
    string Priority,
    string Status,
    int EstimatedMinutes,
    string DependsOn,
    int DeploymentOrder)
{
    public CatalogControl Control => Fact.Control;
}

public sealed record RoadmapPhase(int Number, string Name, string Window, IReadOnlyList<RoadmapItem> Items)
{
    public int EstimatedHours => (int)Math.Ceiling(Items.Sum(item => item.EstimatedMinutes) / 60.0);
}
