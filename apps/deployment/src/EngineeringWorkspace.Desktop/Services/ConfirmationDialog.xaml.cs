using System.Windows;

namespace EngineeringWorkspace.Desktop.Services;

/// <summary>View-only code-behind: sets the dialog result. Cancel is the default for destructive actions.</summary>
public partial class ConfirmationDialog : Window
{
    public ConfirmationDialog(ConfirmationRequest request)
    {
        InitializeComponent();
        DataContext = request;
        ConfirmButton.IsDefault = !request.IsDestructive;
        if (request.IsDestructive) ConfirmButton.Style = (Style)FindResource("Button.Danger");
        if (request.RequiresPasscode) Loaded += (_, _) => PasscodeBox.Focus();
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (DataContext is ConfirmationRequest { Passcode: { } check } && !check(PasscodeBox.Password))
        {
            PasscodeError.Visibility = Visibility.Visible;
            PasscodeBox.Clear();
            PasscodeBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
