using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Infrastructure.Auditing;
using EngineeringWorkspace.Infrastructure.Database;
using EngineeringWorkspace.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EngineeringWorkspace.Infrastructure.Tests;

/// <summary>3.2 install types: the installer's install.json is read, and a Workstation never creates or upgrades the database.</summary>
public sealed class InstallProfileTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("secureplus-install-");

    [Fact]
    public void The_installer_profile_is_read_and_a_missing_file_means_not_recorded()
    {
        var path = Path.Combine(_root.FullName, "install.json");
        // Exactly what the Inno Setup [Code] section writes (backslashes escaped).
        File.WriteAllText(path, "{\r\n  \"type\": \"Workstation\",\r\n  \"sqlServer\": \"sql01\\\\SECUREPLUS\",\r\n  \"databaseName\": \"SecurePlus\"\r\n}\r\n");
        var profile = InstallProfile.Load(path);
        profile.Should().Be(new InstallProfile(InstallType.Workstation, "sql01\\SECUREPLUS", "SecurePlus"));
        profile.CanProvision.Should().BeFalse();

        File.WriteAllText(path, "{ \"type\": \"AllInOne\", \"sqlServer\": \" \", \"databaseName\": \"\" }");
        InstallProfile.Load(path).Should().Be(new InstallProfile(InstallType.AllInOne, null, null));
        InstallProfile.Load(Path.Combine(_root.FullName, "missing.json")).Should().Be(InstallProfile.None);
        File.WriteAllText(path, "not json");
        InstallProfile.Load(path).CanProvision.Should().BeTrue("an unreadable profile behaves like an install before 3.2");
    }

    [Fact]
    public async Task A_workstation_never_creates_or_upgrades_the_database()
    {
        var paths = new PathSettings(_root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName,
            _root.FullName, Path.Combine(_root.FullName, "audit"), _root.FullName, _root.FullName, _root.FullName);
        var context = new AuditContext("tester");
        var redaction = new RedactionService();
        var connections = new DatabaseConnectionFactory(new UnavailableSecretStore(), paths);
        var file = new JsonLinesAuditService(paths.AuditDirectory, context, redaction, TimeProvider.System, NullLogger<JsonLinesAuditService>.Instance);
        var application = new ApplicationSettings("id", "name", "title", "company", "Test", "dashboard/overview", false, false, "3.2.0");
        DatabaseProvisioningService Provisioning(InstallType type) => new(connections, new DatabaseErrorTranslator(redaction), file, context, application, TimeProvider.System,
            NullLogger<DatabaseProvisioningService>.Instance, new InstallProfile(type, "sql01", "SecurePlus"));
        var settings = new DatabaseSettings { Provider = DatabaseProvider.SQLite, DatabaseName = Path.Combine(_root.FullName, "secureplus.db"), CommandTimeoutSeconds = 30 };

        var workstation = Provisioning(InstallType.Workstation);
        var created = await workstation.CreateDatabaseAsync(settings, null, CancellationToken.None);
        created.SanitizedError.Should().Be(InstallProfile.WorkstationRefusal);
        File.Exists(settings.DatabaseName).Should().BeFalse();

        // The server install creates it; the workstation then uses it but cannot repair or upgrade it.
        var server = Provisioning(InstallType.Server);
        await server.CreateDatabaseAsync(settings, null, CancellationToken.None);
        (await server.InitializeSchemaAsync(settings, null, CancellationToken.None)).State.Should().Be(DatabaseOverallState.Ready);
        (await workstation.GetStatusAsync(settings, null, CancellationToken.None)).State.Should().Be(DatabaseOverallState.Ready);
        (await workstation.RepairSchemaAsync(settings, null, CancellationToken.None)).SanitizedError.Should().NotBe(InstallProfile.WorkstationRefusal,
            "a complete schema needs no repair");
    }

    public void Dispose()
    {
        global::Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _root.Delete(recursive: true);
    }
}
