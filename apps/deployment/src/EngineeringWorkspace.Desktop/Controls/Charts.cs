using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace EngineeringWorkspace.Desktop.Controls;

/// <summary>
/// One value of a chart. <see cref="Children"/> break the value down further: clicking a slice or bar that has
/// children drills into them (with a breadcrumb back). <see cref="Tag"/> is passed to the chart's command.
/// </summary>
public sealed record ChartSlice(string Label, double Value, string? Color = null, string? Detail = null, object? Tag = null, IReadOnlyList<ChartSlice>? Children = null)
{
    public bool HasChildren => Children is { Count: > 0 };
}

/// <summary>A row of a stacked bar chart: one label and its segments (for example Passed / Failed / Not Assessed).</summary>
public sealed record StackedRow(string Label, IReadOnlyList<ChartSlice> Segments, object? Tag = null)
{
    public double Total => Segments.Sum(segment => segment.Value);
}

/// <summary>One cell of a heat grid.</summary>
public sealed record HeatCell(string Row, string Column, double? Value, string Text, string? Detail = null, object? Tag = null);

/// <summary>Shared behaviour of the charts: title, breadcrumb drill-down, colors, command and entrance animation.</summary>
public abstract class ChartBase : Border
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(ChartBase),
        new PropertyMetadata(null, (d, e) =>
        {
            ((ChartBase)d).Watch(e.OldValue, e.NewValue);
            ((ChartBase)d).Reset();
        }));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(ChartBase),
        new PropertyMetadata(null, (d, _) => ((ChartBase)d).Rebuild()));
    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(ChartBase));
    public static readonly DependencyProperty ValueFormatProperty = DependencyProperty.Register(nameof(ValueFormat), typeof(string), typeof(ChartBase),
        new PropertyMetadata("N0", (d, _) => ((ChartBase)d).Rebuild()));

    private readonly Stack<(string Label, IReadOnlyList<ChartSlice> Items)> _path = new();
    private bool _rebuildQueued;

    /// <summary>Rebuilds (once per dispatcher cycle) when an observable source collection changes.</summary>
    protected void Watch(object? oldValue, object? newValue)
    {
        if (oldValue is System.Collections.Specialized.INotifyCollectionChanged oldCollection) oldCollection.CollectionChanged -= OnSourceChanged;
        if (newValue is System.Collections.Specialized.INotifyCollectionChanged newCollection) newCollection.CollectionChanged += OnSourceChanged;
    }

    private void OnSourceChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _rebuildQueued = false;
            _path.Clear();
            _current = null;
            Rebuild();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }
    private IReadOnlyList<ChartSlice>? _current;

    protected ChartBase()
    {
        SetResourceReference(BackgroundProperty, "AppBrush.SurfaceBackground");
        SetResourceReference(BorderBrushProperty, "AppBrush.Divider");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(12);
        Margin = new Thickness(0, 0, 10, 10);
        Motion.SetHoverLift(this, true);
        Loaded += (_, _) => Rebuild();
        SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.PreviousSize.Width - e.NewSize.Width) > 24) Rebuild();
        };
    }

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Executed with the clicked <see cref="ChartSlice"/> (or <see cref="HeatCell"/> / <see cref="StackedRow"/>) when it has no children to drill into.</summary>
    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public string ValueFormat
    {
        get => (string)GetValue(ValueFormatProperty);
        set => SetValue(ValueFormatProperty, value);
    }

    /// <summary>The slices shown now (the drilled-into level or the items source).</summary>
    protected IReadOnlyList<ChartSlice> CurrentSlices => _current ?? ItemsSource?.OfType<ChartSlice>().ToArray() ?? [];

    protected string Format(double value) => value.ToString(ValueFormat, CultureInfo.CurrentCulture);

    private void Reset()
    {
        _path.Clear();
        _current = null;
        Rebuild();
    }

    /// <summary>Drills into the slice's children, or runs the command.</summary>
    protected void Activate(object item)
    {
        if (item is ChartSlice { HasChildren: true } slice)
        {
            _path.Push((slice.Label, CurrentSlices));
            _current = slice.Children;
            Rebuild();
            return;
        }

        if (Command?.CanExecute(item) == true) Command.Execute(item);
    }

    protected void Rebuild()
    {
        if (!IsLoaded) return;
        var root = new DockPanel { LastChildFill = true };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(header, Dock.Top);
        if (_path.Count > 0)
        {
            var back = new Button { Content = "◀ Back", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 0, 8, 0), MinHeight = 22 };
            back.SetResourceReference(StyleProperty, "Button.Secondary");
            back.Click += (_, _) =>
            {
                var (_, items) = _path.Pop();
                _current = _path.Count == 0 ? null : items;
                Rebuild();
            };
            DockPanel.SetDock(back, Dock.Right);
            header.Children.Add(back);
        }

        var title = new TextBlock
        {
            Text = _path.Count == 0 ? Title : $"{Title} › {string.Join(" › ", _path.Reverse().Select(level => level.Label))}",
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center
        };
        title.SetResourceReference(TextBlock.FontSizeProperty, "AppFont.Size.Heading3");
        header.Children.Add(title);
        if (!string.IsNullOrEmpty(Title) || _path.Count > 0) root.Children.Add(header);
        var body = BuildBody();
        root.Children.Add(body);
        Child = root;
        Motion.Enter(body, 8, 0.35);
    }

    protected abstract FrameworkElement BuildBody();

    /// <summary>Series color: the slice's own color, else the theme's chart palette.</summary>
    protected Brush BrushFor(ChartSlice slice, int index)
    {
        if (slice.Color is { Length: > 0 } color && new BrushConverter().ConvertFromString(color) is Brush brush) return brush;
        return TryFindResource($"AppChart.Series{index % 10 + 1}") as Brush ?? Brushes.SteelBlue;
    }

    protected static TextBlock Caption(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(StyleProperty, "Text.Caption");
        return block;
    }

    protected static ToolTip Tip(string title, string body) =>
        new() { Content = new StackPanel { Children = { new TextBlock { Text = title, FontWeight = FontWeights.SemiBold }, new TextBlock { Text = body, MaxWidth = 360, TextWrapping = TextWrapping.Wrap } } } };

    /// <summary>Legend row; hovering it highlights <paramref name="target"/>.</summary>
    protected FrameworkElement LegendRow(ChartSlice slice, Brush brush, double total, UIElement? target)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        var swatch = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = brush, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(swatch, Dock.Left);
        row.Children.Add(swatch);
        var value = new TextBlock
        {
            Text = total > 0 ? $"{Format(slice.Value)}  ({slice.Value / total:P0})" : Format(slice.Value), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
        };
        value.SetResourceReference(StyleProperty, "Text.Caption");
        DockPanel.SetDock(value, Dock.Right);
        row.Children.Add(value);
        row.Children.Add(new TextBlock { Text = slice.Label + (slice.HasChildren ? "  ›" : string.Empty), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        row.ToolTip = Tip(slice.Label, slice.Detail ?? $"{Format(slice.Value)}{(slice.HasChildren ? " — click to break down" : string.Empty)}");
        row.MouseLeftButtonUp += (_, _) => Activate(slice);
        if (target is not null)
        {
            row.MouseEnter += (_, _) => Highlight(target, true);
            row.MouseLeave += (_, _) => Highlight(target, false);
        }

        return row;
    }

    protected virtual void Highlight(UIElement element, bool on) => element.Opacity = on ? 0.8 : 1;
}

