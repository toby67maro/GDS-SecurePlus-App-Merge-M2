using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using ArchiTech.Api.Domain;
using ArchiTech.Api.Integrations;
using Microsoft.Extensions.Options;

namespace ArchiTech.Tests;

public sealed class PlannerAuthenticationTests
{
    private static PlannerFlowOptions ValidOptions() => new()
    {
        AuthenticationMode = "EntraCertificate",
        Endpoint = "https://example.01.environment.api.powerplatform.com/trigger",
        AllowedHost = "example.01.environment.api.powerplatform.com",
        TenantId = Guid.NewGuid().ToString(),
        ClientId = Guid.NewGuid().ToString(),
        CertificatePath = "outside-repository.pfx"
    };

    [Fact]
    public void Configuration_requires_explicit_mode_and_exact_environment_host()
    {
        var options = ValidOptions();
        Assert.True(options.IsConfigured);
        options.AllowedHost = "other.01.environment.api.powerplatform.com";
        Assert.False(options.IsConfigured);
        options.Endpoint = "https://other.invalid/trigger";
        options.AllowedHost = "other.invalid";
        Assert.False(options.IsConfigured);
        options = ValidOptions();
        options.Endpoint += "?SIG=secret";
        Assert.False(options.IsConfigured);
        Assert.False(new PlannerFlowOptions { Endpoint = "https://flow.example.test" }.IsConfigured);
        Assert.True(new PlannerFlowOptions().Validate(out _));
    }

    [Fact]
    public void Certificate_configuration_requires_exactly_one_valid_source()
    {
        var options = ValidOptions();
        options.CertificateThumbprint = new string('A', 40);
        Assert.False(options.IsConfigured);
        options.CertificatePath = null;
        Assert.True(options.IsConfigured);
        options.CertificateThumbprint = "not-a-thumbprint";
        Assert.False(options.IsConfigured);
        options.CertificateThumbprint = null;
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public async Task Entra_token_is_added_before_the_request()
    {
        var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var service = new PlannerHandoffService(client, Options.Create(ValidOptions()), new FakeTokenProvider());
        var result = await service.SendAsync(Engagement(), CancellationToken.None);
        Assert.Equal("Sent", result.Status);
        Assert.Equal("Bearer", handler.Scheme);
        Assert.Equal("test-token", handler.Token);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Token_failure_does_not_send_request_or_expose_error()
    {
        var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        var service = new PlannerHandoffService(client, Options.Create(ValidOptions()), new FakeTokenProvider(true));
        var result = await service.SendAsync(Engagement(), CancellationToken.None);
        Assert.Equal("Failed", result.Status);
        Assert.Contains("no flow request was sent", result.Message);
        Assert.DoesNotContain("sensitive exception", result.Message);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task Missing_store_certificate_fails_without_invoking_flow()
    {
        var options = ValidOptions();
        options.CertificatePath = null;
        options.CertificateThumbprint = new string('0', 40);
        using var provider = new PlannerFlowTokenProvider(Options.Create(options));
        await Assert.ThrowsAsync<CryptographicException>(() => provider.GetTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Accepted_response_is_not_mistaken_for_completed_handoff()
    {
        var handler = new CaptureHandler { Status = HttpStatusCode.Accepted };
        using var client = new HttpClient(handler);
        var service = new PlannerHandoffService(client, Options.Create(ValidOptions()), new FakeTokenProvider());
        var result = await service.SendAsync(Engagement(), CancellationToken.None);
        Assert.Equal("Failed", result.Status);
        Assert.Null(result.PlanId);
        Assert.Contains("without confirming completion", result.Message);
    }

    private static Engagement Engagement() => new()
    {
        Id = Guid.NewGuid(),
        Customer = new Customer { Name = "Synthetic", NormalizedName = "SYNTHETIC" },
        Salesperson = "Tester",
        PlannerHandoffStatus = "Pending"
    };

    private sealed class FakeTokenProvider(bool fail = false) : IPlannerFlowTokenProvider
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken) =>
            fail ? throw new IOException("sensitive exception") : Task.FromResult("test-token");
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public int Requests { get; private set; }
        public string? Scheme { get; private set; }
        public string? Token { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Scheme = request.Headers.Authorization?.Scheme;
            Token = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = JsonContent.Create(new { plannerPlanId = "test-plan" })
            });
        }
    }
}
