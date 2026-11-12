using System.Data.Common;
using System.Globalization;
using System.Text;
using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Domain.Auditing;
using EngineeringWorkspace.Infrastructure.Database;
using EngineeringWorkspace.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Infrastructure.Security;

/// <summary>
/// Users &amp; Roles in secureplus.AppUser (V0008). Until the first user is added every user has full access, as in 3.1; the
/// first user must be a Lead, and the last active Lead cannot be removed or demoted. Database access is granted per
/// Windows account (no AD groups): a login FROM WINDOWS, a database user and membership of one secureplus_* role.
/// </summary>
public sealed class SqlUserDirectory(
    IDatabaseStatusService database,
    DatabaseConnectionFactory connections,
    IAuditService audit,
    TimeProvider timeProvider,
    ILogger<SqlUserDirectory> logger,
    string? currentAccount = null) : IUserDirectory
{
    public string CurrentAccount { get; } = AppUser.NormalizeAccount(currentAccount ?? $"{Environment.UserDomainName}\\{Environment.UserName}");

    private bool IsAvailable => database.Settings.Provider != DatabaseProvider.None && database.LatestStatus?.State == DatabaseOverallState.Ready;

    public async Task<bool> IsEnforcedAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return false;
        await using var session = await OpenAsync(cancellationToken);
        await using var command = session.Command(null, $"SELECT COUNT(*) FROM {session.Table("secureplus", "AppUser")} WHERE IsActive = 1;");
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0;
    }

    public async Task<AppUser?> CurrentAsync(CancellationToken cancellationToken = default) =>
        IsAvailable ? (await ListAsync(cancellationToken)).FirstOrDefault(user => user.IsActive && string.Equals(user.WindowsAccount, CurrentAccount, StringComparison.OrdinalIgnoreCase)) : null;

    public async Task<IReadOnlyList<AppUser>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return [];
        await using var session = await OpenAsync(cancellationToken);
        await using var command = session.Command(null,
            $"SELECT WindowsAccount, Upn, DisplayName, RoleCode, IsActive, LastSeenUtc FROM {session.Table("secureplus", "AppUser")} ORDER BY WindowsAccount;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var users = new List<AppUser>();
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(new AppUser(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
                Convert.ToBoolean(reader.GetValue(4), CultureInfo.InvariantCulture), reader.IsDBNull(5) ? null : SqlSession.ReadDate(reader.GetValue(5))));
        }

        return users;
    }

    public async Task SaveAsync(AppUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var account = AppUser.NormalizeAccount(user.WindowsAccount);
        if (!AppUser.IsValidAccount(account)) throw new ArgumentException("Enter the Windows account as DOMAIN\\name, for example GDS\\jsmith.", nameof(user));
        if (!AppRoles.IsValid(user.RoleCode)) throw new ArgumentException("Choose a role.", nameof(user));
        if (!IsAvailable) throw new InvalidOperationException("Users and roles need the application database. Connect it in Settings > Database Configuration.");

        var users = await ListAsync(cancellationToken);
        var active = users.Where(item => item.IsActive).ToArray();
        if (active.Length == 0 && (!user.IsActive || user.RoleCode != AppRoles.Lead))
            throw new InvalidOperationException("The first user must be an active Lead, so someone can manage users.");
        if (active.Length > 0)
        {
            var me = active.FirstOrDefault(item => string.Equals(item.WindowsAccount, CurrentAccount, StringComparison.OrdinalIgnoreCase));
            if (!AppRoles.CanManageUsers(me?.RoleCode)) throw new UnauthorizedAccessException("Only a Lead can change users and roles.");
            var leads = active.Where(item => item.RoleCode == AppRoles.Lead).ToArray();
            if (leads.Length == 1 && string.Equals(leads[0].WindowsAccount, account, StringComparison.OrdinalIgnoreCase) && (!user.IsActive || user.RoleCode != AppRoles.Lead))
                throw new InvalidOperationException("At least one active Lead is needed. Add another Lead first.");
        }

        var previous = users.FirstOrDefault(item => string.Equals(item.WindowsAccount, account, StringComparison.OrdinalIgnoreCase));
        await using (var session = await OpenAsync(cancellationToken))
        {
            var table = session.Table("secureplus", "AppUser");
            await using var command = session.Command(null, previous is null
                ? $"INSERT INTO {table} (WindowsAccount, Upn, DisplayName, RoleCode, IsActive, CreatedBy, CreatedAtUtc) VALUES (@account, @upn, @name, @role, @active, @by, @now);"
                : $"UPDATE {table} SET Upn = @upn, DisplayName = @name, RoleCode = @role, IsActive = @active, ModifiedBy = @by, ModifiedAtUtc = @now WHERE WindowsAccount = @account;");
            DatabaseConnectionFactory.Parameter(command, "@account", previous?.WindowsAccount ?? account);
            DatabaseConnectionFactory.Parameter(command, "@upn", Trim(user.Upn, 320));
            DatabaseConnectionFactory.Parameter(command, "@name", Trim(user.DisplayName, 200));
            DatabaseConnectionFactory.Parameter(command, "@role", user.RoleCode);
            DatabaseConnectionFactory.Parameter(command, "@active", session.Bool(user.IsActive));
            DatabaseConnectionFactory.Parameter(command, "@by", CurrentAccount);
            DatabaseConnectionFactory.Parameter(command, "@now", session.Date(timeProvider.GetUtcNow()));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await audit.RecordAsync(AuditEventTypes.UserRoleChanged, AuditOutcome.Success,
            previous is null ? $"User {account} added as {AppRoles.DisplayName(user.RoleCode)}." : $"User {account} updated: {AppRoles.DisplayName(user.RoleCode)}{(user.IsActive ? string.Empty : ", inactive")}.",
            new Dictionary<string, string?>
            {
                ["User"] = account, ["Role"] = user.RoleCode, ["Active"] = user.IsActive.ToString(), ["PreviousRole"] = previous?.RoleCode,
                ["PreviousActive"] = previous?.IsActive.ToString()
            }, cancellationToken);
    }

    public async Task<DatabaseAccessResult> ApplyDatabaseAccessAsync(AppUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var script = DatabaseAccessScript(user);
        var settings = database.Settings;
        if (!settings.IsServerProvider) return new DatabaseAccessResult(false, "Database roles apply to SQL Server only.", script);
        try
        {
            await using (var master = await connections.CreateSqlServerAsync(settings, master: true, cancellationToken))
            {
                await master.OpenAsync(cancellationToken);
                await ExecuteAsync(master, LoginBatch(user), cancellationToken);
            }

            await using (var connection = await connections.CreateSqlServerAsync(settings, master: false, cancellationToken))
            {
                await connection.OpenAsync(cancellationToken);
                await ExecuteAsync(connection, DatabaseBatch(user), cancellationToken);
            }

            await audit.RecordAsync(AuditEventTypes.UserRoleChanged, AuditOutcome.Success, $"Database access for {user.WindowsAccount} set to {AppRoles.DatabaseRole(user.RoleCode)}.",
                new Dictionary<string, string?> { ["User"] = user.WindowsAccount, ["Role"] = user.RoleCode, ["Active"] = user.IsActive.ToString() }, cancellationToken);
            return new DatabaseAccessResult(true, user.IsActive
                ? $"{user.WindowsAccount} can now connect as {AppRoles.DisplayName(user.RoleCode)}."
                : $"{user.WindowsAccount} was removed from the Secure Plus database roles.", script);
        }
        catch (SqlException exception)
        {
            logger.LogWarning("Database access for {User} could not be applied: {Number} {Message}", user.WindowsAccount, exception.Number, exception.Message);
            var reason = exception.Number is 15247 or 15151 or 229 or 230 or 262 or 300 or 916 or 15401 or 15404
                ? "Your SQL Server login cannot create logins or change role membership (it needs the securityadmin server role)."
                : $"SQL Server reported: {exception.Message}";
            return new DatabaseAccessResult(false, $"{reason} Copy the script and ask your SQL Server administrator to run it.", script);
        }
    }

    public string DatabaseAccessScript(AppUser user)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"-- Secure Plus: database access for {user.WindowsAccount} ({AppRoles.DisplayName(user.RoleCode)}{(user.IsActive ? string.Empty : ", removed")})");
        builder.AppendLine("-- Run as a member of the securityadmin server role (and db_owner of the Secure Plus database).");
        builder.AppendLine("USE [master];");
        builder.AppendLine(LoginBatch(user));
        builder.AppendLine("GO");
        builder.AppendLine(CultureInfo.InvariantCulture, $"USE [{database.Settings.DatabaseName.Replace("]", "]]", StringComparison.Ordinal)}];");
        builder.AppendLine(DatabaseBatch(user));
        builder.AppendLine("GO");
        return builder.ToString();
    }

    private static string LoginBatch(AppUser user)
    {
        var (literal, name) = Names(user);
        return user.IsActive ? $"IF SUSER_ID(N'{literal}') IS NULL CREATE LOGIN [{name}] FROM WINDOWS;" : "-- The login is kept; only database role membership is removed.";
    }

    private static string DatabaseBatch(AppUser user)
    {
        var (literal, name) = Names(user);
        var builder = new StringBuilder();
        if (user.IsActive) builder.AppendLine(CultureInfo.InvariantCulture, $"IF DATABASE_PRINCIPAL_ID(N'{literal}') IS NULL CREATE USER [{name}] FOR LOGIN [{name}];");
        foreach (var role in AppRoles.All.Select(AppRoles.DatabaseRole))
        {
            if (user.IsActive && role == AppRoles.DatabaseRole(user.RoleCode)) continue;
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"IF DATABASE_PRINCIPAL_ID(N'{literal}') IS NOT NULL AND IS_ROLEMEMBER(N'{role}', N'{literal}') = 1 ALTER ROLE [{role}] DROP MEMBER [{name}];");
        }

        if (user.IsActive) builder.AppendLine(CultureInfo.InvariantCulture, $"ALTER ROLE [{AppRoles.DatabaseRole(user.RoleCode)}] ADD MEMBER [{name}];");
        return builder.ToString().TrimEnd();
    }

    /// <summary>The account is validated by <see cref="AppUser.IsValidAccount"/>, so it holds no quote or bracket.</summary>
    private static (string Literal, string Name) Names(AppUser user)
    {
        var account = AppUser.NormalizeAccount(user.WindowsAccount);
        if (!AppUser.IsValidAccount(account)) throw new ArgumentException("The Windows account must be DOMAIN\\name.", nameof(user));
        return (account, account);
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        if (sql.TrimStart().StartsWith("--", StringComparison.Ordinal) && !sql.Contains('\n', StringComparison.Ordinal)) return;
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? Trim(string? value, int length) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length > length ? value.Trim()[..length] : value.Trim();

    private Task<SqlSession> OpenAsync(CancellationToken cancellationToken) =>
        SqlSession.OpenAsync(database, connections, message => new InvalidOperationException(message), cancellationToken);
}

