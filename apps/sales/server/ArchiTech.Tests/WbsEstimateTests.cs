using ArchiTech.Api.Data;
using ArchiTech.Api.Domain;
using ArchiTech.Api.Integrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;

namespace ArchiTech.Tests;

public sealed class WbsEstimateTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private AppDbContext _db = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AppDbContext(options);
        await _db.Database.EnsureCreatedAsync();
        var csvPath = Path.Combine(AppContext.BaseDirectory, "Data", "secure-plus-wbs-tasks.csv");
        await WbsCatalogSeeder.SeedAsync(_db, csvPath);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public void Empty_answers_match_the_default_workbook_scope()
    {
        var answers = AnswerCatalog.Normalize(new Dictionary<string, string[]?>());
        var estimate = WbsEstimator.Calculate(_db.WbsTasks, answers.Values);

        Assert.True(answers.IsValid);
        Assert.Equal(95, _db.WbsTasks.Count());
        Assert.Equal(19, estimate.Tasks.Count);
        Assert.Equal(104m, estimate.OneTimeHoursMin);
        Assert.Equal(198m, estimate.OneTimeHoursMax);
        Assert.Equal(28m, estimate.MonthlyHoursMin);
        Assert.Equal(60m, estimate.MonthlyHoursMax);
    }

    [Fact]
    public void All_scenarios_match_the_workbook_leadership_summary()
    {
        var answers = AnswerCatalog.Normalize(AllScenarioAnswers());
        var estimate = WbsEstimator.Calculate(_db.WbsTasks, answers.Values);

        Assert.True(answers.IsValid);
        Assert.Equal(95, estimate.Tasks.Count);
        Assert.Equal(1_120m, estimate.OneTimeHoursMin);
        Assert.Equal(2_230m, estimate.OneTimeHoursMax);
        Assert.Equal(36m, estimate.MonthlyHoursMin);
        Assert.Equal(76m, estimate.MonthlyHoursMax);
    }

    [Fact]
    public void Hidden_answers_are_removed_before_scope_calculation()
    {
        var answers = AnswerCatalog.Normalize(new Dictionary<string, string[]?>
        {
            ["onPremAD"] = ["no"],
            ["dcCount"] = ["4"],
            ["gpos"] = ["yes"],
            ["identityTarget"] = ["cloud-only"],
            ["mfa"] = ["microsoft"],
            ["moveToMsMfa"] = ["yes"],
            ["storage"] = ["egnyte"],
            ["fileServerTB"] = ["5"],
            ["aiReadiness"] = ["no"],
            ["agents"] = ["yes"]
        });

        Assert.True(answers.IsValid);
        Assert.False(answers.Values.ContainsKey("dcCount"));
        Assert.False(answers.Values.ContainsKey("gpos"));
        Assert.False(answers.Values.ContainsKey("identityTarget"));
        Assert.False(answers.Values.ContainsKey("moveToMsMfa"));
        Assert.False(answers.Values.ContainsKey("fileServerTB"));
        Assert.False(answers.Values.ContainsKey("agents"));
    }

    [Fact]
    public void Organization_answers_are_validated_without_changing_estimate()
    {
        var answers = AnswerCatalog.Normalize(new Dictionary<string, string[]?>
        {
            ["orgIndustry"] = ["Education"],
            ["orgDepartments"] = ["Finance", "finance", "Operations"],
            ["orgLocations"] = ["2"],
            ["orgItModel"] = ["both"],
            ["orgInfrastructure"] = ["cloud"]
        });
        Assert.True(answers.IsValid);
        Assert.Equal(2, answers.Values["orgDepartments"].Count);
        Assert.Equal(19, WbsEstimator.Calculate(_db.WbsTasks, answers.Values).Tasks.Count);
        Assert.False(AnswerCatalog.Normalize(new Dictionary<string, string[]?>
        {
            ["orgDepartments"] = Enumerable.Range(0, 21).Select(i => $"Team {i}").ToArray()
        }).IsValid);
        Assert.False(AnswerCatalog.Normalize(new Dictionary<string, string[]?> { ["orgLocations"] = ["1.5"] }).IsValid);
        Assert.False(AnswerCatalog.Normalize(new Dictionary<string, string[]?>
        {
            ["currentLicenses"] = ["unknown", "business-premium"]
        }).IsValid);
        var comparison = SecurePlusPackages.Compare(
            [new EngagementAnswer { FieldKey = "backupModel", Value = "managed" }]);
        Assert.Equal("Current backup", Assert.Single(comparison.Signals).Questionnaire);
        Assert.Contains("Acronis", Assert.Single(comparison.Signals).ComponentTerms);
        Assert.Contains(comparison.Packages, package => package.Components.Contains("Microsoft 365 E5"));
        Assert.Contains(comparison.Packages, package => package.Components.Any(component => component.Contains("E7")));
    }

    [Fact]
    public async Task Sqlite_persists_customer_engagement_answers_and_wbs_lines_relationally()
    {
        var task = await _db.WbsTasks.OrderBy(item => item.Sequence).FirstAsync();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Name = "Example Customer",
            NormalizedName = "EXAMPLE CUSTOMER",
            CreatedUtc = DateTime.UtcNow
        };
        var engagement = new Engagement
        {
            Id = Guid.NewGuid(),
            Customer = customer,
            Salesperson = "Sales",
            PlannerHandoffStatus = "NotConfigured",
            CreatedUtc = DateTime.UtcNow,
            OneTimeHoursMin = task.MinHours,
            OneTimeHoursMax = task.MaxHours,
            Answers = [new EngagementAnswer { FieldKey = "onPremAD", Value = "yes" }],
            Tasks =
            [
                new EngagementTask
                {
                    Id = Guid.NewGuid(),
                    WbsTaskId = task.Id,
                    WbsTask = task,
                    MinHours = task.MinHours,
                    MaxHours = task.MaxHours
                }
            ]
        };
        _db.Engagements.Add(engagement);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var persisted = await _db.Engagements
            .Include(item => item.Customer)
            .Include(item => item.Answers)
            .Include(item => item.Tasks)
            .SingleAsync(item => item.Id == engagement.Id);

        Assert.Equal(customer.Id, persisted.CustomerId);
        Assert.Equal("Example Customer", persisted.Customer.Name);
        Assert.Single(persisted.Answers);
        Assert.Single(persisted.Tasks);
    }

    [Fact]
    public async Task Planner_handoff_uses_a_stable_engagement_idempotency_key()
    {
        var handler = new CapturingHandler();
        using var client = new HttpClient(handler);
        var service = new PlannerHandoffService(
            client,
            Options.Create(new PlannerFlowOptions { Endpoint = "https://flow.example.test/trigger", AuthenticationMode = "SignedUrlTest" }));
        var task = await _db.WbsTasks.OrderBy(item => item.Sequence).FirstAsync();
        var engagement = new Engagement
        {
            Id = Guid.NewGuid(),
            Customer = new Customer
            {
                Id = Guid.NewGuid(),
                Name = "Planner Test Customer",
                NormalizedName = "PLANNER TEST CUSTOMER",
                CreatedUtc = DateTime.UtcNow
            },
            Salesperson = "Sales",
            PlannerHandoffStatus = "Pending",
            CreatedUtc = DateTime.UtcNow,
            Answers = [new EngagementAnswer { FieldKey = "onPremAD", Value = "yes" }],
            Tasks =
            [
                new EngagementTask
                {
                    Id = Guid.NewGuid(),
                    WbsTaskId = task.Id,
                    WbsTask = task,
                    MinHours = task.MinHours,
                    MaxHours = task.MaxHours
                }
            ]
        };

        var result = await service.SendAsync(engagement, CancellationToken.None);

        Assert.Equal("Sent", result.Status);
        Assert.Equal("plan-123", result.PlanId);
        Assert.Equal(engagement.Id.ToString("D"), handler.IdempotencyKey);
        Assert.Contains(engagement.Id.ToString("D"), handler.Body);
        Assert.Contains(task.Id, handler.Body);
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal(2, payload.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            engagement.Customer.Name,
            payload.RootElement.GetProperty("customer").GetProperty("companyName").GetString());
        Assert.Equal(
            "yes",
            payload.RootElement.GetProperty("answers").GetProperty("onPremAD")[0].GetString());
        Assert.Equal(engagement.Id.ToString(), payload.RootElement.GetProperty("engagementId").GetString());
        Assert.Equal(
            engagement.CustomerId.ToString(),
            payload.RootElement.GetProperty("customer").GetProperty("customerId").GetString());
        Assert.Equal(
            ["Sales", "Project Management", "Engineering"],
            payload.RootElement.GetProperty("stakeholderGroups").EnumerateArray()
                .Select(item => item.GetString()!).ToArray());
        Assert.Equal(
            task.Phase,
            payload.RootElement.GetProperty("phases")[0].GetProperty("name").GetString());
        var phaseWbsIds = payload.RootElement.GetProperty("phases")[0]
            .GetProperty("taskWbsIds").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains(task.Id, phaseWbsIds);
        Assert.Equal(task.Id, payload.RootElement.GetProperty("tasks")[0].GetProperty("wbsId").GetString());
        Assert.Equal(1, payload.RootElement.GetProperty("tasks").GetArrayLength());
    }

    [Fact]
    public async Task Planner_success_without_a_valid_plan_id_is_an_uncertain_failure()
    {
        var responses = new[] { "", "not-json", "{}", """{"plannerPlanId":""}""", """{"plannerPlanId":"   "}""",
            JsonSerializer.Serialize(new { plannerPlanId = new string('x', 201) }) };
        foreach (var response in responses)
        {
            using var client = new HttpClient(new CapturingHandler { ResponseBody = response });
            var service = new PlannerHandoffService(
                client, Options.Create(new PlannerFlowOptions { Endpoint = "https://flow.example.test/trigger", AuthenticationMode = "SignedUrlTest" }));
            var engagement = new Engagement
            {
                Id = Guid.NewGuid(), Customer = new Customer { Name = "Synthetic", NormalizedName = "SYNTHETIC" },
                Salesperson = "Tester", PlannerHandoffStatus = "Pending"
            };
            var result = await service.SendAsync(engagement, CancellationToken.None);
            Assert.Equal("Failed", result.Status);
            Assert.Null(result.PlanId);
            Assert.Null(result.HandedOffUtc);
            Assert.Contains("may exist", result.Message);
        }
    }

    [Theory]
    [InlineData("http")]
    [InlineData("network")]
    [InlineData("timeout")]
    public async Task Planner_failures_warn_that_the_remote_outcome_may_be_uncertain(string failure)
    {
        using var client = new HttpClient(new FailureHandler(failure));
        var service = new PlannerHandoffService(
            client, Options.Create(new PlannerFlowOptions { Endpoint = "https://flow.example.test/trigger", AuthenticationMode = "SignedUrlTest" }));
        var engagement = new Engagement
        {
            Id = Guid.NewGuid(), Customer = new Customer { Name = "Synthetic", NormalizedName = "SYNTHETIC" },
            Salesperson = "Tester", PlannerHandoffStatus = "Pending"
        };

        var result = await service.SendAsync(engagement, CancellationToken.None);

        Assert.Equal("Failed", result.Status);
        Assert.Null(result.PlanId);
        Assert.Contains("may exist", result.Message);
        Assert.Contains("idempotency", result.Message);
    }

    [Fact]
    public async Task Roadmap_workbook_contains_filtered_journey_and_answer_based_hotspots()
    {
        var task = await _db.WbsTasks.SingleAsync(item => item.Id == "0.6");
        var engagement = new Engagement
        {
            Id = Guid.NewGuid(),
            Customer = new Customer
            {
                Id = Guid.NewGuid(),
                Name = "Roadmap Test Customer",
                NormalizedName = "ROADMAP TEST CUSTOMER",
                CreatedUtc = DateTime.UtcNow
            },
            Salesperson = "Sales",
            PlannerHandoffStatus = "NotConfigured",
            CreatedUtc = DateTime.UtcNow,
            Answers = [
                new EngagementAnswer { FieldKey = "onPremAD", Value = "yes" },
                new EngagementAnswer { FieldKey = "orgDepartments", Value = "Finance" },
                new EngagementAnswer { FieldKey = "orgDepartments", Value = "Operations" }
            ],
            Tasks =
            [
                new EngagementTask
                {
                    Id = Guid.NewGuid(),
                    WbsTaskId = task.Id,
                    WbsTask = task,
                    MinHours = task.MinHours,
                    MaxHours = task.MaxHours
                }
            ]
        };

        var catalog = DeploymentControlCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Data", "deployment-controls-v2.0.json"));
        var bytes = new CustomerRoadmapWorkbook().Build(engagement, catalog.Controls);
        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);

        Assert.Equal(
            ["Summary", "Journey Roadmap", "Experience Map", "Control Impact", "Heatmap", "Phase Summary", "Hotspots", "Package Comparison", "Read Me"],
            workbook.Worksheets.Select(sheet => sheet.Name));
        var journey = workbook.Worksheet("Journey Roadmap");
        Assert.Single(journey.Tables);
        Assert.True(journey.Tables.Single().ShowAutoFilter);
        Assert.Equal("Customer leadership", journey.Cell(6, 7).GetString());
        Assert.Contains(
            "On-premises identity selected",
            journey.Cell(7, 14).GetString(),
            StringComparison.Ordinal);
        Assert.Single(workbook.Worksheet("Hotspots").Tables);
        Assert.Equal("Participate", journey.Cell(7, 8).GetString());
        var experience = workbook.Worksheet("Control Impact");
        Assert.Equal("Finance", experience.Cell(6, 7).GetString());
        Assert.Equal("Operations", experience.Cell(6, 8).GetString());
        Assert.Equal(catalog.Controls.OrderBy(control => control.ControlId, StringComparer.Ordinal).First().ControlId,
            experience.Cell(7, 1).GetString());
        Assert.Equal(371, experience.Column(1).CellsUsed().Count(cell => cell.Address.RowNumber >= 7));
        Assert.Equal("Review", experience.Cell(7, 6).GetString());
        Assert.True(experience.Cell(7, 6).HasDataValidation);
        Assert.NotEqual("Unknown", experience.Cell(7, 7).GetString());
        Assert.True(experience.Cell(7, 7).HasDataValidation);
        Assert.Equal(4, experience.ConditionalFormats.Count());
        Assert.NotNull(workbook.Worksheet("Experience Map"));
        Assert.NotNull(workbook.Worksheet("Heatmap"));
        engagement.ExperienceJson =
            """{"groups":[{"name":"Executives","classification":"executive","userCount":12},{"name":"Frontline","classification":"frontline","userCount":80}]}""";
        using var withGroups = new XLWorkbook(new MemoryStream(new CustomerRoadmapWorkbook().Build(engagement, catalog.Controls)));
        var map = withGroups.Worksheet("Experience Map");
        var mapText = string.Join(" ", map.CellsUsed().Select(cell => cell.GetString()));
        Assert.Contains("Executives", mapText, StringComparison.Ordinal);
        Assert.Contains("Frontline", mapText, StringComparison.Ordinal);
        Assert.Equal("All Users", withGroups.Worksheet("Control Impact").Cell(6, 7).GetString());
        Assert.Equal("Executives", withGroups.Worksheet("Control Impact").Cell(6, 8).GetString());
        var packages = workbook.Worksheet("Package Comparison");
        Assert.Single(packages.Tables);
        Assert.DoesNotContain("price", string.Join(" ", packages.CellsUsed().Select(cell => cell.GetString())),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deployment_catalog_has_all_unique_controls_and_scoring_respects_status()
    {
        var catalogPath = Path.Combine(AppContext.BaseDirectory, "Data", "deployment-controls-v2.0.json");
        var catalog = DeploymentControlCatalog.Load(catalogPath);
        var controls = catalog.Controls.Take(3).ToArray();
        var score = DeploymentScoring.Calculate(
            controls,
            new Dictionary<string, string>
            {
                [controls[0].ControlId] = "Implemented",
                [controls[1].ControlId] = "PartiallyImplemented"
            });

        Assert.Equal(371, catalog.Controls.Count);
        Assert.Equal(371, catalog.Controls.Select(control => control.ControlId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(3, score.TotalControls);
        Assert.Equal(1, score.StatusCounts["Implemented"]);
        Assert.Equal(1, score.StatusCounts["PartiallyImplemented"]);
        Assert.Equal(1, score.StatusCounts["NotAssessed"]);
    }

    [Fact]
    public async Task Deployment_profile_and_manual_finding_persist_for_service_plus_customer()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Name = "Deployment Test Customer",
            NormalizedName = "DEPLOYMENT TEST CUSTOMER",
            CreatedUtc = DateTime.UtcNow,
            DeploymentProfile = new DeploymentProfile
            {
                Id = Guid.NewGuid(),
                TenantId = Guid.NewGuid().ToString(),
                PrimaryDomain = "example.test",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                Findings =
                [
                    new DeploymentFinding
                    {
                        ControlId = "1.1",
                        Status = "PartiallyImplemented",
                        Notes = "Manual review required.",
                        UpdatedUtc = DateTime.UtcNow
                    }
                ]
            }
        };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var persisted = await _db.DeploymentProfiles
            .Include(profile => profile.Customer)
            .Include(profile => profile.Findings)
            .SingleAsync(profile => profile.CustomerId == customer.Id);

        Assert.Equal(customer.Name, persisted.Customer.Name);
        Assert.Equal("example.test", persisted.PrimaryDomain);
        Assert.Single(persisted.Findings);
        Assert.Equal("PartiallyImplemented", persisted.Findings.Single().Status);
        Assert.Equal("Manual review required.", persisted.Findings.Single().Notes);
    }

    [Fact]
    public void Assessment_scoring_tracks_status_weight_breakdowns_and_readiness()
    {
        var controls = new[]
        {
            AssessmentControl("A", "Implemented"),
            AssessmentControl("B", "Implemented"),
            AssessmentControl("C", "Partial"),
            AssessmentControl("D", "Gap"),
            AssessmentControl("E", "Not assessed"),
            AssessmentControl("F", "Exception")
        };
        var score = AssessmentScoring.Calculate(
            controls,
            new Dictionary<string, string>
            {
                ["A"] = "Implemented",
                ["B"] = "NotApplicable",
                ["C"] = "PartiallyImplemented",
                ["D"] = "NotImplemented",
                ["F"] = "Exception"
            });

        Assert.Equal(13, score.OverallScore);
        Assert.Equal(54.2, score.OverallPercentage);
        Assert.Equal(6, score.TotalControls);
        Assert.Equal(1, score.ImplementedControls);
        Assert.Equal(1, score.PartialControls);
        Assert.Equal(1, score.NotImplementedControls);
        Assert.Equal(1, score.NotAssessedControls);
        Assert.Equal(1, score.NotApplicableControls);
        Assert.Equal(1, score.ExceptionControls);
        Assert.Equal("HighRisk", score.ReadinessRating);
        Assert.Equal(3, score.Breakdowns.Count);
        Assert.Equal("Phase 1 — Identity Security", AssessmentScoring.FormatPhase("Phase1_IdentitySecurity"));
    }

    [Theory]
    [InlineData(90, "AIReady")]
    [InlineData(75, "LowRisk")]
    [InlineData(55, "ModerateRisk")]
    [InlineData(35, "HighRisk")]
    [InlineData(34.9, "Critical")]
    public void Assessment_readiness_thresholds_match_original_app(double percentage, string expected) =>
        Assert.Equal(expected, AssessmentScoring.Readiness(percentage));

    private static Dictionary<string, string[]?> AllScenarioAnswers() => new()
    {
        ["onPremAD"] = ["yes"],
        ["dcCount"] = ["3"],
        ["gpos"] = ["yes"],
        ["identityTarget"] = ["cloud-only"],
        ["mfa"] = ["duo"],
        ["moveToMsMfa"] = ["yes"],
        ["exchangeOnPrem"] = ["yes"],
        ["migrateExchange"] = ["yes"],
        ["devicesSendEmail"] = ["yes"],
        ["emailSecurity"] = ["proofpoint"],
        ["moveEmailSecurity"] = ["yes"],
        ["edr"] = ["third-party"],
        ["deployMde"] = ["yes"],
        ["intune"] = ["yes"],
        ["whfb"] = ["yes"],
        ["apple"] = ["yes"],
        ["appleOwned"] = ["yes"],
        ["byod"] = ["yes"],
        ["storage"] = ["fileserver", "egnyte"],
        ["fileServerTB"] = ["10"],
        ["poorSharePoint"] = ["yes"],
        ["modernizeData"] = ["yes"],
        ["usesSharePoint"] = ["yes"],
        ["externalCollab"] = ["yes"],
        ["purview"] = ["labels", "retention", "dlp"],
        ["aiReadiness"] = ["yes"],
        ["agents"] = ["yes"],
        ["redundantTools"] = ["yes"]
    };

    private static DeploymentControl AssessmentControl(string id, string name) =>
        new(
            id,
            $"Control {name}",
            "Authentication",
            "Phase1_IdentitySecurity",
            "Secure",
            "Test description",
            "Microsoft Entra ID",
            "Low",
            "Configured",
            "Manual",
            "Test catalog",
            4,
            true,
            "1.1",
            []);

    private sealed class FailureHandler(string failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            failure switch
            {
                "http" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)),
                "network" => Task.FromException<HttpResponseMessage>(new HttpRequestException("Synthetic network failure")),
                "timeout" => Task.FromException<HttpResponseMessage>(new OperationCanceledException()),
                _ => throw new ArgumentOutOfRangeException(nameof(failure))
            };
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? IdempotencyKey { get; private set; }
        public string ResponseBody { get; init; } = """{"plannerPlanId":"plan-123"}""";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            IdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    ResponseBody,
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