/// <summary>Donut (or pie, with <see cref="InnerRadiusRatio"/> 0). Hover pops a slice out; click drills into its breakdown.</summary>
public sealed class DonutChart : ChartBase
{
    public static readonly DependencyProperty InnerRadiusRatioProperty = DependencyProperty.Register(nameof(InnerRadiusRatio), typeof(double), typeof(DonutChart),
        new PropertyMetadata(0.58));
    public static readonly DependencyProperty CenterTextProperty = DependencyProperty.Register(nameof(CenterText), typeof(string), typeof(DonutChart),
        new PropertyMetadata(null, (d, _) => ((DonutChart)d).Rebuild()));
    public static readonly DependencyProperty CenterCaptionProperty = DependencyProperty.Register(nameof(CenterCaption), typeof(string), typeof(DonutChart),
        new PropertyMetadata(null, (d, _) => ((DonutChart)d).Rebuild()));
    public static readonly DependencyProperty DiameterProperty = DependencyProperty.Register(nameof(Diameter), typeof(double), typeof(DonutChart), new PropertyMetadata(180.0));

    public double InnerRadiusRatio { get => (double)GetValue(InnerRadiusRatioProperty); set => SetValue(InnerRadiusRatioProperty, value); }
    public string? CenterText { get => (string?)GetValue(CenterTextProperty); set => SetValue(CenterTextProperty, value); }
    public string? CenterCaption { get => (string?)GetValue(CenterCaptionProperty); set => SetValue(CenterCaptionProperty, value); }
    public double Diameter { get => (double)GetValue(DiameterProperty); set => SetValue(DiameterProperty, value); }

