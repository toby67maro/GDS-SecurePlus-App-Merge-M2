using System.Data.Common;
using System.Globalization;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Tenants;
using EngineeringWorkspace.Infrastructure.Database;

namespace EngineeringWorkspace.Infrastructure.Persistence;

/// <summary>
/// Tenant inventory in secureplus.TenantUser, TenantGroup, TenantGroupMember and TenantDevice (4.0 beta.2).
/// A scan rewrites one tenant's rows in a transaction: objects found keep their first-seen date, objects missing are kept with
/// RemovedAtUtc set (history), and a collection the scan did not read (null) is left exactly as it was.
/// </summary>
public sealed class SqlTenantInventoryRepository(IDatabaseStatusService database, DatabaseConnectionFactory connections, TimeProvider timeProvider) : ITenantInventoryRepository
{
    private const char ListSeparator = ';';

    private static readonly string[] UserColumns =
    [
        "TenantId", "ObjectId", "CustomerKey", "DisplayName", "UserPrincipalName", "Mail", "AccountEnabled", "UserType", "IsSynced", "Department", "JobTitle", "UsageLocation",
        "Licenses", "AdminRoles", "IsMfaRegistered", "MfaMethods", "LastSignInUtc", "CreatedUtc", "FirstSeenUtc", "LastSeenUtc", "RemovedAtUtc", "LastScanRunId"
    ];

    private static readonly string[] GroupColumns =
    [
        "TenantId", "ObjectId", "CustomerKey", "DisplayName", "Description", "Mail", "Kind", "IsDynamic", "MembershipRule", "IsRoleAssignable", "IsSynced", "MemberCount",
        "FirstSeenUtc", "LastSeenUtc", "RemovedAtUtc", "LastScanRunId"
    ];

    private static readonly string[] DeviceColumns =
    [
        "TenantId", "DeviceId", "CustomerKey", "DeviceName", "OperatingSystem", "OsVersion", "ComplianceState", "Ownership", "IsEncrypted", "UserPrincipalName", "EntraDeviceId",
        "Model", "Manufacturer", "LastSyncUtc", "EnrolledUtc", "FirstSeenUtc", "LastSeenUtc", "RemovedAtUtc", "LastScanRunId"
    ];

    public bool IsAvailable => database.Settings.Provider != DatabaseProvider.None && database.LatestStatus?.State == DatabaseOverallState.Ready;

    public async Task<TenantInventoryCounts> SaveAsync(string customerKey, TenantInventorySnapshot inventory, int? scanRunId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerKey);
        ArgumentNullException.ThrowIfNull(inventory);
        if (!IsAvailable) throw new InvalidOperationException("The tenant inventory is stored in the application database, which is not ready.");
        var now = timeProvider.GetUtcNow();
        var tenant = inventory.TenantId.ToString();
        await using var session = await OpenAsync(cancellationToken);
        await using var transaction = await session.Connection.BeginTransactionAsync(cancellationToken);
        var removed = 0;

        if (inventory.Users is { } users)
        {
            var (rows, gone) = await MergeAsync(session, transaction, "TenantUser", "ObjectId", tenant, users.Select(user => user.ObjectId), now, cancellationToken);
            removed += gone.Count(row => row["RemovedAtUtc"] is null);
            var data = users.DistinctBy(user => user.ObjectId, StringComparer.OrdinalIgnoreCase).Select(user => new object?[]
            {
                tenant, user.ObjectId, customerKey, SqlBatch.Text(user.DisplayName, 256), SqlBatch.Text(user.UserPrincipalName, 320), SqlBatch.Text(user.Mail, 320),
                session.Bool(user.AccountEnabled), SqlBatch.Text(user.UserType, 20), session.Bool(user.IsSynced), SqlBatch.Text(user.Department, 256), SqlBatch.Text(user.JobTitle, 256),
                SqlBatch.Text(user.UsageLocation, 10), SqlBatch.Text(Join(user.Licenses), 2000), SqlBatch.Text(Join(user.AdminRoles), 2000),
                user.IsMfaRegistered is { } mfa ? session.Bool(mfa) : null, SqlBatch.Text(Join(user.MfaMethods), 1000), Date(session, user.LastSignInUtc), Date(session, user.CreatedUtc),
                session.Date(rows.GetValueOrDefault(user.ObjectId, now)), session.Date(now), null, scanRunId
            }).Concat(gone.Select(row => Removed(session, row, UserColumns, now))).ToArray();
            await SqlBatch.BulkInsertAsync(session, transaction, session.Table("secureplus", "TenantUser"), UserColumns, data, null, cancellationToken);
        }

