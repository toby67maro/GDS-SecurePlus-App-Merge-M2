using System.Text.Json;
using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Domain.Tenants;

namespace EngineeringWorkspace.Application.Assessments;

/// <summary>Outcome values of one evaluated control.</summary>
public static class AssessmentResultValues
{
    public const string Pass = "Pass";
    public const string Partial = "Partial";
    public const string Fail = "Fail";
    public const string Missing = "Missing";
    public const string NotEvaluated = "Not evaluated";
    public const string Manual = "Manual";
    public const string Info = "Info";

    /// <summary>Results that count towards the score.</summary>
    public static bool IsScored(string result) => result is Pass or Partial or Fail or Missing;
}

/// <summary>The evaluation of one catalog control or tenant check.</summary>
public sealed record AssessmentResultItem(string ControlId, string Area, string Control, string Result, string Severity, string? Evidence, string? Recommendation);

public enum DatasetStatus
{
    Collected,
    Forbidden,
    NotAvailable,
    Failed
}

/// <summary>One Microsoft Graph (or DNS) dataset read from a tenant.</summary>
public sealed record TenantDataset(string Name, string Source, DatasetStatus Status, IReadOnlyList<JsonElement> Items, string? Message)
{
    public bool IsCollected => Status == DatasetStatus.Collected;
}

/// <summary>Everything read from a tenant for one assessment or live view. Read-only data, never secrets.</summary>
public sealed class TenantSnapshot(Guid tenantId, DateTimeOffset collectedAtUtc, IReadOnlyDictionary<string, TenantDataset> datasets)
{
    public Guid TenantId { get; } = tenantId;
    public DateTimeOffset CollectedAtUtc { get; } = collectedAtUtc;
    public IReadOnlyDictionary<string, TenantDataset> Datasets { get; } = datasets;

    /// <summary>Message of a dataset that was not part of this scan (quick scans read fewer datasets).</summary>
    public const string NotCollectedMessage = "Not collected.";

    public TenantDataset Get(string name) =>
        Datasets.TryGetValue(name, out var dataset) ? dataset : new TenantDataset(name, string.Empty, DatasetStatus.NotAvailable, [], NotCollectedMessage);
}

/// <summary>Dataset names the collector produces and the evaluator and Live Assessment read.</summary>
public static class TenantDatasets
{
    public const string Organization = "organization";
    public const string Domains = "domains";
    public const string SubscribedSkus = "subscribedSkus";
    public const string ConditionalAccessPolicies = "conditionalAccessPolicies";
    public const string AuthorizationPolicy = "authorizationPolicy";
    public const string SecurityDefaults = "securityDefaults";
    public const string RegistrationDetails = "registrationDetails";
    public const string RoleAssignments = "roleAssignments";
    public const string RoleEligibilitySchedules = "roleEligibilitySchedules";
    public const string Users = "users";
    public const string Devices = "devices";
    public const string DynamicGroups = "dynamicGroups";
    public const string Applications = "applications";
    public const string ConfigurationPolicies = "configurationPolicies";
    public const string DeviceConfigurations = "deviceConfigurations";
    public const string CompliancePolicies = "compliancePolicies";
    public const string EnrollmentConfigurations = "enrollmentConfigurations";
    public const string ManagedDevices = "managedDevices";
    public const string AndroidAppProtections = "androidAppProtections";
    public const string IosAppProtections = "iosAppProtections";
    public const string AssignedApps = "assignedApps";
    public const string Scripts = "deviceManagementScripts";
    public const string DriverUpdateProfiles = "windowsDriverUpdateProfiles";
    public const string SecureScores = "secureScores";
    public const string AttackSimulations = "attackSimulations";
    public const string SensitivityLabels = "sensitivityLabels";
    public const string RetentionLabels = "retentionLabels";
    public const string SharePointSettings = "sharePointSettings";
    public const string DnsRecords = "dnsRecords";
    /// <summary>Every group (4.0 beta.2 inventory): security, Microsoft 365, distribution, dynamic, role-assignable.</summary>
    public const string Groups = "groups";
    /// <summary>Transitive member count per group ({ groupId, count }).</summary>
    public const string GroupMemberCounts = "groupMemberCounts";
    /// <summary>Transitive members of the groups linked to Secure Plus user groups or deployment variables ({ groupId, memberId, type }).</summary>
    public const string GroupMembers = "groupMembers";

    // 4.0 beta.2 tenant settings (in-depth scans).
    public const string AuthenticationMethodsPolicy = "authenticationMethodsPolicy";
    public const string CrossTenantAccessDefault = "crossTenantAccessDefault";
    public const string AdminConsentRequestPolicy = "adminConsentRequestPolicy";
    public const string DeviceRegistrationPolicy = "deviceRegistrationPolicy";
    public const string GroupSettings = "groupSettings";
    public const string GlobalAdminPimPolicy = "globalAdminPimPolicy";
    public const string AccessReviews = "accessReviews";
    public const string ActivityTimeoutPolicies = "activityBasedTimeoutPolicies";
    public const string ReportSettings = "reportSettings";
    public const string FormsSettings = "formsSettings";
    public const string IntuneSettings = "intuneTenantSettings";
    public const string AutopilotProfiles = "autopilotProfiles";
    public const string MultiAdminApproval = "multiAdminApprovalPolicies";
    public const string IntuneRoleAssignments = "intuneRoleAssignments";
    public const string ForwardingProfiles = "gsaForwardingProfiles";
    public const string BackupService = "m365BackupService";
    public const string AppsAndServices = "appsAndServicesSettings";
    public const string CloudPcUserSettings = "cloudPcUserSettings";
    public const string CloudPcProvisioningPolicies = "cloudPcProvisioningPolicies";
    public const string TeamsAppSettings = "teamsAppSettings";
    public const string NamedLocations = "namedLocations";
    public const string AuthenticationStrengths = "authenticationStrengthPolicies";
}

