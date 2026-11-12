using System.Data;
using System.Globalization;
using EngineeringWorkspace.Application.Database;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Infrastructure.Database;

/// <summary>
/// One-time move of a SQLite database (3.1 and earlier) into SQL Server (BLUEPRINT §10). The SQLite file is first copied to a
/// timestamped backup and is never modified. Every table that has rows in SQLite replaces the same table in SQL Server (the target
/// must already have the current schema; tables empty in SQLite keep the server's rows), in one transaction with constraints off
/// and identities kept; afterwards row counts are compared per table and the transaction is committed only when every table
/// matches. Values are converted to the SQL Server column types (SQLite stores dates as ISO text and booleans as 0/1).
/// </summary>
public sealed class SqliteToSqlServerMover(DatabaseConnectionFactory connections, TimeProvider timeProvider, ILogger<SqliteToSqlServerMover> logger) : IDatabaseMoveService
{
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "secureplus.SchemaVersion" };

    private sealed record TargetColumn(string Name, string Type, bool IsIdentity);

    public async Task<DatabaseMoveResult> MoveAsync(DatabaseSettings source, DatabaseSettings target, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (source.Provider != DatabaseProvider.SQLite) throw new ArgumentException("The source must be a SQLite database.", nameof(source));
        if (!target.IsServerProvider) throw new ArgumentException("The target must be SQL Server or Azure SQL.", nameof(target));

        var path = connections.ResolveSqlitePath(source);
        if (!File.Exists(path)) throw new FileNotFoundException("The SQLite database file was not found.", path);
        SqliteConnection.ClearAllPools();
        var backup = $"{path}.before-move-{timeProvider.GetUtcNow():yyyyMMddHHmmss}.bak";
        File.Copy(path, backup, overwrite: false);
        progress?.Report($"Backed up the SQLite file to {backup}.");

        await using var sqlite = connections.CreateSqlite(source, allowCreate: false);
        await sqlite.OpenAsync(cancellationToken);
        var sourceTables = new List<string>();
        await using (var list = sqlite.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE '%.%' ORDER BY name;";
            await using var reader = await list.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) sourceTables.Add(reader.GetString(0));
        }

        await using var server = await connections.CreateSqlServerAsync(target, master: false, cancellationToken);
        await server.OpenAsync(cancellationToken);
        var columns = await TargetColumnsAsync(server, cancellationToken);
        var results = new List<TableMoveResult>();
        await using var transaction = (SqlTransaction)await server.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var table in columns.Keys) await ExecuteAsync(server, transaction, $"ALTER TABLE {Quote(table)} NOCHECK CONSTRAINT ALL;", cancellationToken);

            foreach (var table in sourceTables)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceRows = await SqliteCountAsync(sqlite, table, cancellationToken);
                if (Skipped.Contains(table))
                {
                    results.Add(new TableMoveResult(table, sourceRows, sourceRows, "Schema history: the server keeps its own."));
                    continue;
                }

                if (!columns.TryGetValue(table, out var targetColumns))
                {
                    results.Add(new TableMoveResult(table, sourceRows, sourceRows == 0 ? 0 : -1, "Not in the SQL Server schema."));
                    continue;
                }

                if (sourceRows == 0)
                {
                    results.Add(new TableMoveResult(table, 0, 0, "Empty in SQLite: the server's rows are kept."));
                    continue;
                }

                var targetRows = await ServerCountAsync(server, transaction, table, cancellationToken);
                if (targetRows > 0)
                {
                    if (string.Equals(table, "secureplus.AuditLedger", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new TableMoveResult(table, sourceRows, sourceRows, "Append-only ledger already has rows on the server: SQLite entries are not added."));
                        continue;
                    }

                    await ExecuteAsync(server, transaction, $"DELETE FROM {Quote(table)};", cancellationToken);
                }

                progress?.Report($"Copying {table} ({sourceRows:N0} rows)...");
                var data = await ReadAsync(sqlite, table, targetColumns, cancellationToken);
                using (var bulk = new SqlBulkCopy(server, SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.KeepNulls, transaction)
                       { DestinationTableName = Quote(table), BulkCopyTimeout = 0, BatchSize = 5000 })
                {
                    foreach (DataColumn column in data.Columns) bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                    await bulk.WriteToServerAsync(data, cancellationToken);
                }

                var copied = await ServerCountAsync(server, transaction, table, cancellationToken);
                results.Add(new TableMoveResult(table, sourceRows, copied, targetRows > 0 ? $"Replaced {targetRows:N0} server row(s)." : string.Empty));
            }

            foreach (var table in columns.Keys) await ExecuteAsync(server, transaction, $"ALTER TABLE {Quote(table)} CHECK CONSTRAINT ALL;", cancellationToken);

            var mismatches = results.Where(result => !result.Matches).ToArray();
            if (mismatches.Length > 0)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                var message = $"Nothing was moved: {mismatches.Length} table(s) did not match ({string.Join(", ", mismatches.Take(5).Select(result => $"{result.Table} {result.SourceRows}/{result.TargetRows}"))}).";
                logger.LogWarning("SQLite move rolled back: {Message}", message);
                return new DatabaseMoveResult(false, backup, results, message);
            }

            await transaction.CommitAsync(cancellationToken);
            var summary = $"Moved {results.Sum(result => result.SourceRows):N0} rows in {results.Count(result => result.SourceRows > 0)} tables; every table's row count matches. The SQLite file was not changed.";
            logger.LogInformation("SQLite database moved to SQL Server: {Summary}", summary);
            return new DatabaseMoveResult(true, backup, results, summary);
        }
        catch
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<Dictionary<string, IReadOnlyList<TargetColumn>>> TargetColumnsAsync(SqlConnection server, CancellationToken cancellationToken)
    {
        await using var command = server.CreateCommand();
        // Writable columns of user tables, without history tables, computed, generated-always (period and ledger) and rowversion columns.
        command.CommandText = """
            SELECT s.name, t.name, c.name, ty.name, c.is_identity
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE t.is_ms_shipped = 0 AND t.temporal_type <> 1 AND t.ledger_type <> 1
              AND c.is_computed = 0 AND c.generated_always_type = 0 AND ty.name NOT IN ('timestamp', 'rowversion')
            ORDER BY s.name, t.name, c.column_id;
            """;
        var tables = new Dictionary<string, List<TargetColumn>>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = $"{reader.GetString(0)}.{reader.GetString(1)}";
            if (!tables.TryGetValue(key, out var list)) tables[key] = list = [];
            list.Add(new TargetColumn(reader.GetString(2), reader.GetString(3), reader.GetBoolean(4)));
        }

        return tables.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<TargetColumn>)pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<DataTable> ReadAsync(SqliteConnection sqlite, string table, IReadOnlyList<TargetColumn> targetColumns, CancellationToken cancellationToken)
    {
        await using var command = sqlite.CreateCommand();
        command.CommandText = $"SELECT * FROM \"{table}\";";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var byName = targetColumns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        var mapped = Enumerable.Range(0, reader.FieldCount)
            .Select(index => (Index: index, Target: byName.GetValueOrDefault(reader.GetName(index))))
            .Where(pair => pair.Target is not null)
            .ToArray();
        var data = new DataTable(table);
        foreach (var (_, column) in mapped) data.Columns.Add(column!.Name, ClrType(column.Type));
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = data.NewRow();
            foreach (var (index, column) in mapped) row[column!.Name] = reader.IsDBNull(index) ? DBNull.Value : ConvertValue(reader.GetValue(index), column.Type);
            data.Rows.Add(row);
        }

        return data;
    }

    private static Type ClrType(string sqlType) => sqlType switch
    {
        "bit" => typeof(bool),
        "tinyint" => typeof(byte),
        "smallint" => typeof(short),
        "int" => typeof(int),
        "bigint" => typeof(long),
        "decimal" or "numeric" or "money" or "smallmoney" => typeof(decimal),
        "float" => typeof(double),
        "real" => typeof(float),
        "date" or "datetime" or "datetime2" or "smalldatetime" => typeof(DateTime),
        "datetimeoffset" => typeof(DateTimeOffset),
        "time" => typeof(TimeSpan),
        "uniqueidentifier" => typeof(Guid),
        "binary" or "varbinary" or "image" => typeof(byte[]),
        _ => typeof(string)
    };

    /// <summary>Converts a SQLite value (INTEGER, REAL, TEXT or BLOB) to the target column's type.</summary>
    internal static object ConvertValue(object value, string sqlType)
    {
        var culture = CultureInfo.InvariantCulture;
        return sqlType switch
        {
            "bit" => value is string text ? text is "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase) : Convert.ToInt64(value, culture) != 0,
            "tinyint" => Convert.ToByte(value, culture),
            "smallint" => Convert.ToInt16(value, culture),
            "int" => Convert.ToInt32(value, culture),
            "bigint" => Convert.ToInt64(value, culture),
            "decimal" or "numeric" or "money" or "smallmoney" => Convert.ToDecimal(value, culture),
            "float" => Convert.ToDouble(value, culture),
            "real" => Convert.ToSingle(value, culture),
            "date" or "datetime" or "datetime2" or "smalldatetime" => value is string text
                ? DateTimeOffset.Parse(text, culture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).UtcDateTime
                : Convert.ToDateTime(value, culture),
            "datetimeoffset" => value is string text ? DateTimeOffset.Parse(text, culture, DateTimeStyles.AssumeUniversal) : new DateTimeOffset(Convert.ToDateTime(value, culture)),
            "time" => value is string text ? TimeSpan.Parse(text, culture) : TimeSpan.FromTicks(Convert.ToInt64(value, culture)),
            "uniqueidentifier" => value is byte[] bytes ? new Guid(bytes) : Guid.Parse(Convert.ToString(value, culture)!),
            "binary" or "varbinary" or "image" => value is byte[] blob ? blob : System.Text.Encoding.UTF8.GetBytes(Convert.ToString(value, culture)!),
            _ => value is byte[] raw ? Convert.ToBase64String(raw) : Convert.ToString(value, culture)!
        };
    }

    private static async Task<long> SqliteCountAsync(SqliteConnection sqlite, string table, CancellationToken cancellationToken)
    {
        await using var command = sqlite.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\";";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<long> ServerCountAsync(SqlConnection server, SqlTransaction transaction, string table, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand($"SELECT COUNT_BIG(*) FROM {Quote(table)};", server, transaction) { CommandTimeout = 0 };
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(SqlConnection server, SqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, server, transaction) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Quote(string table)
    {
        var dot = table.IndexOf('.', StringComparison.Ordinal);
        return $"[{table[..dot].Replace("]", "]]", StringComparison.Ordinal)}].[{table[(dot + 1)..].Replace("]", "]]", StringComparison.Ordinal)}]";
    }
}
