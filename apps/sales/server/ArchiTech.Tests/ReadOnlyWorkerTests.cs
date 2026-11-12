using System.Text.Json;
using ArchiTech.Api.Integrations;
using ArchiTech.ReadOnlyWorker;

namespace ArchiTech.Tests;

public sealed class ReadOnlyWorkerTests
{
    [Fact]
    public async Task Preview_outputs_redacted_policy_evidence_without_modifying_input()
    {
        const string input = """
            {"value":[{"displayName":"Example","state":"enabled",
              "grantControls":{"builtInControls":["mfa"],"operator":"OR"},
              "conditions":{"users":{"includeUsers":["All"],"excludeUsers":["secret-user-id"]},
                            "applications":{"includeApplications":["All"]}}}]}
            """;
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, input);
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = await WorkerCommand.RunAsync(["preview-ca-export", path], output, error, CancellationToken.None);
            Assert.Equal(0, exitCode);
            Assert.Equal("", error.ToString());
            Assert.Equal(input, await File.ReadAllTextAsync(path));
            Assert.DoesNotContain("secret-user-id", output.ToString());
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal(1, json.RootElement.GetProperty("totalPolicies").GetInt32());
            Assert.Equal("NotAssessed", json.RootElement.GetProperty("assessmentStatus").GetString());
            Assert.Equal(1, json.RootElement.GetProperty("policies")[0].GetProperty("excludedUsers").GetInt32());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("execute")]
    [InlineData("run-script")]
    [InlineData("live-assessment")]
    public async Task Rejects_unknown_commands(string command)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await WorkerCommand.RunAsync([command, "ignored"], output, error, CancellationToken.None));
        Assert.Equal("", output.ToString());
        Assert.Contains("Usage:", error.ToString());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"value\":[],\"@odata.nextLink\":\"https://graph.microsoft.com/next\"}")]
    public async Task Rejects_invalid_or_incomplete_exports(string input)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, input);
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(1, await WorkerCommand.RunAsync(["preview-ca-export", path], output, error, CancellationToken.None));
            Assert.Equal("", output.ToString());
            Assert.NotEqual("", error.ToString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Rejects_missing_and_oversized_input()
    {
        var path = Path.GetTempFileName();
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(1, await WorkerCommand.RunAsync(["preview-ca-export", path + ".missing"], output, error, CancellationToken.None));
            await File.WriteAllTextAsync(path, new string(' ', 2 * 1024 * 1024 + 1));
            Assert.Equal(1, await WorkerCommand.RunAsync(["preview-ca-export", path], output, error, CancellationToken.None));
            Assert.Equal("", output.ToString());
            Assert.Contains("2 MiB", error.ToString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Live_read_requires_explicit_confirmation_and_valid_identifiers()
    {
        var fake = new FakeLiveReader();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await WorkerCommand.RunAsync(["read-ca-policies", "bad", Guid.NewGuid().ToString()],
            new StringReader("READ"), output, error, CancellationToken.None, fake));
        Assert.Equal(2, await WorkerCommand.RunAsync(["read-ca-policies", fake.Tenant.ToString(), Guid.NewGuid().ToString()],
            new StringReader("no"), output, error, CancellationToken.None, fake));
        Assert.Equal(0, fake.SignIns);
        Assert.Equal(0, fake.Reads);
        Assert.Contains("CANCEL", error.ToString());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Live_read_fails_closed_before_graph_for_tenant_or_scope_mismatch(bool wrongTenant, bool wrongScope)
    {
        var fake = new FakeLiveReader { WrongTenant = wrongTenant, WrongScope = wrongScope };
        using var output = new StringWriter();
        using var error = new StringWriter();
        var result = await WorkerCommand.RunAsync(["read-ca-policies", fake.Tenant.ToString(), Guid.NewGuid().ToString()],
            new StringReader("READ"), output, error, CancellationToken.None, fake);
        Assert.Equal(1, result);
        Assert.Equal(1, fake.SignIns);
        Assert.Equal(0, fake.Reads);
        Assert.Equal("", output.ToString());
        Assert.DoesNotContain("synthetic-secret", error.ToString());
    }

    [Fact]
    public async Task Live_read_outputs_only_aggregate_evidence_and_never_token()
    {
        var fake = new FakeLiveReader();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var result = await WorkerCommand.RunAsync(["read-ca-policies", fake.Tenant.ToString(), Guid.NewGuid().ToString()],
            new StringReader("READ"), output, error, CancellationToken.None, fake);
        Assert.Equal(0, result);
        Assert.Equal(1, fake.Reads);
        Assert.DoesNotContain("synthetic-secret", output.ToString());
        Assert.DoesNotContain("synthetic-secret", error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(fake.Tenant.ToString(), document.RootElement.GetProperty("tenantId").GetString());
        Assert.Equal("NotAssessed", document.RootElement.GetProperty("evidence").GetProperty("assessmentStatus").GetString());
    }

    [Fact]
    public async Task Live_read_fails_explicitly_on_graph_error_without_leaking_details()
    {
        var fake = new FakeLiveReader { FailRead = true };
        using var output = new StringWriter();
        using var error = new StringWriter();
        var result = await WorkerCommand.RunAsync(["read-ca-policies", fake.Tenant.ToString(), Guid.NewGuid().ToString()],
            new StringReader("READ"), output, error, CancellationToken.None, fake);
        Assert.Equal(1, result);
        Assert.Equal("", output.ToString());
        Assert.Contains("HTTP 403", error.ToString());
        Assert.DoesNotContain("synthetic-secret", error.ToString());
    }

    [Fact]
    public async Task Worker_reports_safe_graph_operation_and_status()
    {
        var fake = new FakeLiveReader { FailNamedRead = true };
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await WorkerCommand.RunAsync(
            ["read-ca-policies", fake.Tenant.ToString(), Guid.NewGuid().ToString(), "--include-authentication-methods"],
            new StringReader("READ"), output, error, CancellationToken.None, fake));
        Assert.Contains("ARCHITECH-EVIDENCE-ERROR:authentication-methods:http:403", error.ToString());
        Assert.Equal("", output.ToString());
        Assert.DoesNotContain("synthetic-secret", error.ToString());
    }

    [Fact]
    public async Task Security_defaults_option_is_explicit_and_propagated_after_confirmation()
    {
        var fake = new FakeLiveReader();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var result = await WorkerCommand.RunAsync(
            ["read-ca-policies", fake.Tenant.ToString(), Guid.NewGuid().ToString(), "--include-security-defaults"],
            new StringReader("READ"), output, error, CancellationToken.None, fake);
        Assert.Equal(0, result);
        Assert.True(fake.IncludedSecurityDefaults);
        Assert.Contains("security defaults", error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        Assert.False(document.RootElement.GetProperty("evidence").GetProperty("securityDefaultsEnabled").GetBoolean());
    }

    [Fact]
    public async Task Named_location_option_is_explicit_and_rejects_duplicate_flags()
    {
        var fake = new FakeLiveReader();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var client = Guid.NewGuid().ToString();
        var result = await WorkerCommand.RunAsync(
            ["read-ca-policies", fake.Tenant.ToString(), client, "--include-named-locations", "--include-security-defaults"],
            new StringReader("READ"), output, error, CancellationToken.None, fake);
        Assert.Equal(0, result);
        Assert.True(fake.IncludedNamedLocations);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(0, document.RootElement.GetProperty("evidence").GetProperty("namedLocationCount").GetInt32());
        Assert.Equal(2, await WorkerCommand.RunAsync(
            ["read-ca-policies", fake.Tenant.ToString(), client, "--include-named-locations", "--include-named-locations"],
            new StringReader("READ"), TextWriter.Null, error, CancellationToken.None, fake));
        Assert.Equal(1, fake.Reads);
    }

    [Fact]
    public async Task Authentication_methods_option_is_explicit_and_propagated()
    {
        var fake = new FakeLiveReader();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await WorkerCommand.RunAsync(
            ["read-ca-policies", fake.Tenant.ToString(), Guid.NewGuid().ToString(), "--include-authentication-methods"],
            new StringReader("READ"), output, error, CancellationToken.None, fake));
        Assert.True(fake.IncludedAuthenticationMethods);
        Assert.Contains("authentication methods", error.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("migrationComplete", json.RootElement.GetProperty("evidence").GetProperty("authenticationMethodsMigrationState").GetString());
    }

    private sealed class FakeLiveReader : ILiveConditionalAccessReader
    {
        public Guid Tenant { get; } = Guid.NewGuid();
        public bool WrongTenant { get; init; }
        public bool WrongScope { get; init; }
        public bool FailRead { get; init; }
        public bool FailNamedRead { get; init; }
        public int SignIns { get; private set; }
        public int Reads { get; private set; }
        public bool IncludedSecurityDefaults { get; private set; }
        public bool IncludedNamedLocations { get; private set; }
        public bool IncludedAuthenticationMethods { get; private set; }

        public Task<DelegatedGraphToken> SignInAsync(Guid tenantId, Guid clientId, CancellationToken cancellationToken)
        {
            SignIns++;
            return Task.FromResult(new DelegatedGraphToken(
                (WrongTenant ? Guid.NewGuid() : Tenant).ToString(),
                WrongScope ? ["User.Read"] : ["Policy.Read.All"], "synthetic-secret"));
        }

        public Task<ConditionalAccessEvidence> ReadAsync(string accessToken, bool includeSecurityDefaults, bool includeNamedLocations, bool includeAuthenticationMethods, CancellationToken cancellationToken)
        {
            Reads++;
            IncludedSecurityDefaults = includeSecurityDefaults;
            IncludedNamedLocations = includeNamedLocations;
            IncludedAuthenticationMethods = includeAuthenticationMethods;
            Assert.Equal("synthetic-secret", accessToken);
            if (FailRead) throw new HttpRequestException("synthetic-secret", null, System.Net.HttpStatusCode.Forbidden);
            if (FailNamedRead) throw new GraphEvidenceHttpException("authentication-methods", System.Net.HttpStatusCode.Forbidden);
            return Task.FromResult(new ConditionalAccessEvidence(1, 1, 1, 0, "NotAssessed", "Manual review required.",
                SecurityDefaultsEnabled: includeSecurityDefaults ? false : null,
                NamedLocationCount: includeNamedLocations ? 0 : null,
                AuthenticationMethodsMigrationState: includeAuthenticationMethods ? "migrationComplete" : null));
        }
    }
}
