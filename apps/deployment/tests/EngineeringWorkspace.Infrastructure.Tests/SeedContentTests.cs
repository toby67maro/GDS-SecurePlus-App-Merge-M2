using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Infrastructure.Catalog;
using FluentAssertions;

namespace EngineeringWorkspace.Infrastructure.Tests;

/// <summary>
/// Build validation of the embedded seed: the release workflow runs the tests before packaging, so a regenerated
/// catalog that loses data fails the build. Raise the minimums deliberately when the seed grows.
/// </summary>
public sealed class SeedContentTests
{
    private static readonly ControlCatalog Catalog = EmbeddedCatalogService.Load();

    [Theory]
    [InlineData(CatalogKinds.ConditionalAccess, 60)]
    [InlineData(CatalogKinds.EndpointSecurity, 130)]
    [InlineData(CatalogKinds.Intune, 1450)] // 4.0 beta.2: 36 Internet Explorer settings and 66 exact duplicates removed on purpose (1,493 left)
    [InlineData(CatalogKinds.AssessmentCheck, 75)]
    [InlineData(CatalogKinds.Standard, 500)]
    [InlineData(CatalogKinds.MicrosoftBaseline, 800)]
    public void Seed_keeps_its_controls(string kind, int minimum) =>
        Catalog.Controls.Count(control => control.Kind == kind).Should().BeGreaterThanOrEqualTo(minimum, "the seed must not lose {0} controls", kind);

    [Fact]
    public void Seed_keeps_its_reference_data()
    {
        Catalog.SeedVersion.Should().NotBe("1");
        Catalog.PolicySets.Should().HaveCountGreaterThanOrEqualTo(11);
        Catalog.FindPolicySet("Golden-Tenant")!.ControlIds.Should().HaveCountGreaterThanOrEqualTo(150);
        Catalog.SolutionAreas.Should().HaveCountGreaterThanOrEqualTo(10);
        Catalog.ExperienceAreas.Should().HaveCountGreaterThanOrEqualTo(8);
        Catalog.Specifications.Should().HaveCountGreaterThanOrEqualTo(3).And.OnlyContain(spec => spec.ControlIds.Count > 0);
        Catalog.Baselines.Should().HaveCountGreaterThanOrEqualTo(3);
        Catalog.Controls.Count(control => control.Remediation.HasAny).Should().BeGreaterThanOrEqualTo(2500);
        Catalog.Controls.Should().OnlyContain(control => control.Implementation.EstimatedMinutes > 0 && control.SolutionArea.Length > 0);
    }

    [Fact]
    public void Control_ids_are_unique_and_aliases_resolve()
    {
        Catalog.Controls.Select(control => control.Id).Should().OnlyHaveUniqueItems();
        Catalog.Aliases.Should().NotBeEmpty();
        foreach (var alias in Catalog.Aliases)
        {
            Catalog.Controls.Should().NotContain(control => control.Id == alias.Alias);
            Catalog.Find(alias.Alias)!.Id.Should().Be(alias.ControlId);
        }

        Catalog.Find("ES-1317-2")!.Id.Should().Be("ES-1317");
        Catalog.Find("CA-203-2")!.VariantOf.Should().Be("CA-203");
        Catalog.Controls.Where(control => control.IsVariant).Should().OnlyContain(control => Catalog.Find(control.VariantOf) != null);
    }

    [Theory]
    [InlineData("frameworks", 250)]
    [InlineData("requirements", 40000)]
    [InlineData("maps", 70000)]
    [InlineData("aliases", 80)]
    [InlineData("scfControls", 1500)]
    [InlineData("objectives", 11000)]
    [InlineData("evidence", 400)]
    [InlineData("risks", 60)]
    [InlineData("threats", 40)]
    [InlineData("controlRisks", 100000)]
    [InlineData("controlThreats", 70000)]
    [InlineData("techniques", 700)]
    [InlineData("techniqueMitigations", 1500)]
    public void Knowledge_base_keeps_its_content(string dataset, int minimum) =>
        EngineeringWorkspace.Infrastructure.KnowledgeBase.EmbeddedKnowledgeBase.Current.Count(dataset).Should().BeGreaterThanOrEqualTo(minimum);

    [Theory]
    [InlineData("baselines", 55)]
    [InlineData("recommendations", 10000)]
    [InlineData("sourceTexts", 9000)]
    [InlineData("settings", 8000)]
    [InlineData("recommendationRequirements", 9000)]
    [InlineData("recommendationSettings", 7000)]
    [InlineData("wbsTasks", 190)]
    [InlineData("questions", 600)]
    [InlineData("methodCoverage", 110)]
    public void Baselines_knowledge_base_keeps_its_content(string dataset, int minimum) =>
        EngineeringWorkspace.Infrastructure.KnowledgeBase.EmbeddedKnowledgeBase.Baselines.Count(dataset).Should().BeGreaterThanOrEqualTo(minimum);

