namespace EngineeringWorkspace.Application.Database;

/// <summary>Who is expected to fill a table.</summary>
public enum DataOwner
{
    /// <summary>Secure Plus writes it while it is used (customers, assessments, changes...).</summary>
    SecurePlus,

    /// <summary>Secure Plus seeds it from the reference data shipped with the installer.</summary>
    SecurePlusSeed,

    /// <summary>Knowledge-base content of a later phase (known issues, scoring model, licence capabilities, CTID mappings, SOW).</summary>
    EaasKnowledgeBase,

    /// <summary>Another EaaS component (delivery estimating, SOW, commercial packaging, questionnaires, drift, PSA).</summary>
    EaasOtherComponent
}

/// <summary>Coverage of one table: owner, why, and (once counted) its rows.</summary>
public sealed record TableCoverage(string Schema, string Table, DataOwner Owner, string Purpose, long? Rows = null)
{
    public string Name => $"{Schema}.{Table}";
    public bool ShouldHaveRows => Owner is DataOwner.SecurePlus or DataOwner.SecurePlusSeed;

    /// <summary>"Filled", "Empty – Expected" or "Empty – Needs Attention" (or "Not Counted").</summary>
    public string State => Rows switch
    {
        null => "Not Counted",
        > 0 => "Filled",
        _ when Owner == DataOwner.SecurePlusSeed => "Empty – Not Loaded Yet",
        _ when Owner == DataOwner.SecurePlus => "Empty – Fills When Used",
        _ when Owner == DataOwner.EaasKnowledgeBase => "Empty – Later Knowledge-Base Phase",
        _ => "Empty – Other EaaS Component"
    };

    public string OwnerName => Owner switch
    {
        DataOwner.SecurePlus => "Secure Plus (when used)",
        DataOwner.SecurePlusSeed => "Secure Plus (seed and knowledge base)",
        DataOwner.EaasKnowledgeBase => "Knowledge base (later phase)",
        _ => "Other EaaS component"
    };
}