        if (inventory.Groups is { } groups)
        {
            var (rows, gone) = await MergeAsync(session, transaction, "TenantGroup", "ObjectId", tenant, groups.Select(group => group.ObjectId), now, cancellationToken);
            removed += gone.Count(row => row["RemovedAtUtc"] is null);
            var data = groups.DistinctBy(group => group.ObjectId, StringComparer.OrdinalIgnoreCase).Select(group => new object?[]
            {
                tenant, group.ObjectId, customerKey, SqlBatch.Text(group.DisplayName, 256), SqlBatch.Text(group.Description, 1024), SqlBatch.Text(group.Mail, 320),
                group.Kind, session.Bool(group.IsDynamic), SqlBatch.Text(group.MembershipRule, 3072), session.Bool(group.IsRoleAssignable), session.Bool(group.IsSynced),
                group.MemberCount, session.Date(rows.GetValueOrDefault(group.ObjectId, now)), session.Date(now), null, scanRunId
            }).Concat(gone.Select(row => Removed(session, row, GroupColumns, now))).ToArray();
            await SqlBatch.BulkInsertAsync(session, transaction, session.Table("secureplus", "TenantGroup"), GroupColumns, data, null, cancellationToken);
        }

        var members = 0;
        if (inventory.Members is { } list)
        {
            // Only the groups whose members were read are replaced; other groups keep their last known members.
            var table = session.Table("secureplus", "TenantGroupMember");
            foreach (var groupId in inventory.MemberGroupIds)
                await SqlBatch.ExecuteAsync(session, transaction, $"DELETE FROM {table} WHERE TenantId = @tenant AND GroupObjectId = @group;",
                    SqlBatch.Parameters(("@tenant", tenant), ("@group", groupId)), cancellationToken);
            var data = list.DistinctBy(member => (member.GroupObjectId.ToUpperInvariant(), member.MemberObjectId.ToUpperInvariant()))
                .Select(member => new object?[] { tenant, member.GroupObjectId, member.MemberObjectId, customerKey, SqlBatch.Text(member.MemberType, 40), session.Date(now) }).ToArray();
            await SqlBatch.BulkInsertAsync(session, transaction, table, ["TenantId", "GroupObjectId", "MemberObjectId", "CustomerKey", "MemberType", "CollectedAtUtc"], data, null,
                cancellationToken);
            members = data.Length;
        }

