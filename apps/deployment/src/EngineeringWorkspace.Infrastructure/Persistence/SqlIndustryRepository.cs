using System.Globalization;
using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Reference;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Infrastructure.Database;

namespace EngineeringWorkspace.Infrastructure.Persistence;

/// <summary>The Industry pick-list in secureplus.Industry.</summary>
public sealed class SqlIndustryRepository(IDatabaseStatusService database, DatabaseConnectionFactory connections, AuditContext audit, TimeProvider timeProvider,
    IUserDirectory? users = null) : IIndustryRepository
{
    public bool IsAvailable => database.Settings.Provider != DatabaseProvider.None && database.LatestStatus?.State == DatabaseOverallState.Ready;

    public async Task<IReadOnlyList<IndustryValue>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return IndustryList.Defaults;
        await EnsureSeededAsync(cancellationToken);
        await using var session = await OpenAsync(cancellationToken);
        var rows = await SqlBatch.QueryAsync(session, null,
            $"SELECT Code, Name, SortOrder, IsActive, IsSystem, IsModified FROM {session.Table("secureplus", "Industry")} ORDER BY SortOrder, Name;", null, cancellationToken);
        return rows.Select(row => new IndustryValue((string)row["Code"]!, (string)row["Name"]!, Convert.ToInt32(row["SortOrder"], CultureInfo.InvariantCulture),
                Convert.ToBoolean(row["IsActive"], CultureInfo.InvariantCulture), Convert.ToBoolean(row["IsSystem"], CultureInfo.InvariantCulture),
                Convert.ToBoolean(row["IsModified"], CultureInfo.InvariantCulture)))
            .Where(item => includeInactive || item.IsActive).ToArray();
    }

    public async Task<int> EnsureSeededAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return 0;
        await using var session = await OpenAsync(cancellationToken);
        var table = session.Table("secureplus", "Industry");
        var existing = (await SqlBatch.QueryAsync(session, null, $"SELECT Code FROM {table};", null, cancellationToken))
            .Select(row => (string)row["Code"]!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = IndustryList.Defaults.Where(item => !existing.Contains(item.Code)).ToArray();
        foreach (var item in missing)
            await SqlBatch.ExecuteAsync(session, null, $"INSERT INTO {table} (Code, Name, SortOrder, IsActive, IsSystem, IsModified) VALUES (@code, @name, @sort, @active, @system, @modified);",
                SqlBatch.Parameters(("@code", item.Code), ("@name", item.Name), ("@sort", item.SortOrder), ("@active", session.Bool(true)), ("@system", session.Bool(true)),
                    ("@modified", session.Bool(false))), cancellationToken);
        return missing.Length;
    }

    public async Task SaveAsync(IndustryValue value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value.Name) || value.Name.Trim().Length > 200) throw new ArgumentException("Enter a name of at most 200 characters.", nameof(value));
        if (!IsAvailable) throw new InvalidOperationException("The industry list is stored in the application database, which is not ready.");
        if (users is not null && await users.IsEnforcedAsync(cancellationToken) && !AppRoles.CanManageUsers((await users.CurrentAsync(cancellationToken))?.RoleCode))
            throw new UnauthorizedAccessException("Only a Lead can change the industry list.");
        var code = string.IsNullOrWhiteSpace(value.Code)
            ? new string(value.Name.Trim().ToUpperInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray()).Trim('-')
            : value.Code.Trim();
        if (code.Length > 40) code = code[..40];
        await using var session = await OpenAsync(cancellationToken);
        var table = session.Table("secureplus", "Industry");
        var exists = await SqlBatch.CountAsync(session, null, $"SELECT COUNT(*) FROM {table} WHERE Code = @code;", SqlBatch.Parameters(("@code", code)), cancellationToken) > 0;
        await SqlBatch.ExecuteAsync(session, null, exists
                ? $"UPDATE {table} SET Name = @name, SortOrder = @sort, IsActive = @active, IsModified = CASE WHEN IsSystem = 1 THEN 1 ELSE IsModified END, ModifiedBy = @by, ModifiedAtUtc = @at WHERE Code = @code;"
                : $"INSERT INTO {table} (Code, Name, SortOrder, IsActive, IsSystem, IsModified, ModifiedBy, ModifiedAtUtc) VALUES (@code, @name, @sort, @active, 0, 0, @by, @at);",
            SqlBatch.Parameters(("@code", code), ("@name", value.Name.Trim()), ("@sort", value.SortOrder), ("@active", session.Bool(value.IsActive)), ("@by", audit.Actor),
                ("@at", session.Date(timeProvider.GetUtcNow()))), cancellationToken);
    }

    private Task<SqlSession> OpenAsync(CancellationToken cancellationToken) =>
        SqlSession.OpenAsync(database, connections, message => new InvalidOperationException(message), cancellationToken);
}
