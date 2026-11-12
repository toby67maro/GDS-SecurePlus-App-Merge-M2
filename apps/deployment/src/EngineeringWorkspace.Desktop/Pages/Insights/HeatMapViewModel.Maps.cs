using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Experience;
using EngineeringWorkspace.Application.Planning;
using EngineeringWorkspace.Application.Reporting;
using EngineeringWorkspace.Desktop.Controls;
using EngineeringWorkspace.Desktop.Pages.Reporting;

namespace EngineeringWorkspace.Desktop.Pages.Insights;

/// <summary>A heat map the page can show.</summary>
public sealed record HeatMapKind(string Code, string Name, string Description, bool HighIsGood, string Legend);

/// <summary>The heat maps besides the compliance map: remediation priority, fleet, trend, threat coverage and experience friction.</summary>
public sealed partial class HeatMapViewModel
{
    public IReadOnlyList<HeatMapKind> MapKinds { get; } =
    [
        new("COMPLIANCE", "Compliance", "Compliance of the selected customer by any two dimensions, with filters and drill-down.", true, "0% → 100% compliant"),
        new("PRIORITY", "Remediation Priority (Severity × Effort)",
            "Gaps by severity and effort. Top-left cells (critical, low effort) are the quick wins to fix first.", false, "few gaps → many gaps"),
        new("FLEET", "Fleet Compliance (Customer × Solution Area)", "Every customer's latest compliance by solution area: spot weak areas across the whole fleet.", true,
            "0% → 100% compliant"),
        new("TREND", "Compliance Trend (Solution Area × Month)", "The selected customer's compliance at the end of each of the last six months.", true, "0% → 100% compliant"),
        new("THREAT", "Threat Coverage (ATT&CK Tactic × Service Tier)",
            "Share of MITRE ATT&CK techniques each service tier's Microsoft capabilities protect against or detect (MITRE CTID mappings).", true, "low coverage → high coverage"),
        new("EXPERIENCE", "Experience Friction (User Group × Solution)",
            "How much each user group's experience interrupts their work, by Microsoft solution (from Experience Configuration).", false, "smooth → high friction"),
    ];

