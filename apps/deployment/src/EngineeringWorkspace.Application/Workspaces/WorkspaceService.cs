using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Application.State;
using EngineeringWorkspace.Domain.Auditing;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Workspaces;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Application.Workspaces;

/// <summary>Raised when the active workspace changes.</summary>
public sealed record WorkspaceContextChanged(Workspace? Previous, Workspace? Current, DateTimeOffset ChangedAtUtc, string Reason);

/// <summary>Persists workspaces (data/Workspaces/{id}/Workspace.ini). Pages never inspect workspace directories.</summary>
public interface IWorkspaceStore
{
    string GetDirectory(WorkspaceId id);
    Task<bool> ExistsAsync(WorkspaceId id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Workspace>> ListAsync(CancellationToken cancellationToken = default);
    Task<Workspace?> LoadAsync(WorkspaceId id, CancellationToken cancellationToken = default);
    Task SaveAsync(Workspace workspace, CancellationToken cancellationToken = default);
}

/// <summary>Exclusively owns the active workspace.</summary>
public interface IWorkspaceService
{
    Workspace? CurrentWorkspace { get; }
    bool HasActiveWorkspace { get; }
    event EventHandler<WorkspaceContextChanged>? WorkspaceChanged;
    Task<OperationResult<Workspace>> OpenAsync(WorkspaceId id, CancellationToken cancellationToken = default);
    Task CloseAsync(CancellationToken cancellationToken = default);
    Task<OperationResult<Workspace>> CreateAsync(string name, CancellationToken cancellationToken = default);
    Task<OperationResult<Workspace?>> RestoreLastAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> SaveAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> SetCurrentCustomerAsync(CustomerId? customerId, Guid? tenantId, CancellationToken cancellationToken = default);
}

public sealed class WorkspaceService(
    IWorkspaceStore store,
    IApplicationStateStore stateStore,
    IAuditService audit,
    AuditContext auditContext,
    WorkspaceSettings settings,
    ApplicationSettings application,
    TimeProvider timeProvider,
    ILogger<WorkspaceService> logger) : IWorkspaceService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Workspace? _current;

    public Workspace? CurrentWorkspace => _current;
    public bool HasActiveWorkspace => _current is not null;
    public event EventHandler<WorkspaceContextChanged>? WorkspaceChanged;

