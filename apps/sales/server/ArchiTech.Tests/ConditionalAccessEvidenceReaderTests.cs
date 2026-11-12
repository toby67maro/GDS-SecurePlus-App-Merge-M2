using System.Net;
using System.Text;
using ArchiTech.Api.Integrations;

namespace ArchiTech.Tests;

public sealed class ConditionalAccessEvidenceReaderTests
{
    [Fact]
    public async Task Reads_all_pages_but_does_not_infer_compliance()
    {
        var handler = new StubHandler((request, number) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("graph.microsoft.com", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("synthetic-token", request.Headers.Authorization?.Parameter);
            return Json(number == 1
                ? """
                  {"value":[{"displayName":"Synthetic MFA","state":"enabled","grantControls":{"builtInControls":["mfa"]},
                  "conditions":{"users":{"includeUsers":["All"],"excludeUsers":["private-user-id"]},
                  "applications":{"includeApplications":["All"],"excludeApplications":["private-app-id"]}}}],
                   "@odata.nextLink":"https://graph.microsoft.com/v1.0/identity/conditionalAccess/policies?$skiptoken=next"}
                  """
                : """
                  {"value":[
                    {"state":"enabled","conditions":{"clientAppTypes":["other"]},"grantControls":{"builtInControls":["block"]}},
                    {"state":"disabled","grantControls":{"builtInControls":["mfa"]}}]}
                  """);
        });
        using var reader = new ConditionalAccessEvidenceReader(handler);
        var evidence = await reader.ReadAsync("synthetic-token", CancellationToken.None);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(3, evidence.TotalPolicies);
        Assert.Equal(2, evidence.EnabledPolicies);
        Assert.Equal(1, evidence.EnabledMfaPolicies);
        Assert.Equal(1, evidence.EnabledLegacyBlockPolicies);
        Assert.Equal("NotAssessed", evidence.AssessmentStatus);
        Assert.Contains("do not prove coverage", evidence.Limitations);
        Assert.NotNull(evidence.Policies);
        Assert.Equal(3, evidence.Policies.Count);
        Assert.Equal("Synthetic MFA", evidence.Policies[0].Name);
        Assert.True(evidence.Policies[0].IncludesAllUsers);
        Assert.Equal(1, evidence.Policies[0].ExcludedUsers);
        Assert.Equal(1, evidence.Policies[0].ExcludedApplications);
        Assert.Null(evidence.Policies[1].IncludesAllUsers);
        var serialized = System.Text.Json.JsonSerializer.Serialize(evidence);
        Assert.DoesNotContain("private-user-id", serialized);
        Assert.DoesNotContain("private-app-id", serialized);
    }

