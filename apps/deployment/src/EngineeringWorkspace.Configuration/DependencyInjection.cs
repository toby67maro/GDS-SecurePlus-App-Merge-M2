using EngineeringWorkspace.Application.Configuration;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Configuration.Snapshot;
using EngineeringWorkspace.Configuration.Writing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Configuration;

public static class DependencyInjection
{
    /// <summary>Registers the validated snapshot and each typed settings record as singletons.</summary>
    public static IServiceCollection AddEngineeringWorkspaceConfiguration(this IServiceCollection services, ConfigurationSnapshot snapshot)
    {
        services.AddSingleton<IConfigurationSnapshot>(snapshot);
        services.AddSingleton<IConfigurationDiagnostics>(snapshot);
        services.AddSingleton(snapshot.Application);
        services.AddSingleton(snapshot.Paths);
        services.AddSingleton(snapshot.Workspace);
        services.AddSingleton(snapshot.ImportExport);
        services.AddSingleton(snapshot.Credentials);
        services.AddSingleton(snapshot.Branding);
        services.AddSingleton(snapshot.Features);
        services.AddSingleton(snapshot.Navigation);
        services.AddSingleton(snapshot.Integrations);
        services.AddSingleton(snapshot.Database);
        services.AddSingleton(snapshot.Logging);
        services.AddSingleton(snapshot.GoldenTenant);
        services.AddSingleton(snapshot.Repositories);
        services.AddSingleton<IConfigurationFileWriter>(provider => new IniConfigurationFileWriter(
            snapshot.Paths.ConfigurationDirectory,
            provider.GetRequiredService<IRedactionService>(),
            provider.GetRequiredService<ILogger<IniConfigurationFileWriter>>()));
        return services;
    }
}
