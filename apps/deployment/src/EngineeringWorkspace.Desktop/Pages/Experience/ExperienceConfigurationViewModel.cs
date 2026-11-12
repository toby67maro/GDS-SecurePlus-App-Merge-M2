using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Experience;
using EngineeringWorkspace.Desktop.Controls;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Domain.Customers;

namespace EngineeringWorkspace.Desktop.Pages.Experience;

/// <summary>
/// Assessment > Experience Configuration: the experience each user group should have, by Microsoft solution, as
/// plain-language choices (MFA prompts, passwordless, blocked locations, personal devices...). Answers are saved in the
/// customer profile (All Users answers, and per-group overrides). Selected frameworks highlight conflicting choices.
/// </summary>
public sealed partial class ExperienceConfigurationViewModel(ICustomerContextService context, ICustomerProfileStore store) : CustomerScopedPageViewModel(context, store)
{
    public const string FrameworksSetting = "Experience.Frameworks";
    private bool _loading;

    public ObservableCollection<ExperienceScope> Scopes { get; } = [];
    public ObservableCollection<ExperienceSolutionSection> Sections { get; } = [];
    public ObservableCollection<FrameworkToggle> Frameworks { get; } = [];
    public ObservableCollection<ChartSlice> SolutionFriction { get; } = [];
    public IReadOnlyList<ExperienceProfile> Profiles => ExperienceCatalog.Profiles;

    [ObservableProperty] private ExperienceScope? _selectedScope;
    [ObservableProperty] private ExperienceProfile? _selectedProfile;
    [ObservableProperty] private double _securityScore;
    [ObservableProperty] private double _frictionScore;
    [ObservableProperty] private string _scoreText = string.Empty;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _scopeDetail = string.Empty;
    [ObservableProperty] private int _conflictCount;

    protected override Task OnCustomerChangedAsync(CustomerProfile? customer)
    {
        _loading = true;
        Scopes.Clear();
        Frameworks.Clear();
        if (customer is null)
        {
            Sections.Clear();
            StatusMessage = "Choose a customer.";
            _loading = false;
            return Task.CompletedTask;
        }

        var targets = (customer.WorkspaceSettings.GetValueOrDefault(FrameworksSetting) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var framework in ExperienceCatalog.Frameworks.Where(item => !item.IsRetired || targets.Contains(item.Code, StringComparer.OrdinalIgnoreCase)))
            Frameworks.Add(new FrameworkToggle(framework, OnFrameworksChanged) { IsSelected = targets.Contains(framework.Code, StringComparer.OrdinalIgnoreCase) });
        Scopes.Add(new ExperienceScope(ExperienceCatalog.AllUsersKey, "All Users (default)", "Applies to everyone unless a group overrides it."));
        foreach (var group in customer.Groups)
            Scopes.Add(new ExperienceScope(group.Key, group.Name,
                $"{ExperienceCatalog.Classification(group.Classification).Name}{(group.UserCount is { } count ? $", {count:N0} users" : string.Empty)} — starts from the {ExperienceCatalog.Profile(group.ExperienceProfile ?? ExperienceCatalog.Classification(group.Classification).DefaultProfile).Name} profile."));
        StatusMessage = customer.Groups.Count == 0
            ? "This customer has no user groups yet. Add them on Workspace > Customer Profiles (up to 25), then tailor each group's experience here."
            : null;
        _loading = false;
        SelectedScope = Scopes[0];
        return Task.CompletedTask;
    }

    partial void OnSelectedScopeChanged(ExperienceScope? value) => Build();

    private UserGroup? Group => SelectedCustomer?.Groups.FirstOrDefault(group => group.Key == SelectedScope?.Key);

    private void Build()
    {
        Sections.Clear();
        if (SelectedCustomer is not { } customer || SelectedScope is not { } scope) return;
        _loading = true;
        var group = Group;
        var resolved = ExperienceEngine.Resolve(customer, group);
        var explicitAnswers = group is null ? customer.DefaultExperienceAnswers : group.EffectiveAnswers;
        SelectedProfile = group is null ? null : ExperienceCatalog.Profile(group.ExperienceProfile ?? ExperienceCatalog.Classification(group.Classification).DefaultProfile);
        ScopeDetail = scope.Detail;
        foreach (var solution in ExperienceCatalog.Solutions)
        {
            var items = new ObservableCollection<ExperienceQuestionItem>();
            foreach (var answer in resolved.Where(answer => answer.Question.Solution == solution))
            {
                var item = new ExperienceQuestionItem(answer.Question, scope.Key, _ => OnAnswerChanged());
                item.Load(answer.Option, answer.Source, explicitAnswers.ContainsKey(answer.Question.Code));
                items.Add(item);
            }

            if (items.Count > 0) Sections.Add(new ExperienceSolutionSection(solution, items));
        }

        _loading = false;
        IsDirty = false;
        Recalculate();
    }

    private void OnAnswerChanged()
    {
        if (_loading) return;
        IsDirty = true;
        Recalculate();
    }

    private void OnFrameworksChanged()
    {
        if (_loading) return;
        IsDirty = true;
        Recalculate();
    }

