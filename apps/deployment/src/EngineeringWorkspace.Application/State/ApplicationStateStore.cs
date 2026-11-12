namespace EngineeringWorkspace.Application.State;

/// <summary>Per-user shell state persisted between sessions. Contains no secrets.</summary>
/// <param name="SetupCompletedVersion">Application version that completed first-run setup; null until setup finishes.</param>
/// <param name="ZoomLevel">Ctrl + mouse wheel zoom level of the windows (1 = 100%).</param>
/// <param name="ThemePreset">Built-in theme chosen in the header (null: the workspace / properties-file theme).</param>
/// <param name="AdvancedModeEnabled">True after Advanced Mode was unlocked with its passcode (shows the Advanced menu).</param>
/// <param name="PinnedNavigationIds">Navigation sections pinned open (they do not collapse when another section expands).</param>
public sealed record ApplicationState(string? LastWorkspaceId, string? LastCustomerId, string? LastRoute, string? SetupCompletedVersion = null, double? ZoomLevel = null,
    IReadOnlyList<string>? PinnedNavigationIds = null, bool AdvancedModeEnabled = false, string? ThemePreset = null)
{
    public static ApplicationState Empty { get; } = new(null, null, null);
}

/// <summary>Loads and saves <see cref="ApplicationState"/> in the writable per-user state directory.</summary>
public interface IApplicationStateStore
{
    Task<ApplicationState> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ApplicationState state, CancellationToken cancellationToken = default);
}
