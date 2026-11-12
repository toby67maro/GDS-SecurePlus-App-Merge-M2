using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace EngineeringWorkspace.Desktop.Controls;

/// <summary>
/// Read-only, selectable code with light syntax highlighting (JSON, PowerShell, HTTP) and a Copy button. Colours come
/// from theme brushes so light and dark themes both read well.
/// </summary>
public sealed class CodeView : UserControl
{
    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(nameof(Code), typeof(string), typeof(CodeView),
        new PropertyMetadata(string.Empty, (d, _) => ((CodeView)d).Render()));

    public static readonly DependencyProperty CodeLanguageProperty = DependencyProperty.Register(nameof(CodeLanguage), typeof(string), typeof(CodeView),
        new PropertyMetadata("json", (d, _) => ((CodeView)d).Render()));

    private static readonly Regex Json = new(
        "(?<key>\"(?:[^\"\\\\]|\\\\.)*\"(?=\\s*:))|(?<string>\"(?:[^\"\\\\]|\\\\.)*\")|(?<number>-?\\b\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?\\b)|(?<keyword>\\b(?:true|false|null)\\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PowerShell = new(
        @"(?<comment>#[^\r\n]*)|(?<string>'(?:[^']|'')*'|""(?:[^""`]|`.)*"")|(?<variable>\$[A-Za-z_][\w:]*)|(?<command>\b[A-Z][a-z]+-[A-Za-z]+\b)|(?<keyword>\b(?:if|else|elseif|foreach|for|while|return|function|param|try|catch|finally|throw)\b)|(?<parameter>(?<=\s)-[A-Za-z]+\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly RichTextBox _box;

    public CodeView()
    {
        _box = new RichTextBox
        {
            IsReadOnly = true,
            IsDocumentEnabled = true,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6),
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
            FontSize = 11.5,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 360
        };
        _box.SetResourceReference(BackgroundProperty, "AppBrush.SurfaceSecondary");
        _box.SetResourceReference(ForegroundProperty, "AppBrush.TextPrimary");
        var copy = new Button { Content = "Copy", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 0, 4), HorizontalAlignment = HorizontalAlignment.Right };
        copy.SetResourceReference(StyleProperty, "Button.Secondary");
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(Code ?? string.Empty);
                copy.Content = "Copied";
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                copy.Content = "Clipboard busy – try again";
            }
        };
        copy.MouseLeave += (_, _) => copy.Content = "Copy";
        var panel = new DockPanel();
        DockPanel.SetDock(copy, Dock.Top);
        panel.Children.Add(copy);
        var frame = new Border { BorderThickness = new Thickness(1), Child = _box };
        frame.SetResourceReference(Border.BorderBrushProperty, "AppBrush.Divider");
        panel.Children.Add(frame);
        Content = panel;
    }

    public string Code
    {
        get => (string)GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    /// <summary>"json", "powershell" or "text".</summary>
    public string CodeLanguage
    {
        get => (string)GetValue(CodeLanguageProperty);
        set => SetValue(CodeLanguageProperty, value);
    }

    private void Render()
    {
        var code = Code ?? string.Empty;
        var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 1 };
        var pattern = CodeLanguage?.ToLowerInvariant() switch
        {
            "json" => Json,
            "powershell" => PowerShell,
            _ => null
        };
        if (pattern is null || code.Length > 400_000)
        {
            paragraph.Inlines.Add(new Run(code));
        }
        else
        {
            var position = 0;
            foreach (Match match in pattern.Matches(code))
            {
                if (match.Index > position) paragraph.Inlines.Add(new Run(code[position..match.Index]));
                var run = new Run(match.Value);
                var group = pattern.GetGroupNames().FirstOrDefault(name => !char.IsDigit(name[0]) && match.Groups[name].Success);
                run.SetResourceReference(TextElement.ForegroundProperty, group switch
                {
                    "key" or "command" => "AppBrush.Secondary",
                    "string" => "AppBrush.Success",
                    "number" or "variable" => "AppBrush.Information",
                    "keyword" or "parameter" => "AppBrush.Warning",
                    "comment" => "AppBrush.TextSecondary",
                    _ => "AppBrush.TextPrimary"
                });
                if (group is "key" or "command") run.FontWeight = FontWeights.SemiBold;
                paragraph.Inlines.Add(run);
                position = match.Index + match.Length;
            }

            if (position < code.Length) paragraph.Inlines.Add(new Run(code[position..]));
        }

        _box.Document = new FlowDocument(paragraph) { PageWidth = 4000, PagePadding = new Thickness(0) };
    }
}
