namespace EngineeringWorkspace.Application.Experience;

/// <summary>How an experience choice relates to a framework's requirements.</summary>
public enum FrameworkEffect
{
    Neutral,
    Supports,

    /// <summary>Allowed, but auditors will ask for compensating controls or it misses a recommendation.</summary>
    Weakens,

    /// <summary>Does not meet the framework's requirement.</summary>
    Conflicts
}

/// <summary>A compliance target. A retired framework is hidden from new choices; saved selections and notes that use it are kept read-only.</summary>
public sealed record ExperienceFramework(string Code, string Name, string Description, bool IsRetired = false);

public sealed record FrameworkNote(string Framework, FrameworkEffect Effect, string Reason);

/// <summary>
/// One answer to an experience question, in plain language. <see cref="Security"/> and <see cref="Friction"/> are 0-100
/// (higher Security = better protected; higher Friction = more interruptions for the user).
/// </summary>
public sealed record ExperienceOption(string Code, string Label, string UserExperience, int Security, int Friction, IReadOnlyList<FrameworkNote> Frameworks, string? Configuration = null)
{
    public FrameworkNote? For(string framework) => Frameworks.FirstOrDefault(note => string.Equals(note.Framework, framework, StringComparison.OrdinalIgnoreCase));
    public FrameworkEffect EffectOn(string framework) => For(framework)?.Effect ?? FrameworkEffect.Neutral;
}

/// <summary>A question a CTO or IT lead answers (no technical terms), for one Microsoft solution.</summary>
public sealed record ExperienceQuestion(string Code, string Solution, string Topic, string Question, string WhyItMatters, IReadOnlyList<ExperienceOption> Options, string DefaultOption)
{
    public ExperienceOption Option(string? code) =>
        Options.FirstOrDefault(option => string.Equals(option.Code, code, StringComparison.OrdinalIgnoreCase)) ?? Options.First(option => option.Code == DefaultOption);
}

/// <summary>A starting experience (preset answers) for a kind of user.</summary>
public sealed record ExperienceProfile(string Code, string Name, string Description, IReadOnlyDictionary<string, string> Answers);

/// <summary>A user classification (Executive, Frontline Worker...) and the experience profile it starts from.</summary>
public sealed record UserClassification(string Code, string Name, string Description, string DefaultProfile);

/// <summary>One resolved answer for a group: the option and where it came from (Group, All Users, Profile or Default).</summary>
public sealed record ExperienceAnswer(ExperienceQuestion Question, ExperienceOption Option, string Source);

/// <summary>An answer that does not meet (or weakens) a selected framework, and the closest option that does.</summary>
public sealed record FrameworkFinding(ExperienceQuestion Question, ExperienceOption Option, ExperienceFramework Framework, FrameworkEffect Effect, string Reason,
    ExperienceOption? CompliantOption);

/// <summary>A solution's scores for one group.</summary>
public sealed record SolutionScore(string Solution, double Security, double Friction, int Conflicts);

/// <summary>The experience of one group (or All Users), before and after applying the selected frameworks.</summary>
public sealed record GroupExperience(
    string Key,
    string Name,
    string Classification,
    int? UserCount,
    ExperienceProfile Profile,
    IReadOnlyList<ExperienceAnswer> Answers,
    IReadOnlyList<FrameworkFinding> Findings,
    IReadOnlyList<ExperienceAnswer> WithFrameworks,
    IReadOnlyList<SolutionScore> Solutions,
    IReadOnlyList<SolutionScore> SolutionsWithFrameworks)
{
    public double Security => Answers.Count == 0 ? 0 : Answers.Average(answer => answer.Option.Security);
    public double Friction => Answers.Count == 0 ? 0 : Answers.Average(answer => answer.Option.Friction);
    public double SecurityWithFrameworks => WithFrameworks.Count == 0 ? 0 : WithFrameworks.Average(answer => answer.Option.Security);
    public double FrictionWithFrameworks => WithFrameworks.Count == 0 ? 0 : WithFrameworks.Average(answer => answer.Option.Friction);
    public int Conflicts => Findings.Count(finding => finding.Effect == FrameworkEffect.Conflicts);
    public int Weaknesses => Findings.Count(finding => finding.Effect == FrameworkEffect.Weakens);
}
