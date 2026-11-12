using System.Diagnostics;
using System.Windows;

namespace EngineeringWorkspace.Desktop.Services;

/// <summary>A confirmation that states exactly what will happen before an administrative or destructive action.</summary>
/// <remarks>With <see cref="Passcode"/>, the dialog also asks for a passcode and only confirms when the check accepts it.</remarks>
public sealed record ConfirmationRequest(string Title, string Message, IReadOnlyList<KeyValuePair<string, string>> Details, string ConfirmText, bool IsDestructive = false,
    Func<string, bool>? Passcode = null, string? PasscodeLabel = null)
{
    public bool RequiresPasscode => Passcode is not null;
}

/// <summary>Desktop view service for dialogs. ViewModels call it; they never create windows.</summary>
public interface IDialogService
{
    bool Confirm(ConfirmationRequest request);
    void ShowInformation(string title, string message);

    /// <summary>Asks where to save a file. <paramref name="filter"/> uses the Windows filter format ("CSV files (*.csv)|*.csv").</summary>
    string? SaveFile(string suggestedName, string filter);

    string? OpenFile(string filter);
}

public sealed class DialogService : IDialogService
{
    public bool Confirm(ConfirmationRequest request)
    {
        var dialog = new ConfirmationDialog(request);
        var owner = DialogOwner.Current;
        if (owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        else dialog.Owner = owner;
        return dialog.ShowDialog() == true;
    }

    public void ShowInformation(string title, string message)
    {
        if (DialogOwner.Current is { } owner) MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        else MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public string? SaveFile(string suggestedName, string filter)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = suggestedName, Filter = filter, AddExtension = true, OverwritePrompt = true };
        return Show(dialog) ? dialog.FileName : null;
    }

    public string? OpenFile(string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = filter, CheckFileExists = true, Multiselect = false };
        return Show(dialog) ? dialog.FileName : null;
    }

    private static bool Show(Microsoft.Win32.CommonDialog dialog) =>
        (DialogOwner.Current is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true;
}

/// <summary>
/// The window a dialog belongs to: the active window, else the main window once it has been shown, else any visible
/// window. During first-run setup the main window exists but has never been shown, and WPF refuses it as an owner.
/// </summary>
public static class DialogOwner
{
    public static Window? Current
    {
        get
        {
            var application = System.Windows.Application.Current;
            if (application is null) return null;
            var shown = application.Windows.OfType<Window>().Where(window => window.IsVisible && window.IsLoaded).ToList();
            return shown.FirstOrDefault(window => window.IsActive)
                   ?? (application.MainWindow is { IsVisible: true, IsLoaded: true } main ? main : null)
                   ?? shown.LastOrDefault();
        }
    }
}

/// <summary>Opens folders in Windows Explorer.</summary>
public interface IShellLauncher
{
    bool OpenFolder(string path);
}

public sealed class ShellLauncher : IShellLauncher
{
    public bool OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return false;
        using var process = Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        return true;
    }
}
