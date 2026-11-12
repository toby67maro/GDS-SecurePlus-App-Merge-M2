using FluentAssertions;
using EngineeringWorkspace.Infrastructure.Repositories;

namespace EngineeringWorkspace.Infrastructure.Tests;

public sealed class RepositoryScannerTests
{
    [Fact]
    public async Task Scanner_reports_malformed_json_and_finds_companion_script()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "valid.json"), """{"id":"policy-1","displayName":"Policy One"}""");
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "valid.ps1"), "Write-Output 'not executed'");
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "invalid.json"), "{");
            var result = await new FilePolicyRepositoryScanner().ScanAsync(directory.FullName, null, CancellationToken.None);

            result.Artifacts.Should().ContainSingle();
            result.Artifacts[0].CompanionPowerShellPath.Should().NotBeNull();
            result.Diagnostics.Should().Contain(item => item.Code == "MalformedJson");
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }
}
