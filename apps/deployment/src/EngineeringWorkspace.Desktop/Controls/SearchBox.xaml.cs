using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace EngineeringWorkspace.Desktop.Controls;

/// <summary>
/// The application's search box: magnifying-glass icon, placeholder text, a clear (×) button and debounced input.
/// <see cref="Text"/> updates <see cref="DelayMilliseconds"/> after typing stops (Enter applies it at once, Escape clears).
/// </summary>
public partial class SearchBox : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(SearchBox),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTextChanged));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(nameof(Placeholder), typeof(string), typeof(SearchBox),
        new PropertyMetadata("Search"));

    public static readonly DependencyProperty DelayMillisecondsProperty = DependencyProperty.Register(nameof(DelayMilliseconds), typeof(int), typeof(SearchBox),
        new PropertyMetadata(250));

    private readonly DispatcherTimer _debounce;
    private bool _syncing;

    public SearchBox()
    {
        InitializeComponent();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) => Commit();
        Input.TextChanged += (_, _) => OnInputChanged();
        Input.KeyDown += OnKeyDown;
        Clear.Click += (_, _) => ClearText();
        Input.GotKeyboardFocus += (_, _) => Frame.SetResourceReference(Border.BorderBrushProperty, "AppBrush.Accent");
        Input.LostKeyboardFocus += (_, _) => Frame.SetResourceReference(Border.BorderBrushProperty, "AppBrush.InputBorder");
        GotFocus += (_, e) =>
        {
            if (e.OriginalSource == this) Input.Focus();
        };
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public int DelayMilliseconds
    {
        get => (int)GetValue(DelayMillisecondsProperty);
        set => SetValue(DelayMillisecondsProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (SearchBox)d;
        var text = e.NewValue as string ?? string.Empty;
        if (box._syncing || box.Input.Text == text) return;
        box._syncing = true;
        box.Input.Text = text;
        box._syncing = false;
        box.UpdateChrome();
    }

    private void OnInputChanged()
    {
        UpdateChrome();
        if (_syncing) return;
        _debounce.Stop();
        _debounce.Interval = TimeSpan.FromMilliseconds(Math.Max(0, DelayMilliseconds));
        _debounce.Start();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Input.Text.Length > 0)
        {
            ClearText();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Commit();
            e.Handled = true;
        }
    }

    private void ClearText()
    {
        Input.Text = string.Empty;
        Commit();
        Input.Focus();
    }

    private void Commit()
    {
        _debounce.Stop();
        if (Text == Input.Text) return;
        _syncing = true;
        Text = Input.Text;
        _syncing = false;
    }

    private void UpdateChrome()
    {
        var empty = string.IsNullOrEmpty(Input.Text);
        PlaceholderText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Clear.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }
}
