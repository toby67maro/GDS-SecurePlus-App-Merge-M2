using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ArchiTech.Tests")]

namespace ArchiTech.Api.Integrations;

public sealed record ConditionalAccessEvidence(
    int TotalPolicies,
    int EnabledPolicies,
    int EnabledMfaPolicies,
    int EnabledLegacyBlockPolicies,
    string AssessmentStatus,
    string Limitations,
    int DisabledPolicies = 0,
    int ReportOnlyPolicies = 0,
    int UnknownStatePolicies = 0,
    IReadOnlyList<OfflinePolicyReview>? Policies = null,
    bool? SecurityDefaultsEnabled = null,
    int? NamedLocationCount = null,
    string? AuthenticationMethodsMigrationState = null);

public sealed record OfflinePolicyReview(
    string? Name,
    string State,
    string[]? GrantControls,
    string? GrantOperator,
    string[]? ClientAppTypes,
    bool? IncludesAllUsers,
    int? ExcludedUsers,
    int? ExcludedGroups,
    int? ExcludedRoles,
    bool? IncludesAllApplications,
    int? ExcludedApplications);

public sealed record OfflineConditionalAccessPreview(
    int TotalPolicies,
    int EnabledPolicies,
    int EnabledMfaPolicies,
    int EnabledLegacyBlockPolicies,
    string AssessmentStatus,
    string Limitations,
    IReadOnlyList<OfflinePolicyReview> Policies);

public sealed class GraphEvidenceHttpException(string operation, HttpStatusCode statusCode)
    : HttpRequestException("Graph evidence request failed.", null, statusCode)
{
    public string Operation { get; } = operation;
}

public sealed class GraphEvidenceFormatException(string operation, Exception innerException)
    : InvalidOperationException("Graph evidence response was incomplete or invalid.", innerException)
{
    public string Operation { get; } = operation;
}

public sealed class GraphEvidenceMissingMigrationStateException()
    : InvalidOperationException("The authentication methods migration state was absent or null.");

/// <summary>Reads selected evidence without acquiring tokens or changing assessment results.</summary>
public sealed class ConditionalAccessEvidenceReader : IDisposable
{
    private const string PolicyPath = "/v1.0/identity/conditionalAccess/policies";
    private const int MaximumPages = 100;
    private const int MaximumPageBytes = 2 * 1024 * 1024;
    private const int MaximumPolicies = 1000;
    private static readonly Uri InitialUri = new($"https://graph.microsoft.com{PolicyPath}");
    private readonly HttpClient _client;

    public ConditionalAccessEvidenceReader()
        : this(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
    }

