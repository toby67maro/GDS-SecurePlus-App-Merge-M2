using EngineeringWorkspace.Application;
using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Application.Workspaces;
using EngineeringWorkspace.Configuration;
using EngineeringWorkspace.Configuration.Loading;
using EngineeringWorkspace.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EngineeringWorkspace.Infrastructure.Tests;

public sealed class ServiceRegistrationTests
{
    [Fact]
    public void Application_and_infrastructure_services_resolve_with_database_mirroring()
    {
        var root = Directory.CreateTempSubdirectory("secureplus-di-");
        try
        {
            var snapshot = ConfigurationLoader.Load(new ConfigurationLoadRequest(root.FullName, root.FullName, root.FullName, [], new Dictionary<string, string?>(), null, null, "1.0.0"),
                new RedactionService()).Snapshot;
            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddEngineeringWorkspaceConfiguration(snapshot);
            services.AddEngineeringWorkspaceApplication();
            services.AddEngineeringWorkspaceInfrastructure("tester");
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

            provider.GetRequiredService<IWorkspaceStore>().Should().BeOfType<DatabaseFirstWorkspaceStore>();
            provider.GetRequiredService<ICustomerProfileStore>().Should().BeOfType<DatabaseFirstCustomerProfileStore>();
            provider.GetRequiredService<IEngagementService>().IsDatabaseAvailable.Should().BeFalse();
            provider.GetRequiredService<IDatabaseProvisioningService>().RequiredSchemaVersion.Should().Be(12);
            provider.GetRequiredService<Application.Catalog.ICatalogDatabaseStore>().Should().BeOfType<SqlCatalogStore>().Which.IsAvailable.Should().BeFalse();
            provider.GetRequiredService<Application.Catalog.ICatalogSyncService>().Should().NotBeNull();
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
