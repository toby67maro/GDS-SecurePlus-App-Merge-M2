using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Configuration.Loading;
using FluentAssertions;

namespace EngineeringWorkspace.Configuration.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void Defaults_are_available_without_files()
    {
        var empty = Directory.CreateTempSubdirectory();
        try
        {
            var result = ConfigurationLoader.Load(new ConfigurationLoadRequest(empty.FullName, empty.FullName, empty.FullName, [], new Dictionary<string, string?>(), null, null, "1.0.0"),
                new RedactionService()).Snapshot;
            result.Application.DefaultRoute.Should().Be("dashboard/overview");
            result.Application.CompanyName.Should().Be("Global Data Systems");
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    [Fact]
    public void Repository_configuration_loads_without_errors_and_includes_the_new_pages()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var config = Path.Combine(root, "config");
        Directory.Exists(config).Should().BeTrue();
        var data = Directory.CreateTempSubdirectory();
        try
        {
            var load = ConfigurationLoader.Load(new ConfigurationLoadRequest(root, data.FullName, config, [], new Dictionary<string, string?>(), null, null, "1.0.0"),
                new RedactionService());
            load.HasErrors.Should().BeFalse(string.Join("; ", load.Snapshot.ValidationErrors.Select(error => error.Message)));
            load.Snapshot.Branding.ApplicationTitle.Should().Be("Secure Plus Agent");
            load.Snapshot.Features.Features["ApplicationSettings"].Should().BeTrue();
            load.Snapshot.Navigation.Nodes.Should().Contain(node => node.Route == "settings/application-settings");
        }
        finally
        {
            data.Delete(recursive: true);
        }
    }
}
