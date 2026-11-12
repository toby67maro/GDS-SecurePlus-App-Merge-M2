using System.Net;
using System.Net.Http.Json;
using ArchiTech.Api.Data;
using ArchiTech.Api.Domain;
using ArchiTech.Api.Endpoints;
using ArchiTech.Api.Integrations;
using ArchiTech.Api.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArchiTech.Tests;

public sealed class PlannerHandoffEndpointTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly HandoffHandler _handler = new();
    private WebApplication _app = null!;
    private Guid _id;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = "Development";
        builder.WebHost.UseTestServer();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
        builder.Services.AddSingleton(DeploymentControlCatalog.Load(Path.Combine(
            AppContext.BaseDirectory, "Data", "deployment-controls-v2.0.json")));
        builder.Services.AddSingleton<CustomerRoadmapWorkbook>();
        builder.Services.AddSingleton(new ArchiTech.Api.Integrations.DeploymentBridge(Microsoft.Extensions.Options.Options.Create(new ArchiTech.Api.Integrations.BridgeOptions()), Microsoft.Extensions.Logging.Abstractions.NullLogger<ArchiTech.Api.Integrations.DeploymentBridge>.Instance));
        builder.Services.AddSingleton(new PlannerHandoffService(
            new HttpClient(_handler),
            Options.Create(new PlannerFlowOptions { Endpoint = "https://flow.example.test/trigger", AuthenticationMode = "SignedUrlTest" })));
        builder.AddWorkspaceAuthentication();
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapSalesEndpoints("Sqlite");
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            var customer = new Customer
            {
                Id = Guid.NewGuid(), Name = "Synthetic", NormalizedName = "SYNTHETIC",
                CreatedUtc = DateTime.UtcNow
            };
            _id = Guid.NewGuid();
            db.Engagements.Add(new Engagement
            {
                Id = _id, Customer = customer, Salesperson = "Tester",
                PlannerHandoffStatus = "NotConfigured", CreatedUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _connection.DisposeAsync();
        _handler.Dispose();
    }

    [Fact]
    public async Task First_send_persists_result_and_sent_requests_do_not_resend()
    {
        using var client = _app.GetTestClient();
        var response = await client.PostAsync($"/api/engagements/{_id}/planner-handoff", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("Sent", result.GetProperty("plannerHandoffStatus").GetString());
        Assert.Equal("synthetic-plan", result.GetProperty("plannerPlanId").GetString());
        Assert.Equal(_id.ToString("D"), _handler.IdempotencyKey);
        await using var scope = _app.Services.CreateAsyncScope();
        var saved = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Engagements.AsNoTracking().SingleAsync(item => item.Id == _id);
        Assert.Equal("Sent", saved.PlannerHandoffStatus);
        Assert.Equal("synthetic-plan", saved.PlannerPlanId);
        Assert.NotNull(saved.PlannerHandedOffUtc);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsync($"/api/engagements/{_id}/planner-handoff", null)).StatusCode);
        Assert.Equal(1, _handler.Requests);
    }

    [Fact]
    public async Task Failed_send_is_persisted_and_cannot_be_blindly_retried()
    {
        _handler.Fail = true;
        using var client = _app.GetTestClient();
        var response = await client.PostAsync($"/api/engagements/{_id}/planner-handoff", null);
        var result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("Failed", result.GetProperty("plannerHandoffStatus").GetString());
        await using var scope = _app.Services.CreateAsyncScope();
        var saved = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Engagements.AsNoTracking().SingleAsync(item => item.Id == _id);
        Assert.Equal("Failed", saved.PlannerHandoffStatus);
        Assert.Contains("may exist", saved.PlannerHandoffMessage);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsync($"/api/engagements/{_id}/planner-handoff", null)).StatusCode);
        Assert.Equal(1, _handler.Requests);
    }

    [Fact]
    public async Task Concurrent_send_is_rejected_while_claim_is_pending()
    {
        _handler.Block = true;
        using var client = _app.GetTestClient();
        var first = client.PostAsync($"/api/engagements/{_id}/planner-handoff", null);
        try
        {
            await _handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var second = await client.PostAsync($"/api/engagements/{_id}/planner-handoff", null);
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            Assert.Equal(1, _handler.Requests);
        }
        finally
        {
            _handler.Release.TrySetResult();
        }
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
    }

    [Fact]
    public async Task Unknown_engagement_returns_not_found_without_sending()
    {
        using var client = _app.GetTestClient();
        var response = await client.PostAsync($"/api/engagements/{Guid.NewGuid()}/planner-handoff", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, _handler.Requests);
    }

    [Fact]
    public async Task Interrupted_send_remains_pending_and_cannot_be_resent()
    {
        _handler.Block = true;
        using var client = _app.GetTestClient();
        using var cancellation = new CancellationTokenSource();
        var first = client.PostAsync($"/api/engagements/{_id}/planner-handoff", null, cancellation.Token);
        await _handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await using var scope = _app.Services.CreateAsyncScope();
        var saved = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Engagements.AsNoTracking().SingleAsync(item => item.Id == _id);
        Assert.Equal("Pending", saved.PlannerHandoffStatus);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsync($"/api/engagements/{_id}/planner-handoff", null)).StatusCode);
        Assert.Equal(1, _handler.Requests);
    }

    [Fact]
    public async Task Failed_handoff_can_be_manually_confirmed_once_without_resending()
    {
        _handler.Fail = true;
        using var client = _app.GetTestClient();
        await client.PostAsync($"/api/engagements/{_id}/planner-handoff", null);
        var response = await client.PostAsJsonAsync($"/api/engagements/{_id}/planner-handoff/confirm",
            new { planId = "verified-plan", evidenceReference = "flow-run-123", confirmedTerminalAndComplete = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("ManualConfirmed", result.GetProperty("plannerHandoffStatus").GetString());
        Assert.Equal("verified-plan", result.GetProperty("plannerPlanId").GetString());
        await using var scope = _app.Services.CreateAsyncScope();
        var saved = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Engagements.AsNoTracking().SingleAsync(item => item.Id == _id);
        Assert.Contains("local-development-user", saved.PlannerHandoffMessage);
        Assert.Contains("flow-run-123", saved.PlannerHandoffMessage);
        Assert.NotNull(saved.PlannerHandedOffUtc);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(
            $"/api/engagements/{_id}/planner-handoff/confirm",
            new { planId = "another-plan", evidenceReference = "run", confirmedTerminalAndComplete = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsync($"/api/engagements/{_id}/planner-handoff", null)).StatusCode);
        Assert.Equal(1, _handler.Requests);
    }

    [Fact]
    public async Task Manual_confirmation_requires_attestation_and_does_not_override_pending()
    {
        using var client = _app.GetTestClient();
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var engagement = await db.Engagements.SingleAsync(item => item.Id == _id);
            engagement.PlannerHandoffStatus = "Pending";
            await db.SaveChangesAsync();
        }
        var url = $"/api/engagements/{_id}/planner-handoff/confirm";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url,
            new { planId = "plan", evidenceReference = "run", confirmedTerminalAndComplete = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url,
            new { planId = "bad plan", evidenceReference = "run", confirmedTerminalAndComplete = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url,
            new { planId = "plan", evidenceReference = "run", confirmedTerminalAndComplete = true })).StatusCode);
        Assert.Equal(0, _handler.Requests);
    }

    [Fact]
    public async Task Manual_confirmation_requires_operator_or_admin()
    {
        _app.Configuration["Authentication:DevelopmentRoles"] = "Sales";
        using var client = _app.GetTestClient();
        var response = await client.PostAsJsonAsync($"/api/engagements/{_id}/planner-handoff/confirm",
            new { planId = "plan", evidenceReference = "run", confirmedTerminalAndComplete = true });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, _handler.Requests);
    }

    [Fact]
    public async Task Journey_history_compares_consecutive_snapshots_for_same_customer_only()
    {
        Guid secondId;
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var first = await db.Engagements.SingleAsync(item => item.Id == _id);
            first.CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            first.Answers.Add(new EngagementAnswer { FieldKey = "orgDepartments", Value = "Finance" });
            secondId = Guid.NewGuid();
            db.Engagements.Add(new Engagement
            {
                Id = secondId, CustomerId = first.CustomerId, Salesperson = "Tester",
                PlannerHandoffStatus = "NotConfigured", CreatedUtc = first.CreatedUtc.AddDays(1),
                Answers = [new EngagementAnswer { FieldKey = "orgDepartments", Value = "Operations" }]
            });
            db.Engagements.Add(new Engagement
            {
                Id = Guid.NewGuid(), Customer = new Customer { Id = Guid.NewGuid(), Name = "Other", NormalizedName = "OTHER" },
                Salesperson = "Tester", PlannerHandoffStatus = "NotConfigured", CreatedUtc = first.CreatedUtc
            });
            await db.SaveChangesAsync();
        }
        using var client = _app.GetTestClient();
        var result = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"/api/engagements/{secondId}/journey-history");
        Assert.Equal(2, result.GetArrayLength());
        var change = result[1].GetProperty("changes")[0];
        Assert.Equal("orgDepartments", change.GetProperty("field").GetString());
        Assert.Equal("Finance", change.GetProperty("before")[0].GetString());
        Assert.Equal("Operations", change.GetProperty("after")[0].GetString());
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/engagements/{Guid.NewGuid()}/journey-history")).StatusCode);
    }

    [Fact]
    public async Task Package_comparison_contains_only_service_reference_and_flags_conflict()
    {
        using var client = _app.GetTestClient();
        var result = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"/api/engagements/{_id}/package-comparison");
        Assert.Contains("disagree", result.GetProperty("disclaimer").GetString());
        Assert.True(result.GetProperty("packages").GetArrayLength() >= 10);
        Assert.DoesNotContain("price", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class HandoffHandler : HttpMessageHandler
    {
        public bool Block { get; set; }
        public bool Fail { get; set; }
        public int Requests;
        public string? IdempotencyKey { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            IdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            Started.TrySetResult();
            if (Block)
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            return Fail
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { plannerPlanId = "synthetic-plan" })
                };
        }
    }
}
