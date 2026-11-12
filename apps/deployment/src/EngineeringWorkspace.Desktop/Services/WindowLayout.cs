using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace EngineeringWorkspace.Desktop.Services;

/// <summary>
/// Application-wide zoom: hold Ctrl and turn the mouse wheel (or press Ctrl + Plus / Ctrl + Minus, Ctrl + 0 to reset)
/// in any window to scale its content. The level is shared by every window and remembered between sessions.
/// </summary>
public static class WindowZoom
{
    public const double Minimum = 0.6;
    public const double Maximum = 2.0;
    private const double Step = 0.1;
    private static double _level = 1.0;

    public static event EventHandler? Changed;

    public static double Level
    {
        get => _level;
        set
        {
            var clamped = Math.Round(Math.Clamp(value, Minimum, Maximum), 2);
            if (Math.Abs(clamped - _level) < 0.001) return;
            _level = clamped;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(WindowZoom), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Window window || e.NewValue is not true) return;
        window.PreviewMouseWheel += OnPreviewMouseWheel;
        window.PreviewKeyDown += OnPreviewKeyDown;
        EventHandler apply = (_, _) => Apply(window);
        Changed += apply;
        window.Closed += (_, _) => Changed -= apply;
        window.Loaded += (_, _) => Apply(window);
        window.ContentRendered += (_, _) => Apply(window);
    }

    private static void Apply(Window window)
    {
        if (window.Content is FrameworkElement content)
            content.LayoutTransform = Math.Abs(Level - 1.0) < 0.001 ? Transform.Identity : new ScaleTransform(Level, Level);
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        Level += e.Delta > 0 ? Step : -Step;
        e.Handled = true;
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add:
                Level += Step;
                e.Handled = true;
                break;
            case Key.OemMinus or Key.Subtract:
                Level -= Step;
                e.Handled = true;
                break;
            case Key.D0 or Key.NumPad0:
                Level = 1.0;
                e.Handled = true;
                break;
        }
    }
}

/// <summary>Sizes windows to fit the screen's work area (never under the taskbar or above the top edge).</summary>
public static class WindowSizing
{
    /// <summary>
    /// Sizes <paramref name="window"/> to a 16:9 rectangle covering at most <paramref name="fraction"/> of the work area
    /// and centres it, so the title bar and edges are always reachable.
    /// </summary>
    public static void FitToWorkArea(Window window, double fraction = 0.9, double aspect = 16.0 / 9.0)
    {
        var area = SystemParameters.WorkArea;
        var width = Math.Min(area.Width * fraction, area.Height * fraction * aspect);
        var height = width / aspect;
        if (height > area.Height * fraction)
        {
            height = area.Height * fraction;
            width = height * aspect;
        }

        window.MinWidth = Math.Min(window.MinWidth, area.Width * 0.8);
        window.MinHeight = Math.Min(window.MinHeight, area.Height * 0.8);
        window.Width = Math.Max(window.MinWidth, width);
        window.Height = Math.Max(window.MinHeight, height);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = area.Left + (area.Width - window.Width) / 2;
        window.Top = area.Top + Math.Max(0, (area.Height - window.Height) / 2);
    }
}
