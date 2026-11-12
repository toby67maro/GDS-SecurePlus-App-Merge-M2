namespace EngineeringWorkspace.Application.Database;

public enum HealthSeverity
{
    Ok,
    Information,
    Warning,
    Critical
}

/// <summary>A data or log file of the application database.</summary>
public sealed record DatabaseFileHealth(string Name, string Type, string Location, double SizeMb, double? UsedMb, string MaxSize, string Growth)
{
    public double? UsedPercent => UsedMb is { } used && SizeMb > 0 ? Math.Round(100 * used / SizeMb, 1) : null;
}

/// <summary>A disk volume holding database files (or, for Azure SQL Database, the database's maximum size).</summary>
public sealed record VolumeHealth(string Volume, double TotalGb, double FreeGb)
{
    public double FreePercent => TotalGb <= 0 ? 0 : Math.Round(100 * FreeGb / TotalGb, 1);
}

public sealed record WaitHealth(string WaitType, double WaitSeconds, long WaitingTasks, double Percent);

public sealed record BlockingHealth(int SessionId, int BlockedBy, string Command, string WaitType, double WaitSeconds, string Database);

public sealed record GrowthPoint(DateOnly Date, double SizeMb, string Source);

public sealed record HealthAlert(HealthSeverity Severity, string Area, string Message);

/// <summary>
/// One capture of SQL Server / Azure SQL health for the application database. Values that could not be read (for
/// example without VIEW SERVER STATE) are null and explained in <see cref="Limitations"/>.
/// </summary>
public sealed record DatabaseHealthSnapshot
{
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public required DatabaseProvider Provider { get; init; }
    public required string Server { get; init; }
    public required string Database { get; init; }
    public string? Version { get; init; }
    public string? Edition { get; init; }
    public bool HasServerState { get; init; }
    public string? PermissionName { get; init; }
    public string? LoginName { get; init; }
    public IReadOnlyList<DatabaseFileHealth> Files { get; init; } = [];
    public IReadOnlyList<VolumeHealth> Volumes { get; init; } = [];
    public double? SqlCpuPercent { get; init; }
    public double? SystemCpuPercent { get; init; }
    public double? SqlMemoryMb { get; init; }
    public double? TargetMemoryMb { get; init; }
    public double? MemoryPercent { get; init; }
    public int? UserSessions { get; init; }
    public int? ActiveRequests { get; init; }
    public IReadOnlyList<BlockingHealth> Blocking { get; init; } = [];
    public IReadOnlyList<WaitHealth> TopWaits { get; init; } = [];
    public string? RecoveryModel { get; init; }
    public DateTimeOffset? LastFullBackupUtc { get; init; }
    public DateTimeOffset? LastLogBackupUtc { get; init; }
    public bool BackupsManagedByPlatform { get; init; }
    public IReadOnlyList<GrowthPoint> Growth { get; init; } = [];
    public IReadOnlyList<string> Limitations { get; init; } = [];
    public string? Error { get; init; }

    public double DataSizeMb => Files.Where(file => file.Type != "LOG").Sum(file => file.SizeMb);
    public double LogSizeMb => Files.Where(file => file.Type == "LOG").Sum(file => file.SizeMb);
}