    [Theory]
    [InlineData("recommendations", new[] { 0, 1 })]
    [InlineData("settings", new[] { 0 })]
    [InlineData("settingAliases", new[] { 1, 2 })]
    [InlineData("sections", new[] { 0, 1 })]
    [InlineData("profiles", new[] { 0, 1 })]
    [InlineData("recommendationSettings", new[] { 0, 1, 2 })]
    [InlineData("recommendationProfiles", new[] { 0, 1, 2 })]
    [InlineData("wbsTasks", new[] { 0, 1 })]
    [InlineData("questions", new[] { 1 })]
    [InlineData("methodCoverage", new[] { 1 })]
    public void Baselines_keys_are_unique_as_SQL_Server_compares_them(string dataset, int[] keyColumns)
    {
        var rows = EngineeringWorkspace.Infrastructure.KnowledgeBase.EmbeddedKnowledgeBase.Baselines.Rows(dataset).ToList();
        rows.Should().NotBeEmpty();
        rows.Select(row => EngineeringWorkspace.Infrastructure.KnowledgeBase.SqlKnowledgeBaseStore.Key(keyColumns.Select(index => (object?)row[index].ToString()).ToArray()))
            .GroupBy(key => key, EngineeringWorkspace.Infrastructure.KnowledgeBase.SqlKnowledgeBaseStore.KeyComparer)
            .Where(group => group.Count() > 1).Select(group => group.Key).Should().BeEmpty();
    }

    [Theory]
    [InlineData("licenseSkus", 500)]
    [InlineData("capabilities", 1000)]
    [InlineData("skuCapabilities", 5000)]
    [InlineData("capabilityTechniques", 1500)]
    [InlineData("requirementTechniques", 6000)]
    [InlineData("tierFeatures", 40)]
    [InlineData("checklistItems", 360)]
    [InlineData("configurationChanges", 1700)]
    [InlineData("changeRequirements", 10000)]
    [InlineData("knownIssues", 1000)]
    [InlineData("guidance", 3000)]
    [InlineData("relations", 20000)]
    [InlineData("procedures", 12000)]
    [InlineData("enrichment", 25000)]
    [InlineData("itemScores", 1500)]
    [InlineData("requirementProperties", 1300)]
    [InlineData("requirementCapabilities", 200)]
    public void Phase3_knowledge_base_keeps_its_content(string dataset, int minimum) =>
        EngineeringWorkspace.Infrastructure.KnowledgeBase.EmbeddedKnowledgeBase.Phase3.Count(dataset).Should().BeGreaterThanOrEqualTo(minimum);

    [Theory]
    [InlineData("licenseSkus", new[] { 0 })]
    [InlineData("capabilities", new[] { 0 })]
    [InlineData("skuCapabilities", new[] { 0, 1 })]
    [InlineData("configurationChanges", new[] { 0 })]
    [InlineData("knownIssues", new[] { 0 })]
    [InlineData("checklistItems", new[] { 0 })]
    [InlineData("expectations", new[] { 0 })]
    [InlineData("attributes", new[] { 0 })]
    [InlineData("relations", new[] { 0, 1, 2, 3, 4 })]
    [InlineData("complexityFactors", new[] { 0 })]
    public void Phase3_keys_are_unique_as_SQL_Server_compares_them(string dataset, int[] keyColumns)
    {
        var rows = EngineeringWorkspace.Infrastructure.KnowledgeBase.EmbeddedKnowledgeBase.Phase3.Rows(dataset).ToList();
        rows.Should().NotBeEmpty();
        rows.Select(row => EngineeringWorkspace.Infrastructure.KnowledgeBase.SqlKnowledgeBaseStore.Key(keyColumns.Select(index => (object?)row[index].ToString()).ToArray()))
            .GroupBy(key => key, EngineeringWorkspace.Infrastructure.KnowledgeBase.SqlKnowledgeBaseStore.KeyComparer)
            .Where(group => group.Count() > 1).Select(group => group.Key).Should().BeEmpty();
    }

    /// <summary>
    /// SQL Server's default collation ignores case and trailing spaces, so natural keys that differ only that way collide
    /// in its unique indexes (2.2.0 failed on SQL Server with 19(3)(c)(i) / 19(3)(c)(I)); SQLite tests would not notice.
    /// </summary>
    [Theory]
    [InlineData("requirements", new[] { 0, 1 })]
    [InlineData("maps", new[] { 0, 1, 2, 3, 5 })]
    [InlineData("objectives", new[] { 0, 1, 2 })]
    [InlineData("techniques", new[] { 0 })]
    public void Knowledge_base_keys_are_unique_as_SQL_Server_compares_them(string dataset, int[] keyColumns)
    {
        var rows = EngineeringWorkspace.Infrastructure.KnowledgeBase.EmbeddedKnowledgeBase.Current.Rows(dataset).ToList();
        rows.Should().NotBeEmpty();
        var keys = rows.Select(row => EngineeringWorkspace.Infrastructure.KnowledgeBase.SqlKnowledgeBaseStore.Key(
            keyColumns.Select(index => (object?)row[index].ToString()).ToArray()));
        keys.GroupBy(key => key, EngineeringWorkspace.Infrastructure.KnowledgeBase.SqlKnowledgeBaseStore.KeyComparer)
            .Where(group => group.Count() > 1).Select(group => group.Key).Should().BeEmpty();
    }
}