/// <summary>
/// The data audit of the SecurePlus database: for every EaaS v3 and Secure Plus table, who fills it and when. The
/// Database Health page adds live row counts; docs/DATA_COVERAGE.md carries the same table for review.
/// </summary>
public static class DataCoverage
{
    private static readonly Dictionary<string, (DataOwner Owner, string Purpose)> Tables = new(StringComparer.OrdinalIgnoreCase)
    {
        // Customer data written by Secure Plus.
        ["cust.Customer"] = (DataOwner.SecurePlus, "Customer profile: name, industry, size, address, phone, e-mail, CRM ID."),
        ["cust.Contact"] = (DataOwner.SecurePlus, "Customer contacts, change approvers and decision owners."),
        ["cust.Tenant"] = (DataOwner.SecurePlus, "Microsoft 365 tenants of each customer."),
        ["cust.TenantLicense"] = (DataOwner.SecurePlus, "Licences per tenant, collected by each assessment (subscribedSkus)."),
        ["cust.Engagement"] = (DataOwner.SecurePlus, "One Secure Plus engagement per customer: sales lead, technical lead, project manager, tier."),
        ["cust.AssessmentRun"] = (DataOwner.SecurePlus, "Every assessment run."),
        ["cust.Exception"] = (DataOwner.SecurePlus, "Approved exceptions (risk acceptances)."),
        ["audit.TenantConnection"] = (DataOwner.SecurePlus, "App registration, consent and last verification per tenant."),
        ["deploy.ChangeRequest"] = (DataOwner.SecurePlus, "Change requests (EaaS view of secureplus.ChangeRequest)."),
        ["sec.AppUser"] = (DataOwner.SecurePlus, "Technicians who use the application."),
        // Seeded reference data.
        ["ref.CodeSet"] = (DataOwner.SecurePlusSeed, "Code lists (industry, organization size, contact role...)."),
        ["ref.CodeValue"] = (DataOwner.SecurePlusSeed, "Values of the code lists (organisation size, contact roles and others; NAICS no longer seeded from 4.0)."),
        ["ref.ImplementationStatus"] = (DataOwner.SecurePlusSeed, "Implementation statuses with completion credit."),
        ["ref.MaturityLevel"] = (DataOwner.SecurePlusSeed, "SCF maturity levels L0–L5."),
        ["ref.RelationshipType"] = (DataOwner.SecurePlusSeed, "Mapping relationship types."),
        ["ref.EntityType"] = (DataOwner.SecurePlusSeed, "Catalog object types."),
        ["ref.MappingSource"] = (DataOwner.SecurePlusSeed, "Sources of control-to-framework mappings."),
        ["svc.ServiceTier"] = (DataOwner.SecurePlusSeed, "Essentials, Secure Plus, Secure Plus Premium."),
        ["sec.AppRole"] = (DataOwner.SecurePlusSeed, "Application roles."),
        ["eux.ExperienceArea"] = (DataOwner.SecurePlusSeed, "Experience areas of the catalog."),
        ["fw.Framework"] = (DataOwner.SecurePlusSeed, "Knowledge base: SCF and its 270 focal documents, plus the catalog's frameworks."),
        ["fw.FrameworkVersion"] = (DataOwner.SecurePlusSeed, "Knowledge base: Version of each framework."),
        ["fw.Requirement"] = (DataOwner.SecurePlusSeed, "Knowledge base: SCF controls, focal-document requirements (NIST 800-53 with full text) and catalog references."),
        ["fw.RequirementMap"] = (DataOwner.SecurePlusSeed, "Knowledge base: SCF crosswalk (76,019 mappings) and catalog-to-SCF links."),
        ["fw.ScfDomain"] = (DataOwner.SecurePlusSeed, "Knowledge base: SCF domains and principles."),
        ["fw.ScfControl"] = (DataOwner.SecurePlusSeed, "Knowledge base: SCF control attributes: weight, PPTDF, CSF function, solutions by business size."),
        ["fw.ScfControlLineage"] = (DataOwner.SecurePlusSeed, "Knowledge base: SCF renumbering and change history."),
        ["fw.CompensatingControl"] = (DataOwner.SecurePlusSeed, "Knowledge base: SCF suggested compensating controls."),
        ["fw.MaturityCriterion"] = (DataOwner.SecurePlusSeed, "Knowledge base: SCF SP-CMM criteria per control and level."),
        ["fw.AssessmentObjective"] = (DataOwner.SecurePlusSeed, "Knowledge base: SCF and NIST SP 800-53A assessment objectives."),
        ["fw.EvidenceArtifact"] = (DataOwner.SecurePlusSeed, "Knowledge base: SCF evidence request list."),
        ["fw.RequirementEvidence"] = (DataOwner.SecurePlusSeed, "Knowledge base: Evidence that proves each SCF control."),
        ["fw.Profile"] = (DataOwner.SecurePlusSeed, "Knowledge base: SCF CORE sets."),
        ["fw.ProfileRequirement"] = (DataOwner.SecurePlusSeed, "Knowledge base: Controls in each SCF CORE set."),
        ["threat.Risk"] = (DataOwner.SecurePlusSeed, "Knowledge base: SCF risk catalog."),
        ["threat.Threat"] = (DataOwner.SecurePlusSeed, "Knowledge base: SCF threat catalog."),
        ["threat.ControlRisk"] = (DataOwner.SecurePlusSeed, "Knowledge base: Likelihood of each risk when an SCF control fails."),
        ["threat.ControlThreat"] = (DataOwner.SecurePlusSeed, "Knowledge base: Likelihood of each threat when an SCF control fails."),
        ["threat.AttackTactic"] = (DataOwner.SecurePlusSeed, "Knowledge base: MITRE ATT&CK tactics."),
        ["threat.AttackTechnique"] = (DataOwner.SecurePlusSeed, "Knowledge base: MITRE ATT&CK techniques and sub-techniques."),
        ["threat.TechniqueTactic"] = (DataOwner.SecurePlusSeed, "Knowledge base: Tactics of each technique."),
        ["threat.AttackMitigation"] = (DataOwner.SecurePlusSeed, "Knowledge base: MITRE ATT&CK mitigations."),
        ["threat.TechniqueMitigation"] = (DataOwner.SecurePlusSeed, "Knowledge base: How each mitigation applies to each technique."),
        ["threat.RequirementTechnique"] = (DataOwner.SecurePlusSeed, "Knowledge base: SCF controls that counter each technique."),
        ["ref.RatingScale"] = (DataOwner.SecurePlusSeed, "Knowledge base: Rating scales (SCF likelihood)."),
        ["ref.RatingScaleValue"] = (DataOwner.SecurePlusSeed, "Knowledge base: Points on each rating scale."),
        // Knowledge base phase 2 (2.3): baselines, delivery content, question bank, deployment method matrix.
        ["src.License"] = (DataOwner.SecurePlusSeed, "Knowledge base: Terms of use of each publisher (CIS, Microsoft, GDS)."),
        ["src.SourceDocument"] = (DataOwner.SecurePlusSeed, "Knowledge base: Every source file loaded, with version, SHA-256 and licence."),
        ["cfg.Platform"] = (DataOwner.SecurePlusSeed, "Knowledge base: Platforms a baseline applies to (Windows 11, Server 2025, macOS, Azure...)."),
        ["cfg.BaselineSource"] = (DataOwner.SecurePlusSeed, "Knowledge base: Baseline publishers (CIS, Microsoft)."),
        ["cfg.Baseline"] = (DataOwner.SecurePlusSeed, "Knowledge base: 56 CIS Benchmarks and 3 Microsoft security baselines."),
        ["cfg.BaselineProfile"] = (DataOwner.SecurePlusSeed, "Knowledge base: Profiles of each baseline (L1/L2, member server, domain controller, E3/E5...)."),
        ["cfg.BaselineSection"] = (DataOwner.SecurePlusSeed, "Knowledge base: Section tree of each baseline."),
        ["cfg.GpoPackage"] = (DataOwner.SecurePlusSeed, "Knowledge base: GPO backups shipped with the Microsoft baselines."),
        ["cfg.Recommendation"] = (DataOwner.SecurePlusSeed, "Knowledge base: CIS recommendations and Microsoft baseline settings."),
        ["cfg.RecommendationSourceText"] = (DataOwner.SecurePlusSeed, "Knowledge base: CIS recommendation text (internal use only under the CIS terms)."),
        ["cfg.RecommendationProfile"] = (DataOwner.SecurePlusSeed, "Knowledge base: Profiles that include each recommendation and the value per profile."),
        ["cfg.RecommendationRequirement"] = (DataOwner.SecurePlusSeed, "Knowledge base: CIS mappings to CIS Controls v8.1, NIST SP 800-53 and NIST CSF 2.0."),
        ["cfg.Setting"] = (DataOwner.SecurePlusSeed, "Knowledge base: Technical settings (registry, Group Policy, Intune) shared across baselines."),
        ["cfg.SettingAlias"] = (DataOwner.SecurePlusSeed, "Knowledge base: Other identifiers of each setting (GPO path, registry path, Settings Catalog path, GRID)."),
        ["cfg.RecommendationSetting"] = (DataOwner.SecurePlusSeed, "Knowledge base: The setting each recommendation checks and the expected value."),
        ["cfg.StigReference"] = (DataOwner.SecurePlusSeed, "Knowledge base: DISA STIG identifiers of the CIS STIG benchmarks."),
        ["threat.RecommendationTechnique"] = (DataOwner.SecurePlusSeed, "Knowledge base: ATT&CK techniques countered by CIS recommendations."),
        ["dlv.Phase"] = (DataOwner.SecurePlusSeed, "Knowledge base: Secure Plus delivery phases P0–P4."),
        ["dlv.Workstream"] = (DataOwner.SecurePlusSeed, "Knowledge base: Secure Plus workstreams."),
        ["dlv.WbsTemplate"] = (DataOwner.SecurePlusSeed, "Knowledge base: Secure Plus work breakdown structure."),
        ["dlv.WbsTask"] = (DataOwner.SecurePlusSeed, "Knowledge base: WBS tasks with hours, duration, dependencies and deliverables."),
        ["dlv.WbsTaskDependency"] = (DataOwner.SecurePlusSeed, "Knowledge base: Task predecessors (where a dependency names another task's deliverable)."),
        ["dlv.WbsTaskCoverage"] = (DataOwner.SecurePlusSeed, "Knowledge base: GDS standard items each WBS task delivers."),
        ["eux.QuestionSet"] = (DataOwner.SecurePlusSeed, "Knowledge base: Secure Plus discovery decisions and the EaaS forms."),
        ["eux.QuestionModule"] = (DataOwner.SecurePlusSeed, "Knowledge base: Sections of the EaaS discovery and design-workshop forms."),
        ["eux.Question"] = (DataOwner.SecurePlusSeed, "Knowledge base: Discovery and design questions with why asked, impact, owner and default."),
        ["eux.AnswerOption"] = (DataOwner.SecurePlusSeed, "Knowledge base: Answer choices of the form questions."),
        ["eux.QuestionRule"] = (DataOwner.SecurePlusSeed, "Knowledge base: Rules that include or exclude policies from the answers."),
        ["eux.QuestionLink"] = (DataOwner.SecurePlusSeed, "Knowledge base: GDS standard items each discovery decision configures."),
        ["eux.Persona"] = (DataOwner.SecurePlusSeed, "Knowledge base: User personas of the EaaS forms."),
        ["kb.Product"] = (DataOwner.SecurePlusSeed, "Knowledge base: Microsoft and third-party products in the deployment method matrix."),
        ["deploy.DeploymentMethod"] = (DataOwner.SecurePlusSeed, "Knowledge base: Deployment methods (Graph, PowerShell modules, ARM, portal attestation...)."),
        ["deploy.ServiceMethodCoverage"] = (DataOwner.SecurePlusSeed, "Knowledge base: How each Microsoft 365 / Azure setting group is deployed and monitored."),
        // Knowledge base phase 3 (2.4): licensing, threat coverage, tiers, checklist, scoring, guidance.
        ["kb.LicenseSku"] = (DataOwner.SecurePlusSeed, "Knowledge base: Every Microsoft licence SKU (licensing service plan reference); also licences seen in tenants."),
        ["kb.Capability"] = (DataOwner.SecurePlusSeed, "Knowledge base: Licensed service plans, CTID security capabilities and admin-centre solutions."),
        ["kb.SkuCapability"] = (DataOwner.SecurePlusSeed, "Knowledge base: Service plans included in each licence."),
        ["kb.RequirementCapability"] = (DataOwner.SecurePlusSeed, "Knowledge base: Microsoft products suggested for each CMMC requirement (GDS CMMC workbook)."),
        ["threat.CapabilityTechnique"] = (DataOwner.SecurePlusSeed, "Knowledge base: ATT&CK techniques each Microsoft 365 / Azure capability protects, detects or responds to (MITRE CTID)."),
        ["svc.TierFeature"] = (DataOwner.SecurePlusSeed, "Knowledge base: Marketed features of each service tier (Service Tiers deck)."),
        ["svc.TierCapability"] = (DataOwner.SecurePlusSeed, "Knowledge base: Capabilities included in each tier's base licence."),
        ["gds.StandardItemTier"] = (DataOwner.SecurePlusSeed, "Knowledge base: Customer Deployment Checklist items per tier (baseline included, optional items optional)."),
        ["gds.StandardItemRequirement"] = (DataOwner.SecurePlusSeed, "Knowledge base: Framework requirements of each catalog standard item."),
        ["kb.ConfigurationChange"] = (DataOwner.SecurePlusSeed, "Knowledge base: Deployable policies of the catalog (Conditional Access, Intune, endpoint security)."),
        ["kb.ConfigurationChangeRequirement"] = (DataOwner.SecurePlusSeed, "Knowledge base: Framework requirements each policy supports."),
        ["kb.KnownIssue"] = (DataOwner.SecurePlusSeed, "Knowledge base: Side effects recorded in the catalog."),
        ["kb.KnownIssueLink"] = (DataOwner.SecurePlusSeed, "Knowledge base: Policies each side effect applies to."),
        ["kb.Guidance"] = (DataOwner.SecurePlusSeed, "Knowledge base: Benefits, user impact, steps, validation and rollback per policy and standard item (draft)."),
        ["cfg.BaselineChange"] = (DataOwner.SecurePlusSeed, "Knowledge base: Settings added or removed between Microsoft baseline versions."),
        ["cfg.RecommendationRelation"] = (DataOwner.SecurePlusSeed, "Knowledge base: Recommendations of different baselines that configure the same setting (same or different value)."),
        ["cfg.RecommendationProcedure"] = (DataOwner.SecurePlusSeed, "Knowledge base: Audit (PowerShell) and remediation (GPO, Intune) steps per recommendation."),
        ["eux.Expectation"] = (DataOwner.SecurePlusSeed, "Knowledge base: Experience expectations per aspect and profile (convenience, balanced, security)."),
        ["eux.AnswerExpectation"] = (DataOwner.SecurePlusSeed, "Knowledge base: Expectations implied by the experience-profile answer."),
        ["eux.ExpectationImpact"] = (DataOwner.SecurePlusSeed, "Knowledge base: Policies that support or contradict each expectation."),
        ["deploy.Ring"] = (DataOwner.SecurePlusSeed, "Knowledge base: Rollout rings (pilot, UAT, production)."),
        ["ref.PropertyDefinition"] = (DataOwner.SecurePlusSeed, "Knowledge base: Framework attributes (CIS IG1–IG3, asset class, SPRS points, POA&M eligibility...)."),
        ["fw.RequirementProperty"] = (DataOwner.SecurePlusSeed, "Knowledge base: Attribute values per CIS safeguard and CMMC requirement."),
        ["fw.ScopeCategory"] = (DataOwner.SecurePlusSeed, "Knowledge base: Scoping categories (ITAR USML, CUI, FCI, PCI CDE, ePHI)."),
        ["ref.SourceStatusLabel"] = (DataOwner.SecurePlusSeed, "Knowledge base: Status words of the compliance worksheets mapped to implementation statuses."),
        ["score.Attribute"] = (DataOwner.SecurePlusSeed, "Knowledge base: Scored attributes (likelihood, impact, SCF weight, SPRS points, user impact...)."),
        ["score.ScoringModel"] = (DataOwner.SecurePlusSeed, "Knowledge base: GDS default risk model (NIST SP 800-30 likelihood × impact)."),
        ["score.ScoringWeight"] = (DataOwner.SecurePlusSeed, "Knowledge base: Weights of the scoring model."),
        ["score.DerivationRule"] = (DataOwner.SecurePlusSeed, "Knowledge base: Rules that fill attribute gaps."),
        ["score.SourcePrecedence"] = (DataOwner.SecurePlusSeed, "Knowledge base: Which source wins per attribute."),
        ["score.EnrichmentValue"] = (DataOwner.SecurePlusSeed, "Knowledge base: Attribute values with source, method and confidence."),
        ["score.ItemScore"] = (DataOwner.SecurePlusSeed, "Knowledge base: Risk score and priority of each SCF control."),
        ["dlv.ComplexityFactor"] = (DataOwner.SecurePlusSeed, "Knowledge base: WBS complexity drivers (multipliers set by GDS)."),
        ["src.SourceObject"] = (DataOwner.SecurePlusSeed, "Knowledge base: Sheets and sections of each source and the tables they feed."),
        ["src.LoadBatch"] = (DataOwner.SecurePlusSeed, "Knowledge base: One row per knowledge-base load."),
        ["src.LoadIssue"] = (DataOwner.SecurePlusSeed, "Knowledge base: Corrections and data-quality notes of each load."),
        // Assessment results published to the EaaS tables after each run (2.4).
        ["cust.Finding"] = (DataOwner.SecurePlus, "Open findings: failing checks of the latest assessment."),
        ["cust.RequirementResult"] = (DataOwner.SecurePlus, "Requirement status derived from the checks mapped to each requirement."),
        ["cust.ScoreSnapshot"] = (DataOwner.SecurePlus, "Score, risk and open findings after each assessment."),
        ["audit.Snapshot"] = (DataOwner.SecurePlus, "One observation snapshot per assessment (drift baseline)."),
        ["audit.SnapshotItem"] = (DataOwner.SecurePlus, "Observed result of each control in the snapshot."),
        ["sec.UserRole"] = (DataOwner.SecurePlus, "Role of each technician (Engineer by default)."),
        ["dlv.ProjectPlan"] = (DataOwner.SecurePlus, "Deployment > Project Plan: customer project plans."),
        ["dlv.ProjectTask"] = (DataOwner.SecurePlus, "Project plan tasks with stable key, order, hours, dates, role, status (incl. Staged), notes, linked CRs/controls."),
        ["secureplus.ChangeRequestTemplate"] = (DataOwner.SecurePlus, "Change request templates: GDS default, global list, per-customer linked copies."),
        ["secureplus.Industry"] = (DataOwner.SecurePlus, "Lead-editable industry list (replaces NAICS in the UI)."),
        ["secureplus.ProjectTaskEvent"] = (DataOwner.SecurePlus, "Append-only task history: remediation attempts, attested manual steps, status changes."),
        ["secureplus.TenantUser"] = (DataOwner.SecurePlus, "Tenant users (scans): UPN, job title, department, usage location, licences; removed users marked."),
        ["secureplus.TenantGroup"] = (DataOwner.SecurePlus, "Tenant groups (scans): kind, dynamic flag, member count; feeds the Entra group pickers."),
        ["secureplus.TenantGroupMember"] = (DataOwner.SecurePlus, "Members of the groups linked to user groups and deployment variables (in-depth scans)."),
        ["secureplus.TenantDevice"] = (DataOwner.SecurePlus, "Intune managed devices with OS, compliance and owner (in-depth scans)."),
        ["secureplus.CatalogControlEdit"] = (DataOwner.SecurePlus, "Lead edits of policies (new, edited, hidden) applied over the GDS catalogue; a reload never overwrites them."),
        ["secureplus.CatalogPolicySetEdit"] = (DataOwner.SecurePlus, "Lead edits of policy sets, applied on top of the GDS catalogue."),
        ["secureplus.CatalogEditHistory"] = (DataOwner.SecurePlus, "Append-only history of every policy and policy-set edit (before and after)."),
        ["dlv.ProjectTaskDependency"] = (DataOwner.SecurePlus, "Project plan task predecessors (finish-to-start)."),
        ["dlv.Role"] = (DataOwner.SecurePlus, "Delivery roles named in project plans (rates stay empty until the rate card is provided)."),
        ["dlv.SowTemplate"] = (DataOwner.SecurePlus, "Assessment > SOW: statement of work templates saved by GDS."),
        ["dlv.SowSection"] = (DataOwner.SecurePlus, "Sections of SOW templates with {{placeholders}}."),
        ["gds.Standard"] = (DataOwner.SecurePlusSeed, "GDS standards."),
        ["gds.StandardItem"] = (DataOwner.SecurePlusSeed, "GDS standard items."),
    };