        if (inventory.Devices is { } devices)
        {
            var (rows, gone) = await MergeAsync(session, transaction, "TenantDevice", "DeviceId", tenant, devices.Select(device => device.DeviceId), now, cancellationToken);
            removed += gone.Count(row => row["RemovedAtUtc"] is null);
            var data = devices.DistinctBy(device => device.DeviceId, StringComparer.OrdinalIgnoreCase).Select(device => new object?[]
            {
                tenant, device.DeviceId, customerKey, SqlBatch.Text(device.DeviceName, 256), SqlBatch.Text(device.OperatingSystem, 60), SqlBatch.Text(device.OsVersion, 60),
                SqlBatch.Text(device.ComplianceState, 40), SqlBatch.Text(device.Ownership, 20), device.IsEncrypted is { } encrypted ? session.Bool(encrypted) : null,
                SqlBatch.Text(device.UserPrincipalName, 320), SqlBatch.Text(device.EntraDeviceId, 64), SqlBatch.Text(device.Model, 256), SqlBatch.Text(device.Manufacturer, 256),
                Date(session, device.LastSyncUtc), Date(session, device.EnrolledUtc), session.Date(rows.GetValueOrDefault(device.DeviceId, now)), session.Date(now), null, scanRunId
            }).Concat(gone.Select(row => Removed(session, row, DeviceColumns, now))).ToArray();
            await SqlBatch.BulkInsertAsync(session, transaction, session.Table("secureplus", "TenantDevice"), DeviceColumns, data, null, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new TenantInventoryCounts(inventory.Users?.Count ?? 0, inventory.Groups?.Count ?? 0, members, inventory.Devices?.Count ?? 0, removed);
    }

    /// <summary>
    /// Reads the tenant's existing rows, deletes them, and returns the first-seen date of each present object plus the full rows of objects
    /// that are no longer present (re-inserted as removed).
    /// </summary>
    private static async Task<(Dictionary<string, DateTimeOffset> FirstSeen, List<Dictionary<string, object?>> Gone)> MergeAsync(SqlSession session, DbTransaction transaction,
        string name, string key, string tenant, IEnumerable<string> present, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var table = session.Table("secureplus", name);
        var existing = await SqlBatch.QueryAsync(session, transaction, $"SELECT * FROM {table} WHERE TenantId = @tenant;", SqlBatch.Parameters(("@tenant", tenant)), cancellationToken);
        var found = present.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var firstSeen = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var gone = new List<Dictionary<string, object?>>();
        foreach (var row in existing)
        {
            var id = (string)row[key]!;
            if (found.Contains(id)) firstSeen[id] = SqlSession.ReadDate(row["FirstSeenUtc"]!);
            else gone.Add(row);
        }

        await SqlBatch.ExecuteAsync(session, transaction, $"DELETE FROM {table} WHERE TenantId = @tenant;", SqlBatch.Parameters(("@tenant", tenant)), cancellationToken);
        return (firstSeen, gone);
    }

    private static object?[] Removed(SqlSession session, Dictionary<string, object?> row, IReadOnlyList<string> columns, DateTimeOffset now) =>
        columns.Select(column => column == "RemovedAtUtc"
            ? row[column] is null or DBNull ? session.Date(now) : row[column]
            : row[column] is DBNull ? null : row[column]).ToArray();

    public async Task<IReadOnlyList<TenantGroup>> ListGroupsAsync(string customerKey, Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return [];
        await using var session = await OpenAsync(cancellationToken);
        var rows = await SqlBatch.QueryAsync(session, null,
            $"SELECT * FROM {session.Table("secureplus", "TenantGroup")} WHERE CustomerKey = @customer AND RemovedAtUtc IS NULL{(tenantId is null ? string.Empty : " AND TenantId = @tenant")} ORDER BY DisplayName;",
            SqlBatch.Parameters(("@customer", customerKey), ("@tenant", tenantId?.ToString())), cancellationToken);
        return rows.Select(row => new TenantGroup(Guid.Parse((string)row["TenantId"]!), (string)row["ObjectId"]!, (string)row["DisplayName"]!, Text(row, "Description"),
            Text(row, "Mail"), (string)row["Kind"]!, Bool(row, "IsDynamic"), Text(row, "MembershipRule"), Bool(row, "IsRoleAssignable"), Bool(row, "IsSynced"),
            row["MemberCount"] is null or DBNull ? null : Convert.ToInt64(row["MemberCount"], CultureInfo.InvariantCulture))).ToArray();
    }

    public async Task<IReadOnlyList<TenantUser>> ListUsersAsync(string customerKey, Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return [];
        await using var session = await OpenAsync(cancellationToken);
        var rows = await SqlBatch.QueryAsync(session, null,
            $"SELECT * FROM {session.Table("secureplus", "TenantUser")} WHERE CustomerKey = @customer AND RemovedAtUtc IS NULL{(tenantId is null ? string.Empty : " AND TenantId = @tenant")} ORDER BY DisplayName;",
            SqlBatch.Parameters(("@customer", customerKey), ("@tenant", tenantId?.ToString())), cancellationToken);
        return rows.Select(row => new TenantUser(Guid.Parse((string)row["TenantId"]!), (string)row["ObjectId"]!, (string)row["DisplayName"]!, Text(row, "UserPrincipalName"),
            Text(row, "Mail"), Bool(row, "AccountEnabled"), Text(row, "UserType"), Bool(row, "IsSynced"), Text(row, "Department"), Text(row, "JobTitle"), Text(row, "UsageLocation"),
            Split(Text(row, "Licenses")), Split(Text(row, "AdminRoles")), row["IsMfaRegistered"] is null or DBNull ? null : Bool(row, "IsMfaRegistered"), Split(Text(row, "MfaMethods")),
            DateOrNull(row["LastSignInUtc"]), DateOrNull(row["CreatedUtc"]))).ToArray();
    }

    public async Task<IReadOnlyList<TenantGroupMember>> ListMembersAsync(Guid tenantId, string groupObjectId, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return [];
        await using var session = await OpenAsync(cancellationToken);
        var rows = await SqlBatch.QueryAsync(session, null,
            $"SELECT GroupObjectId, MemberObjectId, MemberType FROM {session.Table("secureplus", "TenantGroupMember")} WHERE TenantId = @tenant AND GroupObjectId = @group;",
            SqlBatch.Parameters(("@tenant", tenantId.ToString()), ("@group", groupObjectId)), cancellationToken);
        return rows.Select(row => new TenantGroupMember(tenantId, (string)row["GroupObjectId"]!, (string)row["MemberObjectId"]!, Text(row, "MemberType"))).ToArray();
    }

    public async Task<IReadOnlyList<TenantDevice>> ListDevicesAsync(string customerKey, Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return [];
        await using var session = await OpenAsync(cancellationToken);
        var rows = await SqlBatch.QueryAsync(session, null,
            $"SELECT * FROM {session.Table("secureplus", "TenantDevice")} WHERE CustomerKey = @customer AND RemovedAtUtc IS NULL{(tenantId is null ? string.Empty : " AND TenantId = @tenant")} ORDER BY DeviceName;",
            SqlBatch.Parameters(("@customer", customerKey), ("@tenant", tenantId?.ToString())), cancellationToken);
        return rows.Select(row => new TenantDevice(Guid.Parse((string)row["TenantId"]!), (string)row["DeviceId"]!, (string)row["DeviceName"]!, Text(row, "OperatingSystem"),
            Text(row, "OsVersion"), Text(row, "ComplianceState"), Text(row, "Ownership"), row["IsEncrypted"] is null or DBNull ? null : Bool(row, "IsEncrypted"),
            Text(row, "UserPrincipalName"), Text(row, "EntraDeviceId"), Text(row, "Model"), Text(row, "Manufacturer"), DateOrNull(row["LastSyncUtc"]),
            DateOrNull(row["EnrolledUtc"]))).ToArray();
    }

    public async Task DeleteCustomerAsync(string customerKey, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return;
        await using var session = await OpenAsync(cancellationToken);
        foreach (var name in new[] { "TenantGroupMember", "TenantUser", "TenantGroup", "TenantDevice" })
            await SqlBatch.ExecuteAsync(session, null, $"DELETE FROM {session.Table("secureplus", name)} WHERE CustomerKey = @customer;",
                SqlBatch.Parameters(("@customer", customerKey)), cancellationToken);
    }

    private static string? Join(IReadOnlyList<string> values) => values.Count == 0 ? null : string.Join(ListSeparator, values);

    private static IReadOnlyList<string> Split(string? value) =>
        string.IsNullOrEmpty(value) ? [] : value.Split(ListSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static object? Date(SqlSession session, DateTimeOffset? value) => value is { } date ? session.Date(date) : null;

    private static DateTimeOffset? DateOrNull(object? value) => value is null or DBNull ? null : SqlSession.ReadDate(value);

    private static string? Text(Dictionary<string, object?> row, string column) => row[column] is string text ? text : null;

    private static bool Bool(Dictionary<string, object?> row, string column) => Convert.ToBoolean(row[column], CultureInfo.InvariantCulture);

    private Task<SqlSession> OpenAsync(CancellationToken cancellationToken) =>
        SqlSession.OpenAsync(database, connections, message => new InvalidOperationException(message), cancellationToken);
}
