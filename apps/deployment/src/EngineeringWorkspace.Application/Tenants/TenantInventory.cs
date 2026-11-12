using System.Globalization;
using System.Text.Json;
using EngineeringWorkspace.Application.Assessments;

namespace EngineeringWorkspace.Application.Tenants;

/// <summary>A user in a customer tenant (4.0 beta.2 inventory). Directory attributes only; never credentials.</summary>
public sealed record TenantUser(
    Guid TenantId,
    string ObjectId,
    string DisplayName,
    string? UserPrincipalName,
    string? Mail,
    bool AccountEnabled,
    string? UserType,
    bool IsSynced,
    string? Department,
    string? JobTitle,
    string? UsageLocation,
    IReadOnlyList<string> Licenses,
    IReadOnlyList<string> AdminRoles,
    bool? IsMfaRegistered,
    IReadOnlyList<string> MfaMethods,
    DateTimeOffset? LastSignInUtc,
    DateTimeOffset? CreatedUtc)
{
    public bool IsGuest => string.Equals(UserType, "Guest", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A group in a customer tenant.</summary>
public sealed record TenantGroup(
    Guid TenantId,
    string ObjectId,
    string DisplayName,
    string? Description,
    string? Mail,
    string Kind,
    bool IsDynamic,
    string? MembershipRule,
    bool IsRoleAssignable,
    bool IsSynced,
    long? MemberCount)
{
    /// <summary>Shown in pickers: "Sales Team (Security, 42 members)".</summary>
    public string Display => $"{DisplayName} ({Kind}{(IsDynamic ? ", dynamic" : string.Empty)}{(MemberCount is { } count ? $", {count:N0} member{(count == 1 ? string.Empty : "s")}" : string.Empty)})";
}

/// <summary>A transitive member of a group (only read for groups linked to Secure Plus user groups or deployment variables).</summary>
public sealed record TenantGroupMember(Guid TenantId, string GroupObjectId, string MemberObjectId, string? MemberType);

/// <summary>An Intune managed device (in-depth scan).</summary>
public sealed record TenantDevice(
    Guid TenantId,
    string DeviceId,
    string DeviceName,
    string? OperatingSystem,
    string? OsVersion,
    string? ComplianceState,
    string? Ownership,
    bool? IsEncrypted,
    string? UserPrincipalName,
    string? EntraDeviceId,
    string? Model,
    string? Manufacturer,
    DateTimeOffset? LastSyncUtc,
    DateTimeOffset? EnrolledUtc);

/// <summary>Group kinds as Microsoft 365 admin centre names them.</summary>
public static class TenantGroupKinds
{
    public const string Microsoft365 = "Microsoft 365";
    public const string Security = "Security";
    public const string MailSecurity = "Mail-enabled security";
    public const string Distribution = "Distribution";

    public static string Of(bool securityEnabled, bool mailEnabled, IReadOnlyCollection<string> groupTypes) =>
        groupTypes.Contains("Unified", StringComparer.OrdinalIgnoreCase) ? Microsoft365
        : securityEnabled && mailEnabled ? MailSecurity
        : securityEnabled ? Security
        : Distribution;
}

/// <summary>Everything one scan found. Collections that were not read (forbidden, not in this scan) are null so the store keeps what it had.</summary>
public sealed record TenantInventorySnapshot(
    Guid TenantId,
    DateTimeOffset CollectedAtUtc,
    IReadOnlyList<TenantUser>? Users,
    IReadOnlyList<TenantGroup>? Groups,
    IReadOnlyList<TenantGroupMember>? Members,
    IReadOnlyCollection<string> MemberGroupIds,
    IReadOnlyList<TenantDevice>? Devices);

/// <summary>Turns scan datasets into inventory rows (pure; unit-tested).</summary>
public static class TenantInventoryBuilder
{
    public static TenantInventorySnapshot Build(TenantSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var tenant = snapshot.TenantId;
        var skus = Collected(snapshot, TenantDatasets.SubscribedSkus)?.Items
            .Where(sku => sku.TryGetProperty("skuId", out _))
            .ToDictionary(sku => Text(sku, "skuId")!, sku => Text(sku, "skuPartNumber") ?? Text(sku, "skuId")!, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var roles = AdminRoles(snapshot);
        var mfa = Collected(snapshot, TenantDatasets.RegistrationDetails)?.Items
            .Where(item => Text(item, "id") is not null)
            .GroupBy(item => Text(item, "id")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<TenantUser>? users = Collected(snapshot, TenantDatasets.Users)?.Items.Select(user =>
        {
            var id = Text(user, "id")!;
            var licenses = user.TryGetProperty("assignedLicenses", out var assigned) && assigned.ValueKind == JsonValueKind.Array
                ? assigned.EnumerateArray().Select(license => Text(license, "skuId")).OfType<string>().Select(sku => skus.GetValueOrDefault(sku, sku)).Order(StringComparer.OrdinalIgnoreCase).ToArray()
                : [];
            JsonElement registration = default;
            var hasMfa = mfa is not null && mfa.TryGetValue(id, out registration);
            return new TenantUser(tenant, id, Text(user, "displayName") ?? id, Text(user, "userPrincipalName"), Text(user, "mail"), Bool(user, "accountEnabled") ?? true,
                Text(user, "userType"), Bool(user, "onPremisesSyncEnabled") == true, Text(user, "department"), Text(user, "jobTitle"), Text(user, "usageLocation"),
                licenses, roles.GetValueOrDefault(id) ?? [], hasMfa ? Bool(registration, "isMfaRegistered") : null,
                hasMfa && registration.TryGetProperty("methodsRegistered", out var methods) && methods.ValueKind == JsonValueKind.Array
                    ? methods.EnumerateArray().Select(method => method.GetString()).OfType<string>().ToArray() : [],
                user.TryGetProperty("signInActivity", out var activity) && activity.ValueKind == JsonValueKind.Object ? Date(activity, "lastSignInDateTime") : null,
                Date(user, "createdDateTime"));
        }).Where(user => user.ObjectId.Length > 0).ToArray();

        var counts = Collected(snapshot, TenantDatasets.GroupMemberCounts)?.Items
            .ToDictionary(item => Text(item, "groupId")!, item => item.GetProperty("count").GetInt64(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<TenantGroup>? groups = Collected(snapshot, TenantDatasets.Groups)?.Items.Select(group =>
        {
            var types = group.TryGetProperty("groupTypes", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(type => type.GetString()).OfType<string>().ToArray() : [];
            var id = Text(group, "id")!;
            return new TenantGroup(tenant, id, Text(group, "displayName") ?? id, Text(group, "description"), Text(group, "mail"),
                TenantGroupKinds.Of(Bool(group, "securityEnabled") == true, Bool(group, "mailEnabled") == true, types),
                types.Contains("DynamicMembership", StringComparer.OrdinalIgnoreCase), Text(group, "membershipRule"), Bool(group, "isAssignableToRole") == true,
                Bool(group, "onPremisesSyncEnabled") == true, counts.TryGetValue(id, out var count) ? count : null);
        }).ToArray();

        var memberData = Collected(snapshot, TenantDatasets.GroupMembers);
        IReadOnlyList<TenantGroupMember>? members = memberData?.Items
            .Select(item => new TenantGroupMember(tenant, Text(item, "groupId")!, Text(item, "memberId")!, Text(item, "type")))
            .Where(member => member.MemberObjectId is not null).ToArray();
        var memberGroups = members?.Select(member => member.GroupObjectId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];

        IReadOnlyList<TenantDevice>? devices = Collected(snapshot, TenantDatasets.ManagedDevices)?.Items.Select(device =>
            new TenantDevice(tenant, Text(device, "id")!, Text(device, "deviceName") ?? Text(device, "id")!, Text(device, "operatingSystem"), Text(device, "osVersion"),
                Text(device, "complianceState"), Text(device, "managedDeviceOwnerType"), Bool(device, "isEncrypted"), Text(device, "userPrincipalName"),
                Text(device, "azureADDeviceId"), Text(device, "model"), Text(device, "manufacturer"), Date(device, "lastSyncDateTime"), Date(device, "enrolledDateTime")))
            .ToArray();
        return new TenantInventorySnapshot(tenant, snapshot.CollectedAtUtc, users, groups, members, memberGroups, devices);
    }

    /// <summary>Active directory role names per principal ID, from role assignments expanded with their principal.</summary>
    private static Dictionary<string, IReadOnlyList<string>> AdminRoles(TenantSnapshot snapshot)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (Collected(snapshot, TenantDatasets.RoleAssignments) is not { } assignments) return result;
        foreach (var group in assignments.Items.Where(item => Text(item, "principalId") is not null).GroupBy(item => Text(item, "principalId")!, StringComparer.OrdinalIgnoreCase))
            result[group.Key] = group.Select(item => DirectoryRoles.Name(Text(item, "roleDefinitionId"))).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return result;
    }

    private static TenantDataset? Collected(TenantSnapshot snapshot, string name) =>
        snapshot.Datasets.TryGetValue(name, out var dataset) && dataset.IsCollected ? dataset : null;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static DateTimeOffset? Date(JsonElement element, string name) =>
        Text(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
}

/// <summary>Names of the built-in Entra roles by template ID (the ones that matter for admin inventory); others show their ID.</summary>
public static class DirectoryRoles
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["62e90394-69f5-4237-9190-012177145e10"] = "Global Administrator",
        ["e8611ab8-c189-46e8-94e1-60213ab1f814"] = "Privileged Role Administrator",
        ["7be44c8a-adaf-4e2a-84d6-ab2649e08a13"] = "Privileged Authentication Administrator",
        ["194ae4cb-b126-40b2-bd5b-6091b380977d"] = "Security Administrator",
        ["5d6b6bb7-de71-4623-b4af-96380a352509"] = "Security Reader",
        ["f2ef992c-3afb-46b9-b7cf-a126ee74c451"] = "Global Reader",
        ["29232cdf-9323-42fd-ade2-1d097af3e4de"] = "Exchange Administrator",
        ["f28a1f50-f6e7-4571-818b-6a12f2af6b6c"] = "SharePoint Administrator",
        ["69091246-20e8-4a56-aa4d-066075b2a7a8"] = "Teams Administrator",
        ["3a2c62db-5318-420d-8d74-23affee5d9d5"] = "Intune Administrator",
        ["fe930be7-5e62-47db-91af-98c3a49a38b1"] = "User Administrator",
        ["c4e39bd9-1100-46d3-8c65-fb160da0071f"] = "Authentication Administrator",
        ["b0f54661-2d74-4c50-afa3-1ec803f12efe"] = "Billing Administrator",
        ["729827e3-9c14-49f7-bb1b-9608f156bbb8"] = "Helpdesk Administrator",
        ["9b895d92-2cd3-44c7-9d02-a6ac2d5ea5c3"] = "Application Administrator",
        ["158c047a-c907-4556-b7ef-446551a6b5f7"] = "Cloud Application Administrator",
        ["b1be1c3e-b65d-4f19-8427-f6fa0d97feb9"] = "Conditional Access Administrator",
        ["17315797-102d-40b4-93e0-432062caca18"] = "Compliance Administrator",
        ["e6d1a23a-da11-4be4-9570-befc86d067a7"] = "Compliance Data Administrator",
        ["8ac3fc64-6eca-42ea-9e69-59f4c7b60eb2"] = "Hybrid Identity Administrator",
        ["0526716b-113d-4c15-b2c8-68e3c22b9f80"] = "Authentication Policy Administrator",
    };

    public static string Name(string? templateId) => templateId is null ? "Unknown role" : Names.GetValueOrDefault(templateId, templateId);
}

/// <summary>Stores and reads tenant inventory (secureplus.TenantUser, TenantGroup, TenantGroupMember, TenantDevice).</summary>
public interface ITenantInventoryRepository
{
    bool IsAvailable { get; }

    /// <summary>Upserts what the scan found; objects no longer present are marked removed (never deleted). Returns counts.</summary>
    Task<TenantInventoryCounts> SaveAsync(string customerKey, TenantInventorySnapshot inventory, int? scanRunId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TenantGroup>> ListGroupsAsync(string customerKey, Guid? tenantId = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TenantUser>> ListUsersAsync(string customerKey, Guid? tenantId = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TenantGroupMember>> ListMembersAsync(Guid tenantId, string groupObjectId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TenantDevice>> ListDevicesAsync(string customerKey, Guid? tenantId = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes every inventory row of a customer (when the customer is deleted).</summary>
    Task DeleteCustomerAsync(string customerKey, CancellationToken cancellationToken = default);
}

public sealed record TenantInventoryCounts(int Users, int Groups, int Members, int Devices, int Removed);
