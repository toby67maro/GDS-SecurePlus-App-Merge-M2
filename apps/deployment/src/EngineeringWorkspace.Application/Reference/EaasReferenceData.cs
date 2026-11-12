namespace EngineeringWorkspace.Application.Reference;

/// <summary>A code value of an EaaS v3 code set (ref.CodeSet / ref.CodeValue).</summary>
public sealed record CodeValueDefinition(string Code, string Label, string? Description = null);

/// <summary>A named list of codes (ref.CodeSet).</summary>
public sealed record CodeSetDefinition(string Code, string Name, string Description, IReadOnlyList<CodeValueDefinition> Values);

public sealed record ImplementationStatusDefinition(string Code, string Label, decimal CreditFactor, bool CountsAsApplicable);

public sealed record MaturityLevelDefinition(string Code, string Name, string Description);

public sealed record RelationshipTypeDefinition(string Code, string Name, bool CountsAsSatisfies, string Description);

public sealed record ServiceTierDefinition(string Code, string Name, string Tagline, string Description);

public sealed record NamedCode(string Code, string Name, string? Description = null);

/// <summary>Stable code-set codes used by the application.</summary>
public static class CodeSets
{
    public const string Industry = "Industry";
    public const string OrganizationSize = "OrgSizeBand";
    public const string ContactRole = "ContactRole";
    public const string CloudEnvironment = "CloudEnvironment";
    public const string EngagementStatus = "EngagementStatus";
    public const string EngagementType = "EngagementType";
    public const string ExceptionType = "ExceptionType";
    public const string TenantAppRole = "TenantAppRole";
    public const string ConsentStatus = "ConsentStatus";
}

/// <summary>Contact role codes written to cust.Contact.RoleCode.</summary>
public static class ContactRoles
{
    public const string PrimaryContact = "PrimaryContact";
    public const string ChangeApprover = "ChangeApprover";
    public const string TechnicalContact = "TechnicalContact";
}

/// <summary>
/// Reference data the EaaS v3 schema expects but no other component of Secure Plus supplies: code lists for customer
/// data entry (industry, organization size, contact roles...), implementation statuses, maturity levels, relationship
/// types, service tiers and application roles. Seeded idempotently by the reference seed (dataset "eaas-reference").
/// </summary>
public static class EaasReferenceData
{
    /// <summary>Bump when any list below changes so the seed re-applies.</summary>
    public const string Version = "4.0.0";

    // 4.0: the 2022 NAICS sector list is no longer seeded; Secure Plus uses its own Industry list (secureplus.Industry).

    /// <summary>Organization size by number of employees (Microsoft 365 Business plans stop at 300 users).</summary>
    public static IReadOnlyList<CodeValueDefinition> OrganizationSizes { get; } =
    [
        new("1-50", "1–50 employees", "Small business"),
        new("51-300", "51–300 employees", "Small and medium business (Microsoft 365 Business plans)"),
        new("301-1000", "301–1,000 employees", "Mid-market"),
        new("1001-5000", "1,001–5,000 employees", "Enterprise"),
        new("5001-10000", "5,001–10,000 employees", "Large enterprise"),
        new("10001+", "More than 10,000 employees", "Very large enterprise"),
    ];

    public static IReadOnlyList<CodeSetDefinition> CodeSetList { get; } =
    [
        new(CodeSets.OrganizationSize, "Organization Size", "Employee-count bands (cust.Customer.OrgSizeBandCode).", OrganizationSizes),
        new(CodeSets.ContactRole, "Contact Role", "Role of a customer contact (cust.Contact.RoleCode).",
        [
            new(ContactRoles.PrimaryContact, "Primary Contact", "Main point of contact"),
            new(ContactRoles.ChangeApprover, "Change Approver", "Approves changes to the customer's tenants"),
            new(ContactRoles.TechnicalContact, "Technical Contact", "IT administrator or technical owner"),
            new("ExecutiveSponsor", "Executive Sponsor"),
            new("SecurityOfficer", "Security Officer"),
            new("BillingContact", "Billing Contact"),
            new("Other", "Other"),
        ]),
        new(CodeSets.CloudEnvironment, "Cloud Environment", "Microsoft cloud of a tenant (cust.Tenant.CloudEnvironmentCode).",
            [new("Commercial", "Commercial"), new("Gcc", "GCC"), new("GccHigh", "GCC High"), new("DoD", "DoD")]),
        new(CodeSets.EngagementStatus, "Engagement Status", "cust.Engagement.StatusCode.",
            [new("Prospect", "Prospect"), new("Active", "Active"), new("OnHold", "On Hold"), new("Completed", "Completed"), new("Cancelled", "Cancelled")]),
        new(CodeSets.EngagementType, "Engagement Type", "cust.Engagement.EngagementTypeCode.",
            [new("SecurePlus", "Secure Plus Managed Service"), new("Assessment", "Assessment"), new("Project", "Project")]),
        new(CodeSets.ExceptionType, "Exception Type", "cust.Exception.ExceptionTypeCode.",
            [new("RiskAcceptance", "Risk Acceptance"), new("CompensatingControl", "Compensating Control"), new("NotApplicable", "Not Applicable"),
             new("BusinessRequirement", "Business Requirement")]),
        new(CodeSets.TenantAppRole, "Tenant App Role", "audit.TenantConnection.AppRoleCode.",
            [new("Collector", "Collector (read-only assessment)"), new("Deployer", "Deployer (configuration changes)")]),
        new(CodeSets.ConsentStatus, "Consent Status", "audit.TenantConnection.ConsentStatusCode.",
            [new("Granted", "Granted"), new("Partial", "Partially Granted"), new("NotGranted", "Not Granted"), new("Unknown", "Not Verified")]),
    ];

