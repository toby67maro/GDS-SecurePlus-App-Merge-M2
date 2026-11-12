using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Domain.Security;

namespace EngineeringWorkspace.Application.Database;

/// <summary>Database provider. EaaS v3 specifies SQL Server 2022 / Azure SQL; SQLite is a Secure Plus extension.</summary>
public enum DatabaseProvider
{
    None,
    SQLite,
    SqlServer,
    AzureSql
}

/// <summary>
/// Database authentication. Microsoft Entra methods acquire tokens with MSAL and pass them to SqlClient
/// through its access-token callback, which the core Microsoft.Data.SqlClient 7 driver supports.
/// </summary>
public enum DatabaseAuthenticationMethod
{
    None,
    WindowsIntegrated,
    SqlLogin,
    EntraInteractive,
    EntraServicePrincipal,
    EntraManagedIdentity
}

/// <summary>[Database] in Database.ini. Contains secret references only, never passwords.</summary>
public sealed record DatabaseSettings
{
    public DatabaseProvider Provider { get; init; }
    public string ServerName { get; init; } = string.Empty;
    public string? SqlInstance { get; init; }
    public int? Port { get; init; }
    public string DatabaseName { get; init; } = string.Empty;
    public DatabaseAuthenticationMethod Authentication { get; init; }
    public string? UserName { get; init; }
    public SecretReference? PasswordSecretReference { get; init; }
    public string? EntraClientId { get; init; }
    public string? EntraTenantId { get; init; }
    public SecretReference? EntraClientSecretReference { get; init; }
    public string? ManagedIdentityClientId { get; init; }
    public bool EncryptConnection { get; init; } = true;
    public bool TrustServerCertificate { get; init; }
    public int ConnectionTimeoutSeconds { get; init; } = 30;
    public int CommandTimeoutSeconds { get; init; } = 60;
    public int RetryCount { get; init; } = 3;
    public int RetryDelaySeconds { get; init; } = 10;
    public SecretReference? ConnectionStringSecretReference { get; init; }
    public bool AutomaticDatabaseInitialization { get; init; }
    public bool AutomaticSchemaUpgrade { get; init; }

    public bool IsServerProvider => Provider is DatabaseProvider.SqlServer or DatabaseProvider.AzureSql;

    /// <summary>Display form of the server for status, never including credentials.</summary>
    public string DisplayServer => Provider == DatabaseProvider.SQLite
        ? "(local file)"
        : string.Concat(ServerName, string.IsNullOrWhiteSpace(SqlInstance) ? string.Empty : $"\\{SqlInstance}", Port is null ? string.Empty : $",{Port}");
}

/// <summary>Authentication methods valid for each provider.</summary>
public static class DatabaseAuthenticationMethods
{
    public static IReadOnlyList<DatabaseAuthenticationMethod> For(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer => [DatabaseAuthenticationMethod.WindowsIntegrated, DatabaseAuthenticationMethod.SqlLogin,
            DatabaseAuthenticationMethod.EntraInteractive, DatabaseAuthenticationMethod.EntraServicePrincipal],
        DatabaseProvider.AzureSql => [DatabaseAuthenticationMethod.EntraInteractive, DatabaseAuthenticationMethod.EntraServicePrincipal,
            DatabaseAuthenticationMethod.EntraManagedIdentity, DatabaseAuthenticationMethod.SqlLogin],
        DatabaseProvider.SQLite => [DatabaseAuthenticationMethod.None],
        _ => [DatabaseAuthenticationMethod.None]
    };
}

/// <summary>Overall database readiness, as shown in the status panel and status bar.</summary>
public enum DatabaseOverallState
{
    NotConfigured,
    Disconnected,
    Testing,
    ConnectionFailed,
    DatabaseMissing,
    SchemaMissing,
    MigrationRequired,
    IncompatibleSchema,
    Ready
}

/// <summary>State of the Secure Plus schema in a database.</summary>
public enum SchemaState
{
    Unknown,
    Missing,
    Current,
    MigrationRequired,
    Newer,
    Incompatible
}

/// <summary>Failure categories with fixed, actionable messages.</summary>
public enum DatabaseFailureCategory
{
    None,
    Configuration,
    CannotReachServer,
    LoginFailed,
    DatabaseMissing,
    NoAccessToDatabase,
    CannotCreateDatabase,
    CannotCreateSchemaObjects,
    EncryptionOrCertificate,
    NetworkOrTimeout,
    SchemaIncompatible,
    CredentialUnavailable,
    Unexpected
}

/// <summary>One structured readiness check.</summary>
public sealed record DatabaseCheck(string Name, CheckState State, string Message);

