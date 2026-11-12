using System.Security.Cryptography;
using System.Text;
using EngineeringWorkspace.Application.State;

namespace EngineeringWorkspace.Application.Navigation;

/// <summary>
/// Advanced Mode shows the hidden Advanced menu. It is turned on in Settings > Application Settings, and only takes
/// effect when the passcode is entered. The passcode is compared by SHA-256 hash; it is a convenience gate that keeps
/// engineering tools out of everyday use, not a security boundary (anyone with the binaries can bypass it).
/// </summary>
public interface IAdvancedModeService
{
    bool IsEnabled { get; }
    event EventHandler? Changed;
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Enables Advanced Mode when <paramref name="passcode"/> is correct; returns false (and stays off) otherwise.</summary>
    Task<bool> TryEnableAsync(string passcode, CancellationToken cancellationToken = default);

    Task DisableAsync(CancellationToken cancellationToken = default);
}

public sealed class AdvancedModeService(IApplicationStateStore stateStore) : IAdvancedModeService
{
    // SHA-256 of the passcode (UTF-8). The passcode itself is not stored in the application.
    private const string PasscodeHash = "B31417FED8655BDA6C5C2CB26CAE06ECF5A2178DB1EF738A052ABDFE39F1CC63";

    public bool IsEnabled { get; private set; }
    public event EventHandler? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var enabled = (await stateStore.LoadAsync(cancellationToken)).AdvancedModeEnabled;
        if (enabled == IsEnabled) return;
        IsEnabled = enabled;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static bool IsPasscode(string? passcode) =>
        !string.IsNullOrEmpty(passcode) &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(passcode.Trim())), Convert.FromHexString(PasscodeHash));

    public async Task<bool> TryEnableAsync(string passcode, CancellationToken cancellationToken = default)
    {
        if (!IsPasscode(passcode)) return false;
        await SetAsync(true, cancellationToken);
        return true;
    }

    public Task DisableAsync(CancellationToken cancellationToken = default) => SetAsync(false, cancellationToken);

    private async Task SetAsync(bool enabled, CancellationToken cancellationToken)
    {
        var state = await stateStore.LoadAsync(cancellationToken);
        await stateStore.SaveAsync(state with { AdvancedModeEnabled = enabled }, cancellationToken);
        if (IsEnabled == enabled) return;
        IsEnabled = enabled;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