    protected override FrameworkElement BuildBody()
    {
        var slices = CurrentSlices.Where(slice => slice.Value > 0).ToArray();
        var total = slices.Sum(slice => slice.Value);
        var panel = new WrapPanel();
        var size = Diameter;
        var canvas = new Canvas { Width = size + 16, Height = size + 16, Margin = new Thickness(0, 0, 16, 0) };
        var legend = new StackPanel { MinWidth = 200, MaxWidth = 340, VerticalAlignment = VerticalAlignment.Center };
        if (total <= 0)
        {
            panel.Children.Add(Caption("No data yet."));
            return panel;
        }

        double start = -90;
        var center = new Point(size / 2 + 8, size / 2 + 8);
        var outer = size / 2;
        var inner = outer * Math.Clamp(InnerRadiusRatio, 0, 0.9);
        for (var index = 0; index < slices.Length; index++)
        {
            var slice = slices[index];
            var sweep = slice.Value / total * 360;
            var brush = BrushFor(slice, index);
            var path = new Path { Fill = brush, Stroke = (Brush?)TryFindResource("AppBrush.SurfaceBackground") ?? Brushes.White, StrokeThickness = 1.5, Cursor = Cursors.Hand,
                Data = Segment(center, outer, inner, start, Math.Min(sweep, 359.99)), RenderTransform = new TranslateTransform() };
            var mid = (start + sweep / 2) * Math.PI / 180;
            path.Tag = new Vector(Math.Cos(mid), Math.Sin(mid));
            path.ToolTip = Tip(slice.Label, (slice.Detail is null ? string.Empty : slice.Detail + "\n") + $"{Format(slice.Value)} of {Format(total)} ({slice.Value / total:P1})" +
                                            (slice.HasChildren ? "\nClick to break down." : string.Empty));
            path.MouseEnter += (_, _) => Highlight(path, true);
            path.MouseLeave += (_, _) => Highlight(path, false);
            path.MouseLeftButtonUp += (_, _) => Activate(slice);
            canvas.Children.Add(path);
            legend.Children.Add(LegendRow(slice, brush, total, path));
            start += sweep;
        }

        if (inner > 0 && (CenterText is not null || CenterCaption is not null))
        {
            var label = new StackPanel { Width = inner * 2, HorizontalAlignment = HorizontalAlignment.Center };
            var big = new TextBlock { Text = CenterText ?? Format(total), HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap };
            big.SetResourceReference(TextBlock.FontSizeProperty, "AppFont.Size.Heading2");
            label.Children.Add(big);
            if (CenterCaption is not null)
            {
                var small = Caption(CenterCaption);
                small.HorizontalAlignment = HorizontalAlignment.Center;
                small.TextAlignment = TextAlignment.Center;
                label.Children.Add(small);
            }

            label.Measure(new Size(inner * 2, double.PositiveInfinity));
            Canvas.SetLeft(label, center.X - inner);
            Canvas.SetTop(label, center.Y - label.DesiredSize.Height / 2);
            label.IsHitTestVisible = false;
            canvas.Children.Add(label);
        }

        panel.Children.Add(canvas);
        panel.Children.Add(legend);
        return panel;
    }

