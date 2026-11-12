using EngineeringWorkspace.Domain.Auditing;

namespace EngineeringWorkspace.Application.Auditing;

/// <summary>
/// Records business audit events. Separate from diagnostic logging. Implementations must redact values,
/// must never throw for persistence failures (they log them instead) and must never record secrets.
/// </summary>
public interface IAuditService
{
    Task RecordAsync(
        string eventType,
        AuditOutcome outcome,
        string summary,
        IReadOnlyDictionary<string, string?>? properties = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Reads recent audit events for the Auditing and Logs page.</summary>
public interface IAuditReader
{
    Task<IReadOnlyList<AuditEvent>> ReadRecentAsync(int maximumCount, CancellationToken cancellationToken = default);
}

/// <summary>
/// Ambient audit context (actor, current workspace and customer, session correlation ID) maintained by the
/// workspace and customer-context services so audit events carry it without every caller passing it.
/// </summary>
public sealed class AuditContext
{
    private readonly Lock _gate = new();
    private string? _workspaceId;
    private string? _customerId;

    public AuditContext(string actor)
    {
        Actor = string.IsNullOrWhiteSpace(actor) ? "unknown" : actor;
        SessionCorrelationId = Guid.NewGuid().ToString("N");
    }

    public string Actor { get; }
    public string SessionCorrelationId { get; }

    public string? WorkspaceId
    {
        get { lock (_gate) return _workspaceId; }
        set { lock (_gate) _workspaceId = value; }
    }

    public string? CustomerId
    {
        get { lock (_gate) return _customerId; }
        set { lock (_gate) _customerId = value; }
    }
}
