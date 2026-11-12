namespace EngineeringWorkspace.Configuration.Appearance;

/// <summary>Effective appearance (built-in GDS defaults overlaid with ApplicationProperties.json).</summary>
public sealed record ApplicationProperties(int SchemaVersion, AppearanceProperties Appearance)
{
    public static ApplicationProperties BuiltInDefaults { get; } = new(AppearanceSchema.SchemaVersion, new AppearanceProperties(
        new ThemeProperties(ThemeMode.System, true),
        TypographyProperties.FromValues(new Dictionary<string, object>()),
        LayoutProperties.FromValues(new Dictionary<string, object>()),
        new ThemeVariantProperties(ThemeColorProperties.Defaults(dark: false)),
        new ThemeVariantProperties(ThemeColorProperties.Defaults(dark: true))));
}

public sealed record AppearanceProperties(
    ThemeProperties Theme,
    TypographyProperties Typography,
    LayoutProperties Layout,
    ThemeVariantProperties Light,
    ThemeVariantProperties Dark)
{
    public ThemeVariantProperties For(ThemeMode resolvedMode) => resolvedMode == ThemeMode.Dark ? Dark : Light;
}

public sealed record ThemeProperties(ThemeMode Mode, bool AllowUserSelection);

public sealed record ThemeVariantProperties(ThemeColorProperties Colors);

public sealed record TypographyProperties(
    string FontFamily,
    double BaseFontSize,
    double SmallFontSize,
    double CaptionFontSize,
    double NavigationFontSize,
    double NavigationGroupFontSize,
    double Heading1FontSize,
    double Heading2FontSize,
    double Heading3FontSize,
    double StatusBarFontSize,
    double ButtonFontSize,
    string NormalFontWeight,
    string MediumFontWeight,
    string HeadingFontWeight)
{
    /// <summary>Builds the record from resolved values keyed by JSON path; missing entries take the schema default.</summary>
    public static TypographyProperties FromValues(IReadOnlyDictionary<string, object> values)
    {
        T Get<T>(string name) => (T)(values.TryGetValue($"appearance.typography.{name}", out var value)
            ? value
            : AppearanceSchema.Typography.Single(property => property.Path == $"appearance.typography.{name}").Default);
        return new(Get<string>("fontFamily"), Get<double>("baseFontSize"), Get<double>("smallFontSize"), Get<double>("captionFontSize"),
            Get<double>("navigationFontSize"), Get<double>("navigationGroupFontSize"), Get<double>("heading1FontSize"), Get<double>("heading2FontSize"),
            Get<double>("heading3FontSize"), Get<double>("statusBarFontSize"), Get<double>("buttonFontSize"), Get<string>("normalFontWeight"),
            Get<string>("mediumFontWeight"), Get<string>("headingFontWeight"));
    }
}

public sealed record LayoutProperties(
    double NavigationWidth,
    double NavigationCollapsedWidth,
    double HeaderHeight,
    double StatusBarHeight,
    double ControlHeight,
    double ButtonMinHeight,
    double InputMinHeight,
    double CornerRadius,
    double LargeCornerRadius,
    double SpacingExtraSmall,
    double SpacingSmall,
    double SpacingMedium,
    double SpacingLarge,
    double SpacingExtraLarge)
{
    public static LayoutProperties FromValues(IReadOnlyDictionary<string, object> values)
    {
        double Get(string name) => (double)(values.TryGetValue($"appearance.layout.{name}", out var value)
            ? value
            : AppearanceSchema.Layout.Single(property => property.Path == $"appearance.layout.{name}").Default);
        return new(Get("navigationWidth"), Get("navigationCollapsedWidth"), Get("headerHeight"), Get("statusBarHeight"), Get("controlHeight"),
            Get("buttonMinHeight"), Get("inputMinHeight"), Get("cornerRadius"), Get("largeCornerRadius"), Get("spacingExtraSmall"), Get("spacingSmall"),
            Get("spacingMedium"), Get("spacingLarge"), Get("spacingExtraLarge"));
    }
}

/// <summary>Resolved colors of one variant, keyed by camelCase token with typed accessors.</summary>
public sealed class ThemeColorProperties
{
    private readonly IReadOnlyDictionary<string, string> _colors;

    public ThemeColorProperties(IReadOnlyDictionary<string, string> colors)
    {
        var missing = AppearanceSchema.Colors.Where(token => !colors.ContainsKey(token.Path)).Select(token => token.Path).ToArray();
        if (missing.Length > 0) throw new ArgumentException($"Missing color tokens: {string.Join(", ", missing)}.", nameof(colors));
        _colors = new Dictionary<string, string>(colors, StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, string> Tokens => _colors;

    public string this[string token] => _colors[token];

    public static ThemeColorProperties Defaults(bool dark) =>
        new(AppearanceSchema.Colors.ToDictionary(token => token.Path, token => (string)(dark ? token.DarkDefault! : token.Default), StringComparer.Ordinal));

    public string Primary => this["primary"];
    public string PrimaryForeground => this["primaryForeground"];
    public string Secondary => this["secondary"];
    public string SecondaryForeground => this["secondaryForeground"];
    public string Accent => this["accent"];
    public string AccentForeground => this["accentForeground"];
    public string ApplicationBackground => this["applicationBackground"];
    public string SurfaceBackground => this["surfaceBackground"];
    public string SurfaceSecondary => this["surfaceSecondary"];
    public string NavigationBackground => this["navigationBackground"];
    public string NavigationForeground => this["navigationForeground"];
    public string NavigationSelectedBackground => this["navigationSelectedBackground"];
    public string NavigationSelectedForeground => this["navigationSelectedForeground"];
    public string NavigationHoverBackground => this["navigationHoverBackground"];
    public string HeaderBackground => this["headerBackground"];
    public string HeaderForeground => this["headerForeground"];
    public string StatusBarBackground => this["statusBarBackground"];
    public string StatusBarForeground => this["statusBarForeground"];
    public string TextPrimary => this["textPrimary"];
    public string TextSecondary => this["textSecondary"];
    public string TextDisabled => this["textDisabled"];
    public string Border => this["border"];
    public string Divider => this["divider"];
    public string InputBackground => this["inputBackground"];
    public string InputForeground => this["inputForeground"];
    public string InputBorder => this["inputBorder"];
    public string ButtonBackground => this["buttonBackground"];
    public string ButtonForeground => this["buttonForeground"];
    public string ButtonHoverBackground => this["buttonHoverBackground"];
    public string ButtonPressedBackground => this["buttonPressedBackground"];
    public string Success => this["success"];
    public string Warning => this["warning"];
    public string Error => this["error"];
    public string Information => this["information"];
}
