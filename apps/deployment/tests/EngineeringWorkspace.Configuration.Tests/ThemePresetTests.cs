using EngineeringWorkspace.Configuration.Appearance;
using FluentAssertions;

namespace EngineeringWorkspace.Configuration.Tests;

public sealed class ThemePresetTests
{
    [Fact]
    public void Every_theme_is_complete_and_readable()
    {
        ThemePresets.All.Count.Should().BeGreaterThanOrEqualTo(30);
        ThemePresets.All.Select(preset => preset.Name).Should().OnlyHaveUniqueItems();
        foreach (var name in new[] { "Light", "Medium", "Dark", "System", "GDS Light", "GDS Dark", "Aurora Light", "Clay Light", "Blueprint" })
            ThemePresets.Find(name).Should().NotBeNull(name);
        foreach (var preset in ThemePresets.All)
        foreach (var colors in new[] { preset.Light, preset.Dark })
        {
            colors.Tokens.Keys.Should().BeEquivalentTo(AppearanceSchema.Colors.Select(token => token.Path), preset.Name);
            // Text on buttons, the header and the primary color stays legible (WCAG 3:1 for UI text and components).
            foreach (var (foreground, background) in new[] { ("buttonForeground", "buttonBackground"), ("headerForeground", "headerBackground"), ("primaryForeground", "primary"),
                         ("textPrimary", "surfaceBackground"), ("navigationForeground", "navigationBackground") })
                Contrast(colors[foreground], colors[background]).Should().BeGreaterThanOrEqualTo(3, $"{preset.Name}: {foreground} on {background}");
        }

        ThemePresets.Find("System")!.Mode.Should().Be(ThemeMode.System);
        ThemePresets.Mix("#000000", "#FFFFFF", 0.5).Should().Be("#808080");
    }

    private static double Contrast(string a, string b)
    {
        static double Luminance(string hex)
        {
            static double Channel(int value)
            {
                var c = value / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }

            var v = Convert.ToInt32(hex.TrimStart('#')[^6..], 16);
            return 0.2126 * Channel((v >> 16) & 255) + 0.7152 * Channel((v >> 8) & 255) + 0.0722 * Channel(v & 255);
        }

        var (l1, l2) = (Luminance(a), Luminance(b));
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }
}
