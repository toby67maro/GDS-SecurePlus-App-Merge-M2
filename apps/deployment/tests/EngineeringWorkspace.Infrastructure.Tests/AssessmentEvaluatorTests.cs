using System.Text.Json;
using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Infrastructure.Assessments;
using EngineeringWorkspace.Infrastructure.Catalog;
using FluentAssertions;

namespace EngineeringWorkspace.Infrastructure.Tests;

public sealed class AssessmentEvaluatorTests
{
    private static readonly ControlCatalog Catalog = new EmbeddedCatalogService().Catalog;

    private static TenantDataset Dataset(string name, params object[] items) =>
        new(name, "test", DatasetStatus.Collected, items.Select(item => JsonSerializer.SerializeToElement(item)).ToArray(), null);

    private static TenantSnapshot Snapshot(params TenantDataset[] datasets) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, datasets.ToDictionary(dataset => dataset.Name));

    [Fact]
    public void Conditional_access_policy_deployed_from_the_baseline_matches_by_name_and_state()
    {
        var control = Catalog.Controls.First(item => item.Kind == CatalogKinds.ConditionalAccess);
        using var body = JsonDocument.Parse(control.Body);
        var deployed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(control.Body)!;
        deployed["displayName"] = JsonSerializer.SerializeToElement(control.Number + "-PROD-" + control.Name);
        deployed["state"] = JsonSerializer.SerializeToElement("enabled");
        var snapshot = Snapshot(Dataset(TenantDatasets.ConditionalAccessPolicies, deployed));

        var results = new AssessmentEvaluator().Evaluate(Catalog, snapshot);

        results.Single(result => result.ControlId == control.Id).Result.Should().Be(AssessmentResultValues.Pass);
        results.Where(result => result.Area == "Conditional Access" && result.ControlId != control.Id).Should().OnlyContain(result =>
            result.Result == AssessmentResultValues.Missing || result.Result == AssessmentResultValues.Pass);
    }

    [Fact]
    public void Settings_catalog_controls_pass_when_the_setting_is_configured_in_any_policy_and_fail_when_different()
    {
        var control = Catalog.Find("ES-1000")!;
        var expected = control.Expected.Single();
        var instance = new Dictionary<string, object>
        {
            ["@odata.type"] = "#microsoft.graph.deviceManagementConfigurationChoiceSettingInstance",
            ["settingDefinitionId"] = expected.Path,
            ["choiceSettingValue"] = new { value = expected.Value.GetString(), children = Array.Empty<object>() }
        };
        var policy = new { name = "Contoso AV", settings = new[] { new { settingInstance = instance } } };
        var pass = new AssessmentEvaluator().Evaluate(Catalog, Snapshot(Dataset(TenantDatasets.ConfigurationPolicies, policy)));
        pass.Single(result => result.ControlId == "ES-1000").Result.Should().Be(AssessmentResultValues.Pass);

        instance["choiceSettingValue"] = new { value = expected.Value.GetString() + "_other", children = Array.Empty<object>() };
        var policyDifferent = new { name = "Contoso AV", settings = new[] { new { settingInstance = instance } } };
        var fail = new AssessmentEvaluator().Evaluate(Catalog, Snapshot(Dataset(TenantDatasets.ConfigurationPolicies, policyDifferent)));
        fail.Single(result => result.ControlId == "ES-1000").Result.Should().Be(AssessmentResultValues.Fail);
    }

    [Fact]
    public void Tenant_checks_use_graph_data_and_missing_permissions_are_not_evaluated()
    {
        var snapshot = new TenantSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, new Dictionary<string, TenantDataset>
        {
            [TenantDatasets.AuthorizationPolicy] = Dataset(TenantDatasets.AuthorizationPolicy, new
            {
                guestUserRoleId = "10dae51f-b6af-4016-8d66-8c2a99b929b3",
                defaultUserRolePermissions = new { allowedToCreateApps = true, permissionGrantPoliciesAssigned = new[] { "ManagePermissionGrantsForSelf.microsoft-user-default-low" } }
            }),
            [TenantDatasets.ConditionalAccessPolicies] = Dataset(TenantDatasets.ConditionalAccessPolicies, new
            {
                displayName = "Require MFA",
                state = "enabled",
                conditions = new { users = new { includeUsers = new[] { "All" } }, applications = new { includeApplications = new[] { "All" } } },
                grantControls = new { @operator = "OR", builtInControls = new[] { "mfa" } }
            }),
            [TenantDatasets.Users] = new(TenantDatasets.Users, "test", DatasetStatus.Forbidden, [], "HTTP 403")
        });

        var results = new AssessmentEvaluator().Evaluate(Catalog, snapshot).ToDictionary(result => result.ControlId);

        results["CHK-mfa_is_enforced_for_all_users"].Result.Should().Be(AssessmentResultValues.Pass);
        results["CHK-guest_users_have_limited_access_to_properties_and_memberships_of_directory_objects"].Result.Should().Be(AssessmentResultValues.Pass);
        results["CHK-only_admins_shall_be_allowed_to_register_3rd_party_applications"].Result.Should().Be(AssessmentResultValues.Fail);
        results["CHK-non-admin_users_shall_be_prevented_from_providing_consent_to_3rd_party_applications"].Result.Should().Be(AssessmentResultValues.Fail);
        results["CHK-dormant_accounts_are_disabled_after_45_days"].Result.Should().Be(AssessmentResultValues.NotEvaluated);
        results["CHK-inbound_antispam_protections_shall_be_enabled"].Result.Should().Be(AssessmentResultValues.Manual);
        results.Values.Should().NotContain(result => result.ControlId.StartsWith("STD-", StringComparison.Ordinal) || result.ControlId.StartsWith("MSB-", StringComparison.Ordinal));
    }
}
