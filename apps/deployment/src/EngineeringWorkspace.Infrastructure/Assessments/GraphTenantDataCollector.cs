using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Credentials;
using EngineeringWorkspace.Application.Tenants;
using EngineeringWorkspace.Domain.Tenants;
using EngineeringWorkspace.Infrastructure.Microsoft;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Infrastructure.Assessments;

/// <summary>
/// Reads a customer tenant with its app registration: app-only Microsoft Graph GET requests (paged, throttling-aware)
/// plus public DNS (DNS over HTTPS) for SPF, DKIM and DMARC. Read-only; a dataset the app is not permitted to read is
/// recorded as Forbidden and its checks are reported as not evaluated, so one missing permission never fails the run.
/// </summary>
public sealed class GraphTenantDataCollector(ISecretStore secrets, TimeProvider timeProvider, ILogger<GraphTenantDataCollector> logger) : ITenantDataCollector
{
    private const int MaximumPages = 60;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(100) };

    private sealed record DatasetRequest(string Name, string Path, bool Beta = false, bool Eventual = false, bool SingleObject = false, string? Fallback = null);

    private static readonly DatasetRequest[] Requests =
    [
        new(TenantDatasets.Organization, "organization?$select=id,displayName,verifiedDomains,onPremisesSyncEnabled"),
        new(TenantDatasets.Domains, "domains?$select=id,isVerified,isDefault,isInitial,authenticationType,passwordValidityPeriodInDays,supportedServices"),
        new(TenantDatasets.SubscribedSkus, "subscribedSkus?$select=skuId,skuPartNumber,consumedUnits,prepaidUnits,capabilityStatus,servicePlans"),
        new(TenantDatasets.ConditionalAccessPolicies, "identity/conditionalAccess/policies"),
        new(TenantDatasets.AuthorizationPolicy, "policies/authorizationPolicy", SingleObject: true),
        new(TenantDatasets.SecurityDefaults, "policies/identitySecurityDefaultsEnforcementPolicy", SingleObject: true),
        new(TenantDatasets.RegistrationDetails, "reports/authenticationMethods/userRegistrationDetails?$select=id,userPrincipalName,userType,isAdmin,isMfaCapable,isMfaRegistered,methodsRegistered&$top=999"),
        new(TenantDatasets.RoleAssignments, "roleManagement/directory/roleAssignments?$expand=principal"),
        new(TenantDatasets.RoleEligibilitySchedules, "roleManagement/directory/roleEligibilitySchedules"),
        new(TenantDatasets.Users, $"users?$select={UserFields},signInActivity&$top=999", Fallback: $"users?$select={UserFields}&$top=999"),
        new(TenantDatasets.Devices, "devices?$select=id,displayName,accountEnabled,approximateLastSignInDateTime,operatingSystem,trustType&$top=999"),
        new(TenantDatasets.Groups, "groups?$select=id,displayName,description,mail,mailEnabled,securityEnabled,groupTypes,membershipRule,isAssignableToRole,onPremisesSyncEnabled,createdDateTime&$top=999"),
        new(TenantDatasets.DynamicGroups, "groups?$filter=groupTypes/any(c:c eq 'DynamicMembership')&$select=id,displayName,membershipRule&$count=true&$top=999", Eventual: true),
        new(TenantDatasets.Applications, "applications?$select=id,appId,displayName,createdDateTime,signInAudience&$top=999"),
        // Settings catalog and endpoint security policies, and mobileApp.isAssigned, exist only in the beta API.
        new(TenantDatasets.ConfigurationPolicies, "deviceManagement/configurationPolicies?$expand=settings,assignments&$top=100", Beta: true),
        new(TenantDatasets.DeviceConfigurations, "deviceManagement/deviceConfigurations?$expand=assignments"),
        new(TenantDatasets.CompliancePolicies, "deviceManagement/deviceCompliancePolicies?$expand=assignments"),
        new(TenantDatasets.EnrollmentConfigurations, "deviceManagement/deviceEnrollmentConfigurations"),
        new(TenantDatasets.ManagedDevices, "deviceManagement/managedDevices?$select=id,deviceName,operatingSystem,osVersion,complianceState,managedDeviceOwnerType,lastSyncDateTime,enrolledDateTime,isEncrypted,userPrincipalName,azureADDeviceId,model,manufacturer&$top=999"),
        new(TenantDatasets.AndroidAppProtections, "deviceAppManagement/androidManagedAppProtections"),
        new(TenantDatasets.IosAppProtections, "deviceAppManagement/iosManagedAppProtections"),
        new(TenantDatasets.AssignedApps, "deviceAppManagement/mobileApps?$filter=isAssigned eq true&$select=id,displayName&$expand=assignments", Beta: true),
        new(TenantDatasets.Scripts, "deviceManagement/deviceManagementScripts?$select=id,displayName", Beta: true),
        new(TenantDatasets.DriverUpdateProfiles, "deviceManagement/windowsDriverUpdateProfiles", Beta: true),
        new(TenantDatasets.SecureScores, "security/secureScores?$top=1"),
        new(TenantDatasets.AttackSimulations, "security/attackSimulation/simulations?$top=100"),
        new(TenantDatasets.SensitivityLabels, "security/informationProtection/sensitivityLabels", Beta: true),
        new(TenantDatasets.RetentionLabels, "security/labels/retentionLabels"),
        new(TenantDatasets.SharePointSettings, "admin/sharepoint/settings", SingleObject: true),

        // 4.0 beta.2 tenant settings (in-depth scans; a dataset the registration may not read is reported as forbidden).
        new(TenantDatasets.AuthenticationMethodsPolicy, "policies/authenticationMethodsPolicy", Beta: true, SingleObject: true),
        new(TenantDatasets.CrossTenantAccessDefault, "policies/crossTenantAccessPolicy/default", SingleObject: true),
        new(TenantDatasets.AdminConsentRequestPolicy, "policies/adminConsentRequestPolicy", SingleObject: true),
        new(TenantDatasets.DeviceRegistrationPolicy, "policies/deviceRegistrationPolicy", Beta: true, SingleObject: true),
        new(TenantDatasets.GroupSettings, "groupSettings"),
        new(TenantDatasets.GlobalAdminPimPolicy,
            "policies/roleManagementPolicyAssignments?$filter=scopeId eq '/' and scopeType eq 'DirectoryRole' and roleDefinitionId eq '62e90394-69f5-4237-9190-012177145e10'&$expand=policy($expand=rules)"),
        new(TenantDatasets.AccessReviews, "identityGovernance/accessReviews/definitions?$select=id,displayName,status&$top=100"),
        new(TenantDatasets.ActivityTimeoutPolicies, "policies/activityBasedTimeoutPolicies"),
        new(TenantDatasets.ReportSettings, "admin/reportSettings", SingleObject: true),
        new(TenantDatasets.FormsSettings, "admin/forms/settings", Beta: true, SingleObject: true),
        new(TenantDatasets.IntuneSettings, "deviceManagement?$select=settings", SingleObject: true),
        new(TenantDatasets.AutopilotProfiles, "deviceManagement/windowsAutopilotDeploymentProfiles?$select=id,displayName", Beta: true),
        new(TenantDatasets.MultiAdminApproval, "deviceManagement/operationApprovalPolicies?$select=id,displayName,policyType", Beta: true),
        new(TenantDatasets.IntuneRoleAssignments, "deviceManagement/roleAssignments?$select=id,displayName"),
        new(TenantDatasets.ForwardingProfiles, "networkAccess/forwardingProfiles?$select=id,name,state,trafficForwardingType", Beta: true),
        new(TenantDatasets.BackupService, "solutions/backupRestore", Beta: true, SingleObject: true),
        new(TenantDatasets.TeamsAppSettings, "teamwork/teamsAppSettings", SingleObject: true),
        new(TenantDatasets.NamedLocations, "identity/conditionalAccess/namedLocations"),
        new(TenantDatasets.AuthenticationStrengths, "policies/authenticationStrengthPolicies?$select=id,displayName,policyType,allowedCombinations"),
        new(TenantDatasets.AppsAndServices, "admin/appsAndServices", Beta: true, SingleObject: true),
        new(TenantDatasets.CloudPcUserSettings, "deviceManagement/virtualEndpoint/userSettings?$select=id,displayName,localAdminEnabled,resetEnabled,restorePointSetting", Beta: true),
        new(TenantDatasets.CloudPcProvisioningPolicies, "deviceManagement/virtualEndpoint/provisioningPolicies?$select=id,displayName,enableSingleSignOn,provisioningType,windowsSetting", Beta: true),
    ];

    private const string UserFields = "id,displayName,userPrincipalName,mail,accountEnabled,userType,onPremisesSyncEnabled,createdDateTime,department,jobTitle,usageLocation,assignedLicenses";

    /// <summary>Member counts are read for at most this many groups (20 per Graph batch); larger tenants count linked groups only.</summary>
    public const int MaximumCountedGroups = 1000;

    public static IReadOnlyList<string> DatasetNames { get; } =
        [.. Requests.Select(request => request.Name), TenantDatasets.DnsRecords, TenantDatasets.GroupMemberCounts, TenantDatasets.GroupMembers];

    public async Task<TenantSnapshot> CollectAsync(TenantConfiguration tenant, TenantScanOptions options, IProgress<(double Progress, string Activity)>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (tenant.TenantId is not { } tenantId || tenant.ApplicationRegistration is not { } registration || registration.SecretReference is not { } secret)
            throw new InvalidOperationException("The customer's tenant has no Read registration with a stored certificate. Connect the tenant (Customer Profiles) first.");
        var endpoints = MicrosoftCloudEndpoints.For(tenant.CloudEnvironment);
        progress?.Report((2, "Signing in to Microsoft Graph (app-only)..."));
        var token = (await MsalMicrosoftAuthenticationService.AcquireAsync(secrets, tenantId, registration.ClientId, secret, tenant.CloudEnvironment, cancellationToken)).AccessToken;

        var datasets = new Dictionary<string, TenantDataset>(StringComparer.Ordinal);
        var requests = Requests.Where(request => ScanTypes.Includes(options.ScanType, request.Name)).ToArray();
        for (var index = 0; index < requests.Length; index++)
        {
            var request = requests[index];
            progress?.Report((5 + 75.0 * index / requests.Length, $"{ScanTypes.Display(options.ScanType)}: reading {request.Name}..."));
            datasets[request.Name] = await ReadAsync(endpoints.GraphBaseUrl, token, request, cancellationToken);
        }

        progress?.Report((82, "Counting group members..."));
        var groups = datasets.GetValueOrDefault(TenantDatasets.Groups);
        datasets[TenantDatasets.GroupMemberCounts] = await ReadMemberCountsAsync(endpoints.GraphBaseUrl, token, groups, options.LinkedGroupIds, cancellationToken);
        progress?.Report((87, "Reading members of the linked groups..."));
        datasets[TenantDatasets.GroupMembers] = await ReadMembersAsync(endpoints.GraphBaseUrl, token, options.LinkedGroupIds, cancellationToken);

        progress?.Report((92, "Checking SPF, DKIM and DMARC in public DNS..."));
        datasets[TenantDatasets.DnsRecords] = await ReadDnsAsync(datasets[TenantDatasets.Domains], cancellationToken);
        progress?.Report((95, "Tenant data collected."));
        var forbidden = datasets.Values.Where(dataset => dataset.Status == DatasetStatus.Forbidden).Select(dataset => dataset.Name).ToArray();
        logger.LogInformation("Collected {Count} datasets from tenant {TenantId}; forbidden: {Forbidden}", datasets.Count, tenantId, string.Join(", ", forbidden));
        return new TenantSnapshot(tenantId, timeProvider.GetUtcNow(), datasets);
    }

    private async Task<TenantDataset> ReadAsync(Uri graphBase, string token, DatasetRequest request, CancellationToken cancellationToken)
    {
        var version = request.Beta ? "beta" : "v1.0";
        var source = $"GET /{version}/{request.Path}";
        var result = await ReadPagesAsync(new Uri(graphBase, $"{version}/{request.Path}"), token, request, cancellationToken);
        if (result.Status is DatasetStatus.Forbidden or DatasetStatus.NotAvailable && request.Fallback is { } fallback)
        {
            var retry = await ReadPagesAsync(new Uri(graphBase, $"{version}/{fallback}"), token, request, cancellationToken);
            if (retry.Status == DatasetStatus.Collected)
                return retry with { Source = $"GET /{version}/{fallback}", Message = $"Fallback without sign-in activity: {result.Message}" };
        }

        return result with { Source = source };
    }

    private async Task<TenantDataset> ReadPagesAsync(Uri first, string token, DatasetRequest request, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        Uri? next = first;
        var pages = 0;
        while (next is not null && pages++ < MaximumPages)
        {
            using var response = await SendAsync(next, token, request.Eventual, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var message = GraphError(body) ?? response.ReasonPhrase;
                var status = response.StatusCode switch
                {
                    HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => DatasetStatus.Forbidden,
                    HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.NotImplemented => DatasetStatus.NotAvailable,
                    _ => DatasetStatus.Failed
                };
                logger.LogInformation("Dataset {Dataset}: HTTP {Status} {Message}", request.Name, (int)response.StatusCode, message);
                return new TenantDataset(request.Name, string.Empty, status, items, $"HTTP {(int)response.StatusCode}: {message}");
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (request.SingleObject || !root.TryGetProperty("value", out var value))
            {
                items.Add(root.Clone());
                break;
            }

            items.AddRange(value.EnumerateArray().Select(item => item.Clone()));
            next = root.TryGetProperty("@odata.nextLink", out var link) && Uri.TryCreate(link.GetString(), UriKind.Absolute, out var nextUri) ? nextUri : null;
        }

        return new TenantDataset(request.Name, string.Empty, DatasetStatus.Collected, items, next is null ? null : $"Truncated after {MaximumPages} pages.");
    }

    private static async Task<HttpResponseMessage> SendAsync(Uri uri, string token, bool eventual, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (eventual) request.Headers.Add("ConsistencyLevel", "eventual");
            var response = await Http.SendAsync(request, cancellationToken);
            if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 4)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                response.Dispose();
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            return response;
        }
    }

    /// <summary>Transitive member counts through JSON batches of 20: every group up to <see cref="MaximumCountedGroups"/>, otherwise only the linked groups.</summary>
    private async Task<TenantDataset> ReadMemberCountsAsync(Uri graphBase, string token, TenantDataset? groups, IReadOnlyCollection<Guid> linked, CancellationToken cancellationToken)
    {
        const string source = "POST /v1.0/$batch (groups/{id}/transitiveMembers/$count)";
        if (groups is not { IsCollected: true })
            return new TenantDataset(TenantDatasets.GroupMemberCounts, source, DatasetStatus.NotAvailable, [], "Groups could not be read.");
        var ids = groups.Items.Select(group => group.TryGetProperty("id", out var id) ? id.GetString() : null).OfType<string>().ToList();
        var limited = ids.Count > MaximumCountedGroups;
        if (limited) ids = linked.Select(id => id.ToString()).Where(id => ids.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
        var items = new List<JsonElement>();
        foreach (var chunk in ids.Chunk(20))
        {
            var body = JsonSerializer.Serialize(new
            {
                requests = chunk.Select((id, index) => new
                {
                    id = index.ToString(System.Globalization.CultureInfo.InvariantCulture), method = "GET",
                    url = $"/groups/{id}/transitiveMembers/$count", headers = new Dictionary<string, string> { ["ConsistencyLevel"] = "eventual" }
                })
            });
            using var response = await PostAsync(new Uri(graphBase, "v1.0/$batch"), token, body, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized ? DatasetStatus.Forbidden : DatasetStatus.Failed;
                return new TenantDataset(TenantDatasets.GroupMemberCounts, source, status, items, $"HTTP {(int)response.StatusCode}: {GraphError(text) ?? response.ReasonPhrase}");
            }

            using var document = JsonDocument.Parse(text);
            foreach (var answer in document.RootElement.GetProperty("responses").EnumerateArray())
            {
                if (!answer.TryGetProperty("status", out var code) || code.GetInt32() != 200 || !answer.TryGetProperty("body", out var value)) continue;
                long? count = value.ValueKind switch
                {
                    JsonValueKind.Number => value.GetInt64(),
                    JsonValueKind.String when long.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
                    _ => null
                };
                if (count is null) continue;
                var groupId = chunk[int.Parse(answer.GetProperty("id").GetString()!, System.Globalization.CultureInfo.InvariantCulture)];
                items.Add(JsonSerializer.SerializeToElement(new { groupId, count }));
            }
        }

        return new TenantDataset(TenantDatasets.GroupMemberCounts, source, DatasetStatus.Collected, items,
            limited ? $"More than {MaximumCountedGroups} groups: counted the {ids.Count} linked group(s) only." : $"{items.Count} group(s) counted.");
    }

    /// <summary>Transitive members (users, devices, groups) of the groups linked to Secure Plus user groups or deployment variables.</summary>
    private async Task<TenantDataset> ReadMembersAsync(Uri graphBase, string token, IReadOnlyCollection<Guid> linked, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        foreach (var groupId in linked.Distinct())
        {
            var request = new DatasetRequest(TenantDatasets.GroupMembers, $"groups/{groupId}/transitiveMembers?$select=id&$top=999");
            var page = await ReadPagesAsync(new Uri(graphBase, $"v1.0/{request.Path}"), token, request, cancellationToken);
            if (page.Status == DatasetStatus.Forbidden) return page with { Source = "GET /v1.0/groups/{id}/transitiveMembers" };
            foreach (var member in page.Items)
            {
                var type = member.TryGetProperty("@odata.type", out var odata) ? odata.GetString()?.Replace("#microsoft.graph.", string.Empty, StringComparison.Ordinal) : null;
                items.Add(JsonSerializer.SerializeToElement(new { groupId = groupId.ToString(), memberId = member.GetProperty("id").GetString(), type }));
            }
        }

        return new TenantDataset(TenantDatasets.GroupMembers, "GET /v1.0/groups/{id}/transitiveMembers", DatasetStatus.Collected, items, $"{linked.Count} linked group(s).");
    }

    private static async Task<HttpResponseMessage> PostAsync(Uri uri, string token, string json, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await Http.SendAsync(request, cancellationToken);
            if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 4)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                response.Dispose();
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            return response;
        }
    }

    private static string? GraphError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message) ? message.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>SPF, DMARC and DKIM (selector1/selector2 CNAMEs) of each verified custom domain, through DNS over HTTPS.</summary>
    private async Task<TenantDataset> ReadDnsAsync(TenantDataset domains, CancellationToken cancellationToken)
    {
        if (!domains.IsCollected) return new TenantDataset(TenantDatasets.DnsRecords, "DNS over HTTPS", DatasetStatus.NotAvailable, [], "Domains could not be read.");
        var custom = domains.Items
            .Where(domain => domain.TryGetProperty("isVerified", out var verified) && verified.ValueKind == JsonValueKind.True)
            .Select(domain => domain.GetProperty("id").GetString()!)
            .Where(name => !name.EndsWith(".onmicrosoft.com", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".onmicrosoft.us", StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .ToArray();
        var items = new List<JsonElement>();
        try
        {
            foreach (var domain in custom)
            {
                var spf = (await QueryDnsAsync(domain, "TXT", cancellationToken)).FirstOrDefault(record => record.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase));
                var dmarc = (await QueryDnsAsync($"_dmarc.{domain}", "TXT", cancellationToken)).FirstOrDefault(record => record.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase));
                var dkim1 = (await QueryDnsAsync($"selector1._domainkey.{domain}", "CNAME", cancellationToken)).FirstOrDefault();
                var dkim2 = (await QueryDnsAsync($"selector2._domainkey.{domain}", "CNAME", cancellationToken)).FirstOrDefault();
                items.Add(JsonSerializer.SerializeToElement(new { domain, spf, dmarc, dkimSelector1 = dkim1, dkimSelector2 = dkim2 }));
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogInformation(exception, "DNS over HTTPS lookups failed");
            return new TenantDataset(TenantDatasets.DnsRecords, "DNS over HTTPS", DatasetStatus.Failed, items, "Public DNS could not be queried from this network.");
        }

        return new TenantDataset(TenantDatasets.DnsRecords, "DNS over HTTPS (cloudflare-dns.com)", DatasetStatus.Collected, items, $"{custom.Length} custom domain(s).");
    }

    private static async Task<IReadOnlyList<string>> QueryDnsAsync(string name, string type, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(name)}&type={type}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));
        using var response = await Http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return [];
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("Answer", out var answers)) return [];
        return answers.EnumerateArray().Select(answer => answer.GetProperty("data").GetString() ?? string.Empty)
            .Select(data => data.Replace("\" \"", string.Empty, StringComparison.Ordinal).Trim('"')).ToArray();
    }
}
