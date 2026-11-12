using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Infrastructure.Database;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Infrastructure.Persistence;

/// <summary>Records this computer and technician in secureplus.ClientInstall and sec.AppUser at start-up.</summary>
public sealed class ClientRegistration(IDatabaseStatusService database, DatabaseConnectionFactory connections, IOrganizationKeyService keys, ILogger<ClientRegistration> logger)
{
    public async Task RegisterAsync(string actor, string appVersion, CancellationToken cancellationToken = default)
    {
        if (database.Settings.Provider == DatabaseProvider.None || database.LatestStatus?.State != DatabaseOverallState.Ready) return;
        try
        {
            var hasKey = await keys.GetStateAsync(cancellationToken) == OrganizationKeyState.Ready;
            await using var session = await SqlSession.OpenAsync(database, connections, message => new InvalidOperationException(message), cancellationToken);
            var now = session.Date(DateTimeOffset.UtcNow);
            var installs = session.Table("secureplus", "ClientInstall");
            await using (var update = session.Command(null,
                             $"UPDATE {installs} SET AppVersion = @version, LastSeenUtc = @now, HasOrganizationKey = @key WHERE ComputerName = @computer AND UserName = @user;"))
            {
                Parameters(update, ("@version", appVersion), ("@now", now), ("@key", session.Bool(hasKey)), ("@computer", Environment.MachineName), ("@user", Trim(actor, 256)));
                if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    await using var insert = session.Command(null,
                        $"INSERT INTO {installs} (ComputerName, UserName, AppVersion, FirstSeenUtc, LastSeenUtc, HasOrganizationKey) VALUES (@computer, @user, @version, @now, @now, @key);");
                    Parameters(insert, ("@computer", Environment.MachineName), ("@user", Trim(actor, 256)), ("@version", appVersion), ("@now", now), ("@key", session.Bool(hasKey)));
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            var users = session.Table("sec", "AppUser");
            await using var exists = session.Command(null, $"SELECT COUNT(*) FROM {users} WHERE Upn = @upn;");
            Parameters(exists, ("@upn", Trim(actor, 320)));
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 0)
            {
                await using var insert = session.Command(null, $"INSERT INTO {users} (Upn, DisplayName, IsGdsStaff, IsActive) VALUES (@upn, @name, @staff, @active);");
                Parameters(insert, ("@upn", Trim(actor, 320)), ("@name", Trim(Environment.UserName, 200)), ("@staff", session.Bool(true)), ("@active", session.Bool(true)));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            // Every technician gets the Engineer role until an administrator assigns another one (sec.UserRole).
            await using (var role = session.Command(null,
                             $"INSERT INTO {session.Table("sec", "UserRole")} (AppUserId, AppRoleId) SELECT u.AppUserId, r.AppRoleId FROM {users} AS u, {session.Table("sec", "AppRole")} AS r " +
                             $"WHERE u.Upn = @upn AND r.Code = 'Engineer' AND NOT EXISTS (SELECT 1 FROM {session.Table("sec", "UserRole")} AS x WHERE x.AppUserId = u.AppUserId);"))
            {
                Parameters(role, ("@upn", Trim(actor, 320)));
                await role.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or InvalidOperationException)
        {
            logger.LogWarning(exception, "This computer could not be recorded in the database");
        }
    }

    private static string Trim(string value, int length) => value.Length <= length ? value : value[..length];

    private static void Parameters(System.Data.Common.DbCommand command, params (string Name, object? Value)[] values)
    {
        foreach (var (name, value) in values) DatabaseConnectionFactory.Parameter(command, name, value);
    }
}
