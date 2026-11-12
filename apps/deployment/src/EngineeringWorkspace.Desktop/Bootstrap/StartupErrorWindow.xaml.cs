using System.Windows;

namespace EngineeringWorkspace.Desktop.Bootstrap;

/// <summary>Safe startup error window: actionable problems, no stack traces, no secrets.</summary>
public partial class StartupErrorWindow : Window
{
    public StartupErrorWindow(string heading, IReadOnlyList<string> problems, string? diagnosticPath)
    {
        InitializeComponent();
        Heading.Text = heading;
        Problems.ItemsSource = problems;
        DiagnosticPath.Text = diagnosticPath is null ? "A diagnostic file could not be written." : $"Diagnostic file: {diagnosticPath}";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