    [Theory]
    [InlineData("https://evil.example/v1.0/identity/conditionalAccess/policies")]
    [InlineData("https://graph.microsoft.com/v1.0/users")]
    [InlineData("http://graph.microsoft.com/v1.0/identity/conditionalAccess/policies")]
    [InlineData("https://graph.microsoft.com:444/v1.0/identity/conditionalAccess/policies")]
    public async Task Rejects_untrusted_pagination_before_sending_a_token(string nextLink)
    {
        var handler = new StubHandler((_, _) =>
            Json($"{{\"value\":[],\"@odata.nextLink\":\"{nextLink}\"}}"));
        using var reader = new ConditionalAccessEvidenceReader(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync("synthetic-token", CancellationToken.None));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Rejects_repeated_pages_and_incomplete_responses()
    {
        var handler = new StubHandler((_, _) =>
            Json("""{"value":[],"@odata.nextLink":"https://graph.microsoft.com/v1.0/identity/conditionalAccess/policies?$skiptoken=loop"}"""));
        using var reader = new ConditionalAccessEvidenceReader(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync("synthetic-token", CancellationToken.None));
        Assert.Equal(2, handler.RequestCount);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"value\":[{}]}")]
    [InlineData("{\"value\":[{\"state\":\"enabled\",\"grantControls\":{\"builtInControls\":[1]}}]}")]
    public async Task Rejects_malformed_evidence(string content)
    {
        using var reader = new ConditionalAccessEvidenceReader(new StubHandler((_, _) => Json(content)));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync("synthetic-token", CancellationToken.None));
    }

    [Fact]
    public async Task Denied_requests_fail_explicitly_without_returning_compliant_evidence()
    {
        using var reader = new ConditionalAccessEvidenceReader(
            new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var exception = await Assert.ThrowsAsync<GraphEvidenceHttpException>(
            () => reader.ReadAsync("synthetic-token", CancellationToken.None));
        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
    }

    [Fact]
    public async Task Counts_report_only_disabled_and_unknown_states_across_pages()
    {
        using var reader = new ConditionalAccessEvidenceReader(new StubHandler((_, number) => Json(number == 1
            ? """{"value":[{"state":"enabledForReportingButNotEnforced"},{"state":"disabled"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/identity/conditionalAccess/policies?$skiptoken=next"}"""
            : """{"value":[{"state":"futureValue"},{"state":"enabled"}]}""")));
        var result = await reader.ReadAsync("synthetic-token", CancellationToken.None);
        Assert.Equal(4, result.TotalPolicies);
        Assert.Equal(1, result.EnabledPolicies);
        Assert.Equal(1, result.ReportOnlyPolicies);
        Assert.Equal(1, result.DisabledPolicies);
        Assert.Equal(1, result.UnknownStatePolicies);
        Assert.Equal("NotAssessed", result.AssessmentStatus);
    }

    [Fact]
    public async Task Redirects_are_not_followed()
    {
        var handler = new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://evil.example/steal");
            return response;
        });
        using var reader = new ConditionalAccessEvidenceReader(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync("synthetic-token", CancellationToken.None));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Oversized_response_is_rejected()
    {
        using var reader = new ConditionalAccessEvidenceReader(new StubHandler((_, _) =>
            Json(new string(' ', 2 * 1024 * 1024 + 1))));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync("synthetic-token", CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_policy_inventory_larger_than_limit()
    {
        var entries = string.Join(",", Enumerable.Repeat("{\"state\":\"disabled\"}", 1001));
        using var reader = new ConditionalAccessEvidenceReader(new StubHandler((_, _) => Json($"{{\"value\":[{entries}]}}")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync("synthetic-token", CancellationToken.None));
        Assert.Contains("policy limit", error.Message);
    }

    [Fact]
    public async Task Rejects_invalid_details_even_on_disabled_policies()
    {
        using var reader = new ConditionalAccessEvidenceReader(new StubHandler((_, _) =>
            Json("""{"value":[{"state":"disabled","conditions":{"users":{"excludeUsers":[42]}}}]}""")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync("synthetic-token", CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reads_security_defaults_only_when_requested_and_returns_boolean_without_identifiers(bool enabled)
    {
        var handler = new StubHandler((request, number) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("synthetic-token", request.Headers.Authorization?.Parameter);
            Assert.Equal(number == 1 ? "/v1.0/identity/conditionalAccess/policies" :
                "/v1.0/policies/identitySecurityDefaultsEnforcementPolicy", request.RequestUri!.AbsolutePath);
            return number == 1 ? Json("""{"value":[]}""") :
                Json($"{{\"id\":\"private-id\",\"isEnabled\":{enabled.ToString().ToLowerInvariant()}}}");
        });
        using var reader = new ConditionalAccessEvidenceReader(handler);
        var evidence = await reader.ReadAsync("synthetic-token", CancellationToken.None, includeSecurityDefaults: true);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(enabled, evidence.SecurityDefaultsEnabled);
        Assert.Equal("NotAssessed", evidence.AssessmentStatus);
        Assert.DoesNotContain("private-id", System.Text.Json.JsonSerializer.Serialize(evidence));
    }

    [Fact]
    public async Task Security_defaults_are_not_read_without_explicit_selection()
    {
        var handler = new StubHandler((_, _) => Json("""{"value":[]}"""));
        using var reader = new ConditionalAccessEvidenceReader(handler);
        var evidence = await reader.ReadAsync("synthetic-token", CancellationToken.None);
        Assert.Equal(1, handler.RequestCount);
        Assert.Null(evidence.SecurityDefaultsEnabled);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"isEnabled\":null}")]
    [InlineData("{\"isEnabled\":\"false\"}")]
    public async Task Rejects_malformed_security_defaults_without_returning_partial_inventory(string content)
    {
        using var reader = new ConditionalAccessEvidenceReader(new StubHandler((_, number) =>
            Json(number == 1 ? """{"value":[]}""" : content)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reader.ReadAsync("synthetic-token", CancellationToken.None, includeSecurityDefaults: true));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task Rejects_denied_or_redirected_security_defaults_without_returning_partial_inventory(HttpStatusCode status)
    {
        using var reader = new ConditionalAccessEvidenceReader(new StubHandler((_, number) =>
            number == 1 ? Json("""{"value":[]}""") : new HttpResponseMessage(status)));
        if (status == HttpStatusCode.Forbidden)
            await Assert.ThrowsAsync<GraphEvidenceHttpException>(() =>
                reader.ReadAsync("synthetic-token", CancellationToken.None, includeSecurityDefaults: true));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                reader.ReadAsync("synthetic-token", CancellationToken.None, includeSecurityDefaults: true));
    }

    [Fact]
    public async Task Rejects_oversized_security_defaults_response()
    {
        using var reader = new ConditionalAccessEvidenceReader(new StubHandler((_, number) =>
            Json(number == 1 ? """{"value":[]}""" : new string(' ', 2 * 1024 * 1024 + 1))));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reader.ReadAsync("synthetic-token", CancellationToken.None, includeSecurityDefaults: true));
    }

    [Fact]
    public async Task Counts_named_locations_across_pages_without_returning_names_or_addresses()
    {
        var handler = new StubHandler((request, number) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("synthetic-token", request.Headers.Authorization?.Parameter);
            return number switch
            {
                1 => Json("""{"value":[]}"""),
                2 => Json("""{"value":[{"id":"private-id","displayName":"sensitive-name","ipRanges":[{"cidrAddress":"192.0.2.0/24"}]}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/identity/conditionalAccess/namedLocations?$skiptoken=next"}"""),
                _ => Json("""{"value":[{"id":"another-id"}]}""")
            };
        });
        using var reader = new ConditionalAccessEvidenceReader(handler);
        var evidence = await reader.ReadAsync("synthetic-token", CancellationToken.None, includeNamedLocations: true);
        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(2, evidence.NamedLocationCount);
        var json = System.Text.Json.JsonSerializer.Serialize(evidence);
        Assert.DoesNotContain("private-id", json);
        Assert.DoesNotContain("sensitive-name", json);
        Assert.DoesNotContain("192.0.2.0", json);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"value\":[{}]}")]
    [InlineData("{\"value\":[],\"@odata.nextLink\":\"https://evil.example/steal\"}")]
    [InlineData("{\"value\":[],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/users\"}")]
    public async Task Rejects_incomplete_named_locations_without_returning_partial_evidence(string content)
    {
        var handler = new StubHandler((_, number) => Json(number == 1 ? """{"value":[]}""" : content));
        using var reader = new ConditionalAccessEvidenceReader(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reader.ReadAsync("synthetic-token", CancellationToken.None, includeNamedLocations: true));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Named_location_read_is_optional_and_denial_fails_closed()
    {
        var handler = new StubHandler((_, number) =>
            number == 1 ? Json("""{"value":[]}""") : new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var reader = new ConditionalAccessEvidenceReader(handler);
        await Assert.ThrowsAsync<GraphEvidenceHttpException>(() =>
            reader.ReadAsync("synthetic-token", CancellationToken.None, includeNamedLocations: true));
        Assert.Equal(2, handler.RequestCount);
        using var unselected = new ConditionalAccessEvidenceReader(new StubHandler((_, _) => Json("""{"value":[]}""")));
        Assert.Null((await unselected.ReadAsync("synthetic-token", CancellationToken.None)).NamedLocationCount);
    }

    [Fact]
    public async Task Duplicate_named_locations_across_pages_are_rejected()
    {
        using var reader = new ConditionalAccessEvidenceReader(new StubHandler((_, number) => Json(number switch
        {
            1 => """{"value":[]}""",
            2 => """{"value":[{"id":"duplicate"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/identity/conditionalAccess/namedLocations?$skiptoken=next"}""",
            _ => """{"value":[{"id":"duplicate"}]}"""
        })));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reader.ReadAsync("synthetic-token", CancellationToken.None, includeNamedLocations: true));
    }

    [Theory]
    [InlineData("preMigration", "preMigration")]
    [InlineData("migrationInProgress", "migrationInProgress")]
    [InlineData("migrationComplete", "migrationComplete")]
    [InlineData("futureSensitiveValue", "unknown")]
    public async Task Authentication_methods_returns_only_migration_state(string graphState, string expected)
    {
        var handler = new StubHandler((request, number) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(number == 1 ? "/v1.0/identity/conditionalAccess/policies" : "/v1.0/policies/authenticationMethodsPolicy", request.RequestUri!.AbsolutePath);
            return number == 1 ? Json("""{"value":[]}""") :
                Json($"{{\"policyMigrationState\":\"{graphState}\",\"registrationEnforcement\":{{\"private\":\"secret\"}}}}");
        });
        using var reader = new ConditionalAccessEvidenceReader(handler);
        var evidence = await reader.ReadAsync("synthetic-token", CancellationToken.None, includeAuthenticationMethods: true);
        Assert.Equal(expected, evidence.AuthenticationMethodsMigrationState);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(evidence));
        Assert.Equal("NotAssessed", evidence.AssessmentStatus);
    }

    [Theory]
    [InlineData("{\"policyMigrationState\":42}")]
    [InlineData("{\"policyMigrationState\":\"\"}")]
    public async Task Malformed_authentication_methods_response_fails_without_partial_evidence(string content)
    {
        using var reader = new ConditionalAccessEvidenceReader(new StubHandler((_, number) => Json(number == 1 ? """{"value":[]}""" : content)));
        await Assert.ThrowsAsync<GraphEvidenceFormatException>(() =>
            reader.ReadAsync("synthetic-token", CancellationToken.None, includeAuthenticationMethods: true));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"policyMigrationState\":null}")]
    public async Task Missing_authentication_methods_migration_state_is_reported_without_inferring_a_value(string content)
    {
        using var reader = new ConditionalAccessEvidenceReader(new StubHandler((_, number) => Json(number == 1 ? """{"value":[]}""" : content)));
        await Assert.ThrowsAsync<GraphEvidenceMissingMigrationStateException>(() =>
            reader.ReadAsync("synthetic-token", CancellationToken.None, includeAuthenticationMethods: true));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task Denied_or_redirected_authentication_methods_read_fails_closed(HttpStatusCode status)
    {
        using var reader = new ConditionalAccessEvidenceReader(new StubHandler((_, number) =>
            number == 1 ? Json("""{"value":[]}""") : new HttpResponseMessage(status)));
        if (status == HttpStatusCode.Forbidden)
            await Assert.ThrowsAsync<GraphEvidenceHttpException>(() => reader.ReadAsync("synthetic-token", CancellationToken.None, includeAuthenticationMethods: true));
        else
            await Assert.ThrowsAsync<GraphEvidenceFormatException>(() => reader.ReadAsync("synthetic-token", CancellationToken.None, includeAuthenticationMethods: true));
    }

    [Fact]
    public async Task Oversized_authentication_methods_response_fails_closed()
    {
        using var reader = new ConditionalAccessEvidenceReader(new StubHandler((_, number) =>
            Json(number == 1 ? """{"value":[]}""" : new string(' ', 2 * 1024 * 1024 + 1))));
        await Assert.ThrowsAsync<GraphEvidenceFormatException>(() => reader.ReadAsync("synthetic-token", CancellationToken.None, includeAuthenticationMethods: true));
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(respond(request, RequestCount));
        }
    }
}
