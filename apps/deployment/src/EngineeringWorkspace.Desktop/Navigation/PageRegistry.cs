using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Desktop.Pages.Appearance;
using EngineeringWorkspace.Desktop.Pages.Assessments;
using EngineeringWorkspace.Desktop.Pages.Dashboard;
using EngineeringWorkspace.Desktop.Pages.Settings;
using EngineeringWorkspace.Desktop.Pages.Auditing;
using EngineeringWorkspace.Desktop.Pages.Credentials;
using EngineeringWorkspace.Desktop.Pages.Database;
using EngineeringWorkspace.Desktop.Pages.Tenants;

namespace EngineeringWorkspace.Desktop.Navigation;

/// <summary>Desktop view resolution: maps Application view keys to page ViewModel types. Views come from DataTemplates.</summary>
public static class PageRegistry
{
    public static IReadOnlyDictionary<string, Type> ViewModels { get; } = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        [ViewKeys.DatabaseConfiguration] = typeof(DatabaseConfigurationViewModel),
        [ViewKeys.CredentialManagement] = typeof(CredentialManagementViewModel),
        [ViewKeys.UsersAndRoles] = typeof(Pages.Settings.UsersAndRolesViewModel),
        [ViewKeys.TenantAppRegistration] = typeof(TenantAppRegistrationViewModel),
        [ViewKeys.Appearance] = typeof(AppearanceViewModel),
        [ViewKeys.AuditingAndLogs] = typeof(AuditingAndLogsViewModel),
        [ViewKeys.Dashboard] = typeof(DashboardViewModel),
        [ViewKeys.AssessmentHistory] = typeof(AssessmentHistoryViewModel),
        [ViewKeys.ApplicationSettings] = typeof(ApplicationSettingsViewModel),
        [ViewKeys.Catalog] = typeof(Pages.Catalog.CatalogViewModel),
        [ViewKeys.LiveAssessment] = typeof(Pages.Live.LiveAssessmentViewModel),
        [ViewKeys.Roadmap] = typeof(Pages.Insights.RoadmapViewModel),
        [ViewKeys.HeatMap] = typeof(Pages.Insights.HeatMapViewModel),
        [ViewKeys.FleetView] = typeof(Pages.Insights.FleetViewModel),
        [ViewKeys.PolicyEditor] = typeof(Pages.Settings.PolicyEditorViewModel),
        [ViewKeys.CompletionReport] = typeof(Pages.Reporting.CompletionReportViewModel),
        [ViewKeys.SpecificationComparison] = typeof(Pages.Specifications.SpecificationComparisonViewModel),
        [ViewKeys.ChangeRequest] = typeof(Pages.Deployment.ChangeRequestViewModel),
        [ViewKeys.ChangeExecution] = typeof(Pages.Deployment.ChangeExecutionViewModel),
        [ViewKeys.ChangeTracker] = typeof(Pages.Deployment.TrackerViewModel),
        [ViewKeys.CustomerProfiles] = typeof(Pages.Customers.CustomerProfilesViewModel),
        [ViewKeys.SpecificationProfiles] = typeof(Pages.Specifications.SpecificationProfilesViewModel),
        [ViewKeys.CustomerExceptions] = typeof(Pages.Customers.ExceptionsViewModel),
        [ViewKeys.WorkspaceConfiguration] = typeof(Pages.Settings.WorkspaceConfigurationViewModel),
        [ViewKeys.BaselineConfigurations] = typeof(Pages.Settings.BaselineConfigurationsViewModel),
        [ViewKeys.DatabaseHealth] = typeof(Pages.Database.DatabaseHealthViewModel),
        [ViewKeys.DataExplorer] = typeof(Pages.Advanced.DataExplorerViewModel),
        [ViewKeys.Diagnostics] = typeof(Pages.Advanced.DiagnosticsViewModel),
        [ViewKeys.ExperienceConfiguration] = typeof(Pages.Experience.ExperienceConfigurationViewModel),
        [ViewKeys.ExperienceMap] = typeof(Pages.Experience.ExperienceMapViewModel),
        [ViewKeys.ProjectPlan] = typeof(Pages.Planning.ProjectPlanViewModel),
        [ViewKeys.Sow] = typeof(Pages.Planning.SowViewModel),
    };

    /// <summary>Fails fast at startup if a registered route has no page.</summary>
    public static void EnsureComplete(IRouteRegistry routes)
    {
        var missing = routes.All.Where(route => !ViewModels.ContainsKey(route.ViewKey)).Select(route => route.Route).ToArray();
        if (missing.Length > 0) throw new InvalidOperationException($"No page is registered for: {string.Join(", ", missing)}.");
    }
}

/// <summary>Creates page ViewModels. The only place outside the composition root that uses IServiceProvider.</summary>
public interface IPageViewModelFactory
{
    PageViewModel Create(RouteDescriptor route);
}

public sealed class PageViewModelFactory(IServiceProvider services) : IPageViewModelFactory
{
    public PageViewModel Create(RouteDescriptor route) =>
        (PageViewModel)(services.GetService(PageRegistry.ViewModels[route.ViewKey])
                        ?? throw new InvalidOperationException($"Page ViewModel for '{route.ViewKey}' is not registered."));
}
