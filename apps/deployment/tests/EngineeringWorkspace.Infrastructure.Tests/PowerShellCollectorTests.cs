using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Credentials;
using EngineeringWorkspace.Domain.Security;
using EngineeringWorkspace.Domain.Tenants;
using EngineeringWorkspace.Infrastructure.Assessments;
using EngineeringWorkspace.Infrastructure.Microsoft;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EngineeringWorkspace.Infrastructure.Tests;

/// <summary>Certificate credentials and the read-only PowerShell collector (4.0 beta.2).</summary>
public sealed class PowerShellCollectorTests
{
    private static readonly SecretReference Reference = new(SecretReference.OperatingSystemProvider, "customer/test/primary/graph-client-secret");

    private sealed class MemorySecrets(string? value) : ISecretStore
    {
        public string DisplayName => "Memory";
        public bool SupportsProvider(string provider) => true;
        public Task<string?> GetSecretAsync(SecretReference reference, CancellationToken cancellationToken = default) => Task.FromResult(value);
        public Task SetSecretAsync(SecretReference reference, string secret, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> DeleteSecretAsync(SecretReference reference, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> SecretExistsAsync(SecretReference reference, CancellationToken cancellationToken = default) => Task.FromResult(value is not null);
    }

    private static TenantConfiguration Tenant(TenantAuthenticationType type = TenantAuthenticationType.Certificate) =>
        new("primary", "Contoso", "Contoso", Guid.Parse("00000000-0000-0000-0000-0000000000c1"), "contoso.com", CloudEnvironment.Commercial,
            new ApplicationRegistration(Guid.Parse("00000000-0000-0000-0000-0000000000a1"), null, type, Reference, null, null, [TenantCapability.ExchangeAssessment]));

    [Fact]
    public void Certificates_are_created_with_a_private_key_and_stored_only_as_an_opaque_value()
    {
        var created = CertificateCredential.Create("GDS Secure Plus Read - Contoso", TimeSpan.FromDays(365), DateTimeOffset.UtcNow);

        CertificateCredential.IsCertificate(created.StoredValue).Should().BeTrue();
        CertificateCredential.IsCertificate("a-client-secret-value").Should().BeFalse();
        created.NotAfterUtc.Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(365), TimeSpan.FromDays(1));
        using var certificate = CertificateCredential.Load(created.StoredValue);
        certificate.HasPrivateKey.Should().BeTrue();
        certificate.Thumbprint.Should().Be(created.Thumbprint);
        certificate.GetRSAPublicKey()!.KeySize.Should().Be(2048);
        using var publicOnly = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(created.PublicCertificate);
        publicOnly.HasPrivateKey.Should().BeFalse("only the public certificate is uploaded to Microsoft Entra");

        var signature = certificate.GetRSAPrivateKey()!.SignData("token"u8.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        publicOnly.GetRSAPublicKey()!.VerifyData("token"u8.ToArray(), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).Should().BeTrue();
    }

    [Fact]
    public void Collector_output_is_parsed_per_dataset_and_other_lines_are_ignored()
    {
        var lines = new[]
        {
            "Error Acquiring Token:",
            """{"dataset":"exo:Get-OrganizationConfig","status":"Collected","items":[{"OAuth2ClientProfileEnabled":true,"AuditDisabled":false}],"message":null}""",
            """{"dataset":"teams:Get-CsTeamsMeetingPolicy","status":"Forbidden","items":[],"message":"Teams sign-in failed: AADSTS7000215."}""",
            """{"dataset":"exo:Set-OrganizationConfig","status":"Collected","items":[]}""",
            "{not json"
        };

        var parsed = PowerShellTenantDataCollector.Parse(lines);

        parsed.Keys.Should().BeEquivalentTo(["exo:Get-OrganizationConfig", "teams:Get-CsTeamsMeetingPolicy"], "unknown datasets are dropped");
        parsed["exo:Get-OrganizationConfig"].IsCollected.Should().BeTrue();
        parsed["exo:Get-OrganizationConfig"].Items.Single().GetProperty("OAuth2ClientProfileEnabled").GetBoolean().Should().BeTrue();
        parsed["teams:Get-CsTeamsMeetingPolicy"].Status.Should().Be(DatasetStatus.Forbidden);
        parsed["teams:Get-CsTeamsMeetingPolicy"].Message.Should().Contain("AADSTS7000215");
    }

    [Fact]
    public void Only_read_cmdlets_are_in_the_collector_lists()
    {
        PowerShellTenantDataCollector.ExchangeCmdlets.Concat(PowerShellTenantDataCollector.ComplianceCmdlets).Concat(PowerShellTenantDataCollector.TeamsCmdlets)
            .Should().OnlyContain(cmdlet => cmdlet.StartsWith("Get-", StringComparison.Ordinal));
        using var script = typeof(PowerShellTenantDataCollector).Assembly.GetManifestResourceStream("EngineeringWorkspace.Assessments.ReadOnlyCollector.ps1");
        script.Should().NotBeNull("the script is embedded in the build");
        var text = new StreamReader(script!).ReadToEnd();
        text.Should().Contain("-CommandName $exchange").And.Contain("CommandName = $compliance").And.Contain("'^Get-[A-Za-z0-9]+$'");
        text.Should().NotContainAny("Set-", "New-", "Remove-", "Export-PfxCertificate", "Out-File");
    }

    [Fact]
    public void The_installer_script_pins_the_same_module_versions_as_the_collector()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "build", "Version.props"))) directory = directory.Parent;
        directory.Should().NotBeNull("the tests run inside the repository");
        var script = File.ReadAllText(Path.Combine(directory!.FullName, "packaging", "prerequisites", "Install-SecurePlusPowerShellModules.ps1"));
        script.Should().Contain($"'{PowerShellTenantDataCollector.ExchangeModule}' = '{PowerShellTenantDataCollector.ExchangeModuleVersion}'");
        script.Should().Contain($"'{PowerShellTenantDataCollector.TeamsModule}'           = '{PowerShellTenantDataCollector.TeamsModuleVersion}'");
    }

    [Fact]
    public async Task A_client_secret_registration_reports_that_a_certificate_is_needed()
    {
        var collector = new PowerShellTenantDataCollector(new MemorySecrets("a-client-secret-value"), NullLogger<PowerShellTenantDataCollector>.Instance);

        var datasets = await collector.CollectAsync(Tenant(TenantAuthenticationType.ClientSecret), "contoso.onmicrosoft.com", null, CancellationToken.None);

        datasets.Should().HaveCount(PowerShellTenantDataCollector.DatasetNames.Count);
        datasets.Should().OnlyContain(dataset => dataset.Status == DatasetStatus.NotAvailable && dataset.Message!.Contains("certificate"));
    }

    [Fact]
    public void The_initial_domain_comes_from_the_organization_dataset()
    {
        var organization = JsonSerializer.SerializeToElement(new
        {
            id = "x",
            verifiedDomains = new[] { new { name = "contoso.com", isInitial = false }, new { name = "contoso.onmicrosoft.com", isInitial = true } }
        });
        var snapshot = new TenantSnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow, new Dictionary<string, TenantDataset>
        {
            [TenantDatasets.Organization] = new(TenantDatasets.Organization, "test", DatasetStatus.Collected, [organization], null)
        });
        PowerShellTenantDataCollector.InitialDomain(snapshot).Should().Be("contoso.onmicrosoft.com");
    }

    [Fact]
    public async Task The_real_script_returns_every_dataset_when_powershell_7_is_installed()
    {
        // Runs where pwsh is available (the Windows CI runner and engineers' workstations). With a made-up tenant every dataset comes
        // back unavailable or forbidden, which proves the script, the stdin hand-over and the parser work together.
        var created = CertificateCredential.Create("Secure Plus collector test", TimeSpan.FromDays(1), DateTimeOffset.UtcNow);
        var collector = new PowerShellTenantDataCollector(new MemorySecrets(created.StoredValue), NullLogger<PowerShellTenantDataCollector>.Instance);

        var datasets = await collector.CollectAsync(Tenant(), "contoso-secureplus-test.onmicrosoft.com", null, CancellationToken.None);

        datasets.Select(dataset => dataset.Name).Should().BeEquivalentTo(PowerShellTenantDataCollector.DatasetNames);
        datasets.Should().OnlyContain(dataset => !dataset.IsCollected);
        datasets.Should().OnlyContain(dataset => !string.IsNullOrWhiteSpace(dataset.Message));
        var fragment = CertificateCredential.Pkcs12Base64(created.StoredValue).Substring(0, 40);
        datasets.Should().OnlyContain(dataset => !dataset.Message!.Contains(fragment), "the certificate never appears in output");
    }
}
