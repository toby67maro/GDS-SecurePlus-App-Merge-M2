using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Globalization;
using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Domain.Auditing;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Infrastructure.Database;

/// <summary>
/// SQL Server, Azure SQL and SQLite provisioning. Each operation is explicit and separate; checks are read-only;
/// Create Database and Initialize Schema only add objects. Nothing drops, overwrites or downgrades existing or
/// unknown objects, and a database with a newer schema is never modified.
/// </summary>
public sealed class DatabaseProvisioningService(
    DatabaseConnectionFactory connections,
    DatabaseErrorTranslator errors,
    IAuditService audit,
    AuditContext auditContext,
    ApplicationSettings application,
    TimeProvider timeProvider,
    ILogger<DatabaseProvisioningService> logger,
    InstallProfile? install = null) : IDatabaseProvisioningService
{
    private readonly InstallProfile _install = install ?? InstallProfile.None;

    /// <summary>A Workstation install never creates, migrates or repairs the database (BLUEPRINT §10).</summary>
    private DatabaseStatusReport? WorkstationRefusal(DatabaseSettings settings, DatabaseStatusReport? current = null) => _install.CanProvision ? null
        : (current ?? Base(settings)) with
        {
            FailureCategory = DatabaseFailureCategory.Configuration,
            SanitizedError = InstallProfile.WorkstationRefusal,
            RecommendedAction = "Upgrade or create the database from the Server install."
        };

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSuccess = new(StringComparer.OrdinalIgnoreCase);

    public int RequiredSchemaVersion => SchemaMigrations.RequiredVersion;

    public async Task<DatabaseStatusReport> TestServerConnectionAsync(DatabaseSettings settings, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (NotReady(settings) is { } invalid) return invalid;
        var report = Base(settings) with { ConfigurationCheck = CheckState.Passed };
        try
        {
            if (settings.Provider == DatabaseProvider.SQLite)
            {
                progress?.Report("Checking the SQLite file location...");
                var path = connections.ResolveSqlitePath(settings);
                var directoryExists = Directory.Exists(Path.GetDirectoryName(path));
                var exists = File.Exists(path);
                report = report with
                {
                    ServerCheck = directoryExists ? CheckState.Passed : CheckState.Warning,
                    AuthenticationCheck = CheckState.NotApplicable,
                    ServerVersion = $"SQLite {await SqliteVersionAsync(cancellationToken)}",
                    DatabaseExists = exists,
                    DatabaseCheck = exists ? CheckState.Passed : CheckState.Failed,
                    State = exists ? DatabaseOverallState.Disconnected : DatabaseOverallState.DatabaseMissing,
                    SanitizedError = exists ? "The SQLite file exists. Run Check Database next." : directoryExists ? "The SQLite file does not exist yet." : "The SQLite directory does not exist yet; Create Database will create it.",
                    RecommendedAction = exists ? "Run Check Database." : "Use Create Database."
                };
            }
            else
            {
                progress?.Report($"Connecting to {settings.DisplayServer} (master)...");
                await using var connection = await connections.CreateSqlServerAsync(settings, master: true, cancellationToken);
                await connection.OpenAsync(cancellationToken);
                var version = await ServerVersionAsync(connection, settings, cancellationToken);
                var exists = await DatabaseExistsAsync(connection, settings, cancellationToken);
                report = report with
                {
                    ServerCheck = CheckState.Passed,
                    AuthenticationCheck = CheckState.Passed,
                    ServerVersion = version,
                    DatabaseExists = exists,
                    DatabaseCheck = exists ? CheckState.Passed : CheckState.Failed,
                    State = exists ? DatabaseOverallState.Disconnected : DatabaseOverallState.DatabaseMissing,
                    LastSuccessfulConnectionUtc = MarkSuccess(settings),
                    SanitizedError = exists ? "The server connection and sign-in succeeded. Run Check Database next." : $"The server connection succeeded but database '{settings.DatabaseName}' does not exist.",
                    RecommendedAction = exists ? "Run Check Database." : "Use Create Database."
                };
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            report = Failed(settings, report, exception, DatabaseErrorTranslator.Operation.Connect, serverStage: true);
        }

        await AuditAsync(AuditEventTypes.DatabaseConnectionTested, report, cancellationToken);
        return report;
    }

    public async Task<DatabaseStatusReport> CheckDatabaseAsync(DatabaseSettings settings, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var report = await EvaluateAsync(settings, deep: false, progress, cancellationToken);
        await AuditAsync(AuditEventTypes.DatabaseConnectionTested, report, cancellationToken);
        return report;
    }

    public Task<DatabaseStatusReport> ValidateSchemaAsync(DatabaseSettings settings, IProgress<string>? progress, CancellationToken cancellationToken) =>
        EvaluateAsync(settings, deep: true, progress, cancellationToken);

    public Task<DatabaseStatusReport> GetStatusAsync(DatabaseSettings settings, IProgress<string>? progress, CancellationToken cancellationToken) =>
        EvaluateAsync(settings, deep: false, progress, cancellationToken);

    public async Task<DatabaseStatusReport> CreateDatabaseAsync(DatabaseSettings settings, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (NotReady(settings) is { } invalid) return invalid;
        if (WorkstationRefusal(settings) is { } refused) return refused with { State = DatabaseOverallState.DatabaseMissing };
        var report = Base(settings) with { ConfigurationCheck = CheckState.Passed };
        try
        {
            if (settings.Provider == DatabaseProvider.SQLite)
            {
                var path = connections.ResolveSqlitePath(settings);
                if (File.Exists(path)) return await AlreadyExistsAsync(settings, progress, cancellationToken);
                progress?.Report($"Creating SQLite database {path}...");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using (var connection = connections.CreateSqlite(settings, allowCreate: true))
                {
                    await connection.OpenAsync(cancellationToken);
                }
            }
            else
            {
                if (!DatabaseConfigurationValidator.IsValidDatabaseName(settings.DatabaseName))
                    return report with { State = DatabaseOverallState.NotConfigured, ConfigurationCheck = CheckState.Failed, FailureCategory = DatabaseFailureCategory.Configuration, SanitizedError = "The database name is not a valid identifier." };
                await using var connection = await connections.CreateSqlServerAsync(settings, master: true, cancellationToken);
                await connection.OpenAsync(cancellationToken);
                if (await DatabaseExistsAsync(connection, settings, cancellationToken)) return await AlreadyExistsAsync(settings, progress, cancellationToken);
                progress?.Report($"Creating database {settings.DatabaseName} on {settings.DisplayServer}...");
                await using var command = connection.CreateCommand();
                // Identifiers cannot be parameterized: the name passed the strict identifier rule and is delimited by QuoteIdentifier.
                command.CommandText = $"CREATE DATABASE {QuoteIdentifier(settings.DatabaseName)};";
                command.CommandTimeout = settings.CommandTimeoutSeconds;
                await command.ExecuteNonQueryAsync(cancellationToken);
                if (!await DatabaseExistsAsync(connection, settings, cancellationToken))
                    return report with { State = DatabaseOverallState.DatabaseMissing, FailureCategory = DatabaseFailureCategory.CannotCreateDatabase, SanitizedError = "CREATE DATABASE completed but the database is not visible." };
            }

            logger.LogInformation("Database {Database} created on {Server} ({Provider})", settings.DatabaseName, settings.DisplayServer, settings.Provider);
            var created = await EvaluateAsync(settings, deep: false, progress, cancellationToken);
            await AuditAsync(AuditEventTypes.DatabaseCreated, created, cancellationToken, AuditOutcome.Success);
            return created;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            report = Failed(settings, report, exception, DatabaseErrorTranslator.Operation.CreateDatabase, serverStage: false);
            await AuditAsync(AuditEventTypes.DatabaseCreated, report, cancellationToken, AuditOutcome.Failure);
            return report;
        }
    }

    public async Task<DatabaseStatusReport> InitializeSchemaAsync(DatabaseSettings settings, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var before = await EvaluateAsync(settings, deep: false, progress, cancellationToken);
        if (before.SchemaState is SchemaState.Missing or SchemaState.MigrationRequired && WorkstationRefusal(settings, before) is { } refused) return refused;
        if (before.SchemaState is not (SchemaState.Missing or SchemaState.MigrationRequired))
            return before with
            {
                SanitizedError = before.SchemaState == SchemaState.Current ? "The schema is already current; nothing was changed." : before.SanitizedError ?? "The schema cannot be initialized in its current state.",
                RecommendedAction = before.RecommendedAction
            };

        var migrations = SchemaMigrations.For(settings.Provider);
        await audit.RecordAsync(AuditEventTypes.DatabaseMigrationStarted, AuditOutcome.Information,
            $"Schema migration started from version {before.CurrentSchemaVersion?.ToString(CultureInfo.InvariantCulture) ?? "none"} to {RequiredSchemaVersion}.", Properties(before), cancellationToken);
        try
        {
            await using var connection = await OpenDatabaseAsync(settings, cancellationToken);
            await SchemaMigrator.ApplyAsync(connection, settings.CommandTimeoutSeconds, migrations, auditContext.Actor, application.ApplicationVersion, timeProvider, progress, cancellationToken);
            logger.LogInformation("Schema migrations applied to {Database}", settings.DatabaseName);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var failed = Failed(settings, before, exception, DatabaseErrorTranslator.Operation.CreateSchema, serverStage: false);
            await audit.RecordAsync(AuditEventTypes.DatabaseMigrationFailed, AuditOutcome.Failure, $"Schema migration failed: {failed.SanitizedError}", Properties(failed), cancellationToken);
            return failed;
        }

        var after = await EvaluateAsync(settings, deep: true, progress, cancellationToken);
        await audit.RecordAsync(AuditEventTypes.DatabaseMigrationCompleted, AuditOutcome.Success, $"Schema migration completed; schema version {after.CurrentSchemaVersion}.", Properties(after), cancellationToken);
        if (before.SchemaState == SchemaState.Missing)
            await AuditAsync(AuditEventTypes.DatabaseInitialized, after, cancellationToken, after.State == DatabaseOverallState.Ready ? AuditOutcome.Success : AuditOutcome.Warning);
        return after;
    }

    public async Task<DatabaseStatusReport> RepairSchemaAsync(DatabaseSettings settings, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var before = await EvaluateAsync(settings, deep: true, progress, cancellationToken);
        if (before.SchemaState == SchemaState.Newer || before.DatabaseAccessCheck != CheckState.Passed)
            return before;
        if (before.State == DatabaseOverallState.Ready && before.SchemaFindings.Count == 0)
            return before with { SanitizedError = "The schema is complete; nothing needed repair." };
        if (WorkstationRefusal(settings, before) is { } refused) return refused;

        var migrations = SchemaMigrations.For(settings.Provider);
        var columns = MissingColumnStatements(settings.Provider, before.SchemaFindings, migrations, out var skipped);
        await audit.RecordAsync(AuditEventTypes.DatabaseMigrationStarted, AuditOutcome.Information,
            $"Schema repair started ({before.SchemaFindings.Count} finding(s)); re-applying {migrations.Count} idempotent migration(s).", Properties(before), cancellationToken);
        try
        {
            await using var connection = await OpenDatabaseAsync(settings, cancellationToken);
            await SchemaMigrator.ApplyAsync(connection, settings.CommandTimeoutSeconds, migrations, auditContext.Actor, application.ApplicationVersion, timeProvider, progress,
                cancellationToken, reapplyAll: true, additionalStatements: columns);
            logger.LogInformation("Schema repaired for {Database}: scripts re-applied, {Columns} column(s) added", settings.DatabaseName, columns.Count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var failed = Failed(settings, before, exception, DatabaseErrorTranslator.Operation.CreateSchema, serverStage: false);
            await audit.RecordAsync(AuditEventTypes.DatabaseMigrationFailed, AuditOutcome.Failure, $"Schema repair failed: {failed.SanitizedError}", Properties(failed), cancellationToken);
            return failed;
        }

        var after = await EvaluateAsync(settings, deep: true, progress, cancellationToken);
        await audit.RecordAsync(AuditEventTypes.DatabaseMigrationCompleted, after.State == DatabaseOverallState.Ready ? AuditOutcome.Success : AuditOutcome.Warning,
            $"Schema repair completed; state {after.State}.", Properties(after), cancellationToken);
        return skipped.Count == 0 ? after : after with { SchemaFindings = [.. after.SchemaFindings, .. skipped] };
    }

    /// <summary>
    /// ALTER TABLE ... ADD statements for missing columns, taken from the column definitions in the migration scripts.
    /// Only nullable (or defaulted) columns are added; key, identity and required columns are reported instead.
    /// </summary>
    internal static IReadOnlyList<string> MissingColumnStatements(DatabaseProvider provider, IReadOnlyList<string> findings, IReadOnlyList<SchemaMigration> migrations, out List<string> skipped)
    {
        skipped = [];
        var statements = new List<string>();
        var sqlite = provider == DatabaseProvider.SQLite;
        foreach (var finding in findings)
        {
            var match = System.Text.RegularExpressions.Regex.Match(finding, @"^Missing column (?<schema>[^.]+)\.(?<table>[^.]+)\.(?<column>.+)\.$");
            if (!match.Success) continue;
            var (schema, table, column) = (match.Groups["schema"].Value, match.Groups["table"].Value, match.Groups["column"].Value);
            var definition = migrations.Select(migration => ColumnDefinition(migration.Script, sqlite, schema, table, column)).FirstOrDefault(found => found is not null);
            var upper = definition?.ToUpperInvariant() ?? string.Empty;
            if (definition is null || upper.Contains("PRIMARY KEY", StringComparison.Ordinal) || upper.Contains("IDENTITY", StringComparison.Ordinal) ||
                upper.Contains("GENERATED ALWAYS", StringComparison.Ordinal) || (upper.Contains("NOT NULL", StringComparison.Ordinal) && !upper.Contains("DEFAULT", StringComparison.Ordinal)))
            {
                skipped.Add($"Column {schema}.{table}.{column} could not be added automatically (required, key or generated column); a DBA must add it.");
                continue;
            }

            statements.Add(sqlite
                ? $"ALTER TABLE \"{schema}.{table}\" ADD COLUMN {definition};"
                : $"ALTER TABLE {QuoteIdentifier(schema)}.{QuoteIdentifier(table)} ADD {definition};");
        }

        return statements;
    }

    private static string? ColumnDefinition(string script, bool sqlite, string schema, string table, string column)
    {
        var header = sqlite ? $"CREATE TABLE IF NOT EXISTS \"{schema}.{table}\" (" : $"CREATE TABLE [{schema}].[{table}] (";
        var start = script.IndexOf(header, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var end = script.IndexOf("\n);", start, StringComparison.Ordinal);
        if (end < 0) return null;
        var quoted = sqlite ? $"\"{column}\"" : $"[{column}]";
        foreach (var raw in script[(start + header.Length)..end].Split('\n'))
        {
            var line = raw.Trim().TrimEnd(',');
            if (line.StartsWith(quoted + " ", StringComparison.OrdinalIgnoreCase)) return line;
        }

        return null;
    }

    private static async Task<DatabaseStatusReport> PermissionReportAsync(DbConnection connection, DatabaseSettings settings, DatabaseStatusReport report, CancellationToken cancellationToken)
    {
        var permissions = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (connection is SqliteConnection)
        {
            var path = connection.DataSource;
            var writable = !string.IsNullOrEmpty(path) && File.Exists(path) && !new FileInfo(path).IsReadOnly;
            foreach (var name in RuntimePermissions.Concat(SchemaPermissions)) permissions[name] = writable;
        }
        else
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = settings.CommandTimeoutSeconds;
            command.CommandText = "SELECT " + string.Join(", ", RuntimePermissions.Concat(SchemaPermissions).Select(name => $"HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'{name}')")) + ";";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var index = 0;
                foreach (var name in RuntimePermissions.Concat(SchemaPermissions))
                {
                    permissions[name] = !reader.IsDBNull(index) && Convert.ToInt32(reader.GetValue(index), CultureInfo.InvariantCulture) == 1;
                    index++;
                }
            }
        }

        var runtimeMissing = RuntimePermissions.Any(name => !permissions.GetValueOrDefault(name));
        var schemaMissing = SchemaPermissions.Any(name => !permissions.GetValueOrDefault(name));
        return report with
        {
            Permissions = permissions,
            PermissionCheck = runtimeMissing ? CheckState.Failed : schemaMissing ? CheckState.Warning : CheckState.Passed
        };
    }

    /// <summary>Permissions the application needs every day.</summary>
    private static readonly string[] RuntimePermissions = ["SELECT", "INSERT", "UPDATE", "DELETE"];

    /// <summary>Permissions needed to install, upgrade or repair the schema.</summary>
    private static readonly string[] SchemaPermissions = ["CREATE TABLE", "CREATE SCHEMA", "ALTER ANY SCHEMA", "REFERENCES"];

    /// <summary>A database the principal cannot read and write is not ready; missing DDL rights matter only when the schema needs work.</summary>
    private static DatabaseStatusReport ApplyPermissionOutcome(DatabaseStatusReport report)
    {
        var missingRuntime = RuntimePermissions.Where(name => report.Permissions.TryGetValue(name, out var granted) && !granted).ToArray();
        var missingSchema = SchemaPermissions.Where(name => report.Permissions.TryGetValue(name, out var granted) && !granted).ToArray();
        if (report.State == DatabaseOverallState.Ready && missingRuntime.Length > 0)
            return report with
            {
                State = DatabaseOverallState.ConnectionFailed,
                FailureCategory = DatabaseFailureCategory.NoAccessToDatabase,
                SanitizedError = $"The schema is current but the signed-in account lacks {string.Join(", ", missingRuntime)} permission on the database.",
                RecommendedAction = "Add the account to db_datareader and db_datawriter (or grant the listed permissions)."
            };
        if (report.State is DatabaseOverallState.SchemaMissing or DatabaseOverallState.MigrationRequired or DatabaseOverallState.IncompatibleSchema && missingSchema.Length > 0)
            return report with
            {
                PermissionCheck = CheckState.Failed,
                RecommendedAction = $"{report.RecommendedAction} The account also lacks {string.Join(", ", missingSchema)} permission, which installing or repairing the schema needs (for example db_ddladmin or db_owner).".Trim()
            };
        return report;
    }

    private async Task<DatabaseStatusReport> EvaluateAsync(DatabaseSettings settings, bool deep, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (NotReady(settings) is { } invalid) return invalid;
        var report = Base(settings) with { ConfigurationCheck = CheckState.Passed };
        try
        {
            if (settings.Provider == DatabaseProvider.SQLite)
            {
                var path = connections.ResolveSqlitePath(settings);
                report = report with { ServerCheck = CheckState.Passed, AuthenticationCheck = CheckState.NotApplicable, ServerVersion = $"SQLite {await SqliteVersionAsync(cancellationToken)}" };
                if (!File.Exists(path))
                    return report with { State = DatabaseOverallState.DatabaseMissing, DatabaseExists = false, DatabaseCheck = CheckState.Failed, FailureCategory = DatabaseFailureCategory.DatabaseMissing,
                        SanitizedError = "The SQLite database file does not exist.", RecommendedAction = "Use Create Database." };
                report = report with { DatabaseExists = true, DatabaseCheck = CheckState.Passed };
            }
            else
            {
                progress?.Report($"Connecting to {settings.DisplayServer}...");
                await using var master = await connections.CreateSqlServerAsync(settings, master: true, cancellationToken);
                await master.OpenAsync(cancellationToken);
                report = report with { ServerCheck = CheckState.Passed, AuthenticationCheck = CheckState.Passed, ServerVersion = await ServerVersionAsync(master, settings, cancellationToken) };
                if (!await DatabaseExistsAsync(master, settings, cancellationToken))
                    return report with { State = DatabaseOverallState.DatabaseMissing, DatabaseExists = false, DatabaseCheck = CheckState.Failed, FailureCategory = DatabaseFailureCategory.DatabaseMissing,
                        LastSuccessfulConnectionUtc = MarkSuccess(settings), SanitizedError = $"Database '{settings.DatabaseName}' does not exist.", RecommendedAction = "Use Create Database." };
                report = report with { DatabaseExists = true, DatabaseCheck = CheckState.Passed };
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed(settings, report, exception, DatabaseErrorTranslator.Operation.Connect, serverStage: true);
        }

        try
        {
            progress?.Report($"Opening database {settings.DatabaseName}...");
            await using var connection = await OpenDatabaseAsync(settings, cancellationToken);
            report = report with { DatabaseAccessCheck = CheckState.Passed, LastSuccessfulConnectionUtc = MarkSuccess(settings) };
            progress?.Report("Checking database permissions...");
            report = await PermissionReportAsync(connection, settings, report, cancellationToken);
            progress?.Report("Reading schema version...");
            var schema = await SchemaReportAsync(connection, settings, report, deep, cancellationToken);
            return ApplyPermissionOutcome(schema);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var failure = errors.Translate(exception, DatabaseErrorTranslator.Operation.Connect);
            logger.LogWarning("Database {Database} could not be opened: {Category}", settings.DatabaseName, failure.Category);
            return report with
            {
                State = DatabaseOverallState.ConnectionFailed,
                DatabaseAccessCheck = CheckState.Failed,
                FailureCategory = failure.Category == DatabaseFailureCategory.Unexpected ? DatabaseFailureCategory.NoAccessToDatabase : failure.Category,
                SanitizedError = Join(failure.Message, failure.Detail),
                RecommendedAction = failure.RecommendedAction
            };
        }
    }

    private async Task<DatabaseStatusReport> SchemaReportAsync(DbConnection connection, DatabaseSettings settings, DatabaseStatusReport report, bool deep, CancellationToken cancellationToken)
    {
        var tables = await ExistingTablesAsync(connection, settings, cancellationToken);
        if (!tables.ContainsKey("secureplus.SchemaVersion"))
        {
            var known = SchemaMigrations.ExpectedTables.Select(table => $"{table.Schema}.{table.Name}").ToHashSet(StringComparer.OrdinalIgnoreCase);
            var foreign = tables.Keys.Count(known.Contains);
            var legacy = tables.ContainsKey("__secureplus_schema") || tables.ContainsKey("dbo.__secureplus_schema");
            if (foreign == 0 && !legacy)
                return report with { State = DatabaseOverallState.SchemaMissing, SchemaState = SchemaState.Missing, SchemaPresentCheck = CheckState.Failed, SchemaCompatibleCheck = CheckState.NotRun,
                    SanitizedError = "The database is empty of Secure Plus objects.", RecommendedAction = "Use Initialize Schema." };
            var reason = legacy
                ? "The database was created by a pre-release foundation build (marker __secureplus_schema). It is not modified."
                : $"The database contains {foreign} EaaS-named table(s) but no Secure Plus schema version, so it was not initialized by this application (or initialization was interrupted outside a transaction). It is not modified.";
            return report with { State = DatabaseOverallState.IncompatibleSchema, SchemaState = SchemaState.Incompatible, SchemaPresentCheck = CheckState.Warning, SchemaCompatibleCheck = CheckState.Failed,
                FailureCategory = DatabaseFailureCategory.SchemaIncompatible, SanitizedError = reason, RecommendedAction = "Use a new, empty database, or have a DBA review the existing objects." };
        }

        var applied = await SchemaMigrator.AppliedVersionsAsync(connection, null, cancellationToken);
        var current = applied.Count == 0 ? 0 : applied.Keys.Max();
        report = report with { CurrentSchemaVersion = current, SchemaPresentCheck = CheckState.Passed };
        if (current > RequiredSchemaVersion)
            return report with { State = DatabaseOverallState.IncompatibleSchema, SchemaState = SchemaState.Newer, SchemaCompatibleCheck = CheckState.Failed, FailureCategory = DatabaseFailureCategory.SchemaIncompatible,
                SanitizedError = $"Update required: the database (schema version {current}) was upgraded by a newer Secure Plus than this one ({application.ApplicationVersion}).",
                RecommendedAction = "Install the latest Secure Plus on this computer. The database was not changed." };
        if (current < RequiredSchemaVersion)
            return report with { State = DatabaseOverallState.MigrationRequired, SchemaState = SchemaState.MigrationRequired, SchemaCompatibleCheck = CheckState.Warning,
                SanitizedError = $"Schema version {current} is older than the required version {RequiredSchemaVersion}." +
                    (_install.CanProvision ? string.Empty : " This Workstation is newer than the database; the Server install must be upgraded first."),
                RecommendedAction = _install.CanProvision ? "Use Initialize Schema to apply the pending migrations." : "Upgrade the Server install first; it upgrades the database." };

        var findings = new List<string>();
        foreach (var migration in SchemaMigrations.For(settings.Provider))
        {
            if (applied.TryGetValue(migration.Version, out var checksum) && !string.Equals(checksum, migration.Checksum, StringComparison.OrdinalIgnoreCase))
                findings.Add($"Migration V{migration.Version:0000} was applied from a script with a different checksum.");
        }

        if (deep)
        {
            foreach (var table in SchemaMigrations.ExpectedTables)
            {
                if (!tables.TryGetValue($"{table.Schema}.{table.Name}", out var columns)) findings.Add($"Missing table {table.Schema}.{table.Name}.");
                else findings.AddRange(table.Columns.Where(column => !columns.Contains(column)).Select(column => $"Missing column {table.Schema}.{table.Name}.{column}."));
            }
        }

        var missingObjects = findings.Any(finding => finding.StartsWith("Missing", StringComparison.Ordinal));
        return report with
        {
            State = missingObjects ? DatabaseOverallState.IncompatibleSchema : DatabaseOverallState.Ready,
            SchemaState = missingObjects ? SchemaState.Incompatible : SchemaState.Current,
            SchemaCompatibleCheck = missingObjects ? CheckState.Failed : findings.Count > 0 ? CheckState.Warning : CheckState.Passed,
            FailureCategory = missingObjects ? DatabaseFailureCategory.SchemaIncompatible : DatabaseFailureCategory.None,
            SchemaFindings = findings.Take(50).ToArray(),
            SanitizedError = missingObjects ? $"{findings.Count(finding => finding.StartsWith("Missing", StringComparison.Ordinal))} expected schema object(s) are missing." : null,
            RecommendedAction = missingObjects ? "Have a DBA review the database; Secure Plus does not repair or overwrite existing objects." : null
        };
    }

    private async Task<DbConnection> OpenDatabaseAsync(DatabaseSettings settings, CancellationToken cancellationToken)
    {
        DbConnection connection = settings.Provider == DatabaseProvider.SQLite
            ? connections.CreateSqlite(settings, allowCreate: false)
            : await connections.CreateSqlServerAsync(settings, master: false, cancellationToken);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<Dictionary<string, HashSet<string>>> ExistingTablesAsync(DbConnection connection, DatabaseSettings settings, CancellationToken cancellationToken)
    {
        var tables = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = settings.CommandTimeoutSeconds;
        command.CommandText = connection is SqliteConnection
            ? "SELECT m.name, p.name FROM sqlite_master AS m JOIN pragma_table_info(m.name) AS p WHERE m.type = 'table' ORDER BY m.name, p.cid;"
            : "SELECT s.name + N'.' + t.name, c.name FROM sys.tables AS t JOIN sys.schemas AS s ON s.schema_id = t.schema_id JOIN sys.columns AS c ON c.object_id = t.object_id ORDER BY s.name, t.name, c.column_id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var table = reader.GetString(0);
            if (!tables.TryGetValue(table, out var columns)) tables[table] = columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            columns.Add(reader.GetString(1));
        }

        return tables;
    }

    private static async Task<bool> DatabaseExistsAsync(SqlConnection master, DatabaseSettings settings, CancellationToken cancellationToken)
    {
        await using var command = master.CreateCommand();
        command.CommandTimeout = settings.CommandTimeoutSeconds;
        command.CommandText = "SELECT CASE WHEN DB_ID(@name) IS NULL THEN 0 ELSE 1 END;";
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = settings.DatabaseName });
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<string> ServerVersionAsync(SqlConnection connection, DatabaseSettings settings, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = settings.CommandTimeoutSeconds;
        command.CommandText = "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)) + N' ' + CAST(SERVERPROPERTY('Edition') AS nvarchar(128));";
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) ?? connection.ServerVersion;
    }

    private static async Task<string> SqliteVersionAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) ?? "unknown";
    }

    private async Task<DatabaseStatusReport> AlreadyExistsAsync(DatabaseSettings settings, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var report = await EvaluateAsync(settings, deep: false, progress, cancellationToken);
        return report with { SanitizedError = Join("The database already exists; no changes were made.", report.SanitizedError) };
    }

    private DatabaseStatusReport? NotReady(DatabaseSettings settings)
    {
        if (settings.Provider == DatabaseProvider.None)
            return Base(settings) with { State = DatabaseOverallState.NotConfigured, ConfigurationCheck = CheckState.Failed, SanitizedError = "No database provider is configured.", RecommendedAction = "Select SQLite, SQL Server or Azure SQL." };
        var validation = DatabaseConfigurationValidator.Validate(settings);
        return validation.Count == 0 ? null : Base(settings) with
        {
            State = DatabaseOverallState.NotConfigured,
            ConfigurationCheck = CheckState.Failed,
            FailureCategory = DatabaseFailureCategory.Configuration,
            SanitizedError = string.Join(" ", validation.Select(error => $"{error.Key}: {error.Message}")),
            RecommendedAction = "Correct the highlighted settings."
        };
    }

    private DatabaseStatusReport Failed(DatabaseSettings settings, DatabaseStatusReport report, Exception exception, DatabaseErrorTranslator.Operation operation, bool serverStage)
    {
        var failure = errors.Translate(exception, operation);
        logger.LogWarning(exception, "Database operation failed for {Provider} {Server}/{Database}: {Category}", settings.Provider, settings.DisplayServer, settings.DatabaseName, failure.Category);
        var loginFailed = failure.Category is DatabaseFailureCategory.LoginFailed or DatabaseFailureCategory.CredentialUnavailable;
        return report with
        {
            State = DatabaseOverallState.ConnectionFailed,
            ServerCheck = serverStage && !loginFailed && report.ServerCheck != CheckState.Passed ? CheckState.Failed : loginFailed ? CheckState.Passed : report.ServerCheck,
            AuthenticationCheck = loginFailed ? CheckState.Failed : report.AuthenticationCheck,
            FailureCategory = failure.Category,
            SanitizedError = Join(failure.Message, failure.Detail),
            RecommendedAction = failure.RecommendedAction
        };
    }

    private DatabaseStatusReport Base(DatabaseSettings settings) => new()
    {
        State = DatabaseOverallState.Disconnected,
        Provider = settings.Provider.ToString(),
        Server = settings.DisplayServer,
        Database = settings.Provider == DatabaseProvider.SQLite && settings.DatabaseName.Length > 0 ? connections.ResolveSqlitePath(settings) : settings.DatabaseName,
        Authentication = settings.Authentication.ToString(),
        Encrypted = settings.IsServerProvider && settings.EncryptConnection,
        RequiredSchemaVersion = RequiredSchemaVersion,
        LastAttemptUtc = timeProvider.GetUtcNow(),
        LastSuccessfulConnectionUtc = _lastSuccess.TryGetValue(Key(settings), out var last) ? last : null
    };

    private DateTimeOffset MarkSuccess(DatabaseSettings settings) => _lastSuccess[Key(settings)] = timeProvider.GetUtcNow();

    private static string Key(DatabaseSettings settings) => $"{settings.Provider}|{settings.DisplayServer}|{settings.DatabaseName}";

    /// <summary>Delimits a validated SQL Server identifier.</summary>
    public static string QuoteIdentifier(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string Join(string? first, string? second) => string.Join(" ", new[] { first, second }.Where(part => !string.IsNullOrWhiteSpace(part)));

    private async Task AuditAsync(string eventType, DatabaseStatusReport report, CancellationToken cancellationToken, AuditOutcome? outcome = null) =>
        await audit.RecordAsync(eventType, outcome ?? (report.State is DatabaseOverallState.ConnectionFailed or DatabaseOverallState.NotConfigured ? AuditOutcome.Failure : AuditOutcome.Success),
            $"{report.Provider} {report.Server}/{report.Database}: {report.State}.", Properties(report), cancellationToken);

    private static Dictionary<string, string?> Properties(DatabaseStatusReport report) => new()
    {
        ["Provider"] = report.Provider,
        ["Server"] = report.Server,
        ["Database"] = report.Database,
        ["Authentication"] = report.Authentication,
        ["State"] = report.State.ToString(),
        ["SchemaVersion"] = report.CurrentSchemaVersion?.ToString(CultureInfo.InvariantCulture),
        ["FailureCategory"] = report.FailureCategory == DatabaseFailureCategory.None ? null : report.FailureCategory.ToString()
    };
}