    private static readonly Dictionary<string, (DataOwner Owner, string Purpose)> Schemas = new(StringComparer.OrdinalIgnoreCase)
    {
        ["secureplus"] = (DataOwner.SecurePlus, "Secure Plus extension table."),
        ["pol"] = (DataOwner.SecurePlusSeed, "Policy repository and policy sets (catalog seed)."),
        ["cust"] = (DataOwner.EaasOtherComponent, "Engagement detail captured by other EaaS components (questionnaires, requirement results, platforms, personas)."),
        ["ref"] = (DataOwner.EaasKnowledgeBase, "Rating scales and source labels of the EaaS knowledge base."),
        ["src"] = (DataOwner.EaasKnowledgeBase, "Source documents and ETL batches of the EaaS knowledge base."),
        ["fw"] = (DataOwner.EaasKnowledgeBase, "SCF / NIST / CIS framework content (crosswalks, objectives, evidence)."),
        ["threat"] = (DataOwner.EaasKnowledgeBase, "SCF risk and threat catalogs, MITRE ATT&CK."),
        ["cfg"] = (DataOwner.EaasKnowledgeBase, "CIS benchmarks, Microsoft baselines and canonical settings."),
        ["kb"] = (DataOwner.EaasKnowledgeBase, "Microsoft products, capabilities, configuration changes and guidance."),
        ["gds"] = (DataOwner.EaasKnowledgeBase, "GDS standard tiers and requirement mappings."),
        ["score"] = (DataOwner.EaasKnowledgeBase, "Scoring model and enrichment values."),
        ["eux"] = (DataOwner.EaasOtherComponent, "Experience questionnaire and expectations."),
        ["dlv"] = (DataOwner.EaasOtherComponent, "Delivery estimating: phases, WBS, rate cards, SOW templates, estimates, project plans."),
        ["svc"] = (DataOwner.EaasOtherComponent, "Commercial packaging: tier features, components, packages, add-ons."),
        ["sec"] = (DataOwner.EaasOtherComponent, "Row-level security for the EaaS reporting portal."),
        ["spec"] = (DataOwner.EaasOtherComponent, "Contracted engagement specifications (EaaS portal)."),
        ["deploy"] = (DataOwner.EaasOtherComponent, "Deployment plans, rings and runs of the EaaS deployment service."),
        ["audit"] = (DataOwner.EaasOtherComponent, "Drift snapshots and comparison of the EaaS drift service."),
        ["psa"] = (DataOwner.EaasOtherComponent, "ConnectWise PSA synchronization."),
    };

    /// <summary>Owner and purpose of a table.</summary>
    public static TableCoverage Classify(string schema, string table)
    {
        if (Tables.TryGetValue($"{schema}.{table}", out var known)) return new TableCoverage(schema, table, known.Owner, known.Purpose);
        if (Schemas.TryGetValue(schema, out var bySchema)) return new TableCoverage(schema, table, bySchema.Owner, bySchema.Purpose);
        return new TableCoverage(schema, table, DataOwner.EaasOtherComponent, "Not part of the Secure Plus schema.");
    }
}
