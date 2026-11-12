using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Credentials;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Domain.Auditing;
using EngineeringWorkspace.Domain.Security;
using EngineeringWorkspace.Infrastructure.Auditing;
using EngineeringWorkspace.Infrastructure.Database;
using EngineeringWorkspace.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace EngineeringWorkspace.Infrastructure.Tests;

/// <summary>
/// 3.2 (V0008) on a real SQL Server 2022: the audit ledger is append-only, the secureplus_* roles deny tenant credentials to
/// Sales, NOC and Manager, users and roles follow the Lead rules, and only the designated computer runs schedules.
/// Opt-in: set SECUREPLUS_SQLSERVER_TEST_SERVER (see <see cref="SqlServerKnowledgeBaseTests"/>).
/// </summary>
public sealed class SqlServerAccessControlTests
{
    private const string Variable = "SECUREPLUS_SQLSERVER_TEST_SERVER";

    [SqlServerFact]
    public async Task Roles_ledger_users_and_scheduling_computer_work_on_SQL_Server_2022()
    {
        var server = Environment.GetEnvironmentVariable(Variable)!;
        var root = Directory.CreateTempSubdirectory("secureplus-access-");
        var name = $"SecurePlusAccess_{Guid.NewGuid():N}";
        var builder = new SqlConnectionStringBuilder(server);
        var dataSource = builder.DataSource.Split(',');
        var reference = new SecretReference(SecretReference.OperatingSystemProvider, "database/test/sql-login");
        var secrets = new MemorySecrets();
        await secrets.SetSecretAsync(reference, builder.Password);
        var paths = new PathSettings(root.FullName, root.FullName, root.FullName, root.FullName, root.FullName, root.FullName, root.FullName, root.FullName,
            root.FullName, Path.Combine(root.FullName, "audit"), root.FullName, root.FullName, root.FullName);
        var context = new AuditContext("GDS\\lead1");
        var redaction = new RedactionService();
        var connections = new DatabaseConnectionFactory(secrets, paths);
        var file = new JsonLinesAuditService(paths.AuditDirectory, context, redaction, TimeProvider.System, NullLogger<JsonLinesAuditService>.Instance);
        var application = new ApplicationSettings("id", "name", "title", "company", "Test", "dashboard/overview", false, false, "3.2.0-test");
        var provisioning = new DatabaseProvisioningService(connections, new DatabaseErrorTranslator(redaction), file, context, application,
            TimeProvider.System, NullLogger<DatabaseProvisioningService>.Instance);
        var settings = new DatabaseSettings
        {
            Provider = DatabaseProvider.SqlServer, ServerName = dataSource[0], Port = dataSource.Length > 1 ? int.Parse(dataSource[1]) : null, DatabaseName = name,
            Authentication = DatabaseAuthenticationMethod.SqlLogin, UserName = builder.UserID, PasswordSecretReference = reference,
            TrustServerCertificate = builder.TrustServerCertificate, CommandTimeoutSeconds = 600
        };

        try
        {
            (await provisioning.CreateDatabaseAsync(settings, null, CancellationToken.None)).FailureCategory.Should().Be(DatabaseFailureCategory.None);
            var status = await provisioning.InitializeSchemaAsync(settings, null, CancellationToken.None);
            status.State.Should().Be(DatabaseOverallState.Ready, status.SanitizedError);
            status.CurrentSchemaVersion.Should().Be(12);
            var fixedStatus = new FixedStatus(settings, status);

            // The ledger is a SQL Server 2022 append-only ledger table and every audit event reaches it.
            (await ScalarAsync(server, name, "SELECT ledger_type FROM sys.tables WHERE name = 'AuditLedger';")).Should().Be(3, "3 = APPEND_ONLY_LEDGER_TABLE");
            var ledger = new LedgerAuditService(file, new Lazy<IDatabaseStatusService>(() => fixedStatus), connections, context, redaction, TimeProvider.System,
                NullLogger<LedgerAuditService>.Instance, "PC-01");
            await ledger.RecordAsync(AuditEventTypes.TenantChangeApplied, AuditOutcome.Success, "Created policy", new Dictionary<string, string?> { ["Tenant"] = "t-1" });
            await ledger.RecordAsync(AuditEventTypes.TenantCredentialUsed, AuditOutcome.Information, "Used");
            (await ScalarAsync(server, name, "SELECT COUNT(*) FROM secureplus.AuditLedger WHERE Category = 'Deployment' AND TenantId = 't-1' AND ComputerName = 'PC-01';")).Should().Be(1);
            (await ScalarAsync(server, name, "SELECT COUNT(*) FROM secureplus.AuditLedger WHERE Category = 'CredentialUse';")).Should().Be(1);
            var update = () => ScalarAsync(server, name, "UPDATE secureplus.AuditLedger SET Summary = 'changed'; SELECT 0;");
            await update.Should().ThrowAsync<SqlException>("even sysadmin cannot update an append-only ledger table");
            var delete = () => ScalarAsync(server, name, "DELETE FROM secureplus.AuditLedger; SELECT 0;");
            await delete.Should().ThrowAsync<SqlException>();

            // Database roles: Sales cannot read tenant credentials, Engineer can; Sales cannot add users.
            await ScalarAsync(server, name, """
                CREATE USER [sales_user] WITHOUT LOGIN; ALTER ROLE [secureplus_sales] ADD MEMBER [sales_user];
                CREATE USER [engineer_user] WITHOUT LOGIN; ALTER ROLE [secureplus_engineer] ADD MEMBER [engineer_user];
                SELECT 0;
                """);
            (await ScalarAsync(server, name, "EXECUTE AS USER = 'engineer_user'; SELECT COUNT(*) FROM secureplus.TenantSecret; REVERT;")).Should().Be(0);
            var salesSecrets = () => ScalarAsync(server, name, "EXECUTE AS USER = 'sales_user'; SELECT COUNT(*) FROM secureplus.TenantSecret;");
            (await salesSecrets.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(229, "permission denied");
            (await ScalarAsync(server, name, "EXECUTE AS USER = 'sales_user'; SELECT COUNT(*) FROM cust.Customer; REVERT;")).Should().Be(0, "Sales reads ordinary tables");
            var salesUser = () => ScalarAsync(server, name,
                "EXECUTE AS USER = 'sales_user'; INSERT INTO secureplus.AppUser (WindowsAccount, RoleCode, CreatedBy, CreatedAtUtc) VALUES ('GDS\\x', 'Lead', 'x', SYSUTCDATETIME());");
            await salesUser.Should().ThrowAsync<SqlException>();
            var engineerLedger = () => ScalarAsync(server, name, "EXECUTE AS USER = 'engineer_user'; DELETE FROM secureplus.AuditLedger;");
            await engineerLedger.Should().ThrowAsync<SqlException>();

            // Users and roles: the first user must be a Lead; afterwards only a Lead changes users; the last Lead stays.
            var lead = new SqlUserDirectory(fixedStatus, connections, ledger, TimeProvider.System, NullLogger<SqlUserDirectory>.Instance, "gds\\lead1");
            (await lead.IsEnforcedAsync()).Should().BeFalse();
            var first = () => lead.SaveAsync(new AppUser("GDS\\eng1", null, "Engineer One", AppRoles.Engineer, true));
            await first.Should().ThrowAsync<InvalidOperationException>().WithMessage("*first user must be an active Lead*");
            await lead.SaveAsync(new AppUser("GDS\\lead1", "lead1@gds.example", "Lead One", AppRoles.Lead, true));
            await lead.SaveAsync(new AppUser("gds\\eng1", null, "Engineer One", AppRoles.Engineer, true));
            (await lead.ListAsync()).Select(user => user.WindowsAccount).Should().BeEquivalentTo(["GDS\\eng1", "GDS\\lead1"]);
            (await lead.CurrentAsync())!.RoleCode.Should().Be(AppRoles.Lead);
            var demote = () => lead.SaveAsync(new AppUser("GDS\\lead1", null, null, AppRoles.Engineer, true));
            await demote.Should().ThrowAsync<InvalidOperationException>().WithMessage("*one active Lead*");
            var engineer = new SqlUserDirectory(fixedStatus, connections, ledger, TimeProvider.System, NullLogger<SqlUserDirectory>.Instance, "GDS\\eng1");
            var notLead = () => engineer.SaveAsync(new AppUser("GDS\\eng2", null, null, AppRoles.Engineer, true));
            await notLead.Should().ThrowAsync<UnauthorizedAccessException>();
            var script = lead.DatabaseAccessScript(new AppUser("GDS\\eng1", null, null, AppRoles.Engineer, true));
            script.Should().Contain("CREATE LOGIN [GDS\\eng1] FROM WINDOWS").And.Contain("ALTER ROLE [secureplus_engineer] ADD MEMBER [GDS\\eng1]")
                .And.Contain("DROP MEMBER [GDS\\eng1]").And.Contain($"USE [{name}]");
            (await ScalarAsync(server, name, "SELECT COUNT(*) FROM secureplus.AuditLedger WHERE Category = 'Access';")).Should().Be(2);

            // Scheduling computer: none designated → any computer; designated → only that one, with a heartbeat.
            var pc1 = new SqlSchedulerHostService(fixedStatus, connections, context, application, TimeProvider.System, "PC-01");
            var pc2 = new SqlSchedulerHostService(fixedStatus, connections, context, application, TimeProvider.System, "PC-02");
            (await pc2.MayRunSchedulesAsync()).Should().BeTrue();
            await pc1.DesignateAsync("PC-01");
            (await pc1.MayRunSchedulesAsync()).Should().BeTrue();
            (await pc2.MayRunSchedulesAsync()).Should().BeFalse();
            (await pc1.GetAsync())!.IsStale(DateTimeOffset.UtcNow).Should().BeTrue("no heartbeat yet");
            await pc2.HeartbeatAsync("Started", "ignored", true);
            await pc1.HeartbeatAsync("Started", "2 schedule(s) started", true);
            var host = (await pc1.GetAsync())!;
            host.IsStale(DateTimeOffset.UtcNow).Should().BeFalse();
            host.LastMessage.Should().Be("2 schedule(s) started");
            host.AppVersion.Should().Be("3.2.0-test");
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

    [SqlServerFact]
    public async Task A_SQLite_database_moves_to_SQL_Server_with_matching_row_counts_and_the_file_unchanged()
    {
        var server = Environment.GetEnvironmentVariable(Variable)!;
        var root = Directory.CreateTempSubdirectory("secureplus-move-");
        var name = $"SecurePlusMove_{Guid.NewGuid():N}";
        var builder = new SqlConnectionStringBuilder(server);
        var dataSource = builder.DataSource.Split(',');
        var reference = new SecretReference(SecretReference.OperatingSystemProvider, "database/test/sql-login");
        var secrets = new MemorySecrets();
        await secrets.SetSecretAsync(reference, builder.Password);
        var paths = new PathSettings(root.FullName, root.FullName, root.FullName, root.FullName, root.FullName, root.FullName, root.FullName, root.FullName,
            root.FullName, Path.Combine(root.FullName, "audit"), root.FullName, root.FullName, root.FullName);
        var context = new AuditContext("GDS\\lead1");
        var redaction = new RedactionService();
        var connections = new DatabaseConnectionFactory(secrets, paths);
        var file = new JsonLinesAuditService(paths.AuditDirectory, context, redaction, TimeProvider.System, NullLogger<JsonLinesAuditService>.Instance);
        var application = new ApplicationSettings("id", "name", "title", "company", "Test", "dashboard/overview", false, false, "3.2.0-test");
        var provisioning = new DatabaseProvisioningService(connections, new DatabaseErrorTranslator(redaction), file, context, application,
            TimeProvider.System, NullLogger<DatabaseProvisioningService>.Instance);
        var sqlite = new DatabaseSettings { Provider = DatabaseProvider.SQLite, DatabaseName = Path.Combine(root.FullName, "secureplus.db"), CommandTimeoutSeconds = 60 };
        var target = new DatabaseSettings
        {
            Provider = DatabaseProvider.SqlServer, ServerName = dataSource[0], Port = dataSource.Length > 1 ? int.Parse(dataSource[1]) : null, DatabaseName = name,
            Authentication = DatabaseAuthenticationMethod.SqlLogin, UserName = builder.UserID, PasswordSecretReference = reference,
            TrustServerCertificate = builder.TrustServerCertificate, CommandTimeoutSeconds = 600
        };

        try
        {
            // A 3.1-style SQLite database with a customer, a completed assessment, an exception, a user and audit entries.
            await provisioning.CreateDatabaseAsync(sqlite, null, CancellationToken.None);
            var sqliteStatus = await provisioning.InitializeSchemaAsync(sqlite, null, CancellationToken.None);
            sqliteStatus.State.Should().Be(DatabaseOverallState.Ready);
            var sqliteDb = new FixedStatus(sqlite, sqliteStatus);
            var repository = new EngineeringWorkspace.Infrastructure.Persistence.SqlEngagementRepository(sqliteDb, connections, context, TimeProvider.System);
            var customer = EngineeringWorkspace.Domain.Customers.CustomerProfile.Create(new EngineeringWorkspace.Domain.Common.CustomerId("contoso"), "Contoso");
            await repository.UpsertCustomerAsync(customer, isSample: false);
            var run = await repository.StartAssessmentAsync(customer.Id, null, "Engine run", null);
            await repository.CompleteAssessmentAsync(run, EngineeringWorkspace.Application.Assessments.AssessmentStatusCodes.Completed, 50m,
                [new("CA-100", "Identity", "Admin strong authentication", "Pass", "High", "Policy on", null), new("CA-101", "Identity", "Trusted device", "Fail", "High", null, null)]);
            var users = new SqlUserDirectory(sqliteDb, connections, file, TimeProvider.System, NullLogger<SqlUserDirectory>.Instance, "GDS\\lead1");
            await users.SaveAsync(new AppUser("GDS\\lead1", null, "Lead", AppRoles.Lead, true));
            var ledger = new LedgerAuditService(file, new Lazy<IDatabaseStatusService>(() => sqliteDb), connections, context, redaction, TimeProvider.System,
                NullLogger<LedgerAuditService>.Instance, "PC-01");
            await ledger.RecordAsync(AuditEventTypes.TenantChangeApplied, AuditOutcome.Success, "Created policy");
            global::Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var before = await File.ReadAllBytesAsync(sqlite.DatabaseName);

            // The server install created an empty database with the current schema.
            (await provisioning.CreateDatabaseAsync(target, null, CancellationToken.None)).FailureCategory.Should().Be(DatabaseFailureCategory.None);
            (await provisioning.InitializeSchemaAsync(target, null, CancellationToken.None)).State.Should().Be(DatabaseOverallState.Ready);

            var mover = new SqliteToSqlServerMover(connections, TimeProvider.System, NullLogger<SqliteToSqlServerMover>.Instance);
            var result = await mover.MoveAsync(sqlite, target, null, CancellationToken.None);
            result.Succeeded.Should().BeTrue(result.Message);
            result.Tables.Should().OnlyContain(table => table.Matches);
            File.Exists(result.BackupPath).Should().BeTrue();
            (await File.ReadAllBytesAsync(sqlite.DatabaseName)).Should().Equal(before, "the SQLite file is never modified");

            (await ScalarAsync(server, name, "SELECT COUNT(*) FROM secureplus.AssessmentResult;")).Should().Be(2);
            (await ScalarAsync(server, name, "SELECT COUNT(*) FROM cust.Customer;")).Should().BeGreaterThan(0);
            (await ScalarAsync(server, name, "SELECT COUNT(*) FROM secureplus.AppUser WHERE RoleCode = 'Lead';")).Should().Be(1);
            (await ScalarAsync(server, name, "SELECT COUNT(*) FROM secureplus.AuditLedger WHERE Category = 'Deployment';")).Should().Be(1);
            (await ScalarAsync(server, name, "SELECT COUNT(*) FROM sys.foreign_keys WHERE is_disabled = 1;")).Should().Be(0, "constraints are re-enabled");

            // The moved database is usable: the repository reads the customer's history on SQL Server.
            var serverStatus = await provisioning.GetStatusAsync(target, null, CancellationToken.None);
            serverStatus.State.Should().Be(DatabaseOverallState.Ready, serverStatus.SanitizedError);
            var moved = new EngineeringWorkspace.Infrastructure.Persistence.SqlEngagementRepository(new FixedStatus(target, serverStatus), connections, context, TimeProvider.System);
            (await moved.StartAssessmentAsync(customer.Id, null, "After move", null)).Should().BeGreaterThan(run, "identities continue after the moved rows");
        }
        finally
        {
            SqlConnection.ClearAllPools();
            global::Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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

    private static async Task<long> ScalarAsync(string server, string database, string sql)
    {
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(server) { InitialCatalog = database, Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed class SqlServerFactAttribute : FactAttribute
    {
        public SqlServerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
                Skip = $"Opt-in: set {Variable} to a SQL Server 2022 connection string.";
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
