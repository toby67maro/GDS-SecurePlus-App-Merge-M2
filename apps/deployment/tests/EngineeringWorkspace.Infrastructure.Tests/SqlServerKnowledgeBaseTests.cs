using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Credentials;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Domain.Security;
using EngineeringWorkspace.Infrastructure.Auditing;
using EngineeringWorkspace.Infrastructure.Catalog;
using EngineeringWorkspace.Infrastructure.Database;
using EngineeringWorkspace.Infrastructure.KnowledgeBase;
using EngineeringWorkspace.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace EngineeringWorkspace.Infrastructure.Tests;

/// <summary>
/// Loads the catalog and both knowledge-base phases into a real SQL Server, whose default collation ignores case and trailing
/// spaces (SQLite does not). Opt-in: set SECUREPLUS_SQLSERVER_TEST_SERVER to a connection string for a server where the
/// login may create databases (for example "Server=localhost,1433;User ID=sa;Password=...;TrustServerCertificate=True").
/// The test creates a database with a unique name and drops it afterwards.
/// </summary>
public sealed class SqlServerKnowledgeBaseTests
{
    private const string Variable = "SECUREPLUS_SQLSERVER_TEST_SERVER";

    [SqlServerFact]
    public async Task Catalog_and_knowledge_base_load_and_reload_on_SQL_Server()
    {
        var server = Environment.GetEnvironmentVariable(Variable)!;

        var root = Directory.CreateTempSubdirectory("secureplus-sqlserver-");
        var name = $"SecurePlusTest_{Guid.NewGuid():N}";
        var builder = new SqlConnectionStringBuilder(server);
        var dataSource = builder.DataSource.Split(',');
        var reference = new SecretReference(SecretReference.OperatingSystemProvider, "database/test/sql-login");
        var secrets = new MemorySecrets();
        await secrets.SetSecretAsync(reference, builder.Password);
        var paths = new PathSettings(root.FullName, root.FullName, root.FullName, root.FullName, root.FullName, root.FullName, root.FullName, root.FullName,
            root.FullName, Path.Combine(root.FullName, "audit"), root.FullName, root.FullName, root.FullName);
        var context = new AuditContext("tester");
        var redaction = new RedactionService();
        var connections = new DatabaseConnectionFactory(secrets, paths);
        var provisioning = new DatabaseProvisioningService(connections, new DatabaseErrorTranslator(redaction),
            new JsonLinesAuditService(paths.AuditDirectory, context, redaction, TimeProvider.System, NullLogger<JsonLinesAuditService>.Instance),
            context, new ApplicationSettings("id", "name", "title", "company", "Test", "dashboard/overview", false, false, "1.0.0"),
            TimeProvider.System, NullLogger<DatabaseProvisioningService>.Instance);
        var settings = new DatabaseSettings
        {
            Provider = DatabaseProvider.SqlServer, ServerName = dataSource[0], Port = dataSource.Length > 1 ? int.Parse(dataSource[1]) : null, DatabaseName = name,
            Authentication = DatabaseAuthenticationMethod.SqlLogin, UserName = builder.UserID, PasswordSecretReference = reference,
            TrustServerCertificate = builder.TrustServerCertificate, CommandTimeoutSeconds = 600
        };

        try
        {
            var created = await provisioning.CreateDatabaseAsync(settings, null, CancellationToken.None);
            created.FailureCategory.Should().Be(DatabaseFailureCategory.None, created.SanitizedError);
            var status = await provisioning.InitializeSchemaAsync(settings, null, CancellationToken.None);
            status.State.Should().Be(DatabaseOverallState.Ready, status.SanitizedError);
            var fixedStatus = new FixedStatus(settings, status);

            await new SqlCatalogStore(fixedStatus, connections, TimeProvider.System).SyncAsync(EmbeddedCatalogService.Load(), force: false, null);
            var store = new EngineeringWorkspace.Application.KnowledgeBase.CompositeKnowledgeBaseStore(
                [new SqlKnowledgeBaseStore(fixedStatus, connections, TimeProvider.System), new SqlBaselineKnowledgeStore(fixedStatus, connections, TimeProvider.System),
                 new SqlPhase3KnowledgeStore(fixedStatus, connections, TimeProvider.System)]);
            var kb = EmbeddedKnowledgeBase.Current;
            var baselines = EmbeddedKnowledgeBase.Baselines;

            (await store.LoadAsync(force: false, null)).Skipped.Should().BeFalse();
            (await store.GetStateAsync()).IsCurrent.Should().BeTrue();
            var requirements = await ScalarAsync(server, name, "SELECT COUNT(*) FROM fw.Requirement;");
            (await ScalarAsync(server, name, "SELECT COUNT(*) FROM fw.RequirementMap m JOIN ref.MappingSource s ON s.MappingSourceId = m.MappingSourceId WHERE s.Code = 'SCF-STRM';"))
                .Should().Be(kb.Count("maps"));
            (await ScalarAsync(server, name, "SELECT COUNT(*) FROM threat.ControlThreat;")).Should().Be(kb.Count("controlThreats"));
            (await ScalarAsync(server, name, "SELECT COUNT(*) FROM cfg.Recommendation;")).Should().Be(baselines.Count("recommendations"));
            (await ScalarAsync(server, name, "SELECT COUNT(*) FROM cfg.Setting;")).Should().Be(baselines.Count("settings"));
            (await ScalarAsync(server, name, "SELECT COUNT(*) FROM cfg.SettingAlias;")).Should().Be(baselines.Count("settingAliases"));
            (await ScalarAsync(server, name, "SELECT COUNT(*) FROM eux.Question;")).Should().Be(baselines.Count("questions"));
            (await ScalarAsync(server, name, "SELECT COUNT(*) FROM kb.ConfigurationChange;")).Should().Be(EmbeddedKnowledgeBase.Phase3.Count("configurationChanges"));
            (await ScalarAsync(server, name, "SELECT COUNT(*) FROM score.EnrichmentValue;")).Should().BeGreaterThan(25000);

            // Phase 4 on SQL Server: a completed run is published to the EaaS tables.
            await new SqlReferenceSeedStore(fixedStatus, connections, TimeProvider.System).SeedAsync(EmbeddedCatalogService.Load(), false, null);
            var repository = new SqlEngagementRepository(fixedStatus, connections, new AuditContext("tester"), TimeProvider.System);
            var customer = EngineeringWorkspace.Domain.Customers.CustomerProfile.Create(new EngineeringWorkspace.Domain.Common.CustomerId("contoso"), "Contoso");
            await repository.UpsertCustomerAsync(customer, isSample: false);
            var run = await repository.StartAssessmentAsync(customer.Id, null, "Engine run", null);
            await repository.CompleteAssessmentAsync(run, EngineeringWorkspace.Application.Assessments.AssessmentStatusCodes.Completed, 50m,
                [new("CA-100", "Identity", "Admin strong authentication", "Pass", "High", "Policy on", null), new("CA-101", "Identity", "Trusted device", "Fail", "High", null, null)]);
            repository.LastEaasPublishError.Should().BeNull();
            (await ScalarAsync(server, name, "SELECT COUNT(*) FROM cust.Finding;")).Should().Be(1);
            (await ScalarAsync(server, name, "SELECT COUNT(*) FROM audit.SnapshotItem;")).Should().Be(2);

            // 3.0 planning data on SQL Server.
            var planning = new SqlPlanningRepository(fixedStatus, connections);
            (await planning.GetWorkBreakdownAsync()).Tasks.Should().NotBeEmpty();
            (await planning.GetThreatCoverageAsync()).Should().Contain(cell => cell.Covered > 0);
            var setting = (await ScalarTextAsync(server, name, "SELECT TOP 1 AliasValue FROM cfg.SettingAlias WHERE AliasTypeCode = 'REGISTRY';"))!;
            (await planning.FindSettingsAsync([setting])).Should().NotBeEmpty();
            await planning.SaveSowTemplateAsync(EngineeringWorkspace.Application.Planning.SowTemplates.BuiltIn[0]);
            (await planning.GetSowTemplatesAsync()).Should().ContainSingle();
            var start = new DateOnly(2026, 11, 9);
            var plan = await planning.SaveProjectPlanAsync("contoso", new EngineeringWorkspace.Application.Planning.ProjectPlan(null, "Plan", start, start.AddDays(5), "Draft",
            [
                new("a", "1", "Design", "Plan", "Architect", null, start, start, 1, 8, 0, false, "NotStarted", "High", null, []),
                new("b", "2", "Build", "Build", "Engineer", "Sam", start.AddDays(1), start.AddDays(3), 2.5, 20, 10.5, false, "InProgress", null, null, ["a"])
            ]));
            var loaded = (await planning.GetProjectPlansAsync("contoso")).Single();
            loaded.Tasks.Should().HaveCount(2);
            loaded.Tasks[1].Predecessors.Should().ContainSingle();
            loaded.Tasks[1].Start.Should().Be(start.AddDays(1));
            await planning.DeleteProjectPlanAsync("contoso", plan.Id!.Value);

            (await store.LoadAsync(force: true, null)).Skipped.Should().BeFalse();
            (await ScalarAsync(server, name, "SELECT COUNT(*) FROM fw.Requirement;")).Should().Be(requirements, "a reload updates in place");
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await using (var connection = new SqlConnection(new SqlConnectionStringBuilder(server) { InitialCatalog = "master" }.ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"IF DB_ID('{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END";
                await command.ExecuteNonQueryAsync();
            }

            root.Delete(recursive: true);
        }
    }

    private static async Task<string?> ScalarTextAsync(string server, string database, string sql)
    {
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(server) { InitialCatalog = database }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> ScalarAsync(string server, string database, string sql)
    {
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(server) { InitialCatalog = database }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>Runs only when <see cref="Variable"/> is set.</summary>
    private sealed class SqlServerFactAttribute : FactAttribute
    {
        public SqlServerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
                Skip = $"Opt-in: set {Variable} to a SQL Server connection string to load the knowledge base on SQL Server.";
        }
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _secrets = [];
        public string DisplayName => "memory";
        public bool SupportsProvider(string provider) => true;
        public Task<string?> GetSecretAsync(SecretReference reference, CancellationToken cancellationToken = default) => Task.FromResult(_secrets.GetValueOrDefault(reference.ToString()));

        public Task SetSecretAsync(SecretReference reference, string secret, CancellationToken cancellationToken = default)
        {
            _secrets[reference.ToString()] = secret;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteSecretAsync(SecretReference reference, CancellationToken cancellationToken = default) => Task.FromResult(_secrets.Remove(reference.ToString()));
        public Task<bool> SecretExistsAsync(SecretReference reference, CancellationToken cancellationToken = default) => Task.FromResult(_secrets.ContainsKey(reference.ToString()));
    }

    private sealed class FixedStatus(DatabaseSettings settings, DatabaseStatusReport report) : IDatabaseStatusService
    {
        public DatabaseSettings Settings => settings;
        public DatabaseStatusReport? LatestStatus => report;
        public event EventHandler<DatabaseStatusReport>? StatusChanged { add { } remove { } }
        public void Publish(DatabaseStatusReport value) { }
        public Task<DatabaseStatusReport> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(report);
        public Task<OperationResult<string>> SaveSettingsAsync(DatabaseSettings value, CancellationToken cancellationToken = default) => Task.FromResult(OperationResult<string>.Success("unused"));
    }
}
