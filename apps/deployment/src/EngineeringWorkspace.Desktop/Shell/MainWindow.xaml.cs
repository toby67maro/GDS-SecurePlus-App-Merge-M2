using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace EngineeringWorkspace.Desktop.Shell;

/// <summary>The shell window. No logic: the DataContext is the ShellViewModel.</summary>
public partial class MainWindow : Window
{
    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Services.WindowSizing.FitToWorkArea(this);
        Loaded += (_, _) =>
        {
            // The navigation pane starts at the theme's width (ApplicationProperties.json) and can then be dragged.
            if (TryFindResource("AppLayout.NavigationWidth") is double width && width > 0) NavigationColumn.Width = new GridLength(width);
        };
    }

    /// <summary>Esc closes a top-bar picker and returns focus to its icon.</summary>
    private void OnPickerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not Popup popup) return;
        popup.IsOpen = false;
        (popup.PlacementTarget as UIElement)?.Focus();
        e.Handled = true;
    }

    /// <summary>Opening a picker moves keyboard focus into it.</summary>
    private void OnPickerOpened(object? sender, EventArgs e)
    {
        if (sender is Popup { Child: Border { Child: Panel panel } })
            Dispatcher.BeginInvoke(() => panel.Children.OfType<ComboBox>().FirstOrDefault()?.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }
}
