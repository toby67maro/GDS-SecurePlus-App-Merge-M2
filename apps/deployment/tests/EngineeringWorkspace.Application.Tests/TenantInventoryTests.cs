using System.Text.Json;
using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Reporting;
using EngineeringWorkspace.Application.Tenants;
using FluentAssertions;

namespace EngineeringWorkspace.Application.Tests;

/// <summary>4.0 beta.2: quick/in-depth scans, inventory from scan data, run selection for reports.</summary>
public sealed class TenantInventoryTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static TenantDataset Dataset(string name, params object[] items) =>
        new(name, "test", DatasetStatus.Collected, items.Select(item => JsonSerializer.SerializeToElement(item)).ToArray(), null);

    [Fact]
    public void Inventory_is_built_from_users_groups_counts_members_roles_licences_and_devices()
    {
        var snapshot = new TenantSnapshot(Tenant, DateTimeOffset.UtcNow, new Dictionary<string, TenantDataset>
        {
            [TenantDatasets.SubscribedSkus] = Dataset(TenantDatasets.SubscribedSkus, new { skuId = "sku-1", skuPartNumber = "SPE_E5" }),
            [TenantDatasets.Users] = Dataset(TenantDatasets.Users,
                new { id = "u1", displayName = "Ann Admin", userPrincipalName = "ann@contoso.com", accountEnabled = true, userType = "Member", onPremisesSyncEnabled = (bool?)null,
                    department = "IT", jobTitle = "Engineer", usageLocation = "US", assignedLicenses = new[] { new { skuId = "sku-1" } },
                    signInActivity = new { lastSignInDateTime = "2026-11-01T10:00:00Z" } },
                new { id = "u2", displayName = "Guest", userType = "Guest", accountEnabled = false }),
            [TenantDatasets.RoleAssignments] = Dataset(TenantDatasets.RoleAssignments, new { principalId = "u1", roleDefinitionId = "62e90394-69f5-4237-9190-012177145e10" }),
            [TenantDatasets.RegistrationDetails] = Dataset(TenantDatasets.RegistrationDetails, new { id = "u1", isMfaRegistered = true, methodsRegistered = new[] { "fido2", "microsoftAuthenticatorPush" } }),
            [TenantDatasets.Groups] = Dataset(TenantDatasets.Groups,
                new { id = "g1", displayName = "Store staff", securityEnabled = true, mailEnabled = false, groupTypes = Array.Empty<string>() },
                new { id = "g2", displayName = "All Company", securityEnabled = false, mailEnabled = true, groupTypes = new[] { "Unified", "DynamicMembership" }, membershipRule = "user.accountEnabled -eq true" }),
            [TenantDatasets.GroupMemberCounts] = Dataset(TenantDatasets.GroupMemberCounts, new { groupId = "g1", count = 42L }),
            [TenantDatasets.GroupMembers] = Dataset(TenantDatasets.GroupMembers, new { groupId = "g1", memberId = "u1", type = "user" }),
            [TenantDatasets.ManagedDevices] = Dataset(TenantDatasets.ManagedDevices, new { id = "d1", deviceName = "IPAD-01", operatingSystem = "iPadOS", complianceState = "compliant",
                managedDeviceOwnerType = "company", isEncrypted = true, lastSyncDateTime = "2026-11-07T08:00:00Z" }),
        });

        var inventory = TenantInventoryBuilder.Build(snapshot);

        var ann = inventory.Users!.Single(user => user.ObjectId == "u1");
        ann.Licenses.Should().Equal("SPE_E5");
        ann.AdminRoles.Should().Equal("Global Administrator");
        ann.IsMfaRegistered.Should().BeTrue();
        ann.MfaMethods.Should().Contain("fido2");
        ann.LastSignInUtc.Should().NotBeNull();
        inventory.Users!.Single(user => user.ObjectId == "u2").IsGuest.Should().BeTrue();
        var store = inventory.Groups!.Single(group => group.ObjectId == "g1");
        store.Kind.Should().Be(TenantGroupKinds.Security);
        store.MemberCount.Should().Be(42);
        store.Display.Should().Be("Store staff (Security, 42 members)");
        inventory.Groups!.Single(group => group.ObjectId == "g2").Should().Match<TenantGroup>(group => group.Kind == TenantGroupKinds.Microsoft365 && group.IsDynamic && group.MemberCount == null);
        inventory.Members.Should().ContainSingle().Which.MemberObjectId.Should().Be("u1");
        inventory.MemberGroupIds.Should().Equal("g1");
        inventory.Devices.Should().ContainSingle().Which.Should().Match<TenantDevice>(device => device.Ownership == "company" && device.IsEncrypted == true);
    }

    [Fact]
    public void Collections_not_read_by_the_scan_stay_null_so_the_store_keeps_them()
    {
        var quick = new TenantSnapshot(Tenant, DateTimeOffset.UtcNow, new Dictionary<string, TenantDataset>
        {
            [TenantDatasets.Users] = Dataset(TenantDatasets.Users, new { id = "u1", displayName = "Ann" }),
            [TenantDatasets.Groups] = new(TenantDatasets.Groups, "test", DatasetStatus.Forbidden, [], "403"),
        });
        var inventory = TenantInventoryBuilder.Build(quick);
        inventory.Users.Should().HaveCount(1);
        inventory.Groups.Should().BeNull("forbidden: keep what was stored");
        inventory.Devices.Should().BeNull("a quick scan does not read devices");
        ScanTypes.Includes(ScanTypes.Quick, TenantDatasets.ManagedDevices).Should().BeFalse();
        ScanTypes.Includes(ScanTypes.Quick, TenantDatasets.Groups).Should().BeTrue();
        ScanTypes.Includes(ScanTypes.InDepth, TenantDatasets.ManagedDevices).Should().BeTrue();
        ScanTypes.Normalize("quick").Should().Be(ScanTypes.Quick);
        ScanTypes.Normalize(null).Should().Be(ScanTypes.InDepth);
    }

    [Fact]
    public void Reports_use_the_latest_in_depth_scan_and_fall_back_to_quick_scans()
    {
        AssessmentSummary Run(int id, string type, int day) =>
            new(id, "contoso", null, $"run {id}", AssessmentStatusCodes.Completed, 50, 10, 5, new DateTimeOffset(2026, 11, day, 0, 0, 0, TimeSpan.Zero), null, null, false) { ScanType = type };
        var mixed = new[] { Run(3, ScanTypes.Quick, 8), Run(2, ScanTypes.InDepth, 5), Run(1, ScanTypes.InDepth, 1) };
        RunSelection.PreferInDepth(mixed).Select(run => run.AssessmentRunId).Should().Equal(2, 1);
        RunSelection.PreferInDepth([Run(3, ScanTypes.Quick, 8)]).Should().ContainSingle();
    }

    [Fact]
    public void Linked_groups_come_from_user_groups_and_group_deployment_variables_only_when_they_are_ids()
    {
        var group = Guid.NewGuid();
        var variable = Guid.NewGuid();
        var customer = EngineeringWorkspace.Domain.Customers.CustomerProfile.Create(new EngineeringWorkspace.Domain.Common.CustomerId("contoso"), "Contoso") with
        {
            UserGroups =
            [
                new("g1", "Store staff", "Frontline", EntraGroup: group.ToString()),
                new("g2", "Legacy", "Office", EntraGroup: "Sales Team (typed name)")
            ],
            WorkspaceSettings = new Dictionary<string, string> { ["Deploy.FrontlineQrCodeGroup"] = variable.ToString(), ["Deploy.RING"] = "Pilot" }
        };
        AssessmentService.LinkedGroupIds(customer).Should().BeEquivalentTo([group, variable]);
    }
}
