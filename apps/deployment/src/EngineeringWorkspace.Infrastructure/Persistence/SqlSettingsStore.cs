using System.Data.Common;
using System.Globalization;
using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Configuration;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace EngineeringWorkspace.Infrastructure.Persistence;

/// <summary>Settings read from the database at start-up: shared values, this computer's values and the database properties.</summary>
public sealed record DatabaseSettingsSnapshot(
    IReadOnlyDictionary<string, string?> Shared,
    IReadOnlyDictionary<string, string?> Computer,
    IReadOnlyDictionary<string, string?> Properties);

/// <summary>
/// secureplus.Setting and secureplus.DatabaseProperty. Works directly from <see cref="DatabaseSettings"/> so that the
/// bootstrapper can read settings before the application host (and its database status service) exists.
/// </summary>
public sealed class SqlSettingsStore(DatabaseConnectionFactory connections, string computerName)
{
    public string ComputerName { get; } = computerName;

    /// <summary>Settings and properties, or null when the database cannot be reached or has no settings table yet (schema older than V0007).</summary>
    public async Task<DatabaseSettingsSnapshot?> TryLoadAsync(DatabaseSettings settings, CancellationToken cancellationToken)
    {
        if (settings.Provider == DatabaseProvider.None) return null;
        try
        {
            await using var connection = await OpenAsync(settings, cancellationToken);
            if (!await TableExistsAsync(connection, "Setting", cancellationToken)) return null;
            var shared = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var computer = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            await using (var command = Command(connection, $"SELECT Scope, ComputerName, SettingKey, Value FROM {Table(connection, "Setting")} WHERE Scope = @shared OR (Scope = @computer AND ComputerName = @name);"))
            {
                Parameter(command, "@shared", SettingScopes.Shared);
                Parameter(command, "@computer", SettingScopes.Computer);
                Parameter(command, "@name", ComputerName);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    (reader.GetString(0) == SettingScopes.Shared ? shared : computer)[reader.GetString(2)] = reader.IsDBNull(3) ? null : reader.GetString(3);
            }

            return new DatabaseSettingsSnapshot(shared, computer, await ReadPropertiesAsync(connection, cancellationToken));
        }
        catch (Exception exception) when (exception is DbException or DatabaseCredentialUnavailableException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>Writes settings (replacing the same scope, computer and key) in one transaction.</summary>
    public async Task WriteAsync(DatabaseSettings settings, IReadOnlyList<SettingWrite> writes, string actor, CancellationToken cancellationToken)
    {
        if (writes.Count == 0) return;
        await using var connection = await OpenAsync(settings, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var table = Table(connection, "Setting");
        foreach (var write in writes)
        {
            var computer = write.Scope == SettingScopes.Computer ? ComputerName : string.Empty;
            await using (var delete = Command(connection, $"DELETE FROM {table} WHERE Scope = @scope AND ComputerName = @computer AND SettingKey = @key;", transaction))
            {
                Parameter(delete, "@scope", write.Scope);
                Parameter(delete, "@computer", computer);
                Parameter(delete, "@key", write.Key);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            if (write.Remove) continue;
            await using var insert = Command(connection, $"INSERT INTO {table} (Scope, ComputerName, SettingKey, Value, ModifiedBy, ModifiedAtUtc) VALUES (@scope, @computer, @key, @value, @by, @at);",
                transaction);
            Parameter(insert, "@scope", write.Scope);
            Parameter(insert, "@computer", computer);
            Parameter(insert, "@key", write.Key);
            Parameter(insert, "@value", write.Value);
            Parameter(insert, "@by", actor.Length > 256 ? actor[..256] : actor);
            Parameter(insert, "@at", Date(connection, write.ModifiedAtUtc ?? DateTimeOffset.UtcNow));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Every stored setting (both scopes, all computers), for the Workspace Configuration page.</summary>
    public async Task<IReadOnlyList<StoredSetting>> ListAsync(DatabaseSettings settings, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(settings, cancellationToken);
        await using var command = Command(connection, $"SELECT Scope, ComputerName, SettingKey, Value, ModifiedBy, ModifiedAtUtc FROM {Table(connection, "Setting")} ORDER BY SettingKey;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<StoredSetting>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new StoredSetting(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4),
                SqlSession.ReadDate(reader.GetValue(5))));
        return rows;
    }

    public async Task SetPropertyAsync(DatabaseSettings settings, string key, string? value, string actor, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(settings, cancellationToken);
        var table = Table(connection, "DatabaseProperty");
        await using (var delete = Command(connection, $"DELETE FROM {table} WHERE PropertyKey = @key;"))
        {
            Parameter(delete, "@key", key);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var insert = Command(connection, $"INSERT INTO {table} (PropertyKey, Value, ModifiedBy, ModifiedAtUtc) VALUES (@key, @value, @by, @at);");
        Parameter(insert, "@key", key);
        Parameter(insert, "@value", value);
        Parameter(insert, "@by", actor.Length > 256 ? actor[..256] : actor);
        Parameter(insert, "@at", Date(connection, DateTimeOffset.UtcNow));
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, string?>> GetPropertiesAsync(DatabaseSettings settings, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(settings, cancellationToken);
        return await TableExistsAsync(connection, "DatabaseProperty", cancellationToken)
            ? await ReadPropertiesAsync(connection, cancellationToken)
            : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<Dictionary<string, string?>> ReadPropertiesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var properties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        await using var command = Command(connection, $"SELECT PropertyKey, Value FROM {Table(connection, "DatabaseProperty")};");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) properties[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        return properties;
    }

    private async Task<DbConnection> OpenAsync(DatabaseSettings settings, CancellationToken cancellationToken)
    {
        DbConnection connection = settings.Provider == DatabaseProvider.SQLite
            ? connections.CreateSqlite(settings, allowCreate: false)
            : await connections.CreateSqlServerAsync(settings, master: false, cancellationToken);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<bool> TableExistsAsync(DbConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, connection is SqliteConnection
            ? "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;"
            : "SELECT COUNT(*) FROM sys.tables AS t JOIN sys.schemas AS s ON s.schema_id = t.schema_id WHERE s.name = N'secureplus' AND t.name = @name;");
        Parameter(command, "@name", connection is SqliteConnection ? $"secureplus.{table}" : table);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0;
    }

    private static string Table(DbConnection connection, string name) => connection is SqliteConnection ? $"\"secureplus.{name}\"" : $"[secureplus].[{name}]";
    private static object Date(DbConnection connection, DateTimeOffset value) => connection is SqliteConnection ? value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) : value.UtcDateTime;

    private static DbCommand Command(DbConnection connection, string text, DbTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = text;
        command.Transaction = transaction;
        command.CommandTimeout = 30;
        return command;
    }

    private static void Parameter(DbCommand command, string name, object? value) => DatabaseConnectionFactory.Parameter(command, name, value);
}

/// <summary>Runtime access to the database settings (after start-up), for writers and the settings pages.</summary>
/// <remarks>The database status service is resolved lazily: it saves Database.ini through the configuration writer, which uses this store.</remarks>
public sealed class SharedSettingsStore(Lazy<IDatabaseStatusService> status, SqlSettingsStore store) : ISharedSettingsStore
{
    private IDatabaseStatusService Database => status.Value;

    public bool IsAvailable => Database.Settings.Provider != DatabaseProvider.None && Database.LatestStatus?.State == DatabaseOverallState.Ready;
    public string ComputerName => store.ComputerName;
    public Task WriteAsync(IReadOnlyList<SettingWrite> writes, string actor, CancellationToken cancellationToken = default) => store.WriteAsync(Database.Settings, writes, actor, cancellationToken);
    public Task<IReadOnlyList<StoredSetting>> ListAsync(CancellationToken cancellationToken = default) => store.ListAsync(Database.Settings, cancellationToken);
    public Task<IReadOnlyDictionary<string, string?>> GetPropertiesAsync(CancellationToken cancellationToken = default) => store.GetPropertiesAsync(Database.Settings, cancellationToken);
    public Task SetPropertyAsync(string key, string? value, string actor, CancellationToken cancellationToken = default) =>
        store.SetPropertyAsync(Database.Settings, key, value, actor, cancellationToken);
}

/// <summary>
/// Saves settings to the database once it is ready (shared or per computer by section). Database.ini – the connection
/// itself – and settings saved before a database exists go to the local INI writer.
/// </summary>
public sealed class RoutingConfigurationWriter(IConfigurationFileWriter files, ISharedSettingsStore settings, IRedactionService redaction, Application.Auditing.AuditContext audit)
    : IConfigurationFileWriter
{
    public async Task<OperationResult<string>> WriteSectionAsync(string fileName, string section, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default)
    {
        if (string.Equals(fileName, "Database.ini", StringComparison.OrdinalIgnoreCase) || !settings.IsAvailable)
            return await files.WriteSectionAsync(fileName, section, values, cancellationToken);
        var secretKey = values.Keys.FirstOrDefault(key => redaction.IsSecretKey(key));
        if (secretKey is not null)
            return OperationResult<string>.Failure("SecretNotAllowed", $"'{secretKey}' is secret-bearing and cannot be saved as a setting.", null, "Store the secret in Credential Management and save its reference.");
        if (values.Values.Any(value => value is not null && (value.Contains('\n') || value.Contains('\r'))))
            return OperationResult<string>.Failure("InvalidValue", "Setting values cannot contain line breaks.");
        try
        {
            await settings.WriteAsync(values.Select(pair => new SettingWrite(SettingScopes.For(section), $"{section}:{pair.Key}", pair.Value ?? string.Empty)).ToArray(), audit.Actor,
                cancellationToken);
            return OperationResult<string>.Success(SettingScopes.For(section) == SettingScopes.Computer ? $"this computer's settings ({settings.ComputerName})" : "the shared settings");
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            return OperationResult<string>.Failure("SettingWriteFailed", "The setting could not be saved to the database.", exception.Message, "Check the database connection.");
        }
    }
}
