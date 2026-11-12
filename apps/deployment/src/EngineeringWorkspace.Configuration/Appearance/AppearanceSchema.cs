namespace EngineeringWorkspace.Configuration.Appearance;

public enum ThemeMode
{
    Light,
    Dark,
    System
}

public enum AppearanceValueKind
{
    Mode,
    Boolean,
    FontFamily,
    FontSize,
    FontWeight,
    Dimension,
    Color
}

/// <summary>One property of ApplicationProperties.json: path, type, built-in GDS default, allowed range and description.</summary>
public sealed record AppearanceProperty(
    string Path,
    AppearanceValueKind Kind,
    object Default,
    object? DarkDefault,
    double Minimum,
    double Maximum,
    string Description);

/// <summary>
/// The single place that defines the built-in GDS appearance: every property, its default and its allowed range.
/// Sources of the colors are recorded in docs/architecture/theme-system.md.
/// </summary>
public static class AppearanceSchema
{
    public const int SchemaVersion = 1;

    public static IReadOnlyList<string> FontWeights { get; } =
        ["Thin", "ExtraLight", "UltraLight", "Light", "Normal", "Regular", "Medium", "DemiBold", "SemiBold", "Bold", "ExtraBold", "UltraBold", "Black", "Heavy", "ExtraBlack", "UltraBlack"];

    /// <summary>Color tokens in schema order. Resource keys are AppColor.&lt;Token&gt; and AppBrush.&lt;Token&gt; in PascalCase.</summary>
    public static IReadOnlyList<AppearanceProperty> Colors { get; } =
    [
        Color("primary", "#002060", "#72E1F5", "Primary brand color (GDS navy)."),
        Color("primaryForeground", "#FFFFFF", "#001433", "Text and icons on primary."),
        Color("secondary", "#156082", "#156082", "Secondary brand color (GDS teal)."),
        Color("secondaryForeground", "#FFFFFF", "#FFFFFF", "Text and icons on secondary."),
        Color("accent", "#72E1F5", "#AAEDF9", "Accent color (GDS cyan)."),
        Color("accentForeground", "#002060", "#001433", "Text and icons on accent."),
        Color("applicationBackground", "#F3F6FA", "#0A1426", "Window background."),
        Color("surfaceBackground", "#FFFFFF", "#111E36", "Page and card background."),
        Color("surfaceSecondary", "#E4F2F5", "#182A4A", "Secondary surfaces such as panels and table headers."),
        Color("navigationBackground", "#002060", "#001433", "Left navigation background."),
        Color("navigationForeground", "#E6EDF7", "#D3F1FC", "Left navigation text."),
        Color("navigationSelectedBackground", "#156082", "#156082", "Active navigation item background."),
        Color("navigationSelectedForeground", "#FFFFFF", "#FFFFFF", "Active navigation item text."),
        Color("navigationHoverBackground", "#0F3A73", "#0B2A5A", "Navigation item background on hover."),
        Color("headerBackground", "#002060", "#002060", "Header background."),
        Color("headerForeground", "#FFFFFF", "#FFFFFF", "Header text."),
        Color("statusBarBackground", "#115472", "#001433", "Status bar background."),
        Color("statusBarForeground", "#FFFFFF", "#D3F1FC", "Status bar text."),
        Color("textPrimary", "#1F1F1F", "#F2F5FA", "Primary text."),
        Color("textSecondary", "#4D4D4D", "#B8C4D6", "Secondary text."),
        Color("textDisabled", "#7F7F7F", "#6E7C93", "Disabled text."),
        Color("border", "#BFBFBF", "#33476B", "Control and card borders."),
        Color("divider", "#D9D9D9", "#26385A", "Dividers and grid lines."),
        Color("inputBackground", "#FFFFFF", "#0D1830", "Input background."),
        Color("inputForeground", "#1F1F1F", "#F2F5FA", "Input text."),
        Color("inputBorder", "#7F7F7F", "#4A5F85", "Input border."),
        Color("buttonBackground", "#156082", "#156082", "Button background."),
        Color("buttonForeground", "#FFFFFF", "#FFFFFF", "Button text."),
        Color("buttonHoverBackground", "#115472", "#1C77A0", "Button background on hover."),
        Color("buttonPressedBackground", "#002060", "#115472", "Button background while pressed."),
        Color("success", "#107C10", "#6CCB5F", "Success status."),
        Color("warning", "#9D5D00", "#FCE100", "Warning status."),
        Color("error", "#C42B1C", "#FF99A4", "Error status."),
        Color("information", "#156082", "#72E1F5", "Information status."),
    ];

