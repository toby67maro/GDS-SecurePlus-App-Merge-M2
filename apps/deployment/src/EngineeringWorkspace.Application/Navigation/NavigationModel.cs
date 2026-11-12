namespace EngineeringWorkspace.Application.Navigation;

public enum NavigationNodeKind
{
    Section,
    Group,
    Item
}

/// <summary>
/// One configured navigation node (a row of Navigation.ini after merging all layers by stable ID). Configured
/// state is never mutated for UI state; runtime expansion and selection live in <see cref="NavigationState"/>.
/// </summary>
public sealed record NavigationNodeDefinition
{
    public required NavigationNodeKind Kind { get; init; }
    public required string Id { get; init; }
    public string? ParentId { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public string? IconKey { get; init; }
    public int SortOrder { get; init; }
    public bool IsVisible { get; init; } = true;
    public bool IsEnabled { get; init; } = true;
    public string? RequiredFeature { get; init; }
    public bool ExpandedByDefault { get; init; } = true;
    public bool CanCollapse { get; init; } = true;
    public bool ShowHeader { get; init; } = true;
    public string? Route { get; init; }
    public bool IsDefault { get; init; }
    public bool RequiresWorkspace { get; init; }
    public bool RequiresCustomer { get; init; }
    public string? RequiredCapability { get; init; }

    /// <summary>Shown only while Advanced Mode is unlocked (Settings > Application Settings).</summary>
    public bool RequiresAdvancedMode { get; init; }
}

/// <summary>All configured navigation nodes.</summary>
public sealed record NavigationDefinition(IReadOnlyList<NavigationNodeDefinition> Nodes);

/// <summary>A resolved node, shared shape of sections, groups and items.</summary>
public abstract record NavigationNode
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public string? IconKey { get; init; }
    public int SortOrder { get; init; }
    public bool IsVisible { get; init; } = true;
    public bool IsEnabled { get; init; } = true;
    public string? DisabledReason { get; init; }
    public string? RequiredFeature { get; init; }
    public string? ParentId { get; init; }
    public IReadOnlyList<NavigationNode> Children { get; init; } = [];
}

public sealed record NavigationSection : NavigationNode
{
    public bool ExpandedByDefault { get; init; } = true;
    public bool CanCollapse { get; init; } = true;
}

public sealed record NavigationGroup : NavigationNode
{
    public bool ExpandedByDefault { get; init; } = true;
    public bool ShowHeader { get; init; } = true;
}

public sealed record NavigationItem : NavigationNode
{
    public required string Route { get; init; }
    public bool IsDefault { get; init; }
    public bool RequiredWorkspace { get; init; }
    public bool RequiredCustomer { get; init; }
    public string? RequiredCapability { get; init; }
}

/// <summary>Runtime navigation state, separate from configuration.</summary>
public sealed record NavigationState(string? ActiveRoute, IReadOnlySet<string> ExpandedNodeIds);

/// <summary>
/// Logical description of a destination. Desktop maps <see cref="ViewKey"/> to a page ViewModel and view;
/// Application never references WPF types.
/// </summary>
public sealed record RouteDescriptor(
    string Route,
    string PageId,
    string DisplayName,
    string Description,
    string ViewKey,
    string? RequiredFeature,
    bool RequiresWorkspace,
    bool RequiresCustomer,
    bool IsPlaceholder,
    string? PlannedMilestone,
    string ParentSection);