/// <summary>Warning and critical thresholds of the Database Health page.</summary>
public sealed record HealthThresholds
{
    public double DiskFreeWarningPercent { get; init; } = 15;
    public double DiskFreeCriticalPercent { get; init; } = 5;
    public double CpuWarningPercent { get; init; } = 80;
    public double CpuCriticalPercent { get; init; } = 95;
    public double MemoryWarningPercent { get; init; } = 90;
    public double LogUsedWarningPercent { get; init; } = 80;
    public double LogUsedCriticalPercent { get; init; } = 95;
    public double BlockingWarningSeconds { get; init; } = 30;
    public double BlockingCriticalSeconds { get; init; } = 300;
    public TimeSpan FullBackupWarningAge { get; init; } = TimeSpan.FromDays(1);
    public TimeSpan FullBackupCriticalAge { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan LogBackupWarningAge { get; init; } = TimeSpan.FromHours(4);

    public static HealthThresholds Default { get; } = new();
}

/// <summary>Turns a snapshot into alerts. Pure, so the thresholds are unit-tested.</summary>
public static class HealthEvaluator
{
    public static IReadOnlyList<HealthAlert> Evaluate(DatabaseHealthSnapshot snapshot, HealthThresholds? thresholds = null, DateTimeOffset? now = null)
    {
        var limits = thresholds ?? HealthThresholds.Default;
        var at = now ?? snapshot.CapturedAtUtc;
        var alerts = new List<HealthAlert>();
        if (snapshot.Error is { } error)
        {
            alerts.Add(new HealthAlert(HealthSeverity.Critical, "Connection", error));
            return alerts;
        }

        foreach (var volume in snapshot.Volumes)
        {
            if (volume.FreePercent < limits.DiskFreeCriticalPercent)
                alerts.Add(new(HealthSeverity.Critical, "Disk", $"{volume.Volume} has {volume.FreePercent:0.#}% free ({volume.FreeGb:N1} GB), below {limits.DiskFreeCriticalPercent:0.#}%."));
            else if (volume.FreePercent < limits.DiskFreeWarningPercent)
                alerts.Add(new(HealthSeverity.Warning, "Disk", $"{volume.Volume} has {volume.FreePercent:0.#}% free ({volume.FreeGb:N1} GB), below {limits.DiskFreeWarningPercent:0.#}%."));
        }

        foreach (var log in snapshot.Files.Where(file => file.Type == "LOG" && file.UsedPercent is not null))
        {
            if (log.UsedPercent >= limits.LogUsedCriticalPercent)
                alerts.Add(new(HealthSeverity.Critical, "Log", $"Log file {log.Name} is {log.UsedPercent:0.#}% used."));
            else if (log.UsedPercent >= limits.LogUsedWarningPercent)
                alerts.Add(new(HealthSeverity.Warning, "Log", $"Log file {log.Name} is {log.UsedPercent:0.#}% used."));
        }

        var cpu = snapshot.SqlCpuPercent;
        if (cpu >= limits.CpuCriticalPercent) alerts.Add(new(HealthSeverity.Critical, "CPU", $"SQL Server CPU is {cpu:0}%."));
        else if (cpu >= limits.CpuWarningPercent) alerts.Add(new(HealthSeverity.Warning, "CPU", $"SQL Server CPU is {cpu:0}%."));
        if (snapshot.MemoryPercent >= limits.MemoryWarningPercent)
            alerts.Add(new(HealthSeverity.Warning, "Memory", $"Memory use is {snapshot.MemoryPercent:0}% of the target."));

        if (snapshot.Blocking.Count > 0)
        {
            var longest = snapshot.Blocking.Max(block => block.WaitSeconds);
            var severity = longest >= limits.BlockingCriticalSeconds ? HealthSeverity.Critical : longest >= limits.BlockingWarningSeconds ? HealthSeverity.Warning : HealthSeverity.Information;
            alerts.Add(new(severity, "Blocking", $"{snapshot.Blocking.Count} blocked request(s); the longest has waited {longest:N0} s."));
        }

        if (snapshot.Provider == DatabaseProvider.SqlServer && !snapshot.BackupsManagedByPlatform)
        {
            if (snapshot.LastFullBackupUtc is not { } full)
                alerts.Add(new(HealthSeverity.Warning, "Backup", "No full backup of the database is recorded."));
            else if (at - full > limits.FullBackupCriticalAge)
                alerts.Add(new(HealthSeverity.Critical, "Backup", $"The last full backup is {(at - full).TotalDays:N0} day(s) old."));
            else if (at - full > limits.FullBackupWarningAge)
                alerts.Add(new(HealthSeverity.Warning, "Backup", $"The last full backup is {(at - full).TotalHours:N0} hour(s) old."));
            if (snapshot.RecoveryModel is "FULL" or "BULK_LOGGED" && (snapshot.LastLogBackupUtc is not { } log || at - log > limits.LogBackupWarningAge))
                alerts.Add(new(HealthSeverity.Warning, "Backup", $"The database uses the {snapshot.RecoveryModel} recovery model but the log has not been backed up in the last {limits.LogBackupWarningAge.TotalHours:0} hours."));
        }

        if (!snapshot.HasServerState && snapshot.Provider != DatabaseProvider.SQLite)
            alerts.Add(new(HealthSeverity.Information, "Permissions",
                $"{snapshot.PermissionName ?? "VIEW SERVER STATE"} is not granted, so CPU, memory, sessions, blocking, waits and disk space are partly unavailable."));
        return alerts;
    }