/// <summary>A complete, sanitized database status report.</summary>
public sealed record DatabaseStatusReport
{
    public required DatabaseOverallState State { get; init; }
    public required string Provider { get; init; }
    public required string Server { get; init; }
    public required string Database { get; init; }
    public required string Authentication { get; init; }
    public required bool Encrypted { get; init; }
    public bool? DatabaseExists { get; init; }
    public SchemaState SchemaState { get; init; } = SchemaState.Unknown;
    public int? CurrentSchemaVersion { get; init; }
    public required int RequiredSchemaVersion { get; init; }
    public string? ServerVersion { get; init; }
    public DateTimeOffset? LastSuccessfulConnectionUtc { get; init; }
    public DateTimeOffset? LastAttemptUtc { get; init; }
    public DatabaseFailureCategory FailureCategory { get; init; }
    public string? SanitizedError { get; init; }
    public string? RecommendedAction { get; init; }
    public IReadOnlyList<string> SchemaFindings { get; init; } = [];

    // Readiness checklist (structured, not display strings).
    public CheckState ConfigurationCheck { get; init; } = CheckState.NotRun;
    public CheckState ServerCheck { get; init; } = CheckState.NotRun;
    public CheckState AuthenticationCheck { get; init; } = CheckState.NotRun;
    public CheckState DatabaseCheck { get; init; } = CheckState.NotRun;
    public CheckState DatabaseAccessCheck { get; init; } = CheckState.NotRun;
    public CheckState SchemaPresentCheck { get; init; } = CheckState.NotRun;
    public CheckState SchemaCompatibleCheck { get; init; } = CheckState.NotRun;
    public CheckState PermissionCheck { get; init; } = CheckState.NotRun;

    /// <summary>Database permissions the signed-in principal has (true) or lacks (false), for example "SELECT" or "CREATE TABLE".</summary>
    public IReadOnlyDictionary<string, bool> Permissions { get; init; } = new Dictionary<string, bool>();

    /// <summary>True when the schema can be repaired by re-applying the idempotent migration scripts.</summary>
    public bool CanRepairSchema => SchemaState is SchemaState.Incompatible or SchemaState.MigrationRequired or SchemaState.Missing;

    public CheckState OverallCheck => State == DatabaseOverallState.Ready ? CheckState.Passed
        : State is DatabaseOverallState.NotConfigured or DatabaseOverallState.Disconnected or DatabaseOverallState.Testing ? CheckState.NotRun
        : CheckState.Failed;

    public string MigrationState => SchemaState switch
    {
        SchemaState.Current => "Up to date",
        SchemaState.MigrationRequired => "Migration required",
        SchemaState.Missing => "Not initialized",
        SchemaState.Newer => "Database schema is newer than this application supports",
        SchemaState.Incompatible => "Incompatible",
        _ => "Unknown"
    };
}

/// <summary>Database operations. Every operation is separate, explicit, cancellable and runs off the UI thread.</summary>
public interface IDatabaseProvisioningService
{
    int RequiredSchemaVersion { get; }

    /// <summary>Connects to the server only (SQL Server: master database; SQLite: opens without creating the file).</summary>
    Task<DatabaseStatusReport> TestServerConnectionAsync(DatabaseSettings settings, IProgress<string>? progress, CancellationToken cancellationToken);

    /// <summary>Checks that the database exists and can be opened.</summary>
    Task<DatabaseStatusReport> CheckDatabaseAsync(DatabaseSettings settings, IProgress<string>? progress, CancellationToken cancellationToken);

    /// <summary>Creates the database. Callers must obtain explicit confirmation first.</summary>
    Task<DatabaseStatusReport> CreateDatabaseAsync(DatabaseSettings settings, IProgress<string>? progress, CancellationToken cancellationToken);

    /// <summary>Read-only validation of the schema version and expected objects.</summary>
    Task<DatabaseStatusReport> ValidateSchemaAsync(DatabaseSettings settings, IProgress<string>? progress, CancellationToken cancellationToken);

    /// <summary>Applies pending migrations transactionally. Never drops, overwrites or downgrades objects.</summary>
    Task<DatabaseStatusReport> InitializeSchemaAsync(DatabaseSettings settings, IProgress<string>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Re-applies every migration script (each statement only creates missing objects), records missing versions and
    /// adds missing nullable columns. Never drops, alters or overwrites existing objects or data.
    /// </summary>
    Task<DatabaseStatusReport> RepairSchemaAsync(DatabaseSettings settings, IProgress<string>? progress, CancellationToken cancellationToken);

    /// <summary>Read-only full status (server, database, schema). Safe to call at startup.</summary>
    Task<DatabaseStatusReport> GetStatusAsync(DatabaseSettings settings, IProgress<string>? progress, CancellationToken cancellationToken);
}