    private void Recalculate()
    {
        var questions = Sections.SelectMany(section => section.Questions).ToArray();
        var frameworks = Frameworks.Where(toggle => toggle.IsSelected).Select(toggle => toggle.Framework).ToArray();
        foreach (var question in questions) question.ShowFrameworks(frameworks);
        if (questions.Length == 0) return;
        SecurityScore = Math.Round(questions.Average(question => question.Selected.Security));
        FrictionScore = Math.Round(questions.Average(question => question.Selected.Friction));
        ConflictCount = questions.Count(question => frameworks.Any(framework => question.Selected.EffectOn(framework.Code) == FrameworkEffect.Conflicts));
        ScoreText = $"Security {SecurityScore:0} / 100 · Friction {FrictionScore:0} / 100" + (frameworks.Length > 0 ? $" · {ConflictCount} conflict(s) with the selected frameworks" : string.Empty);
        SolutionFriction.Clear();
        foreach (var section in Sections)
            SolutionFriction.Add(new ChartSlice(section.Name.Split(" (")[0], Math.Round(section.Questions.Average(question => question.Selected.Friction)), null,
                string.Join("\n", section.Questions.Select(question => $"{question.Topic}: {question.Selected.Label} (friction {question.Selected.Friction})")),
                null, section.Questions.Select(question => new ChartSlice(question.Topic, question.Selected.Friction, null, question.Selected.UserExperience)).ToArray()));
    }

    /// <summary>Imports the customer configuration JSON produced from the GDS EaaS discovery and design-workshop forms.</summary>
    public async Task ImportFormResultsAsync(string path)
    {
        if (SelectedCustomer is not { } selected)
        {
            StatusMessage = "Choose a customer first.";
            return;
        }

        try
        {
            var customer = await CustomerStore.LoadAsync(selected.Id) ?? selected;
            var result = EaasFormImport.Import(customer, await File.ReadAllTextAsync(path));
            await CustomerStore.SaveAsync(result.Profile);
            if (CustomerContext.CurrentCustomer?.Id == result.Profile.Id) CustomerContext.Refresh(result.Profile);
            var index = CustomerOptions.IndexOf(selected);
            if (index >= 0) CustomerOptions[index] = result.Profile;
            SelectedCustomer = result.Profile;
            StatusMessage = $"Imported the EaaS form results into {result.Profile.Name}:\n• " + string.Join("\n• ", result.Notes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            StatusMessage = $"The form results could not be imported: {exception.Message}";
        }
    }

    [RelayCommand]
    private void ApplyProfile()
    {
        if (SelectedProfile is not { } profile) return;
        foreach (var question in Sections.SelectMany(section => section.Questions))
            if (profile.Answers.TryGetValue(question.Question.Code, out var code))
                question.Load(question.Question.Option(code), $"{profile.Name} profile", Group is null);
        IsDirty = true;
        Recalculate();
        StatusMessage = $"The {profile.Name} profile is applied to {SelectedScope?.Name}. Save to keep it.";
    }

    [RelayCommand]
    private void ResetToInherited()
    {
        if (SelectedCustomer is not { } customer) return;
        var group = Group;
        var inherited = ExperienceEngine.Resolve(customer, group is null ? null : group with { Answers = new Dictionary<string, string>() });
        foreach (var question in Sections.SelectMany(section => section.Questions))
        {
            var answer = inherited.First(item => item.Question.Code == question.Question.Code);
            question.Load(answer.Option, answer.Source, false);
        }

        IsDirty = true;
        Recalculate();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (SelectedCustomer is not { } selected) return;
        var customer = await CustomerStore.LoadAsync(selected.Id) ?? selected;
        var answers = Sections.SelectMany(section => section.Questions).Where(question => question.IsExplicit)
            .ToDictionary(question => question.Question.Code, question => question.Selected.Code, StringComparer.OrdinalIgnoreCase);
        var settings = new Dictionary<string, string>(customer.WorkspaceSettings, StringComparer.OrdinalIgnoreCase)
        {
            [FrameworksSetting] = string.Join(",", Frameworks.Where(toggle => toggle.IsSelected).Select(toggle => toggle.Framework.Code))
        };
        CustomerProfile updated;
        if (Group is { } group)
        {
            var groups = customer.Groups.Select(item => item.Key == group.Key
                ? item with { Answers = answers, ExperienceProfile = SelectedProfile?.Code ?? item.ExperienceProfile }
                : item).ToArray();
            updated = customer with { UserGroups = groups, WorkspaceSettings = settings };
        }
        else
        {
            updated = customer with { ExperienceAnswers = answers, WorkspaceSettings = settings };
        }

        await CustomerStore.SaveAsync(updated);
        if (CustomerContext.CurrentCustomer?.Id == updated.Id) CustomerContext.Refresh(updated);
        var index = CustomerOptions.IndexOf(selected);
        var scopeKey = SelectedScope?.Key;
        if (index >= 0) CustomerOptions[index] = updated;
        SelectedCustomer = updated;
        SelectedScope = Scopes.FirstOrDefault(scope => scope.Key == scopeKey) ?? Scopes.FirstOrDefault();
        IsDirty = false;
        StatusMessage = $"The experience of {SelectedScope?.Name} is saved for {updated.Name}.";
    }
}