    protected override void Highlight(UIElement element, bool on)
    {
        if (element is not Path { Tag: Vector direction, RenderTransform: TranslateTransform translate } path) return;
        var duration = TimeSpan.FromSeconds(0.15);
        translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(on ? direction.X * 7 : 0, duration));
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(on ? direction.Y * 7 : 0, duration));
        path.Opacity = on ? 0.92 : 1;
    }

    private static Geometry Segment(Point center, double outer, double inner, double startDegrees, double sweepDegrees)
    {
        Point At(double radius, double degrees) => new(center.X + radius * Math.Cos(degrees * Math.PI / 180), center.Y + radius * Math.Sin(degrees * Math.PI / 180));
        var end = startDegrees + sweepDegrees;
        var large = sweepDegrees > 180;
        var figure = new PathFigure { StartPoint = At(outer, startDegrees), IsClosed = true };
        figure.Segments.Add(new ArcSegment(At(outer, end), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true));
        if (inner > 0)
        {
            figure.Segments.Add(new LineSegment(At(inner, end), true));
            figure.Segments.Add(new ArcSegment(At(inner, startDegrees), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true));
        }
        else
        {
            figure.Segments.Add(new LineSegment(center, true));
        }

        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }
}

/// <summary>Horizontal bars (labels left) that grow in, highlight on hover and drill down on click.</summary>
public sealed class HorizontalBarChart : ChartBase
{
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double?), typeof(HorizontalBarChart));
    public static readonly DependencyProperty LabelWidthProperty = DependencyProperty.Register(nameof(LabelWidth), typeof(double), typeof(HorizontalBarChart), new PropertyMetadata(200.0));

    /// <summary>Scale maximum (for example 100 for percentages); defaults to the largest value.</summary>
    public double? Maximum { get => (double?)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double LabelWidth { get => (double)GetValue(LabelWidthProperty); set => SetValue(LabelWidthProperty, value); }

    protected override FrameworkElement BuildBody()
    {
        var slices = CurrentSlices;
        var panel = new StackPanel();
        if (slices.Count == 0)
        {
            panel.Children.Add(Caption("No data yet."));
            return panel;
        }

        var max = Maximum ?? Math.Max(1e-9, slices.Max(slice => slice.Value));
        var width = Math.Max(160, ActualWidth - LabelWidth - 140);
        for (var index = 0; index < slices.Count; index++)
        {
            var slice = slices[index];
            var brush = BrushFor(slice, index);
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = slice.Label + (slice.HasChildren ? "  ›" : string.Empty), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center });
            var track = new Border { Height = 16, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
            track.SetResourceReference(Border.BackgroundProperty, "AppBrush.SurfaceSecondary");
            Grid.SetColumn(track, 1);
            var bar = new Border
            {
                Height = 16, CornerRadius = new CornerRadius(3), Background = brush, HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(2, Math.Min(1, slice.Value / max) * width), RenderTransform = new ScaleTransform(1, 1), RenderTransformOrigin = new Point(0, 0.5)
            };
            Grid.SetColumn(bar, 1);
            var text = Caption($"{Format(slice.Value)}{(Maximum is 100 ? "%" : string.Empty)}");
            text.Margin = new Thickness(8, 0, 0, 0);
            text.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(text, 2);
            row.Children.Add(track);
            row.Children.Add(bar);
            row.Children.Add(text);
            row.ToolTip = Tip(slice.Label, (slice.Detail ?? Format(slice.Value)) + (slice.HasChildren ? "\nClick to break down." : string.Empty));
            row.MouseEnter += (_, _) => bar.Opacity = 0.75;
            row.MouseLeave += (_, _) => bar.Opacity = 1;
            row.MouseLeftButtonUp += (_, _) => Activate(slice);
            ((ScaleTransform)bar.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.45 + index * 0.03)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            panel.Children.Add(row);
        }

        return panel;
    }
}

