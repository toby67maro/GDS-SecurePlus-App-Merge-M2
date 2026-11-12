using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Domain.Tenants;

namespace EngineeringWorkspace.Application.Tenants;

/// <summary>
/// Maps capabilities to the Microsoft Graph application permissions required by the operations the assessment engine
/// and tenant connection actually call (least privilege, read-only). Each permission names the calls that need it;
/// docs/configuration/graph-permissions.md has the full reference. More privileged permissions that include a
/// required one (for example Directory.Read.All for Organization.Read.All, or any *.ReadWrite.* for its *.Read.*)
/// also satisfy it.
/// </summary>
public sealed class PermissionValidationService : IPermissionValidationService
{
    /// <summary>One required permission of a capability.</summary>
    public sealed record PermissionRule(TenantCapability Capability, string Permission, IReadOnlyList<string> SatisfiedBy, string Operations);

    private static PermissionRule Rule(TenantCapability capability, string permission, string operations, params string[] alsoSatisfiedBy) =>
        new(capability, permission, [permission, .. alsoSatisfiedBy, .. ReadWriteOf(permission)], operations);

    private static IEnumerable<string> ReadWriteOf(string permission) =>
        permission.Contains(".Read.", StringComparison.Ordinal) ? [permission.Replace(".Read.", ".ReadWrite.", StringComparison.Ordinal)] : [];

