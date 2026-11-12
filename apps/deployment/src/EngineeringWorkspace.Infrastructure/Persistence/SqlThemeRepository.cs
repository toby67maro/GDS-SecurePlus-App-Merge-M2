using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Themes;
using EngineeringWorkspace.Infrastructure.Database;

namespace EngineeringWorkspace.Infrastructure.Persistence;

/// <summary>Themes in secureplus.Theme. System themes cannot be deleted.</summary>
public sealed class SqlThemeRepository(IDatabaseStatusService database, DatabaseConnectionFactory connections) : IThemeRepository
{
    public bool IsAvailable => database.Settings.Provider != DatabaseProvider.None && database.LatestStatus?.State == DatabaseOverallState.Ready;

    public async Task<IReadOnlyList<ThemeRecord>> ListThemesAsync(CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        return await ReadAsync(session, null, cancellationToken);
    }

    public async Task<ThemeRecord?> GetThemeAsync(string themeKey, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        return (await ReadAsync(session, themeKey, cancellationToken)).FirstOrDefault();
    }

    public async Task SaveThemeAsync(ThemeRecord theme, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        var table = session.Table("secureplus", "Theme");
        await using var exists = session.Command(null, $"SELECT COUNT(*) FROM {table} WHERE ThemeKey = @key;");
        DatabaseConnectionFactory.Parameter(exists, "@key", theme.ThemeKey);
        var found = Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) > 0;
        await using var command = session.Command(null, found
            ? $"UPDATE {table} SET Name = @name, Description = @description, PropertiesJson = @json, ModifiedBy = @by, ModifiedAtUtc = @at WHERE ThemeKey = @key;"
            : $"INSERT INTO {table} (ThemeKey, Name, Description, PropertiesJson, IsSystem, CreatedBy, CreatedAtUtc) VALUES (@key, @name, @description, @json, @system, @by, @at);");
        DatabaseConnectionFactory.Parameter(command, "@key", theme.ThemeKey);
        DatabaseConnectionFactory.Parameter(command, "@name", theme.Name.Length > 200 ? theme.Name[..200] : theme.Name);
        DatabaseConnectionFactory.Parameter(command, "@description", theme.Description);
        DatabaseConnectionFactory.Parameter(command, "@json", theme.PropertiesJson);
        DatabaseConnectionFactory.Parameter(command, "@system", session.Bool(theme.IsSystem));
        DatabaseConnectionFactory.Parameter(command, "@by", found ? theme.ModifiedBy ?? theme.CreatedBy : theme.CreatedBy);
        DatabaseConnectionFactory.Parameter(command, "@at", session.Date(found ? theme.ModifiedAtUtc ?? DateTimeOffset.UtcNow : theme.CreatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteThemeAsync(string themeKey, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        await using var command = session.Command(null, $"DELETE FROM {session.Table("secureplus", "Theme")} WHERE ThemeKey = @key AND IsSystem = 0;");
        DatabaseConnectionFactory.Parameter(command, "@key", themeKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<ThemeRecord>> ReadAsync(SqlSession session, string? key, CancellationToken cancellationToken)
    {
        await using var command = session.Command(null,
            $"SELECT ThemeKey, Name, Description, PropertiesJson, IsSystem, CreatedBy, CreatedAtUtc, ModifiedBy, ModifiedAtUtc FROM {session.Table("secureplus", "Theme")}" +
            (key is null ? " ORDER BY IsSystem DESC, Name;" : " WHERE ThemeKey = @key;"));
        if (key is not null) DatabaseConnectionFactory.Parameter(command, "@key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var themes = new List<ThemeRecord>();
        while (await reader.ReadAsync(cancellationToken))
            themes.Add(new ThemeRecord(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
                Convert.ToBoolean(reader.GetValue(4), System.Globalization.CultureInfo.InvariantCulture), reader.GetString(5), SqlSession.ReadDate(reader.GetValue(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : SqlSession.ReadDate(reader.GetValue(8))));
        return themes;
    }

    private Task<SqlSession> OpenAsync(CancellationToken cancellationToken) =>
        SqlSession.OpenAsync(database, connections, message => new InvalidOperationException(message), cancellationToken);
}
