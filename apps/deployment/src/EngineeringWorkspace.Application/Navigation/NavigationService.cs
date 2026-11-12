using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Features;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Application.Workspaces;
using EngineeringWorkspace.Domain.Auditing;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Application.Navigation;

/// <summary>All known destinations. Every configured route must be registered here.</summary>
public interface IRouteRegistry
{
    IReadOnlyList<RouteDescriptor> All { get; }
    bool TryGet(string route, out RouteDescriptor descriptor);
}

public sealed class RouteRegistry : IRouteRegistry
{
    private readonly Dictionary<string, RouteDescriptor> _routes;

    public RouteRegistry(IEnumerable<RouteDescriptor> routes)
    {
        _routes = new Dictionary<string, RouteDescriptor>(StringComparer.Ordinal);
        foreach (var route in routes)
        {
            if (!_routes.TryAdd(route.Route, route))
                throw new InvalidOperationException($"Route '{route.Route}' is registered more than once.");
        }

        All = _routes.Values.OrderBy(route => route.Route, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<RouteDescriptor> All { get; }

    public bool TryGet(string route, out RouteDescriptor descriptor) => _routes.TryGetValue(route, out descriptor!);
}

/// <summary>Outcome of a navigation request.</summary>
public sealed record NavigationResult(bool Succeeded, string? Route, string? Message)
{
    public static NavigationResult Success(string route) => new(true, route, null);
    public static NavigationResult Failure(string? route, string message) => new(false, route, message);
}

public sealed record NavigatedEventArgs(string? PreviousRoute, RouteDescriptor Current, NavigationItem Item);

/// <summary>Builds the resolved navigation tree from configuration, the route registry and runtime policies.</summary>
public static class NavigationResolver
{
    public static IReadOnlyList<NavigationSection> Resolve(
        NavigationDefinition definition,
        IRouteRegistry routes,
        IFeatureService features,
        bool hasWorkspace,
        bool hasCustomer,
        bool advancedMode = false)
    {
        var byParent = definition.Nodes
            .Where(node => node.ParentId is not null)
            .GroupBy(node => node.ParentId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(node => node.SortOrder).ThenBy(node => node.Id, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

        NavigationNode? Build(NavigationNodeDefinition node, string? inheritedDisabledReason)
        {
            if (!node.IsVisible) return null;
            if (node.RequiresAdvancedMode && !advancedMode) return null;
            var reason = inheritedDisabledReason;
            if (reason is null && !node.IsEnabled) reason = "Disabled in navigation configuration.";
            if (reason is null && node.RequiredFeature is not null && !features.IsEnabled(node.RequiredFeature))
                reason = $"The '{node.RequiredFeature}' feature is turned off in Features.ini.";

            var children = byParent.TryGetValue(node.Id, out var childNodes)
                ? childNodes.Select(child => Build(child, reason)).OfType<NavigationNode>().ToArray()
                : [];

            switch (node.Kind)
            {
                case NavigationNodeKind.Section:
                    return new NavigationSection
                    {
                        Id = node.Id, DisplayName = node.DisplayName, Description = node.Description, IconKey = node.IconKey,
                        SortOrder = node.SortOrder, IsEnabled = reason is null, DisabledReason = reason, RequiredFeature = node.RequiredFeature,
                        ParentId = null, Children = children, ExpandedByDefault = node.ExpandedByDefault, CanCollapse = node.CanCollapse
                    };
                case NavigationNodeKind.Group:
                    return new NavigationGroup
                    {
                        Id = node.Id, DisplayName = node.DisplayName, Description = node.Description, IconKey = node.IconKey,
                        SortOrder = node.SortOrder, IsEnabled = reason is null, DisabledReason = reason, RequiredFeature = node.RequiredFeature,
                        ParentId = node.ParentId, Children = children, ExpandedByDefault = node.ExpandedByDefault, ShowHeader = node.ShowHeader
                    };
                default:
                    if (node.Route is null || !routes.TryGet(node.Route, out var descriptor)) return null;
                    if (reason is null && descriptor.RequiredFeature is not null && !features.IsEnabled(descriptor.RequiredFeature))
                        reason = $"The '{descriptor.RequiredFeature}' feature is turned off in Features.ini.";
                    var requiresWorkspace = node.RequiresWorkspace || descriptor.RequiresWorkspace;
                    var requiresCustomer = node.RequiresCustomer || descriptor.RequiresCustomer;
                    if (reason is null && requiresWorkspace && !hasWorkspace) reason = "Open a workspace to use this page.";
                    if (reason is null && requiresCustomer && !hasCustomer) reason = "Select a customer to use this page.";
                    return new NavigationItem
                    {
                        Id = node.Id, DisplayName = node.DisplayName, Description = node.Description ?? descriptor.Description, IconKey = node.IconKey,
                        SortOrder = node.SortOrder, IsEnabled = reason is null, DisabledReason = reason, RequiredFeature = node.RequiredFeature ?? descriptor.RequiredFeature,
                        ParentId = node.ParentId, Route = descriptor.Route, IsDefault = node.IsDefault, RequiredWorkspace = requiresWorkspace,
                        RequiredCustomer = requiresCustomer, RequiredCapability = node.RequiredCapability
                    };
            }
        }

        return definition.Nodes
            .Where(node => node.Kind == NavigationNodeKind.Section && node.ParentId is null)
            .OrderBy(node => node.SortOrder).ThenBy(node => node.Id, StringComparer.Ordinal)
            .Select(node => Build(node, null))
            .OfType<NavigationSection>()
            .ToArray();
    }

    public static IEnumerable<NavigationItem> Items(IEnumerable<NavigationNode> nodes) =>
        nodes.SelectMany(node => node is NavigationItem item ? [item] : Items(node.Children));
}

/// <summary>Owns logical navigation. Never constructs views; Desktop resolves the page for a route.</summary>
public interface INavigationService
{
    IReadOnlyList<NavigationSection> Sections { get; }
    NavigationState State { get; }
    RouteDescriptor? CurrentRoute { get; }
    event EventHandler<NavigatedEventArgs>? Navigated;
    event EventHandler? TreeChanged;
    Task<NavigationResult> NavigateAsync(string route, CancellationToken cancellationToken = default);
    Task<NavigationResult> NavigateToDefaultAsync(string? preferredRoute, CancellationToken cancellationToken = default);
    void SetExpanded(string nodeId, bool expanded);
    bool TryGetRoute(string route, out RouteDescriptor descriptor);
}

public sealed class NavigationService : INavigationService
{
    private readonly NavigationDefinition _definition;
    private readonly IRouteRegistry _routes;
    private readonly IFeatureService _features;
    private readonly IWorkspaceService _workspaces;
    private readonly ICustomerContextService _customers;
    private readonly IAuditService _audit;
    private readonly ApplicationSettings _application;
    private readonly ILogger<NavigationService> _logger;
    private readonly HashSet<string> _expanded;
    private readonly IAdvancedModeService? _advancedMode;
    private string? _activeRoute;

    public NavigationService(
        NavigationDefinition definition,
        IRouteRegistry routes,
        IFeatureService features,
        IWorkspaceService workspaces,
        ICustomerContextService customers,
        IAuditService audit,
        ApplicationSettings application,
        ILogger<NavigationService> logger,
        IAdvancedModeService? advancedMode = null)
    {
        _advancedMode = advancedMode;
        _definition = definition;
        _routes = routes;
        _features = features;
        _workspaces = workspaces;
        _customers = customers;
        _audit = audit;
        _application = application;
        _logger = logger;
        _expanded = new HashSet<string>(
            definition.Nodes.Where(node => node.Kind != NavigationNodeKind.Item && node.ExpandedByDefault).Select(node => node.Id),
            StringComparer.Ordinal);
        Sections = Resolve();
        _workspaces.WorkspaceChanged += (_, _) => Rebuild();
        _customers.CustomerChanged += (_, _) => Rebuild();
        if (_advancedMode is not null) _advancedMode.Changed += (_, _) => Rebuild();
    }

    public IReadOnlyList<NavigationSection> Sections { get; private set; }
    public NavigationState State => new(_activeRoute, new HashSet<string>(_expanded, StringComparer.Ordinal));
    public RouteDescriptor? CurrentRoute => _activeRoute is not null && _routes.TryGet(_activeRoute, out var descriptor) ? descriptor : null;
    public event EventHandler<NavigatedEventArgs>? Navigated;
    public event EventHandler? TreeChanged;

    public async Task<NavigationResult> NavigateAsync(string route, CancellationToken cancellationToken = default)
    {
        var item = NavigationResolver.Items(Sections).FirstOrDefault(candidate => string.Equals(candidate.Route, route, StringComparison.Ordinal));
        if (item is null || !_routes.TryGet(route, out var descriptor))
            return NavigationResult.Failure(route, $"'{route}' is not a configured, visible destination.");
        if (!item.IsEnabled)
            return NavigationResult.Failure(route, item.DisabledReason ?? "This destination is disabled.");

        var previous = _activeRoute;
        _activeRoute = route;
        Navigated?.Invoke(this, new NavigatedEventArgs(previous, descriptor, item));
        if (!string.Equals(previous, route, StringComparison.Ordinal))
        {
            _logger.LogInformation("Navigated from {PreviousRoute} to {Route}", previous ?? "(none)", route);
            await _audit.RecordAsync(AuditEventTypes.RouteChanged, AuditOutcome.Information, $"Navigated to {route}.",
                new Dictionary<string, string?> { ["PreviousRoute"] = previous, ["Route"] = route }, cancellationToken);
        }

        return NavigationResult.Success(route);
    }

    /// <summary>
    /// Navigates to the preferred route (for example the last route), then the configured default route, then the
    /// item marked IsDefault, then the first enabled item in tree order — a deterministic fallback.
    /// </summary>
    public async Task<NavigationResult> NavigateToDefaultAsync(string? preferredRoute, CancellationToken cancellationToken = default)
    {
        var items = NavigationResolver.Items(Sections).ToArray();
        var candidates = new[] { preferredRoute, _application.DefaultRoute, items.FirstOrDefault(item => item.IsDefault)?.Route }
            .Concat(items.Where(item => item.IsEnabled).Select(item => item.Route))
            .Where(route => !string.IsNullOrWhiteSpace(route))
            .Distinct(StringComparer.Ordinal);
        foreach (var route in candidates)
        {
            var result = await NavigateAsync(route!, cancellationToken);
            if (result.Succeeded) return result;
            _logger.LogWarning("Startup route {Route} unavailable: {Reason}", route, result.Message);
        }

        return NavigationResult.Failure(null, "No navigation destination is enabled. Check Navigation.ini and Features.ini.");
    }

    public void SetExpanded(string nodeId, bool expanded)
    {
        if (expanded) _expanded.Add(nodeId);
        else _expanded.Remove(nodeId);
    }

    public bool TryGetRoute(string route, out RouteDescriptor descriptor) => _routes.TryGet(route, out descriptor);

    private IReadOnlyList<NavigationSection> Resolve() =>
        NavigationResolver.Resolve(_definition, _routes, _features, _workspaces.HasActiveWorkspace, _customers.CurrentCustomer is not null,
            _advancedMode?.IsEnabled ?? false);

    private void Rebuild()
    {
        Sections = Resolve();
        TreeChanged?.Invoke(this, EventArgs.Empty);
    }
}