    public static IReadOnlyList<ImplementationStatusDefinition> ImplementationStatuses { get; } =
    [
        new("Implemented", "Implemented", 1.00m, true),
        new("PartiallyImplemented", "Partially Implemented", 0.50m, true),
        new("Planned", "Planned", 0.00m, true),
        new("NotImplemented", "Not Implemented", 0.00m, true),
        new("RiskAccepted", "Risk Accepted (Exception)", 1.00m, true),
        new("CompensatingControl", "Compensating Control", 1.00m, true),
        new("NotApplicable", "Not Applicable", 0.00m, false),
        new("NotAssessed", "Not Assessed", 0.00m, false),
    ];

    /// <summary>SCF Security &amp; Privacy Capability Maturity Model (SP-CMM) levels.</summary>
    public static IReadOnlyList<MaturityLevelDefinition> MaturityLevels { get; } =
    [
        new("L0", "Not Performed", "The control is not performed."),
        new("L1", "Performed Informally", "Practices are ad hoc and depend on individuals."),
        new("L2", "Planned & Tracked", "Practices are planned and tracked but not standardized."),
        new("L3", "Well Defined", "Practices are documented, standardized and consistently implemented."),
        new("L4", "Quantitatively Controlled", "Practices are measured and controlled with metrics."),
        new("L5", "Continuously Improving", "Practices are continuously improved and optimized."),
    ];

    /// <summary>NIST OLIR set-theory relationships between requirements.</summary>
    public static IReadOnlyList<RelationshipTypeDefinition> RelationshipTypes { get; } =
    [
        new("Equal", "Equal", true, "The two requirements are the same."),
        new("SubsetOf", "Subset Of", true, "The focal requirement is contained in the reference."),
        new("SupersetOf", "Superset Of", false, "The focal requirement contains the reference and more."),
        new("IntersectsWith", "Intersects With", false, "The requirements overlap partly."),
        new("RelatedTo", "Related To", false, "The requirements are related but not comparable."),
        new("NotRelated", "Not Related", false, "No relationship."),
    ];

    public static IReadOnlyList<ServiceTierDefinition> ServiceTiers { get; } =
    [
        // Taglines from the GDS Modern Workplace Service Tiers deck.
        new("Essentials", "Essentials", "Core Microsoft 365 management for organizations needing foundational support",
            "Standard Microsoft 365 licensing, helpdesk and technical support, basic endpoint and policy setup."),
        new("SecurePlus", "Secure Plus", "The security and productivity foundation for organizations building toward AI adoption",
            "Microsoft 365 Business Premium with E5 Security; identity and device protection, policy enforcement and compliance, data governance, backup, SOC monitoring."),
        new("SecurePlusPremium", "Secure Plus Premium", "Complete M365 experience with advanced security, analytics, AI readiness and Teams calling",
            "Microsoft 365 E5; advanced identity and access controls, threat detection, data classification, 24x7x365 SOC, monthly optimization reviews, Teams Calling."),
    ];

    public static IReadOnlyList<NamedCode> AppRoles { get; } =
    [
        new("Admin", "Administrator"), new("Engineer", "Engineer"), new("Sales", "Sales"), new("CustomerViewer", "Customer Viewer"),
    ];

    public static IReadOnlyList<NamedCode> EntityTypes { get; } =
    [
        new("Requirement", "Framework Requirement", "fw.Requirement"), new("Recommendation", "Baseline Recommendation", "cfg.Recommendation"),
        new("Setting", "Technical Setting", "cfg.Setting"), new("ConfigurationChange", "Configuration Change", "kb.ConfigurationChange"),
        new("StandardItem", "GDS Standard Item", "gds.StandardItem"), new("Artifact", "Policy Artifact", "pol.Artifact"),
        new("Expectation", "Experience Expectation", "eux.Expectation"), new("PolicySet", "Policy Set", "pol.PolicySet"),
    ];

    public static string? OrganizationSizeName(string? code) => OrganizationSizes.FirstOrDefault(value => value.Code == code)?.Label;
}
