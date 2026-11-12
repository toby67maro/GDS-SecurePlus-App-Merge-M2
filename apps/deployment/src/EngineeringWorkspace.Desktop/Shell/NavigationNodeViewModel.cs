using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EngineeringWorkspace.Application.Navigation;

namespace EngineeringWorkspace.Desktop.Shell;

/// <summary>A node of the rendered navigation tree. Runtime state only; configuration is never mutated.</summary>
public sealed partial class NavigationNodeViewModel : ObservableObject
{
    private readonly Action<NavigationNodeViewModel> _selected;
    private readonly Action<NavigationNodeViewModel, bool> _expanded;

    public NavigationNodeViewModel(NavigationNode node, bool isExpanded, bool isPinned, Action<NavigationNodeViewModel> selected, Action<NavigationNodeViewModel, bool> expanded)
    {
        Node = node;
        _selected = selected;
        _expanded = expanded;
        _isExpanded = isExpanded;
        _isPinned = isPinned;
    }

    public NavigationNode Node { get; }
    public ObservableCollection<NavigationNodeViewModel> Children { get; } = [];
    public string DisplayName => Node.DisplayName;
    public bool IsItem => Node is NavigationItem;
    public bool IsSection => Node is NavigationSection;
    public bool IsGroup => Node is NavigationGroup;
    public bool IsEnabled => Node.IsEnabled;
    public string? Route => (Node as NavigationItem)?.Route;
    public string? ToolTip => Node.DisabledReason ?? Node.Description;

    /// <summary>Section glyph (Segoe MDL2 Assets) by the configured icon key.</summary>
    public string Icon => Node.IconKey switch
    {
        "dashboard" => "",
        "assessment" => "",
        "planning" => "",
        "deployment" => "",
        "reporting" => "",
        "workspace" => "",
        "settings" => "\uE713",
        "advanced" => "\uEC7A",
        _ => ""
    };

    /// <summary>
    /// Vector icon used instead of the glyph for sections that have one (Deployment: a rocket taking off, drawn upright
    /// in a 24×24 box; the view tilts it 30° clockwise, about 60° from horizontal).
    /// </summary>
    public System.Windows.Media.Geometry? IconGeometry => Node.IconKey == "deployment" ? RocketGeometry : null;

    public bool HasIconGeometry => IconGeometry is not null;

    private static readonly System.Windows.Media.Geometry RocketGeometry = CreateRocket();

    private static System.Windows.Media.Geometry CreateRocket()
    {
        var geometry = System.Windows.Media.Geometry.Parse(
            "F0 M12,1.5 C15.2,4.6 16.2,8.8 16.2,12.8 L16.2,17 L7.8,17 L7.8,12.8 C7.8,8.8 8.8,4.6 12,1.5 Z " +
            "M12,7.9 A1.8,1.8 0 1 0 12.001,7.9 Z " +
            "M7.8,12.2 L4.6,15.6 L4.6,19.2 L7.8,17 Z M16.2,12.2 L19.4,15.6 L19.4,19.2 L16.2,17 Z " +
            "M9.8,18 L12,22.8 L14.2,18 Z");
        geometry.Freeze();
        return geometry;
    }

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>A pinned section stays open when another section is expanded.</summary>
    [ObservableProperty]
    private bool _isPinned;

    /// <summary>True for the section (and group) that contains the current page.</summary>
    [ObservableProperty]
    private bool _isActive;

    partial void OnIsExpandedChanged(bool value) => _expanded(this, value);

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) _selected(this);
    }

    public IEnumerable<NavigationNodeViewModel> Descendants() => Children.SelectMany(child => new[] { child }.Concat(child.Descendants()));
}
