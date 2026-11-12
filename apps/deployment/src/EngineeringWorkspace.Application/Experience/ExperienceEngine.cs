using EngineeringWorkspace.Domain.Customers;

namespace EngineeringWorkspace.Application.Experience;

/// <summary>
/// Works out each user group's experience and what selected compliance frameworks would change.
/// Answer precedence: the group's own answer, then its experience profile, then the customer's All Users answer, then
/// the GDS Balanced profile, then the question's default. With frameworks selected, every answer that conflicts with one
/// (or, when <c>strict</c>, weakens one) is replaced by the least disruptive option that satisfies all of them.
/// </summary>
public static class ExperienceEngine
{
    public static IReadOnlyList<ExperienceAnswer> Resolve(CustomerProfile customer, UserGroup? group)
    {
        var balanced = ExperienceCatalog.Profile("BALANCED");
        var profile = group?.ExperienceProfile is { Length: > 0 } code ? ExperienceCatalog.Profile(code) : null;
        var answers = new List<ExperienceAnswer>();
        foreach (var question in ExperienceCatalog.Questions)
        {
            if (group is not null && group.EffectiveAnswers.TryGetValue(question.Code, out var own) && Has(question, own))
                answers.Add(new(question, question.Option(own), "Group"));
            else if (profile is not null && profile.Answers.TryGetValue(question.Code, out var preset))
                answers.Add(new(question, question.Option(preset), $"{profile.Name} profile"));
            else if (customer.DefaultExperienceAnswers.TryGetValue(question.Code, out var all) && Has(question, all))
                answers.Add(new(question, question.Option(all), "All Users"));
            else if (balanced.Answers.TryGetValue(question.Code, out var recommended))
                answers.Add(new(question, question.Option(recommended), "GDS recommended"));
            else
                answers.Add(new(question, question.Option(question.DefaultOption), "Default"));
        }

        return answers;
    }

    /// <summary>Options the engine may pick on its own when adjusting for frameworks. Shared-device QR code sign-in is single-factor and only ever chosen by an engineer.</summary>
    private static bool IsAutomatic(ExperienceOption option) => option.Code != ExperienceCatalog.SharedDeviceQrCode;

    private static bool Has(ExperienceQuestion question, string option) => question.Options.Any(candidate => string.Equals(candidate.Code, option, StringComparison.OrdinalIgnoreCase));

    public static GroupExperience Evaluate(CustomerProfile customer, UserGroup? group, IReadOnlyCollection<string> frameworks, bool strict = false)
    {
        var selected = frameworks.Select(ExperienceCatalog.Framework).OfType<ExperienceFramework>().ToArray();
        var answers = Resolve(customer, group);
        var findings = new List<FrameworkFinding>();
        var adjusted = new List<ExperienceAnswer>();
        bool Fails(ExperienceOption option, ExperienceFramework framework) =>
            option.EffectOn(framework.Code) == FrameworkEffect.Conflicts || (strict && option.EffectOn(framework.Code) == FrameworkEffect.Weakens);

        foreach (var answer in answers)
        {
            var compliant = answer.Question.Options.Where(option => IsAutomatic(option) && selected.All(framework => !Fails(option, framework)))
                .OrderBy(option => option.Friction).ThenByDescending(option => option.Security).FirstOrDefault();
            foreach (var framework in selected)
            {
                var effect = answer.Option.EffectOn(framework.Code);
                if (effect is FrameworkEffect.Conflicts or FrameworkEffect.Weakens)
                    findings.Add(new FrameworkFinding(answer.Question, answer.Option, framework, effect, answer.Option.For(framework.Code)!.Reason,
                        answer.Question.Options.Where(option => IsAutomatic(option) && !Fails(option, framework) && option.EffectOn(framework.Code) != FrameworkEffect.Weakens)
                            .OrderBy(option => option.Friction).ThenByDescending(option => option.Security).FirstOrDefault()));
            }

            var needsChange = selected.Any(framework => Fails(answer.Option, framework));
            adjusted.Add(needsChange && compliant is not null && compliant.Code != answer.Option.Code ? answer with { Option = compliant, Source = "Required by frameworks" } : answer);
        }

        var profile = group?.ExperienceProfile is { Length: > 0 } code ? ExperienceCatalog.Profile(code) : ExperienceCatalog.Profile("BALANCED");
        return new GroupExperience(group?.Key ?? ExperienceCatalog.AllUsersKey, group?.Name ?? "All Users", group is null ? "Everyone" : ExperienceCatalog.Classification(group.Classification).Name,
            group?.UserCount, profile, answers, findings, adjusted, Scores(answers, findings), Scores(adjusted, []));
    }

    private static IReadOnlyList<SolutionScore> Scores(IReadOnlyList<ExperienceAnswer> answers, IReadOnlyList<FrameworkFinding> findings) =>
        answers.GroupBy(answer => answer.Question.Solution)
            .Select(group => new SolutionScore(group.Key, group.Average(answer => answer.Option.Security), group.Average(answer => answer.Option.Friction),
                findings.Count(finding => finding.Question.Solution == group.Key && finding.Effect == FrameworkEffect.Conflicts)))
            .OrderBy(score => ExperienceCatalog.Solutions.ToList().IndexOf(score.Solution)).ToArray();

    /// <summary>All Users first, then every group.</summary>
    public static IReadOnlyList<GroupExperience> EvaluateAll(CustomerProfile customer, IReadOnlyCollection<string> frameworks, bool strict = false) =>
        [Evaluate(customer, null, frameworks, strict), .. customer.Groups.Select(group => Evaluate(customer, group, frameworks, strict))];

    /// <summary>Plain-language description of an answer set by solution, for reports and the SOW.</summary>
    public static string Describe(IReadOnlyList<ExperienceAnswer> answers) =>
        string.Join("\n", answers.GroupBy(answer => answer.Question.Solution).Select(group =>
            $"{group.Key}:\n" + string.Join("\n", group.Select(answer => $"  • {answer.Question.Topic}: {answer.Option.Label}. {answer.Option.UserExperience}"))));
}
