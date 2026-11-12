using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace EngineeringWorkspace.Desktop.Controls;

/// <summary>
/// Hosts a table or list with a user-adjustable height: drag the grip at the bottom edge to resize it, or select
/// Expand to open the same content in a separate, maximized and resizable window. Closing that window puts the
/// content back. Used for every data grid so table data can actually be read.
/// </summary>
[TemplatePart(Name = GripPart, Type = typeof(Thumb))]
[TemplatePart(Name = ExpandPart, Type = typeof(ButtonBase))]
public sealed class ResizablePanel : ContentControl
{
    private const string GripPart = "PART_Grip";
    private const string ExpandPart = "PART_Expand";
    private Thumb? _grip;
    private ButtonBase? _expand;
    private Window? _expandedWindow;

    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(ResizablePanel), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty PanelHeightProperty =
        DependencyProperty.Register(nameof(PanelHeight), typeof(double), typeof(ResizablePanel), new FrameworkPropertyMetadata(320d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MinPanelHeightProperty =
        DependencyProperty.Register(nameof(MinPanelHeight), typeof(double), typeof(ResizablePanel), new PropertyMetadata(120d));

    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(ResizablePanel), new PropertyMetadata(false));

    /// <summary>Title of the expanded window (and an optional caption above the panel).</summary>
    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public double PanelHeight
    {
        get => (double)GetValue(PanelHeightProperty);
        set => SetValue(PanelHeightProperty, value);
    }

    public double MinPanelHeight
    {
        get => (double)GetValue(MinPanelHeightProperty);
        set => SetValue(MinPanelHeightProperty, value);
    }

    /// <summary>True while the content is shown in the expanded window.</summary>
    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        private set => SetValue(IsExpandedProperty, value);
    }

    public override void OnApplyTemplate()
    {
        if (_grip is not null) _grip.DragDelta -= OnGripDragDelta;
        if (_expand is not null) _expand.Click -= OnExpandClick;
        base.OnApplyTemplate();
        _grip = GetTemplateChild(GripPart) as Thumb;
        _expand = GetTemplateChild(ExpandPart) as ButtonBase;
        if (_grip is not null) _grip.DragDelta += OnGripDragDelta;
        if (_expand is not null) _expand.Click += OnExpandClick;
    }

    private void OnGripDragDelta(object sender, DragDeltaEventArgs e) =>
        PanelHeight = Math.Max(MinPanelHeight, PanelHeight + e.VerticalChange);

    private void OnExpandClick(object sender, RoutedEventArgs e)
    {
        if (_expandedWindow is not null)
        {
            _expandedWindow.Activate();
            return;
        }

        var content = Content;
        if (content is null) return;
        Content = null;
        var host = new Border { Child = content as UIElement, Padding = new Thickness(16) };
        if (host.Child is null)
        {
            Content = content;
            return;
        }

        var owner = Window.GetWindow(this);
        _expandedWindow = new Window
        {
            Title = string.IsNullOrWhiteSpace(Header) ? owner?.Title ?? "Details" : Header,
            Owner = owner,
            Content = host,
            DataContext = DataContext,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowState = WindowState.Maximized,
            ResizeMode = ResizeMode.CanResize,
            Width = 1200,
            Height = 800,
            MinWidth = 480,
            MinHeight = 320,
            ShowInTaskbar = false
        };
        Services.WindowZoom.SetIsEnabled(_expandedWindow, true);
        _expandedWindow.SetResourceReference(BackgroundProperty, "AppBrush.ApplicationBackground");
        _expandedWindow.SetResourceReference(ForegroundProperty, "AppBrush.TextPrimary");
        _expandedWindow.SetResourceReference(FontFamilyProperty, "AppFont.Family");
        _expandedWindow.Closed += (_, _) =>
        {
            host.Child = null;
            Content = content;
            IsExpanded = false;
            _expandedWindow = null;
        };
        IsExpanded = true;
        _expandedWindow.Show();
    }
}
