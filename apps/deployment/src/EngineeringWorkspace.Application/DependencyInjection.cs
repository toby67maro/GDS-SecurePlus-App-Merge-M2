using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Credentials;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Features;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Application.Tenants;
using EngineeringWorkspace.Application.Workspaces;
using Microsoft.Extensions.DependencyInjection;

namespace EngineeringWorkspace.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers Application services. Settings records (ApplicationSettings, DatabaseSettings, ...) are
    /// registered by EngineeringWorkspace.Configuration from the validated snapshot.
    /// </summary>
    public static IServiceCollection AddEngineeringWorkspaceApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IRedactionService, RedactionService>();
        services.AddSingleton<IFeatureService, FeatureService>();
        services.AddSingleton<IRouteRegistry>(_ => new RouteRegistry(RouteCatalog.All));
        services.AddSingleton<IWorkspaceService, WorkspaceService>();
        services.AddSingleton<ICustomerContextService, CustomerContextService>();
        services.AddSingleton<IAdvancedModeService, AdvancedModeService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<ICredentialService, CredentialService>();
        services.AddSingleton<IDatabaseStatusService, DatabaseStatusService>();
        services.AddSingleton<IPermissionValidationService, PermissionValidationService>();
        services.AddSingleton<ITenantConnectionService, TenantConnectionService>();
        services.AddTransient<ITenantCredentialService, TenantCredentialService>();
        services.AddTransient<ITenantRegistrationService, TenantRegistrationService>();
        services.AddTransient<AppRegistrationAssistant>();
        services.AddSingleton<IEngagementService, EngagementService>();
        services.AddSingleton<Jobs.IJobService, Jobs.JobService>();
        services.AddTransient<ITenantOnboardingService, TenantOnboardingService>();
        services.AddSingleton<GoldenTenant.IGoldenBaselineService, GoldenTenant.GoldenBaselineService>();
        services.AddSingleton<Governance.IBaselineService, Governance.BaselineService>();
        services.AddSingleton<Assessments.IScoringScope>(provider => provider.GetRequiredService<Governance.IBaselineService>());
        services.AddSingleton<IAssessmentService, AssessmentService>();
        services.AddSingleton<IAssessmentScheduler, AssessmentScheduler>();
        services.AddSingleton<IInsightService, InsightService>();
        services.AddSingleton<Catalog.ICatalogSyncService, Catalog.CatalogSyncService>();
        services.AddSingleton<KnowledgeBase.IKnowledgeBaseService, KnowledgeBase.KnowledgeBaseService>();
        services.AddSingleton(provider =>
        {
            var application = provider.GetRequiredService<Settings.ApplicationSettings>();
            return Export.ExportBranding.Gds with { CompanyName = application.CompanyName, ProductName = application.Title };
        });
        services.AddSingleton<Export.IExportService, Export.ExportService>();
        services.AddSingleton<Reporting.IReportingService, Reporting.ReportingService>();
        services.AddSingleton<Governance.ChangeExecutionService>();
        services.AddSingleton<Planning.TaskRemediationService>();
        return services;
    }
}
