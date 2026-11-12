using System.Globalization;

namespace EngineeringWorkspace.Configuration.Appearance;

/// <summary>
/// A built-in theme the user picks in the header or on the Appearance page. A preset is either one fixed palette
/// (Light, Dark, GDS Dark, Aurora...) or follows the Windows app theme (System, GDS). Names are our own; several are
/// inspired by familiar palettes (Copilot, Claude, Visio and Office/Visio diagram themes) as the descriptions say.
/// </summary>
public sealed record ThemePreset(string Name, string Description, string Family, ThemeColorProperties Light, ThemeColorProperties Dark, ThemeMode Mode)
{
    public bool IsDark => Mode == ThemeMode.Dark;

    /// <summary>The swatch shown in the theme picker: background, navigation, accent.</summary>
    public string SwatchBackground => (Mode == ThemeMode.Dark ? Dark : Light).ApplicationBackground;
    public string SwatchNavigation => (Mode == ThemeMode.Dark ? Dark : Light).NavigationBackground;
    public string SwatchAccent => (Mode == ThemeMode.Dark ? Dark : Light).ButtonBackground;

    public override string ToString() => Name;
}

public static class ThemePresets
{
    public const string SystemName = "System";
    public const string GdsName = "GDS (follows Windows)";

    /// <summary>Seed colors of a palette; every other token is derived.</summary>
    private sealed record Seed(bool Dark, string Background, string Surface, string Navigation, string Primary, string Accent,
        string? Header = null, string? Text = null, string? NavigationText = null, string? Secondary = null);

    public static IReadOnlyList<ThemePreset> All { get; } = Build();

