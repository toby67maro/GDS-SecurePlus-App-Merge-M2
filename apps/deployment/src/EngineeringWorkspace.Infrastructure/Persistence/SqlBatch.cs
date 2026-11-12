using System.Data.Common;
using System.Globalization;
using System.Text;
using EngineeringWorkspace.Infrastructure.Database;

namespace EngineeringWorkspace.Infrastructure.Persistence;

/// <summary>Parameterized batch helpers shared by the SQL Server and SQLite stores.</summary>
internal static class SqlBatch
{
    // SQL Server allows 2,100 parameters per command; SQLite 32,766. Stay well below both.
    public const int MaxParameters = 900;

    public static string? Text(string? value, int length = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= length ? trimmed : trimmed[..length];
    }

    public static async Task ExecuteAsync(SqlSession session, DbTransaction? transaction, string sql, Action<DbCommand>? parameters, CancellationToken cancellationToken)
    {
        await using var command = session.Command(transaction, sql);
        parameters?.Invoke(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<int> CountAsync(SqlSession session, DbTransaction? transaction, string sql, Action<DbCommand>? parameters, CancellationToken cancellationToken)
    {
        await using var command = session.Command(transaction, sql);
        parameters?.Invoke(command);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public static async Task<object?> ScalarAsync(SqlSession session, DbTransaction? transaction, string sql, Action<DbCommand>? parameters, CancellationToken cancellationToken)
    {
        await using var command = session.Command(transaction, sql);
        parameters?.Invoke(command);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DBNull ? null : value;
    }

    public static async Task<int> InsertIdAsync(SqlSession session, DbTransaction? transaction, string sql, Action<DbCommand> parameters, CancellationToken cancellationToken)
    {
        await using var command = session.Command(transaction, $"{sql} {session.IdentitySql}");
        parameters(command);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    /// <summary>Reads rows as dictionaries (column name → value, DBNull as null).</summary>
    public static async Task<List<Dictionary<string, object?>>> QueryAsync(SqlSession session, DbTransaction? transaction, string sql, Action<DbCommand>? parameters,
        CancellationToken cancellationToken)
    {
        await using var command = session.Command(transaction, sql);
        parameters?.Invoke(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++)
                row[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            rows.Add(row);
        }

        return rows;
    }

    public static async Task BulkInsertAsync(SqlSession session, DbTransaction? transaction, string table, IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows,
        Action<int, int>? onBatch, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return;
        var perBatch = Math.Max(1, MaxParameters / columns.Count);
        var prefix = $"INSERT INTO {table} ({string.Join(", ", columns.Select(Quote))}) VALUES ";
        for (var offset = 0; offset < rows.Count; offset += perBatch)
        {
            var count = Math.Min(perBatch, rows.Count - offset);
            await using var command = session.Command(transaction, string.Empty);
            var text = new StringBuilder(prefix);
            for (var row = 0; row < count; row++)
            {
                text.Append(row == 0 ? "(" : ", (");
                var values = rows[offset + row];
                for (var column = 0; column < columns.Count; column++)
                {
                    var name = $"@p{row}_{column}";
                    text.Append(column == 0 ? name : $", {name}");
                    DatabaseConnectionFactory.Parameter(command, name, values[column]);
                }

                text.Append(')');
            }

            command.CommandText = text.Append(';').ToString();
            await command.ExecuteNonQueryAsync(cancellationToken);
            onBatch?.Invoke(offset + count, rows.Count);
        }
    }

    /// <summary>Updates rows by key, several statements per command.</summary>
    public static async Task BulkUpdateAsync(SqlSession session, DbTransaction? transaction, string table, IReadOnlyList<string> keyColumns, IReadOnlyList<string> columns,
        IReadOnlyList<object?[]> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return;
        var all = columns.Concat(keyColumns).ToArray();
        var perBatch = Math.Max(1, MaxParameters / all.Length);
        for (var offset = 0; offset < rows.Count; offset += perBatch)
        {
            var count = Math.Min(perBatch, rows.Count - offset);
            await using var command = session.Command(transaction, string.Empty);
            var text = new StringBuilder();
            for (var row = 0; row < count; row++)
            {
                var values = rows[offset + row];
                text.Append($"UPDATE {table} SET ");
                text.Append(string.Join(", ", columns.Select((column, index) => $"{Quote(column)} = @u{row}_{index}")));
                text.Append(" WHERE ");
                text.Append(string.Join(" AND ", keyColumns.Select((column, index) => $"{Quote(column)} = @u{row}_{columns.Count + index}")));
                text.Append(';');
                for (var index = 0; index < all.Length; index++)
                    DatabaseConnectionFactory.Parameter(command, $"@u{row}_{index}", values[index]);
            }

            command.CommandText = text.ToString();
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>Deletes rows whose single-column key is in <paramref name="keys"/>.</summary>
    public static async Task DeleteKeysAsync(SqlSession session, DbTransaction? transaction, string table, string keyColumn, IReadOnlyList<object> keys,
        CancellationToken cancellationToken)
    {
        for (var offset = 0; offset < keys.Count; offset += MaxParameters)
        {
            var count = Math.Min(MaxParameters, keys.Count - offset);
            await using var command = session.Command(transaction, string.Empty);
            var names = new List<string>(count);
            for (var index = 0; index < count; index++)
            {
                names.Add($"@k{index}");
                DatabaseConnectionFactory.Parameter(command, $"@k{index}", keys[offset + index]);
            }

            command.CommandText = $"DELETE FROM {table} WHERE {keyColumn} IN ({string.Join(", ", names)});";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>Single-row helper that adds a parameter list: (name, value) pairs.</summary>
    /// <summary>Quotes a column name with brackets (SQL Server and SQLite both accept them), so reserved words such as RowCount work.</summary>
    public static string Quote(string column) => column.StartsWith('[') ? column : $"[{column}]";

    public static Action<DbCommand> Parameters(params (string Name, object? Value)[] values) => command =>
    {
        foreach (var (name, value) in values) DatabaseConnectionFactory.Parameter(command, name, value);
    };
}
