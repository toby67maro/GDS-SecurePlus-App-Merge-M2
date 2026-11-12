namespace EngineeringWorkspace.Domain.Auditing;

public enum AuditOutcome
{
    Success,
    Failure,
    Warning,
    Information
}

/// <summary>
/// A business-level record of something an engineer or the application did. Audit is separate from
/// diagnostic logging. Properties must never contain secret values; the audit service redacts them anyway.
/// </summary>
public sealed record AuditEvent(
    Guid AuditEventId,
    string EventType,
    DateTimeOffset TimestampUtc,
    string Actor,
    string? WorkspaceId,
    string? CustomerId,
    string CorrelationId,
    AuditOutcome Outcome,
    string Summary,
    IReadOnlyDictionary<string, string?> Properties);

/// <summary>Stable audit event type names.</summary>
public static class AuditEventTypes
{
    public const string ApplicationStarted = nameof(ApplicationStarted);
    public const string ApplicationStopped = nameof(ApplicationStopped);
    public const string WorkspaceOpened = nameof(WorkspaceOpened);
    public const string WorkspaceClosed = nameof(WorkspaceClosed);
    public const string CustomerSelected = nameof(CustomerSelected);
    public const string ConfigurationLoaded = nameof(ConfigurationLoaded);
    public const string ConfigurationValidationFailed = nameof(ConfigurationValidationFailed);
    public const string RouteChanged = nameof(RouteChanged);
    public const string ThemeChanged = nameof(ThemeChanged);
    public const string ConfigurationChanged = nameof(ConfigurationChanged);
    public const string ConfigurationExported = nameof(ConfigurationExported);

    public const string DatabaseConfigurationChanged = nameof(DatabaseConfigurationChanged);
    public const string DatabaseConnectionTested = nameof(DatabaseConnectionTested);
    public const string DatabaseCreated = nameof(DatabaseCreated);
    public const string DatabaseInitialized = nameof(DatabaseInitialized);
    public const string DatabaseMigrationStarted = nameof(DatabaseMigrationStarted);
    public const string DatabaseMigrationCompleted = nameof(DatabaseMigrationCompleted);
    public const string DatabaseMigrationFailed = nameof(DatabaseMigrationFailed);

    public const string TenantAppRegistrationCreated = nameof(TenantAppRegistrationCreated);
    public const string TenantAppRegistrationUpdated = nameof(TenantAppRegistrationUpdated);
    public const string TenantConnectionTested = nameof(TenantConnectionTested);
    public const string TenantAuthenticationSucceeded = nameof(TenantAuthenticationSucceeded);
    public const string TenantAuthenticationFailed = nameof(TenantAuthenticationFailed);
    public const string TenantPermissionValidationSucceeded = nameof(TenantPermissionValidationSucceeded);
    public const string TenantPermissionValidationFailed = nameof(TenantPermissionValidationFailed);

    public const string CredentialCreated = nameof(CredentialCreated);
    public const string CredentialUpdated = nameof(CredentialUpdated);
    public const string CredentialDeleted = nameof(CredentialDeleted);
    public const string CredentialExpiring = nameof(CredentialExpiring);
    public const string CredentialExpired = nameof(CredentialExpired);
    public const string TenantChangeApplied = nameof(TenantChangeApplied);
    public const string TenantChangeFailed = nameof(TenantChangeFailed);
    public const string TenantCredentialUsed = nameof(TenantCredentialUsed);
    public const string UserRoleChanged = nameof(UserRoleChanged);
    public const string SchedulingComputerChanged = nameof(SchedulingComputerChanged);
}