    public static ThemePreset? Find(string? name) =>
        name is null ? null : All.FirstOrDefault(preset => string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<ThemePreset> Build()
    {
        var gdsLight = ThemeColorProperties.Defaults(dark: false);
        var gdsDark = ThemeColorProperties.Defaults(dark: true);
        var light = Derive(new Seed(false, "#F5F5F5", "#FFFFFF", "#EBEBEB", "#0F6CBD", "#0F6CBD", "#FFFFFF", "#1F1F1F", "#1F1F1F"));
        var dark = Derive(new Seed(true, "#1F1F1F", "#292929", "#141414", "#479EF5", "#479EF5", "#141414", "#F2F2F2", "#E6E6E6"));
        var presets = new List<ThemePreset>
        {
            new("Light", "Clean neutral light (Windows Fluent style).", "Standard", light, light, ThemeMode.Light),
            One("Medium", "Soft mid-grey surfaces: easy on the eyes in bright and dim rooms.", "Standard",
                new Seed(false, "#C9CDD3", "#DADDE2", "#3A3F47", "#2F5D8A", "#2F5D8A", "#3A3F47", "#15181C", "#F0F2F5")),
            new("Dark", "Neutral dark grey (Windows Fluent style).", "Standard", dark, dark, ThemeMode.Dark),
            new(SystemName, "Light or Dark, following the Windows app theme.", "Standard", light, dark, ThemeMode.System),
            new("GDS Light", "Global Data Systems navy, teal and cyan on light surfaces.", "GDS", gdsLight, gdsLight, ThemeMode.Light),
            new("GDS Dark", "Global Data Systems navy, teal and cyan on deep navy.", "GDS", gdsDark, gdsDark, ThemeMode.Dark),
            new(GdsName, "GDS Light or GDS Dark, following the Windows app theme.", "GDS", gdsLight, gdsDark, ThemeMode.System),
            One("GDS Globe", "The globe blue of the GDS logo with navy navigation.", "GDS",
                new Seed(false, "#EEF3FA", "#FFFFFF", "#0B2A5B", "#3F6FB5", "#3F6FB5", "#0B2A5B", "#14213D")),

            // Inspired by AI assistant palettes.
            One("Aurora Light", "Inspired by Copilot: soft lilac surfaces with violet-to-teal accents.", "Assistant",
                new Seed(false, "#F7F5FC", "#FFFFFF", "#EFEAFB", "#6B4FD8", "#0F9D9A", "#FFFFFF", "#1B1830", "#2A2346")),
            One("Aurora Dark", "Inspired by Copilot: midnight indigo with violet and teal glow.", "Assistant",
                new Seed(true, "#14122A", "#1E1B3A", "#0E0C20", "#9F8CFF", "#3DD6C9", "#0E0C20", "#EEEAFF", "#D8D1FF")),
            One("Clay Light", "Inspired by Claude: warm ivory paper with terracotta accents.", "Assistant",
                new Seed(false, "#F5F4EE", "#FBFAF6", "#EDEAE0", "#C96442", "#C96442", "#F5F4EE", "#2B2A26", "#3D3929")),
            One("Clay Dark", "Inspired by Claude: charcoal with warm terracotta.", "Assistant",
                new Seed(true, "#262624", "#30302E", "#1F1E1D", "#D97757", "#D97757", "#1F1E1D", "#F0EEE6", "#E3DFD3")),

            // Inspired by Visio and its diagram themes.
            One("Blueprint", "Inspired by Visio: diagram blue ribbon on a white canvas.", "Diagram",
                new Seed(false, "#F3F5F9", "#FFFFFF", "#3955A3", "#3955A3", "#2E86DE", "#3955A3", "#1E2433", "#FFFFFF")),
            One("Blueprint Night", "Inspired by Visio dark mode: blueprint lines on slate.", "Diagram",
                new Seed(true, "#1B2030", "#242B3E", "#141826", "#7C97E8", "#5FB7FF", "#141826", "#E8ECF7", "#D5DCF3")),
            One("Facet", "Inspired by the Facet diagram theme: fresh greens.", "Diagram",
                new Seed(false, "#F2F7F2", "#FFFFFF", "#2F6B3A", "#3E8E41", "#88C057", "#2F6B3A", "#1D2B1E", "#F1FAF0")),
            One("Ion", "Inspired by the Ion diagram theme: teal on deep ocean.", "Diagram",
                new Seed(true, "#0F2A2E", "#15373C", "#0A1F22", "#2EC4B6", "#9BE7DF", "#0A1F22", "#E6FAF8", "#CDF3EF")),
            One("Retrospect", "Inspired by the Retrospect diagram theme: burnt orange and stone.", "Diagram",
                new Seed(false, "#F6F3EF", "#FFFFFF", "#4A4540", "#C8641E", "#E39B4C", "#4A4540", "#2A2520", "#F6EEE6")),
            One("Integral", "Inspired by the Integral diagram theme: navy with steel blue.", "Diagram",
                new Seed(false, "#F1F4F8", "#FFFFFF", "#1E3A5F", "#2C5A8C", "#5B8DC9", "#1E3A5F", "#1A2433", "#E9F0F8")),
            One("Slice", "Inspired by the Slice diagram theme: dark teal with crisp cyan.", "Diagram",
                new Seed(true, "#10232B", "#173039", "#0B1A20", "#2BB3C8", "#7FDBEA", "#0B1A20", "#E4F6FA", "#C9EEF5")),
            One("Organic", "Inspired by the Organic diagram theme: olive, moss and sand.", "Diagram",
                new Seed(false, "#F4F2EA", "#FBFAF5", "#556B2F", "#6B8E23", "#B5A642", "#556B2F", "#26291C", "#F3F5EA")),
            One("Whisp", "Inspired by the Whisp diagram theme: airy lavender and periwinkle.", "Diagram",
                new Seed(false, "#F6F5FB", "#FFFFFF", "#E7E4F5", "#6A5ACD", "#8E9FE8", "#FFFFFF", "#26233A", "#2E2A4A")),
            One("Daybreak", "Inspired by the Daybreak diagram theme: sunrise coral and gold.", "Diagram",
                new Seed(false, "#FFF7F0", "#FFFFFF", "#7A2E3A", "#D9534F", "#F0A43A", "#7A2E3A", "#2E1F1F", "#FFEFEA")),
            One("Radiance", "Inspired by the Radiance diagram theme: gold light on graphite.", "Diagram",
                new Seed(true, "#1E1E22", "#28282E", "#141417", "#E0B341", "#F5D77A", "#141417", "#F5F2E8", "#EDE6CF")),
            One("Sequence", "Inspired by the Sequence diagram theme: plum and orchid.", "Diagram",
                new Seed(false, "#F7F3F8", "#FFFFFF", "#4B2142", "#8E3B7E", "#C77DBA", "#4B2142", "#2A1A28", "#F8ECF5")),

            // More moods.
            One("Midnight", "Near-black with electric blue: maximum focus at night.", "Mood",
                new Seed(true, "#0B0E14", "#131822", "#07090D", "#4C8DFF", "#4C8DFF", "#07090D", "#E6EBF5", "#C9D3E6")),
            One("Polar", "Arctic frost blues and snow (Nord-like).", "Mood",
                new Seed(false, "#ECEFF4", "#F8F9FB", "#2E3440", "#5E81AC", "#88C0D0", "#2E3440", "#2E3440", "#ECEFF4")),
            One("Polar Night", "Arctic night slate with frost accents (Nord-like).", "Mood",
                new Seed(true, "#2E3440", "#3B4252", "#242933", "#88C0D0", "#81A1C1", "#242933", "#ECEFF4", "#D8DEE9")),
            One("Solar Light", "Warm cream with balanced accents (Solarized-like).", "Mood",
                new Seed(false, "#FDF6E3", "#FFFBEF", "#EEE8D5", "#268BD2", "#2AA198", "#FDF6E3", "#073642", "#073642")),
            One("Solar Dark", "Deep teal-black with balanced accents (Solarized-like).", "Mood",
                new Seed(true, "#002B36", "#073642", "#00212B", "#2AA198", "#268BD2", "#00212B", "#EEE8D5", "#93A1A1")),
            One("Forest", "Pine greens and bark browns.", "Mood",
                new Seed(true, "#13201A", "#1A2B22", "#0D1712", "#5FB37C", "#C2A36B", "#0D1712", "#E5F2E9", "#CFE5D6")),
            One("Ocean", "Sea blues and sandy highlights.", "Mood",
                new Seed(false, "#EEF6F9", "#FFFFFF", "#0B4F6C", "#01BAEF", "#20BF55", "#0B4F6C", "#0D2A36", "#E6F6FC")),
            One("Slate", "Cool slate greys with indigo.", "Mood",
                new Seed(false, "#F1F3F5", "#FFFFFF", "#334155", "#4F46E5", "#4F46E5", "#334155", "#0F172A", "#E2E8F0")),
            One("Rose", "Blush pinks with deep berry.", "Mood",
                new Seed(false, "#FBF3F5", "#FFFFFF", "#6D1F3C", "#B83B5E", "#E07A9A", "#6D1F3C", "#2B1620", "#FBE8EF")),
            One("High Contrast", "Black, white and yellow for maximum legibility.", "Accessibility",
                new Seed(true, "#000000", "#000000", "#000000", "#FFFF00", "#00FFFF", "#000000", "#FFFFFF", "#FFFFFF")),
        };
        return presets;
    }

    private static ThemePreset One(string name, string description, string family, Seed seed)
    {
        var colors = Derive(seed);
        return new ThemePreset(name, description, family, colors, colors, seed.Dark ? ThemeMode.Dark : ThemeMode.Light);
    }

    /// <summary>Derives all color tokens from the seed, keeping text readable on every surface.</summary>
    private static ThemeColorProperties Derive(Seed s)
    {
        var text = s.Text ?? (s.Dark ? "#F2F2F2" : "#1F1F1F");
        var navText = s.NavigationText ?? ReadableOn(s.Navigation);
        var header = s.Header ?? s.Navigation;
        var secondary = s.Secondary ?? Mix(s.Primary, s.Dark ? "#FFFFFF" : "#000000", 0.15);
        var navSelected = IsDark(s.Navigation) ? Mix(s.Primary, s.Navigation, 0.25) : Mix(s.Primary, "#FFFFFF", 0.80);
        var navSelectedText = IsDark(s.Navigation) ? ReadableOn(navSelected) : Mix(s.Primary, "#000000", 0.35);
        var colors = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["primary"] = s.Primary,
            ["primaryForeground"] = ReadableOn(s.Primary),
            ["secondary"] = secondary,
            ["secondaryForeground"] = ReadableOn(secondary),
            ["accent"] = s.Accent,
            ["accentForeground"] = ReadableOn(s.Accent),
            ["applicationBackground"] = s.Background,
            ["surfaceBackground"] = s.Surface,
            ["surfaceSecondary"] = Mix(s.Surface, s.Primary, s.Dark ? 0.12 : 0.07),
            ["navigationBackground"] = s.Navigation,
            ["navigationForeground"] = navText,
            ["navigationSelectedBackground"] = navSelected,
            ["navigationSelectedForeground"] = navSelectedText,
            ["navigationHoverBackground"] = Mix(s.Navigation, IsDark(s.Navigation) ? "#FFFFFF" : "#000000", 0.08),
            ["headerBackground"] = header,
            ["headerForeground"] = ReadableOn(header),
            ["statusBarBackground"] = Mix(header, IsDark(header) ? "#000000" : "#FFFFFF", 0.15),
            ["statusBarForeground"] = ReadableOn(Mix(header, IsDark(header) ? "#000000" : "#FFFFFF", 0.15)),
            ["textPrimary"] = text,
            ["textSecondary"] = Mix(text, s.Surface, 0.30),
            ["textDisabled"] = Mix(text, s.Surface, 0.55),
            ["border"] = Mix(s.Surface, text, s.Dark ? 0.22 : 0.25),
            ["divider"] = Mix(s.Surface, text, s.Dark ? 0.14 : 0.12),
            ["inputBackground"] = s.Dark ? Mix(s.Surface, "#000000", 0.20) : "#FFFFFF",
            ["inputForeground"] = text,
            ["inputBorder"] = Mix(s.Surface, text, 0.40),
            ["buttonBackground"] = s.Primary,
            ["buttonForeground"] = ReadableOn(s.Primary),
            ["buttonHoverBackground"] = Mix(s.Primary, s.Dark ? "#FFFFFF" : "#000000", 0.12),
            ["buttonPressedBackground"] = Mix(s.Primary, s.Dark ? "#FFFFFF" : "#000000", 0.25),
            ["success"] = s.Dark ? "#6CCB5F" : "#107C10",
            ["warning"] = s.Dark ? "#FCE100" : "#9D5D00",
            ["error"] = s.Dark ? "#FF99A4" : "#C42B1C",
            ["information"] = s.Dark ? Mix(s.Accent, "#FFFFFF", 0.2) : Mix(s.Primary, "#000000", 0.1),
        };
        return new ThemeColorProperties(colors);
    }