/// <summary>The two kinds of scan (4.0 beta.2). Live Assessment runs quick scans; Assessment Scan offers both.</summary>
public static class ScanTypes
{
    /// <summary>Inventory (users, groups, membership, roles, licences, domains) and a posture snapshot. Minutes.</summary>
    public const string Quick = "Quick";

    /// <summary>Everything: the full assessment plus Intune devices, policy and app assignments, sign-in activity, MFA methods and licences per user.</summary>
    public const string InDepth = "InDepth";

    public static string Normalize(string? value) => string.Equals(value, Quick, StringComparison.OrdinalIgnoreCase) ? Quick : InDepth;

    public static string Display(string? value) => Normalize(value) == Quick ? "Quick scan" : "In-depth scan";

    /// <summary>Datasets a quick scan reads (DNS records included).</summary>
    public static IReadOnlySet<string> QuickDatasets { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        TenantDatasets.Organization, TenantDatasets.Domains, TenantDatasets.SubscribedSkus, TenantDatasets.ConditionalAccessPolicies,
        TenantDatasets.AuthorizationPolicy, TenantDatasets.SecurityDefaults, TenantDatasets.RegistrationDetails, TenantDatasets.RoleAssignments,
        TenantDatasets.RoleEligibilitySchedules, TenantDatasets.Users, TenantDatasets.Groups, TenantDatasets.GroupMemberCounts, TenantDatasets.GroupMembers,
        TenantDatasets.DynamicGroups, TenantDatasets.SecureScores, TenantDatasets.DnsRecords,
    };

    public static bool Includes(string scanType, string dataset) => Normalize(scanType) == InDepth || QuickDatasets.Contains(dataset);
}

/// <summary>What a scan reads: its type and the Entra groups whose members are needed (groups linked to user groups or deployment variables).</summary>
public sealed record TenantScanOptions(string ScanType, IReadOnlyCollection<Guid> LinkedGroupIds)
{
    public static TenantScanOptions InDepth { get; } = new(ScanTypes.InDepth, []);
}

/// <summary>Reads a tenant with its app registration (app-only, read-only). Infrastructure.</summary>
public interface ITenantDataCollector
{
    Task<TenantSnapshot> CollectAsync(TenantConfiguration tenant, IProgress<(double Progress, string Activity)>? progress, CancellationToken cancellationToken) =>
        CollectAsync(tenant, TenantScanOptions.InDepth, progress, cancellationToken);

    Task<TenantSnapshot> CollectAsync(TenantConfiguration tenant, TenantScanOptions options, IProgress<(double Progress, string Activity)>? progress, CancellationToken cancellationToken);
}

/// <summary>Evaluates catalog controls and tenant checks against a snapshot.</summary>
public interface IAssessmentEvaluator
{
    IReadOnlyList<AssessmentResultItem> Evaluate(ControlCatalog catalog, TenantSnapshot snapshot);
}

/// <summary>Score of a result set: passes plus half of partials over everything scored, as a percentage.</summary>
public static class AssessmentScoring
{
    public static decimal? Score(IReadOnlyCollection<AssessmentResultItem> results)
    {
        var scored = results.Where(result => AssessmentResultValues.IsScored(result.Result)).ToArray();
        if (scored.Length == 0) return null;
        var points = scored.Sum(result => result.Result == AssessmentResultValues.Pass ? 1m : result.Result == AssessmentResultValues.Partial ? 0.5m : 0m);
        return Math.Round(100m * points / scored.Length, 1);
    }
}

/// <summary>Status of a schedule's most recent run.</summary>
public static class ScheduleStatusCodes
{
    public const string Idle = "Idle";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
}

/// <summary>A recurring assessment of a customer ("*" = every customer with a connected tenant).</summary>
public sealed record AssessmentSchedule(
    int ScheduleId,
    string Name,
    IReadOnlyList<string> CustomerKeys,
    Scheduling.ScheduleRecurrence Recurrence,
    bool IsEnabled,
    string TimeZoneId,
    DateTimeOffset? LastRunUtc,
    DateTimeOffset? NextRunUtc)
{
    public bool AllCustomers => CustomerKeys.Contains("*");

    /// <summary>Idle, Running, Succeeded or Failed (see <see cref="ScheduleStatusCodes"/>).</summary>
    public string LastStatus { get; init; } = ScheduleStatusCodes.Idle;
    public string? LastMessage { get; init; }
    public string? CreatedBy { get; init; }
    public DateTimeOffset? ModifiedAtUtc { get; init; }

    /// <summary>Quick or in-depth scan (<see cref="ScanTypes"/>); schedules made before 4.0 beta.2 run in-depth scans.</summary>
    public string ScanType { get; init; } = ScanTypes.InDepth;

    /// <summary>The schedule's time zone; the local zone when the stored one is unknown on this computer.</summary>
    public TimeZoneInfo Zone => FindZone(TimeZoneId);

    public static TimeZoneInfo FindZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Local;
        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Local;
    }

    public string Describe() => Recurrence.Describe();
}

/// <summary>One execution of a schedule.</summary>
public sealed record ScheduleRunInfo(int ScheduleRunId, int ScheduleId, DateTimeOffset StartedAtUtc, DateTimeOffset? CompletedAtUtc, string StatusCode, string? Message, string? Host,
    string? AssessmentRunIds);

