using System.Windows;

namespace EngineeringWorkspace.Desktop.Setup;

/// <summary>First-run setup. DialogResult is true only when setup completed; closing it any other way exits the application.</summary>
public partial class SetupWindow : Window
{
    public SetupWindow(SetupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Services.WindowSizing.FitToWorkArea(this, 0.88);
    }

    private void OnCompleteClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnExitClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
