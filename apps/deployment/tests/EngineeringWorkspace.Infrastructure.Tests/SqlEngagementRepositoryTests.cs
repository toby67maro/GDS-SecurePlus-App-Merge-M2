using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Scheduling;
using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Customers;
using EngineeringWorkspace.Domain.Workspaces;
using EngineeringWorkspace.Infrastructure.Auditing;
using EngineeringWorkspace.Infrastructure.Database;
using EngineeringWorkspace.Infrastructure.Persistence;
using EngineeringWorkspace.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace EngineeringWorkspace.Infrastructure.Tests;

public sealed class SqlEngagementRepositoryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("secureplus-engagement-");
    private readonly DatabaseProvisioningService _provisioning;
    private readonly DatabaseConnectionFactory _connections;
    private readonly DatabaseSettings _settings;

    public SqlEngagementRepositoryTests()
    {
        var paths = new PathSettings(_root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName,
            _root.FullName, Path.Combine(_root.FullName, "audit"), _root.FullName, _root.FullName, _root.FullName);
        var context = new AuditContext("tester");
        var redaction = new RedactionService();
        _connections = new DatabaseConnectionFactory(new UnavailableSecretStore(), paths);
        _provisioning = new DatabaseProvisioningService(_connections, new DatabaseErrorTranslator(redaction),
            new JsonLinesAuditService(paths.AuditDirectory, context, redaction, TimeProvider.System, NullLogger<JsonLinesAuditService>.Instance),
            context, new ApplicationSettings("id", "name", "title", "company", "Test", "dashboard/overview", false, false, "1.0.0"),
            TimeProvider.System, NullLogger<DatabaseProvisioningService>.Instance);
        _settings = new DatabaseSettings { Provider = DatabaseProvider.SQLite, DatabaseName = Path.Combine(_root.FullName, "secureplus.db"), CommandTimeoutSeconds = 30 };
    }

    [Fact]
    public async Task Customer_profiles_are_stored_in_the_database_and_fill_the_EaaS_customer_tables()
    {
        var status = await ReadyAsync();
        var repository = new SqlEngagementRepository(new FixedStatus(_settings, status), _connections, new AuditContext("tester"), TimeProvider.System);
        var tenantId = Guid.NewGuid();
        var profile = CustomerProfile.Create(new CustomerId("fabrikam"), "Fabrikam Inc") with
        {
            Tenants = [new EngineeringWorkspace.Domain.Tenants.TenantConfiguration("primary", "Fabrikam", "fabrikam.onmicrosoft.com", tenantId, "fabrikam.com",
                EngineeringWorkspace.Domain.Tenants.CloudEnvironment.Commercial,
                new EngineeringWorkspace.Domain.Tenants.ApplicationRegistration(Guid.NewGuid(), null, EngineeringWorkspace.Domain.Tenants.TenantAuthenticationType.ClientSecret,
                    null, null, null, []))],
            Contacts =
            [
                new CustomerContact("Ana Ruiz", "ana@fabrikam.com", "IT Director", null, false, "+1 555 0100", null, IsPrimary: true),
                new CustomerContact("Lee Chen", "lee@fabrikam.com", "CISO", null, false, "+1 555 0101", "+1 555 0199", IsChangeApprover: true)
            ],
            Organization = new CustomerOrganization("52", "51-300", "1 Main Street", null, "Austin", "Texas", "73301", "United States", "+1 555 0000",
                "info@fabrikam.com", "https://fabrikam.com", "CRM-42", "Sam Sales", "Tia Tech", "Pat PM", "SecurePlus")
        };

        await repository.SaveCustomerProfileAsync(profile, isSample: false);

        var stored = await repository.LoadCustomerProfileAsync("fabrikam");
        stored!.Version.Should().Be(1);
        stored.Profile.Details.Should().Be(profile.Details);
        stored.Profile.Contacts.Should().Equal(profile.Contacts);
        stored.Profile.Tenants.Single().TenantId.Should().Be(tenantId);
        (await repository.ListCustomerProfilesAsync()).Select(item => item.Profile.Name).Should().Equal("Fabrikam Inc");

        await using var connection = new SqliteConnection($"Data Source={_settings.DatabaseName}");
        await connection.OpenAsync();
        async Task<string?> Text(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        (await Text("""SELECT IndustryCode || '|' || OrgSizeBandCode || '|' || City || '|' || StateRegion || '|' || Phone || '|' || CrmAccountId FROM "cust.Customer" WHERE CustomerKey = 'fabrikam';"""))
            .Should().Be("52|51-300|Austin|Texas|+1 555 0000|CRM-42");
        (await Text("""SELECT COUNT(*) FROM "cust.Contact" c JOIN "cust.Customer" u ON u.CustomerId = c.CustomerId WHERE u.CustomerKey = 'fabrikam';""")).Should().Be("2");
        (await Text("""SELECT RoleCode || '|' || Phone || '|' || IsChangeApprover FROM "cust.Contact" WHERE Email = 'lee@fabrikam.com';""")).Should().Be("ChangeApprover|+1 555 0101|1");
        (await Text("""SELECT PrimaryDomain || '|' || CloudEnvironmentCode || '|' || TenantKey FROM "cust.Tenant" WHERE TenantGuid = '""" + tenantId + "';")).Should().Be("fabrikam.com|Commercial|primary");
        (await Text("""SELECT e.SalesOwner || '|' || e.TechnicalLead || '|' || e.ProjectManager FROM "cust.Engagement" e JOIN "secureplus.CustomerLink" l ON l.EngagementId = e.EngagementId WHERE l.CustomerKey = 'fabrikam';"""))
            .Should().Be("Sam Sales|Tia Tech|Pat PM");
        (await Text("""SELECT COUNT(*) FROM "audit.TenantConnection" WHERE AppRoleCode = 'Collector';""")).Should().Be("1");

        // Licences from an assessment fill kb.LicenseSku and cust.TenantLicense (re-recording the same day replaces them).
        var licences = new[] { new EngineeringWorkspace.Application.Customers.TenantLicenseCount("SPB", 25, 21), new EngineeringWorkspace.Application.Customers.TenantLicenseCount("AAD_PREMIUM_P2", 5, 2) };
        await repository.RecordTenantLicensesAsync("fabrikam", "primary", licences, new DateOnly(2026, 11, 8));
        await repository.RecordTenantLicensesAsync("fabrikam", "primary", licences, new DateOnly(2026, 11, 8));
        (await Text("""SELECT COUNT(*) FROM "cust.TenantLicense";""")).Should().Be("2");
        (await Text("""SELECT l.Quantity || '/' || l.Assigned FROM "cust.TenantLicense" l JOIN "kb.LicenseSku" k ON k.LicenseSkuId = l.LicenseSkuId WHERE k.SkuPartNumber = 'SPB';""")).Should().Be("25/21");

        // Exceptions and change requests also appear in the EaaS tables.
        var governance = new SqlGovernanceStore(new FixedStatus(_settings, status), _connections, TimeProvider.System);
        var exception = await governance.SaveExceptionAsync(new EngineeringWorkspace.Application.Governance.CustomerExceptionRecord(0, "fabrikam", "CA-101", "Legacy app", "Lee Chen",
            DateTimeOffset.UtcNow.AddDays(90), EngineeringWorkspace.Application.Governance.ExceptionStatusCodes.Approved, "tester", DateTimeOffset.UtcNow));
        (await Text("""SELECT COUNT(*) FROM "cust.Exception" WHERE SecurePlusExceptionId IS NOT NULL;""")).Should().Be("1");
        await governance.SaveExceptionAsync(exception with { StatusCode = EngineeringWorkspace.Application.Governance.ExceptionStatusCodes.Revoked });
        (await Text("""SELECT COUNT(*) FROM "cust.Exception";""")).Should().Be("0", "only approved exceptions are mirrored");
        var change = await governance.SaveChangeRequestAsync(new EngineeringWorkspace.Application.Governance.ChangeRequestRecord(0, "fabrikam", "Enable MFA for admins", null, "Medium",
            "Admins re-register", "Disable the policy", "tester", null, null, null, null, EngineeringWorkspace.Application.Governance.ChangeStatusCodes.Draft, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, []));
        await governance.SaveChangeRequestAsync(change with { StatusCode = EngineeringWorkspace.Application.Governance.ChangeStatusCodes.Submitted });
        (await Text("""SELECT Title || '|' || DecisionCode FROM "deploy.ChangeRequest" WHERE SecurePlusChangeRequestId IS NOT NULL;""")).Should().Be("Enable MFA for admins|Pending");
        await governance.DeleteChangeRequestAsync(change.ChangeRequestId);
        (await Text("""SELECT COUNT(*) FROM "deploy.ChangeRequest";""")).Should().Be("0");

        // Removing a contact on the next save removes it from cust.Contact; the version increments.
        var saved = await repository.SaveCustomerProfileAsync(profile with { Contacts = [profile.Contacts[1]] }, isSample: false);
        saved.Version.Should().Be(2);
        (await Text("""SELECT COUNT(*) FROM "cust.Contact" WHERE Email = 'ana@fabrikam.com';""")).Should().Be("0");
        (await repository.LoadCustomerProfileAsync("fabrikam"))!.Profile.ChangeApprovers.Single().Name.Should().Be("Lee Chen");
    }

    [Fact]
    public async Task Records_customers_workspaces_and_assessments_and_reports_engagement_state()
    {
        var status = await ReadyAsync();
        status.Permissions.Should().ContainKey("SELECT").WhoseValue.Should().BeTrue();
        status.PermissionCheck.Should().Be(CheckState.Passed);
        var repository = new SqlEngagementRepository(new FixedStatus(_settings, status), _connections, new AuditContext("tester"), TimeProvider.System);
        repository.IsAvailable.Should().BeTrue();

        var customer = CustomerProfile.Create(new CustomerId("gds-labs"), "GDS Labs");
        (await repository.GetCustomerStateAsync(customer.Id)).CustomerRecorded.Should().BeFalse();
        await repository.UpsertCustomerAsync(customer, isSample: false);
        await repository.UpsertCustomerAsync(customer with { Name = "GDS Labs Ltd" }, isSample: false);

        var state = await repository.GetCustomerStateAsync(customer.Id);
        state.CustomerRecorded.Should().BeTrue();
        state.HasAssessment.Should().BeFalse();
        state.HasWorkspace.Should().BeFalse();

        var now = DateTimeOffset.UtcNow;
        var workspace = Workspace.Create(new WorkspaceId("gds-labs"), "GDS Labs", Path.Combine(_root.FullName, "gds-labs"), "Test", now) with { CurrentCustomerId = customer.Id };
        await repository.UpsertWorkspaceAsync(workspace);
        await repository.UpsertWorkspaceAsync(workspace with { Name = "GDS Labs workspace" });

        var findings = EngagementService.SampleFindings();
        var recorded = await repository.RecordAssessmentAsync(new RecordAssessmentRequest(customer.Id, workspace.Id, "Baseline", AssessmentStatusCodes.Completed, "SecurePlus", 61.5m,
            findings, IsSample: true));
        await repository.RecordAssessmentAsync(new RecordAssessmentRequest(customer.Id, workspace.Id, "Initial assessment", AssessmentStatusCodes.Pending, "SecurePlus", null, [], false));

        state = await repository.GetCustomerStateAsync(customer.Id);
        state.AssessmentCount.Should().Be(2);
        state.WorkspaceCount.Should().Be(1);
        var all = await repository.ListAssessmentsAsync(null);
        all.Should().HaveCount(2);
        var baseline = all.Single(item => item.Name == "Baseline");
        baseline.ControlsTotal.Should().Be(findings.Count);
        baseline.ControlsPassed.Should().Be(findings.Count(finding => finding.Result == "Pass"));
        baseline.Score.Should().Be(61.5m);
        baseline.IsSample.Should().BeTrue();
        baseline.CompletedAtUtc.Should().NotBeNull();
        baseline.RequestedBy.Should().Be("tester");
        recorded.AssessmentRunId.Should().Be(baseline.AssessmentRunId);
        (await repository.ListFindingsAsync(baseline.AssessmentRunId)).Should().HaveCount(findings.Count);
        var pending = all.Single(item => item.Name == "Initial assessment");
        pending.StatusCode.Should().Be(AssessmentStatusCodes.Pending);

        var runId = await repository.StartAssessmentAsync(customer.Id, workspace.Id, "Engine run", pending.AssessmentRunId);
        runId.Should().Be(pending.AssessmentRunId);
        var results = Enumerable.Range(0, 250).Select(index => new AssessmentResultItem($"CTL-{index}", "Area", $"Control {index}",
            index % 2 == 0 ? AssessmentResultValues.Pass : AssessmentResultValues.Missing, "High", "evidence", null)).ToArray();
        await repository.CompleteAssessmentAsync(runId, AssessmentStatusCodes.Completed, 50m, results);
        (await repository.ListResultsAsync(runId)).Should().HaveCount(250);
        (await repository.ListFindingsAsync(runId)).Should().HaveCount(250);
        var completed = (await repository.ListAssessmentsAsync(customer.Id)).Single(item => item.AssessmentRunId == runId);
        completed.StatusCode.Should().Be(AssessmentStatusCodes.Completed);
        completed.ControlsTotal.Should().Be(250);
        completed.ControlsPassed.Should().Be(125);

        // Chunked writes: a live run that stops half-way keeps what was saved and is marked Partial.
        var liveRun = await repository.StartAssessmentAsync(customer.Id, workspace.Id, "Live run", null, AssessmentRunSources.Live, null);
        await repository.AppendResultsAsync(liveRun, results.Take(100).ToArray());
        await repository.AppendResultsAsync(liveRun, results.Skip(100).Take(50).ToArray());
        await repository.FinishAssessmentAsync(liveRun, new AssessmentRunTotals(AssessmentStatusCodes.Partial, null, 0, 0, 150, "Connection lost"));
        var partial = (await repository.ListAssessmentsAsync(customer.Id)).Single(item => item.AssessmentRunId == liveRun);
        partial.StatusCode.Should().Be(AssessmentStatusCodes.Partial);
        partial.Source.Should().Be(AssessmentRunSources.Live);
        partial.ResultsSaved.Should().Be(150);
        partial.ErrorMessage.Should().Be("Connection lost");
        (await repository.ListResultsAsync(liveRun)).Should().HaveCount(150);

        var due = DateTimeOffset.UtcNow.AddMinutes(-5);
        var schedule = await repository.SaveScheduleAsync(new AssessmentSchedule(0, "Second Tuesday", [customer.Id.Value],
            new ScheduleRecurrence(ScheduleFrequency.Monthly, new TimeOnly(18, 30), MonthlyMode: MonthlyMode.NthWeekday, WeekOfMonth: 2, Weekday: DayOfWeek.Tuesday,
                Months: ScheduleMasks.Months([1, 4, 7, 10])), true, "UTC", null, due));
        schedule.ScheduleId.Should().BePositive();
        schedule.Recurrence.Weekday.Should().Be(DayOfWeek.Tuesday);
        schedule.Recurrence.Months.Should().Be(ScheduleMasks.Months([1, 4, 7, 10]));
        schedule.CustomerKeys.Should().Equal(customer.Id.Value);
        schedule.LastStatus.Should().Be(ScheduleStatusCodes.Idle);

        // Claims are atomic: the second host cannot claim the same occurrence; a running schedule cannot be claimed again.
        var next = due.AddMonths(3);
        (await repository.TryClaimScheduleAsync(schedule.ScheduleId, due, next, TimeSpan.FromHours(6))).Should().BeTrue();
        (await repository.TryClaimScheduleAsync(schedule.ScheduleId, due, next, TimeSpan.FromHours(6))).Should().BeFalse();
        (await repository.TryClaimScheduleAsync(schedule.ScheduleId, null, null, TimeSpan.FromHours(6))).Should().BeFalse();
        var scheduleRun = await repository.StartScheduleRunAsync(schedule.ScheduleId, "Task Scheduler");
        await repository.CompleteScheduleRunAsync(scheduleRun, ScheduleStatusCodes.Succeeded, "1 assessment completed.", [liveRun]);
        await repository.UpdateScheduleStatusAsync(schedule.ScheduleId, ScheduleStatusCodes.Succeeded, "1 assessment completed.", DateTimeOffset.UtcNow);
        var stored = (await repository.ListSchedulesAsync()).Single();
        stored.LastStatus.Should().Be(ScheduleStatusCodes.Succeeded);
        stored.LastRunUtc.Should().NotBeNull();
        stored.NextRunUtc.Should().BeCloseTo(next, TimeSpan.FromSeconds(1));
        (await repository.ListScheduleRunsAsync(schedule.ScheduleId, 10)).Should().ContainSingle().Which.AssessmentRunIds.Should().Be(liveRun.ToString(System.Globalization.CultureInfo.InvariantCulture));

        await repository.SetScheduleEnabledAsync(schedule.ScheduleId, false, null);
        (await repository.ListSchedulesAsync()).Should().ContainSingle().Which.IsEnabled.Should().BeFalse();
        await repository.DeleteScheduleAsync(schedule.ScheduleId);
        (await repository.ListSchedulesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Repair_recreates_missing_tables_and_nullable_columns()
    {
        await ReadyAsync();
        await using (var connection = _connections.CreateSqlite(_settings, allowCreate: false))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                                  PRAGMA foreign_keys = OFF;
                                  DROP TABLE "secureplus.AssessmentFinding";
                                  ALTER TABLE "cust.Customer" DROP COLUMN "Notes";
                                  """;
            await command.ExecuteNonQueryAsync();
        }

        var broken = await _provisioning.ValidateSchemaAsync(_settings, null, CancellationToken.None);
        broken.State.Should().Be(DatabaseOverallState.IncompatibleSchema);
        broken.CanRepairSchema.Should().BeTrue();
        broken.SchemaFindings.Should().Contain(finding => finding.Contains("AssessmentFinding", StringComparison.Ordinal))
            .And.Contain(finding => finding.Contains("cust.Customer.Notes", StringComparison.Ordinal));

        var repaired = await _provisioning.RepairSchemaAsync(_settings, null, CancellationToken.None);
        repaired.State.Should().Be(DatabaseOverallState.Ready, repaired.SanitizedError);
        repaired.SchemaFindings.Should().BeEmpty();
    }

    [Fact]
    public async Task Repository_is_unavailable_until_the_database_is_ready()
    {
        var repository = new SqlEngagementRepository(new FixedStatus(_settings, null), _connections, new AuditContext("tester"), TimeProvider.System);
        repository.IsAvailable.Should().BeFalse();
        var none = new SqlEngagementRepository(new FixedStatus(new DatabaseSettings(), null), _connections, new AuditContext("tester"), TimeProvider.System);
        await none.Invoking(item => item.ListAssessmentsAsync(null)).Should().ThrowAsync<EngagementStoreUnavailableException>();
    }

    private async Task<DatabaseStatusReport> ReadyAsync()
    {
        await _provisioning.CreateDatabaseAsync(_settings, null, CancellationToken.None);
        var initialized = await _provisioning.InitializeSchemaAsync(_settings, null, CancellationToken.None);
        initialized.State.Should().Be(DatabaseOverallState.Ready, initialized.SanitizedError);
        return initialized;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _root.Delete(recursive: true);
    }

    private sealed class FixedStatus(DatabaseSettings settings, DatabaseStatusReport? report) : IDatabaseStatusService
    {
        public DatabaseSettings Settings => settings;
        public DatabaseStatusReport? LatestStatus => report;
        public event EventHandler<DatabaseStatusReport>? StatusChanged { add { } remove { } }
        public void Publish(DatabaseStatusReport value) { }
        public Task<DatabaseStatusReport> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(report!);
        public Task<OperationResult<string>> SaveSettingsAsync(DatabaseSettings value, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationResult<string>.Success("unused"));
    }
}
