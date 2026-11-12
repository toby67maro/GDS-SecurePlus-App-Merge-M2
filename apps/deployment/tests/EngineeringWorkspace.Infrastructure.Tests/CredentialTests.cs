using EngineeringWorkspace.Domain.Credentials;
using EngineeringWorkspace.Domain.Security;
using FluentAssertions;

namespace EngineeringWorkspace.Infrastructure.Tests;

public sealed class CredentialTests
{
    [Fact]
    public void Credential_health_tracks_expiration_without_exposing_secret()
    {
        var metadata = new CredentialMetadata("id", "Display", CredentialType.ApiKey, "Test",
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(10),
            SecretReference.Parse("os:test"));

        metadata.EvaluateExpiration(DateTimeOffset.UtcNow, TimeSpan.FromDays(30)).Should().Be(CredentialStatus.ExpiresSoon);
        metadata.SecretReference.ToString().Should().NotContain("secret-value");
    }
}