/// <summary>Vertical columns with value labels; hover highlights, click drills down.</summary>
public sealed class ColumnChart : ChartBase
{
    public static readonly DependencyProperty ChartHeightProperty = DependencyProperty.Register(nameof(ChartHeight), typeof(double), typeof(ColumnChart), new PropertyMetadata(180.0));
    public double ChartHeight { get => (double)GetValue(ChartHeightProperty); set => SetValue(ChartHeightProperty, value); }

    protected override FrameworkElement BuildBody()
    {
        var slices = CurrentSlices;
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        scroll.Content = panel;
        if (slices.Count == 0) return Caption("No data yet.");
        var max = Math.Max(1e-9, slices.Max(slice => slice.Value));
        for (var index = 0; index < slices.Count; index++)
        {
            var slice = slices[index];
            var column = new StackPanel { Width = 64, Margin = new Thickness(4, 0, 4, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Bottom };
            var value = Caption(Format(slice.Value));
            value.HorizontalAlignment = HorizontalAlignment.Center;
            var height = Math.Max(2, slice.Value / max * ChartHeight);
            var area = new Grid { Height = ChartHeight };
            var bar = new Border { Height = height, Width = 34, CornerRadius = new CornerRadius(4, 4, 0, 0), Background = BrushFor(slice, index), VerticalAlignment = VerticalAlignment.Bottom,
                RenderTransformOrigin = new Point(0.5, 1), RenderTransform = new ScaleTransform(1, 1) };
            area.Children.Add(bar);
            var label = new TextBlock { Text = slice.Label, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxHeight = 40, TextTrimming = TextTrimming.CharacterEllipsis };
            label.SetResourceReference(TextBlock.FontSizeProperty, "AppFont.Size.Caption");
            column.Children.Add(value);
            column.Children.Add(area);
            column.Children.Add(label);
            column.ToolTip = Tip(slice.Label, (slice.Detail ?? Format(slice.Value)) + (slice.HasChildren ? "\nClick to break down." : string.Empty));
            column.MouseEnter += (_, _) => bar.Opacity = 0.75;
            column.MouseLeave += (_, _) => bar.Opacity = 1;
            column.MouseLeftButtonUp += (_, _) => Activate(slice);
            ((ScaleTransform)bar.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.45 + index * 0.03)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            panel.Children.Add(column);
        }

        return scroll;
    }
}

/// <summary>Stacked horizontal bars (rows of segments); a segment click runs the command with the segment (Tag carries context).</summary>
public sealed class StackedBarChart : ChartBase
{
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(nameof(Rows), typeof(IEnumerable), typeof(StackedBarChart),
        new PropertyMetadata(null, (d, e) =>
        {
            ((StackedBarChart)d).Watch(e.OldValue, e.NewValue);
            ((StackedBarChart)d).Rebuild();
        }));

    public IEnumerable? Rows { get => (IEnumerable?)GetValue(RowsProperty); set => SetValue(RowsProperty, value); }

    protected override FrameworkElement BuildBody()
    {
        var rows = Rows?.OfType<StackedRow>().ToArray() ?? [];
        var panel = new StackPanel();
        if (rows.Length == 0)
        {
            panel.Children.Add(Caption("No data yet."));
            return panel;
        }

        var width = Math.Max(160, ActualWidth - 380);
        var legendNames = rows.SelectMany(row => row.Segments).Select(segment => segment.Label).Distinct().ToArray();
        foreach (var row in rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock { Text = row.Label, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center });
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Height = 18 };
            Grid.SetColumn(bar, 1);
            var total = Math.Max(1e-9, row.Total);
            foreach (var segment in row.Segments.Where(segment => segment.Value > 0))
            {
                var index = Array.IndexOf(legendNames, segment.Label);
                var part = new Border { Width = segment.Value / total * width, Background = BrushFor(segment, index), Cursor = Cursors.Hand,
                    ToolTip = Tip($"{row.Label} — {segment.Label}", segment.Detail ?? $"{Format(segment.Value)} ({segment.Value / total:P0})") };
                part.MouseEnter += (_, _) => part.Opacity = 0.75;
                part.MouseLeave += (_, _) => part.Opacity = 1;
                part.MouseLeftButtonUp += (_, _) => Activate(segment with { Tag = segment.Tag ?? row.Tag });
                bar.Children.Add(part);
            }