    internal ConditionalAccessEvidenceReader(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Dispose() => _client.Dispose();

    public async Task<ConditionalAccessEvidence> ReadAsync(string accessToken, CancellationToken cancellationToken,
        bool includeSecurityDefaults = false, bool includeNamedLocations = false, bool includeAuthenticationMethods = false)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("A verified delegated Graph access token is required.", nameof(accessToken));

        var uri = InitialUri;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        var enabled = 0;
        var mfa = 0;
        var legacyBlocks = 0;
        var disabled = 0;
        var reportOnly = 0;
        var unknown = 0;
        var reviews = new List<OfflinePolicyReview>();
        for (var page = 0; page < MaximumPages; page++)
        {
            if (!visited.Add(uri.AbsoluteUri))
                throw new InvalidOperationException("Graph pagination repeated a page; evidence is incomplete.");

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await _client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
                throw new InvalidOperationException("Graph returned a redirect; evidence is incomplete.");
            if (!response.IsSuccessStatusCode)
                throw new GraphEvidenceHttpException("policies", response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + count > MaximumPageBytes)
                    throw new InvalidOperationException("Graph policy response exceeded the size limit; evidence is incomplete.");
                buffer.Write(chunk, 0, count);
            }
            buffer.Position = 0;
            using var json = await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
            var root = json.RootElement;
            var counts = CountPolicies(root);
            if (reviews.Count + counts.TotalPolicies > MaximumPolicies)
                throw new InvalidOperationException("Graph policy response exceeded the policy limit; evidence is incomplete.");
            reviews.AddRange(ReviewPolicies(root));
            total += counts.TotalPolicies;
            enabled += counts.EnabledPolicies;
            mfa += counts.EnabledMfaPolicies;
            legacyBlocks += counts.EnabledLegacyBlockPolicies;
            disabled += counts.DisabledPolicies;
            reportOnly += counts.ReportOnlyPolicies;
            unknown += counts.UnknownStatePolicies;

            if (!root.TryGetProperty("@odata.nextLink", out var next))
            {
                var securityDefaults = includeSecurityDefaults
                    ? await ReadSecurityDefaultsAsync(accessToken, cancellationToken)
                    : (bool?)null;
                var namedLocations = includeNamedLocations
                    ? await ReadNamedLocationCountAsync(accessToken, cancellationToken)
                    : (int?)null;
                string? migrationState = null;
                if (includeAuthenticationMethods)
                {
                    try { migrationState = await ReadAuthenticationMethodsMigrationStateAsync(accessToken, cancellationToken); }
                    catch (GraphEvidenceMissingMigrationStateException) { throw; }
                    catch (Exception exception) when (exception is InvalidOperationException or JsonException)
                    {
                        throw new GraphEvidenceFormatException("authentication-methods", exception);
                    }
                }
                return new ConditionalAccessEvidence(total, enabled, mfa, legacyBlocks, "NotAssessed",
                    "Policy details and selected evidence do not prove coverage, effective enforcement, exclusions, or compliance. Review tenant configuration manually.",
                    disabled, reportOnly, unknown, reviews, securityDefaults, namedLocations, migrationState);
            }
            if (next.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(next.GetString(), UriKind.Absolute, out var nextUri) ||
                nextUri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(nextUri.Host, InitialUri.Host, StringComparison.OrdinalIgnoreCase) ||
                !nextUri.IsDefaultPort ||
                !string.Equals(nextUri.AbsolutePath, PolicyPath, StringComparison.Ordinal) ||
                !string.IsNullOrEmpty(nextUri.UserInfo) || !string.IsNullOrEmpty(nextUri.Fragment))
                throw new InvalidOperationException("Graph returned an invalid pagination link; evidence is incomplete.");
            uri = nextUri;
        }
        throw new InvalidOperationException("Graph pagination exceeded the page limit; evidence is incomplete.");
    }

    private async Task<string> ReadAuthenticationMethodsMigrationStateAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/policies/authenticationMethodsPolicy");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
            throw new InvalidOperationException("Graph returned a redirect; evidence is incomplete.");
        if (!response.IsSuccessStatusCode)
            throw new GraphEvidenceHttpException("authentication-methods", response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + count > MaximumPageBytes)
                throw new InvalidOperationException("Graph authentication methods response exceeded the size limit; evidence is incomplete.");
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        using var json = await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
        if (json.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Graph authentication methods response was malformed; evidence is incomplete.");
        if (!json.RootElement.TryGetProperty("policyMigrationState", out var state) || state.ValueKind == JsonValueKind.Null)
            throw new GraphEvidenceMissingMigrationStateException();
        if (state.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(state.GetString()) ||
            state.GetString()!.Length > 80)
            throw new InvalidOperationException("Graph authentication methods response was malformed; evidence is incomplete.");
        var value = state.GetString()!;
        return value.ToLowerInvariant() switch
        {
            "premigration" => "preMigration",
            "migrationinprogress" => "migrationInProgress",
            "migrationcomplete" => "migrationComplete",
            _ => "unknown"
        };
    }

    private async Task<int> ReadNamedLocationCountAsync(string accessToken, CancellationToken cancellationToken)
    {
        const string path = "/v1.0/identity/conditionalAccess/namedLocations";
        var uri = new Uri($"https://graph.microsoft.com{path}?$select=id");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = 0;
        for (var page = 0; page < MaximumPages; page++)
        {
            if (!visited.Add(uri.AbsoluteUri))
                throw new InvalidOperationException("Graph named location pagination repeated a page; evidence is incomplete.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
                throw new InvalidOperationException("Graph returned a redirect; evidence is incomplete.");
            if (!response.IsSuccessStatusCode)
                throw new GraphEvidenceHttpException("named-locations", response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + count > MaximumPageBytes)
                    throw new InvalidOperationException("Graph named locations response exceeded the size limit; evidence is incomplete.");
                buffer.Write(chunk, 0, count);
            }
            buffer.Position = 0;
            using var json = await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("value", out var locations) ||
                locations.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Graph named locations response was malformed; evidence is incomplete.");
            foreach (var location in locations.EnumerateArray())
            {
                if (location.ValueKind != JsonValueKind.Object || !location.TryGetProperty("id", out var id) ||
                    id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                    throw new InvalidOperationException("Graph named location was malformed; evidence is incomplete.");
                if (!identifiers.Add(id.GetString()!))
                    throw new InvalidOperationException("Graph named locations contained duplicate IDs; evidence is incomplete.");
                if (++total > 1000)
                    throw new InvalidOperationException("Graph named locations exceeded the limit; evidence is incomplete.");
            }
            if (!root.TryGetProperty("@odata.nextLink", out var next)) return total;
            if (next.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(next.GetString(), UriKind.Absolute, out var nextUri) ||
                nextUri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(nextUri.Host, InitialUri.Host, StringComparison.OrdinalIgnoreCase) ||
                !nextUri.IsDefaultPort || !string.Equals(nextUri.AbsolutePath, path, StringComparison.Ordinal) ||
                !string.IsNullOrEmpty(nextUri.UserInfo) || !string.IsNullOrEmpty(nextUri.Fragment))
                throw new InvalidOperationException("Graph returned an invalid named locations pagination link; evidence is incomplete.");
            uri = nextUri;
        }
        throw new InvalidOperationException("Graph named locations pagination exceeded the page limit; evidence is incomplete.");
    }

    private async Task<bool> ReadSecurityDefaultsAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://graph.microsoft.com/v1.0/policies/identitySecurityDefaultsEnforcementPolicy");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
            throw new InvalidOperationException("Graph returned a redirect; evidence is incomplete.");
        if (!response.IsSuccessStatusCode)
            throw new GraphEvidenceHttpException("security-defaults", response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + count > MaximumPageBytes)
                throw new InvalidOperationException("Graph security defaults response exceeded the size limit; evidence is incomplete.");
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        using var json = await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty("isEnabled", out var enabled) ||
            enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidOperationException("Graph security defaults response was malformed; evidence is incomplete.");
        return enabled.GetBoolean();
    }

    public static OfflineConditionalAccessPreview PreviewExport(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Provide a Graph collection object containing a value array.");
        if (root.TryGetProperty("@odata.nextLink", out _))
            throw new InvalidOperationException("The export contains a next page; provide a complete collection, not a single page.");

        var counts = CountPolicies(root);
        var reviews = ReviewPolicies(root);
        return new OfflineConditionalAccessPreview(
            counts.TotalPolicies, counts.EnabledPolicies, counts.EnabledMfaPolicies,
            counts.EnabledLegacyBlockPolicies, "NotAssessed",
            "Offline user-supplied export: tenant identity, freshness, and completeness cannot be verified. Policy details do not prove coverage, effective enforcement, exclusions, or security defaults. Missing fields are unknown, not empty. No assessment result was changed.",
            reviews);
    }

    private static OfflinePolicyReview[] ReviewPolicies(JsonElement root) =>
        root.GetProperty("value").EnumerateArray().Select(policy =>
        {
            var grant = OptionalObject(policy, "grantControls");
            var conditions = OptionalObject(policy, "conditions");
            var users = OptionalObject(conditions, "users");
            var applications = OptionalObject(conditions, "applications");
            var includedUsers = OptionalStrings(users, "includeUsers");
            var includedApplications = OptionalStrings(applications, "includeApplications");
            return new OfflinePolicyReview(
                OptionalString(policy, "displayName"),
                policy.GetProperty("state").GetString()!,
                OptionalStrings(grant, "builtInControls"),
                OptionalString(grant, "operator"),
                OptionalStrings(conditions, "clientAppTypes"),
                includedUsers?.Contains("All", StringComparer.OrdinalIgnoreCase),
                OptionalStrings(users, "excludeUsers")?.Length,
                OptionalStrings(users, "excludeGroups")?.Length,
                OptionalStrings(users, "excludeRoles")?.Length,
                includedApplications?.Contains("All", StringComparer.OrdinalIgnoreCase),
                OptionalStrings(applications, "excludeApplications")?.Length);
        }).ToArray();

    private static JsonElement? OptionalObject(JsonElement? parent, string name)
    {
        if (parent is null || !parent.Value.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Graph policy response was malformed; evidence is incomplete.");
        return value;
    }

    private static string? OptionalString(JsonElement? parent, string name)
    {
        if (parent is null || !parent.Value.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Graph policy response was malformed; evidence is incomplete.");
        return value.GetString();
    }

    private static string[]? OptionalStrings(JsonElement? parent, string name)
    {
        if (parent is null || !parent.Value.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Graph policy response was malformed; evidence is incomplete.");
        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("Graph policy response was malformed; evidence is incomplete.");
            result.Add(item.GetString()!);
        }
        return result.ToArray();
    }

    private static ConditionalAccessEvidence CountPolicies(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("value", out var policies) || policies.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Graph policy response was malformed; evidence is incomplete.");

        var total = 0;
        var enabled = 0;
        var mfa = 0;
        var legacyBlocks = 0;
        var disabled = 0;
        var reportOnly = 0;
        var unknown = 0;
        foreach (var policy in policies.EnumerateArray())
        {
            if (policy.ValueKind != JsonValueKind.Object ||
                !policy.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("Graph policy state was missing; evidence is incomplete.");
            total++;
            var stateValue = state.GetString();
            if (!string.Equals(stateValue, "enabled", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(stateValue, "disabled", StringComparison.OrdinalIgnoreCase)) disabled++;
                else if (string.Equals(stateValue, "enabledForReportingButNotEnforced", StringComparison.OrdinalIgnoreCase)) reportOnly++;
                else unknown++;
                continue;
            }
            enabled++;
            var controls = GetStrings(policy, "grantControls", "builtInControls");
            var clients = GetStrings(policy, "conditions", "clientAppTypes");
            if (controls.Contains("mfa", StringComparer.OrdinalIgnoreCase))
                mfa++;
            if (controls.Contains("block", StringComparer.OrdinalIgnoreCase) &&
                (clients.Contains("exchangeActiveSync", StringComparer.OrdinalIgnoreCase) ||
                 clients.Contains("other", StringComparer.OrdinalIgnoreCase)))
                legacyBlocks++;
        }
        return new ConditionalAccessEvidence(total, enabled, mfa, legacyBlocks, "NotAssessed", "", disabled, reportOnly, unknown);
    }

    private static string[] GetStrings(JsonElement policy, string parentName, string propertyName)
    {
        if (!policy.TryGetProperty(parentName, out var parent) || parent.ValueKind == JsonValueKind.Null)
            return [];
        if (parent.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Graph policy response was malformed; evidence is incomplete.");
        if (!parent.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
            return [];
        if (property.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Graph policy response was malformed; evidence is incomplete.");
        var values = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("Graph policy response was malformed; evidence is incomplete.");
            values.Add(item.GetString()!);
        }
        return values.ToArray();
    }
}
