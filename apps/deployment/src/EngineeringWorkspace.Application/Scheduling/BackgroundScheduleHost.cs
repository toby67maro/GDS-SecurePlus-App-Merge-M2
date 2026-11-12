namespace EngineeringWorkspace.Application.Scheduling;

public enum BackgroundHostState
{
    NotSupported,
    NotRegistered,
    Registered,
    Error
}

public sealed record BackgroundHostStatus(BackgroundHostState State, string Detail)
{
    public bool IsRegistered => State == BackgroundHostState.Registered;
}

/// <summary>
/// Runs due schedules while the application is closed: a per-user Windows Task Scheduler task that starts the
/// application with <c>--run-schedules</c> every few minutes while the user is signed in.
/// </summary>
public interface IBackgroundScheduleHost
{
    Task<BackgroundHostStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<BackgroundHostStatus> RegisterAsync(CancellationToken cancellationToken = default);
    Task<BackgroundHostStatus> UnregisterAsync(CancellationToken cancellationToken = default);
}