/// <summary>The designated scheduling PC in secureplus.SchedulerHost (one row, HostKey = 'Primary').</summary>
public sealed class SqlSchedulerHostService(
    IDatabaseStatusService database,
    DatabaseConnectionFactory connections,
    AuditContext context,
    Application.Settings.ApplicationSettings application,
    TimeProvider timeProvider,
    string? computerName = null) : ISchedulerHostService
{
    private const string Key = "Primary";

    public string ThisComputer { get; } = computerName ?? Environment.MachineName;

    private bool IsAvailable => database.Settings.Provider != DatabaseProvider.None && database.LatestStatus?.State == DatabaseOverallState.Ready;

    public async Task<SchedulerHostStatus?> GetAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return null;
        await using var session = await OpenAsync(cancellationToken);
        await using var command = session.Command(null,
            $"SELECT ComputerName, DesignatedBy, DesignatedAtUtc, HeartbeatUtc, LastRunUtc, LastStatus, LastMessage, AppVersion FROM {session.Table("secureplus", "SchedulerHost")} WHERE HostKey = @key;");
        DatabaseConnectionFactory.Parameter(command, "@key", Key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new SchedulerHostStatus(null, null, null, null, null, null, null, null);
        string? Text(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
        DateTimeOffset? Date(int index) => reader.IsDBNull(index) ? null : SqlSession.ReadDate(reader.GetValue(index));
        return new SchedulerHostStatus(Text(0), Text(1), Date(2), Date(3), Date(4), Text(5), Text(6), Text(7));
    }

    public async Task DesignateAsync(string? computerName, CancellationToken cancellationToken = default)
    {
        var name = string.IsNullOrWhiteSpace(computerName) ? null : computerName.Trim();
        if (name is { Length: > 128 }) throw new ArgumentException("The computer name is too long.", nameof(computerName));
        await using var session = await OpenAsync(cancellationToken);
        await EnsureRowAsync(session, cancellationToken);
        await using var command = session.Command(null,
            $"UPDATE {session.Table("secureplus", "SchedulerHost")} SET ComputerName = @name, DesignatedBy = @by, DesignatedAtUtc = @now, HeartbeatUtc = NULL WHERE HostKey = @key;");
        DatabaseConnectionFactory.Parameter(command, "@name", name);
        DatabaseConnectionFactory.Parameter(command, "@by", context.Actor.Length > 256 ? context.Actor[..256] : context.Actor);
        DatabaseConnectionFactory.Parameter(command, "@now", session.Date(timeProvider.GetUtcNow()));
        DatabaseConnectionFactory.Parameter(command, "@key", Key);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> MayRunSchedulesAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetAsync(cancellationToken);
        return status is null || !status.IsDesignated || string.Equals(status.ComputerName, ThisComputer, StringComparison.OrdinalIgnoreCase);
    }

    public async Task HeartbeatAsync(string? status, string? message, bool ranSchedules, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return;
        await using var session = await OpenAsync(cancellationToken);
        await using var command = session.Command(null,
            $"UPDATE {session.Table("secureplus", "SchedulerHost")} SET HeartbeatUtc = @now, AppVersion = @version" +
            (ranSchedules ? ", LastRunUtc = @now, LastStatus = @status, LastMessage = @message" : string.Empty) +
            " WHERE HostKey = @key AND ComputerName = @computer;");
        var now = timeProvider.GetUtcNow();
        DatabaseConnectionFactory.Parameter(command, "@now", session.Date(now));
        DatabaseConnectionFactory.Parameter(command, "@version", application.ApplicationVersion);
        DatabaseConnectionFactory.Parameter(command, "@status", status is { Length: > 40 } ? status[..40] : status);
        DatabaseConnectionFactory.Parameter(command, "@message", message is { Length: > 1000 } ? message[..1000] : message);
        DatabaseConnectionFactory.Parameter(command, "@key", Key);
        DatabaseConnectionFactory.Parameter(command, "@computer", ThisComputer);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureRowAsync(SqlSession session, CancellationToken cancellationToken)
    {
        var table = session.Table("secureplus", "SchedulerHost");
        await using var command = session.Command(null, session.Sqlite
            ? $"INSERT OR IGNORE INTO {table} (HostKey) VALUES (@key);"
            : $"IF NOT EXISTS (SELECT 1 FROM {table} WHERE HostKey = @key) INSERT INTO {table} (HostKey) VALUES (@key);");
        DatabaseConnectionFactory.Parameter(command, "@key", Key);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private Task<SqlSession> OpenAsync(CancellationToken cancellationToken) =>
        SqlSession.OpenAsync(database, connections, message => new InvalidOperationException(message), cancellationToken);
}
