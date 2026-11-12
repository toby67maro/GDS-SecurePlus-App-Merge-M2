using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EngineeringWorkspace.Desktop.Pages.Insights;

public partial class RoadmapView
{
    public RoadmapView() => InitializeComponent();

    /// <summary>Saves the whole timeline (not just the visible part) as a PNG image.</summary>
    private void OnSaveTimelineImage(object sender, RoutedEventArgs e)
    {
        if (TimelineControl.Surface is not { } surface || surface.ActualWidth < 1) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PNG image (*.png)|*.png", FileName = $"SecurePlus-Roadmap-Timeline-{DateTime.Now:yyyyMMdd-HHmm}.png" };
        if (dialog.ShowDialog() != true) return;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth * 1.5), (int)Math.Ceiling(surface.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(dialog.FileName);
        encoder.Save(stream);
    }
}