            var text = Caption(Format(row.Total));
            text.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(text, 2);
            grid.Children.Add(bar);
            grid.Children.Add(text);
            panel.Children.Add(grid);
        }

        var legend = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        for (var index = 0; index < legendNames.Length; index++)
        {
            var sample = rows.SelectMany(row => row.Segments).First(segment => segment.Label == legendNames[index]);
            legend.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0),
                Children = { new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = BrushFor(sample, index), Margin = new Thickness(0, 0, 6, 0) }, Caption(legendNames[index]) }
            });
        }

        panel.Children.Add(legend);
        return panel;
    }
}

/// <summary>Semicircle gauge for a percentage (0-100), colored red → amber → green.</summary>
public sealed class GaugeChart : ChartBase
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(GaugeChart),
        new PropertyMetadata(0.0, (d, _) => ((GaugeChart)d).Rebuild()));
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(GaugeChart),
        new PropertyMetadata(null, (d, _) => ((GaugeChart)d).Rebuild()));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string? Subtitle { get => (string?)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

    protected override FrameworkElement BuildBody()
    {
        const double size = 180;
        var value = Math.Clamp(Value, 0, 100);
        var canvas = new Canvas { Width = size, Height = size / 2 + 34 };
        var center = new Point(size / 2, size / 2);
        Geometry Arc(double from, double to)
        {
            Point At(double degrees) => new(center.X + (size / 2 - 12) * Math.Cos(degrees * Math.PI / 180), center.Y + (size / 2 - 12) * Math.Sin(degrees * Math.PI / 180));
            var figure = new PathFigure { StartPoint = At(from) };
            figure.Segments.Add(new ArcSegment(At(to), new Size(size / 2 - 12, size / 2 - 12), 0, to - from > 180, SweepDirection.Clockwise, true));
            return new PathGeometry([figure]);
        }

        var track = new Path { Data = Arc(180, 360), StrokeThickness = 18, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        track.SetResourceReference(Shape.StrokeProperty, "AppBrush.SurfaceSecondary");
        canvas.Children.Add(track);
        var color = value >= 80 ? "AppBrush.Success" : value >= 50 ? "AppBrush.Warning" : "AppBrush.Error";
        if (value > 0)
        {
            var fill = new Path { Data = Arc(180, 180 + value / 100 * 179.9), StrokeThickness = 18, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            fill.SetResourceReference(Shape.StrokeProperty, color);
            canvas.Children.Add(fill);
        }

        var text = new TextBlock { Text = $"{value:0}%", FontWeight = FontWeights.SemiBold, Width = size, TextAlignment = TextAlignment.Center };
        text.SetResourceReference(TextBlock.FontSizeProperty, "AppFont.Size.Heading1");
        Canvas.SetTop(text, size / 2 - 34);
        canvas.Children.Add(text);
        if (Subtitle is not null)
        {
            var caption = Caption(Subtitle);
            caption.Width = size;
            caption.TextAlignment = TextAlignment.Center;
            Canvas.SetTop(caption, size / 2 + 6);
            canvas.Children.Add(caption);
        }

        return canvas;
    }
}

/// <summary>Trend line with hover markers (values in order; labels on the x axis).</summary>
public sealed class LineChart : ChartBase
{
    public static readonly DependencyProperty ChartHeightProperty = DependencyProperty.Register(nameof(ChartHeight), typeof(double), typeof(LineChart), new PropertyMetadata(160.0));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double?), typeof(LineChart));
    public double ChartHeight { get => (double)GetValue(ChartHeightProperty); set => SetValue(ChartHeightProperty, value); }
    public double? Maximum { get => (double?)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    protected override FrameworkElement BuildBody()
    {
        var points = CurrentSlices;
        if (points.Count == 0) return Caption("No data yet.");
        var width = Math.Max(240, ActualWidth - 40);
        var height = ChartHeight;
        var max = Maximum ?? Math.Max(1e-9, points.Max(point => point.Value));
        var canvas = new Canvas { Width = width, Height = height + 24 };
        var step = points.Count > 1 ? (width - 20) / (points.Count - 1) : 0;
        Point At(int index) => new(10 + index * step, height - points[index].Value / max * (height - 10));
        for (var line = 0; line <= 4; line++)
        {
            var y = height - line / 4.0 * (height - 10);
            var grid = new Line { X1 = 0, X2 = width, Y1 = y, Y2 = y, StrokeThickness = 1, StrokeDashArray = [2, 3] };
            grid.SetResourceReference(Shape.StrokeProperty, "AppBrush.Divider");
            canvas.Children.Add(grid);
        }

        var figure = new PathFigure { StartPoint = At(0) };
        var area = new PathFigure { StartPoint = new Point(At(0).X, height), IsClosed = true };
        area.Segments.Add(new LineSegment(At(0), true));
        for (var index = 1; index < points.Count; index++)
        {
            figure.Segments.Add(new LineSegment(At(index), true));
            area.Segments.Add(new LineSegment(At(index), true));
        }

        area.Segments.Add(new LineSegment(new Point(At(points.Count - 1).X, height), true));
        var brush = BrushFor(points[0], 0);
        canvas.Children.Add(new Path { Data = new PathGeometry([area]), Fill = brush, Opacity = 0.15 });
        canvas.Children.Add(new Path { Data = new PathGeometry([figure]), Stroke = brush, StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round });
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            var dot = new Ellipse { Width = 10, Height = 10, Fill = brush, Cursor = Cursors.Hand, ToolTip = Tip(point.Label, point.Detail ?? Format(point.Value)) };
            dot.SetResourceReference(Shape.StrokeProperty, "AppBrush.SurfaceBackground");
            dot.StrokeThickness = 2;
            Canvas.SetLeft(dot, At(index).X - 5);
            Canvas.SetTop(dot, At(index).Y - 5);
            var position = At(index);
            dot.MouseEnter += (_, _) => { dot.Width = dot.Height = 14; Canvas.SetLeft(dot, position.X - 7); Canvas.SetTop(dot, position.Y - 7); };
            dot.MouseLeave += (_, _) => { dot.Width = dot.Height = 10; Canvas.SetLeft(dot, position.X - 5); Canvas.SetTop(dot, position.Y - 5); };
            dot.MouseLeftButtonUp += (_, _) => Activate(point);
            canvas.Children.Add(dot);
            if (points.Count <= 12 || index % Math.Max(1, points.Count / 8) == 0)
            {
                var label = Caption(point.Label);
                label.TextWrapping = TextWrapping.NoWrap;
                Canvas.SetLeft(label, Math.Max(0, At(index).X - 24));
                Canvas.SetTop(label, height + 4);
                canvas.Children.Add(label);
            }
        }

        return canvas;
    }
}

