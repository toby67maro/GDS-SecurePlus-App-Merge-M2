using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using ArchiTech.Api.Data;
using ArchiTech.Api.Domain;
using ArchiTech.Api.Endpoints;
using ArchiTech.Api.Integrations;
using ArchiTech.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArchiTech.Tests;

public sealed class DeploymentWorkflowTests : IAsyncLifetime
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection = new("Data Source=:memory:");
    private WebApplication _app = null!;
    private DeploymentControlCatalog _catalog = null!;
    private readonly FakeLocalEvidenceWorker _liveWorker = new();

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _catalog = DeploymentControlCatalog.Load(Path.Combine(
            AppContext.BaseDirectory,
            "Data",
            "deployment-controls-v2.0.json"));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
        builder.Services.AddSingleton(_catalog);
        builder.Services.AddSingleton<CustomerRoadmapWorkbook>();
        builder.Services.AddSingleton(new ArchiTech.Api.Integrations.DeploymentBridge(Microsoft.Extensions.Options.Options.Create(new ArchiTech.Api.Integrations.BridgeOptions()), Microsoft.Extensions.Logging.Abstractions.NullLogger<ArchiTech.Api.Integrations.DeploymentBridge>.Instance));
        builder.Services.AddSingleton<ILocalEvidenceWorker>(_liveWorker);
        builder.Services.AddHttpClient<PlannerHandoffService>();
        builder.Services.Configure<PlannerFlowOptions>(_ => { });
        builder.Services
            .AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireRole(
                    DeploymentRoles.Sales,
                    DeploymentRoles.Engineer,
                    DeploymentRoles.Approver,
                    DeploymentRoles.Operator,
                    DeploymentRoles.Administrator)
                .Build();
            options.AddPolicy("DeploymentAccess", policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(
                    DeploymentRoles.Engineer,
                    DeploymentRoles.Approver,
                    DeploymentRoles.Operator,
                    DeploymentRoles.Administrator));
            options.AddPolicy("DeploymentAuthor", policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(DeploymentRoles.Engineer, DeploymentRoles.Administrator));
            options.AddPolicy("DeploymentApproval", policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(DeploymentRoles.Approver, DeploymentRoles.Administrator));
        });

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            await WbsCatalogSeeder.SeedAsync(
                db,
                Path.Combine(AppContext.BaseDirectory, "Data", "secure-plus-wbs-tasks.csv"));
            await StandardsCatalogSeeder.SeedAsync(
                db,
                Path.Combine(AppContext.BaseDirectory, "Data", "standards"));
        }
        _app.MapSalesEndpoints("Sqlite");
        _app.MapDeploymentEndpoints();
        _app.MapAssessmentEndpoints();
        _app.MapLocalEvidenceEndpoints();
        _app.MapDeploymentChangePlanEndpoints();
        _app.MapStandardsEndpoints();
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Cancelling_live_request_signals_worker_without_saving_evidence()
    {
        using var engineer = CreateClient(DeploymentRoles.Engineer, "engineer");
        var response = await engineer.PostAsJsonAsync("/api/deployment/profiles", new { customerName = "Cancellation test" });
        using var created = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var customer = created.RootElement.GetProperty("customerId").GetGuid();
        var tenant = Guid.NewGuid();
        var client = Guid.NewGuid();
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profile = await db.DeploymentProfiles.SingleAsync(p => p.CustomerId == customer);
            profile.TenantId = tenant.ToString();
            profile.EntraApplicationClientId = client.ToString();
            await db.SaveChangesAsync();
        }

        _liveWorker.WaitForCancellation = true;
        using var cancellation = new CancellationTokenSource();
        var request = engineer.PostAsJsonAsync($"/api/deployment/profiles/{customer}/conditional-access-evidence/live",
            new { tenantId = tenant, clientId = client, confirmed = true }, cancellation.Token);
        await _liveWorker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await _liveWorker.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await using var verifyScope = _app.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await verifyDb.DeploymentFindings.ToListAsync());
        Assert.Empty(await verifyDb.AssessmentRuns.ToListAsync());
    }

    [Fact]
    public async Task Live_evidence_requires_author_role_confirmed_matching_profile_and_does_not_change_findings()
    {
        using var engineer = CreateClient(DeploymentRoles.Engineer, "engineer");
        using var sales = CreateClient(DeploymentRoles.Sales, "sales");
        var profileResponse = await engineer.PostAsJsonAsync("/api/deployment/profiles", new { customerName = "Live evidence test" });
        using var created = System.Text.Json.JsonDocument.Parse(await profileResponse.Content.ReadAsStringAsync());
        var customer = created.RootElement.GetProperty("customerId").GetGuid();
        var tenant = Guid.NewGuid();
        var app = Guid.NewGuid();
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profile = await db.DeploymentProfiles.SingleAsync(p => p.CustomerId == customer);
            profile.TenantId = tenant.ToString();
            profile.EntraApplicationClientId = app.ToString();
            await db.SaveChangesAsync();
        }
        var url = $"/api/deployment/profiles/{customer}/conditional-access-evidence/live";
        var request = new { tenantId = tenant.ToString(), clientId = app.ToString(), confirmed = true };
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.PostAsJsonAsync(url, request)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await engineer.PostAsJsonAsync(url, new { tenantId = tenant.ToString(), clientId = app.ToString(), confirmed = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await engineer.PostAsJsonAsync(url, new { tenantId = Guid.NewGuid().ToString(), clientId = app.ToString(), confirmed = true })).StatusCode);
        Assert.Equal(0, _liveWorker.Reads);
        var response = await engineer.PostAsJsonAsync(url, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, _liveWorker.Reads);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("NotAssessed", json.RootElement.GetProperty("evidence").GetProperty("assessmentStatus").GetString());
        var candidates = json.RootElement.GetProperty("reviewCandidates").EnumerateArray().ToArray();
        Assert.Equal(["AUTH-004", "AUTH-002"], candidates.Select(candidate => candidate.GetProperty("controlId").GetString()));
        Assert.All(candidates, candidate =>
        {
            Assert.Contains(tenant.ToString(), candidate.GetProperty("summary").GetString());
            Assert.Contains("does not establish", candidate.GetProperty("limitations").GetString());
            Assert.False(candidate.TryGetProperty("status", out _));
        });
        var defaultsResponse = await engineer.PostAsJsonAsync(url,
            new { tenantId = tenant.ToString(), clientId = app.ToString(), confirmed = true, includeSecurityDefaults = true });
        Assert.Equal(HttpStatusCode.OK, defaultsResponse.StatusCode);
        Assert.True(_liveWorker.IncludedSecurityDefaults);
        using var defaultsJson = System.Text.Json.JsonDocument.Parse(await defaultsResponse.Content.ReadAsStringAsync());
        Assert.False(defaultsJson.RootElement.GetProperty("evidence").GetProperty("securityDefaultsEnabled").GetBoolean());
        _liveWorker.OmitSecurityDefaults = true;
        var incompleteResponse = await engineer.PostAsJsonAsync(url,
            new { tenantId = tenant.ToString(), clientId = app.ToString(), confirmed = true, includeSecurityDefaults = true });
        Assert.Equal(HttpStatusCode.BadGateway, incompleteResponse.StatusCode);
        var locationsResponse = await engineer.PostAsJsonAsync(url,
            new { tenantId = tenant.ToString(), clientId = app.ToString(), confirmed = true, includeNamedLocations = true });
        Assert.Equal(HttpStatusCode.OK, locationsResponse.StatusCode);
        Assert.True(_liveWorker.IncludedNamedLocations);
        using var locationsJson = System.Text.Json.JsonDocument.Parse(await locationsResponse.Content.ReadAsStringAsync());
        Assert.Equal(0, locationsJson.RootElement.GetProperty("evidence").GetProperty("namedLocationCount").GetInt32());
        _liveWorker.OmitNamedLocations = true;
        var missingLocations = await engineer.PostAsJsonAsync(url,
            new { tenantId = tenant.ToString(), clientId = app.ToString(), confirmed = true, includeNamedLocations = true });
        Assert.Equal(HttpStatusCode.BadGateway, missingLocations.StatusCode);
        var methodsResponse = await engineer.PostAsJsonAsync(url,
            new { tenantId = tenant.ToString(), clientId = app.ToString(), confirmed = true, includeAuthenticationMethods = true });
        Assert.Equal(HttpStatusCode.OK, methodsResponse.StatusCode);
        Assert.True(_liveWorker.IncludedAuthenticationMethods);
        using var methodsJson = System.Text.Json.JsonDocument.Parse(await methodsResponse.Content.ReadAsStringAsync());
        Assert.Equal("migrationComplete", methodsJson.RootElement.GetProperty("evidence").GetProperty("authenticationMethodsMigrationState").GetString());
        _liveWorker.OmitAuthenticationMethods = true;
        var missingMethods = await engineer.PostAsJsonAsync(url,
            new { tenantId = tenant.ToString(), clientId = app.ToString(), confirmed = true, includeAuthenticationMethods = true });
        Assert.Equal(HttpStatusCode.BadGateway, missingMethods.StatusCode);
        await using var verifyScope = _app.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await verifyDb.DeploymentFindings.ToListAsync());
        Assert.Empty(await verifyDb.AssessmentRuns.ToListAsync());
    }

    private sealed class FakeLocalEvidenceWorker : ILocalEvidenceWorker
    {
        public int Reads { get; private set; }
        public bool IncludedSecurityDefaults { get; private set; }
        public bool OmitSecurityDefaults { get; set; }
        public bool IncludedNamedLocations { get; private set; }
        public bool OmitNamedLocations { get; set; }
        public bool IncludedAuthenticationMethods { get; private set; }
        public bool OmitAuthenticationMethods { get; set; }
        public bool WaitForCancellation { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsAvailable(HttpContext context) => true;
        public async Task<LiveEvidenceResult> ReadAsync(Guid tenantId, Guid clientId, bool includeSecurityDefaults, bool includeNamedLocations, bool includeAuthenticationMethods, CancellationToken cancellationToken)
        {
            Reads++;
            IncludedSecurityDefaults = includeSecurityDefaults;
            IncludedNamedLocations = includeNamedLocations;
            IncludedAuthenticationMethods = includeAuthenticationMethods;
            if (WaitForCancellation)
            {
                Started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException)
                {
                    Cancelled.TrySetResult();
                    throw;
                }
            }
            return new LiveEvidenceResult(tenantId, DateTimeOffset.UtcNow,
                new ConditionalAccessEvidence(1, 0, 0, 0, "NotAssessed", "Manual review required.", 0, 1,
                    SecurityDefaultsEnabled: includeSecurityDefaults && !OmitSecurityDefaults ? false : null,
                    NamedLocationCount: includeNamedLocations && !OmitNamedLocations ? 0 : null,
                    AuthenticationMethodsMigrationState: includeAuthenticationMethods && !OmitAuthenticationMethods ? "migrationComplete" : null));
        }
    }

    [Fact]
    public async Task Sales_can_use_engagement_api_but_cannot_read_deployment_profiles()
    {
        using var client = CreateClient(DeploymentRoles.Sales, "sales-user");

        var engagementResponse = await client.GetAsync("/api/engagements");
        var profilesResponse = await client.GetAsync("/api/deployment/profiles");
        var createProfileResponse = await client.PostAsJsonAsync(
            "/api/deployment/profiles",
            new { customerName = "Sales must not create this deployment profile" });
        var assessmentResponse = await client.GetAsync(
            $"/api/deployment/profiles/{Guid.NewGuid()}/assessments");
        var setupChecklistResponse = await client.GetAsync(
            $"/api/deployment/profiles/{Guid.NewGuid()}/setup-checklist");

        Assert.Equal(HttpStatusCode.OK, engagementResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, profilesResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, createProfileResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, assessmentResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, setupChecklistResponse.StatusCode);
    }

    [Fact]
    public async Task Deployment_engineer_can_add_a_customer_profile_without_creating_an_engagement()
    {
        using var engineer = CreateClient(DeploymentRoles.Engineer, "deployment-user");
        var response = await engineer.PostAsJsonAsync(
            "/api/deployment/profiles",
            new
            {
                customerName = "  Manual   Customer  ",
                contactName = "  Test Contact ",
                contactEmail = " contact@example.com "
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var responseJson = System.Text.Json.JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        var root = responseJson.RootElement;
        var customerId = root.GetProperty("customerId").GetGuid();
        Assert.Equal("Manual   Customer", root.GetProperty("customerName").GetString());
        Assert.Equal("Test Contact", root.GetProperty("contactName").GetString());
        Assert.Equal("contact@example.com", root.GetProperty("contactEmail").GetString());
        Assert.False(root.GetProperty("tenantChangesEnabled").GetBoolean());

        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var customer = await db.Customers.SingleAsync(item => item.Id == customerId);
            Assert.Equal("MANUAL CUSTOMER", customer.NormalizedName);
            Assert.Empty(await db.Engagements.Where(item => item.CustomerId == customerId).ToListAsync());
            Assert.NotNull(await db.DeploymentProfiles.SingleOrDefaultAsync(
                item => item.CustomerId == customerId));
        }

        var duplicateResponse = await engineer.PostAsJsonAsync(
            "/api/deployment/profiles",
            new { customerName = "manual customer" });
        Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);

        var controlsResponse = await engineer.GetAsync($"/api/deployment/profiles/{customerId}/controls");
        Assert.Equal(HttpStatusCode.OK, controlsResponse.StatusCode);
        using var controlsJson = System.Text.Json.JsonDocument.Parse(
            await controlsResponse.Content.ReadAsStringAsync());
        Assert.Equal("full-catalog", controlsJson.RootElement.GetProperty("scope").GetString());
        Assert.True(controlsJson.RootElement.GetProperty("count").GetInt32() > 0);

        var checklistUrl = $"/api/deployment/profiles/{customerId}/setup-checklist";
        var checklistResponse = await engineer.GetAsync(checklistUrl);
        Assert.Equal(HttpStatusCode.OK, checklistResponse.StatusCode);
        using var checklistJson = System.Text.Json.JsonDocument.Parse(
            await checklistResponse.Content.ReadAsStringAsync());
        Assert.Equal(4, checklistJson.RootElement.GetProperty("steps").GetArrayLength());
        Assert.All(
            checklistJson.RootElement.GetProperty("steps").EnumerateArray(),
            step => Assert.False(step.GetProperty("isComplete").GetBoolean()));

        var updateChecklistResponse = await engineer.PutAsJsonAsync(
            $"{checklistUrl}/register-application",
            new { isComplete = true });
        Assert.Equal(HttpStatusCode.NoContent, updateChecklistResponse.StatusCode);
        var persistedChecklistResponse = await engineer.GetAsync(checklistUrl);
        using var persistedChecklistJson = System.Text.Json.JsonDocument.Parse(
            await persistedChecklistResponse.Content.ReadAsStringAsync());
        var completedChecklistStep = persistedChecklistJson.RootElement.GetProperty("steps")
            .EnumerateArray()
            .Single(step => step.GetProperty("stepKey").GetString() == "register-application");
        Assert.True(completedChecklistStep.GetProperty("isComplete").GetBoolean());
        Assert.True(completedChecklistStep.GetProperty("updatedUtc").GetDateTime() <= DateTime.UtcNow);
        var invalidChecklistStepResponse = await engineer.PutAsJsonAsync(
            $"{checklistUrl}/tenant-write",
            new { isComplete = true });
        Assert.Equal(HttpStatusCode.BadRequest, invalidChecklistStepResponse.StatusCode);

        var discoveryUrl = $"/api/deployment/profiles/{customerId}/discovery";
        var manualDiscovery = await engineer.GetAsync(discoveryUrl);
        Assert.Equal(HttpStatusCode.OK, manualDiscovery.StatusCode);
        using var manualDiscoveryJson = System.Text.Json.JsonDocument.Parse(await manualDiscovery.Content.ReadAsStringAsync());
        Assert.Equal(474, manualDiscoveryJson.RootElement.GetProperty("totalQuestions").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await engineer.PutAsJsonAsync(
            $"{discoveryUrl}/DEC-P0-001",
            new { status = "Draft", answer = "Manual profile answer", notes = "" })).StatusCode);
        using var manualDiscoveryAfter = System.Text.Json.JsonDocument.Parse(
            await (await engineer.GetAsync(discoveryUrl)).Content.ReadAsStringAsync());
        Assert.Equal(1, manualDiscoveryAfter.RootElement.GetProperty("draftQuestions").GetInt32());

        var deploymentExportResponse = await engineer.GetAsync("/api/exports/workspace.json");
        Assert.Equal(HttpStatusCode.OK, deploymentExportResponse.StatusCode);
        using var deploymentExportJson = System.Text.Json.JsonDocument.Parse(
            await deploymentExportResponse.Content.ReadAsStringAsync());
        Assert.Equal(9, deploymentExportJson.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Manual profile answer", Assert.Single(deploymentExportJson.RootElement
            .GetProperty("securePlusStandards").GetProperty("profileDiscoveryResponses").EnumerateArray())
            .GetProperty("answer").GetString());
        Assert.True(deploymentExportJson.RootElement.GetProperty("deploymentProfiles")
            .EnumerateArray()
            .Single(item => item.GetProperty("customerId").GetGuid() == customerId)
            .GetProperty("setupSteps")
            .EnumerateArray()
            .Single(step => step.GetProperty("stepKey").GetString() == "register-application")
            .GetProperty("isComplete")
            .GetBoolean());

        var invalidEmailResponse = await engineer.PostAsJsonAsync(
            "/api/deployment/profiles",
            new { customerName = "Invalid Email Customer", contactEmail = "not-an-email" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidEmailResponse.StatusCode);
    }

    [Fact]
    public async Task Graph_permission_inventory_is_deployment_only_partial_and_does_not_enable_tenant_access()
    {
        using var sales = CreateClient(DeploymentRoles.Sales, "sales-user");
        using var engineer = CreateClient(DeploymentRoles.Engineer, "deployment-user");
        const string url = "/api/deployment/assessment-permission-mappings";
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync(url)).StatusCode);
        var response = await engineer.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var activeCount = _catalog.Controls.Count(control => control.IsActive);
        Assert.Equal(activeCount, root.GetProperty("activeControlCount").GetInt32());
        Assert.Equal(24, root.GetProperty("partialEvidenceMappingCount").GetInt32());
        Assert.Equal(activeCount - 24, root.GetProperty("unmappedControlCount").GetInt32());
        var mappings = root.GetProperty("mappings").EnumerateArray().ToArray();
        Assert.Equal(
            ["AUTH-001", "AUTH-002", "AUTH-004", "AUTH-005", "AUTH-011", "AUTH-012", "AUTH-014",
             "AUTH-024", "AUTH-025", "AUTH-029", "AUTH-031",
             "CAN-002", "CAN-003", "CAN-004", "CAN-006", "CAN-007", "CAN-009", "CAN-010",
             "CAN-011", "CAN-012", "CAN-013", "CAN-014", "CAN-015", "CAN-016"],
            mappings.Select(item => item.GetProperty("controlId").GetString() ?? "").ToArray());
        Assert.All(mappings, item =>
        {
            Assert.Equal("PartialEvidenceOnly", item.GetProperty("mappingStatus").GetString());
            var id = item.GetProperty("controlId").GetString();
            Assert.Equal(id switch
            {
                "AUTH-001" => "DeviceManagementConfiguration.Read.All",
                "AUTH-011" or "AUTH-012" or "AUTH-029" => "AuditLog.Read.All",
                "AUTH-031" => "Policy.Read.AuthenticationMethod",
                _ => "Policy.Read.All"
            },
                item.GetProperty("delegatedPermission").GetString());
            Assert.Equal(id switch
            {
                "AUTH-001" => "GET /v1.0/deviceManagement/deviceCompliancePolicies",
                "AUTH-011" or "AUTH-012" or "AUTH-029" => "GET /v1.0/reports/authenticationMethods/userRegistrationDetails",
                "AUTH-031" => "GET /v1.0/policies/authenticationMethodsPolicy",
                "CAN-016" => "GET /v1.0/identity/conditionalAccess/namedLocations",
                _ => "GET /v1.0/identity/conditionalAccess/policies"
            }, item.GetProperty("endpoint").GetString());
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("limitations").GetString()));
        });
        var unmapped = root.GetProperty("unmappedControls").EnumerateArray().ToArray();
        Assert.Equal(activeCount - 24, unmapped.Length);
        var reportedIds = mappings.Select(item => item.GetProperty("controlId").GetString())
            .Concat(unmapped.Select(item => item.GetProperty("controlId").GetString())).ToArray();
        Assert.Equal(activeCount, reportedIds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(_catalog.Controls.Where(control => control.IsActive),
            control => Assert.Contains(control.ControlId, reportedIds));
        Assert.False(root.GetProperty("liveAssessmentEnabled").GetBoolean());
        Assert.False(root.GetProperty("tenantChangesEnabled").GetBoolean());
    }

    [Fact]
    public async Task Fleet_shows_latest_completed_assessment_and_profiles_without_one()
    {
        var (customerId, assessmentId, _) = await SeedCompletedAssessmentAsync();
        Guid unassessedId;
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var existing = await db.AssessmentRuns.SingleAsync(run => run.Id == assessmentId);
            existing.CompletedUtc = DateTime.UtcNow.AddDays(-2);
            existing.NotAssessedControls = 10;
            existing.TotalControls = 11;
            var otherCustomer = new Customer
            {
                Id = Guid.NewGuid(),
                Name = "Unassessed customer",
                NormalizedName = "UNASSESSED CUSTOMER",
                CreatedUtc = DateTime.UtcNow,
                DeploymentProfile = new DeploymentProfile
                {
                    Id = Guid.NewGuid(),
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                }
            };
            unassessedId = otherCustomer.Id;
            db.Customers.Add(otherCustomer);
            db.AssessmentRuns.AddRange(
                new AssessmentRun
                {
                    Id = Guid.NewGuid(), CustomerId = customerId, Name = "Newer completed",
                    Assessor = "Tester", Source = "Manual", CatalogVersion = "2.0",
                    StartedUtc = DateTime.UtcNow.AddDays(-1), CompletedUtc = DateTime.UtcNow.AddDays(-1),
                    IsComplete = true, ReadinessRating = "Critical", OverallPercentage = 20,
                    TotalControls = 11, NotAssessedControls = 8
                },
                new AssessmentRun
                {
                    Id = Guid.NewGuid(), CustomerId = customerId, Name = "Newer draft",
                    Assessor = "Tester", Source = "Manual", CatalogVersion = "2.0",
                    StartedUtc = DateTime.UtcNow, IsComplete = false, ReadinessRating = "AIReady",
                    OverallPercentage = 100
                });
            await db.SaveChangesAsync();
        }

        using var sales = CreateClient(DeploymentRoles.Sales, "sales-user");
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync("/api/deployment/fleet")).StatusCode);
        using var engineer = CreateClient(DeploymentRoles.Engineer, "deployment-user");
        var response = await engineer.GetAsync("/api/deployment/fleet");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(2, root.GetProperty("customerCount").GetInt32());
        Assert.Equal(1, root.GetProperty("withCompletedAssessment").GetInt32());
        Assert.Equal(1, root.GetProperty("withoutCompletedAssessment").GetInt32());
        var customers = root.GetProperty("customers").EnumerateArray().ToArray();
        var assessed = customers.Single(item => item.GetProperty("customerId").GetGuid() == customerId);
        Assert.Equal("Newer completed", assessed.GetProperty("latestCompletedAssessment").GetProperty("name").GetString());
        Assert.Equal(8, assessed.GetProperty("latestCompletedAssessment").GetProperty("notAssessedControls").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, customers.Single(item =>
            item.GetProperty("customerId").GetGuid() == unassessedId).GetProperty("latestCompletedAssessment").ValueKind);
        Assert.False(root.GetProperty("tenantChangesEnabled").GetBoolean());
    }

    [Fact]
    public async Task Assessment_comparison_requires_matching_completed_customer_snapshots()
    {
        var (customerId, baselineId, controlId) = await SeedCompletedAssessmentAsync();
        var targetId = Guid.NewGuid();
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AssessmentRuns.Add(new AssessmentRun
            {
                Id = targetId,
                CustomerId = customerId,
                Name = "Later snapshot",
                Assessor = "Test operator",
                Source = "Manual",
                CatalogVersion = "2.0",
                StartedUtc = DateTime.UtcNow,
                CompletedUtc = DateTime.UtcNow,
                IsComplete = true,
                ReadinessRating = "Critical",
                OverallPercentage = 50,
                Results = [new AssessmentResult
                {
                    ControlId = controlId,
                    Status = "Implemented",
                    Assessor = "Test operator",
                    ReviewedUtc = DateTime.UtcNow
                }]
            });
            await db.SaveChangesAsync();
        }

        var url = $"/api/deployment/profiles/{customerId}/assessment-comparison?baselineId={baselineId}&targetId={targetId}";
        using var engineer = CreateClient(DeploymentRoles.Engineer, "deployment-user");
        using var sales = CreateClient(DeploymentRoles.Sales, "sales-user");
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync(url)).StatusCode);
        var response = await engineer.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(50, json.RootElement.GetProperty("scoreDifference").GetDouble());
        Assert.Equal(1, json.RootElement.GetProperty("controlCount").GetInt32());
        var change = Assert.Single(json.RootElement.GetProperty("changes").EnumerateArray());
        Assert.Equal("NotImplemented", change.GetProperty("baselineStatus").GetString());
        Assert.Equal("Implemented", change.GetProperty("targetStatus").GetString());
        Assert.False(json.RootElement.GetProperty("tenantChangesEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await engineer.GetAsync(
            $"/api/deployment/profiles/{customerId}/assessment-comparison?baselineId={baselineId}&targetId={baselineId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await engineer.GetAsync(
            $"/api/deployment/profiles/{Guid.NewGuid()}/assessment-comparison?baselineId={baselineId}&targetId={targetId}")).StatusCode);

        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var target = await db.AssessmentRuns.Include(run => run.Results).SingleAsync(run => run.Id == targetId);
            target.IsComplete = false;
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Conflict, (await engineer.GetAsync(url)).StatusCode);
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var target = await db.AssessmentRuns.Include(run => run.Results).SingleAsync(run => run.Id == targetId);
            target.IsComplete = true;
            target.CatalogVersion = "other";
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Conflict, (await engineer.GetAsync(url)).StatusCode);
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var target = await db.AssessmentRuns.Include(run => run.Results).SingleAsync(run => run.Id == targetId);
            target.CatalogVersion = "2.0";
            target.Results.Single().ControlId = "DIFFERENT-001";
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Conflict, (await engineer.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Offline_policy_preview_requires_authorization_and_never_updates_assessments()
    {
        var (customerId, assessmentId, _) = await SeedCompletedAssessmentAsync();
        using var engineer = CreateClient(DeploymentRoles.Engineer, "deployment-user");
        using var sales = CreateClient(DeploymentRoles.Sales, "sales-user");
        var url = $"/api/deployment/profiles/{customerId}/conditional-access-evidence/preview";
        const string data = """
            {"value":[{"displayName":"Synthetic policy","state":"enabled",
              "grantControls":{"builtInControls":["mfa"],"operator":"OR"},
              "conditions":{"clientAppTypes":["browser"],"users":{"includeUsers":["All"],"excludeUsers":["secret-user-id"],"excludeGroups":[]},
              "applications":{"includeApplications":["All"],"excludeApplications":["app-id"]}}}]}
            """;
        static StringContent Json(string text) => new(text, System.Text.Encoding.UTF8, "application/json");

        Assert.Equal(HttpStatusCode.Forbidden, (await sales.PostAsync(url, Json(data))).StatusCode);
        var response = await engineer.PostAsync(url, Json(data));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, result.RootElement.GetProperty("enabledMfaPolicies").GetInt32());
        Assert.Equal("NotAssessed", result.RootElement.GetProperty("assessmentStatus").GetString());
        var policy = Assert.Single(result.RootElement.GetProperty("policies").EnumerateArray());
        Assert.Equal("Synthetic policy", policy.GetProperty("name").GetString());
        Assert.True(policy.GetProperty("includesAllUsers").GetBoolean());
        Assert.Equal(1, policy.GetProperty("excludedUsers").GetInt32());
        Assert.Equal(0, policy.GetProperty("excludedGroups").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, policy.GetProperty("excludedRoles").ValueKind);
        Assert.Equal(1, policy.GetProperty("excludedApplications").GetInt32());
        Assert.DoesNotContain("secret-user-id", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await engineer.PostAsync(url,
            Json("""{"value":[{"state":"enabled","conditions":{"users":{"excludeUsers":[1]}}}]}"""))).StatusCode);
        Assert.Contains("cannot be verified", result.RootElement.GetProperty("limitations").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await engineer.PostAsync(url, Json("{"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await engineer.PostAsync(url,
            Json("""{"value":[],"@odata.nextLink":"https://graph.microsoft.com/example"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
            (await engineer.PostAsync(url, Json(new string(' ', 2 * 1024 * 1024 + 1)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await engineer.PostAsync(
            $"/api/deployment/profiles/{Guid.NewGuid()}/conditional-access-evidence/preview", Json(data))).StatusCode);
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.AssessmentRuns.Include(item => item.Results).SingleAsync(item => item.Id == assessmentId);
        Assert.True(run.IsComplete);
        Assert.Equal("NotImplemented", Assert.Single(run.Results).Status);
        Assert.Equal(1, await db.AssessmentRuns.CountAsync());
    }

    [Fact]
    public async Task Assessment_report_is_deployment_only_and_escapes_recorded_evidence()
    {
        var (customerId, assessmentId, _) = await SeedCompletedAssessmentAsync();
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var customer = await db.Customers.SingleAsync(item => item.Id == customerId);
            customer.Name = "<script>alert('customer')</script>";
            var result = await db.AssessmentResults.SingleAsync(item => item.AssessmentRunId == assessmentId);
            result.Evidence = "<img src=x onerror=alert(1)>";
            await db.SaveChangesAsync();
        }

        using var sales = CreateClient(DeploymentRoles.Sales, "sales-user");
        var salesResponse = await sales.GetAsync($"/api/deployment/assessments/{assessmentId}/report.html");
        Assert.Equal(HttpStatusCode.Forbidden, salesResponse.StatusCode);

        using var engineer = CreateClient(DeploymentRoles.Engineer, "deployment-user");
        var response = await engineer.GetAsync($"/api/deployment/assessments/{assessmentId}/report.html");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        var report = await response.Content.ReadAsStringAsync();
        Assert.Contains("Completed snapshot", report);
        Assert.Contains("Not assessed controls are not confirmed gaps", report);
        Assert.Contains("&lt;script&gt;", report);
        Assert.Contains("&lt;img", report);
        Assert.DoesNotContain("<script>", report);
        Assert.DoesNotContain("<img", report);
        Assert.Equal(HttpStatusCode.NotFound,
            (await engineer.GetAsync($"/api/deployment/assessments/{Guid.NewGuid()}/report.html")).StatusCode);
    }

    [Fact]
    public async Task Design_review_requires_unambiguous_identity_and_verifiable_original_requester()
    {
        var (customerId, assessmentId, controlId) = await SeedCompletedAssessmentAsync();
        using var missing = CreateClient(DeploymentRoles.Engineer, "test");
        missing.DefaultRequestHeaders.Add("X-Test-Missing-Identity", "true");
        var create = new { assessmentRunId = assessmentId, title = "Identity review", controlIds = new[] { controlId } };
        Assert.Equal(HttpStatusCode.Forbidden,
            (await missing.PostAsJsonAsync($"/api/deployment/profiles/{customerId}/change-plans", create)).StatusCode);
        using var ambiguous = CreateClient(DeploymentRoles.Engineer, "requester");
        ambiguous.DefaultRequestHeaders.Add("X-Test-Subject", "different-subject");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await ambiguous.PostAsJsonAsync($"/api/deployment/profiles/{customerId}/change-plans", create)).StatusCode);
        using var subOnly = CreateClient(DeploymentRoles.Engineer, "display-name");
        subOnly.DefaultRequestHeaders.Add("X-Test-Missing-Identity", "true");
        subOnly.DefaultRequestHeaders.Add("X-Test-Subject", "stable-subject");
        Assert.Equal(HttpStatusCode.Created,
            (await subOnly.PostAsJsonAsync($"/api/deployment/profiles/{customerId}/change-plans", create)).StatusCode);
        using var author = CreateClient(DeploymentRoles.Engineer, "requester");
        var response = await author.PostAsJsonAsync($"/api/deployment/profiles/{customerId}/change-plans", create);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var plan = await response.Content.ReadFromJsonAsync<PlanResponse>();
        Assert.NotNull(plan);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await missing.PostAsync($"/api/deployment/change-plans/{plan.Id}/submit", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await author.PostAsync($"/api/deployment/change-plans/{plan.Id}/submit", null)).StatusCode);
        using var noReviewer = CreateClient(DeploymentRoles.Approver, "test");
        noReviewer.DefaultRequestHeaders.Add("X-Test-Missing-Identity", "true");
        var decision = new { decision = "Accept", notes = "Review" };
        Assert.Equal(HttpStatusCode.Forbidden,
            (await noReviewer.PostAsJsonAsync($"/api/deployment/change-plans/{plan.Id}/decision", decision)).StatusCode);

        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await db.DeploymentChangePlans.SingleAsync(p => p.Id == plan.Id);
            stored.RequestedById = "unknown";
            await db.SaveChangesAsync();
        }
        using var reviewer = CreateClient(DeploymentRoles.Approver, "reviewer");
        Assert.Equal(HttpStatusCode.Conflict,
            (await reviewer.PostAsJsonAsync($"/api/deployment/change-plans/{plan.Id}/decision", decision)).StatusCode);
        await using var verifyScope = _app.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var final = await verifyDb.DeploymentChangePlans.Include(p => p.Events).SingleAsync(p => p.Id == plan.Id);
        Assert.Equal("PendingReview", final.Status);
        Assert.Equal(2, final.Events.Count);
    }

    [Fact]
    public async Task Competing_submissions_produce_only_one_submitted_audit_event()
    {
        var (customerId, assessmentId, controlId) = await SeedCompletedAssessmentAsync();
        using var author = CreateClient(DeploymentRoles.Engineer, "requester");
        var response = await author.PostAsJsonAsync($"/api/deployment/profiles/{customerId}/change-plans",
            new { assessmentRunId = assessmentId, title = "Concurrent submission", controlIds = new[] { controlId } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var plan = await response.Content.ReadFromJsonAsync<PlanResponse>();
        Assert.NotNull(plan);

        await using var firstScope = _app.Services.CreateAsyncScope();
        await using var secondScope = _app.Services.CreateAsyncScope();
        var firstDb = firstScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var secondDb = secondScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var first = await firstDb.DeploymentChangePlans.Include(p => p.Events).SingleAsync(p => p.Id == plan.Id);
        var second = await secondDb.DeploymentChangePlans.Include(p => p.Events).SingleAsync(p => p.Id == plan.Id);
        first.Status = second.Status = "PendingReview";
        first.Events.Add(new DeploymentChangePlanEvent { EventType = "Submitted", ActorId = "requester", Actor = "requester", OccurredUtc = DateTime.UtcNow });
        second.Events.Add(new DeploymentChangePlanEvent { EventType = "Submitted", ActorId = "requester", Actor = "requester", OccurredUtc = DateTime.UtcNow });
        await firstDb.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondDb.SaveChangesAsync());

        await using var verifyScope = _app.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var final = await verifyDb.DeploymentChangePlans.Include(p => p.Events).SingleAsync(p => p.Id == plan.Id);
        Assert.Equal("PendingReview", final.Status);
        Assert.Single(final.Events, e => e.EventType == "Submitted");
        Assert.Equal(HttpStatusCode.Conflict, (await author.PostAsync($"/api/deployment/change-plans/{plan.Id}/submit", null)).StatusCode);
    }

    [Fact]
    public async Task Competing_plan_reviews_do_not_overwrite_decision_or_add_losing_audit_event()
    {
        var (customerId, assessmentId, controlId) = await SeedCompletedAssessmentAsync();
        using var author = CreateClient(DeploymentRoles.Engineer, "requester");
        var createdResponse = await author.PostAsJsonAsync($"/api/deployment/profiles/{customerId}/change-plans",
            new { assessmentRunId = assessmentId, title = "Concurrent review", controlIds = new[] { controlId } });
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var plan = await createdResponse.Content.ReadFromJsonAsync<PlanResponse>();
        Assert.NotNull(plan);
        Assert.Equal(HttpStatusCode.OK, (await author.PostAsync($"/api/deployment/change-plans/{plan.Id}/submit", null)).StatusCode);

        await using var winnerScope = _app.Services.CreateAsyncScope();
        await using var loserScope = _app.Services.CreateAsyncScope();
        var winnerDb = winnerScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var loserDb = loserScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var winner = await winnerDb.DeploymentChangePlans.Include(p => p.Events).SingleAsync(p => p.Id == plan.Id);
        var loser = await loserDb.DeploymentChangePlans.Include(p => p.Events).SingleAsync(p => p.Id == plan.Id);
        winner.Status = "Accepted";
        winner.ReviewDecision = "Accept";
        winner.Events.Add(new DeploymentChangePlanEvent { EventType = "Accepted", ActorId = "reviewer-a", Actor = "reviewer-a", OccurredUtc = DateTime.UtcNow });
        loser.Status = "Rejected";
        loser.ReviewDecision = "Reject";
        loser.Events.Add(new DeploymentChangePlanEvent { EventType = "Rejected", ActorId = "reviewer-b", Actor = "reviewer-b", OccurredUtc = DateTime.UtcNow });
        await winnerDb.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => loserDb.SaveChangesAsync());

        await using var verifyScope = _app.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var final = await verifyDb.DeploymentChangePlans.Include(p => p.Events).SingleAsync(p => p.Id == plan.Id);
        Assert.Equal("Accepted", final.Status);
        Assert.Equal("Accept", final.ReviewDecision);
        Assert.Single(final.Events, e => e.EventType == "Accepted");
        Assert.DoesNotContain(final.Events, e => e.EventType == "Rejected");
        using var approver = CreateClient(DeploymentRoles.Approver, "reviewer-b");
        Assert.Equal(HttpStatusCode.Conflict,
            (await approver.PostAsJsonAsync($"/api/deployment/change-plans/{plan.Id}/decision",
                new { decision = "Reject", notes = "Second review" })).StatusCode);
    }

    [Fact]
    public async Task New_assessment_can_start_unassessed_without_copying_profile_findings()
    {
        var (customerId, _, controlId) = await SeedCompletedAssessmentAsync();
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profile = await db.DeploymentProfiles.SingleAsync(p => p.CustomerId == customerId);
            db.DeploymentFindings.Add(new DeploymentFinding
            {
                DeploymentProfileId = profile.Id, ControlId = controlId, Status = "Implemented",
                Evidence = "Earlier manual review", ReviewedBy = "Earlier operator", UpdatedUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        using var engineer = CreateClient(DeploymentRoles.Engineer, "engineer");
        var url = $"/api/deployment/profiles/{customerId}/assessments";
        var freshResponse = await engineer.PostAsJsonAsync(url, new { startUnassessed = true });
        Assert.Equal(HttpStatusCode.Created, freshResponse.StatusCode);
        using var fresh = System.Text.Json.JsonDocument.Parse(await freshResponse.Content.ReadAsStringAsync());
        var freshControls = fresh.RootElement.GetProperty("controls").EnumerateArray().ToArray();
        Assert.Equal(_catalog.Controls.Count(c => c.IsActive), freshControls.Length);
        Assert.All(freshControls, item => Assert.Equal("NotAssessed", item.GetProperty("status").GetString()));
        var freshItem = freshControls.Single(item => item.GetProperty("controlId").GetString() == controlId);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, freshItem.GetProperty("evidence").ValueKind);

        var copyResponse = await engineer.PostAsJsonAsync(url, new { });
        Assert.Equal(HttpStatusCode.Created, copyResponse.StatusCode);
        using var copy = System.Text.Json.JsonDocument.Parse(await copyResponse.Content.ReadAsStringAsync());
        var copied = copy.RootElement.GetProperty("controls").EnumerateArray()
            .Single(item => item.GetProperty("controlId").GetString() == controlId);
        Assert.Equal("Implemented", copied.GetProperty("status").GetString());
        Assert.Equal("Earlier manual review", copied.GetProperty("evidence").GetString());
    }

    [Fact]
    public async Task Discovery_crosswalk_keeps_exact_candidate_unknown_and_unresolved_distinct()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var decision = await db.SecurePlusDecisions.AsNoTracking().FirstAsync();
        decision.BaselineReferences = "GBL-P0-PKO-001;GBL-P0-T1-PKO-001;GBL-P0-MISSING-999";
        var settings = await db.SecurePlusBaselineSettings.AsNoTracking().Take(2).ToListAsync();
        settings[0].BaselineReference = "GBL-P0-T1-PKO-001";
        settings[0].Control = _catalog.Controls.First(control => control.IsActive).ControlId;
        settings[1].BaselineReference = "GBL-P0-T2-PKO-001";
        settings[1].Control = "NO-SUCH-CONTROL";

        var result = DiscoveryCrosswalk.Build([decision], settings, _catalog.Controls);
        Assert.Single(result.UnresolvedReferences);
        Assert.Equal(3, result.Links.Count);
        Assert.Single(result.Links, link => link.MatchType == "ExactReference" && link.AssessmentControlExists);
        Assert.Single(result.Links, link => link.MatchType == "FormatCandidate" && link.AssessmentControlExists);
        Assert.Single(result.Links, link => link.MatchType == "FormatCandidate" && !link.AssessmentControlExists);
    }

    [Fact]
    public async Task Discovery_crosswalk_reports_candidates_without_assessing_controls()
    {
        using var engineer = CreateClient(DeploymentRoles.Engineer, "engineer");
        using var sales = CreateClient(DeploymentRoles.Sales, "sales");
        const string url = "/api/deployment/standards/secure-plus/assessment-crosswalk";
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync(url)).StatusCode);
        var response = await engineer.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(474, root.GetProperty("decisionCount").GetInt32());
        Assert.True(root.GetProperty("decisionsWithCandidates").GetInt32() > 0);
        Assert.Contains("require human review", root.GetProperty("notice").GetString());
        Assert.Contains(root.GetProperty("links").EnumerateArray(),
            link => link.GetProperty("matchType").GetString() == "FormatCandidate");
        Assert.All(root.GetProperty("links").EnumerateArray(), link =>
            Assert.False(link.TryGetProperty("status", out _)));
    }

    [Fact]
    public async Task Assessment_plan_requires_independent_review_and_exports_audit_history()
    {
        var (customerId, assessmentId, controlId) = await SeedCompletedAssessmentAsync();
        using var engineer = CreateClient(
            $"{DeploymentRoles.Engineer},{DeploymentRoles.Approver}",
            "requester");
        using var approver = CreateClient(DeploymentRoles.Approver, "reviewer");

        var createResponse = await engineer.PostAsJsonAsync(
            $"/api/deployment/profiles/{customerId}/change-plans",
            new
            {
                assessmentRunId = assessmentId,
                title = "Synthetic deployment review",
                notes = "<script>Test-only plan</script>",
                controlIds = new[] { controlId }
            });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<PlanResponse>();
        Assert.NotNull(created);
        Assert.Equal("Draft", created.Status);
        Assert.False(created.ExecutionEnabled);
        Assert.Equal(["DraftCreated"], created.Events.Select(item => item.EventType));

        var previewResponse = await engineer.GetAsync($"/api/deployment/change-plans/{created.Id}/preview.html");
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        Assert.Equal("attachment", previewResponse.Content.Headers.ContentDisposition?.DispositionType);
        var preview = await previewResponse.Content.ReadAsStringAsync();
        Assert.Contains("not an executable script", preview);
        Assert.Contains("Assessment catalog version:", preview);
        Assert.Contains(controlId, preview);
        Assert.Contains("&lt;script&gt;Test-only plan&lt;/script&gt;", preview);
        Assert.DoesNotContain("<script>", preview);
        Assert.Equal(HttpStatusCode.NotFound,
            (await engineer.GetAsync($"/api/deployment/change-plans/{Guid.NewGuid()}/preview.html")).StatusCode);
        using (var salesPreview = CreateClient(DeploymentRoles.Sales, "sales-user"))
        {
            Assert.Equal(HttpStatusCode.Forbidden,
                (await salesPreview.GetAsync($"/api/deployment/change-plans/{created.Id}/preview.html")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await salesPreview.GetAsync($"/api/deployment/change-plans/{created.Id}/draft.txt")).StatusCode);
        }
        var draftResponse = await engineer.GetAsync($"/api/deployment/change-plans/{created.Id}/draft.txt");
        Assert.Equal(HttpStatusCode.OK, draftResponse.StatusCode);
        Assert.Equal("text/plain", draftResponse.Content.Headers.ContentType?.MediaType);
        var draft = await draftResponse.Content.ReadAsStringAsync();
        Assert.Contains(controlId, draft);
        Assert.Contains("Not approved; manual review required", draft);
        Assert.All(draft.Split('\n', StringSplitOptions.RemoveEmptyEntries), line => Assert.StartsWith("# ", line));

        var submitResponse = await engineer.PostAsync(
            $"/api/deployment/change-plans/{created.Id}/submit",
            content: null);
        Assert.Equal(HttpStatusCode.OK, submitResponse.StatusCode);

        var selfApprovalResponse = await engineer.PostAsJsonAsync(
            $"/api/deployment/change-plans/{created.Id}/decision",
            new { decision = "Accept", notes = "Self-review must be rejected." });
        Assert.Equal(HttpStatusCode.Forbidden, selfApprovalResponse.StatusCode);

        var approvalResponse = await approver.PostAsJsonAsync(
            $"/api/deployment/change-plans/{created.Id}/decision",
            new { decision = "Accept", notes = "Independent design review only." });
        Assert.Equal(HttpStatusCode.OK, approvalResponse.StatusCode);
        var approved = await approvalResponse.Content.ReadFromJsonAsync<PlanResponse>();
        Assert.NotNull(approved);
        Assert.Equal("Accepted", approved.Status);
        Assert.False(approved.ExecutionEnabled);
        Assert.Equal(["DraftCreated", "Submitted", "Accepted"], approved.Events.Select(item => item.EventType));
        Assert.Equal("reviewer", approved.Events[^1].ActorId);
        var reviewedPreview = await approver.GetStringAsync($"/api/deployment/change-plans/{created.Id}/preview.html");
        Assert.Contains("Independent design review only.", reviewedPreview);

        using var sales = CreateClient(DeploymentRoles.Sales, "sales-user");
        var exportResponse = await sales.GetAsync("/api/exports/workspace.json");
        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);
        var salesExportText = await exportResponse.Content.ReadAsStringAsync();
        using var salesExport = System.Text.Json.JsonDocument.Parse(salesExportText);
        Assert.Equal(6, salesExport.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.False(salesExport.RootElement.TryGetProperty("deploymentProfiles", out _));
        Assert.False(salesExport.RootElement.TryGetProperty("deploymentCatalog", out _));
        Assert.False(salesExport.RootElement.TryGetProperty("assessments", out _));
        Assert.False(salesExport.RootElement.TryGetProperty("changePlans", out _));
        Assert.False(salesExport.RootElement.TryGetProperty("securePlusStandards", out _));
        Assert.DoesNotContain("sourceInventory", salesExportText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Synthetic deployment review", salesExportText, StringComparison.Ordinal);

        using var deployment = CreateClient(DeploymentRoles.Engineer, "deployment-user");
        var deploymentExportResponse = await deployment.GetAsync("/api/exports/workspace.json");
        Assert.Equal(HttpStatusCode.OK, deploymentExportResponse.StatusCode);
        var deploymentExportText = await deploymentExportResponse.Content.ReadAsStringAsync();
        using var deploymentExportJson = System.Text.Json.JsonDocument.Parse(deploymentExportText);
        Assert.Equal(9, deploymentExportJson.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            474,
            deploymentExportJson.RootElement.GetProperty("securePlusStandards")
                .GetProperty("discoveryDecisions").GetArrayLength());
        Assert.Equal(
            149,
            deploymentExportJson.RootElement.GetProperty("securePlusStandards")
                .GetProperty("targetModelReference").GetArrayLength());
        Assert.Equal(
            36,
            deploymentExportJson.RootElement.GetProperty("securePlusStandards")
                .GetProperty("sourceInventory").GetProperty("sources").GetArrayLength());
        var export = System.Text.Json.JsonSerializer.Deserialize<WorkspaceExport>(
            deploymentExportText,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.NotNull(export);
        Assert.Equal(9, export.SchemaVersion);
        var exportedPlan = Assert.Single(export.ChangePlans);
        Assert.Equal(approved.Id, exportedPlan.Id);
        Assert.Equal(3, exportedPlan.Events.Count);
    }

    [Fact]
    public async Task Full_discovery_is_deployment_only_and_saves_responses_per_engagement()
    {
        var (customerId, _, _) = await SeedCompletedAssessmentAsync();
        Guid engagementId;
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var customer = await db.Customers.SingleAsync(item => item.Id == customerId);
            var engagement = new Engagement
            {
                Id = Guid.NewGuid(),
                Customer = customer,
                Salesperson = "Synthetic deployment engagement",
                PlannerHandoffStatus = "NotConfigured",
                CreatedUtc = DateTime.UtcNow
            };
            db.Engagements.Add(engagement);
            await db.SaveChangesAsync();
            engagementId = engagement.Id;
        }

        using var sales = CreateClient(DeploymentRoles.Sales, "sales-user");
        using var engineer = CreateClient(DeploymentRoles.Engineer, "deployment-user");
        var salesResponse = await sales.GetAsync(
            $"/api/deployment/engagements/{engagementId}/discovery");
        Assert.Equal(HttpStatusCode.Forbidden, salesResponse.StatusCode);

        var discoveryResponse = await engineer.GetAsync(
            $"/api/deployment/engagements/{engagementId}/discovery");
        Assert.Equal(HttpStatusCode.OK, discoveryResponse.StatusCode);
        using var initial = System.Text.Json.JsonDocument.Parse(
            await discoveryResponse.Content.ReadAsStringAsync());
        Assert.Equal(474, initial.RootElement.GetProperty("totalQuestions").GetInt32());
        Assert.False(initial.RootElement.GetProperty("tenantChangesEnabled").GetBoolean());
        Assert.Equal(
            "WIP - validate before treating as an approved standard",
            initial.RootElement.GetProperty("sourceStatus").GetString());
        var firstQuestion = initial.RootElement.GetProperty("questions").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "DEC-P0-001");
        Assert.Equal("NotStarted", firstQuestion.GetProperty("status").GetString());

        var invalidDeferred = await engineer.PutAsJsonAsync(
            $"/api/deployment/engagements/{engagementId}/discovery/DEC-P0-001",
            new { status = "Deferred", answer = (string?)null, notes = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, invalidDeferred.StatusCode);

        var saveResponse = await engineer.PutAsJsonAsync(
            $"/api/deployment/engagements/{engagementId}/discovery/DEC-P0-001",
            new { status = "Resolved", answer = "Prioritize identity and endpoint security.", notes = "Confirmed in discovery workshop." });
        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);

        var updatedResponse = await engineer.GetAsync(
            $"/api/deployment/engagements/{engagementId}/discovery");
        using var updated = System.Text.Json.JsonDocument.Parse(
            await updatedResponse.Content.ReadAsStringAsync());
        Assert.Equal(1, updated.RootElement.GetProperty("resolvedQuestions").GetInt32());
        var resolved = updated.RootElement.GetProperty("questions").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "DEC-P0-001");
        Assert.Equal("Resolved", resolved.GetProperty("status").GetString());
        Assert.Equal(
            "Prioritize identity and endpoint security.",
            resolved.GetProperty("answer").GetString());

        var profileUrl = $"/api/deployment/profiles/{customerId}/discovery";
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync(profileUrl)).StatusCode);
        using var approver = CreateClient(DeploymentRoles.Approver, "approver-user");
        Assert.Equal(HttpStatusCode.OK, (await approver.GetAsync(profileUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await approver.PutAsJsonAsync(
            $"{profileUrl}/DEC-P0-001", new { status = "Draft", answer = "No", notes = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await engineer.GetAsync(
            $"/api/deployment/profiles/{Guid.NewGuid()}/discovery")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await engineer.PutAsJsonAsync(
            $"{profileUrl}/DEC-P0-001", new { status = "Resolved", answer = "", notes = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await engineer.PutAsJsonAsync(
            $"{profileUrl}/UNKNOWN", new { status = "Draft", answer = "", notes = "" })).StatusCode);

        using var profileInitial = System.Text.Json.JsonDocument.Parse(
            await (await engineer.GetAsync(profileUrl)).Content.ReadAsStringAsync());
        Assert.Equal(474, profileInitial.RootElement.GetProperty("totalQuestions").GetInt32());
        Assert.Equal(0, profileInitial.RootElement.GetProperty("resolvedQuestions").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await engineer.PutAsJsonAsync(
            $"{profileUrl}/DEC-P0-001",
            new { status = "Resolved", answer = "Profile answer", notes = "Independent log" })).StatusCode);
        using var profileUpdated = System.Text.Json.JsonDocument.Parse(
            await (await engineer.GetAsync(profileUrl)).Content.ReadAsStringAsync());
        Assert.Equal(customerId, profileUpdated.RootElement.GetProperty("customerId").GetGuid());
        Assert.Equal(1, profileUpdated.RootElement.GetProperty("resolvedQuestions").GetInt32());
        Assert.Equal("Profile answer", profileUpdated.RootElement.GetProperty("questions").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "DEC-P0-001").GetProperty("answer").GetString());
        using var engagementAfter = System.Text.Json.JsonDocument.Parse(
            await (await engineer.GetAsync($"/api/deployment/engagements/{engagementId}/discovery")).Content.ReadAsStringAsync());
        Assert.Equal("Prioritize identity and endpoint security.", engagementAfter.RootElement.GetProperty("questions").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "DEC-P0-001").GetProperty("answer").GetString());
        using var export = System.Text.Json.JsonDocument.Parse(
            await (await engineer.GetAsync("/api/exports/workspace.json")).Content.ReadAsStringAsync());
        Assert.Equal("Profile answer", Assert.Single(export.RootElement.GetProperty("securePlusStandards")
            .GetProperty("profileDiscoveryResponses").EnumerateArray()).GetProperty("answer").GetString());
        var (otherCustomerId, _, _) = await SeedCompletedAssessmentAsync();
        using var otherProfile = System.Text.Json.JsonDocument.Parse(await (await engineer.GetAsync(
            $"/api/deployment/profiles/{otherCustomerId}/discovery")).Content.ReadAsStringAsync());
        Assert.Equal(0, otherProfile.RootElement.GetProperty("resolvedQuestions").GetInt32());
    }

    [Fact]
    public async Task Standards_catalog_exposes_versioned_work_and_model_references_without_enabling_execution()
    {
        using var engineer = CreateClient(DeploymentRoles.Engineer, "deployment-user");
        var summaryResponse = await engineer.GetAsync("/api/deployment/standards/secure-plus/summary");
        Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
        using var summary = System.Text.Json.JsonDocument.Parse(
            await summaryResponse.Content.ReadAsStringAsync());
        var root = summary.RootElement;
        Assert.Equal(474, root.GetProperty("discoveryQuestionCount").GetInt32());
        Assert.Equal(193, root.GetProperty("workItemCount").GetInt32());
        Assert.Equal(532, root.GetProperty("baselineSettingCount").GetInt32());
        Assert.Equal(36, root.GetProperty("sourceCount").GetInt32());
        Assert.NotEmpty(root.GetProperty("sourceAreas").EnumerateArray());
        Assert.NotEmpty(root.GetProperty("sourceStatuses").EnumerateArray());
        Assert.Equal(149, root.GetProperty("targetModelTableCount").GetInt32());
        Assert.Equal(1206, root.GetProperty("targetModelColumnCount").GetInt32());
        var licenseFamilies = root.GetProperty("licenseFamilies").EnumerateArray().ToArray();
        Assert.NotEmpty(licenseFamilies);
        Assert.Equal(532, licenseFamilies.Sum(item => item.GetProperty("count").GetInt32()));
        Assert.False(root.GetProperty("tenantChangesEnabled").GetBoolean());
        Assert.True(root.GetProperty("modelReferenceOnly").GetBoolean());

        var sourceResponse = await engineer.GetAsync(
            "/api/deployment/standards/sources?status=Restricted%20pending%20rights%20review");
        Assert.Equal(HttpStatusCode.OK, sourceResponse.StatusCode);
        using var sourceInventory = System.Text.Json.JsonDocument.Parse(
            await sourceResponse.Content.ReadAsStringAsync());
        var restrictedSources = sourceInventory.RootElement.GetProperty("sources").EnumerateArray().ToArray();
        Assert.Equal(3, restrictedSources.Length);
        Assert.All(restrictedSources, item =>
            Assert.Contains("rights", item.GetProperty("usageBoundary").GetString(), StringComparison.OrdinalIgnoreCase));

        var modelResponse = await engineer.GetAsync(
            "/api/deployment/standards/target-model?search=Engagement");
        Assert.Equal(HttpStatusCode.OK, modelResponse.StatusCode);
        using var model = System.Text.Json.JsonDocument.Parse(
            await modelResponse.Content.ReadAsStringAsync());
        Assert.True(model.RootElement.GetProperty("modelReferenceOnly").GetBoolean());
        Assert.True(model.RootElement.GetProperty("count").GetInt32() > 0);

        using var sales = CreateClient(DeploymentRoles.Sales, "sales-user");
        var salesCatalogResponse = await sales.GetAsync("/api/deployment/standards/secure-plus/summary");
        Assert.Equal(HttpStatusCode.Forbidden, salesCatalogResponse.StatusCode);
    }

    private HttpClient CreateClient(string role, string userId)
    {
        var client = _app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", role);
        client.DefaultRequestHeaders.Add("X-Test-User", userId);
        return client;
    }

    private async Task<(Guid CustomerId, Guid AssessmentId, string ControlId)> SeedCompletedAssessmentAsync()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var firstControl = _catalog.Controls.First(control => control.IsActive);
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Name = "Synthetic Workflow Customer",
            NormalizedName = Guid.NewGuid().ToString("N"),
            CreatedUtc = DateTime.UtcNow,
            DeploymentProfile = new DeploymentProfile
            {
                Id = Guid.NewGuid(),
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            }
        };
        var run = new AssessmentRun
        {
            Id = Guid.NewGuid(),
            Customer = customer,
            Name = "Synthetic completed assessment",
            Assessor = "Test operator",
            Source = "Manual",
            CatalogVersion = "2.0",
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
            IsComplete = true,
            ReadinessRating = "Critical",
            CalculatedUtc = DateTime.UtcNow,
            Results =
            [
                new AssessmentResult
                {
                    ControlId = firstControl.ControlId,
                    Status = "NotImplemented",
                    Assessor = "Test operator",
                    ReviewedUtc = DateTime.UtcNow
                }
            ]
        };
        db.AssessmentRuns.Add(run);
        await db.SaveChangesAsync();
        return (customer.Id, run.Id, firstControl.ControlId);
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Role", out var role) ||
                !Request.Headers.TryGetValue("X-Test-User", out var userId))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim> { new Claim(ClaimTypes.Name, userId.ToString()) };
            if (!Request.Headers.ContainsKey("X-Test-Missing-Identity"))
                claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));
            if (Request.Headers.TryGetValue("X-Test-Subject", out var subject))
                claims.Add(new Claim("sub", subject.ToString()));
            claims.AddRange(role.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => new Claim(ClaimTypes.Role, item)));
            var identity = new ClaimsIdentity(claims, Scheme.Name, ClaimTypes.Name, ClaimTypes.Role);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed record PlanResponse(
        Guid Id,
        string Status,
        bool ExecutionEnabled,
        IReadOnlyList<PlanEventResponse> Events);

    private sealed record PlanEventResponse(string EventType, string ActorId, string Actor, string? Notes);

    private sealed record WorkspaceExport(int SchemaVersion, IReadOnlyList<PlanExport> ChangePlans);

    private sealed record PlanExport(Guid Id, IReadOnlyList<PlanEventResponse> Events);
}
