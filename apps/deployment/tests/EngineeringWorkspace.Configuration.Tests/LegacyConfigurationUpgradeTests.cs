using EngineeringWorkspace.Configuration.Upgrade;
using FluentAssertions;

namespace EngineeringWorkspace.Configuration.Tests;

public sealed class LegacyConfigurationUpgradeTests
{
    [Fact]
    public void Old_default_names_and_legacy_paths_are_upgraded_and_custom_values_kept()
    {
        var root = Directory.CreateTempSubdirectory("secureplus-upgrade-");
        try
        {
            var config = Path.Combine(root.FullName, "config");
            Directory.CreateDirectory(config);
            var legacy = Path.Combine(root.FullName, "Global Data Systems", "Secure Plus - Modern Work");
            var current = Path.Combine(root.FullName, "Global Data Systems", "Secure Plus Agent");
            File.WriteAllText(Path.Combine(config, "Branding.ini"), "; comment kept\r\n[Branding]\r\nApplicationName=Secure Plus - Modern Work\r\nApplicationTitle=Secure Plus - Modern Work (Global Data Systems)\r\nCompanyName=Contoso IT\r\n");
            File.WriteAllText(Path.Combine(config, "Application.ini"), "[Application]\r\nName=My custom name\r\n[Paths]\r\nExportDirectory=" + legacy + "\\data\\Exports\r\n");
            File.WriteAllText(Path.Combine(config, "Database.ini"), "[Database]\r\nProvider=SQLite\r\nDatabaseName=" + legacy + "\\data\\secureplus.db\r\n");

            var changed = LegacyConfigurationUpgrade.Apply(config, legacy, current);

            changed.Should().HaveCount(3);
            var branding = File.ReadAllText(Path.Combine(config, "Branding.ini"));
            branding.Should().Contain("; comment kept").And.Contain("ApplicationName=Secure Plus Agent").And.Contain("ApplicationTitle=Secure Plus Agent").And.Contain("CompanyName=Contoso IT");
            var application = File.ReadAllText(Path.Combine(config, "Application.ini"));
            application.Should().Contain("Name=My custom name").And.Contain("ExportDirectory=" + current + "\\data\\Exports");
            File.ReadAllText(Path.Combine(config, "Database.ini")).Should().Contain("DatabaseName=" + current + "\\data\\secureplus.db");
            LegacyConfigurationUpgrade.Apply(config, legacy, current).Should().BeEmpty();
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Legacy_data_folder_is_moved_to_the_new_name()
    {
        var root = Directory.CreateTempSubdirectory("secureplus-root-");
        try
        {
            var legacy = ApplicationDataPaths.LegacyRoot(root.FullName);
            Directory.CreateDirectory(Path.Combine(legacy, "data"));
            File.WriteAllText(Path.Combine(legacy, "data", "marker.txt"), "x");

            var resolved = ApplicationDataPaths.ResolveRoot(root.FullName);

            resolved.Should().EndWith("Secure Plus Agent");
            File.Exists(Path.Combine(resolved, "data", "marker.txt")).Should().BeTrue();
            Directory.Exists(legacy).Should().BeFalse();
            ApplicationDataPaths.ResolveRoot(root.FullName).Should().Be(resolved);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
