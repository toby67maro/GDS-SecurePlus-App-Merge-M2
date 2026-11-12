using FluentAssertions;
using EngineeringWorkspace.Configuration.Appearance;

namespace EngineeringWorkspace.Configuration.Tests;

public sealed class ApplicationPropertiesTests
{
    [Fact]
    public void Missing_properties_file_uses_builtin_gds_defaults()
    {
        var directory = CreateDirectory();
        try
        {
            var result = ApplicationPropertiesLoader.Load(Path.Combine(directory, "config", "ApplicationProperties.json"));

            result.Properties.Appearance.Light.Colors.Primary.Should().Be("#002060");
            result.Properties.Appearance.Dark.Colors.ApplicationBackground.Should().Be("#0A1426");
            result.Diagnostics.Should().ContainSingle();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Invalid_values_are_replaced_without_failing_load()
    {
        var directory = CreateDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "config", "ApplicationProperties.json"), """
            {
              "schemaVersion": 1,
              "appearance": {
                "theme": { "mode": "Dark" },
                "typography": { "baseFontSize": -1 },
                "light": { "colors": { "primary": "not-a-color" } }
              }
            }
            """);

            var result = ApplicationPropertiesLoader.Load(Path.Combine(directory, "config", "ApplicationProperties.json"));

            result.Properties.Appearance.Theme.Mode.Should().Be(ThemeMode.Dark);
            result.Properties.Appearance.Typography.BaseFontSize.Should().Be(13);
            result.Properties.Appearance.Light.Colors.Primary.Should().Be("#002060");
            result.Diagnostics.Should().NotBeEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Malformed_json_falls_back_to_defaults()
    {
        var directory = CreateDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "config", "ApplicationProperties.json"), "{ invalid");

            var result = ApplicationPropertiesLoader.Load(Path.Combine(directory, "config", "ApplicationProperties.json"));

            result.Properties.Appearance.Theme.Mode.Should().Be(ThemeMode.System);
            result.Diagnostics.Should().ContainSingle();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"secureplus-theme-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "config"));
        return directory;
    }
}
