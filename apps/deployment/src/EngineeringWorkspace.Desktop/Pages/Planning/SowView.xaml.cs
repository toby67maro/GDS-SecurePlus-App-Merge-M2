using System.IO;
using System.Windows;

namespace EngineeringWorkspace.Desktop.Pages.Planning;

public partial class SowView
{
    public SowView() => InitializeComponent();

    private SowViewModel? Model => DataContext as SowViewModel;

    private void OnSaveHtml(object sender, RoutedEventArgs e) => Save("HTML document (*.html)|*.html", "html");

    /// <summary>Word opens HTML saved with a .doc extension as a document (no Office installation needed to create it).</summary>
    private void OnSaveWord(object sender, RoutedEventArgs e) => Save("Word document (*.doc)|*.doc", "doc");

    private void Save(string filter, string extension)
    {
        if (Model?.Html is not { } html)
        {
            if (Model is not null) Model.StatusMessage = "Generate the SOW first.";
            return;
        }

        var name = string.Concat(Model.DocumentTitle.Split(Path.GetInvalidFileNameChars())).Replace(' ', '-');
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = filter, FileName = $"{name}.{extension}" };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, html, new System.Text.UTF8Encoding(true));
        Model.StatusMessage = $"Saved {dialog.FileName}.";
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (Model is { HasPreview: true } model) Clipboard.SetText(model.PlainText);
    }
}
