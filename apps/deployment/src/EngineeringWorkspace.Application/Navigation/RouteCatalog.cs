using EngineeringWorkspace.Application.Features;

namespace EngineeringWorkspace.Application.Navigation;

/// <summary>Stable routes. Lowercase, slash-separated and free of WPF type names.</summary>
public static class Routes
{
    public const string DashboardOverview = "dashboard/overview";
    public const string DashboardRoadmap = "dashboard/roadmap";
    public const string DashboardHeatMap = "dashboard/heat-map";
    public const string DashboardCatalog = "dashboard/catalog";
    public const string DashboardFleetView = "dashboard/fleet-view";
    public const string AssessmentEngine = "assessment/engine";
    public const string AssessmentLive = "assessment/live";
    public const string DeploymentChangeRequest = "deployment/change-request";
    public const string DeploymentChangeExecution = "deployment/change-execution";
    public const string DeploymentTracker = "deployment/tracker";
    public const string ReportingControlCompletion = "reporting/control-completion";
    public const string ReportingSolutionCompletion = "reporting/solution-completion";
    public const string ReportingSpecificationCompletion = "reporting/specification-completion";
    public const string ReportingComplianceCompletion = "reporting/compliance-completion";
    public const string ReportingFrameworkCompletion = "reporting/framework-completion";
    public const string ReportingExperienceCompletion = "reporting/experience-completion";
    public const string ReportingSpecificationComparison = "reporting/specification-comparison";
    public const string CustomerProfiles = "workspace/customer-profiles";
    public const string SpecificationProfiles = "workspace/specification-profiles";
    public const string TenantAppRegistration = "workspace/tenant-app-registration";
    public const string CustomerExceptions = "workspace/exceptions";
    public const string ApplicationSettings = "settings/application-settings";
    public const string WorkspaceConfiguration = "settings/workspace-configuration";
    public const string BaselineConfigurations = "settings/baseline-configurations";
    public const string PolicyEditor = "settings/policies";
    public const string DatabaseConfiguration = "settings/database-configuration";
    public const string DatabaseHealth = "settings/database-health";
    public const string AuditingAndLogs = "settings/auditing-and-logs";
    public const string CredentialManagement = "settings/credential-management";
    public const string UsersAndRoles = "settings/users-and-roles";
    public const string Appearance = "settings/appearance";
    public const string AssessmentExperienceConfiguration = "assessment/experience-configuration";
    public const string AssessmentSow = "assessment/sow";
    public const string DeploymentProjectPlan = "deployment/project-plan";
    public const string ReportingExperienceMap = "reporting/experience-map";
    public const string AdvancedDataExplorer = "advanced/data-explorer";
    public const string AdvancedDiagnostics = "advanced/diagnostics";

    /// <summary>The six completion reports share one page; the route selects the report.</summary>
    public static IReadOnlyList<string> CompletionReports { get; } =
    [
        ReportingControlCompletion, ReportingSolutionCompletion, ReportingSpecificationCompletion, ReportingComplianceCompletion, ReportingFrameworkCompletion,
        ReportingExperienceCompletion
    ];
}

/// <summary>View keys Desktop maps to page ViewModels.</summary>
public static class ViewKeys
{
    public const string DatabaseConfiguration = "page.database-configuration";
    public const string TenantAppRegistration = "page.tenant-app-registration";
    public const string CredentialManagement = "page.credential-management";
    public const string UsersAndRoles = "page.users-and-roles";
    public const string Appearance = "page.appearance";
    public const string AuditingAndLogs = "page.auditing-and-logs";
    public const string Dashboard = "page.dashboard";
    public const string AssessmentHistory = "page.assessment-history";
    public const string ApplicationSettings = "page.application-settings";
    public const string Catalog = "page.catalog";
    public const string LiveAssessment = "page.live-assessment";
    public const string Roadmap = "page.roadmap";
    public const string HeatMap = "page.heat-map";
    public const string FleetView = "page.fleet-view";
    public const string CompletionReport = "page.completion-report";
    public const string SpecificationComparison = "page.specification-comparison";
    public const string ChangeRequest = "page.change-request";
    public const string ChangeExecution = "page.change-execution";
    public const string ChangeTracker = "page.change-tracker";
    public const string CustomerProfiles = "page.customer-profiles";
    public const string SpecificationProfiles = "page.specification-profiles";
    public const string CustomerExceptions = "page.customer-exceptions";
    public const string WorkspaceConfiguration = "page.workspace-configuration";
    public const string BaselineConfigurations = "page.baseline-configurations";
    public const string PolicyEditor = "page.policy-editor";
    public const string DatabaseHealth = "page.database-health";
    public const string ExperienceConfiguration = "page.experience-configuration";
    public const string Sow = "page.sow";
    public const string ProjectPlan = "page.project-plan";
    public const string ExperienceMap = "page.experience-map";
    public const string DataExplorer = "page.data-explorer";
    public const string Diagnostics = "page.diagnostics";
}

