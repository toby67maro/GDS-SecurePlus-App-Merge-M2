using System.Text.Json;
using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Infrastructure.Assessments;
using EngineeringWorkspace.Infrastructure.Catalog;
using FluentAssertions;

namespace EngineeringWorkspace.Infrastructure.Tests;

/// <summary>Tenant-setting controls (TS-): rule syntax, licence gating and the shipped catalogue content.</summary>
public sealed class TenantSettingRulesTests
{
    private static readonly ControlCatalog Catalog = new EmbeddedCatalogService().Catalog;

    private static TenantDataset Dataset(string name, params object[] items) =>
        new(name, "test", DatasetStatus.Collected, items.Select(item => JsonSerializer.SerializeToElement(item)).ToArray(), null);

    private static TenantSnapshot Snapshot(params TenantDataset[] datasets) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, datasets.ToDictionary(dataset => dataset.Name));

    private static CatalogControl Control(string rule, string? license = null)
    {
        var template = Catalog.Find("TS-ENT-001")!;
        var attributes = new Dictionary<string, string>(template.Attributes) { [TenantSettingRules.RuleAttribute] = rule };
        if (license is not null) attributes["License requirement"] = license;
        else attributes.Remove("License requirement");
        return template with { Id = "TS-TEST-001", Attributes = attributes };
    }

    private static string Evaluate(string rule, params TenantDataset[] datasets) => TenantSettingRules.Evaluate(Control(rule), Snapshot(datasets)).Result;

    private static readonly object MethodsPolicy = new
    {
        policyMigrationState = "migrationComplete",
        authenticationMethodConfigurations = new object[]
        {
            new { id = "Sms", state = "disabled" },
            new { id = "Fido2", state = "enabled" },
            new { id = "MicrosoftAuthenticator", state = "enabled", featureSettings = new { displayAppInformationRequiredState = new { state = "enabled" } } }
        }
    };

    [Fact]
    public void Array_filters_and_nested_paths_select_the_right_value()
    {
        var dataset = Dataset("authenticationMethodsPolicy", MethodsPolicy);
        Evaluate("""{"dataset":"authenticationMethodsPolicy","path":"authenticationMethodConfigurations[id=Sms].state","op":"eq","value":"disabled"}""", dataset)
            .Should().Be(AssessmentResultValues.Pass);
        Evaluate("""{"dataset":"authenticationMethodsPolicy","path":"authenticationMethodConfigurations[id=Fido2].state","op":"eq","value":"disabled"}""", dataset)
            .Should().Be(AssessmentResultValues.Fail);
        Evaluate("""{"dataset":"authenticationMethodsPolicy","path":"authenticationMethodConfigurations[id=MicrosoftAuthenticator].featureSettings.displayAppInformationRequiredState.state","op":"in","value":["enabled","default"]}""", dataset)
            .Should().Be(AssessmentResultValues.Pass);
        Evaluate("""{"dataset":"authenticationMethodsPolicy","path":"authenticationMethodConfigurations[id=Voice].state","op":"eq","value":"disabled"}""", dataset)
            .Should().Be(AssessmentResultValues.Fail, "a setting that is not present cannot pass an equality check");
    }

    [Fact]
    public void Where_filters_odata_types_numbers_and_match_any_work_together()
    {
        var locations = Dataset("namedLocations",
            new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.countryNamedLocation", ["id"] = "c1" },
            new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.ipNamedLocation", ["id"] = "i1", ["isTrusted"] = false },
            new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.ipNamedLocation", ["id"] = "i2", ["isTrusted"] = true });
        Evaluate("""{"dataset":"namedLocations","path":"isTrusted","op":"eq","value":true,"match":"any","where":"@odata.type=#microsoft.graph.ipNamedLocation"}""", locations)
            .Should().Be(AssessmentResultValues.Pass);
        Evaluate("""{"dataset":"namedLocations","path":"isTrusted","op":"eq","value":true,"where":"@odata.type=#microsoft.graph.ipNamedLocation"}""", locations)
            .Should().Be(AssessmentResultValues.Fail, "match defaults to all");

        var settings = Dataset("groupSettings", new { displayName = "Password Rule Settings", values = new[] { new { name = "LockoutThreshold", value = "10" } } });
        Evaluate("""{"dataset":"groupSettings","path":"values[name=LockoutThreshold].value","op":"lte","value":10,"where":"displayName=Password Rule Settings"}""", settings)
            .Should().Be(AssessmentResultValues.Pass, "numbers stored as strings compare numerically");
    }

    [Fact]
    public void Contains_with_a_list_needs_every_value_and_notcontains_needs_none()
    {
        var policy = Dataset("exo:Get-RoleAssignmentPolicy", new { Identity = "Default Role Assignment Policy", AssignedRoles = new[] { "MyBaseOptions", "My Marketplace Apps" } });
        Evaluate("""{"dataset":"exo:Get-RoleAssignmentPolicy","path":"AssignedRoles","op":"notcontains","value":["My Custom Apps","My Marketplace Apps"]}""", policy)
            .Should().Be(AssessmentResultValues.Fail);
        Evaluate("""{"dataset":"exo:Get-RoleAssignmentPolicy","path":"AssignedRoles","op":"contains","value":["MyBaseOptions","My Marketplace Apps"]}""", policy)
            .Should().Be(AssessmentResultValues.Pass);
        Evaluate("""{"dataset":"exo:Get-RoleAssignmentPolicy","path":"AssignedRoles","op":"contains","value":["MyBaseOptions","My Custom Apps"]}""", policy)
            .Should().Be(AssessmentResultValues.Fail);
    }

    [Fact]
    public void Composite_rules_need_every_part_and_report_each_one()
    {
        var federation = Dataset("teams:Get-CsTenantFederationConfiguration", new { AllowTeamsConsumer = false, AllowTeamsConsumerInbound = true });
        const string rule = """{"all":[{"dataset":"teams:Get-CsTenantFederationConfiguration","path":"AllowTeamsConsumer","op":"eq","value":false},{"dataset":"teams:Get-CsTenantFederationConfiguration","path":"AllowTeamsConsumerInbound","op":"eq","value":false}]}""";
        var result = TenantSettingRules.Evaluate(Control(rule), Snapshot(federation));
        result.Result.Should().Be(AssessmentResultValues.Fail);
        result.Evidence.Should().Contain("OK ").And.Contain("NOT MET ");

        TenantSettingRules.Evaluate(Control(rule), Snapshot()).Result.Should().Be(AssessmentResultValues.NotEvaluated, "the PowerShell dataset was not collected");
    }

    [Fact]
    public void A_missing_licence_reports_not_applicable_instead_of_a_gap()
    {
        const string rule = """{"dataset":"conditionalAccessPolicies","path":"conditions.signInRiskLevels","op":"notempty","where":"state=enabled","requires":{"dataset":"subscribedSkus","path":"servicePlans[*].servicePlanName","op":"in","value":["AAD_PREMIUM_P2"],"match":"any"}}""";
        var policies = Dataset("conditionalAccessPolicies", new { state = "enabled", conditions = new { signInRiskLevels = Array.Empty<string>() } });
        var p1 = Dataset("subscribedSkus", new { skuPartNumber = "SPB", servicePlans = new[] { new { servicePlanName = "AAD_PREMIUM" } } });
        var p2 = Dataset("subscribedSkus", new { skuPartNumber = "SPE_E5", servicePlans = new[] { new { servicePlanName = "AAD_PREMIUM" }, new { servicePlanName = "AAD_PREMIUM_P2" } } });

        var notLicensed = TenantSettingRules.Evaluate(Control(rule, "Microsoft Entra ID P2"), Snapshot(policies, p1));
        notLicensed.Result.Should().Be(AssessmentResultValues.Info);
        notLicensed.Evidence.Should().Contain("Microsoft Entra ID P2");
        TenantSettingRules.Evaluate(Control(rule), Snapshot(policies, p2)).Result.Should().Be(AssessmentResultValues.Fail);
        AssessmentResultValues.IsScored(AssessmentResultValues.Info).Should().BeFalse();
    }

    [Fact]
    public void Manual_tenant_settings_report_the_click_path()
    {
        var control = Catalog.Find("TS-ENT-033")!;
        control.Evaluation.Should().Be(CatalogEvaluations.Manual);
        var result = new AssessmentEvaluator().Evaluate(Catalog, Snapshot()).Single(item => item.ControlId == "TS-ENT-033");
        result.Result.Should().Be(AssessmentResultValues.Manual);
        result.Evidence.Should().Contain("Password reset");
    }

    [Fact]
    public void The_catalogue_covers_every_listed_microsoft_area_with_valid_rules()
    {
        var settings = Catalog.Controls.Where(control => control.Kind == CatalogKinds.TenantSetting).ToArray();
        settings.Should().HaveCountGreaterThanOrEqualTo(190);
        settings.Select(control => control.Product).Distinct().Should().Contain([
            "Microsoft Entra ID", "Conditional Access", "Microsoft Intune", "Microsoft Defender XDR", "Exchange Online and Defender for Office 365",
            "SharePoint and OneDrive", "Microsoft Teams", "Microsoft Purview", "Copilot and AI", "Power Platform", "Microsoft 365 admin center",
            "Global Secure Access", "Windows 365 and Azure Virtual Desktop", "Hybrid identity", "Microsoft Azure"
        ]);
        foreach (var control in settings.Where(control => control.Evaluation == CatalogEvaluations.TenantSetting))
        {
            var rule = TenantSettingRules.Parse(control);
            rule.Should().NotBeNull(control.Id);
            (rule!.Dataset.Length > 0 || rule.All is { Count: > 0 }).Should().BeTrue(control.Id);
            new[] { "eq", "ne", "in", "notin", "gte", "lte", "contains", "notcontains", "exists", "notexists", "empty", "notempty" }.Should().Contain(rule.Op, control.Id);
        }

        settings.Where(control => control.Product == "Microsoft Azure").Should().OnlyContain(control =>
            control.Evaluation == CatalogEvaluations.Manual || control.Id == "TS-AZR-001", "Azure is assessed automatically with Lighthouse in 5.0");
    }

    [Fact]
    public void Every_tenant_setting_dataset_is_collected_by_graph_dns_or_powershell()
    {
        var known = GraphTenantDataCollector.DatasetNames.Concat(PowerShellTenantDataCollector.DatasetNames).ToHashSet(StringComparer.Ordinal);
        var used = Catalog.Controls.Where(control => control.Evaluation == CatalogEvaluations.TenantSetting)
            .Select(TenantSettingRules.Parse).OfType<TenantSettingRules.Rule>()
            .SelectMany(rule => new[] { rule, rule.Requires }.Concat(rule.All ?? []))
            .OfType<TenantSettingRules.Rule>().Where(rule => rule.Dataset.Length > 0).Select(rule => rule.Dataset).Distinct();
        used.Should().OnlyContain(dataset => known.Contains(dataset));
    }

    [Fact]
    public void Licensed_entra_settings_evaluate_against_a_realistic_tenant()
    {
        var snapshot = Snapshot(
            Dataset("authorizationPolicy", new
            {
                allowInvitesFrom = "adminsAndGuestInviters",
                guestUserRoleId = "2af84b1e-32c8-42b7-82bc-daa82404023b",
                defaultUserRolePermissions = new { allowedToCreateApps = true, permissionGrantPoliciesAssigned = new[] { "ManagePermissionGrantsForSelf.microsoft-user-default-legacy" } }
            }),
            Dataset("authenticationMethodsPolicy", MethodsPolicy));
        var results = new AssessmentEvaluator().Evaluate(Catalog, snapshot).Where(result => result.ControlId.StartsWith("TS-ENT-", StringComparison.Ordinal))
            .ToDictionary(result => result.ControlId, result => result.Result);
        results["TS-ENT-001"].Should().Be(AssessmentResultValues.Fail);
        results["TS-ENT-005"].Should().Be(AssessmentResultValues.Pass);
        results["TS-ENT-006"].Should().Be(AssessmentResultValues.Pass);
        results["TS-ENT-009"].Should().Be(AssessmentResultValues.Fail);
        results["TS-ENT-011"].Should().Be(AssessmentResultValues.Pass);
        results["TS-ENT-013"].Should().Be(AssessmentResultValues.Pass);
        results["TS-ENT-021"].Should().Be(AssessmentResultValues.Pass);
        results["TS-ENT-010"].Should().Be(AssessmentResultValues.NotEvaluated, "adminConsentRequestPolicy was not read");
    }
}