    /// <summary>Every capability's required permissions. Capabilities with no Graph API (Exchange, Teams) have none.</summary>
    public static IReadOnlyList<PermissionRule> Rules { get; } =
    [
        Rule(TenantCapability.TenantDiscovery, "Organization.Read.All", "GET /organization, /domains, /subscribedSkus (tenant identity, domains, licences)", "Directory.Read.All"),

        Rule(TenantCapability.EntraAssessment, "Directory.Read.All", "GET /users, /groups, /devices, /domains, /applications, /servicePrincipals (accounts, dynamic groups, stale devices, apps)"),
        Rule(TenantCapability.EntraAssessment, "Policy.Read.All", "GET /policies/authorizationPolicy, /policies/identitySecurityDefaultsEnforcementPolicy (consent, app registration and guest settings)"),
        Rule(TenantCapability.EntraAssessment, "AuditLog.Read.All", "GET /reports/authenticationMethods/userRegistrationDetails and users' signInActivity (MFA registration, dormant accounts)"),
        Rule(TenantCapability.EntraAssessment, "RoleManagement.Read.Directory", "GET /roleManagement/directory/roleAssignments and roleEligibilitySchedules (Global Administrators, permanent admin assignments)",
            "RoleManagement.Read.All", "Directory.Read.All"),

        Rule(TenantCapability.ConditionalAccessAssessment, "Policy.Read.All", "GET /identity/conditionalAccess/policies (Conditional Access controls and checks)"),

        // 4.0 beta.2 tenant settings.
        Rule(TenantCapability.EntraAssessment, "RoleManagementPolicy.Read.Directory", "GET /policies/roleManagementPolicyAssignments (PIM settings for Global Administrator)"),
        Rule(TenantCapability.EntraAssessment, "AccessReview.Read.All", "GET /identityGovernance/accessReviews/definitions (access reviews)"),
        Rule(TenantCapability.EntraAssessment, "Policy.Read.DeviceConfiguration", "GET /policies/deviceRegistrationPolicy (who can join devices, device limit, local admin)"),
        Rule(TenantCapability.EntraAssessment, "ReportSettings.Read.All", "GET /admin/reportSettings (concealed names in usage reports)"),
        Rule(TenantCapability.EntraAssessment, "OrgSettings-Forms.Read.All", "GET /admin/forms/settings (Microsoft Forms phishing protection)"),
        Rule(TenantCapability.IntuneAssessment, "DeviceManagementRBAC.Read.All", "GET /deviceManagement/roleAssignments, operationApprovalPolicies (Intune RBAC, multi-admin approval)"),
        Rule(TenantCapability.TeamsAssessment, "TeamworkAppSettings.Read.All", "GET /teamwork/teamsAppSettings (Teams org-wide app settings)"),
        Rule(TenantCapability.EntraAssessment, "NetworkAccess.Read.All", "GET /networkAccess/forwardingProfiles (Global Secure Access traffic profiles)"),
        Rule(TenantCapability.EntraAssessment, "BackupRestore-Configuration.Read.All", "GET /solutions/backupRestore (Microsoft 365 Backup)"),
        Rule(TenantCapability.EntraAssessment, "OrgSettings-AppsAndServices.Read.All", "GET /admin/appsAndServices (Microsoft 365 user-owned apps and trials)"),
        Rule(TenantCapability.IntuneAssessment, "CloudPC.Read.All", "GET /deviceManagement/virtualEndpoint (Windows 365 user settings and provisioning policies)"),

        Rule(TenantCapability.IntuneAssessment, "DeviceManagementConfiguration.Read.All",
            "GET /deviceManagement/configurationPolicies (settings catalog, endpoint security), deviceConfigurations, deviceCompliancePolicies"),
        Rule(TenantCapability.IntuneAssessment, "DeviceManagementApps.Read.All", "GET /deviceAppManagement/androidManagedAppProtections, iosManagedAppProtections, mobileApps"),
        Rule(TenantCapability.IntuneAssessment, "DeviceManagementScripts.Read.All", "GET /deviceManagement/deviceManagementScripts (PowerShell scripts deployed by Intune)"),
        Rule(TenantCapability.IntuneAssessment, "DeviceManagementServiceConfig.Read.All", "GET /deviceManagement/deviceEnrollmentConfigurations (personal device enrollment restrictions)"),

        Rule(TenantCapability.DeviceAssessment, "DeviceManagementManagedDevices.Read.All", "GET /deviceManagement/managedDevices (enrolled devices and compliance state)"),

        Rule(TenantCapability.DefenderAssessment, "SecurityEvents.Read.All", "GET /security/secureScores (Microsoft Secure Score)"),
        Rule(TenantCapability.DefenderAssessment, "AttackSimulation.Read.All", "GET /security/attackSimulation/simulations (attack simulation training)"),

        Rule(TenantCapability.PurviewAssessment, "InformationProtectionPolicy.Read.All", "GET /security/informationProtection/sensitivityLabels (sensitivity labels)"),
        Rule(TenantCapability.PurviewAssessment, "RecordsManagement.Read.All", "GET /security/labels/retentionLabels (retention labels)"),

        Rule(TenantCapability.SharePointAssessment, "SharePointTenantSettings.Read.All", "GET /admin/sharepoint/settings (external sharing)"),

        Rule(TenantCapability.LicenseAssessment, "Organization.Read.All", "GET /subscribedSkus (licences)", "Directory.Read.All"),

        Rule(TenantCapability.ApplicationAssessment, "Application.Read.All", "GET /applications and /servicePrincipals (app inventory)", "Directory.Read.All"),

        // Live deployment of approved change requests (Deployment > Change Execution > Apply to Tenant).
        Rule(TenantCapability.PolicyDeployment, "Policy.ReadWrite.ConditionalAccess", "POST /identity/conditionalAccess/policies (created in report-only state)"),
        Rule(TenantCapability.PolicyDeployment, "DeviceManagementConfiguration.ReadWrite.All",
            "POST /deviceManagement/configurationPolicies, deviceConfigurations, deviceCompliancePolicies, intents (Intune and endpoint security policies)"),
        Rule(TenantCapability.PolicyDeployment, "DeviceManagementApps.ReadWrite.All", "POST /deviceAppManagement/*ManagedAppProtections (app protection policies)"),
        Rule(TenantCapability.PolicyDeployment, "DeviceManagementServiceConfig.ReadWrite.All", "POST /deviceManagement/deviceEnrollmentConfigurations (enrollment restrictions)"),
        Rule(TenantCapability.PolicyDeployment, "Policy.Read.All", "GET /identity/conditionalAccess/policies (existing policy names before creating)"),
        Rule(TenantCapability.PolicyDeployment, "Group.Read.All", "GET /groups (resolve deployment group variables to object IDs)"),
        Rule(TenantCapability.PolicyDeployment, "Organization.Read.All", "GET /organization (sign-in test of the Deploy registration)"),
    ];

