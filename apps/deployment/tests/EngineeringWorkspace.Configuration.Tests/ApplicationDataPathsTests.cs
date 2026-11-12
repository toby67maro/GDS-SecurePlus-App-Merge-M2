using FluentAssertions;
using EngineeringWorkspace.Configuration;

namespace EngineeringWorkspace.Configuration.Tests;

public sealed class ApplicationDataPathsTests
{
    [Fact]
    public void Runtime_directories_are_user_writable_and_complete()
    {
        var root = Path.Combine(Path.GetTempPath(), $"secureplus-data-{Guid.NewGuid():N}");
        try
        {
            ApplicationDataPaths.EnsureCreated(root);
            var directories = ApplicationDataPaths.GetDirectories(root);

            directories.Keys.Should().Contain(["Configuration", "Logs", "Audit", "Credentials", "Data"]);
            directories.Values.Should().OnlyContain(path => Directory.Exists(path));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Template_copy_does_not_overwrite_user_configuration()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), $"secureplus-package-{Guid.NewGuid():N}");
        var userRoot = Path.Combine(Path.GetTempPath(), $"secureplus-user-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(packageRoot, "config"));
            Directory.CreateDirectory(Path.Combine(userRoot, "config"));
            File.WriteAllText(Path.Combine(packageRoot, "config", "ApplicationProperties.json"), "package");
            File.WriteAllText(Path.Combine(userRoot, "config", "ApplicationProperties.json"), "custom");

            ApplicationDataPaths.CopyTemplateIfMissing(packageRoot, userRoot, "config/ApplicationProperties.json");

            File.ReadAllText(Path.Combine(userRoot, "config", "ApplicationProperties.json")).Should().Be("custom");
        }
        finally
        {
            if (Directory.Exists(packageRoot)) Directory.Delete(packageRoot, recursive: true);
            if (Directory.Exists(userRoot)) Directory.Delete(userRoot, recursive: true);
        }
    }
}
