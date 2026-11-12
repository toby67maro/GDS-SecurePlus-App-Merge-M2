using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Application.Configuration;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Application.Credentials;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Repositories;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Application.State;
using EngineeringWorkspace.Application.Tenants;
using EngineeringWorkspace.Application.Workspaces;
using EngineeringWorkspace.Infrastructure.Auditing;
using EngineeringWorkspace.Infrastructure.Catalog;
using EngineeringWorkspace.Infrastructure.Database;
using EngineeringWorkspace.Infrastructure.FileSystem;
using EngineeringWorkspace.Infrastructure.Microsoft;
using EngineeringWorkspace.Infrastructure.Persistence;
using EngineeringWorkspace.Infrastructure.Repositories;
using EngineeringWorkspace.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers Infrastructure implementations. Called only from the Desktop composition root.</summary>
    public static IServiceCollection AddEngineeringWorkspaceInfrastructure(this IServiceCollection services, string actor)
    {
        services.AddSingleton(new AuditContext(actor));
        // The installer's choice (Server / Workstation / All-in-one); SECUREPLUS_INSTALL_PROFILE points elsewhere for testing.
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(InstallProfile)))
            services.AddSingleton(InstallProfile.Load(Environment.GetEnvironmentVariable("SECUREPLUS_INSTALL_PROFILE")));
        services.AddSingleton<JsonLinesAuditService>(provider => new JsonLinesAuditService(
            provider.GetRequiredService<PathSettings>().AuditDirectory,
            provider.GetRequiredService<AuditContext>(),
            provider.GetRequiredService<Application.Security.IRedactionService>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<JsonLinesAuditService>>()));
        services.AddSingleton(provider => new LedgerAuditService(provider.GetRequiredService<JsonLinesAuditService>(),
            new Lazy<IDatabaseStatusService>(provider.GetRequiredService<IDatabaseStatusService>), provider.GetRequiredService<DatabaseConnectionFactory>(),
            provider.GetRequiredService<AuditContext>(), provider.GetRequiredService<Application.Security.IRedactionService>(), provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<LedgerAuditService>>()));
        services.AddSingleton<IAuditService>(provider => provider.GetRequiredService<LedgerAuditService>());
        services.AddSingleton<IAuditReader>(provider => provider.GetRequiredService<JsonLinesAuditService>());

        // This computer's DPAPI store holds the database connection's secrets and the organization key; tenant secrets are shared
        // through the database, encrypted with the organization key (SharedSecretStore).
        services.AddSingleton(provider => new LocalSecrets(OperatingSystem.IsWindows()
            ? new DpapiSecretStore(provider.GetRequiredService<PathSettings>().CredentialDirectory, provider.GetRequiredService<ILogger<DpapiSecretStore>>())
            : new UnavailableSecretStore()));
        services.AddSingleton(provider => new OrganizationKeyService(provider.GetRequiredService<ISharedSettingsStore>(), provider.GetRequiredService<LocalSecrets>(),
            provider.GetRequiredService<IAuditService>(), new Lazy<Application.Security.IUserDirectory>(provider.GetRequiredService<Application.Security.IUserDirectory>)));
        services.AddSingleton<Application.Security.IUserDirectory>(provider => new SqlUserDirectory(provider.GetRequiredService<IDatabaseStatusService>(),
            provider.GetRequiredService<DatabaseConnectionFactory>(), provider.GetRequiredService<IAuditService>(), provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<SqlUserDirectory>>()));
        services.AddSingleton<Application.Security.ISchedulerHostService>(provider => new SqlSchedulerHostService(provider.GetRequiredService<IDatabaseStatusService>(),
            provider.GetRequiredService<DatabaseConnectionFactory>(), provider.GetRequiredService<AuditContext>(), provider.GetRequiredService<ApplicationSettings>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<Application.Security.IOrganizationKeyService>(provider => provider.GetRequiredService<OrganizationKeyService>());
        services.AddSingleton<ISecretStore>(provider => new SharedSecretStore(provider.GetRequiredService<LocalSecrets>(), provider.GetRequiredService<OrganizationKeyService>(),
            new Lazy<IDatabaseStatusService>(provider.GetRequiredService<IDatabaseStatusService>), provider.GetRequiredService<DatabaseConnectionFactory>(),
            provider.GetRequiredService<AuditContext>(), provider.GetRequiredService<ILogger<SharedSecretStore>>(),
            new Lazy<IAuditService>(provider.GetRequiredService<IAuditService>), new Lazy<Application.Security.IUserDirectory>(provider.GetRequiredService<Application.Security.IUserDirectory>)));
        services.AddSingleton<ICredentialMetadataStore>(provider => new JsonCredentialMetadataStore(
            provider.GetRequiredService<PathSettings>().CredentialDirectory, provider.GetRequiredService<ILogger<JsonCredentialMetadataStore>>()));

        services.AddSingleton<IniWorkspaceStore>();
        services.AddSingleton<IniCustomerProfileStore>();
        services.AddSingleton<SqlEngagementRepository>();
        services.AddSingleton<IEngagementRepository>(provider => provider.GetRequiredService<SqlEngagementRepository>());
        services.AddSingleton<ICustomerProfileRepository>(provider => provider.GetRequiredService<SqlEngagementRepository>());
        services.AddSingleton<IWorkspaceRepository>(provider => provider.GetRequiredService<SqlEngagementRepository>());
        // File stores stay the source of truth for the UI; saves are mirrored into the application database when it is ready.
        services.AddSingleton<LocalFileImporter>();
        services.AddSingleton<ClientRegistration>();
        services.AddSingleton<Application.Themes.IThemeRepository, SqlThemeRepository>();
        services.AddSingleton(provider => new SqlSettingsStore(provider.GetRequiredService<DatabaseConnectionFactory>(), Environment.MachineName));
        services.AddSingleton<ISharedSettingsStore>(provider => new SharedSettingsStore(
            new Lazy<IDatabaseStatusService>(provider.GetRequiredService<IDatabaseStatusService>), provider.GetRequiredService<SqlSettingsStore>()));
        // Settings are saved to the database; the INI writer remains for Database.ini and for saves before a database exists.
        if (services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IConfigurationFileWriter)) is { ImplementationFactory: { } iniFactory } iniWriter)
        {
            services.Remove(iniWriter);
            services.AddSingleton<IConfigurationFileWriter>(provider => new RoutingConfigurationWriter((IConfigurationFileWriter)iniFactory(provider),
                provider.GetRequiredService<ISharedSettingsStore>(), provider.GetRequiredService<IRedactionService>(), provider.GetRequiredService<AuditContext>()));
        }
        services.AddSingleton<IWorkspaceStore, DatabaseFirstWorkspaceStore>();
        services.AddSingleton<ICustomerProfileStore, DatabaseFirstCustomerProfileStore>();
        services.AddSingleton<IApplicationStateStore>(provider => new JsonApplicationStateStore(
            provider.GetRequiredService<PathSettings>().StateDirectory, provider.GetRequiredService<ILogger<JsonApplicationStateStore>>()));

        services.AddSingleton(provider => new DatabaseConnectionFactory(provider.GetRequiredService<LocalSecrets>().Store, provider.GetRequiredService<PathSettings>()));
        services.AddSingleton<DatabaseErrorTranslator>();
        services.AddSingleton<IDatabaseProvisioningService, DatabaseProvisioningService>();
        services.AddSingleton<IDatabaseMoveService, SqliteToSqlServerMover>();
        services.AddSingleton<Application.Governance.IChangeRequestTemplateRepository, SqlChangeRequestTemplateRepository>();
        services.AddSingleton<Application.Reference.IIndustryRepository>(provider => new SqlIndustryRepository(provider.GetRequiredService<IDatabaseStatusService>(),
            provider.GetRequiredService<DatabaseConnectionFactory>(), provider.GetRequiredService<AuditContext>(), provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<Application.Security.IUserDirectory>()));
        services.AddSingleton<Application.Tenants.ITenantInventoryRepository, SqlTenantInventoryRepository>();
        services.AddSingleton<IDatabaseHealthService, DatabaseHealthService>();
        services.AddSingleton<Application.Planning.IPlanningRepository, Persistence.SqlPlanningRepository>();

        services.AddSingleton<IMicrosoftAuthenticationService, MsalMicrosoftAuthenticationService>();
        services.AddSingleton<IMicrosoftGraphClientFactory, MicrosoftGraphClientFactory>();
        services.AddSingleton<Application.Governance.ITenantChangeWriter, GraphTenantChangeWriter>();
        services.AddSingleton<IAppRegistrationService, GraphAppRegistrationService>();

        services.AddSingleton<IPolicyRepositoryScanner, FilePolicyRepositoryScanner>();
        services.AddSingleton<EmbeddedCatalogService>();
        services.AddSingleton<ICatalogEditRepository>(provider => new SqlCatalogEditRepository(provider.GetRequiredService<IDatabaseStatusService>(),
            provider.GetRequiredService<DatabaseConnectionFactory>(), provider.GetRequiredService<Application.Auditing.AuditContext>(), provider.GetRequiredService<TimeProvider>(),
            provider.GetService<Application.Security.IUserDirectory>()));
        services.AddSingleton<IEditableCatalogService>(provider => new EditableCatalogService(provider.GetRequiredService<EmbeddedCatalogService>().Catalog,
            provider.GetRequiredService<ICatalogEditRepository>(), provider.GetRequiredService<ILogger<EditableCatalogService>>()));
        services.AddSingleton<ICatalogService>(provider => provider.GetRequiredService<IEditableCatalogService>());
        services.AddSingleton<ICatalogDatabaseStore, SqlCatalogStore>();
        services.AddSingleton<KnowledgeBase.SqlKnowledgeBaseStore>();
        services.AddSingleton<KnowledgeBase.SqlBaselineKnowledgeStore>();
        services.AddSingleton<KnowledgeBase.SqlPhase3KnowledgeStore>();
        services.AddSingleton<Application.KnowledgeBase.IKnowledgeBaseStore>(provider => new Application.KnowledgeBase.CompositeKnowledgeBaseStore(
        [
            provider.GetRequiredService<KnowledgeBase.SqlKnowledgeBaseStore>(), provider.GetRequiredService<KnowledgeBase.SqlBaselineKnowledgeStore>(),
            provider.GetRequiredService<KnowledgeBase.SqlPhase3KnowledgeStore>()
        ]));
        services.AddSingleton<IReferenceSeedStore, SqlReferenceSeedStore>();
        services.AddSingleton<SqlGovernanceStore>();
        services.AddSingleton<Application.Governance.ICustomerExceptionRepository>(provider => provider.GetRequiredService<SqlGovernanceStore>());
        services.AddSingleton<Application.Governance.ISpecificationRepository>(provider => provider.GetRequiredService<SqlGovernanceStore>());
        services.AddSingleton<Application.Governance.IBaselineRepository>(provider => provider.GetRequiredService<SqlGovernanceStore>());
        services.AddSingleton<Application.Governance.IChangeRequestRepository>(provider => provider.GetRequiredService<SqlGovernanceStore>());
        services.AddSingleton<Application.Governance.IEntityAuditRepository>(provider => provider.GetRequiredService<SqlGovernanceStore>());
        services.AddSingleton<Application.Scheduling.IBackgroundScheduleHost, Scheduling.WindowsTaskSchedulerHost>();
        services.AddSingleton<Assessments.GraphTenantDataCollector>();
        services.AddSingleton<Assessments.PowerShellTenantDataCollector>();
        services.AddSingleton<ITenantDataCollector, Assessments.CompositeTenantDataCollector>();
        services.AddSingleton<IAssessmentEvaluator, Assessments.AssessmentEvaluator>();
        return services;
    }
}
