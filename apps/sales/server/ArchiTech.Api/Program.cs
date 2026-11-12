using ArchiTech.Api.Data;
using ArchiTech.Api.Domain;
using ArchiTech.Api.Endpoints;
using ArchiTech.Api.Integrations;
using ArchiTech.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var databaseProvider = builder.Configuration["Database:Provider"] ?? "Sqlite";
var connectionString = builder.Configuration.GetConnectionString("Default");

if (databaseProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
{
    var databasePath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "architech.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    connectionString ??= $"Data Source={databasePath}";
    builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
}
else if (databaseProvider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
{
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException(
            "ConnectionStrings:Default is required when Database:Provider is SqlServer.");
    }

    builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connectionString));
}
else
{
    throw new InvalidOperationException(
        $"Unsupported Database:Provider '{databaseProvider}'. Use Sqlite or SqlServer.");
}

builder.Services
    .AddOptions<PlannerFlowOptions>()
    .BindConfiguration("PlannerFlow")
    .Validate(
        options => options.Validate(out _),
        "PlannerFlow configuration is invalid. Check the authentication mode, endpoint, and identity settings.")
    .Validate(
        options => builder.Environment.IsDevelopment() || options.AuthenticationMode != "SignedUrlTest",
        "SignedUrlTest is allowed only in Development.")
    .ValidateOnStart();
builder.Services.AddSingleton<IPlannerFlowTokenProvider, PlannerFlowTokenProvider>();
builder.Services.AddHttpClient<PlannerHandoffService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
})
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
    .RemoveAllLoggers();
builder.Services.AddSingleton<CustomerRoadmapWorkbook>();
builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection(BridgeOptions.SectionName));
builder.Services.AddSingleton<DeploymentBridge>();
builder.Services.Configure<FabricOptions>(builder.Configuration.GetSection(FabricOptions.SectionName));
builder.Services.AddSingleton<FabricEventPublisher>();
builder.Services.AddHostedService<DeploymentSyncWorker>();
builder.Services.AddSingleton<ILocalEvidenceWorker, LocalEvidenceWorker>();
var deploymentCatalogPath = Path.Combine(
    builder.Environment.ContentRootPath,
    "Data",
    "deployment-controls-v2.0.json");
builder.Services.AddSingleton(DeploymentControlCatalog.Load(deploymentCatalogPath));
builder.Services.AddProblemDetails();
builder.AddWorkspaceAuthentication();