    public static IReadOnlyList<AppearanceProperty> Theme { get; } =
    [
        new("appearance.theme.mode", AppearanceValueKind.Mode, ThemeMode.System, null, 0, 0, "Light, Dark or System (follows the Windows app theme)."),
        new("appearance.theme.allowUserSelection", AppearanceValueKind.Boolean, true, null, 0, 0, "Show the Light/Dark/System selector in the header and on the Appearance page."),
    ];

    public static IReadOnlyList<AppearanceProperty> Typography { get; } =
    [
        new("appearance.typography.fontFamily", AppearanceValueKind.FontFamily, "Segoe UI", null, 1, 200, "Font family name (1-200 characters)."),
        Size("baseFontSize", 13, 9, 32, "Body text."),
        Size("smallFontSize", 11.5, 9, 32, "Small text."),
        Size("captionFontSize", 10.5, 9, 32, "Captions."),
        Size("navigationFontSize", 13, 9, 32, "Navigation items."),
        Size("navigationGroupFontSize", 12, 9, 32, "Navigation section and group headers."),
        Size("heading1FontSize", 22, 12, 72, "Page titles."),
        Size("heading2FontSize", 17, 12, 72, "Section titles."),
        Size("heading3FontSize", 14.5, 12, 72, "Sub-section titles."),
        Size("statusBarFontSize", 11, 9, 32, "Status bar text."),
        Size("buttonFontSize", 13, 9, 32, "Button text."),
        new("appearance.typography.normalFontWeight", AppearanceValueKind.FontWeight, "Normal", null, 0, 0, "Body weight."),
        new("appearance.typography.mediumFontWeight", AppearanceValueKind.FontWeight, "Medium", null, 0, 0, "Emphasis weight."),
        new("appearance.typography.headingFontWeight", AppearanceValueKind.FontWeight, "SemiBold", null, 0, 0, "Heading weight."),
    ];

    public static IReadOnlyList<AppearanceProperty> Layout { get; } =
    [
        Dimension("navigationWidth", 250, 160, 480, "Expanded navigation width."),
        Dimension("navigationCollapsedWidth", 64, 40, 120, "Collapsed navigation width."),
        Dimension("headerHeight", 52, 40, 120, "Header height."),
        Dimension("statusBarHeight", 24, 20, 48, "Status bar height."),
        Dimension("controlHeight", 30, 24, 64, "Default control height."),
        Dimension("buttonMinHeight", 30, 24, 64, "Minimum button height."),
        Dimension("inputMinHeight", 30, 24, 64, "Minimum input height."),
        Dimension("cornerRadius", 4, 0, 16, "Default corner radius."),
        Dimension("largeCornerRadius", 8, 0, 32, "Corner radius of cards and dialogs."),
        Dimension("spacingExtraSmall", 4, 0, 48, "Spacing XS."),
        Dimension("spacingSmall", 8, 0, 48, "Spacing S."),
        Dimension("spacingMedium", 10, 0, 48, "Spacing M."),
        Dimension("spacingLarge", 14, 0, 48, "Spacing L."),
        Dimension("spacingExtraLarge", 18, 0, 48, "Spacing XL."),
    ];

    /// <summary>Foreground/background pairs checked for contrast (WCAG 2.x ratio; warning below 4.5).</summary>
    public static IReadOnlyList<(string Foreground, string Background)> ContrastPairs { get; } =
    [
        ("textPrimary", "applicationBackground"), ("textPrimary", "surfaceBackground"), ("textSecondary", "surfaceBackground"),
        ("navigationForeground", "navigationBackground"), ("navigationSelectedForeground", "navigationSelectedBackground"),
        ("headerForeground", "headerBackground"), ("statusBarForeground", "statusBarBackground"), ("buttonForeground", "buttonBackground"),
        ("inputForeground", "inputBackground"), ("primaryForeground", "primary"), ("secondaryForeground", "secondary"), ("accentForeground", "accent"),
    ];

    private static AppearanceProperty Color(string token, string light, string dark, string description) =>
        new(token, AppearanceValueKind.Color, light, dark, 0, 0, description);

    private static AppearanceProperty Size(string name, double value, double minimum, double maximum, string description) =>
        new($"appearance.typography.{name}", AppearanceValueKind.FontSize, value, null, minimum, maximum, description);

    private static AppearanceProperty Dimension(string name, double value, double minimum, double maximum, string description) =>
        new($"appearance.layout.{name}", AppearanceValueKind.Dimension, value, null, minimum, maximum, description);
}