    public static HealthSeverity Overall(IEnumerable<HealthAlert> alerts)
    {
        var max = alerts.Select(alert => alert.Severity).DefaultIfEmpty(HealthSeverity.Ok).Max();
        return max == HealthSeverity.Information ? HealthSeverity.Ok : max;
    }

    /// <summary>T-SQL an administrator runs to give the application's login the read-only monitoring permission.</summary>
    public static string GrantScript(DatabaseHealthSnapshot snapshot)
    {
        var login = string.IsNullOrWhiteSpace(snapshot.LoginName) ? "<login>" : snapshot.LoginName.Replace("]", "]]", StringComparison.Ordinal);
        return snapshot.Provider == DatabaseProvider.AzureSql
            ? $"-- Run in the [{snapshot.Database}] database as an administrator\r\nGRANT VIEW DATABASE STATE TO [{login}];\r\n"
            : $"-- Run on {snapshot.Server} as a sysadmin\r\nUSE [master];\r\nGRANT VIEW SERVER STATE TO [{login}];\r\n";
    }
}

/// <summary>Reads health metrics of the configured database. Read-only; never changes the server.</summary>
public interface IDatabaseHealthService
{
    Task<DatabaseHealthSnapshot> CaptureAsync(CancellationToken cancellationToken = default);

    /// <summary>Row count of every table of the configured database, classified by <see cref="DataCoverage"/>.</summary>
    Task<IReadOnlyList<TableCoverage>> CountTablesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one page of a table (Advanced > Data Explorer). Only tables that exist in the database are read; columns
    /// that look like secrets (secret, password, token, certificate, key material) are masked.
    /// </summary>
    Task<TablePage> ReadTableAsync(string schema, string table, int offset, int take, string? filter = null, CancellationToken cancellationToken = default);
}

/// <summary>A page of rows of one table, as text.</summary>
public sealed record TablePage(string Schema, string Table, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows, long TotalRows, int Offset)
{
    /// <summary>True for a column whose values are never shown (secrets, passwords, tokens, certificates, key material).</summary>
    public static bool IsSecretColumn(string column)
    {
        var name = column.ToLowerInvariant();
        return name.Contains("secret") || name.Contains("password") || name.Contains("token") || name.Contains("certificate") ||
               name.Contains("ciphertext") || name.Contains("encrypted") || name.Contains("privatekey") || name.Contains("wrappedkey") || name is "keymaterial" or "salt" or "nonce";
    }
}

/// <summary>
/// The mode of a SecurePlus database, chosen at first setup and stored in secureplus.DatabaseProperty so every computer
/// agrees. Production: schema upgrades need an explicit confirmation and no sample data is loaded. Test: sample data is
/// allowed, upgrades apply automatically and the header shows a TEST banner.
/// </summary>
public static class DatabaseModes
{
    public const string PropertyKey = "Mode";
    public const string Production = "Production";
    public const string Test = "Test";

    public static bool IsValid(string? mode) => mode is Production or Test;
}
