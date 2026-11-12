using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EngineeringWorkspace.Application.Experience;

namespace EngineeringWorkspace.Desktop.Pages.Experience;

/// <summary>A selectable framework chip.</summary>
public sealed partial class FrameworkToggle(ExperienceFramework framework, Action changed) : ObservableObject
{
    public ExperienceFramework Framework { get; } = framework;
    public string Name => Framework.Name;
    public string Description => Framework.Description;

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => changed();
}

/// <summary>One option of a question (a radio button).</summary>
public sealed partial class ExperienceOptionItem(ExperienceQuestionItem owner, ExperienceOption option) : ObservableObject
{
    public ExperienceOption Option { get; } = option;
    public string Label => Option.Label;
    public string UserExperience => Option.UserExperience;
    public string Scores => $"Security {Option.Security} · Friction {Option.Friction}";
    public string? Configuration => Option.Configuration;
    public bool HasConfiguration => !string.IsNullOrWhiteSpace(Option.Configuration);
    public string GroupName => owner.GroupName;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _frameworkText = string.Empty;
    [ObservableProperty] private bool _hasConflict;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) owner.Select(this);
    }
}

/// <summary>One question with its options, the chosen one and where the choice comes from.</summary>
public sealed partial class ExperienceQuestionItem : ObservableObject
{
    private readonly Action<ExperienceQuestionItem> _changed;
    private bool _loading;

    public ExperienceQuestionItem(ExperienceQuestion question, string scope, Action<ExperienceQuestionItem> changed)
    {
        Question = question;
        GroupName = $"{scope}:{question.Code}";
        _changed = changed;
        Options = new ObservableCollection<ExperienceOptionItem>(question.Options.Select(option => new ExperienceOptionItem(this, option)));
    }

    public ExperienceQuestion Question { get; }
    public string GroupName { get; }
    public string Text => Question.Question;
    public string Topic => Question.Topic;
    public string Why => Question.WhyItMatters;
    public ObservableCollection<ExperienceOptionItem> Options { get; }

    [ObservableProperty] private string _source = string.Empty;
    [ObservableProperty] private bool _isExplicit;
    [ObservableProperty] private string _warning = string.Empty;

    public ExperienceOption Selected => Options.FirstOrDefault(option => option.IsSelected)?.Option ?? Question.Option(Question.DefaultOption);

    public void Load(ExperienceOption option, string source, bool isExplicit)
    {
        _loading = true;
        foreach (var item in Options) item.IsSelected = item.Option.Code == option.Code;
        Source = source;
        IsExplicit = isExplicit;
        _loading = false;
    }

    internal void Select(ExperienceOptionItem item)
    {
        foreach (var other in Options.Where(other => other != item)) other.IsSelected = false;
        if (_loading) return;
        IsExplicit = true;
        Source = "Chosen here";
        _changed(this);
    }

    /// <summary>Marks options that conflict with the selected frameworks.</summary>
    public void ShowFrameworks(IReadOnlyCollection<ExperienceFramework> frameworks)
    {
        foreach (var option in Options)
        {
            var notes = frameworks.Select(framework => (framework, note: option.Option.For(framework.Code)))
                .Where(pair => pair.note is { Effect: FrameworkEffect.Conflicts or FrameworkEffect.Weakens }).ToArray();
            option.HasConflict = notes.Any(pair => pair.note!.Effect == FrameworkEffect.Conflicts);
            option.FrameworkText = string.Join("\n", notes.Select(pair => $"{(pair.note!.Effect == FrameworkEffect.Conflicts ? "✖ Conflicts with" : "⚠ Weakens")} {pair.framework.Name}: {pair.note.Reason}"));
        }

        var selected = Options.FirstOrDefault(option => option.IsSelected);
        Warning = selected is { FrameworkText.Length: > 0 } ? selected.FrameworkText : string.Empty;
    }
}

public sealed record ExperienceSolutionSection(string Name, ObservableCollection<ExperienceQuestionItem> Questions);

/// <summary>An experience scope: All Users or one user group.</summary>
public sealed record ExperienceScope(string Key, string Name, string Detail)
{
    public override string ToString() => Name;
}
