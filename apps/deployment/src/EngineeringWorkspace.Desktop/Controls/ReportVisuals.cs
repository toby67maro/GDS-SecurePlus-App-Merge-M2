using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EngineeringWorkspace.Desktop.Controls;

/// <summary>KPI tiles (items with Title, Value and Detail) in a wrap panel.</summary>
public sealed class KpiPanel : ItemsControl
{
    public KpiPanel()
    {
        Focusable = false;
        ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
        ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
            """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Border Style="{DynamicResource Kpi.Tile}">
                    <StackPanel>
                        <TextBlock Text="{Binding Title}" Style="{DynamicResource Text.Secondary}" />
                        <TextBlock Text="{Binding Value}" Style="{DynamicResource Text.KpiValue}" />
                        <TextBlock Text="{Binding Detail}" Style="{DynamicResource Text.Caption}" TextWrapping="Wrap" MaxWidth="220" />
                    </StackPanel>
                </Border>
            </DataTemplate>
            """);
    }
}

/// <summary>Horizontal bar chart: items with Label, Width (pixels), Fill and Text.</summary>
public sealed class BarChart : ItemsControl
{
    public BarChart()
    {
        Focusable = false;
        ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
            """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Grid Margin="0,2">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="230" />
                        <ColumnDefinition Width="Auto" />
                        <ColumnDefinition Width="*" />
                    </Grid.ColumnDefinitions>
                    <TextBlock Text="{Binding Label}" TextTrimming="CharacterEllipsis" TextWrapping="NoWrap" VerticalAlignment="Center" ToolTip="{Binding Label}" />
                    <Border Grid.Column="1" Width="320" Height="14" Background="{DynamicResource AppBrush.SurfaceSecondary}" CornerRadius="2" VerticalAlignment="Center">
                        <Border Width="{Binding Width}" HorizontalAlignment="Left" Background="{Binding Fill}" CornerRadius="2" ToolTip="{Binding Text}" />
                    </Border>
                    <TextBlock Grid.Column="2" Text="{Binding Text}" Margin="8,0,0,0" VerticalAlignment="Center" Style="{DynamicResource Text.Caption}" />
                </Grid>
            </DataTemplate>
            """);
    }
}

/// <summary>"Export: .csv .html .json .txt" buttons; each calls <see cref="Command"/> with Csv, Html, Json or Txt.</summary>
public sealed class ExportButtons : StackPanel
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(ExportButtons),
        new PropertyMetadata(null, (d, e) => ((ExportButtons)d).Apply((ICommand?)e.NewValue)));

    public ExportButtons()
    {
        Orientation = Orientation.Horizontal;
        var label = new TextBlock { Text = "Export:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 8) };
        Children.Add(label);
        foreach (var (format, text) in new[] { ("Csv", ".csv"), ("Html", ".html"), ("Json", ".json"), ("Txt", ".txt") })
        {
            var button = new Button { Content = text, CommandParameter = format, ToolTip = $"Export to {text}" };
            button.SetResourceReference(StyleProperty, "Button.Secondary");
            Children.Add(button);
        }
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    private void Apply(ICommand? command)
    {
        foreach (var button in Children.OfType<Button>()) button.Command = command;
    }
}
