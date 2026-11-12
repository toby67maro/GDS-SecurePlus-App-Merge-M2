using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ArchiTech.Tests;

public sealed class OwnershipAndManagementTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private WebApplication _app = null!;
    private Guid _aliceEngagement;
    private Guid _bobEngagement;

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
        builder.Services.AddSingleton(new FabricEventPublisher(Options.Create(new FabricOptions()), NullLogger<FabricEventPublisher>.Instance));
        builder.Services.AddSingleton(new DeploymentBridge(Options.Create(new BridgeOptions()), NullLogger<DeploymentBridge>.Instance));
        builder.Services.AddSingleton(new PlannerHandoffService(
            new HttpClient(), Options.Create(new PlannerFlowOptions())));
        builder.AddWorkspaceAuthentication();
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapSalesEndpoints("Sqlite");
        _app.MapManagementEndpoints();
        _app.MapExperienceEndpoints();
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            _aliceEngagement = Seed(db, "Alpha Co", "Alice", "alice");
            _bobEngagement = Seed(db, "Beta Co", "Bob", "bob");
            await db.SaveChangesAsync();
        }
        await _app.StartAsync();
    }

    private static Guid Seed(AppDbContext db, string company, string salesperson, string ownerId)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(), Name = company, NormalizedName = company.ToUpperInvariant(), CreatedUtc = DateTime.UtcNow
        };
        var id = Guid.NewGuid();
        db.Engagements.Add(new Engagement
        {
            Id = id, Customer = customer, Salesperson = salesperson, OwnerId = ownerId,
            OwnerEmail = $"{ownerId}@example.test", PlannerHandoffStatus = "NotConfigured", CreatedUtc = DateTime.UtcNow
        });
        return id;
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private HttpClient As(string userId, string roles)
    {
        _app.Configuration["Authentication:DevelopmentUserId"] = userId;
        _app.Configuration["Authentication:DevelopmentRoles"] = roles;
        return _app.GetTestClient();
    }

    [Fact]
    public async Task Salesperson_lists_and_opens_only_their_own_engagements()
    {
        using var alice = As("alice", "Sales");
        var list = await alice.GetFromJsonAsync<JsonElement>("/api/engagements");
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("Alpha Co", list[0].GetProperty("companyName").GetString());
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/api/engagements/{_aliceEngagement}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/engagements/{_bobEngagement}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/engagements/{_bobEngagement}/export.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await alice.PostAsync($"/api/engagements/{_bobEngagement}/planner-handoff", null)).StatusCode);
    }

    [Fact]
    public async Task Management_and_administrators_see_every_engagement()
    {
        foreach (var role in new[] { "Management", "DeploymentAdmin" })
        {
            using var client = As("carol", role);
            var list = await client.GetFromJsonAsync<JsonElement>("/api/engagements");
            Assert.Equal(2, list.GetArrayLength());
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/engagements/{_bobEngagement}")).StatusCode);
        }
    }

    [Fact]
    public async Task Management_dashboard_is_closed_to_salespeople()
    {
        using var sales = As("alice", "Sales");
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync("/api/management/engagements")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync("/api/management/summary")).StatusCode);
        using var manager = As("carol", "Management");
        var rows = await manager.GetFromJsonAsync<JsonElement>("/api/management/engagements");
        Assert.Equal(2, rows.GetArrayLength());
        var summary = await manager.GetFromJsonAsync<JsonElement>("/api/management/summary");
        Assert.Equal(2, summary.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Created_engagement_is_stamped_with_the_caller_and_experience()
    {
        using var alice = As("alice", "Sales");
        var response = await alice.PostAsJsonAsync("/api/engagements", new
        {
            companyName = "Gamma Co",
            salesperson = "Alice",
            experience = new
            {
                industryKey = "HEALTHCARE",
                groups = new[] { new { name = "Clinicians", classification = "FRONTLINE", userCount = 40 } }
            }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NotConfigured", created.GetProperty("deploymentSyncStatus").GetString());
        Assert.Equal("HEALTHCARE", created.GetProperty("experience").GetProperty("industryKey").GetString());
        var list = await alice.GetFromJsonAsync<JsonElement>("/api/engagements");
        Assert.Equal(2, list.GetArrayLength());
        using var bob = As("bob", "Sales");
        Assert.DoesNotContain(
            (await bob.GetFromJsonAsync<JsonElement>("/api/engagements")).EnumerateArray(),
            item => item.GetProperty("companyName").GetString() == "Gamma Co");
    }

    [Fact]
    public async Task Experience_catalog_and_evaluation_use_the_deployment_engine()
    {
        using var client = As("alice", "Sales");
        var catalog = await client.GetFromJsonAsync<JsonElement>("/api/experience/catalog");
        Assert.True(catalog.GetProperty("questions").GetArrayLength() > 0);
        Assert.True(catalog.GetProperty("industries").GetArrayLength() >= 20);
        var response = await client.PostAsJsonAsync("/api/experience/evaluate", new
        {
            experience = new { groups = new[] { new { name = "Executives", classification = "EXECUTIVE" } } },
            frameworks = Array.Empty<string>()
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(result.GetProperty("groups").EnumerateArray(),
            group => group.GetProperty("name").GetString() == "Executives");
    }
}

public sealed class DeploymentBridgeUnitTests
{
    [Theory]
    [InlineData("Acme Corp.", "acme-corp")]
    [InlineData("  O'Reilly & Sons  ", "o-reilly-sons")]
    [InlineData("!!!", "customer")]
    public void Customer_keys_are_stable_slugs(string name, string expected) =>
        Assert.Equal(expected, DeploymentBridge.Slug(name));

    [Fact]
    public void Customer_key_is_capped_at_64_characters() =>
        Assert.True(DeploymentBridge.Slug(new string('a', 200)).Length <= 64);

    [Fact]
    public void Script_batches_split_on_go_lines()
    {
        var batches = DeploymentBridge.SplitBatches("SELECT 1;\nGO\nSELECT 2;\r\ngo\n\nSELECT 3;").ToArray();
        Assert.Equal(["SELECT 1;", "SELECT 2;", "SELECT 3;"], batches);
    }

    [Fact]
    public void Merge_preserves_engineer_owned_data_and_adds_sales_fields()
    {
        var existing = """
        {"schema":1,"name":"Acme","tenants":[{"tenantId":"t-1"}],
         "contacts":[{"name":"Pat","email":"pat@acme.test","isPrimary":true}],
         "organization":{"technicalLead":"Eng One"},
         "userGroups":[{"key":"execs","name":"Execs","classification":"EXECUTIVE","notes":"keep"}]}
        """;
        var engagement = new Engagement
        {
            Salesperson = "Alice", PlannerHandoffStatus = "NotConfigured",
            Customer = new Customer { Name = "Acme", NormalizedName = "ACME", ContactName = "Sam", ContactEmail = "sam@acme.test" },
            ExperienceJson = """
            {"industryKey":"LEGAL","answers":{"q1":"strict"},
             "groups":[{"name":"execs","userCount":12},{"name":"Field","classification":"FRONTLINE"}]}
            """
        };

        var merged = JsonNode.Parse(DeploymentBridge.MergeProfile(existing, engagement))!;

        Assert.Equal("t-1", merged["tenants"]![0]!["tenantId"]!.GetValue<string>());
        Assert.Equal("Eng One", merged["organization"]!["technicalLead"]!.GetValue<string>());
        Assert.Equal("Alice", merged["organization"]!["salesLead"]!.GetValue<string>());
        Assert.Equal("LEGAL", merged["organization"]!["industryKey"]!.GetValue<string>());
        Assert.Equal("strict", merged["experienceAnswers"]!["q1"]!.GetValue<string>());
        var contacts = merged["contacts"]!.AsArray();
        Assert.Equal(2, contacts.Count);
        Assert.False(contacts[1]!["isPrimary"]!.GetValue<bool>());
        var groups = merged["userGroups"]!.AsArray();
        Assert.Equal(2, groups.Count);
        Assert.Equal("keep", groups[0]!["notes"]!.GetValue<string>());
        Assert.Equal(12, groups[0]!["userCount"]!.GetValue<int>());
        Assert.Equal("field", groups[1]!["key"]!.GetValue<string>());
    }

    [Fact]
    public void Merge_creates_a_new_profile_when_none_exists()
    {
        var engagement = new Engagement
        {
            Salesperson = "Alice", PlannerHandoffStatus = "NotConfigured",
            Customer = new Customer { Name = "NewCo", NormalizedName = "NEWCO", ContactEmail = "a@newco.test" }
        };
        var merged = JsonNode.Parse(DeploymentBridge.MergeProfile(null, engagement))!;
        Assert.Equal("NewCo", merged["name"]!.GetValue<string>());
        Assert.True(merged["contacts"]![0]!["isPrimary"]!.GetValue<bool>());
        Assert.Contains("sales-intake", merged["tags"]!.AsArray().Select(tag => tag!.GetValue<string>()));
    }
}
