using System.Text.Json;
using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Domain.Auditing;
using EngineeringWorkspace.Infrastructure.Database;
using EngineeringWorkspace.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Infrastructure.Auditing;

/// <summary>Ledger categories: what kind of record an audit event is.</summary>
public static class AuditLedgerCategories
{
    public const string Audit = nameof(Audit);
    public const string Approval = nameof(Approval);
    public const string Deployment = nameof(Deployment);
    public const string CredentialUse = nameof(CredentialUse);
    public const string Access = nameof(Access);

    public static string For(string eventType) => eventType switch
    {
        AuditEventTypes.TenantChangeApplied or AuditEventTypes.TenantChangeFailed => Deployment,
        AuditEventTypes.TenantCredentialUsed => CredentialUse,
        AuditEventTypes.UserRoleChanged or AuditEventTypes.SchedulingComputerChanged => Access,
        _ when eventType.Contains("Approv", StringComparison.OrdinalIgnoreCase) || eventType.Contains("ChangeRequest", StringComparison.OrdinalIgnoreCase) => Approval,
        _ => Audit
    };
}

/// <summary>
/// Writes every audit event to the local JSON-lines file (as before) and, when the application database is ready, to the
/// append-only secureplus.AuditLedger (SQL Server 2022 ledger table), so people with SQL rights cannot alter or delete the
/// audit log, approvals, deployment history or credential use. Ledger failures are logged, never thrown.
/// </summary>
public sealed class LedgerAuditService(
    JsonLinesAuditService file,
    Lazy<IDatabaseStatusService> status,
    DatabaseConnectionFactory connections,
    AuditContext context,
    IRedactionService redaction,
    TimeProvider timeProvider,
    ILogger<LedgerAuditService> logger,
    string? computerName = null) : IAuditService, IAuditReader
{
    private readonly string _computer = computerName ?? Environment.MachineName;
    private IDatabaseStatusService database => status.Value;

    public async Task RecordAsync(string eventType, AuditOutcome outcome, string summary, IReadOnlyDictionary<string, string?>? properties = null,
        CancellationToken cancellationToken = default)
    {
        await file.RecordAsync(eventType, outcome, summary, properties, cancellationToken);
        if (database.Settings.Provider == DatabaseProvider.None || database.LatestStatus?.State != DatabaseOverallState.Ready) return;
        try
        {
            var safe = (properties ?? new Dictionary<string, string?>()).ToDictionary(pair => pair.Key, pair => redaction.RedactValue(pair.Key, pair.Value), StringComparer.Ordinal);
            await using var session = await SqlSession.OpenAsync(database, connections, message => new InvalidOperationException(message), cancellationToken);
            await using var command = session.Command(null,
                $"INSERT INTO {session.Table("secureplus", "AuditLedger")} (EventId, OccurredAtUtc, Category, EventType, Outcome, Actor, ComputerName, WorkspaceId, CustomerId, TenantId, Summary, PropertiesJson) " +
                "VALUES (@id, @at, @category, @type, @outcome, @actor, @computer, @workspace, @customer, @tenant, @summary, @json);");
            DatabaseConnectionFactory.Parameter(command, "@id", session.Sqlite ? Guid.NewGuid().ToString() : Guid.NewGuid());
            DatabaseConnectionFactory.Parameter(command, "@at", session.Date(timeProvider.GetUtcNow()));
            DatabaseConnectionFactory.Parameter(command, "@category", AuditLedgerCategories.For(eventType));
            DatabaseConnectionFactory.Parameter(command, "@type", Cut(eventType, 100));
            DatabaseConnectionFactory.Parameter(command, "@outcome", outcome.ToString());
            DatabaseConnectionFactory.Parameter(command, "@actor", Cut(context.Actor, 256));
            DatabaseConnectionFactory.Parameter(command, "@computer", Cut(_computer, 128));
            DatabaseConnectionFactory.Parameter(command, "@workspace", Cut(context.WorkspaceId, 64));
            DatabaseConnectionFactory.Parameter(command, "@customer", Cut(safe.GetValueOrDefault("Customer") ?? context.CustomerId, 64));
            DatabaseConnectionFactory.Parameter(command, "@tenant", Cut(safe.GetValueOrDefault("Tenant") ?? safe.GetValueOrDefault("TenantId"), 64));
            DatabaseConnectionFactory.Parameter(command, "@summary", Cut(redaction.RedactText(summary), 1000));
            DatabaseConnectionFactory.Parameter(command, "@json", safe.Count == 0 ? null : JsonSerializer.Serialize(safe));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            logger.LogWarning(exception, "Audit event {EventType} could not be written to the audit ledger", eventType);
        }
    }

    public Task<IReadOnlyList<AuditEvent>> ReadRecentAsync(int maximumCount, CancellationToken cancellationToken = default) =>
        file.ReadRecentAsync(maximumCount, cancellationToken);

    private static string? Cut(string? value, int length) => value is null ? null : value.Length > length ? value[..length] : value;
}