var app = builder.Build();
app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (databaseProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
    {
        await db.Database.EnsureCreatedAsync();
        foreach (var (column, ddl) in new[]
        {
            ("OwnerId", "ALTER TABLE Engagements ADD COLUMN OwnerId TEXT NULL"),
            ("OwnerEmail", "ALTER TABLE Engagements ADD COLUMN OwnerEmail TEXT NULL"),
            ("DeploymentSyncStatus", "ALTER TABLE Engagements ADD COLUMN DeploymentSyncStatus TEXT NOT NULL DEFAULT 'Pending'"),
            ("DeploymentSyncMessage", "ALTER TABLE Engagements ADD COLUMN DeploymentSyncMessage TEXT NULL"),
            ("DeploymentSyncedUtc", "ALTER TABLE Engagements ADD COLUMN DeploymentSyncedUtc TEXT NULL"),
            ("ExperienceJson", "ALTER TABLE Engagements ADD COLUMN ExperienceJson TEXT NULL")
        })
        {
            var exists = await db.Database
                .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM pragma_table_info('Engagements') WHERE name = {0}", column)
                .SingleAsync();
            if (exists == 0) await db.Database.ExecuteSqlRawAsync(ddl);
        }
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS DeploymentProfiles (
                Id TEXT NOT NULL CONSTRAINT PK_DeploymentProfiles PRIMARY KEY,
                CustomerId TEXT NOT NULL,
                TenantId TEXT NULL,
                PrimaryDomain TEXT NULL,
                EntraApplicationClientId TEXT NULL,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                CONSTRAINT FK_DeploymentProfiles_Customers_CustomerId
                    FOREIGN KEY (CustomerId) REFERENCES Customers (Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_DeploymentProfiles_CustomerId
                ON DeploymentProfiles (CustomerId);
            CREATE TABLE IF NOT EXISTS DeploymentProfileSetupSteps (
                Id INTEGER NOT NULL CONSTRAINT PK_DeploymentProfileSetupSteps PRIMARY KEY AUTOINCREMENT,
                DeploymentProfileId TEXT NOT NULL,
                StepKey TEXT NOT NULL,
                IsComplete INTEGER NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                CONSTRAINT FK_DeploymentProfileSetupSteps_DeploymentProfiles_DeploymentProfileId
                    FOREIGN KEY (DeploymentProfileId) REFERENCES DeploymentProfiles (Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_DeploymentProfileSetupSteps_Profile_Step
                ON DeploymentProfileSetupSteps (DeploymentProfileId, StepKey);
            CREATE TABLE IF NOT EXISTS DeploymentFindings (
                Id INTEGER NOT NULL CONSTRAINT PK_DeploymentFindings PRIMARY KEY AUTOINCREMENT,
                DeploymentProfileId TEXT NOT NULL,
                ControlId TEXT NOT NULL,
                Status TEXT NOT NULL,
                Notes TEXT NULL,
                Evidence TEXT NULL,
                ReviewedBy TEXT NULL,
                UpdatedUtc TEXT NOT NULL,
                CONSTRAINT FK_DeploymentFindings_DeploymentProfiles_DeploymentProfileId
                    FOREIGN KEY (DeploymentProfileId) REFERENCES DeploymentProfiles (Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_DeploymentFindings_Profile_Control
                ON DeploymentFindings (DeploymentProfileId, ControlId);
            CREATE TABLE IF NOT EXISTS AssessmentRuns (
                Id TEXT NOT NULL CONSTRAINT PK_AssessmentRuns PRIMARY KEY,
                CustomerId TEXT NOT NULL,
                EngagementId TEXT NULL,
                Name TEXT NOT NULL,
                Assessor TEXT NOT NULL,
                Source TEXT NOT NULL,
                CatalogVersion TEXT NOT NULL,
                StartedUtc TEXT NOT NULL,
                CompletedUtc TEXT NULL,
                IsComplete INTEGER NOT NULL,
                OverallScore REAL NOT NULL,
                OverallPercentage REAL NOT NULL,
                TotalControls INTEGER NOT NULL,
                ImplementedControls INTEGER NOT NULL,
                PartialControls INTEGER NOT NULL,
                NotImplementedControls INTEGER NOT NULL,
                NotAssessedControls INTEGER NOT NULL,
                ExceptionControls INTEGER NOT NULL,
                NotApplicableControls INTEGER NOT NULL,
                ReadinessRating TEXT NOT NULL,
                CalculatedUtc TEXT NOT NULL,
                CONSTRAINT FK_AssessmentRuns_Customers_CustomerId
                    FOREIGN KEY (CustomerId) REFERENCES Customers (Id) ON DELETE RESTRICT,
                CONSTRAINT FK_AssessmentRuns_Engagements_EngagementId
                    FOREIGN KEY (EngagementId) REFERENCES Engagements (Id) ON DELETE RESTRICT
            );
            CREATE INDEX IF NOT EXISTS IX_AssessmentRuns_CustomerId_StartedUtc
                ON AssessmentRuns (CustomerId, StartedUtc);
            CREATE TABLE IF NOT EXISTS AssessmentResults (
                Id INTEGER NOT NULL CONSTRAINT PK_AssessmentResults PRIMARY KEY AUTOINCREMENT,
                AssessmentRunId TEXT NOT NULL,
                ControlId TEXT NOT NULL,
                Status TEXT NOT NULL,
                Notes TEXT NULL,
                Evidence TEXT NULL,
                Assessor TEXT NOT NULL,
                ReviewedUtc TEXT NOT NULL,
                ExceptionReason TEXT NULL,
                CONSTRAINT FK_AssessmentResults_AssessmentRuns_AssessmentRunId
                    FOREIGN KEY (AssessmentRunId) REFERENCES AssessmentRuns (Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_AssessmentResults_AssessmentRunId_ControlId
                ON AssessmentResults (AssessmentRunId, ControlId);
            CREATE TABLE IF NOT EXISTS AssessmentScoreBreakdowns (
                Id INTEGER NOT NULL CONSTRAINT PK_AssessmentScoreBreakdowns PRIMARY KEY AUTOINCREMENT,
                AssessmentRunId TEXT NOT NULL,
                BreakdownType TEXT NOT NULL,
                Label TEXT NOT NULL,
                Percentage REAL NOT NULL,
                TotalPoints INTEGER NOT NULL,
                EarnedPoints INTEGER NOT NULL,
                GapCount INTEGER NOT NULL,
                CONSTRAINT FK_AssessmentScoreBreakdowns_AssessmentRuns_AssessmentRunId
                    FOREIGN KEY (AssessmentRunId) REFERENCES AssessmentRuns (Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_AssessmentScoreBreakdowns_AssessmentRunId_BreakdownType_Label
                ON AssessmentScoreBreakdowns (AssessmentRunId, BreakdownType, Label);
            CREATE TABLE IF NOT EXISTS AssessmentRecommendations (
                Id INTEGER NOT NULL CONSTRAINT PK_AssessmentRecommendations PRIMARY KEY AUTOINCREMENT,
                AssessmentRunId TEXT NOT NULL,
                Sequence INTEGER NOT NULL,
                Severity TEXT NOT NULL,
                Text TEXT NOT NULL,
                CONSTRAINT FK_AssessmentRecommendations_AssessmentRuns_AssessmentRunId
                    FOREIGN KEY (AssessmentRunId) REFERENCES AssessmentRuns (Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_AssessmentRecommendations_AssessmentRunId_Sequence
                ON AssessmentRecommendations (AssessmentRunId, Sequence);
            CREATE TABLE IF NOT EXISTS DeploymentChangePlans (
                Id TEXT NOT NULL CONSTRAINT PK_DeploymentChangePlans PRIMARY KEY,
                CustomerId TEXT NOT NULL,
                AssessmentRunId TEXT NOT NULL,
                Title TEXT NOT NULL,
                Notes TEXT NULL,
                Status TEXT NOT NULL,
                RequestedById TEXT NOT NULL,
                RequestedBy TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                SubmittedUtc TEXT NULL,
                ReviewedById TEXT NULL,
                ReviewedBy TEXT NULL,
                ReviewedUtc TEXT NULL,
                ReviewDecision TEXT NULL,
                ReviewNotes TEXT NULL,
                CONSTRAINT FK_DeploymentChangePlans_Customers_CustomerId
                    FOREIGN KEY (CustomerId) REFERENCES Customers (Id) ON DELETE RESTRICT,
                CONSTRAINT FK_DeploymentChangePlans_AssessmentRuns_AssessmentRunId
                    FOREIGN KEY (AssessmentRunId) REFERENCES AssessmentRuns (Id) ON DELETE RESTRICT
            );
            CREATE INDEX IF NOT EXISTS IX_DeploymentChangePlans_CustomerId_CreatedUtc
                ON DeploymentChangePlans (CustomerId, CreatedUtc);
            CREATE INDEX IF NOT EXISTS IX_DeploymentChangePlans_AssessmentRunId
                ON DeploymentChangePlans (AssessmentRunId);
            CREATE INDEX IF NOT EXISTS IX_DeploymentChangePlans_Status
                ON DeploymentChangePlans (Status);
            CREATE TABLE IF NOT EXISTS DeploymentChangePlanItems (
                Id INTEGER NOT NULL CONSTRAINT PK_DeploymentChangePlanItems PRIMARY KEY AUTOINCREMENT,
                DeploymentChangePlanId TEXT NOT NULL,
                ControlId TEXT NOT NULL,
                ControlName TEXT NOT NULL,
                AssessmentStatus TEXT NOT NULL,
                Sequence INTEGER NOT NULL,
                CONSTRAINT FK_DeploymentChangePlanItems_DeploymentChangePlans_DeploymentChangePlanId
                    FOREIGN KEY (DeploymentChangePlanId) REFERENCES DeploymentChangePlans (Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_DeploymentChangePlanItems_PlanId_ControlId
                ON DeploymentChangePlanItems (DeploymentChangePlanId, ControlId);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_DeploymentChangePlanItems_PlanId_Sequence
                ON DeploymentChangePlanItems (DeploymentChangePlanId, Sequence);
            CREATE TABLE IF NOT EXISTS DeploymentChangePlanEvents (
                Id INTEGER NOT NULL CONSTRAINT PK_DeploymentChangePlanEvents PRIMARY KEY AUTOINCREMENT,
                DeploymentChangePlanId TEXT NOT NULL,
                EventType TEXT NOT NULL,
                ActorId TEXT NOT NULL,
                Actor TEXT NOT NULL,
                Notes TEXT NULL,
                OccurredUtc TEXT NOT NULL,
                CONSTRAINT FK_DeploymentChangePlanEvents_DeploymentChangePlans_DeploymentChangePlanId
                    FOREIGN KEY (DeploymentChangePlanId) REFERENCES DeploymentChangePlans (Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_DeploymentChangePlanEvents_PlanId_OccurredUtc
                ON DeploymentChangePlanEvents (DeploymentChangePlanId, OccurredUtc);
            """);
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS SecurePlusDecisions (
                Id TEXT NOT NULL,
                CatalogVersion TEXT NOT NULL,
                BaselineReferences TEXT NOT NULL,
                Phase TEXT NOT NULL,
                Category TEXT NOT NULL,
                Topic TEXT NOT NULL,
                Question TEXT NOT NULL,
                Rationale TEXT NOT NULL,
                AnswerType TEXT NOT NULL,
                SuggestedAnswers TEXT NOT NULL,
                ConfigurationImpact TEXT NOT NULL,
                DecisionOwner TEXT NOT NULL,
                RequiredParticipants TEXT NOT NULL,
                RequiredBy TEXT NOT NULL,
                EvidenceRequired TEXT NOT NULL,
                DefaultIfUndecided TEXT NOT NULL,
                EngineeringNotes TEXT NOT NULL,
                CONSTRAINT PK_SecurePlusDecisions PRIMARY KEY (CatalogVersion, Id)
            );
            CREATE INDEX IF NOT EXISTS IX_SecurePlusDecisions_CatalogVersion_Phase_Category
                ON SecurePlusDecisions (CatalogVersion, Phase, Category);
            CREATE TABLE IF NOT EXISTS EngagementDecisionResponses (
                Id INTEGER NOT NULL CONSTRAINT PK_EngagementDecisionResponses PRIMARY KEY AUTOINCREMENT,
                EngagementId TEXT NOT NULL,
                CatalogVersion TEXT NOT NULL,
                DecisionId TEXT NOT NULL,
                Status TEXT NOT NULL,
                Answer TEXT NULL,
                Notes TEXT NULL,
                UpdatedBy TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                CONSTRAINT FK_EngagementDecisionResponses_Engagements_EngagementId
                    FOREIGN KEY (EngagementId) REFERENCES Engagements (Id) ON DELETE CASCADE,
                CONSTRAINT FK_EngagementDecisionResponses_SecurePlusDecisions_DecisionId
                    FOREIGN KEY (CatalogVersion, DecisionId)
                        REFERENCES SecurePlusDecisions (CatalogVersion, Id) ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_EngagementDecisionResponses_EngagementId_DecisionId
                ON EngagementDecisionResponses (EngagementId, CatalogVersion, DecisionId);
            CREATE TABLE IF NOT EXISTS ProfileDecisionResponses (
                Id INTEGER NOT NULL CONSTRAINT PK_ProfileDecisionResponses PRIMARY KEY AUTOINCREMENT,
                DeploymentProfileId TEXT NOT NULL,
                CatalogVersion TEXT NOT NULL,
                DecisionId TEXT NOT NULL,
                Status TEXT NOT NULL,
                Answer TEXT NULL,
                Notes TEXT NULL,
                UpdatedBy TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                CONSTRAINT FK_ProfileDecisionResponses_DeploymentProfiles_DeploymentProfileId
                    FOREIGN KEY (DeploymentProfileId) REFERENCES DeploymentProfiles (Id) ON DELETE CASCADE,
                CONSTRAINT FK_ProfileDecisionResponses_SecurePlusDecisions_CatalogVersion_DecisionId
                    FOREIGN KEY (CatalogVersion, DecisionId)
                        REFERENCES SecurePlusDecisions (CatalogVersion, Id) ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ProfileDecisionResponses_DeploymentProfileId_CatalogVersion_DecisionId
                ON ProfileDecisionResponses (DeploymentProfileId, CatalogVersion, DecisionId);
            CREATE INDEX IF NOT EXISTS IX_ProfileDecisionResponses_CatalogVersion_DecisionId
                ON ProfileDecisionResponses (CatalogVersion, DecisionId);
            CREATE TABLE IF NOT EXISTS SecurePlusWorkItems (
                Id TEXT NOT NULL,
                CatalogVersion TEXT NOT NULL,
                PhaseReference TEXT NOT NULL,
                Phase TEXT NOT NULL,
                Workstream TEXT NOT NULL,
                TaskNumber INTEGER NOT NULL,
                TaskName TEXT NOT NULL,
                SubTaskNumber TEXT NOT NULL,
                SubTaskName TEXT NOT NULL,
                GroupAbbreviation TEXT NOT NULL,
                GroupItemNumber INTEGER NOT NULL,
                Control TEXT NOT NULL,
                ControlDescription TEXT NOT NULL,
                Deliverable TEXT NOT NULL,
                Dependency TEXT NOT NULL,
                AppliesWhen TEXT NOT NULL,
                LicenseRequirement TEXT NOT NULL,
                EstimatedHoursMin TEXT NOT NULL,
                EstimatedHoursMax TEXT NOT NULL,
                DurationDaysMin TEXT NOT NULL,
                DurationDaysMax TEXT NOT NULL,
                Parallelizable INTEGER NOT NULL,
                SupportHandoff INTEGER NOT NULL,
                ComplexityDriver TEXT NOT NULL,
                LeadershipNotes TEXT NOT NULL,
                CONSTRAINT PK_SecurePlusWorkItems PRIMARY KEY (CatalogVersion, Id)
            );
            CREATE INDEX IF NOT EXISTS IX_SecurePlusWorkItems_CatalogVersion_Phase_Workstream
                ON SecurePlusWorkItems (CatalogVersion, Phase, Workstream);
            CREATE TABLE IF NOT EXISTS SecurePlusBaselineSettings (
                Id TEXT NOT NULL,
                CatalogVersion TEXT NOT NULL,
                BaselineReference TEXT NOT NULL,
                Phase TEXT NOT NULL,
                Workstream TEXT NOT NULL,
                TaskNumber INTEGER NOT NULL,
                TaskName TEXT NOT NULL,
                GroupAbbreviation TEXT NOT NULL,
                GroupItemNumber INTEGER NOT NULL,
                Control TEXT NOT NULL,
                SettingCategory TEXT NOT NULL,
                SettingName TEXT NOT NULL,
                TargetAction TEXT NOT NULL,
                ReviewPath TEXT NOT NULL,
                DeploymentMethod TEXT NOT NULL,
                ValidationMethod TEXT NOT NULL,
                EvidenceRequired TEXT NOT NULL,
                AppliesWhen TEXT NOT NULL,
                LicenseRequirement TEXT NOT NULL,
                ImplementationMode TEXT NOT NULL,
                PilotGroup TEXT NOT NULL,
                Exclusions TEXT NOT NULL,
                ExceptionCriteria TEXT NOT NULL,
                RollbackMethod TEXT NOT NULL,
                ReviewCadence TEXT NOT NULL,
                SupportOwner TEXT NOT NULL,
                EngineeringNotes TEXT NOT NULL,
                CONSTRAINT PK_SecurePlusBaselineSettings PRIMARY KEY (CatalogVersion, Id)
            );
            CREATE INDEX IF NOT EXISTS IX_SecurePlusBaselineSettings_CatalogVersion_Phase_ImplementationMode
                ON SecurePlusBaselineSettings (CatalogVersion, Phase, ImplementationMode);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_SecurePlusBaselineSettings_Baseline_Control_Category_Name
                ON SecurePlusBaselineSettings (CatalogVersion, BaselineReference, Control, SettingCategory, SettingName);
            CREATE TABLE IF NOT EXISTS TargetModelTables (
                Id TEXT NOT NULL,
                CatalogVersion TEXT NOT NULL,
                Schema TEXT NOT NULL,
                Name TEXT NOT NULL,
                Description TEXT NOT NULL,
                EstimatedRows TEXT NOT NULL,
                PrimarySources TEXT NOT NULL,
                TemporalHistory TEXT NOT NULL,
                CONSTRAINT PK_TargetModelTables PRIMARY KEY (CatalogVersion, Id)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_TargetModelTables_Schema_Name
                ON TargetModelTables (CatalogVersion, Schema, Name);
            CREATE TABLE IF NOT EXISTS TargetModelColumns (
                TableId TEXT NOT NULL,
                CatalogVersion TEXT NOT NULL,
                ColumnOrdinal INTEGER NOT NULL,
                Name TEXT NOT NULL,
                DataType TEXT NOT NULL,
                IsPrimaryKey INTEGER NOT NULL,
                IsForeignKey INTEGER NOT NULL,
                ForeignKeyTarget TEXT NULL,
                IsUniqueKeyPart INTEGER NOT NULL,
                IsIndexed INTEGER NOT NULL,
                CONSTRAINT PK_TargetModelColumns PRIMARY KEY (CatalogVersion, TableId, ColumnOrdinal),
                CONSTRAINT FK_TargetModelColumns_TargetModelTables_TableId
                    FOREIGN KEY (CatalogVersion, TableId)
                        REFERENCES TargetModelTables (CatalogVersion, Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_TargetModelColumns_TableId_Name
                ON TargetModelColumns (CatalogVersion, TableId, Name);
            CREATE TABLE IF NOT EXISTS StandardsReferenceSources (
                Id TEXT NOT NULL,
                CatalogVersion TEXT NOT NULL,
                Area TEXT NOT NULL,
                Name TEXT NOT NULL,
                FileName TEXT NOT NULL,
                Format TEXT NOT NULL,
                SourceVersion TEXT NOT NULL,
                Status TEXT NOT NULL,
                UsageBoundary TEXT NOT NULL,
                Description TEXT NOT NULL,
                ImportedRecordCount INTEGER NULL,
                RecordUnit TEXT NULL,
                CONSTRAINT PK_StandardsReferenceSources PRIMARY KEY (CatalogVersion, Id)
            );
            CREATE INDEX IF NOT EXISTS IX_StandardsReferenceSources_CatalogVersion_Area_Status
                ON StandardsReferenceSources (CatalogVersion, Area, Status);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_StandardsReferenceSources_CatalogVersion_Name
                ON StandardsReferenceSources (CatalogVersion, Name);
            """);
    }
    else
    {
        await db.Database.MigrateAsync();
    }

    var csvPath = Path.Combine(
        app.Environment.ContentRootPath,
        "Data",
        "secure-plus-wbs-tasks.csv");
    await WbsCatalogSeeder.SeedAsync(db, csvPath);
    await StandardsCatalogSeeder.SeedAsync(
        db,
        Path.Combine(app.Environment.ContentRootPath, "Data", "standards"));
    var customersWithoutProfiles = await db.Customers
        .Where(customer => !db.DeploymentProfiles.Any(profile => profile.CustomerId == customer.Id))
        .ToListAsync();
    if (customersWithoutProfiles.Count > 0)
    {
        var now = DateTime.UtcNow;
        db.DeploymentProfiles.AddRange(customersWithoutProfiles.Select(customer => new DeploymentProfile
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            CreatedUtc = now,
            UpdatedUtc = now
        }));
        await db.SaveChangesAsync();
    }
}

app.MapSalesEndpoints(databaseProvider);
app.MapManagementEndpoints();
app.MapExperienceEndpoints();
app.MapDeploymentEndpoints();
app.MapAssessmentEndpoints();
app.MapLocalEvidenceEndpoints();
app.MapDeploymentChangePlanEndpoints();
app.MapStandardsEndpoints();
app.MapAuthenticationEndpoints();
app.MapFallbackToFile("index.html").AllowAnonymous();
app.Run();
