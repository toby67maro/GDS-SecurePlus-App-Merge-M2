using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Infrastructure.Auditing;
using EngineeringWorkspace.Infrastructure.Database;
using EngineeringWorkspace.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EngineeringWorkspace.Infrastructure.Tests;

public sealed class SQLiteDatabaseConnectionTests
{
    [Fact]
    public async Task SQLite_workflow_reports_connection_schema_missing_then_ready()
    {
        var root = Directory.CreateTempSubdirectory("secureplus-state-");
        try
        {
            var paths = new PathSettings(root.FullName, root.FullName, root.FullName, root.FullName, root.FullName, root.FullName, root.FullName, root.FullName,
                root.FullName, Path.Combine(root.FullName, "audit"), root.FullName, root.FullName, root.FullName);
            var context = new AuditContext("test");
            var redaction = new RedactionService();
            var service = new DatabaseProvisioningService(
                new DatabaseConnectionFactory(new UnavailableSecretStore(), paths),
                new DatabaseErrorTranslator(redaction),
                new JsonLinesAuditService(paths.AuditDirectory, context, redaction, TimeProvider.System, NullLogger<JsonLinesAuditService>.Instance),
                context,
                new ApplicationSettings("id", "name", "title", "company", "Test", "dashboard/overview", false, false, "1.0.0"),
                TimeProvider.System,
                NullLogger<DatabaseProvisioningService>.Instance);
            var options = new DatabaseSettings
            {
                Provider = DatabaseProvider.SQLite,
                DatabaseName = Path.Combine(root.FullName, "secureplus.db"),
                CommandTimeoutSeconds = 30
            };

            var connected = await service.CreateDatabaseAsync(options, null, CancellationToken.None);
            var missing = await service.ValidateSchemaAsync(options, null, CancellationToken.None);
            var initialized = await service.InitializeSchemaAsync(options, null, CancellationToken.None);
            var current = await service.ValidateSchemaAsync(options, null, CancellationToken.None);

            connected.DatabaseAccessCheck.Should().Be(CheckState.Passed);
            missing.State.Should().Be(DatabaseOverallState.SchemaMissing);
            initialized.State.Should().Be(DatabaseOverallState.Ready);
            current.CurrentSchemaVersion.Should().Be(service.RequiredSchemaVersion);
            current.OverallCheck.Should().Be(CheckState.Passed);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