    /// <summary>Capabilities whose checks use no Microsoft Graph application permission (shown as "Not required").</summary>
    private static readonly IReadOnlyDictionary<TenantCapability, string> NoGraphPermission = new Dictionary<TenantCapability, string>
    {
        [TenantCapability.ExchangeAssessment] = "SPF, DKIM and DMARC are checked in public DNS; Exchange Online and Purview settings are read by the read-only PowerShell collector (Exchange.ManageAsApp and the Global Reader role, granted by Update permissions).",
    };

    /// <summary>Capabilities of the Read registration: everything the scans use, no write permission (4.0 beta.2).</summary>
    public static IReadOnlyList<TenantCapability> ReadCapabilities { get; } =
        Enum.GetValues<TenantCapability>().Where(capability => capability != TenantCapability.PolicyDeployment).ToArray();

    /// <summary>Capabilities of the Deploy registration, which exists only during deploy phases.</summary>
    public static IReadOnlyList<TenantCapability> DeployCapabilities { get; } = [TenantCapability.PolicyDeployment];

    /// <summary>
    /// Capabilities a registration is checked against. A registration that names only TenantDiscovery (the old default) uses all
    /// read capabilities. Write permissions belong to the Deploy registration only.
    /// </summary>
    public static IReadOnlyCollection<TenantCapability> Effective(IReadOnlyCollection<TenantCapability> configured) =>
        configured.All(capability => capability == TenantCapability.TenantDiscovery) ? ReadCapabilities : configured.Distinct().ToArray();

    public IReadOnlyList<CapabilityPermissionStatus> Describe(IReadOnlyCollection<TenantCapability> configured)
    {
        var effective = Effective(configured);
        var rows = new List<CapabilityPermissionStatus>();
        foreach (var capability in Enum.GetValues<TenantCapability>())
        {
            var rules = Rules.Where(rule => rule.Capability == capability).ToArray();
            if (rules.Length == 0)
            {
                rows.Add(new CapabilityPermissionStatus(capability, "Not required", "None", effective.Contains(capability), CheckState.NotApplicable,
                    NoGraphPermission.GetValueOrDefault(capability, "No Microsoft Graph permission is needed.")));
                continue;
            }

            rows.AddRange(rules.Select(rule => new CapabilityPermissionStatus(capability, rule.Permission, "Application", effective.Contains(capability), CheckState.NotRun,
                $"Not validated. Needed for {rule.Operations}.")));
        }

        return rows;
    }

    public IReadOnlyList<CapabilityPermissionStatus> Validate(IReadOnlyCollection<TenantCapability> configured, IReadOnlyCollection<string> grantedApplicationPermissions)
    {
        var granted = new HashSet<string>(grantedApplicationPermissions, StringComparer.OrdinalIgnoreCase);
        return Describe(configured).Select(row =>
        {
            var rule = Rules.FirstOrDefault(candidate => candidate.Capability == row.Capability && candidate.Permission == row.RequiredPermission);
            if (rule is null || !row.Configured) return row;
            var satisfiedBy = rule.SatisfiedBy.FirstOrDefault(granted.Contains);
            return satisfiedBy is null
                ? row with { Validated = CheckState.Failed, Status = $"Missing. Needed for {rule.Operations}. Use Update permissions automatically, or grant it with admin consent." }
                : row with { Validated = CheckState.Passed, Status = satisfiedBy == rule.Permission ? "Granted" : $"Granted through {satisfiedBy}" };
        }).ToArray();
    }

    public IReadOnlyList<string> RequiredApplicationPermissions(IReadOnlyCollection<TenantCapability> configured)
    {
        var effective = Effective(configured);
        return Rules.Where(rule => effective.Contains(rule.Capability)).Select(rule => rule.Permission)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
    }
}