/// <summary>
/// The logical destinations the application knows, and the default navigation tree. Configuration layers can
/// rename, reorder, hide or disable nodes by stable ID, but every configured route must be one of these.
/// </summary>
public static class RouteCatalog
{
    public static IReadOnlyList<RouteDescriptor> All { get; } =
    [
        Page(Routes.DashboardOverview, "Overview", "Customers, workspaces, database status and assessment results.", ViewKeys.Dashboard, FeatureIds.Dashboard, "Dashboard"),
        Page(Routes.DashboardRoadmap, "Roadmap", "The customer's remediation roadmap: controls not yet met, in phases by priority, severity, effort and dependencies.",
            ViewKeys.Roadmap, FeatureIds.Dashboard, "Dashboard"),
        Page(Routes.DashboardHeatMap, "Heat Map", "Compliance by any two dimensions (framework, standard, experience, solution area...) with drill-down to the controls.",
            ViewKeys.HeatMap, FeatureIds.Dashboard, "Dashboard"),
        Page(Routes.DashboardCatalog, "Catalog", "Every Secure Plus control with its baselines, frameworks, remediation options, implementation details and latest result.",
            ViewKeys.Catalog, FeatureIds.RepositoryCatalog, "Dashboard"),
        Page(Routes.DashboardFleetView, "Fleet View", "Every customer tenant: latest assessment, compliance, failing controls by severity, trend and schedules.",
            ViewKeys.FleetView, FeatureIds.Dashboard, "Dashboard"),
        Page(Routes.AssessmentEngine, "Assessment Scan", "Run quick or in-depth scans, schedule recurring scans and review scan history.", ViewKeys.AssessmentHistory,
            FeatureIds.Assessment, "Assessment"),
        Page(Routes.AssessmentLive, "Live Assessment", "Quick-scan up to 25 tenants now, several at a time (users, groups, roles, licences and posture); results are saved to the database.", ViewKeys.LiveAssessment,
            FeatureIds.LiveAssessment, "Assessment"),
        Page(Routes.AssessmentExperienceConfiguration, "Experience Configuration",
            "The experience each user group should have, by Microsoft solution: plain-language choices a CTO or IT lead makes (MFA prompts, passwordless, blocked locations...).",
            ViewKeys.ExperienceConfiguration, FeatureIds.Assessment, "Assessment"),
        Page(Routes.AssessmentSow, "SOW", "Create statement of work templates or pick one, and fill it from the customer profile, assessment, roadmap and project plan.",
            ViewKeys.Sow, FeatureIds.Assessment, "Assessment"),
        Page(Routes.DeploymentChangeRequest, "Change Request", "Create, edit, submit and approve change requests for a customer's controls and policies.",
            ViewKeys.ChangeRequest, FeatureIds.Deployment, "Deployment"),
        Page(Routes.DeploymentChangeExecution, "Change Execution", "Simulate an approved change request, or apply it to the tenant (passcode and confirmation required), with pre-checks, progress, logging and re-assessment.",
            ViewKeys.ChangeExecution, FeatureIds.Deployment, "Deployment"),
        Page(Routes.DeploymentProjectPlan, "Project Plan", "Build a project plan from the work breakdown structure and the roadmap: tasks, roles, hours and a work-day schedule.",
            ViewKeys.ProjectPlan, FeatureIds.Deployment, "Deployment"),
        Page(Routes.DeploymentTracker, "Tracker", "Every change request and execution across customers.", ViewKeys.ChangeTracker, FeatureIds.Deployment, "Deployment"),
        Page(Routes.ReportingControlCompletion, "Control Completion View", "Percentage of controls satisfied by status, severity and category.", ViewKeys.CompletionReport,
            FeatureIds.Reporting, "Reporting"),
        Page(Routes.ReportingSolutionCompletion, "Solution Completion View", "Completion by solution area.", ViewKeys.CompletionReport, FeatureIds.Reporting, "Reporting"),
        Page(Routes.ReportingSpecificationCompletion, "Specification Completion View", "Completion against each specification.", ViewKeys.CompletionReport,
            FeatureIds.Reporting, "Reporting"),
        Page(Routes.ReportingComplianceCompletion, "Compliance Completion View", "Completion by compliance standard.", ViewKeys.CompletionReport, FeatureIds.Reporting,
            "Reporting"),
        Page(Routes.ReportingFrameworkCompletion, "Framework Completion View", "Completion by framework.", ViewKeys.CompletionReport, FeatureIds.Reporting, "Reporting"),
        Page(Routes.ReportingExperienceCompletion, "Experience Completion View", "Completion by experience.", ViewKeys.CompletionReport, FeatureIds.Reporting, "Reporting"),
        Page(Routes.ReportingSpecificationComparison, "Specification Comparison", "Compare a tenant with a specification and/or other tenants, control by control and policy by policy.",
            ViewKeys.SpecificationComparison, FeatureIds.GoldenTenant, "Reporting"),
        Page(Routes.ReportingExperienceMap, "Experience Map",
            "Each user group, the experience it has and what it means; add frameworks such as NIST and see how each group's experience changes and where it conflicts.",
            ViewKeys.ExperienceMap, FeatureIds.Reporting, "Reporting"),
        Page(Routes.AdvancedDataExplorer, "Data Explorer", "Browse every table of the application database: row counts, columns and rows (read-only).",
            ViewKeys.DataExplorer, FeatureIds.DatabaseConfiguration, "Advanced"),
        Page(Routes.AdvancedDiagnostics, "Diagnostics", "Versions, folders, knowledge base parts, effective configuration and maintenance actions for engineers.",
            ViewKeys.Diagnostics, FeatureIds.Auditing, "Advanced"),
        Page(Routes.CustomerProfiles, "Customer Profiles", "View, edit and export customer profiles (secrets are never exported).", ViewKeys.CustomerProfiles,
            FeatureIds.CustomerProfiles, "Workspace"),
        Page(Routes.SpecificationProfiles, "Specification Profiles", "Build, clone, edit, assign and delete specifications.", ViewKeys.SpecificationProfiles,
            FeatureIds.CustomerProfiles, "Workspace"),
        Page(Routes.TenantAppRegistration, "Tenant App Registration", "Configure and test the Microsoft 365 app registration of a customer tenant.",
            ViewKeys.TenantAppRegistration, FeatureIds.CustomerProfiles, "Workspace"),
        Page(Routes.CustomerExceptions, "Exceptions", "Approved exceptions: controls a customer is not required to meet, with reason, approver and expiry.",
            ViewKeys.CustomerExceptions, FeatureIds.CustomerProfiles, "Workspace"),
        Page(Routes.ApplicationSettings, "Application Settings", "Environment, paths, workspace, import/export, credential, logging and feature settings.",
            ViewKeys.ApplicationSettings, FeatureIds.ApplicationSettings, "Settings"),
        Page(Routes.WorkspaceConfiguration, "Workspace Configuration", "View and edit workspace settings, and import or export them as JSON.",
            ViewKeys.WorkspaceConfiguration, FeatureIds.WorkspaceConfiguration, "Settings"),
        Page(Routes.BaselineConfigurations, "Baseline Configurations", "Build, activate, clone, edit and delete baselines, and see which customers comply.",
            ViewKeys.BaselineConfigurations, FeatureIds.GoldenTenant, "Settings"),
        Page(Routes.PolicyEditor, "Policies and Policy Sets", "Leads create, edit, hide and delete policies of any kind and policy sets; edits survive catalogue reloads.",
            ViewKeys.PolicyEditor, FeatureIds.GoldenTenant, "Settings"),
        Page(Routes.DatabaseConfiguration, "Database Configuration", "Configure, test, create and initialize the Secure Plus database.", ViewKeys.DatabaseConfiguration,
            FeatureIds.DatabaseConfiguration, "Settings"),
        Page(Routes.DatabaseHealth, "Database Health", "SQL Server size, disk, CPU, memory, sessions, blocking, waits and backups, refreshed every 45 seconds.",
            ViewKeys.DatabaseHealth, FeatureIds.DatabaseConfiguration, "Settings"),
        Page(Routes.AuditingAndLogs, "Auditing and Logs", "Effective configuration, configuration diagnostics and recent audit events.", ViewKeys.AuditingAndLogs,
            FeatureIds.Auditing, "Settings"),
        Page(Routes.CredentialManagement, "Credential Management", "Add, replace, validate and delete stored credentials.", ViewKeys.CredentialManagement,
            FeatureIds.CredentialManagement, "Settings"),
        Page(Routes.UsersAndRoles, "Users & Roles", "Who can use Secure Plus and with which role (Sales, Engineer, Technician/Deployer, NOC, Lead, Manager), and their database access.",
            ViewKeys.UsersAndRoles, FeatureIds.ApplicationSettings, "Settings"),
        Page(Routes.Appearance, "Appearance", "Theme mode, appearance properties file and GDS defaults.", ViewKeys.Appearance, FeatureIds.Appearance, "Settings"),
    ];

