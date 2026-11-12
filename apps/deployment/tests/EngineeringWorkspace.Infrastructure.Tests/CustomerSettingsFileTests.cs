using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Customers;
using EngineeringWorkspace.Infrastructure.FileSystem;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EngineeringWorkspace.Infrastructure.Tests;

/// <summary>4.0 A1: integration and workspace settings were read from Customer.ini but never written, so edits without a database were lost.</summary>
public sealed class CustomerSettingsFileTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("secureplus-customer-ini-");

    [Fact]
    public async Task Integration_and_workspace_settings_are_saved_and_removed_in_the_customer_file()
    {
        var paths = new PathSettings(_root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName,
            _root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName);
        var store = new IniCustomerProfileStore(paths, NullLogger<IniCustomerProfileStore>.Instance);
        var profile = CustomerProfile.Create(new CustomerId("contoso"), "Contoso") with
        {
            IntegrationSettings = new Dictionary<string, string> { ["ConnectWise.CompanyId"] = "1234" },
            WorkspaceSettings = new Dictionary<string, string> { ["Experience.Frameworks"] = "HIPAA", ["Deploy.RING"] = "Pilot", ["Region"] = "South" }
        };
        await store.SaveAsync(profile);
        var loaded = (await store.LoadAsync(profile.Id))!;
        loaded.IntegrationSettings.Should().Contain("ConnectWise.CompanyId", "1234");
        loaded.WorkspaceSettings.Should().HaveCount(3).And.Contain("Deploy.RING", "Pilot");

        await store.SaveAsync(loaded with { WorkspaceSettings = new Dictionary<string, string> { ["Region"] = "North" } });
        (await store.LoadAsync(profile.Id))!.WorkspaceSettings.Should().ContainSingle().Which.Value.Should().Be("North");
    }

    public void Dispose() => _root.Delete(recursive: true);
}
