using System.Data.Common;
using System.Globalization;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Security;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Infrastructure.Database;

/// <summary>
/// Collects SQL Server / Azure SQL health from catalog views and DMVs. Each query runs on its own so that a missing
/// permission (VIEW SERVER STATE, msdb access) degrades only the metrics that need it. Read-only. For SQLite the file
/// sizes and the free space of its drive are reported.
/// </summary>
public sealed class DatabaseHealthService(IDatabaseStatusService status, DatabaseConnectionFactory connections, IRedactionService redaction,
    TimeProvider time, ILogger<DatabaseHealthService> logger) : IDatabaseHealthService
{
    /// <summary>Benign background waits excluded from Top Waits.</summary>
    private const string BenignWaits =
        "'BROKER_EVENTHANDLER','BROKER_RECEIVE_WAITFOR','BROKER_TASK_STOP','BROKER_TO_FLUSH','BROKER_TRANSMITTER','CHECKPOINT_QUEUE','CHKPT','CLR_AUTO_EVENT'," +
        "'CLR_MANUAL_EVENT','CLR_SEMAPHORE','DBMIRROR_DBM_EVENT','DBMIRROR_EVENTS_QUEUE','DBMIRROR_WORKER_QUEUE','DBMIRRORING_CMD','DIRTY_PAGE_POLL','DISPATCHER_QUEUE_SEMAPHORE'," +
        "'EXECSYNC','FSAGENT','FT_IFTS_SCHEDULER_IDLE_WAIT','FT_IFTSHC_MUTEX','HADR_CLUSAPI_CALL','HADR_FILESTREAM_IOMGR_IOCOMPLETION','HADR_LOGCAPTURE_WAIT'," +
        "'HADR_NOTIFICATION_DEQUEUE','HADR_TIMER_TASK','HADR_WORK_QUEUE','KSOURCE_WAKEUP','LAZYWRITER_SLEEP','LOGMGR_QUEUE','MEMORY_ALLOCATION_EXT','ONDEMAND_TASK_QUEUE'," +
        "'PARALLEL_REDO_DRAIN_WORKER','PARALLEL_REDO_LOG_CACHE','PARALLEL_REDO_TRAN_LIST','PARALLEL_REDO_WORKER_SYNC','PARALLEL_REDO_WORKER_WAIT_WORK'," +
        "'PREEMPTIVE_XE_GETTARGETSTATE','PWAIT_ALL_COMPONENTS_INITIALIZED','PWAIT_DIRECTLOGCONSUMER_GETNEXT','QDS_PERSIST_TASK_MAIN_LOOP_SLEEP','QDS_ASYNC_QUEUE'," +
        "'QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP','QDS_SHUTDOWN_QUEUE','REDO_THREAD_PENDING_WORK','REQUEST_FOR_DEADLOCK_SEARCH','RESOURCE_QUEUE','SERVER_IDLE_CHECK'," +
        "'SLEEP_BPOOL_FLUSH','SLEEP_DBSTARTUP','SLEEP_DCOMSTARTUP','SLEEP_MASTERDBREADY','SLEEP_MASTERMDREADY','SLEEP_MASTERUPGRADED','SLEEP_MSDBSTARTUP','SLEEP_SYSTEMTASK'," +
        "'SLEEP_TASK','SLEEP_TEMPDBSTARTUP','SNI_HTTP_ACCEPT','SOS_WORK_DISPATCHER','SP_SERVER_DIAGNOSTICS_SLEEP','SQLTRACE_BUFFER_FLUSH','SQLTRACE_INCREMENTAL_FLUSH_SLEEP'," +
        "'SQLTRACE_WAIT_ENTRIES','VDI_CLIENT_OTHER','WAIT_FOR_RESULTS','WAITFOR','WAITFOR_TASKSHUTDOWN','WAIT_XTP_RECOVERY','WAIT_XTP_HOST_WAIT','WAIT_XTP_OFFLINE_CKPT_NEW_LOG'," +
        "'WAIT_XTP_CKPT_CLOSE','XE_DISPATCHER_JOIN','XE_DISPATCHER_WAIT','XE_TIMER_EVENT'";

    private readonly List<GrowthPoint> _sessionSamples = [];

    public async Task<DatabaseHealthSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var settings = status.Settings;
        var now = time.GetUtcNow();
        return settings.Provider switch
        {
            DatabaseProvider.SQLite => CaptureSqlite(settings, now),
            DatabaseProvider.SqlServer or DatabaseProvider.AzureSql => await CaptureSqlServerAsync(settings, now, cancellationToken).ConfigureAwait(false),
            _ => new DatabaseHealthSnapshot
            {
                CapturedAtUtc = now, Provider = settings.Provider, Server = "–", Database = "–",
                Error = "No database is configured. Configure one in Settings > Database Configuration."
            }
        };
    }

    private DatabaseHealthSnapshot CaptureSqlite(DatabaseSettings settings, DateTimeOffset now)
    {
        var path = connections.ResolveSqlitePath(settings);
        var files = new List<DatabaseFileHealth>();
        foreach (var (file, type) in new[] { (path, "ROWS"), ($"{path}-wal", "LOG") })
        {
            if (!File.Exists(file)) continue;
            files.Add(new DatabaseFileHealth(Path.GetFileName(file), type, file, Math.Round(new FileInfo(file).Length / 1048576.0, 2), null, "Unlimited", "Automatic"));
        }

        var volumes = new List<VolumeHealth>();
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            volumes.Add(new VolumeHealth(drive.Name, drive.TotalSize / 1073741824.0, drive.AvailableFreeSpace / 1073741824.0));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogDebug(exception, "Drive of {Path} could not be read", path);
        }

        string? version = null;
        if (files.Count > 0)
        {
            try
            {
                using var connection = connections.CreateSqlite(settings, allowCreate: false);
                connection.Open();
                version = $"SQLite {connection.ServerVersion}";
            }
            catch (Exception exception) when (exception is DbException or InvalidOperationException)
            {
                logger.LogDebug(exception, "SQLite version could not be read");
            }
        }

        Sample(now, files.Where(file => file.Type == "ROWS").Sum(file => file.SizeMb));
        return new DatabaseHealthSnapshot
        {
            CapturedAtUtc = now, Provider = DatabaseProvider.SQLite, Server = Environment.MachineName, Database = path,
            Version = version ?? "SQLite", Edition = "Embedded", HasServerState = true,
            Files = files, Volumes = volumes, Growth = [.. _sessionSamples], BackupsManagedByPlatform = true,
            Limitations = files.Count == 0
                ? ["The SQLite database file does not exist yet; create it in Settings > Database Configuration."]
                : ["SQLite is an embedded file database: CPU, memory, sessions, blocking, waits and backups apply to SQL Server and Azure SQL only. Back up the file with your workstation backups."]
        };
    }

    private async Task<DatabaseHealthSnapshot> CaptureSqlServerAsync(DatabaseSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var limitations = new List<string>();
        SqlConnection connection;
        try
        {
            connection = await connections.CreateSqlServerAsync(settings, master: false, cancellationToken).ConfigureAwait(false);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DbException or DatabaseCredentialUnavailableException or InvalidOperationException)
        {
            return new DatabaseHealthSnapshot
            {
                CapturedAtUtc = now, Provider = settings.Provider, Server = settings.DisplayServer, Database = settings.DatabaseName,
                Error = $"The database could not be reached: {redaction.RedactText(exception.Message)}"
            };
        }

        await using (connection)
        {
            async Task<T?> Try<T>(string area, Func<Task<T>> read)
            {
                try
                {
                    return await read().ConfigureAwait(false);
                }
                catch (SqlException exception)
                {
                    logger.LogDebug(exception, "Health metric {Area} unavailable", area);
                    limitations.Add($"{area}: {(exception.Number is 229 or 297 or 300 or 916 ? "permission denied" : redaction.RedactText(exception.Message))}");
                    return default;
                }
            }

            var (version, edition, engineEdition, login) = await Try("Version", async () =>
            {
                await using var command = Command(connection, "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), CAST(SERVERPROPERTY('Edition') AS nvarchar(128)), " +
                                                              "CAST(SERVERPROPERTY('ProductLevel') AS nvarchar(128)), CAST(SERVERPROPERTY('EngineEdition') AS int), SUSER_SNAME()");
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                return ($"{reader.GetString(0)} ({reader.GetString(2)})", reader.GetString(1), reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4));
            });
            var isAzureDatabase = engineEdition == 5;
            var permission = isAzureDatabase ? "VIEW DATABASE STATE" : "VIEW SERVER STATE";
            var hasState = await Try("Permissions", async () =>
                Convert.ToInt32(await Command(connection, isAzureDatabase
                    ? "SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DATABASE STATE')"
                    : "SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE')").ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1);
            if (!hasState) limitations.Add($"{permission} is not granted to {login ?? "the application login"}: server-wide metrics are limited to what the login can see.");

            var files = await Try("Files", async () =>
            {
                await using var command = Command(connection,
                    "SELECT name, type_desc, physical_name, size * 8 / 1024.0, CAST(FILEPROPERTY(name, 'SpaceUsed') AS bigint) * 8 / 1024.0, max_size, growth, is_percent_growth FROM sys.database_files");
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var list = new List<DatabaseFileHealth>();
                while (await reader.ReadAsync(cancellationToken))
                {
                    var maxPages = reader.GetInt32(5);
                    var growth = reader.GetInt32(6);
                    list.Add(new DatabaseFileHealth(reader.GetString(0), reader.GetString(1), reader.GetString(2), Math.Round(Convert.ToDouble(reader.GetValue(3), CultureInfo.InvariantCulture), 1),
                        reader.IsDBNull(4) ? null : Math.Round(Convert.ToDouble(reader.GetValue(4), CultureInfo.InvariantCulture), 1),
                        maxPages switch { -1 => "Unlimited", 0 => "No growth", _ => $"{maxPages * 8L / 1024:N0} MB" },
                        growth == 0 ? "None" : reader.GetBoolean(7) ? $"{growth}%" : $"{growth * 8L / 1024:N0} MB"));
                }

                return (IReadOnlyList<DatabaseFileHealth>)list;
            }) ?? [];

            IReadOnlyList<VolumeHealth> volumes = isAzureDatabase
                ? await Try("Database size limit", async () =>
                {
                    var max = Convert.ToDouble(await Command(connection, "SELECT CAST(DATABASEPROPERTYEX(DB_NAME(), 'MaxSizeInBytes') AS bigint)").ExecuteScalarAsync(cancellationToken),
                        CultureInfo.InvariantCulture) / 1073741824.0;
                    var used = files.Where(file => file.Type != "LOG").Sum(file => file.UsedMb ?? file.SizeMb) / 1024.0;
                    return (IReadOnlyList<VolumeHealth>)[new VolumeHealth("Database maximum size", max, Math.Max(0, max - used))];
                }) ?? []
                : await Try("Disk space", async () =>
                {
                    await using var command = Command(connection, "SELECT DISTINCT vs.volume_mount_point, vs.total_bytes, vs.available_bytes FROM sys.database_files AS f " +
                                                                  "CROSS APPLY sys.dm_os_volume_stats(DB_ID(), f.file_id) AS vs");
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    var list = new List<VolumeHealth>();
                    while (await reader.ReadAsync(cancellationToken))
                        list.Add(new VolumeHealth(reader.GetString(0), reader.GetInt64(1) / 1073741824.0, reader.GetInt64(2) / 1073741824.0));
                    return (IReadOnlyList<VolumeHealth>)list;
                }) ?? [];

            double? sqlCpu = null, systemCpu = null, memoryMb = null, targetMb = null, memoryPercent = null;
            if (isAzureDatabase)
            {
                await Try("CPU and memory", async () =>
                {
                    await using var command = Command(connection, "SELECT TOP (1) avg_cpu_percent, avg_memory_usage_percent FROM sys.dm_db_resource_stats ORDER BY end_time DESC");
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        sqlCpu = Convert.ToDouble(reader.GetValue(0), CultureInfo.InvariantCulture);
                        memoryPercent = Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture);
                    }

                    return true;
                });
            }
            else if (hasState)
            {
                await Try("CPU", async () =>
                {
                    await using var command = Command(connection,
                        "SELECT TOP (1) x.record.value('(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]', 'int'), " +
                        "x.record.value('(./Record/SchedulerMonitorEvent/SystemHealth/SystemIdle)[1]', 'int') " +
                        "FROM (SELECT CONVERT(xml, record) AS record, [timestamp] FROM sys.dm_os_ring_buffers " +
                        "WHERE ring_buffer_type = N'RING_BUFFER_SCHEDULER_MONITOR' AND record LIKE N'%<SystemHealth>%') AS x ORDER BY x.[timestamp] DESC");
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        sqlCpu = reader.GetInt32(0);
                        systemCpu = 100 - reader.GetInt32(1);
                    }

                    return true;
                });
                await Try("Memory", async () =>
                {
                    await using var command = Command(connection, "SELECT (SELECT physical_memory_in_use_kb FROM sys.dm_os_process_memory) / 1024.0, committed_target_kb / 1024.0 FROM sys.dm_os_sys_info");
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        memoryMb = Math.Round(Convert.ToDouble(reader.GetValue(0), CultureInfo.InvariantCulture));
                        targetMb = Math.Round(Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture));
                        memoryPercent = targetMb > 0 ? Math.Round(100 * memoryMb.Value / targetMb.Value, 1) : null;
                    }

                    return true;
                });
            }

            var (sessions, requests) = await Try("Sessions", async () =>
            {
                await using var command = Command(connection, "SELECT (SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE is_user_process = 1), " +
                                                              "(SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id <> @@SPID AND session_id > 50)");
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                return ((int?)reader.GetInt32(0), (int?)reader.GetInt32(1));
            });

            var blocking = await Try("Blocking", async () =>
            {
                await using var command = Command(connection, "SELECT r.session_id, r.blocking_session_id, r.command, ISNULL(r.wait_type, ''), r.wait_time / 1000.0, ISNULL(DB_NAME(r.database_id), '') " +
                                                              "FROM sys.dm_exec_requests AS r WHERE r.blocking_session_id <> 0 ORDER BY r.wait_time DESC");
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var list = new List<BlockingHealth>();
                while (await reader.ReadAsync(cancellationToken))
                    list.Add(new BlockingHealth(reader.GetInt16(0), reader.GetInt16(1), reader.GetString(2), reader.GetString(3),
                        Math.Round(Convert.ToDouble(reader.GetValue(4), CultureInfo.InvariantCulture), 1), reader.GetString(5)));
                return (IReadOnlyList<BlockingHealth>)list;
            }) ?? [];

            var waits = await Try("Waits", async () =>
            {
                var view = isAzureDatabase ? "sys.dm_db_wait_stats" : "sys.dm_os_wait_stats";
                await using var command = Command(connection, $"WITH w AS (SELECT wait_type, wait_time_ms, waiting_tasks_count FROM {view} WHERE wait_time_ms > 0 AND wait_type NOT IN ({BenignWaits}) " +
                                                              "AND wait_type NOT LIKE 'SLEEP%' AND wait_type NOT LIKE 'PREEMPTIVE_OS%') " +
                                                              "SELECT TOP (8) wait_type, wait_time_ms / 1000.0, waiting_tasks_count, 100.0 * wait_time_ms / NULLIF(SUM(wait_time_ms) OVER (), 0) FROM w ORDER BY wait_time_ms DESC");
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var list = new List<WaitHealth>();
                while (await reader.ReadAsync(cancellationToken))
                    list.Add(new WaitHealth(reader.GetString(0), Math.Round(Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture)), reader.GetInt64(2),
                        reader.IsDBNull(3) ? 0 : Math.Round(Convert.ToDouble(reader.GetValue(3), CultureInfo.InvariantCulture), 1)));
                return (IReadOnlyList<WaitHealth>)list;
            }) ?? [];

            string? recovery = null;
            DateTimeOffset? lastFull = null, lastLog = null;
            IReadOnlyList<GrowthPoint> growth = [];
            if (!isAzureDatabase)
            {
                recovery = await Try("Recovery model", async () => (string?)await Command(connection, "SELECT recovery_model_desc FROM sys.databases WHERE database_id = DB_ID()").ExecuteScalarAsync(cancellationToken));
                await Try("Backups", async () =>
                {
                    await using var command = Command(connection, "SELECT MAX(CASE WHEN type = 'D' THEN backup_finish_date END), MAX(CASE WHEN type = 'L' THEN backup_finish_date END) " +
                                                                  "FROM msdb.dbo.backupset WHERE database_name = DB_NAME()");
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        lastFull = reader.IsDBNull(0) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Local));
                        lastLog = reader.IsDBNull(1) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Local));
                    }

                    return true;
                });
                growth = await Try("Growth trend", async () =>
                {
                    await using var command = Command(connection, "SELECT CAST(backup_finish_date AS date), MAX(backup_size) / 1048576.0 FROM msdb.dbo.backupset " +
                                                                  "WHERE database_name = DB_NAME() AND type = 'D' AND backup_finish_date > DATEADD(day, -90, GETDATE()) " +
                                                                  "GROUP BY CAST(backup_finish_date AS date) ORDER BY 1");
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    var list = new List<GrowthPoint>();
                    while (await reader.ReadAsync(cancellationToken))
                        list.Add(new GrowthPoint(DateOnly.FromDateTime(reader.GetDateTime(0)), Math.Round(Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture), 1), "Full backup size"));
                    return (IReadOnlyList<GrowthPoint>)list;
                }) ?? [];
            }

            Sample(now, files.Where(file => file.Type != "LOG").Sum(file => file.UsedMb ?? file.SizeMb));
            if (growth.Count < 2) growth = [.. growth, .. _sessionSamples];

            return new DatabaseHealthSnapshot
            {
                CapturedAtUtc = now, Provider = settings.Provider, Server = settings.DisplayServer, Database = settings.DatabaseName,
                Version = version, Edition = edition, HasServerState = hasState, PermissionName = permission, LoginName = login,
                Files = files, Volumes = volumes, SqlCpuPercent = sqlCpu, SystemCpuPercent = systemCpu, SqlMemoryMb = memoryMb, TargetMemoryMb = targetMb,
                MemoryPercent = memoryPercent, UserSessions = sessions, ActiveRequests = requests, Blocking = blocking, TopWaits = waits,
                RecoveryModel = recovery, LastFullBackupUtc = lastFull?.ToUniversalTime(), LastLogBackupUtc = lastLog?.ToUniversalTime(), BackupsManagedByPlatform = isAzureDatabase,
                Growth = growth, Limitations = limitations
            };
        }
    }

    public async Task<IReadOnlyList<TableCoverage>> CountTablesAsync(CancellationToken cancellationToken = default)
    {
        var settings = status.Settings;
        var counts = new List<TableCoverage>();
        if (settings.Provider == DatabaseProvider.SQLite)
        {
            await using var connection = connections.CreateSqlite(settings, allowCreate: false);
            await connection.OpenAsync(cancellationToken);
            var names = new List<string>();
            await using (var list = connection.CreateCommand())
            {
                list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE '%.%' ORDER BY name;";
                await using var reader = await list.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) names.Add(reader.GetString(0));
            }

            foreach (var name in names)
            {
                await using var count = connection.CreateCommand();
                count.CommandText = $"SELECT COUNT(*) FROM \"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\";";
                var rows = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
                var dot = name.IndexOf('.', StringComparison.Ordinal);
                counts.Add(DataCoverage.Classify(name[..dot], name[(dot + 1)..]) with { Rows = rows });
            }
        }
        else if (settings.IsServerProvider)
        {
            await using var connection = await connections.CreateSqlServerAsync(settings, master: false, cancellationToken);
            await connection.OpenAsync(cancellationToken);
            await using var command = Command(connection,
                "SELECT s.name, t.name, SUM(p.rows) FROM sys.tables AS t JOIN sys.schemas AS s ON s.schema_id = t.schema_id " +
                "JOIN sys.partitions AS p ON p.object_id = t.object_id AND p.index_id IN (0, 1) WHERE t.is_ms_shipped = 0 AND t.temporal_type <> 1 " +
                "GROUP BY s.name, t.name ORDER BY s.name, t.name");
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                counts.Add(DataCoverage.Classify(reader.GetString(0), reader.GetString(1)) with { Rows = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture) });
        }

        return counts;
    }

    public async Task<TablePage> ReadTableAsync(string schema, string table, int offset, int take, string? filter = null, CancellationToken cancellationToken = default)
    {
        // Only names the database itself reports are used in SQL (no user text reaches the statement except as a parameter).
        var known = (await CountTablesAsync(cancellationToken)).FirstOrDefault(item =>
            string.Equals(item.Schema, schema, StringComparison.OrdinalIgnoreCase) && string.Equals(item.Table, table, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{schema}.{table} is not a table of this database.");
        offset = Math.Max(0, offset);
        take = Math.Clamp(take, 1, 1000);
        var settings = status.Settings;
        System.Data.Common.DbConnection connection = settings.Provider == DatabaseProvider.SQLite
            ? connections.CreateSqlite(settings, allowCreate: false)
            : await connections.CreateSqlServerAsync(settings, master: false, cancellationToken);
        await using (connection)
        {
            await connection.OpenAsync(cancellationToken);
            var sqlite = settings.Provider == DatabaseProvider.SQLite;
            var target = sqlite ? $"\"{known.Schema}.{known.Table}\"" : $"[{known.Schema}].[{known.Table}]";
            var columns = new List<string>();
            await using (var probe = connection.CreateCommand())
            {
                probe.CommandText = sqlite ? $"SELECT * FROM {target} LIMIT 0;" : $"SELECT TOP (0) * FROM {target};";
                await using var reader = await probe.ExecuteReaderAsync(cancellationToken);
                for (var index = 0; index < reader.FieldCount; index++) columns.Add(reader.GetName(index));
            }

            var textColumns = columns.Where(column => !TablePage.IsSecretColumn(column)).ToArray();
            var where = string.IsNullOrWhiteSpace(filter) || textColumns.Length == 0
                ? string.Empty
                : " WHERE " + string.Join(" OR ", textColumns.Select(column => sqlite
                    ? $"CAST(\"{column}\" AS TEXT) LIKE @filter"
                    : $"CAST([{column}] AS NVARCHAR(4000)) LIKE @filter"));
            void Filter(System.Data.Common.DbCommand command)
            {
                if (where.Length > 0) DatabaseConnectionFactory.Parameter(command, "@filter", $"%{filter!.Trim()}%");
            }

            long total;
            await using (var count = connection.CreateCommand())
            {
                count.CommandText = $"SELECT COUNT(*) FROM {target}{where};";
                Filter(count);
                total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            }

            var rows = new List<IReadOnlyList<string?>>();
            await using (var page = connection.CreateCommand())
            {
                page.CommandText = sqlite
                    ? $"SELECT * FROM {target}{where} LIMIT {take} OFFSET {offset};"
                    : $"SELECT * FROM {target}{where} ORDER BY (SELECT NULL) OFFSET {offset} ROWS FETCH NEXT {take} ROWS ONLY;";
                Filter(page);
                await using var reader = await page.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var row = new string?[reader.FieldCount];
                    for (var index = 0; index < reader.FieldCount; index++)
                        row[index] = reader.IsDBNull(index) ? null
                            : TablePage.IsSecretColumn(columns[index]) ? "••••••"
                            : reader.GetValue(index) is byte[] bytes ? $"(binary, {bytes.Length} bytes)"
                            : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture);
                    rows.Add(row);
                }
            }

            return new TablePage(known.Schema, known.Table, columns, rows, total, offset);
        }
    }

    /// <summary>Keeps one used-size sample per day of this session (the trend when no backup history is available).</summary>
    private void Sample(DateTimeOffset now, double sizeMb)
    {
        var day = DateOnly.FromDateTime(now.LocalDateTime);
        lock (_sessionSamples)
        {
            _sessionSamples.RemoveAll(point => point.Date == day);
            _sessionSamples.Add(new GrowthPoint(day, Math.Round(sizeMb, 1), "Measured"));
            if (_sessionSamples.Count > 90) _sessionSamples.RemoveAt(0);
        }
    }

    private static SqlCommand Command(SqlConnection connection, string text) => new(text, connection) { CommandTimeout = 15 };
}