    /// <summary>The compiled default navigation tree. Children sort in steps of 10.</summary>
    public static IReadOnlyList<NavigationNodeDefinition> DefaultNavigation { get; } = BuildDefaultNavigation();

    private static RouteDescriptor Page(string route, string name, string description, string viewKey, string feature, string section, bool customer = false) =>
        new(route, PageId(route), name, description, viewKey, feature, false, customer, false, null, section);

    private static string PageId(string route) => route.Replace('/', '.');

    private static IReadOnlyList<NavigationNodeDefinition> BuildDefaultNavigation()
    {
        var nodes = new List<NavigationNodeDefinition>
        {
            Section("dashboard", "Dashboard", 100, "dashboard"),
            Section("assessment", "Assessment", 200, "assessment"),
            Section("deployment", "Deployment", 400, "deployment"),
            Section("reporting", "Reporting", 500, "reporting"),
            Section("workspace", "Engagements", 600, "workspace"),
            Section("settings", "Settings", 700, "settings"),
            Section("advanced", "Advanced", 800, "advanced") with { RequiresAdvancedMode = true },
        };

        void AddItems(string parent, params string[] routes)
        {
            for (var index = 0; index < routes.Length; index++)
            {
                var descriptor = All.Single(route => route.Route == routes[index]);
                nodes.Add(new NavigationNodeDefinition
                {
                    Kind = NavigationNodeKind.Item,
                    Id = routes[index].Replace('/', '-'),
                    ParentId = parent,
                    DisplayName = descriptor.DisplayName,
                    Description = descriptor.Description,
                    SortOrder = (index + 1) * 10,
                    RequiredFeature = descriptor.RequiredFeature,
                    Route = descriptor.Route,
                    IsDefault = descriptor.Route == Routes.DashboardOverview,
                    RequiresWorkspace = descriptor.RequiresWorkspace,
                    RequiresCustomer = descriptor.RequiresCustomer,
                });
            }
        }

        AddItems("dashboard", Routes.DashboardOverview, Routes.DashboardRoadmap, Routes.DashboardHeatMap, Routes.DashboardCatalog, Routes.DashboardFleetView);
        AddItems("assessment", Routes.AssessmentEngine, Routes.AssessmentLive, Routes.AssessmentExperienceConfiguration, Routes.AssessmentSow);
        AddItems("deployment", Routes.DeploymentChangeRequest, Routes.DeploymentChangeExecution, Routes.DeploymentProjectPlan, Routes.DeploymentTracker);
        AddItems("reporting", [Routes.ReportingExperienceMap, .. Routes.CompletionReports, Routes.ReportingSpecificationComparison]);
        AddItems("workspace", Routes.CustomerProfiles, Routes.SpecificationProfiles, Routes.TenantAppRegistration, Routes.CustomerExceptions);
        AddItems("settings", Routes.ApplicationSettings, Routes.WorkspaceConfiguration, Routes.BaselineConfigurations, Routes.PolicyEditor, Routes.DatabaseConfiguration,
            Routes.DatabaseHealth, Routes.AuditingAndLogs, Routes.CredentialManagement, Routes.UsersAndRoles, Routes.Appearance);
        AddItems("advanced", Routes.AdvancedDataExplorer, Routes.AdvancedDiagnostics);
        return nodes;
    }

    private static NavigationNodeDefinition Section(string id, string name, int order, string icon) =>
        new() { Kind = NavigationNodeKind.Section, Id = id, DisplayName = name, SortOrder = order, IconKey = icon, ExpandedByDefault = false };

    private static NavigationNodeDefinition Group(string id, string parent, string name, int order) =>
        new() { Kind = NavigationNodeKind.Group, Id = id, ParentId = parent, DisplayName = name, SortOrder = order };
}
