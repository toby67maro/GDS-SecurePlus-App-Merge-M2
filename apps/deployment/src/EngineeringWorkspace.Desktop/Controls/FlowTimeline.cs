using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace EngineeringWorkspace.Desktop.Controls;

/// <summary>A box on the flow timeline: placed by working-hour offsets, with the keys of the boxes it depends on.</summary>
public sealed record TimelineNode(string Key, string Label, string Sublabel, double StartHour, double FinishHour, IReadOnlyList<string> DependsOn, string? Color, string Detail,
    object? Tag = null);

/// <summary>
/// Left-to-right work timeline on a Monday-Friday calendar: work weeks and days across the top, each node a bar in a
/// lane, arrows from every dependency to the work that needs it. Hover highlights a node and its arrows; click runs
/// <see cref="ChartBase.Command"/> with the node.
/// </summary>
public sealed class FlowTimeline : Border
{
    public static readonly DependencyProperty NodesProperty = DependencyProperty.Register(nameof(Nodes), typeof(IEnumerable), typeof(FlowTimeline),
        new PropertyMetadata(null, (d, e) =>
        {
            var timeline = (FlowTimeline)d;
            if (e.OldValue is System.Collections.Specialized.INotifyCollectionChanged old) old.CollectionChanged -= timeline.OnChanged;
            if (e.NewValue is System.Collections.Specialized.INotifyCollectionChanged now) now.CollectionChanged += timeline.OnChanged;
            timeline.Queue();
        }));
    public static readonly DependencyProperty StartDateProperty = DependencyProperty.Register(nameof(StartDate), typeof(DateTime), typeof(FlowTimeline),
        new PropertyMetadata(DateTime.Today, (d, _) => ((FlowTimeline)d).Queue()));
    public static readonly DependencyProperty HoursPerDayProperty = DependencyProperty.Register(nameof(HoursPerDay), typeof(double), typeof(FlowTimeline),
        new PropertyMetadata(8.0, (d, _) => ((FlowTimeline)d).Queue()));
    public static readonly DependencyProperty PixelsPerDayProperty = DependencyProperty.Register(nameof(PixelsPerDay), typeof(double), typeof(FlowTimeline),
        new PropertyMetadata(48.0, (d, _) => ((FlowTimeline)d).Queue()));
    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(FlowTimeline));

    private const double LaneHeight = 46;
    private const double HeaderHeight = 44;
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 640 };
    private readonly Dictionary<string, (Border Box, List<Path> Arrows)> _visuals = new(StringComparer.OrdinalIgnoreCase);
    private bool _queued;

    public FlowTimeline()
    {
        SetResourceReference(BackgroundProperty, "AppBrush.SurfaceBackground");
        SetResourceReference(BorderBrushProperty, "AppBrush.Divider");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(8);
        Child = _scroll;
        Loaded += (_, _) => Build();
    }

    public IEnumerable? Nodes { get => (IEnumerable?)GetValue(NodesProperty); set => SetValue(NodesProperty, value); }
    public DateTime StartDate { get => (DateTime)GetValue(StartDateProperty); set => SetValue(StartDateProperty, value); }
    public double HoursPerDay { get => (double)GetValue(HoursPerDayProperty); set => SetValue(HoursPerDayProperty, value); }

    /// <summary>Zoom: width of one work day in pixels.</summary>
    public double PixelsPerDay { get => (double)GetValue(PixelsPerDayProperty); set => SetValue(PixelsPerDayProperty, value); }

    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    /// <summary>The drawn canvas (for exporting the timeline as an image).</summary>
    public FrameworkElement? Surface => _scroll.Content as FrameworkElement;

    private void OnChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Queue();

    private void Queue()
    {
        if (_queued || !IsLoaded) return;
        _queued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _queued = false;
            Build();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void Build()
    {
        _visuals.Clear();
        var nodes = Nodes?.OfType<TimelineNode>().OrderBy(node => node.StartHour).ThenBy(node => node.Key, StringComparer.Ordinal).ToArray() ?? [];
        if (nodes.Length == 0)
        {
            _scroll.Content = new TextBlock { Text = "Nothing to schedule.", Margin = new Thickness(8) };
            return;
        }

        var hoursPerDay = HoursPerDay <= 0 ? 8 : HoursPerDay;
        var pixelsPerHour = Math.Max(1, PixelsPerDay) / hoursPerDay;
        var totalHours = nodes.Max(node => node.FinishHour);
        var days = (int)Math.Ceiling(totalHours / hoursPerDay) + 1;
        var width = days * PixelsPerDay + 40;

        // Lanes: each node goes in the first lane that is free when it starts.
        var laneEnds = new List<double>();
        var lanes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            var lane = laneEnds.FindIndex(end => end <= node.StartHour + 1e-6);
            if (lane < 0)
            {
                laneEnds.Add(0);
                lane = laneEnds.Count - 1;
            }

            laneEnds[lane] = Math.Max(node.FinishHour, node.StartHour + 30 / pixelsPerHour);
            lanes[node.Key] = lane;
        }

        var canvas = new Canvas { Width = width, Height = HeaderHeight + laneEnds.Count * LaneHeight + 16 };
        canvas.SetResourceReference(Panel.BackgroundProperty, "AppBrush.SurfaceBackground");
        var divider = (Brush?)TryFindResource("AppBrush.Divider") ?? Brushes.LightGray;
        var secondary = (Brush?)TryFindResource("AppBrush.TextSecondary") ?? Brushes.Gray;

        // Calendar header: a label per work week and a tick per day.
        var date = DateOnly.FromDateTime(StartDate);
        while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) date = date.AddDays(1);
        for (var day = 0; day < days; day++)
        {
            var x = day * PixelsPerDay;
            var isWeekStart = day == 0 || date.DayOfWeek == DayOfWeek.Monday;
            canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = isWeekStart ? 0 : 26, Y2 = canvas.Height, Stroke = divider, StrokeThickness = isWeekStart ? 1.2 : 0.5,
                StrokeDashArray = isWeekStart ? null : [2, 4] });
            if (isWeekStart)
            {
                var week = new TextBlock { Text = $"Week of {date:ddd d MMM}", FontWeight = FontWeights.SemiBold };
                week.SetResourceReference(TextBlock.FontSizeProperty, "AppFont.Size.Small");
                Canvas.SetLeft(week, x + 4);
                Canvas.SetTop(week, 2);
                canvas.Children.Add(week);
            }

            if (PixelsPerDay >= 30)
            {
                var label = new TextBlock { Text = date.ToString("ddd d"), Foreground = secondary };
                label.SetResourceReference(TextBlock.FontSizeProperty, "AppFont.Size.Caption");
                Canvas.SetLeft(label, x + 3);
                Canvas.SetTop(label, 24);
                canvas.Children.Add(label);
            }

            date = date.AddDays(1);
            while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) date = date.AddDays(1);
        }

        // Boxes.
        var boxes = new Dictionary<string, Rect>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            var x = node.StartHour * pixelsPerHour;
            var w = Math.Max(26, (node.FinishHour - node.StartHour) * pixelsPerHour - 2);
            var y = HeaderHeight + lanes[node.Key] * LaneHeight + 4;
            var fill = node.Color is { Length: > 0 } color && new BrushConverter().ConvertFromString(color) is Brush brush ? brush : (Brush?)TryFindResource("AppChart.Series1") ?? Brushes.SteelBlue;
            var box = new Border
            {
                Width = w, Height = LaneHeight - 10, CornerRadius = new CornerRadius(6), Background = fill, Cursor = Cursors.Hand, Padding = new Thickness(6, 2, 6, 2),
                ToolTip = new ToolTip { Content = new TextBlock { Text = node.Detail, MaxWidth = 420, TextWrapping = TextWrapping.Wrap } },
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = node.Label, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap },
                        new TextBlock { Text = node.Sublabel, Foreground = Brushes.White, Opacity = 0.85, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap }
                    }
                }
            };
            Canvas.SetLeft(box, x);
            Canvas.SetTop(box, y);
            Motion.SetHoverLift(box, true);
            box.MouseEnter += (_, _) => Highlight(node.Key, true);
            box.MouseLeave += (_, _) => Highlight(node.Key, false);
            box.MouseLeftButtonUp += (_, _) =>
            {
                if (Command?.CanExecute(node) == true) Command.Execute(node);
            };
            canvas.Children.Add(box);
            boxes[node.Key] = new Rect(x, y, w, LaneHeight - 10);
            _visuals[node.Key] = (box, []);
        }

        // Arrows from each dependency's right edge to the dependent's left edge.
        var arrowBrush = (Brush?)TryFindResource("AppBrush.TextSecondary") ?? Brushes.Gray;
        foreach (var node in nodes)
        foreach (var dependency in node.DependsOn.Where(boxes.ContainsKey))
        {
            var from = boxes[dependency];
            var to = boxes[node.Key];
            var start = new Point(from.Right, from.Top + from.Height / 2);
            var end = new Point(to.Left - 2, to.Top + to.Height / 2);
            var bend = Math.Max(24, (end.X - start.X) / 2);
            var figure = new PathFigure { StartPoint = start };
            figure.Segments.Add(new BezierSegment(new Point(start.X + bend, start.Y), new Point(end.X - bend, end.Y), end, true));
            var head = new PathFigure { StartPoint = end, IsClosed = true, IsFilled = true };
            head.Segments.Add(new LineSegment(new Point(end.X - 7, end.Y - 4), true));
            head.Segments.Add(new LineSegment(new Point(end.X - 7, end.Y + 4), true));
            var arrow = new Path { Data = new PathGeometry([figure, head]), Stroke = arrowBrush, Fill = arrowBrush, StrokeThickness = 1.4, Opacity = 0.65, IsHitTestVisible = false };
            Panel.SetZIndex(arrow, -1);
            canvas.Children.Add(arrow);
            _visuals[node.Key].Arrows.Add(arrow);
            _visuals[dependency].Arrows.Add(arrow);
        }

        _scroll.Content = canvas;
        Motion.Enter(canvas, 10, 0.35);
    }

    private void Highlight(string key, bool on)
    {
        if (!_visuals.TryGetValue(key, out var visual)) return;
        foreach (var arrow in visual.Arrows)
        {
            arrow.Opacity = on ? 1 : 0.65;
            arrow.StrokeThickness = on ? 2.6 : 1.4;
        }
    }
}
