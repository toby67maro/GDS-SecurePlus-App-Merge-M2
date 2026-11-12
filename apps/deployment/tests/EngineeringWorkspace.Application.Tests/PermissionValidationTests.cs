using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Tenants;
using EngineeringWorkspace.Domain.Tenants;
using FluentAssertions;

namespace EngineeringWorkspace.Application.Tests;

public sealed class PermissionValidationTests
{
    private readonly PermissionValidationService _service = new();

    [Fact]
    public void Every_capability_is_defined_and_the_legacy_default_means_all_read_capabilities()
    {
        var rows = _service.Describe([TenantCapability.TenantDiscovery]);
        rows.Should().NotContain(row => row.Status.Contains("Not yet defined", StringComparison.Ordinal));
        rows.Where(row => row.RequiredPermission != "Not required" && row.Capability != TenantCapability.PolicyDeployment).Should().OnlyContain(row => row.Configured);
        rows.Where(row => row.Capability == TenantCapability.PolicyDeployment).Should().OnlyContain(row => !row.Configured,
            "4.0 beta.2: write permissions belong to the Deploy registration only");
        _service.RequiredApplicationPermissions([TenantCapability.TenantDiscovery]).Should().Contain(["Policy.Read.All", "DeviceManagementConfiguration.Read.All", "AuditLog.Read.All"]);
    }

    [Fact]
    public void Granted_or_more_privileged_permissions_pass_and_missing_ones_fail()
    {
        string[] granted = ["Directory.Read.All", "Policy.Read.All", "DeviceManagementConfiguration.ReadWrite.All", "RoleManagement.Read.All"];
        var rows = _service.Validate(Enum.GetValues<TenantCapability>(), granted);

        rows.Single(row => row.RequiredPermission == "Organization.Read.All" && row.Capability == TenantCapability.TenantDiscovery).Validated.Should().Be(CheckState.Passed);
        rows.Single(row => row.RequiredPermission == "DeviceManagementConfiguration.Read.All").Validated.Should().Be(CheckState.Passed);
        rows.Single(row => row.RequiredPermission == "RoleManagement.Read.Directory").Validated.Should().Be(CheckState.Passed);
        rows.Single(row => row.RequiredPermission == "AuditLog.Read.All").Validated.Should().Be(CheckState.Failed);
        rows.Single(row => row.Capability == TenantCapability.ExchangeAssessment).Validated.Should().Be(CheckState.NotApplicable);
    }
}