    public async Task<OperationResult<Workspace>> OpenAsync(WorkspaceId id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var workspace = await store.LoadAsync(id, cancellationToken);
            if (workspace is null)
                return OperationResult<Workspace>.Failure("WorkspaceMissing", $"Workspace '{id}' does not exist.", null, "Create the workspace or choose another one.");
            await ActivateAsync(workspace, "Opened", cancellationToken);
            return OperationResult<Workspace>.Success(workspace);
        }
        catch (IOException exception)
        {
            logger.LogError(exception, "Workspace {WorkspaceId} could not be opened", id.Value);
            return OperationResult<Workspace>.Failure("WorkspaceUnreadable", $"Workspace '{id}' could not be read.", exception.GetType().Name, "Check the workspace directory permissions.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var previous = _current;
            if (previous is null) return;
            _current = null;
            auditContext.WorkspaceId = null;
            WorkspaceChanged?.Invoke(this, new WorkspaceContextChanged(previous, null, timeProvider.GetUtcNow(), "Closed"));
            await audit.RecordAsync(AuditEventTypes.WorkspaceClosed, AuditOutcome.Success, $"Workspace '{previous.Name}' closed.",
                new Dictionary<string, string?> { ["WorkspaceId"] = previous.Id.Value }, cancellationToken);
            logger.LogInformation("Workspace {WorkspaceId} closed", previous.Id.Value);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult<Workspace>> CreateAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return OperationResult<Workspace>.Failure("WorkspaceNameRequired", "A workspace name is required.");
        WorkspaceId id;
        try
        {
            id = new WorkspaceId(StableIdentifier.FromDisplayName(name));
        }
        catch (DomainException exception)
        {
            return OperationResult<Workspace>.Failure("WorkspaceNameInvalid", exception.Message, null, "Use letters and digits in the workspace name.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (await store.ExistsAsync(id, cancellationToken))
                return OperationResult<Workspace>.Failure("WorkspaceExists", $"Workspace '{id}' already exists.", null, "Open the existing workspace or choose another name.");
            var workspace = Workspace.Create(id, name.Trim(), store.GetDirectory(id), application.Environment, timeProvider.GetUtcNow());
            await store.SaveAsync(workspace, cancellationToken);
            await ActivateAsync(workspace, "Created", cancellationToken);
            return OperationResult<Workspace>.Success(workspace);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult<Workspace?>> RestoreLastAsync(CancellationToken cancellationToken = default)
    {
        var state = await stateStore.LoadAsync(cancellationToken);
        var candidate = settings.RestoreLastWorkspace ? state.LastWorkspaceId : null;
        candidate ??= string.IsNullOrWhiteSpace(settings.DefaultWorkspace) ? null : settings.DefaultWorkspace;
        if (candidate is null) return OperationResult<Workspace?>.Success(null);
        if (!WorkspaceId.TryCreate(candidate, out var id))
            return OperationResult<Workspace?>.Failure("WorkspaceIdInvalid", $"The startup workspace '{candidate}' is not a valid workspace identifier.");

        if (!await store.ExistsAsync(id, cancellationToken))
        {
            if (!settings.CreateDirectoryIfMissing)
                return OperationResult<Workspace?>.Failure("WorkspaceMissing", $"The startup workspace '{id}' does not exist.", null,
                    "Create it, clear [Workspace] DefaultWorkspace, or set CreateDirectoryIfMissing=true.");
            var created = await CreateAsync(id.Value, cancellationToken);
            return created.Succeeded ? OperationResult<Workspace?>.Success(created.Value) : OperationResult<Workspace?>.Failure(created.Error!);
        }

        var opened = await OpenAsync(id, cancellationToken);
        return opened.Succeeded ? OperationResult<Workspace?>.Success(opened.Value) : OperationResult<Workspace?>.Failure(opened.Error!);
    }

    public async Task<OperationResult> SaveAsync(CancellationToken cancellationToken = default)
    {
        var workspace = _current;
        if (workspace is null) return OperationResult.Success();
        try
        {
            await store.SaveAsync(workspace with { ModifiedAtUtc = timeProvider.GetUtcNow() }, cancellationToken);
            return OperationResult.Success();
        }
        catch (IOException exception)
        {
            logger.LogError(exception, "Workspace {WorkspaceId} could not be saved", workspace.Id.Value);
            return OperationResult.Failure("WorkspaceSaveFailed", "The workspace could not be saved.", exception.GetType().Name, "Check the workspace directory permissions.");
        }
    }

    public async Task<OperationResult> SetCurrentCustomerAsync(CustomerId? customerId, Guid? tenantId, CancellationToken cancellationToken = default)
    {
        var workspace = _current;
        if (workspace is null) return OperationResult.Success();
        _current = workspace with { CurrentCustomerId = customerId, TenantId = tenantId, ModifiedAtUtc = timeProvider.GetUtcNow() };
        return await SaveAsync(cancellationToken);
    }

    private async Task ActivateAsync(Workspace workspace, string reason, CancellationToken cancellationToken)
    {
        var previous = _current;
        _current = workspace;
        auditContext.WorkspaceId = workspace.Id.Value;
        var state = await stateStore.LoadAsync(cancellationToken);
        await stateStore.SaveAsync(state with { LastWorkspaceId = workspace.Id.Value }, cancellationToken);
        WorkspaceChanged?.Invoke(this, new WorkspaceContextChanged(previous, workspace, timeProvider.GetUtcNow(), reason));
        await audit.RecordAsync(AuditEventTypes.WorkspaceOpened, AuditOutcome.Success, $"Workspace '{workspace.Name}' {reason.ToLowerInvariant()}.",
            new Dictionary<string, string?> { ["WorkspaceId"] = workspace.Id.Value, ["Reason"] = reason }, cancellationToken);
        logger.LogInformation("Workspace {WorkspaceId} activated ({Reason})", workspace.Id.Value, reason);
    }
}

/// <summary>Workspaces in the database (secureplus.Workspace), shared by every computer. Directories are local to each computer.</summary>
public interface IWorkspaceRepository
{
    bool IsAvailable { get; }
    Task<IReadOnlyList<Workspace>> ListWorkspacesAsync(string localWorkspaceRoot, CancellationToken cancellationToken = default);
    Task<Workspace?> LoadWorkspaceAsync(WorkspaceId id, string localWorkspaceRoot, CancellationToken cancellationToken = default);
    Task SaveWorkspaceAsync(Workspace workspace, CancellationToken cancellationToken = default);
    Task ArchiveWorkspaceAsync(WorkspaceId id, CancellationToken cancellationToken = default);
}
