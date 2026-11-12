using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EngineeringWorkspace.Desktop.Controls;

/// <summary>One choice of a <see cref="MultiSelectFilter"/>.</summary>
public sealed partial class SelectableOption(string value, string label) : ObservableObject
{
    public string Value { get; } = value;
    public string Label { get; } = label;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// A checkbox drop-down ("Select All" plus one checkbox per option), rendered by the MultiSelectFilter DataTemplate.
/// As a filter (<see cref="EmptyMeansAll"/> = true) nothing selected means "(All)". As a picker (for example
/// "Which Days") the selection is used as is.
/// </summary>
public sealed partial class MultiSelectFilter : ObservableObject
{
    private bool _bulk;

    public MultiSelectFilter(string label, bool emptyMeansAll = true, IEnumerable<(string Value, string Label)>? options = null)
    {
        Label = label;
        EmptyMeansAll = emptyMeansAll;
        if (options is not null) SetOptions(options);
    }

    public string Label { get; }
    public bool EmptyMeansAll { get; }
    public ObservableCollection<SelectableOption> Options { get; } = [];

    [ObservableProperty]
    private bool _isOpen;

    /// <summary>Raised whenever the selection changes.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<string> SelectedValues => Options.Where(option => option.IsSelected).Select(option => option.Value).ToArray();

    /// <summary>True when the selection narrows the results (some, but not all, options selected).</summary>
    public bool IsActive
    {
        get
        {
            var selected = Options.Count(option => option.IsSelected);
            return EmptyMeansAll ? selected > 0 && selected < Options.Count : true;
        }
    }

    /// <summary>Text shown on the closed drop-down.</summary>
    public string Summary
    {
        get
        {
            var selected = Options.Where(option => option.IsSelected).ToArray();
            if (selected.Length == 0) return EmptyMeansAll ? "(All)" : "None";
            if (selected.Length == Options.Count) return EmptyMeansAll ? "(All)" : "All";
            return selected.Length <= 3 ? string.Join(", ", selected.Select(option => option.Label)) : $"{selected.Length} selected";
        }
    }

    /// <summary>State of "Select All": true when all are selected, false when none, null otherwise.</summary>
    public bool? AllSelected
    {
        get
        {
            var selected = Options.Count(option => option.IsSelected);
            return selected == 0 ? false : selected == Options.Count ? true : null;
        }
        set => SetAll(value == true);
    }

    public bool Matches(string? value) => !IsActive || (value is not null && Options.Any(option => option.IsSelected && string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase)));

    public bool MatchesAny(IEnumerable<string> values)
    {
        if (!IsActive) return true;
        var selected = SelectedValues.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return values.Any(selected.Contains);
    }

    /// <summary>Replaces the options; values that were selected stay selected.</summary>
    public void SetOptions(IEnumerable<(string Value, string Label)> options, IEnumerable<string>? select = null)
    {
        var keep = (select ?? SelectedValues).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _bulk = true;
        foreach (var option in Options) option.PropertyChanged -= OnOptionChanged;
        Options.Clear();
        foreach (var (value, label) in options)
        {
            var option = new SelectableOption(value, label) { IsSelected = keep.Contains(value) };
            option.PropertyChanged += OnOptionChanged;
            Options.Add(option);
        }

        _bulk = false;
        Notify();
    }

    public void Select(IEnumerable<string> values)
    {
        var set = values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _bulk = true;
        foreach (var option in Options) option.IsSelected = set.Contains(option.Value);
        _bulk = false;
        Notify();
    }

    public void SetAll(bool selected)
    {
        _bulk = true;
        foreach (var option in Options) option.IsSelected = selected;
        _bulk = false;
        Notify();
    }

    /// <summary>Back to the default: "(All)" for a filter (nothing selected).</summary>
    public void Reset() => SetAll(false);

    private void OnOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectableOption.IsSelected) && !_bulk) Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(IsActive));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
