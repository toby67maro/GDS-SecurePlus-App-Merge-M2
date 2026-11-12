using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace EngineeringWorkspace.Infrastructure.Database;

/// <summary>
/// Applies pending embedded migrations in one transaction and records each in secureplus.SchemaVersion. On SQL
/// Server an exclusive application lock prevents concurrent initialization. Any failure rolls everything back.
/// Scripts only create missing objects; they never drop, alter or overwrite existing ones.
/// </summary>
public static class SchemaMigrator
{
    private const string LockResource = "SecurePlus.SchemaMigration";

    public static async Task ApplyAsync(
        DbConnection connection,
        int commandTimeoutSeconds,
        IReadOnlyList<SchemaMigration> migrations,
        string appliedBy,
        string applicationVersion,
        TimeProvider timeProvider,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        bool reapplyAll = false,
        IReadOnlyList<string>? additionalStatements = null)
    {
        var sqlite = connection is SqliteConnection;
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            if (!sqlite)
            {
                await using var lockCommand = Command(connection, transaction, commandTimeoutSeconds,
                    "DECLARE @result int; EXEC @result = sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = @timeout; SELECT @result;");
                DatabaseConnectionFactory.Parameter(lockCommand, "@resource", LockResource);
                DatabaseConnectionFactory.Parameter(lockCommand, "@timeout", commandTimeoutSeconds * 1000);
                if (Convert.ToInt32(await lockCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) < 0)
                    throw new TimeoutException("Another session holds the schema migration lock.");
            }

            await using (var bootstrap = Command(connection, transaction, commandTimeoutSeconds, sqlite
                             ? """
                               CREATE TABLE IF NOT EXISTS "secureplus.SchemaVersion" ("Version" INTEGER NOT NULL PRIMARY KEY, "Description" TEXT NOT NULL, "Checksum" TEXT NOT NULL,
                                 "AppliedAtUtc" TEXT NOT NULL, "AppliedBy" TEXT NOT NULL, "ApplicationVersion" TEXT NOT NULL);
                               """
                             : """
                               IF SCHEMA_ID(N'secureplus') IS NULL EXEC(N'CREATE SCHEMA [secureplus] AUTHORIZATION [dbo]');
                               IF OBJECT_ID(N'[secureplus].[SchemaVersion]', N'U') IS NULL
                               CREATE TABLE [secureplus].[SchemaVersion] ([Version] INT NOT NULL CONSTRAINT [PK_SchemaVersion] PRIMARY KEY, [Description] NVARCHAR(200) NOT NULL,
                                 [Checksum] CHAR(64) NOT NULL, [AppliedAtUtc] DATETIME2 NOT NULL, [AppliedBy] NVARCHAR(256) NOT NULL, [ApplicationVersion] NVARCHAR(50) NOT NULL);
                               """))
            {
                await bootstrap.ExecuteNonQueryAsync(cancellationToken);
            }

            var applied = await AppliedVersionsAsync(connection, transaction, cancellationToken);
            var latest = migrations.Max(migration => migration.Version);
            if (applied.Count > 0 && applied.Keys.Max() > latest)
                throw new InvalidOperationException("Database schema is newer than this application supports.");
            foreach (var migration in migrations.Where(migration => reapplyAll || !applied.ContainsKey(migration.Version)).OrderBy(migration => migration.Version))
            {
                var batches = migration.Batches;
                for (var index = 0; index < batches.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (index % 25 == 0) progress?.Report($"Applying V{migration.Version:0000} {migration.Description}: batch {index + 1} of {batches.Count}...");
                    if (sqlite && await SqliteBatchAlreadyAppliedAsync(connection, transaction, batches[index], cancellationToken)) continue;
                    await using var command = Command(connection, transaction, commandTimeoutSeconds, batches[index]);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                if (applied.ContainsKey(migration.Version)) continue;
                await using var record = Command(connection, transaction, commandTimeoutSeconds, sqlite
                    ? "INSERT INTO \"secureplus.SchemaVersion\" (\"Version\", \"Description\", \"Checksum\", \"AppliedAtUtc\", \"AppliedBy\", \"ApplicationVersion\") VALUES (@version, @description, @checksum, @applied, @by, @app);"
                    : "INSERT INTO [secureplus].[SchemaVersion] ([Version], [Description], [Checksum], [AppliedAtUtc], [AppliedBy], [ApplicationVersion]) VALUES (@version, @description, @checksum, @applied, @by, @app);");
                var now = timeProvider.GetUtcNow();
                DatabaseConnectionFactory.Parameter(record, "@version", migration.Version);
                DatabaseConnectionFactory.Parameter(record, "@description", migration.Description);
                DatabaseConnectionFactory.Parameter(record, "@checksum", migration.Checksum);
                DatabaseConnectionFactory.Parameter(record, "@applied", sqlite ? now.ToString("O", CultureInfo.InvariantCulture) : now.UtcDateTime);
                DatabaseConnectionFactory.Parameter(record, "@by", appliedBy);
                DatabaseConnectionFactory.Parameter(record, "@app", applicationVersion);
                await record.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var statement in additionalStatements ?? [])
            {
                progress?.Report(statement);
                await using var command = Command(connection, transaction, commandTimeoutSeconds, statement);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public static async Task<Dictionary<int, string>> AppliedVersionsAsync(DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = connection is SqliteConnection
            ? "SELECT \"Version\", \"Checksum\" FROM \"secureplus.SchemaVersion\";"
            : "SELECT [Version], [Checksum] FROM [secureplus].[SchemaVersion];";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var versions = new Dictionary<int, string>();
        while (await reader.ReadAsync(cancellationToken)) versions[Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)] = reader.GetString(1).Trim();
        return versions;
    }

    private static DbCommand Command(DbConnection connection, DbTransaction transaction, int commandTimeoutSeconds, string text)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = text;
        return command;
    }

    private static readonly System.Text.RegularExpressions.Regex SqliteAlterColumn = new(
        "^ALTER TABLE \"(?<table>[^\"]+)\" (?<action>ADD|DROP) COLUMN \"(?<column>[^\"]+)\"", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// SQLite has no "ADD COLUMN IF NOT EXISTS" or "DROP COLUMN IF EXISTS": an ALTER TABLE ... ADD COLUMN batch is skipped when the column
    /// exists and a DROP COLUMN batch when it does not, so re-applying (for example during Repair) is safe.
    /// </summary>
    private static async Task<bool> SqliteBatchAlreadyAppliedAsync(DbConnection connection, DbTransaction transaction, string batch, CancellationToken cancellationToken)
    {
        var statement = string.Join('\n', batch.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal))).Trim();
        var match = SqliteAlterColumn.Match(statement);
        if (!match.Success) return false;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info(@table) WHERE name = @column;";
        DatabaseConnectionFactory.Parameter(command, "@table", match.Groups["table"].Value);
        DatabaseConnectionFactory.Parameter(command, "@column", match.Groups["column"].Value);
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) > 0;
        return match.Groups["action"].Value == "ADD" ? exists : !exists;
    }
}
