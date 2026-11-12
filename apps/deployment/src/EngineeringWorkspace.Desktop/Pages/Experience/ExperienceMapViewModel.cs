using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Experience;
using EngineeringWorkspace.Application.Export;
using EngineeringWorkspace.Desktop.Controls;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Desktop.Services;
using EngineeringWorkspace.Domain.Customers;

namespace EngineeringWorkspace.Desktop.Pages.Experience;

/// <summary>A line of a group card: what users experience in one area, and how frameworks change it.</summary>
public sealed record ExperienceLine(string Solution, string Topic, string Choice, string UserExperience, string Source, string? Change, bool IsChanged);

/// <summary>A conflict shown on a group card.</summary>
public sealed record ConflictLine(string Framework, string Topic, string Choice, string Reason, string Fix, bool IsConflict);

/// <summary>One user group's card on the Experience Map.</summary>
public sealed record GroupCard(
    GroupExperience Experience,
    IReadOnlyList<ExperienceLine> Lines,
    IReadOnlyList<ConflictLine> Conflicts,
    IReadOnlyList<ChartSlice> SolutionFriction)
{
    public string Name => Experience.Name;
    public string Subtitle => $"{Experience.Classification}{(Experience.UserCount is { } count ? $" · {count:N0} users" : string.Empty)} · {Experience.Profile.Name} profile";
    public string SecurityText => $"{Experience.Security:0} → {Experience.SecurityWithFrameworks:0}";
    public string FrictionText => $"{Experience.Friction:0} → {Experience.FrictionWithFrameworks:0}";
    public string ConflictText => Experience.Conflicts == 0 && Experience.Weaknesses == 0
        ? "Meets the selected frameworks."
        : $"{Experience.Conflicts} conflict(s), {Experience.Weaknesses} weakness(es) with the selected frameworks.";
    public bool HasConflicts => Conflicts.Count > 0;
    public double Security => Experience.Security;
    public double Friction => Experience.Friction;
}

