using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Customers;
using EngineeringWorkspace.Domain.Workspaces;
using EngineeringWorkspace.Infrastructure.Auditing;
using EngineeringWorkspace.Infrastructure.Catalog;
using EngineeringWorkspace.Infrastructure.Database;
using EngineeringWorkspace.Infrastructure.Persistence;
using EngineeringWorkspace.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace EngineeringWorkspace.Infrastructure.Tests;

public sealed class CatalogDatabaseTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("secureplus-catalog-");
    private readonly DatabaseProvisioningService _provisioning;
    private readonly DatabaseConnectionFactory _connections;
    private readonly DatabaseSettings _settings;

    public CatalogDatabaseTests()
    {
        var paths = new PathSettings(_root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName,
            _root.FullName, Path.Combine(_root.FullName, "audit"), _root.FullName, _root.FullName, _root.FullName);
        var context = new AuditContext("tester");
        var redaction = new RedactionService();
        _connections = new DatabaseConnectionFactory(new UnavailableSecretStore(), paths);
        _provisioning = new DatabaseProvisioningService(_connections, new DatabaseErrorTranslator(redaction),
            new JsonLinesAuditService(paths.AuditDirectory, context, redaction, TimeProvider.System, NullLogger<JsonLinesAuditService>.Instance),
            context, new ApplicationSettings("id", "name", "title", "company", "Test", "dashboard/overview", false, false, "1.0.0"),
            TimeProvider.System, NullLogger<DatabaseProvisioningService>.Instance);
        _settings = new DatabaseSettings { Provider = DatabaseProvider.SQLite, DatabaseName = Path.Combine(_root.FullName, "secureplus.db"), CommandTimeoutSeconds = 30 };
    }

    [Fact]
    public async Task Loads_the_full_catalog_into_the_EaaS_tables_and_is_idempotent()
    {
        var status = await ReadyAsync();
        var catalog = EmbeddedCatalogService.Load();
        catalog.ContentHash.Should().HaveLength(64);
        var store = new SqlCatalogStore(new FixedStatus(_settings, status), _connections, TimeProvider.System);
        store.IsAvailable.Should().BeTrue();
        (await store.GetStateAsync()).IsCurrent(catalog).Should().BeFalse();

        var updates = new List<double>();
        var first = await store.SyncAsync(catalog, force: false, new SyncProgress(update => updates.Add(update.Progress)));
        first.Skipped.Should().BeFalse();
        first.Controls.Should().Be(catalog.Controls.Count);
        first.PolicySets.Should().Be(catalog.PolicySets.Count);
        first.Frameworks.Should().BeGreaterThan(5);
        first.Mappings.Should().BeGreaterThan(catalog.Controls.Count);
        updates.Should().NotBeEmpty().And.BeInAscendingOrder();

        var state = await store.GetStateAsync();
        state.IsCurrent(catalog).Should().BeTrue();
        state.Version.Should().Be(catalog.Version);
        state.Controls.Should().Be(catalog.Controls.Count);

        var counts = await CountsAsync();
        counts["secureplus.CatalogControl"].Should().Be(catalog.Controls.Count);
        counts["pol.Artifact"].Should().Be(catalog.Controls.Count);
        counts["pol.ArtifactSetting"].Should().Be(catalog.Controls.Sum(control => control.Expected.Count));
        counts["pol.PolicySet"].Should().Be(catalog.PolicySets.Count);
        counts["pol.PolicySetMember"].Should().Be(first.PolicySetMembers).And.BeGreaterThan(0);
        counts["pol.PolicySetInclude"].Should().Be(9);
        counts["pol.ArtifactMapping"].Should().Be(first.Mappings);
        counts["fw.Requirement"].Should().Be(first.Requirements);
        counts["gds.Standard"].Should().Be(4, "GDS checks, standards, Microsoft baselines and tenant settings");
        counts["gds.StandardItem"].Should().Be(catalog.Controls.Count(control => control.Kind is CatalogKinds.Standard or CatalogKinds.MicrosoftBaseline or CatalogKinds.AssessmentCheck or CatalogKinds.TenantSetting));
        counts["pol.RepositoryVersion"].Should().Be(1);

        // Every flat row links to its artifact; the Golden Tenant baseline lists its composed sets.
        (await ScalarAsync("""SELECT COUNT(*) FROM "secureplus.CatalogControl" c JOIN "pol.Artifact" a ON a.ArtifactId = c.ArtifactId;""")).Should().Be(catalog.Controls.Count);
        (await ScalarAsync("""SELECT COUNT(*) FROM "pol.PolicySet" WHERE IsApprovedBaseline = 1 AND Code = 'Golden-Tenant';""")).Should().Be(1);

        // A second sync is skipped; a forced one replaces the data without duplicates.
        (await store.SyncAsync(catalog, force: false, null)).Skipped.Should().BeTrue();
        var forced = await store.SyncAsync(catalog, force: true, null);
        forced.Skipped.Should().BeFalse();
        var after = await CountsAsync();
        after.Should().BeEquivalentTo(counts);
        (await ScalarAsync("""SELECT COUNT(*) FROM "pol.RepositoryVersion" WHERE StatusCode = 'Current';""")).Should().Be(1);
    }

    [Theory]
    [InlineData("NIST 800-53 AC-2(7)", "NIST-800-53", "Rev 5", "AC-2(7)")]
    [InlineData("CIS v8.1 10.1", "CIS-CONTROLS", "v8.1", "10.1")]
    [InlineData("ISO 27001:2022 A.8.12", "ISO-27001", "2022", "A.8.12")]
    [InlineData("PCI DSS 4.0.1 6.3.3", "PCI-DSS", "4.0.1", "6.3.3")]
    [InlineData("HIPAA 164.312(d)", "HIPAA", "45 CFR 164", "164.312(d)")]
    [InlineData("Microsoft Security Baseline", "MS-SECURITY-BASELINE", "Current", "General")]
    [InlineData("Some Framework X-1", "SOME-FRAMEWORK", "Current", "X-1")]
    public void Parses_framework_references(string label, string framework, string version, string requirement)
    {
        var reference = FrameworkReference.Parse(label);
        reference.FrameworkCode.Should().Be(framework);
        reference.Version.Should().Be(version);
        reference.RequirementCode.Should().Be(requirement);
    }

    [Fact]
    public async Task Reference_seed_is_versioned_idempotent_and_keeps_user_changes()
    {
        var status = await ReadyAsync();
        var catalog = EmbeddedCatalogService.Load();
        var seeds = new SqlReferenceSeedStore(new FixedStatus(_settings, status), _connections, TimeProvider.System);
        (await seeds.IsCurrentAsync(catalog)).Should().BeFalse();

        var first = await seeds.SeedAsync(catalog, force: false, null);
        first.Datasets.Should().OnlyContain(dataset => !dataset.Skipped);
        (await seeds.IsCurrentAsync(catalog)).Should().BeTrue();
        var counts = await SeedCountsAsync();
        counts["secureplus.ControlDetail"].Should().Be(catalog.Controls.Count);
        counts["secureplus.ControlAlias"].Should().Be(catalog.Aliases.Count);
        counts["secureplus.ReferenceValue"].Should().Be(catalog.SolutionAreas.Count + catalog.ExperienceAreas.Count);
        counts["secureplus.Specification"].Should().Be(catalog.Specifications.Count);
        counts["secureplus.Baseline"].Should().Be(catalog.Baselines.Count);
        counts["ref.CodeSet"].Should().Be(EngineeringWorkspace.Application.Reference.EaasReferenceData.CodeSetList.Count);
        counts["ref.CodeValue"].Should().Be(EngineeringWorkspace.Application.Reference.EaasReferenceData.CodeSetList.Sum(set => set.Values.Count));
        counts["ref.ImplementationStatus"].Should().Be(8);
        counts["svc.ServiceTier"].Should().Be(3);
        counts["eux.ExperienceArea"].Should().Be(catalog.ExperienceAreas.Count);
        (await ScalarAsync("""SELECT COUNT(*) FROM "ref.CodeValue" v JOIN "ref.CodeSet" s ON s.CodeSetId = v.CodeSetId WHERE s.Code = 'Industry';""")).Should().Be(0, "NAICS is no longer seeded from 4.0");

        // A second run changes nothing.
        var second = await seeds.SeedAsync(catalog, force: false, null);
        second.Datasets.Should().OnlyContain(dataset => dataset.Skipped);
        (await SeedCountsAsync()).Should().Equal(counts);

        // A user's change to a system baseline, a customer's tailored specification and its assignment survive a forced re-seed.
        await ExecuteAsync("""UPDATE "secureplus.Baseline" SET Name = 'Customised', IsModified = 1, IsActive = 1 WHERE BaselineKey = 'BL-MS-SECURITY-BASELINES';""");
        await ExecuteAsync("""
                           INSERT INTO "secureplus.Specification" (SpecificationKey, Name, IsStandard, IsSystem, IsModified, StatusCode, CreatedBy, CreatedAtUtc)
                           VALUES ('SPEC-CUSTOM', 'Contoso tailored', 0, 0, 0, 'Draft', 'tester', '2026-11-07T00:00:00Z');
                           INSERT INTO "secureplus.SpecificationAssignment" (CustomerKey, SpecificationKey, AssignedBy, AssignedAtUtc) VALUES ('contoso', 'SPEC-CUSTOM', 'tester', '2026-11-07T00:00:00Z');
                           """);
        var forced = await seeds.SeedAsync(catalog, force: true, null);
        forced.Datasets.Single(dataset => dataset.Dataset == SeedDatasets.Baselines).Protected.Should().Be(1);
        (await ScalarTextAsync("""SELECT Name FROM "secureplus.Baseline" WHERE BaselineKey = 'BL-MS-SECURITY-BASELINES';""")).Should().Be("Customised");
        (await ScalarAsync("""SELECT COUNT(*) FROM "secureplus.Specification" WHERE SpecificationKey = 'SPEC-CUSTOM';""")).Should().Be(1);
        (await ScalarAsync("""SELECT COUNT(*) FROM "secureplus.SpecificationAssignment";""")).Should().Be(1);
        (await ScalarAsync("""SELECT COUNT(*) FROM "secureplus.ControlDetail";""")).Should().Be(catalog.Controls.Count);
    }

    [Fact]
    public async Task Knowledge_base_loads_SCF_NIST_and_MITRE_links_the_catalog_and_reloads_without_duplicates()
    {
        var status = await ReadyAsync();
        var fixedStatus = new FixedStatus(_settings, status);
        await new SqlCatalogStore(fixedStatus, _connections, TimeProvider.System).SyncAsync(EmbeddedCatalogService.Load(), force: false, null);
        var store = new EngineeringWorkspace.Infrastructure.KnowledgeBase.SqlKnowledgeBaseStore(fixedStatus, _connections, TimeProvider.System);
        var kb = EngineeringWorkspace.Infrastructure.KnowledgeBase.EmbeddedKnowledgeBase.Current;
        (await store.GetStateAsync()).IsCurrent.Should().BeFalse();

        var first = await store.LoadAsync(force: false, null);
        first.Skipped.Should().BeFalse();
        (await store.GetStateAsync()).IsCurrent.Should().BeTrue("every catalog reference was linked, so the load is complete");

        (await ScalarAsync("""SELECT COUNT(*) FROM "fw.Framework" WHERE ScfFocalDocumentId IS NOT NULL OR Code = 'scf';""")).Should().Be(kb.Count("frameworks"));
        (await ScalarAsync("""SELECT COUNT(*) FROM "fw.ScfControl";""")).Should().Be(1591);
        (await ScalarAsync("""SELECT COUNT(*) FROM "fw.RequirementMap" m JOIN "ref.MappingSource" s ON s.MappingSourceId = m.MappingSourceId WHERE s.Code = 'SCF-STRM';"""))
            .Should().Be(kb.Count("maps"));
        (await ScalarAsync("""SELECT COUNT(*) FROM "fw.RequirementMap" m JOIN "ref.MappingSource" s ON s.MappingSourceId = m.MappingSourceId WHERE s.Code = 'GDS-ALIAS';"""))
            .Should().Be(kb.Count("aliases"));
        (await ScalarAsync("""SELECT COUNT(*) FROM "fw.AssessmentObjective";""")).Should().Be(kb.Count("objectives"));
        (await ScalarAsync("""SELECT COUNT(*) FROM "threat.ControlRisk" WHERE LikelihoodScaleValueId IS NOT NULL;""")).Should().Be(kb.Count("controlRisks"));
        (await ScalarAsync("""SELECT COUNT(*) FROM "threat.ControlThreat";""")).Should().Be(kb.Count("controlThreats"));
        (await ScalarAsync("""SELECT COUNT(*) FROM "threat.AttackTechnique" WHERE IsSubTechnique = 1 AND ParentTechniqueId IS NOT NULL;""")).Should().BeGreaterThan(300);
        (await ScalarAsync("""SELECT COUNT(*) FROM "threat.TechniqueMitigation";""")).Should().Be(kb.Count("techniqueMitigations"));
        (await ScalarTextAsync("""SELECT r.Title FROM "fw.Requirement" r JOIN "fw.FrameworkVersion" v ON v.FrameworkVersionId = r.FrameworkVersionId JOIN "fw.Framework" f ON f.FrameworkId = v.FrameworkId WHERE f.ScfColumnHeader = 'NIST 800-53 R5.2' AND r.RequirementCode = 'AC-02';"""))
            .Should().Be("Account Management");
        // A catalog control's NIST reference now reaches its SCF controls through the crosswalk.
        (await ScalarAsync("""
                           SELECT COUNT(DISTINCT scf.RequirementCode) FROM "fw.Requirement" cat
                           JOIN "fw.FrameworkVersion" cv ON cv.FrameworkVersionId = cat.FrameworkVersionId JOIN "fw.Framework" cf ON cf.FrameworkId = cv.FrameworkId
                           JOIN "fw.RequirementMap" alias ON alias.FromRequirementId = cat.RequirementId
                           JOIN "fw.RequirementMap" strm ON strm.ToRequirementId = alias.ToRequirementId
                           JOIN "fw.Requirement" scf ON scf.RequirementId = strm.FromRequirementId
                           WHERE cf.Code = 'NIST-800-53' AND cat.RequirementCode = 'AC-4';
                           """)).Should().BeGreaterThan(0);

        var requirementIds = await ScalarAsync("""SELECT SUM(RequirementId) FROM "fw.Requirement";""");
        var requirementCount = await ScalarAsync("""SELECT COUNT(*) FROM "fw.Requirement";""");
        (await store.LoadAsync(force: false, null)).Skipped.Should().BeTrue();
        var forced = await store.LoadAsync(force: true, null);
        forced.Skipped.Should().BeFalse();
        (await ScalarAsync("""SELECT COUNT(*) FROM "fw.Requirement";""")).Should().Be(requirementCount, "a reload updates in place");
        (await ScalarAsync("""SELECT SUM(RequirementId) FROM "fw.Requirement";""")).Should().Be(requirementIds, "requirement IDs are stable");
        (await ScalarAsync("""SELECT COUNT(*) FROM "fw.RequirementMap";""")).Should().Be(kb.Count("maps") + kb.Count("aliases") + await ScalarAsync("""SELECT COUNT(*) FROM "fw.RequirementMap" m JOIN "ref.MappingSource" s ON s.MappingSourceId = m.MappingSourceId WHERE s.Code NOT IN ('SCF-STRM', 'GDS-ALIAS');"""));
    }

    [Fact]
    public async Task Baselines_WBS_and_question_bank_load_after_the_knowledge_base_and_reload_in_place()
    {
        var status = await ReadyAsync();
        var fixedStatus = new FixedStatus(_settings, status);
        await new SqlCatalogStore(fixedStatus, _connections, TimeProvider.System).SyncAsync(EmbeddedCatalogService.Load(), force: false, null);
        var phase2 = new EngineeringWorkspace.Infrastructure.KnowledgeBase.SqlBaselineKnowledgeStore(fixedStatus, _connections, TimeProvider.System);
        var store = new EngineeringWorkspace.Application.KnowledgeBase.CompositeKnowledgeBaseStore(
            [new EngineeringWorkspace.Infrastructure.KnowledgeBase.SqlKnowledgeBaseStore(fixedStatus, _connections, TimeProvider.System), phase2]);
        var kb = EngineeringWorkspace.Infrastructure.KnowledgeBase.EmbeddedKnowledgeBase.Baselines;
        (await store.GetStateAsync()).IsCurrent.Should().BeFalse();

        (await store.LoadAsync(force: false, null)).Skipped.Should().BeFalse();
        (await store.GetStateAsync()).IsCurrent.Should().BeTrue();
        (await ScalarAsync("""SELECT COUNT(*) FROM "cfg.Baseline";""")).Should().Be(kb.Count("baselines"));
        (await ScalarAsync("""SELECT COUNT(*) FROM "cfg.Recommendation";""")).Should().Be(kb.Count("recommendations"));
        (await ScalarAsync("""SELECT COUNT(*) FROM "cfg.RecommendationSourceText" WHERE IsInternalOnly = 1;""")).Should().Be(kb.Count("sourceTexts"));
        (await ScalarAsync("""SELECT COUNT(*) FROM "cfg.Setting";""")).Should().Be(kb.Count("settings"));
        (await ScalarAsync("""SELECT COUNT(*) FROM "cfg.RecommendationRequirement";""")).Should().Be(kb.Count("recommendationRequirements"));
        (await ScalarAsync("""SELECT COUNT(*) FROM "dlv.WbsTask";""")).Should().Be(kb.Count("wbsTasks"));
        (await ScalarAsync("""SELECT COUNT(*) FROM "eux.Question";""")).Should().Be(kb.Count("questions"));
        (await ScalarAsync("""SELECT COUNT(*) FROM "deploy.ServiceMethodCoverage";""")).Should().Be(kb.Count("methodCoverage"));
        // The registry value behind 'Configure Windows Defender SmartScreen' is one setting shared by the CIS benchmarks and the Microsoft baseline.
        (await ScalarAsync("""
                           SELECT COUNT(DISTINCT b.BaselineSourceId) FROM "cfg.Setting" s JOIN "cfg.RecommendationSetting" rs ON rs.SettingId = s.SettingId
                           JOIN "cfg.Recommendation" r ON r.RecommendationId = rs.RecommendationId JOIN "cfg.Baseline" b ON b.BaselineId = r.BaselineId
                           WHERE s.RegistryValueName = 'EnableSmartScreen';
                           """)).Should().Be(2);
        // CIS recommendations reach CIS Controls v8.1 safeguards loaded by phase 1.
        (await ScalarAsync("""
                           SELECT COUNT(*) FROM "cfg.RecommendationRequirement" rr JOIN "fw.Requirement" q ON q.RequirementId = rr.RequirementId
                           JOIN "fw.FrameworkVersion" v ON v.FrameworkVersionId = q.FrameworkVersionId JOIN "fw.Framework" f ON f.FrameworkId = v.FrameworkId
                           WHERE f.Code = 'general-cis-csc-8-1';
                           """)).Should().BeGreaterThan(7000);
        // WBS tasks and discovery questions point at the catalog's Secure Plus standard items.
        (await ScalarAsync("""SELECT COUNT(DISTINCT WbsTaskId) FROM "dlv.WbsTaskCoverage";""")).Should().Be(kb.Count("wbsTasks"));
        (await ScalarAsync("""SELECT COUNT(DISTINCT QuestionId) FROM "eux.QuestionLink";""")).Should().BeGreaterThan(400);

        var ids = await ScalarAsync("""SELECT SUM(RecommendationId) FROM "cfg.Recommendation";""");
        (await store.LoadAsync(force: false, null)).Skipped.Should().BeTrue();
        (await phase2.LoadAsync(force: true, null)).Skipped.Should().BeFalse();
        (await ScalarAsync("""SELECT COUNT(*) FROM "cfg.Recommendation";""")).Should().Be(kb.Count("recommendations"), "a reload updates in place");
        (await ScalarAsync("""SELECT SUM(RecommendationId) FROM "cfg.Recommendation";""")).Should().Be(ids, "recommendation IDs are stable");
        (await ScalarAsync("""SELECT COUNT(*) FROM "cfg.SettingAlias";""")).Should().Be(kb.Count("settingAliases"));
    }

    [Fact]
    public async Task Phase3_loads_and_assessment_results_reach_the_EaaS_tables()
    {
        var status = await ReadyAsync();
        var fixedStatus = new FixedStatus(_settings, status);
        await new SqlCatalogStore(fixedStatus, _connections, TimeProvider.System).SyncAsync(EmbeddedCatalogService.Load(), force: false, null);
        await new SqlReferenceSeedStore(fixedStatus, _connections, TimeProvider.System).SeedAsync(EmbeddedCatalogService.Load(), false, null);
        var phase3 = new EngineeringWorkspace.Infrastructure.KnowledgeBase.SqlPhase3KnowledgeStore(fixedStatus, _connections, TimeProvider.System);
        var store = new EngineeringWorkspace.Application.KnowledgeBase.CompositeKnowledgeBaseStore(
        [
            new EngineeringWorkspace.Infrastructure.KnowledgeBase.SqlKnowledgeBaseStore(fixedStatus, _connections, TimeProvider.System),
            new EngineeringWorkspace.Infrastructure.KnowledgeBase.SqlBaselineKnowledgeStore(fixedStatus, _connections, TimeProvider.System), phase3
        ]);
        var kb = EngineeringWorkspace.Infrastructure.KnowledgeBase.EmbeddedKnowledgeBase.Phase3;
        (await store.LoadAsync(force: false, null)).Skipped.Should().BeFalse();
        (await store.GetStateAsync()).IsCurrent.Should().BeTrue();
        (await ScalarAsync("""SELECT COUNT(*) FROM "kb.ConfigurationChange";""")).Should().Be(kb.Count("configurationChanges"));
        (await ScalarAsync("""SELECT COUNT(*) FROM "kb.SkuCapability";""")).Should().Be(kb.Count("skuCapabilities"));
        (await ScalarAsync("""SELECT COUNT(*) FROM "threat.CapabilityTechnique";""")).Should().Be(kb.Count("capabilityTechniques"));
        (await ScalarAsync("""SELECT COUNT(*) FROM "svc.TierFeature";""")).Should().Be(kb.Count("tierFeatures"));
        (await ScalarAsync("""SELECT COUNT(*) FROM "score.ItemScore";""")).Should().Be(kb.Count("itemScores"));
        (await ScalarAsync("""SELECT COUNT(*) FROM "cfg.RecommendationRelation";""")).Should().Be(kb.Count("relations"));
        (await ScalarAsync("""SELECT COUNT(*) FROM "src.LoadBatch";""")).Should().Be(1);
        (await ScalarAsync("""SELECT COUNT(*) FROM "svc.ServiceTier" WHERE BaseLicenseSkuId IS NOT NULL;""")).Should().Be(2);
        // Secure Plus (Business Premium + E5 Security) includes Conditional Access (Entra ID P1).
        (await ScalarAsync("""
                           SELECT COUNT(*) FROM "svc.TierCapability" tc JOIN "svc.ServiceTier" t ON t.ServiceTierId = tc.ServiceTierId
                           JOIN "kb.Capability" c ON c.CapabilityId = tc.CapabilityId WHERE t.Code = 'SecurePlus' AND c.Code = 'SP-AAD_PREMIUM';
                           """)).Should().Be(1);
        var changes = await ScalarAsync("""SELECT SUM(ConfigurationChangeId) FROM "kb.ConfigurationChange";""");
        (await phase3.LoadAsync(force: true, null)).Skipped.Should().BeFalse();
        (await ScalarAsync("""SELECT SUM(ConfigurationChangeId) FROM "kb.ConfigurationChange";""")).Should().Be(changes, "configuration change IDs are stable");
        (await ScalarAsync("""SELECT COUNT(*) FROM "kb.Guidance";""")).Should().BeGreaterThan(3000);
        string.Join("; ", await OverlongTextAsync()).Should().BeEmpty("SQL Server rejects text longer than the column (SQLite does not check)");

        // Phase 4: a completed assessment is published to the EaaS engagement tables.
        var repository = new SqlEngagementRepository(fixedStatus, _connections, new AuditContext("tester"), TimeProvider.System);
        var customer = EngineeringWorkspace.Domain.Customers.CustomerProfile.Create(new CustomerId("contoso"), "Contoso");
        await repository.UpsertCustomerAsync(customer, isSample: false);
        var run = await repository.StartAssessmentAsync(customer.Id, null, "Engine run", null);
        await repository.CompleteAssessmentAsync(run, EngineeringWorkspace.Application.Assessments.AssessmentStatusCodes.Completed, 50m,
        [
            new("CA-100", "Identity", "Admin strong authentication", "Pass", "High", "Policy on", null),
            new("CA-101", "Identity", "Admin trusted device", "Fail", "High", "Policy missing", "Deploy CA-101"),
            new("CHK-mfa_is_enforced_for_all_users", "Identity", "MFA for all users", "Missing", "Critical", null, null),
            new("NOT-IN-CATALOG", "Other", "Unknown control", "Info", "Low", null, null)
        ]);
        repository.LastEaasPublishError.Should().BeNull();
        (await ScalarAsync("""SELECT COUNT(*) FROM "cust.Finding" WHERE StatusCode = 'Open';""")).Should().Be(2);
        (await ScalarAsync("""SELECT COUNT(*) FROM "cust.Finding" WHERE ConfigurationChangeId IS NOT NULL;""")).Should().Be(1);
        (await ScalarAsync("""SELECT COUNT(*) FROM "cust.RequirementResult" WHERE IsDerived = 1;""")).Should().BeGreaterThan(0);
        (await ScalarAsync("""SELECT COUNT(*) FROM "cust.ScoreSnapshot";""")).Should().Be(1);
        (await ScalarAsync("""SELECT COUNT(*) FROM "audit.Snapshot";""")).Should().Be(1);
        (await ScalarAsync("""SELECT COUNT(*) FROM "audit.SnapshotItem";""")).Should().Be(4);
        // A second run replaces the open findings instead of adding to them.
        var second = await repository.StartAssessmentAsync(customer.Id, null, "Engine run 2", null);
        await repository.CompleteAssessmentAsync(second, EngineeringWorkspace.Application.Assessments.AssessmentStatusCodes.Completed, 100m,
            [new("CA-101", "Identity", "Admin trusted device", "Pass", "High", "Policy on", null)]);
        (await ScalarAsync("""SELECT COUNT(*) FROM "cust.Finding" WHERE StatusCode = 'Open';""")).Should().Be(0);
        (await ScalarAsync("""SELECT COUNT(*) FROM "cust.ScoreSnapshot";""")).Should().Be(2);

        // 3.0 planning data: WBS, threat coverage, setting remediation, SOW templates and project plans.
        var planning = new SqlPlanningRepository(fixedStatus, _connections);
        planning.IsAvailable.Should().BeTrue();
        var wbs = await planning.GetWorkBreakdownAsync();
        wbs.Tasks.Should().HaveCountGreaterThan(100);
        wbs.Phases.Should().NotBeEmpty();
        wbs.Tasks.Should().Contain(task => task.Predecessors.Count > 0);
        var coverage = await planning.GetThreatCoverageAsync();
        coverage.Should().Contain(cell => cell.Column == "All Microsoft" && cell.Covered > 0);
        coverage.Should().Contain(cell => cell.Column != "All Microsoft");
        coverage.Where(cell => cell.Column == "All Microsoft").Sum(cell => cell.Covered).Should().BeGreaterThanOrEqualTo(
            coverage.Where(cell => cell.Column != "All Microsoft").GroupBy(cell => cell.Column).Max(group => group.Sum(cell => cell.Covered)));
        var alias = (await QueryTextAsync("""SELECT AliasValue FROM "cfg.SettingAlias" WHERE AliasTypeCode = 'REGISTRY' LIMIT 1;"""))!;
        (await planning.FindSettingsAsync([alias.ToUpperInvariant()])).Should().NotBeEmpty("aliases match case-insensitively");

        var template = Application.Planning.SowTemplates.BuiltIn[0] with { Code = "TEST-SOW" };
        await planning.SaveSowTemplateAsync(template);
        await planning.SaveSowTemplateAsync(template with { Name = "Renamed", Sections = template.Sections.Take(2).ToArray() });
        var saved = (await planning.GetSowTemplatesAsync()).Single(item => item.Code == "TEST-SOW");
        saved.Name.Should().Be("Renamed");
        saved.Sections.Should().HaveCount(2);
        saved.ServiceTierCode.Should().Be("SecurePlus");
        await planning.DeleteSowTemplateAsync("TEST-SOW");
        (await planning.GetSowTemplatesAsync()).Should().NotContain(item => item.Code == "TEST-SOW");

        var start = new DateOnly(2026, 11, 9);
        var plan = new Application.Planning.ProjectPlan(null, "Deployment", start, start.AddDays(3), "Draft",
        [
            new("t1", "1", "Kick-off", "Initiate", "Project Manager", null, start, start, 0.5, 4, 0, true, "NotStarted", "High", wbs.Tasks[0].Code, []),
            new("t2", "2", "Conditional Access", "Build", "Engineer", "Alex", start, start.AddDays(2), 2, 16, 25, false, "InProgress", null, null, ["t1"]),
        ]);
        var stored = await planning.SaveProjectPlanAsync("contoso", plan);
        stored.Id.Should().NotBeNull();
        var reloaded = (await planning.GetProjectPlansAsync("contoso")).Single();
        reloaded.Tasks.Should().HaveCount(2);
        reloaded.Tasks[1].Predecessors.Should().Equal(reloaded.Tasks[0].Key);
        reloaded.Tasks[1].Role.Should().Be("Engineer");
        reloaded.Tasks[0].WbsTaskCode.Should().Be(wbs.Tasks[0].Code);
        await planning.SaveProjectPlanAsync("contoso", stored with { Name = "Deployment v2", Tasks = stored.Tasks.Take(1).ToArray() });
        (await planning.GetProjectPlansAsync("contoso")).Single().Tasks.Should().HaveCount(1);
        (await ScalarAsync("""SELECT COUNT(*) FROM "dlv.Role";""")).Should().BeGreaterThanOrEqualTo(2);
        await planning.DeleteProjectPlanAsync("contoso", stored.Id!.Value);
        (await planning.GetProjectPlansAsync("contoso")).Should().BeEmpty();
        string.Join("; ", await OverlongTextAsync()).Should().BeEmpty();
    }

    private async Task<string?> QueryTextAsync(string sql)
    {
        await using var connection = _connections.CreateSqlite(_settings, allowCreate: false);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<Dictionary<string, long>> SeedCountsAsync()
    {
        var counts = new Dictionary<string, long>();
        foreach (var table in new[] { "secureplus.ControlDetail", "secureplus.ControlAlias", "secureplus.ReferenceValue", "secureplus.Specification", "secureplus.SpecificationItem",
                     "secureplus.Baseline", "secureplus.BaselineItem", "secureplus.SeedVersion", "ref.CodeSet", "ref.CodeValue", "ref.ImplementationStatus", "svc.ServiceTier",
                     "eux.ExperienceArea" })
            counts[table] = await ScalarAsync($"SELECT COUNT(*) FROM \"{table}\";");
        return counts;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = _connections.CreateSqlite(_settings, allowCreate: false);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string?> ScalarTextAsync(string sql)
    {
        await using var connection = _connections.CreateSqlite(_settings, allowCreate: false);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string?)await command.ExecuteScalarAsync();
    }

    private async Task<Dictionary<string, long>> CountsAsync()
    {
        string[] tables = ["secureplus.CatalogControl", "pol.Artifact", "pol.ArtifactSetting", "pol.PolicySet", "pol.PolicySetMember", "pol.PolicySetInclude",
            "pol.PolicySetPrerequisite", "pol.ArtifactMapping", "pol.PolicyType", "pol.Category", "fw.Framework", "fw.FrameworkVersion", "fw.Requirement", "gds.Standard",
            "gds.StandardItem", "pol.RepositoryVersion", "ref.MappingSource"];
        var counts = new Dictionary<string, long>();
        foreach (var table in tables) counts[table] = await ScalarAsync($"SELECT COUNT(*) FROM \"{table}\";");
        return counts;
    }

    /// <summary>Columns holding text longer than their SQL Server length (from the SQL Server migration scripts).</summary>
    private async Task<List<string>> OverlongTextAsync()
    {
        var limits = new Dictionary<string, List<(string Column, int Max)>>(StringComparer.Ordinal);
        void Add(string table, string column, int max)
        {
            if (!limits.TryGetValue(table, out var list)) limits[table] = list = [];
            list.Add((column, max));
        }

        var assembly = typeof(EngineeringWorkspace.Infrastructure.KnowledgeBase.SqlPhase3KnowledgeStore).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith("EngineeringWorkspace.Schema.SqlServer.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            string? table = null;
            foreach (var line in (await reader.ReadToEndAsync()).Split('\n'))
            {
                var create = System.Text.RegularExpressions.Regex.Match(line, @"CREATE TABLE \[(\w+)\]\.\[(\w+)\]");
                if (create.Success) table = $"{create.Groups[1].Value}.{create.Groups[2].Value}";
                var drop = System.Text.RegularExpressions.Regex.Match(line, @"ALTER TABLE \[(\w+)\]\.\[(\w+)\]\s+DROP COLUMN\s+\[(\w+)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (drop.Success && limits.TryGetValue($"{drop.Groups[1].Value}.{drop.Groups[2].Value}", out var dropped)) dropped.RemoveAll(item => item.Column == drop.Groups[3].Value);
                var alter = System.Text.RegularExpressions.Regex.Match(line, @"ALTER TABLE \[(\w+)\]\.\[(\w+)\]\s+ADD\s+\[(\w+)\]\s+N?(?:VAR)?CHAR\((\d+)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (alter.Success) Add($"{alter.Groups[1].Value}.{alter.Groups[2].Value}", alter.Groups[3].Value, int.Parse(alter.Groups[4].Value));
                else if (table is not null && System.Text.RegularExpressions.Regex.Match(line, @"^\s+\[(\w+)\]\s+N?(?:VAR)?CHAR\((\d+)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } column)
                    Add(table, column.Groups[1].Value, int.Parse(column.Groups[2].Value));
            }
        }

        var problems = new List<string>();
        await using var connection = _connections.CreateSqlite(_settings, allowCreate: false);
        await connection.OpenAsync();
        foreach (var (table, columns) in limits)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {string.Join(", ", columns.Select(column => $"MAX(LENGTH(\"{column.Column}\"))"))} FROM \"{table}\";";
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) continue;
            for (var index = 0; index < columns.Count; index++)
                if (!reader.IsDBNull(index) && reader.GetInt64(index) > columns[index].Max)
                    problems.Add($"{table}.{columns[index].Column}: {reader.GetInt64(index)} > {columns[index].Max}");
        }

        return problems;
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = _connections.CreateSqlite(_settings, allowCreate: false);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed class SyncProgress(Action<(double Progress, string Activity)> handler) : IProgress<(double Progress, string Activity)>
    {
        public void Report((double Progress, string Activity) value) => handler(value);
    }

    private async Task<DatabaseStatusReport> ReadyAsync()
    {
        await _provisioning.CreateDatabaseAsync(_settings, null, CancellationToken.None);
        var initialized = await _provisioning.InitializeSchemaAsync(_settings, null, CancellationToken.None);
        initialized.State.Should().Be(DatabaseOverallState.Ready, initialized.SanitizedError);
        return initialized;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _root.Delete(recursive: true);
    }

    private sealed class FixedStatus(DatabaseSettings settings, DatabaseStatusReport? report) : IDatabaseStatusService
    {
        public DatabaseSettings Settings => settings;
        public DatabaseStatusReport? LatestStatus => report;
        public event EventHandler<DatabaseStatusReport>? StatusChanged { add { } remove { } }
        public void Publish(DatabaseStatusReport value) { }
        public Task<DatabaseStatusReport> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(report!);
        public Task<OperationResult<string>> SaveSettingsAsync(DatabaseSettings value, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<string>.Success("unused"));
    }
}
