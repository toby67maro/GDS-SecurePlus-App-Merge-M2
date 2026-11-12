using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Governance;
using EngineeringWorkspace.Application.Reference;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Infrastructure.Auditing;
using EngineeringWorkspace.Infrastructure.Database;
using EngineeringWorkspace.Infrastructure.Persistence;
using EngineeringWorkspace.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EngineeringWorkspace.Infrastructure.Tests;

/// <summary>4.0 C1 change request templates and E4 industry list (V0009, SQLite).</summary>
public sealed class ChangeRequestTemplateTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("secureplus-crt-");

    private async Task<(DatabaseConnectionFactory Connections, IDatabaseStatusService Status)> DatabaseAsync()
    {
        var paths = new PathSettings(_root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName,
            _root.FullName, Path.Combine(_root.FullName, "audit"), _root.FullName, _root.FullName, _root.FullName);
        var context = new AuditContext("tester");
        var redaction = new RedactionService();
        var connections = new DatabaseConnectionFactory(new UnavailableSecretStore(), paths);
        var provisioning = new DatabaseProvisioningService(connections, new DatabaseErrorTranslator(redaction),
            new JsonLinesAuditService(paths.AuditDirectory, context, redaction, TimeProvider.System, NullLogger<JsonLinesAuditService>.Instance), context,
            new ApplicationSettings("id", "name", "title", "company", "Test", "dashboard/overview", false, false, "4.0.0"), TimeProvider.System,
            NullLogger<DatabaseProvisioningService>.Instance);
        var settings = new DatabaseSettings { Provider = DatabaseProvider.SQLite, DatabaseName = Path.Combine(_root.FullName, "secureplus.db"), CommandTimeoutSeconds = 30 };
        await provisioning.CreateDatabaseAsync(settings, null, CancellationToken.None);
        var status = await provisioning.InitializeSchemaAsync(settings, null, CancellationToken.None);
        status.State.Should().Be(DatabaseOverallState.Ready, status.SanitizedError);
        status.CurrentSchemaVersion.Should().Be(12);
        return (connections, new Status(settings, status));
    }

    [Fact]
    public async Task Templates_copy_with_a_source_link_save_as_global_and_the_GDS_default_is_protected()
    {
        var (connections, status) = await DatabaseAsync();
        var repository = new SqlChangeRequestTemplateRepository(status, connections, new AuditContext("tester"), TimeProvider.System);

        var globals = await repository.ListGlobalAsync();
        globals.Should().ContainSingle(template => template.Key == ChangeRequestTemplates.DefaultKey && template.IsSystem);
        var change = () => repository.SaveAsync(globals[0]);
        await change.Should().ThrowAsync<InvalidOperationException>();
        var delete = () => repository.DeleteAsync(ChangeRequestTemplates.DefaultKey);
        await delete.Should().ThrowAsync<InvalidOperationException>();

        var copy = await repository.AssignAsync("contoso", ChangeRequestTemplates.DefaultKey);
        copy.SourceKey.Should().Be(ChangeRequestTemplates.DefaultKey);
        copy.SourceVersion.Should().Be(1);
        var edited = await repository.SaveAsync(copy with { Name = "Contoso CR", Content = copy.Content with { ApprovalText = "Approved by Contoso." } });
        edited.Version.Should().Be(copy.Version + 1);

        var global = await repository.SaveAsGlobalAsync(edited, "Healthcare CR");
        (await repository.ListGlobalAsync()).Should().Contain(template => template.Key == global.Key && template.Content.ApprovalText == "Approved by Contoso.");
        (await repository.ListForCustomerAsync("contoso")).Should().ContainSingle().Which.SourceKey.Should().Be(global.Key);
        (await repository.ListForCustomerAsync("fabrikam")).Should().BeEmpty();

        var sections = ChangeRequestTemplates.SectionsFromText(ChangeRequestTemplates.SectionsToText(ChangeRequestTemplates.Default.Content.Sections));
        sections.Should().BeEquivalentTo(ChangeRequestTemplates.Default.Content.Sections);
        ChangeRequestTemplates.Fill("[GDS CR-{{ChangeRequest.Id}}] {{Unknown}}", new Dictionary<string, string> { ["ChangeRequest.Id"] = "42" })
            .Should().Be("[GDS CR-42] {{Unknown}}");
    }

    [Fact]
    public async Task Industry_list_is_seeded_once_and_Lead_edits_are_kept()
    {
        var (connections, status) = await DatabaseAsync();
        var industries = new SqlIndustryRepository(status, connections, new AuditContext("tester"), TimeProvider.System);
        (await industries.ListAsync()).Should().HaveCount(20);
        await industries.SaveAsync(new IndustryValue("MARINE", "Marine, Ports & Maritime", 30));
        await industries.SaveAsync(new IndustryValue(string.Empty, "Mining", 205));
        (await industries.EnsureSeededAsync()).Should().Be(0, "nothing is missing and nothing is overwritten");
        var list = await industries.ListAsync();
        list.Should().Contain(item => item.Code == "MARINE" && item.Name == "Marine, Ports & Maritime" && item.IsModified);
        list.Should().Contain(item => item.Code == "MINING" && !item.IsSystem);
        IndustryList.Display(list, IndustryList.Other, "Aquaculture").Should().Be("Aquaculture");
    }

    [Fact]
    public async Task Project_task_keys_links_and_history_survive_saves()
    {
        var (connections, status) = await DatabaseAsync();
        var context = new AuditContext("tester");
        var engagement = new SqlEngagementRepository(status, connections, context, TimeProvider.System);
        var customer = EngineeringWorkspace.Domain.Customers.CustomerProfile.Create(new EngineeringWorkspace.Domain.Common.CustomerId("contoso"), "Contoso");
        await engagement.UpsertCustomerAsync(customer, isSample: false);
        var planning = new SqlPlanningRepository(status, connections);
        var start = new DateOnly(2026, 11, 9);
        var plan = new EngineeringWorkspace.Application.Planning.ProjectPlan(null, "Plan", start, start.AddDays(3), "Draft",
        [
            new("task-a", "1", "Design", "Plan", "Architect", null, start, start, 1, 8, 0, false, "NotStarted", null, null, [], ScheduleRank: 0),
            new("task-b", "2", "Remediate CA", "Build", "Engineer", null, start.AddDays(1), start.AddDays(1), 1, 8, 0, false, "Staged", null, null, ["task-a"],
                Notes: "Report-only first", ChangeRequestIds: [7], ControlIds: ["CA-100"], ScheduleRank: 1, PinnedStart: start.AddDays(1))
        ]);
        var saved = await planning.SaveProjectPlanAsync("contoso", plan);
        saved.Tasks.Select(task => task.Key).Should().Equal("task-a", "task-b");
        var again = await planning.SaveProjectPlanAsync("contoso", saved);
        var loaded = (await planning.GetProjectPlansAsync("contoso")).Single();
        loaded.Tasks.Select(task => task.Key).Should().Equal(new[] { "task-a", "task-b" }, (a, b) => a == b, "keys are stable across saves");
        var task = loaded.Tasks[1];
        task.Predecessors.Should().Equal("task-a");
        task.ChangeRequests.Should().Equal(7);
        task.Controls.Should().Equal("CA-100");
        task.Notes.Should().Be("Report-only first");
        task.PinnedStart.Should().Be(start.AddDays(1));
        task.ScheduleRank.Should().Be(1);
        task.Status.Should().Be("Staged");

        await planning.AddTaskEventAsync(new EngineeringWorkspace.Application.Planning.ProjectTaskEvent(0, again.Id!.Value, "task-b", "Manual – attested", "GPO linked", null,
            "gpo.zip", "tester", DateTimeOffset.UtcNow), [1, 2, 3]);
        (await planning.GetTaskEventsAsync(again.Id!.Value, "task-b")).Should().ContainSingle().Which.AttachmentName.Should().Be("gpo.zip");
        await using var connection = connections.CreateSqlite(status.Settings, allowCreate: false);
        await connection.OpenAsync();
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE \"secureplus.ProjectTaskEvent\" SET Summary = 'changed';";
        var change = () => update.ExecuteNonQueryAsync();
        await change.Should().ThrowAsync<global::Microsoft.Data.Sqlite.SqliteException>("task history is append-only");
    }

    [Fact]
    public async Task Catalog_control_table_has_no_licensing_column()
    {
        var (connections, status) = await DatabaseAsync();
        await using var connection = connections.CreateSqlite(status.Settings, allowCreate: false);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('secureplus.CatalogControl') WHERE name = 'Licensing';";
        Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(0);
    }

    [Fact]
    public async Task Tenant_inventory_keeps_first_seen_marks_removed_and_leaves_unread_collections_alone()
    {
        var (connections, status) = await DatabaseAsync();
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        var repository = new SqlTenantInventoryRepository(status, connections, clock);
        var tenant = Guid.NewGuid();
        EngineeringWorkspace.Application.Tenants.TenantUser User(string id) =>
            new(tenant, id, $"User {id}", $"{id}@contoso.com", null, true, "Member", false, "IT", "Engineer", "US", ["SPE_E5"], [], true, ["fido2"], null, null);
        EngineeringWorkspace.Application.Tenants.TenantGroup Group(string id, long? count) => new(tenant, id, $"Group {id}", null, null, "Security", false, null, false, false, count);
        var device = new EngineeringWorkspace.Application.Tenants.TenantDevice(tenant, "d1", "IPAD-01", "iPadOS", "18.1", "compliant", "company", true, "u1@contoso.com", null, null, null, null, null);

        var first = await repository.SaveAsync("contoso", new(tenant, clock.GetUtcNow(), [User("u1"), User("u2")], [Group("g1", 2), Group("g2", null)],
            [new(tenant, "g1", "u1", "user"), new(tenant, "g1", "u2", "user")], ["g1"], [device]), 1);
        first.Should().Be(new EngineeringWorkspace.Application.Tenants.TenantInventoryCounts(2, 2, 2, 1, 0));

        clock.Advance(TimeSpan.FromDays(7));
        // Quick scan: u2 left the tenant; members of g1 re-read; devices not read (null) must survive.
        var second = await repository.SaveAsync("contoso", new(tenant, clock.GetUtcNow(), [User("u1"), User("u3")], [Group("g1", 1), Group("g2", 5)],
            [new(tenant, "g1", "u1", "user")], ["g1"], null), 2);
        second.Removed.Should().Be(1);

        (await repository.ListUsersAsync("contoso")).Select(user => user.ObjectId).Should().BeEquivalentTo(["u1", "u3"]);
        (await repository.ListGroupsAsync("contoso", tenant)).Single(group => group.ObjectId == "g2").MemberCount.Should().Be(5);
        (await repository.ListMembersAsync(tenant, "g1")).Should().ContainSingle().Which.MemberObjectId.Should().Be("u1");
        (await repository.ListDevicesAsync("contoso")).Should().ContainSingle("devices were not read by the quick scan");

        await using (var connection = connections.CreateSqlite(status.Settings, allowCreate: false))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT ObjectId, FirstSeenUtc, RemovedAtUtc FROM \"secureplus.TenantUser\" ORDER BY ObjectId;";
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<(string Id, string First, string? Removed)>();
            while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            rows.Single(row => row.Id == "u1").First.Should().StartWith("2026-11-01", "first seen is kept across scans");
            rows.Single(row => row.Id == "u2").Removed.Should().StartWith("2026-11-08", "a user who left is kept as removed");
            rows.Single(row => row.Id == "u3").First.Should().StartWith("2026-11-08");
        }

        // A third scan keeps the removed date instead of moving it.
        clock.Advance(TimeSpan.FromDays(1));
        (await repository.SaveAsync("contoso", new(tenant, clock.GetUtcNow(), [User("u1"), User("u3")], null, null, [], null), 3)).Removed.Should().Be(0);

        await repository.DeleteCustomerAsync("contoso");
        (await repository.ListUsersAsync("contoso")).Should().BeEmpty();
        (await repository.ListGroupsAsync("contoso")).Should().BeEmpty();
    }

    [Fact]
    public async Task Lead_edits_survive_catalogue_reloads_reach_the_database_copy_and_keep_append_only_history()
    {
        var (connections, status) = await DatabaseAsync();
        var repository = new SqlCatalogEditRepository(status, connections, new AuditContext("lead@gds"), TimeProvider.System);
        var gds = EngineeringWorkspace.Infrastructure.Catalog.EmbeddedCatalogService.Load();
        var service = new EngineeringWorkspace.Application.Catalog.EditableCatalogService(gds, repository,
            NullLogger<EngineeringWorkspace.Application.Catalog.EditableCatalogService>.Instance);
        var original = gds.Find("CA-100")!;
        var edited = EngineeringWorkspace.Application.Catalog.CatalogControlContent.From(original) with { Name = "Admins: strong auth (Contoso wording)" };
        await repository.SaveControlAsync(new("CA-100", "CA-100", false, edited, EngineeringWorkspace.Application.Catalog.CatalogComposer.Hash(original), 0, "", DateTimeOffset.UtcNow));
        await repository.SaveControlAsync(new("L-0001", null, false, edited with { Name = "Lead control: block legacy kiosks" }, null, 0, "", DateTimeOffset.UtcNow));
        await repository.SaveControlAsync(new("CA-101", "CA-101", true, null, null, 0, "", DateTimeOffset.UtcNow));
        await repository.SavePolicySetAsync(new("L-SET-RETAIL", null, false, "Retail stores", "Shared iPads", 90, ["CA-100", "L-0001"], [], null, 0, "", DateTimeOffset.UtcNow));
        await service.RefreshAsync();

        var store = new SqlCatalogStore(status, connections, TimeProvider.System);
        await store.SyncAsync(service.Catalog, force: false, null);
        await store.SyncAsync(service.Catalog, force: true, null); // a reload

        var edits = await repository.LoadAsync();
        edits.Controls.Should().HaveCount(3, "a catalogue reload never touches Lead edits");
        edits.PolicySets.Should().ContainSingle();
        service.Catalog.Find("CA-100")!.Name.Should().Be("Admins: strong auth (Contoso wording)");
        service.Catalog.Find("CA-101").Should().BeNull();
        service.Catalog.FindPolicySet("L-SET-RETAIL")!.ControlIds.Should().Equal("CA-100", "L-0001");

        await using var connection = connections.CreateSqlite(status.Settings, allowCreate: false);
        await connection.OpenAsync();
        async Task<long> Count(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        (await Count("SELECT COUNT(*) FROM \"secureplus.CatalogControl\" WHERE ControlId = 'L-0001';")).Should().Be(1, "the database copy reflects Lead controls");
        (await Count("SELECT COUNT(*) FROM \"secureplus.CatalogControl\" WHERE ControlId = 'CA-101';")).Should().Be(0);
        (await Count("SELECT COUNT(*) FROM \"secureplus.CatalogEditHistory\";")).Should().Be(4);
        await using var tamper = connection.CreateCommand();
        tamper.CommandText = "DELETE FROM \"secureplus.CatalogEditHistory\";";
        await ((Func<Task>)(() => tamper.ExecuteNonQueryAsync())).Should().ThrowAsync<global::Microsoft.Data.Sqlite.SqliteException>();

        // Removing the edit brings the GDS version back.
        await repository.DeleteControlAsync("CA-101");
        await service.RefreshAsync();
        service.Catalog.Find("CA-101").Should().NotBeNull();
        (await repository.HistoryAsync("CA-101")).Select(entry => entry.Action).Should().Equal("Deleted", "Hidden");
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    public void Dispose()
    {
        global::Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _root.Delete(recursive: true);
    }

    private sealed class Status(DatabaseSettings settings, DatabaseStatusReport report) : IDatabaseStatusService
    {
        public DatabaseSettings Settings => settings;
        public DatabaseStatusReport? LatestStatus => report;
        public event EventHandler<DatabaseStatusReport>? StatusChanged { add { } remove { } }
        public void Publish(DatabaseStatusReport value) { }
        public Task<DatabaseStatusReport> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(report);
        public Task<OperationResult<string>> SaveSettingsAsync(DatabaseSettings value, CancellationToken cancellationToken = default) => Task.FromResult(OperationResult<string>.Success("unused"));
    }
}
