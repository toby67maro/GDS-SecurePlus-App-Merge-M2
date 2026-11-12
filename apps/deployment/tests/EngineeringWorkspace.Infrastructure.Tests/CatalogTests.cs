using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Infrastructure.Catalog;
using FluentAssertions;

namespace EngineeringWorkspace.Infrastructure.Tests;

public sealed class CatalogTests
{
    [Fact]
    public void Embedded_catalog_loads_every_kind_with_unique_ids_and_resolved_policy_sets()
    {
        var catalog = new EmbeddedCatalogService().Catalog;

        catalog.Controls.Should().HaveCountGreaterThan(3000);
        catalog.Controls.Select(control => control.Id).Should().OnlyHaveUniqueItems();
        catalog.Controls.Select(control => control.Kind).Distinct().Should().Contain(
            [CatalogKinds.ConditionalAccess, CatalogKinds.EndpointSecurity, CatalogKinds.Intune, CatalogKinds.AssessmentCheck, CatalogKinds.Standard, CatalogKinds.MicrosoftBaseline]);
        catalog.Controls.Where(control => control.Evaluation == CatalogEvaluations.SettingsCatalog).Should().OnlyContain(control => control.Expected.Count > 0);
        catalog.FindPolicySet("Golden-Tenant")!.ControlIds.Should().HaveCountGreaterThan(100);
        catalog.ControlsOf(["01-Identity-Protection"]).Should().OnlyContain(control => control.Kind == CatalogKinds.ConditionalAccess);
        catalog.Find("CHK-mfa_is_enforced_for_all_users")!.Severity.Should().Be("Critical");
    }
}
