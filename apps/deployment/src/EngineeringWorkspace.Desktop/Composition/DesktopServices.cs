using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Configuration.Appearance;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Desktop.Pages.Appearance;
using EngineeringWorkspace.Desktop.Pages.Assessments;
using EngineeringWorkspace.Desktop.Pages.Dashboard;
using EngineeringWorkspace.Desktop.Pages.Settings;
using EngineeringWorkspace.Desktop.Pages.Auditing;
using EngineeringWorkspace.Desktop.Pages.Credentials;
using EngineeringWorkspace.Desktop.Pages.Database;
using EngineeringWorkspace.Desktop.Pages.Tenants;
using EngineeringWorkspace.Desktop.Services;
using EngineeringWorkspace.Desktop.Shell;
using EngineeringWorkspace.Desktop.Setup;
using EngineeringWorkspace.Desktop.Theming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Desktop.Composition;

public static class DesktopServices
{
    /// <summary>Desktop registrations: singletons for the shell, theme and view services; transient page ViewModels.</summary>
    public static IServiceCollection AddEngineeringWorkspaceDesktop(this IServiceCollection services)
    {
        services.AddSingleton<Theming.WorkspaceThemeCoordinator>();
        services.AddTransient<Pages.Credentials.OrganizationKeyPanel>();
        services.AddSingleton<IThemeService>(provider => new ThemeManager(
            Path.Combine(provider.GetRequiredService<PathSettings>().ConfigurationDirectory, ApplicationPropertiesLoader.FileName),
            provider.GetRequiredService<IAuditService>(),
            provider.GetRequiredService<ILogger<ThemeManager>>(),
            provider.GetRequiredService<Application.State.IApplicationStateStore>()));
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<ExportCoordinator>();
        services.AddSingleton<Pages.Deployment.DeploymentSelection>();
        services.AddSingleton<IShellLauncher, ShellLauncher>();
        services.AddSingleton<IPageViewModelFactory, PageViewModelFactory>();
        services.AddSingleton<ActivityViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>();
        services.AddTransient<SetupViewModel>();
        services.AddTransient<SetupWindow>();

        services.AddTransient<DatabaseConfigurationViewModel>();
        services.AddTransient<DatabaseHealthViewModel>();
        services.AddTransient<CredentialManagementViewModel>();
        services.AddTransient<Pages.Settings.UsersAndRolesViewModel>();
        services.AddTransient<Pages.Settings.PolicyEditorViewModel>();
        services.AddSingleton<Pages.Planning.TaskDetailServices>();
        services.AddSingleton(provider => new Pages.Customers.TenantConnectServices(provider.GetRequiredService<EngineeringWorkspace.Application.Tenants.ITenantOnboardingService>(),
            provider.GetRequiredService<EngineeringWorkspace.Application.Jobs.IJobService>(), provider.GetRequiredService<EngineeringWorkspace.Application.Tenants.IPermissionValidationService>(),
            provider.GetService<EngineeringWorkspace.Application.Tenants.ITenantInventoryRepository>(), provider.GetService<Services.IDialogService>()));
        services.AddTransient<TenantAppRegistrationViewModel>();
        services.AddTransient<AppearanceViewModel>();
        services.AddTransient<AuditingAndLogsViewModel>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<AssessmentHistoryViewModel>();
        services.AddTransient<ApplicationSettingsViewModel>();
        services.AddTransient<Pages.Catalog.CatalogViewModel>();
        services.AddTransient<Pages.Settings.BaselineConfigurationsViewModel>();
        services.AddTransient<Pages.Settings.WorkspaceConfigurationViewModel>();
        services.AddTransient<Pages.Deployment.TrackerViewModel>();
        services.AddTransient<Pages.Deployment.ChangeExecutionViewModel>();
        services.AddTransient<Pages.Deployment.ChangeRequestViewModel>();
        services.AddTransient<Pages.Specifications.SpecificationProfilesViewModel>();
        services.AddTransient<Pages.Customers.CustomerProfilesViewModel>();
        services.AddTransient<Pages.Customers.ExceptionsViewModel>();
        services.AddTransient<Pages.Specifications.SpecificationComparisonViewModel>();
        services.AddTransient<Pages.Insights.FleetViewModel>();
        services.AddTransient<Pages.Insights.HeatMapViewModel>();
        services.AddTransient<Pages.Advanced.DataExplorerViewModel>();
        services.AddTransient<Pages.Advanced.DiagnosticsViewModel>();
        services.AddTransient<Pages.Experience.ExperienceConfigurationViewModel>();
        services.AddTransient<Pages.Experience.ExperienceMapViewModel>();
        services.AddTransient<Pages.Planning.ProjectPlanViewModel>();
        services.AddTransient<Pages.Planning.SowViewModel>();
        services.AddTransient<Pages.Insights.RoadmapViewModel>();
        services.AddTransient<Pages.Reporting.CompletionReportViewModel>();
        services.AddTransient<Pages.Live.LiveAssessmentViewModel>();
        return services;
    }
}