/// <summary>
/// Reporting > Experience Map: every user group of the customer, the experience it has (in plain language, by
/// solution), and — with frameworks such as NIST or CMMC switched on — what has to change, where experiences conflict
/// and how much friction each group gains. Helps a customer see why one group's experience can't meet another need.
/// </summary>
public sealed partial class ExperienceMapViewModel(ICustomerContextService context, ICustomerProfileStore store, ExportCoordinator export)
    : CustomerScopedPageViewModel(context, store)
{
    private bool _loading;
    private IReadOnlyList<GroupExperience> _experiences = [];

    public ObservableCollection<FrameworkToggle> Frameworks { get; } = [];
    public ObservableCollection<GroupCard> Groups { get; } = [];
    public ObservableCollection<HeatCell> FrictionNow { get; } = [];
    public ObservableCollection<HeatCell> FrictionWithFrameworks { get; } = [];
    public ObservableCollection<ChartSlice> ConflictsByFramework { get; } = [];
    public ObservableCollection<ChartSlice> FrictionChange { get; } = [];
    public ObservableCollection<ChartSlice> UsersByGroup { get; } = [];

    [ObservableProperty] private bool _strict;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _detailTitle = "Click a cell or a chart to see the detail.";
    [ObservableProperty] private string _detailText = string.Empty;
    [ObservableProperty] private int _totalConflicts;
    [ObservableProperty] private string _frictionDelta = string.Empty;
    [ObservableProperty] private string _securityDelta = string.Empty;
    [ObservableProperty] private int _groupCount;

    partial void OnStrictChanged(bool value) => Evaluate();

    protected override Task OnCustomerChangedAsync(CustomerProfile? customer)
    {
        _loading = true;
        Frameworks.Clear();
        if (customer is not null)
        {
            var targets = (customer.WorkspaceSettings.GetValueOrDefault(ExperienceConfigurationViewModel.FrameworksSetting) ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var framework in ExperienceCatalog.Frameworks.Where(item => !item.IsRetired || targets.Contains(item.Code, StringComparer.OrdinalIgnoreCase)))
                Frameworks.Add(new FrameworkToggle(framework, Evaluate) { IsSelected = targets.Contains(framework.Code, StringComparer.OrdinalIgnoreCase) });
        }

        _loading = false;
        Evaluate();
        return Task.CompletedTask;
    }

    private void Evaluate()
    {
        if (_loading) return;
        Groups.Clear();
        FrictionNow.Clear();
        FrictionWithFrameworks.Clear();
        ConflictsByFramework.Clear();
        FrictionChange.Clear();
        UsersByGroup.Clear();
        if (SelectedCustomer is not { } customer)
        {
            StatusMessage = "Choose a customer.";
            return;
        }

        var selected = Frameworks.Where(toggle => toggle.IsSelected).Select(toggle => toggle.Framework.Code).ToArray();
        _experiences = ExperienceEngine.EvaluateAll(customer, selected, Strict);
        GroupCount = customer.Groups.Count;
        StatusMessage = customer.Groups.Count == 0
            ? "No user groups yet: only All Users is shown. Add groups (up to 25) on Workspace > Customer Profiles and tailor them on Assessment > Experience Configuration."
            : null;
        foreach (var experience in _experiences)
        {
            var after = experience.WithFrameworks.ToDictionary(answer => answer.Question.Code);
            var lines = experience.Answers.Select(answer =>
            {
                var changed = after[answer.Question.Code].Option.Code != answer.Option.Code;
                return new ExperienceLine(answer.Question.Solution, answer.Question.Topic, answer.Option.Label, answer.Option.UserExperience, answer.Source,
                    changed ? $"With the frameworks: {after[answer.Question.Code].Option.Label} — {after[answer.Question.Code].Option.UserExperience}" : null, changed);
            }).ToArray();
            var conflicts = experience.Findings.Select(finding => new ConflictLine(finding.Framework.Name, finding.Question.Topic, finding.Option.Label, finding.Reason,
                finding.CompliantOption is { } fix ? $"Change to \"{fix.Label}\" (friction {finding.Option.Friction} → {fix.Friction})" : "No option fully meets it; document a compensating control.",
                finding.Effect == FrameworkEffect.Conflicts)).ToArray();
            var friction = experience.Solutions.Select(score => new ChartSlice(Short(score.Solution), Math.Round(score.Friction), null,
                $"Security {score.Security:0}, friction {score.Friction:0}", null,
                experience.Answers.Where(answer => answer.Question.Solution == score.Solution)
                    .Select(answer => new ChartSlice(answer.Question.Topic, answer.Option.Friction, null, $"{answer.Option.Label}: {answer.Option.UserExperience}")).ToArray())).ToArray();
            Groups.Add(new GroupCard(experience, lines, conflicts, friction));
            foreach (var (now, later) in experience.Solutions.Zip(experience.SolutionsWithFrameworks))
            {
                FrictionNow.Add(new HeatCell(experience.Name, Short(now.Solution), now.Friction / 100, now.Friction.ToString("0", CultureInfo.CurrentCulture),
                    Detail(experience, now.Solution, false), (experience.Key, now.Solution, false)));
                FrictionWithFrameworks.Add(new HeatCell(experience.Name, Short(later.Solution), later.Friction / 100,
                    later.Friction.ToString("0", CultureInfo.CurrentCulture) + (Math.Abs(later.Friction - now.Friction) >= 1 ? $" ({later.Friction - now.Friction:+0;-0})" : string.Empty),
                    Detail(experience, later.Solution, true), (experience.Key, later.Solution, true)));
            }

            FrictionChange.Add(new ChartSlice(experience.Name, Math.Round(experience.FrictionWithFrameworks - experience.Friction, 1), null,
                $"Friction {experience.Friction:0} → {experience.FrictionWithFrameworks:0}; security {experience.Security:0} → {experience.SecurityWithFrameworks:0}"));
            if (experience.UserCount is > 0) UsersByGroup.Add(new ChartSlice(experience.Name, experience.UserCount.Value, null, experience.Classification));
        }

        foreach (var framework in _experiences.SelectMany(experience => experience.Findings).Where(finding => finding.Effect == FrameworkEffect.Conflicts)
                     .GroupBy(finding => finding.Framework.Name))
            ConflictsByFramework.Add(new ChartSlice(framework.Key, framework.Count(), null, $"{framework.Count()} conflicting choice(s)", null,
                framework.GroupBy(finding => finding.Question.Topic).Select(topic => new ChartSlice(topic.Key, topic.Count(), null,
                    string.Join("\n", topic.Select(finding => $"{finding.Option.Label}: {finding.Reason}").Distinct()))).ToArray()));
        TotalConflicts = _experiences.Sum(experience => experience.Conflicts);
        var users = _experiences.Skip(1).Where(experience => experience.UserCount is > 0).ToArray();
        FrictionDelta = $"{_experiences.Average(experience => experience.Friction):0} → {_experiences.Average(experience => experience.FrictionWithFrameworks):0}";
        SecurityDelta = $"{_experiences.Average(experience => experience.Security):0} → {_experiences.Average(experience => experience.SecurityWithFrameworks):0}";
        Summary = selected.Length == 0
            ? $"{_experiences.Count} experience(s). Switch on frameworks to see what they change."
            : $"{_experiences.Count} experience(s) checked against {selected.Length} framework(s): {TotalConflicts} conflicting choice(s)" +
              (users.Length > 0 ? $"; {users.Where(experience => experience.Conflicts > 0).Sum(experience => experience.UserCount ?? 0):N0} users in groups with conflicts." : ".");
    }

    private static string Short(string solution) => solution.Split(" (")[0];

    private static string Detail(GroupExperience experience, string solution, bool withFrameworks) =>
        string.Join("\n", (withFrameworks ? experience.WithFrameworks : experience.Answers).Where(answer => answer.Question.Solution == solution)
            .Select(answer => $"{answer.Question.Topic}: {answer.Option.Label} (friction {answer.Option.Friction}, security {answer.Option.Security})"));

    [RelayCommand]
    private void ShowDetail(object? item)
    {
        switch (item)
        {
            case HeatCell cell:
                DetailTitle = $"{cell.Row} — {cell.Column}";
                DetailText = cell.Detail ?? cell.Text;
                break;
            case ChartSlice slice:
                DetailTitle = slice.Label;
                DetailText = slice.Detail ?? slice.Value.ToString("0.#", CultureInfo.CurrentCulture);
                break;
        }
    }

    [RelayCommand]
    private async Task ExportAsync(object? format)
    {
        if (SelectedCustomer is not { } customer || _experiences.Count == 0)
        {
            StatusMessage = "There is nothing to export.";
            return;
        }

        var frameworks = string.Join(", ", Frameworks.Where(toggle => toggle.IsSelected).Select(toggle => toggle.Name));
        var document = new ExportDocument("Experience Map", customer.Name, DateTimeOffset.UtcNow,
            [new("Frameworks", frameworks.Length == 0 ? "None selected" : frameworks), new("Groups", _experiences.Count.ToString(CultureInfo.InvariantCulture)),
             new("Conflicts", TotalConflicts.ToString(CultureInfo.InvariantCulture)), new("Average friction", FrictionDelta), new("Average security", SecurityDelta)],
            [
                new ExportTable("Groups", ["Group", "Classification", "Users", "Profile", "Security", "Friction", "Security with frameworks", "Friction with frameworks", "Conflicts"],
                    _experiences.Select(experience => (IReadOnlyList<string?>)
                    [
                        experience.Name, experience.Classification, experience.UserCount?.ToString(CultureInfo.InvariantCulture), experience.Profile.Name,
                        experience.Security.ToString("0", CultureInfo.InvariantCulture), experience.Friction.ToString("0", CultureInfo.InvariantCulture),
                        experience.SecurityWithFrameworks.ToString("0", CultureInfo.InvariantCulture), experience.FrictionWithFrameworks.ToString("0", CultureInfo.InvariantCulture),
                        experience.Conflicts.ToString(CultureInfo.InvariantCulture)
                    ]).ToArray()),
                new ExportTable("Experience by group", ["Group", "Solution", "Topic", "Choice", "What users experience", "Source", "With frameworks"],
                    Groups.SelectMany(group => group.Lines.Select(line => (IReadOnlyList<string?>)[group.Name, line.Solution, line.Topic, line.Choice, line.UserExperience, line.Source, line.Change]))
                        .ToArray()),
                new ExportTable("Conflicts", ["Group", "Framework", "Topic", "Choice", "Why", "Fix"],
                    Groups.SelectMany(group => group.Conflicts.Select(conflict => (IReadOnlyList<string?>)[group.Name, conflict.Framework, conflict.Topic, conflict.Choice, conflict.Reason, conflict.Fix]))
                        .ToArray())
            ]);
        try
        {
            StatusMessage = await export.ExportAsync(document, ExportCoordinator.Parse(format)) ?? StatusMessage;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"The export could not be written: {exception.Message}";
        }
    }
}