/// <summary>Heat grid: rows × columns of cells colored by value (0-1 → red, amber, green; or a custom scale); click a cell for its detail.</summary>
public sealed class HeatGrid : ChartBase
{
    public static readonly DependencyProperty CellsProperty = DependencyProperty.Register(nameof(Cells), typeof(IEnumerable), typeof(HeatGrid),
        new PropertyMetadata(null, (d, e) =>
        {
            ((HeatGrid)d).Watch(e.OldValue, e.NewValue);
            ((HeatGrid)d).Rebuild();
        }));
    public static readonly DependencyProperty HighIsGoodProperty = DependencyProperty.Register(nameof(HighIsGood), typeof(bool), typeof(HeatGrid),
        new PropertyMetadata(true, (d, _) => ((HeatGrid)d).Rebuild()));
    public static readonly DependencyProperty LegendTextProperty = DependencyProperty.Register(nameof(LegendText), typeof(string), typeof(HeatGrid),
        new PropertyMetadata(null, (d, _) => ((HeatGrid)d).Rebuild()));

    public IEnumerable? Cells { get => (IEnumerable?)GetValue(CellsProperty); set => SetValue(CellsProperty, value); }

    /// <summary>True: high values are green (compliance); false: high values are red (risk).</summary>
    public bool HighIsGood { get => (bool)GetValue(HighIsGoodProperty); set => SetValue(HighIsGoodProperty, value); }