    public ObservableCollection<HeatCell> OtherCells { get; } = [];
    public ObservableCollection<FactRow> OtherDetail { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompliance), nameof(IsOtherMap))]
    private HeatMapKind? _selectedKind;

    [ObservableProperty] private string _otherCaption = "Click a cell for detail.";
    [ObservableProperty] private string _otherDetailText = string.Empty;
    [ObservableProperty] private bool _otherLoading;

    public bool IsCompliance => SelectedKind is null or { Code: "COMPLIANCE" };
    public bool IsOtherMap => !IsCompliance;

    partial void OnSelectedKindChanged(HeatMapKind? value) => _ = LoadOtherMapAsync();

    private async Task LoadOtherMapAsync()
    {
        OtherCells.Clear();
        OtherDetail.Clear();
        OtherDetailText = string.Empty;
        OtherCaption = "Click a cell for detail.";
        if (IsCompliance) return;
        OtherLoading = true;
        try
        {
            switch (SelectedKind!.Code)
            {
                case "PRIORITY":
                    BuildPriority();
                    break;
                case "FLEET":
                    await BuildFleetAsync();
                    break;
                case "TREND":
                    await BuildTrendAsync();
                    break;
                case "THREAT":
                    await BuildThreatAsync();
                    break;
                case "EXPERIENCE":
                    BuildExperience();
                    break;
            }

            if (OtherCells.Count == 0) OtherCaption = "No data for this heat map yet.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
        {
            OtherCaption = $"The heat map could not be built: {exception.Message}";
        }
        finally
        {
            OtherLoading = false;
        }
    }

    private void BuildPriority()
    {
        if (_results is not { HasResults: true }) return;
        var gaps = _results.Facts.Where(fact => fact.IsGap).ToArray();
        var max = 1.0;
        var cells = new List<(string Severity, string Effort, ResultFact[] Facts)>();
        foreach (var severity in new[] { "Critical", "High", "Medium", "Low", "Informational" })
        foreach (var effort in new[] { "Low", "Medium", "High" })
        {
            var facts = gaps.Where(fact => string.Equals(fact.Control.Severity, severity, StringComparison.OrdinalIgnoreCase) &&
                                           string.Equals(fact.Control.Implementation.Effort, effort, StringComparison.OrdinalIgnoreCase)).ToArray();
            cells.Add((severity, effort, facts));
            max = Math.Max(max, facts.Length);
        }

        foreach (var (severity, effort, facts) in cells)
            OtherCells.Add(new HeatCell(severity, $"{effort} effort", facts.Length == 0 ? null : facts.Length / max, facts.Length.ToString(CultureInfo.CurrentCulture),
                $"{facts.Length} gap(s), about {facts.Sum(fact => fact.Control.Implementation.EstimatedMinutes) / 60.0:0.#} hour(s)" +
                (severity is "Critical" or "High" && effort == "Low" ? "\nQuick wins: fix these first." : string.Empty), facts));
    }

    private async Task BuildFleetAsync()
    {
        if (!reporting.IsAvailable) return;
        var current = catalog.Catalog;
        var area = ReportDimensions.Get(ReportDimensions.SolutionArea, current);
        foreach (var customer in CustomerOptions.Take(30))
        {
            var results = await reporting.GetCustomerResultsAsync(customer.Id.Value, Scope?.Value ?? ReportScope.ActiveBaselines);
            if (!results.HasResults) continue;
            foreach (var group in results.Facts.SelectMany(fact => area.Keys(fact).DefaultIfEmpty("Unspecified").Select(key => (key, fact))).GroupBy(pair => pair.key))
            {
                var completion = Completion.Summarize(group.Key, group.Key, group.Select(pair => pair.fact).ToArray());
                OtherCells.Add(new HeatCell(customer.Name, group.Key, completion.Percent is { } percent ? percent / 100 : null, completion.PercentText,
                    $"{completion.Controls} control(s), {completion.Gaps} gap(s)", group.Select(pair => pair.fact).ToArray()));
            }
        }
    }

    private async Task BuildTrendAsync()
    {
        if (SelectedCustomer is not { } customer || !reporting.IsAvailable) return;
        var current = catalog.Catalog;
        var area = ReportDimensions.Get(ReportDimensions.SolutionArea, current);
        var now = DateTime.Today;
        DateTimeOffset? lastAssessment = null;
        for (var back = 5; back >= 0; back--)
        {
            var month = new DateTime(now.Year, now.Month, 1).AddMonths(-back);
            var end = back == 0 ? DateTimeOffset.Now : new DateTimeOffset(month.AddMonths(1));
            var results = await reporting.GetCustomerResultsAsync(customer.Id.Value, Scope?.Value ?? ReportScope.ActiveBaselines, null, end);
            if (!results.HasResults) continue;
            var label = month.ToString("MMM yyyy", CultureInfo.CurrentCulture);
            var assessed = results.Latest!.CompletedAtUtc ?? results.Latest.RequestedAtUtc;
            var note = lastAssessment == assessed ? " (no new assessment this month)" : string.Empty;
            lastAssessment = assessed;
            foreach (var group in results.Facts.SelectMany(fact => area.Keys(fact).DefaultIfEmpty("Unspecified").Select(key => (key, fact))).GroupBy(pair => pair.key))
            {
                var completion = Completion.Summarize(group.Key, group.Key, group.Select(pair => pair.fact).ToArray());
                OtherCells.Add(new HeatCell(group.Key, label, completion.Percent is { } percent ? percent / 100 : null, completion.PercentText,
                    $"Assessment of {assessed.ToLocalTime():d}{note}: {completion.Controls} control(s), {completion.Gaps} gap(s)", group.Select(pair => pair.fact).ToArray()));
            }
        }
    }

    private async Task BuildThreatAsync()
    {
        if (!planning.IsAvailable)
        {
            OtherCaption = "The application database is not ready; threat coverage comes from the knowledge base.";
            return;
        }

        foreach (var cell in await planning.GetThreatCoverageAsync())
            OtherCells.Add(new HeatCell(cell.Tactic, cell.Column, cell.Techniques == 0 ? null : cell.Ratio, cell.Techniques == 0 ? "—" : cell.Ratio.ToString("P0", CultureInfo.CurrentCulture),
                $"{cell.Covered} of {cell.Techniques} technique(s) covered." + (cell.Gaps.Count > 0 ? $"\nNot covered, for example:\n{string.Join("\n", cell.Gaps)}" : string.Empty), cell));
    }

    private void BuildExperience()
    {
        if (SelectedCustomer is not { } customer) return;
        var frameworks = (customer.WorkspaceSettings.GetValueOrDefault(Experience.ExperienceConfigurationViewModel.FrameworksSetting) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var experience in ExperienceEngine.EvaluateAll(customer, frameworks))
        foreach (var score in experience.Solutions)
            OtherCells.Add(new HeatCell(experience.Name, score.Solution.Split(" (")[0], score.Friction / 100, score.Friction.ToString("0", CultureInfo.CurrentCulture),
                string.Join("\n", experience.Answers.Where(answer => answer.Question.Solution == score.Solution)
                    .Select(answer => $"{answer.Question.Topic}: {answer.Option.Label} (friction {answer.Option.Friction})")) +
                (score.Conflicts > 0 ? $"\n{score.Conflicts} conflict(s) with the compliance targets." : string.Empty), experience));
    }

    [RelayCommand]
    private void SelectOtherCell(object? item)
    {
        OtherDetail.Clear();
        if (item is not HeatCell cell) return;
        OtherCaption = $"{cell.Row} × {cell.Column}";
        OtherDetailText = cell.Detail ?? cell.Text;
        if (cell.Tag is IReadOnlyList<ResultFact> facts)
        {
            var current = catalog.Catalog;
            foreach (var fact in facts.OrderBy(fact => fact.Credit).ThenBy(fact => ReportDimensions.SeverityRank(fact.Control.Severity)).Take(300))
                OtherDetail.Add(new FactRow(fact.ControlId, fact.Control.Name, fact.Status, fact.Control.Severity, fact.Control.KindDisplayName, current.SolutionAreaName(fact.Control.SolutionArea),
                    current.ExperienceAreaName(fact.Control.ExperienceArea), fact.Control.Implementation.Effort, fact.Evidence));
        }
    }
}
