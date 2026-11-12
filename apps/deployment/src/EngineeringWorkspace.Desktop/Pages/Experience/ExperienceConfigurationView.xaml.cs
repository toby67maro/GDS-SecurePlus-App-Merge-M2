using System.Windows;

namespace EngineeringWorkspace.Desktop.Pages.Experience;

public partial class ExperienceConfigurationView
{
    public ExperienceConfigurationView() => InitializeComponent();

    private async void OnImportFormResults(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ExperienceConfigurationViewModel model) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "EaaS customer configuration (*.json)|*.json|All files (*.*)|*.*",
            Title = "Import EaaS form results (customer configuration JSON)"
        };
        if (dialog.ShowDialog() == true) await model.ImportFormResultsAsync(dialog.FileName);
    }
}
