using System.Windows;
using System.Windows.Media;
using EngineeringWorkspace.Configuration.Appearance;

namespace EngineeringWorkspace.Desktop.Theming;

/// <summary>
/// Converts effective appearance properties into the WPF resource dictionary. Keys: AppColor.&lt;Token&gt;,
/// AppBrush.&lt;Token&gt; (PascalCase tokens), AppFont.*, AppLayout.*, AppSpacing.* and AppCorner.*. Views never
/// hard-code these values.
/// </summary>
public static class ThemeResourceBuilder
{
    public static ResourceDictionary Build(AppearanceProperties appearance, ThemeMode effectiveMode)
    {
        var dictionary = new ResourceDictionary();
        var colors = appearance.For(effectiveMode).Colors;
        foreach (var token in AppearanceSchema.Colors)
        {
            var name = char.ToUpperInvariant(token.Path[0]) + token.Path[1..];
            var color = (Color)ColorConverter.ConvertFromString(colors[token.Path]);
            dictionary[$"AppColor.{name}"] = color;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            dictionary[$"AppBrush.{name}"] = brush;
        }

        // Header logo: the white GDS logo on dark headers, the navy logo on light ones.
        var header = colors.HeaderBackground;
        var logo = new System.Windows.Media.Imaging.BitmapImage(new Uri(
            ThemePresets.IsDark(header) ? "pack://application:,,,/Assets/gds-logo-white.png" : "pack://application:,,,/Assets/gds-logo.png", UriKind.Absolute));
        logo.Freeze();
        dictionary["AppImage.HeaderLogo"] = logo;

        // Chart series colors: the theme's accents first, then a balanced categorical palette.
        string[] series = [colors.ButtonBackground, colors.Accent, colors.Success, colors.Warning, colors.Error, colors.Information, "#8764B8", "#E3008C", "#00B7C3", "#CA5010"];
        for (var index = 0; index < series.Length; index++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(series[index]));
            brush.Freeze();
            dictionary[$"AppChart.Series{index + 1}"] = brush;
        }

        var typography = appearance.Typography;
        dictionary["AppFont.Family"] = FontFamilyOrDefault(typography.FontFamily);
        dictionary["AppFont.Size.Caption"] = typography.CaptionFontSize;
        dictionary["AppFont.Size.Small"] = typography.SmallFontSize;
        dictionary["AppFont.Size.Base"] = typography.BaseFontSize;
        dictionary["AppFont.Size.Navigation"] = typography.NavigationFontSize;
        dictionary["AppFont.Size.NavigationGroup"] = typography.NavigationGroupFontSize;
        dictionary["AppFont.Size.Heading1"] = typography.Heading1FontSize;
        dictionary["AppFont.Size.Heading2"] = typography.Heading2FontSize;
        dictionary["AppFont.Size.Heading3"] = typography.Heading3FontSize;
        dictionary["AppFont.Size.StatusBar"] = typography.StatusBarFontSize;
        dictionary["AppFont.Size.Button"] = typography.ButtonFontSize;
        dictionary["AppFont.Weight.Normal"] = Weight(typography.NormalFontWeight, FontWeights.Normal);
        dictionary["AppFont.Weight.Medium"] = Weight(typography.MediumFontWeight, FontWeights.Medium);
        dictionary["AppFont.Weight.Heading"] = Weight(typography.HeadingFontWeight, FontWeights.SemiBold);

        var layout = appearance.Layout;
        dictionary["AppLayout.NavigationWidth"] = layout.NavigationWidth;
        dictionary["AppLayout.NavigationCollapsedWidth"] = layout.NavigationCollapsedWidth;
        dictionary["AppLayout.HeaderHeight"] = layout.HeaderHeight;
        dictionary["AppLayout.StatusBarHeight"] = layout.StatusBarHeight;
        dictionary["AppLayout.ControlHeight"] = layout.ControlHeight;
        dictionary["AppLayout.ButtonMinHeight"] = layout.ButtonMinHeight;
        dictionary["AppLayout.InputMinHeight"] = layout.InputMinHeight;
        dictionary["AppSpacing.XS"] = new Thickness(layout.SpacingExtraSmall);
        dictionary["AppSpacing.S"] = new Thickness(layout.SpacingSmall);
        dictionary["AppSpacing.M"] = new Thickness(layout.SpacingMedium);
        dictionary["AppSpacing.L"] = new Thickness(layout.SpacingLarge);
        dictionary["AppSpacing.XL"] = new Thickness(layout.SpacingExtraLarge);
        dictionary["AppCorner.Default"] = new CornerRadius(layout.CornerRadius);
        dictionary["AppCorner.Large"] = new CornerRadius(layout.LargeCornerRadius);
        return dictionary;
    }

    private static FontFamily FontFamilyOrDefault(string name)
    {
        var family = new FontFamily(name);
        // An unknown family falls back silently in WPF; keep the configured name, with Segoe UI as fallback chain.
        return string.Equals(name, "Segoe UI", StringComparison.OrdinalIgnoreCase) ? family : new FontFamily($"{name}, Segoe UI");
    }

    private static FontWeight Weight(string value, FontWeight fallback) =>
        new FontWeightConverter().ConvertFromInvariantString(value) is FontWeight weight ? weight : fallback;
}