    private static (double R, double G, double B) Rgb(string hex)
    {
        var value = hex.TrimStart('#');
        if (value.Length == 8) value = value[2..];
        return (int.Parse(value[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture), int.Parse(value[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(value[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    /// <summary>Linear mix: <paramref name="amount"/> of <paramref name="other"/> into <paramref name="color"/>.</summary>
    public static string Mix(string color, string other, double amount)
    {
        var (r1, g1, b1) = Rgb(color);
        var (r2, g2, b2) = Rgb(other);
        int Channel(double a, double b) => (int)Math.Round(a + (b - a) * amount);
        return string.Create(CultureInfo.InvariantCulture, $"#{Channel(r1, r2):X2}{Channel(g1, g2):X2}{Channel(b1, b2):X2}");
    }

    private static double Luminance(string hex)
    {
        var (r, g, b) = Rgb(hex);
        static double Linear(double channel)
        {
            var c = channel / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
    }

    public static bool IsDark(string hex) => Luminance(hex) < 0.22;

    /// <summary>White or near-black, whichever has more contrast on <paramref name="background"/>.</summary>
    public static string ReadableOn(string background)
    {
        var l = Luminance(background);
        var white = 1.05 / (l + 0.05);
        var black = (l + 0.05) / 0.05;
        return white >= black ? "#FFFFFF" : "#141414";
    }
}
