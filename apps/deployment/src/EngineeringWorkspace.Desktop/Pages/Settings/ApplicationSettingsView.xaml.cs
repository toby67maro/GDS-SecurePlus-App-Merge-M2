using System.Windows;

namespace EngineeringWorkspace.Desktop.Pages.Settings;

public partial class ApplicationSettingsView
{
    public ApplicationSettingsView() => InitializeComponent();

    /// <summary>Passes the passcode to the page (a PasswordBox cannot be bound) and re-evaluates Advanced Mode.</summary>
    private async void OnAdvancedPasscodeChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ApplicationSettingsViewModel model) return;
        model.AdvancedPasscode = AdvancedPasscodeBox.Password;
        if (model.AdvancedModeRequested && !model.AdvancedModeEnabled) await model.ApplyAdvancedModeAsync();
    }
}
