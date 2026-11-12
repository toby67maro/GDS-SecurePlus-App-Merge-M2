using System.Data.Common;
using System.Globalization;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace EngineeringWorkspace.Infrastructure.Persistence;

/// <summary>An open application-database connection plus the dialect differences between SQL Server and SQLite.</summary>
internal sealed class SqlSession(DbConnection connection, int commandTimeoutSeconds) : IAsyncDisposable
{
    public DbConnection Connection { get; } = connection;
    public bool Sqlite { get; } = connection is SqliteConnection;
    public int CommandTimeoutSeconds { get; set; } = commandTimeoutSeconds;

    /// <summary>Returns the identity of the row inserted by the preceding statement.</summary>
    public string IdentitySql => Sqlite ? "SELECT last_insert_rowid();" : "SELECT CAST(SCOPE_IDENTITY() AS INT);";

    public string Table(string schema, string name) => Sqlite ? $"\"{schema}.{name}\"" : $"[{schema}].[{name}]";

    public object Date(DateTimeOffset value) => Sqlite ? value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) : value.UtcDateTime;

    public object Bool(bool value) => Sqlite ? (value ? 1 : 0) : value;

    public static DateTimeOffset ReadDate(object value) => value switch
    {
        DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
        DateTimeOffset offset => offset,
        string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
        _ => DateTimeOffset.MinValue
    };

    public DbCommand Command(DbTransaction? transaction, string text)
    {
        var command = Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = text;
        return command;
    }

    /// <summary>Opens the configured application database; fails with <paramref name="unavailable"/> when it is not ready.</summary>
    public static async Task<SqlSession> OpenAsync(IDatabaseStatusService database, DatabaseConnectionFactory connections, Func<string, Exception> unavailable,
        CancellationToken cancellationToken)
    {
        var settings = database.Settings;
        if (settings.Provider == DatabaseProvider.None)
            throw unavailable("No application database is configured. Configure it in Settings > Database Configuration.");
        if (database.LatestStatus is { State: not DatabaseOverallState.Ready } status)
            throw unavailable($"The application database is not ready ({status.State}).");
        DbConnection connection = settings.Provider == DatabaseProvider.SQLite
            ? connections.CreateSqlite(settings, allowCreate: false)
            : await connections.CreateSqlServerAsync(settings, master: false, cancellationToken);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return new SqlSession(connection, settings.CommandTimeoutSeconds);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}