    public string? LegendText { get => (string?)GetValue(LegendTextProperty); set => SetValue(LegendTextProperty, value); }

    public static Color Scale(double value, bool highIsGood)
    {
        var v = Math.Clamp(highIsGood ? value : 1 - value, 0, 1);
        // red (#D13438) → amber (#F7B500) → green (#2E9E44)
        static Color Lerp(Color a, Color b, double t) =>
            Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        var red = Color.FromRgb(0xD1, 0x34, 0x38);
        var amber = Color.FromRgb(0xF7, 0xB5, 0x00);
        var green = Color.FromRgb(0x2E, 0x9E, 0x44);
        return v < 0.5 ? Lerp(red, amber, v * 2) : Lerp(amber, green, (v - 0.5) * 2);
    }

    protected override FrameworkElement BuildBody()
    {
        var cells = Cells?.OfType<HeatCell>().ToArray() ?? [];
        if (cells.Length == 0) return Caption("No data yet.");
        var rows = cells.Select(cell => cell.Row).Distinct().ToArray();
        var columns = cells.Select(cell => cell.Column).Distinct().ToArray();
        var lookup = cells.GroupBy(cell => (cell.Row, cell.Column)).ToDictionary(group => group.Key, group => group.First());
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        foreach (var _ in columns) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 64 });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < columns.Length; c++)
        {
            var header = new TextBlock { Text = columns[c], FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 2, 6) };
            header.SetResourceReference(TextBlock.FontSizeProperty, "AppFont.Size.Small");
            Grid.SetColumn(header, c + 1);
            grid.Children.Add(header);
        }

        for (var r = 0; r < rows.Length; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = rows[r], TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetRow(label, r + 1);
            grid.Children.Add(label);
            for (var c = 0; c < columns.Length; c++)
            {
                lookup.TryGetValue((rows[r], columns[c]), out var cell);
                var color = cell?.Value is { } v ? Scale(v, HighIsGood) : Colors.Transparent;
                var box = new Border
                {
                    Margin = new Thickness(2), MinHeight = 34, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(color), Cursor = cell is null ? Cursors.Arrow : Cursors.Hand,
                    RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1)
                };
                if (cell?.Value is null) box.SetResourceReference(Border.BackgroundProperty, "AppBrush.SurfaceSecondary");
                var text = new TextBlock { Text = cell?.Text ?? "—", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold,
                    Foreground = cell?.Value is null ? (Brush?)TryFindResource("AppBrush.TextSecondary") ?? Brushes.Gray : Brushes.White };
                box.Child = text;
                if (cell is not null)
                {
                    box.ToolTip = Tip($"{cell.Row} × {cell.Column}", cell.Detail ?? cell.Text);
                    box.MouseEnter += (_, _) => ((ScaleTransform)box.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.06, TimeSpan.FromSeconds(0.12)));
                    box.MouseEnter += (_, _) => ((ScaleTransform)box.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.06, TimeSpan.FromSeconds(0.12)));
                    box.MouseLeave += (_, _) => ((ScaleTransform)box.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.12)));
                    box.MouseLeave += (_, _) => ((ScaleTransform)box.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.12)));
                    box.MouseLeftButtonUp += (_, _) => Activate(cell);
                }

                Grid.SetRow(box, r + 1);
                Grid.SetColumn(box, c + 1);
                grid.Children.Add(box);
            }
        }

        var panel = new StackPanel { Children = { new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled } } };
        var scale = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(200, 8, 0, 0) };
        foreach (var step in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
            scale.Children.Add(new Border { Width = 28, Height = 10, Background = new SolidColorBrush(Scale(step, HighIsGood)) });
        scale.Children.Add(Caption("  " + (LegendText ?? (HighIsGood ? "low → high (higher is better)" : "low → high (higher is worse)"))));
        panel.Children.Add(scale);
        return panel;
    }
}
