using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ArchiTech.Api.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

[assembly: InternalsVisibleTo("ArchiTech.Tests")]

if ((args.Length != 1 && (args.Length != 2 || args[1] != "--resume-empty")) ||
    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ARCHITECH_TRANSFER_SQL")) ||
    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ARCHITECH_TRANSFER_SHA256")))
{
    Console.Error.WriteLine("Usage: set ARCHITECH_TRANSFER_SQL and ARCHITECH_TRANSFER_SHA256, then pass the path to a verified SQLite backup and optionally --resume-empty.");
    return 1;
}

try
{
    await Transfer.RunAsync(args[0], Environment.GetEnvironmentVariable("ARCHITECH_TRANSFER_SQL")!,
        Environment.GetEnvironmentVariable("ARCHITECH_TRANSFER_SHA256")!, args.Length == 2);
    return 0;
}
catch (Exception exception)
{
    var code = exception is SqlException sqlException ? $", SQL error {sqlException.Number}" :
        exception is SqliteException sqliteException ? $", SQLite error {sqliteException.SqliteErrorCode}" : "";
    var detail = exception is Transfer.TransferFailure failure ? $": {failure.Message}" : "";
    Console.Error.WriteLine($"Transfer failed ({exception.GetType().Name}{code}){detail}; no data values are included in this error.");
    return 1;
}

internal static class Transfer
{
    private const int BatchSize = 1000;

    public static async Task RunAsync(string sourcePath, string destinationString, string expectedHash, bool resumeEmpty)
    {
        var sourceFile = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourceFile)) throw new TransferFailure("The SQLite backup does not exist.");
        await using var file = File.OpenRead(sourceFile);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(file));
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new TransferFailure("The SQLite backup checksum does not match.");
        Console.WriteLine($"Source backup SHA-256: {actualHash}");
        var sourceBuilder = new SqliteConnectionStringBuilder { DataSource = sourceFile, Mode = SqliteOpenMode.ReadOnly };
        await using var source = new SqliteConnection(sourceBuilder.ConnectionString);
        await source.OpenAsync();
        await using (var check = source.CreateCommand())
        {
            check.CommandText = "PRAGMA integrity_check";
            if ((string?)await check.ExecuteScalarAsync() != "ok")
                throw new TransferFailure("SQLite integrity check failed.");
            check.CommandText = "PRAGMA foreign_key_check";
            await using var reader = await check.ExecuteReaderAsync();
            if (await reader.ReadAsync()) throw new TransferFailure("SQLite foreign key check failed.");
        }
        await ValidateSourceSequences(source);

        var destinationBuilder = new SqlConnectionStringBuilder(destinationString);
        if (string.IsNullOrWhiteSpace(destinationBuilder.InitialCatalog) ||
            new[] { "master", "model", "msdb", "tempdb" }.Contains(destinationBuilder.InitialCatalog, StringComparer.OrdinalIgnoreCase))
            throw new TransferFailure("Specify a dedicated, existing destination database.");
        destinationBuilder.Pooling = false;
        await using var destination = new SqlConnection(destinationBuilder.ConnectionString);
        await destination.OpenAsync();
        var sourceTables = await GetSourceTables(source);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(destination.ConnectionString).Options;
        await using (var db = new AppDbContext(options))
        {
            await using var check = destination.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM sys.objects WHERE is_ms_shipped = 0 AND type IN ('U','V','P','FN','IF','TF','TR','SN','SO')";
            var objectCount = Convert.ToInt32(await check.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            if (objectCount != 0)
            {
                if (!resumeEmpty) throw new TransferFailure("Destination is not empty; use --resume-empty only for a previously initialized, empty rehearsal.");
                check.CommandText = "SELECT COUNT(*) FROM sys.objects WHERE is_ms_shipped = 0 AND type IN ('V','P','FN','IF','TF','TR','SN','SO')";
                if (Convert.ToInt32(await check.ExecuteScalarAsync(), CultureInfo.InvariantCulture) != 0)
                    throw new TransferFailure("Unexpected destination objects.");
                var existing = await GetDestinationTables(destination);
                await ValidateTables(source, destination, sourceTables, existing);
                var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
                var known = db.Database.GetMigrations().ToArray();
                if (applied.Length == 0 || !applied.SequenceEqual(known.Take(applied.Length), StringComparer.Ordinal))
                    throw new TransferFailure("Destination migration history does not match this application.");
            }
            await db.Database.MigrateAsync();
        }
        Console.WriteLine("Schema migrations applied to the empty destination.");

        var tables = await GetDestinationTables(destination);
        await ValidateTables(source, destination, sourceTables, tables);
        var ordered = OrderTables(tables);

        await using var transaction = (SqlTransaction)await destination.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            foreach (var table in ordered)
            {
                await using var check = destination.CreateCommand();
                check.Transaction = transaction;
                check.CommandText = $"SELECT COUNT_BIG(*) FROM [dbo].{QuoteSql(table.Name)} WITH (TABLOCKX, HOLDLOCK)";
                if (Convert.ToInt64(await check.ExecuteScalarAsync(), CultureInfo.InvariantCulture) != 0)
                    throw new TransferFailure($"Destination table {table.Name} is not empty.");
            }
            foreach (var table in ordered)
            {
                await CopyTable(source, destination, transaction, table);
            }
            await transaction.CommitAsync();
            Console.WriteLine("All table contents reconciled; transaction committed.");
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    internal static async Task ValidateSourceSequences(SqliteConnection source)
    {
        await using var check = source.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'sqlite_sequence'";
        if (Convert.ToInt64(await check.ExecuteScalarAsync(), CultureInfo.InvariantCulture) == 0) return;
        check.CommandText = "SELECT name, seq FROM sqlite_sequence";
        var sequences = new List<(string Table, long Sequence)>();
        await using (var reader = await check.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) sequences.Add((reader.GetString(0), reader.GetInt64(1)));
        }
        foreach (var (table, sequence) in sequences)
        {
            check.CommandText = $"SELECT COALESCE(MAX({QuoteSqlite("Id")}), 0) FROM {QuoteSqlite(table)}";
            var maximum = Convert.ToInt64(await check.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            if (sequence > maximum)
                throw new TransferFailure($"SQLite identity sequence for {table} exceeds its maximum ID; manual sequence planning is required.");
        }
    }

    private static async Task ValidateTables(SqliteConnection source, SqlConnection destination, HashSet<string> sourceTables, List<Table> tables)
    {
        await using var constraints = destination.CreateCommand();
        constraints.CommandText = """
            SELECT (SELECT COUNT(*) FROM sys.foreign_keys WHERE is_disabled = 1 OR is_not_trusted = 1) +
                   (SELECT COUNT(*) FROM sys.check_constraints WHERE is_disabled = 1 OR is_not_trusted = 1)
            """;
        if (Convert.ToInt32(await constraints.ExecuteScalarAsync(), CultureInfo.InvariantCulture) != 0)
            throw new TransferFailure("Destination contains disabled or untrusted constraints.");
        if (!sourceTables.SetEquals(tables.Select(t => t.Name)))
            throw new TransferFailure("Source and destination table sets differ.");
        foreach (var table in tables)
        {
            var sourceColumns = await GetSourceColumns(source, table.Name);
            if (!sourceColumns.SetEquals(table.Columns.Select(c => c.Name)))
                throw new TransferFailure($"Column mismatch in {table.Name}.");
            await using var check = destination.CreateCommand();
            check.CommandText = $"SELECT COUNT_BIG(*) FROM [dbo].{QuoteSql(table.Name)}";
            if (Convert.ToInt64(await check.ExecuteScalarAsync(), CultureInfo.InvariantCulture) != 0)
                throw new TransferFailure($"Destination table {table.Name} is not empty.");
        }
    }

    private static async Task<HashSet<string>> GetSourceTables(SqliteConnection source)
    {
        await using var command = source.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        await using var reader = await command.ExecuteReaderAsync();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }

    private static async Task<HashSet<string>> GetSourceColumns(SqliteConnection source, string table)
    {
        await using var command = source.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteSqlite(table)})";
        await using var reader = await command.ExecuteReaderAsync();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync()) names.Add(reader.GetString(1));
        return names;
    }

    private static async Task<List<Table>> GetDestinationTables(SqlConnection connection)
    {
        var tables = new Dictionary<string, Table>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.name, t.name, c.name, ty.name, c.max_length
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns c ON c.object_id = t.object_id
            JOIN sys.types ty ON c.user_type_id = ty.user_type_id
            WHERE t.is_ms_shipped = 0 AND t.name <> '__EFMigrationsHistory'
            ORDER BY t.name, c.column_id
            """;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                if (reader.GetString(0) != "dbo") throw new InvalidOperationException("Unexpected SQL schema.");
                var name = reader.GetString(1);
                if (!tables.TryGetValue(name, out var table)) tables.Add(name, table = new Table(name));
                table.Columns.Add(new Column(reader.GetString(2), reader.GetString(3), reader.GetInt16(4)));
            }
        }
        command.CommandText = """
            SELECT OBJECT_NAME(parent_object_id), OBJECT_NAME(referenced_object_id)
            FROM sys.foreign_keys WHERE is_disabled = 0
            """;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var parent = reader.GetString(0);
                var referenced = reader.GetString(1);
                if (!tables.TryGetValue(parent, out var table) || !tables.ContainsKey(referenced))
                    throw new InvalidOperationException("Unexpected SQL foreign key.");
                table.Dependencies.Add(referenced);
            }
        }
        return tables.Values.ToList();
    }

    internal static List<Table> OrderTables(List<Table> tables)
    {
        var pending = tables.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var ordered = new List<Table>();
        while (pending.Count > 0)
        {
            var ready = pending.Values.Where(t => t.Dependencies.All(d => !pending.ContainsKey(d))).OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
            if (ready.Count == 0) throw new InvalidOperationException("Cyclic or unresolved SQL foreign keys.");
            foreach (var table in ready) { ordered.Add(table); pending.Remove(table.Name); }
        }
        return ordered;
    }

    private static async Task CopyTable(SqliteConnection source, SqlConnection destination, SqlTransaction transaction, Table table)
    {
        Console.WriteLine($"Copying {table.Name}...");
        var names = string.Join(", ", table.Columns.Select(c => QuoteSqlite(c.Name)));
        await using var read = source.CreateCommand();
        read.CommandText = $"SELECT {names} FROM {QuoteSqlite(table.Name)}";
        await using var sourceRows = await read.ExecuteReaderAsync();
        var sourceHashes = new List<string>();
        var batch = CreateBatch(table);
        while (await sourceRows.ReadAsync())
        {
            var values = new object[table.Columns.Count];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = sourceRows.IsDBNull(i) ? DBNull.Value : ConvertValue(sourceRows.GetValue(i), table.Columns[i].SqlType);
                if (values[i] is string text && table.Columns[i].MaxBytes >= 0 && text.Length * 2 > table.Columns[i].MaxBytes)
                    throw new TransferFailure($"Source text exceeds the SQL limit for {table.Name}.{table.Columns[i].Name}.");
            }
            sourceHashes.Add(Hash(values));
            batch.Rows.Add(values);
            if (batch.Rows.Count == BatchSize) { await WriteBatch(destination, transaction, table, batch); batch.Clear(); }
        }
        if (batch.Rows.Count != 0) await WriteBatch(destination, transaction, table, batch);

        var destinationHashes = new List<string>();
        await using var verify = destination.CreateCommand();
        verify.Transaction = transaction;
        verify.CommandText = $"SELECT {string.Join(", ", table.Columns.Select(c => QuoteSql(c.Name)))} FROM [dbo].{QuoteSql(table.Name)}";
        await using var targetRows = await verify.ExecuteReaderAsync();
        while (await targetRows.ReadAsync())
        {
            var values = new object[table.Columns.Count];
            targetRows.GetValues(values);
            destinationHashes.Add(Hash(values));
        }
        sourceHashes.Sort(StringComparer.Ordinal);
        destinationHashes.Sort(StringComparer.Ordinal);
        if (!sourceHashes.SequenceEqual(destinationHashes, StringComparer.Ordinal))
            throw new TransferFailure($"Content reconciliation failed for {table.Name}.");
        Console.WriteLine($"{table.Name}: {sourceHashes.Count} rows reconciled");
    }

    private static DataTable CreateBatch(Table table)
    {
        var data = new DataTable();
        foreach (var column in table.Columns) data.Columns.Add(column.Name, ClrType(column.SqlType));
        return data;
    }

    private static async Task WriteBatch(SqlConnection connection, SqlTransaction transaction, Table table, DataTable data)
    {
        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.KeepNulls | SqlBulkCopyOptions.CheckConstraints, transaction)
        {
            DestinationTableName = $"[dbo].{QuoteSql(table.Name)}",
            BatchSize = BatchSize
        };
        foreach (var column in table.Columns) bulk.ColumnMappings.Add(column.Name, column.Name);
        await bulk.WriteToServerAsync(data);
    }

    private static Type ClrType(string sqlType) => sqlType switch
    {
        "nvarchar" => typeof(string),
        "uniqueidentifier" => typeof(Guid),
        "int" => typeof(int),
        "bigint" => typeof(long),
        "bit" => typeof(bool),
        "decimal" => typeof(decimal),
        "float" => typeof(double),
        "datetime2" or "date" => typeof(DateTime),
        _ => throw new InvalidOperationException($"Unsupported SQL type: {sqlType}.")
    };

    internal static object ConvertValue(object value, string sqlType) => sqlType switch
    {
        "nvarchar" => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        "uniqueidentifier" => Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!),
        "int" => Convert.ToInt32(value, CultureInfo.InvariantCulture),
        "bigint" => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        "bit" => Convert.ToInt64(value, CultureInfo.InvariantCulture) switch { 0 => false, 1 => true, _ => throw new InvalidOperationException("Invalid SQLite boolean.") },
        "decimal" => decimal.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture),
        "float" => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        "datetime2" or "date" => DateTime.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        _ => throw new InvalidOperationException($"Unsupported SQL type: {sqlType}.")
    };

    internal static string Hash(object[] values)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            foreach (var value in values)
            {
                writer.Write(value is not DBNull);
                if (value is DBNull) continue;
                var text = value switch
                {
                    DateTime date => date.Ticks.ToString(CultureInfo.InvariantCulture),
                    decimal number => number.ToString("G29", CultureInfo.InvariantCulture),
                    double number => number.ToString("R", CultureInfo.InvariantCulture),
                    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                    _ => value.ToString()
                };
                writer.Write(text ?? throw new InvalidOperationException("Unexpected null value."));
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static string QuoteSql(string value) => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";
    private static string QuoteSqlite(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    internal sealed record Column(string Name, string SqlType, short MaxBytes);
    internal sealed record Table(string Name)
    {
        public List<Column> Columns { get; } = [];
        public HashSet<string> Dependencies { get; } = new(StringComparer.Ordinal);
    }

    internal sealed class TransferFailure(string message) : Exception(message);
}
