using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Configuration;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Configuration.Loading;
using EngineeringWorkspace.Infrastructure.Auditing;
using EngineeringWorkspace.Infrastructure.Database;
using EngineeringWorkspace.Infrastructure.Persistence;
using EngineeringWorkspace.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EngineeringWorkspace.Infrastructure.Tests;

public sealed class SettingsDatabaseTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("secureplus-settings-");

    [Fact]
    public async Task Ini_settings_are_imported_once_then_loaded_from_the_database_as_shared_and_computer_layers()
    {
        var paths = new PathSettings(_root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName, _root.FullName,
            _root.FullName, Path.Combine(_root.FullName, "audit"), _root.FullName, _root.FullName, _root.FullName);
        var context = new AuditContext("tester");
        var redaction = new RedactionService();
        var connections = new DatabaseConnectionFactory(new UnavailableSecretStore(), paths);
        var provisioning = new DatabaseProvisioningService(connections, new DatabaseErrorTranslator(redaction),
            new JsonLinesAuditService(paths.AuditDirectory, context, redaction, TimeProvider.System, NullLogger<JsonLinesAuditService>.Instance),
            context, new ApplicationSettings("id", "name", "title", "company", "Test", "dashboard/overview", false, false, "1.0.0"), TimeProvider.System,
            NullLogger<DatabaseProvisioningService>.Instance);
        var database = new DatabaseSettings { Provider = DatabaseProvider.SQLite, DatabaseName = Path.Combine(_root.FullName, "secureplus.db"), CommandTimeoutSeconds = 30 };
        await provisioning.CreateDatabaseAsync(database, null, CancellationToken.None);
        (await provisioning.InitializeSchemaAsync(database, null, CancellationToken.None)).State.Should().Be(DatabaseOverallState.Ready);

        // A user's configuration: one changed value, one unchanged default, one per-computer value and one secret.
        var config = Directory.CreateDirectory(Path.Combine(_root.FullName, "config")).FullName;
        var defaults = Directory.CreateDirectory(Path.Combine(config, "Defaults")).FullName;
        await File.WriteAllTextAsync(Path.Combine(defaults, "Branding.ini"), "[Branding]\r\nApplicationName=Secure Plus Agent\r\nCompanyName=Global Data Systems\r\n");
        await File.WriteAllTextAsync(Path.Combine(config, "Branding.ini"), "[Branding]\r\nApplicationName=Contoso Agent\r\nCompanyName=Global Data Systems\r\n");
        await File.WriteAllTextAsync(Path.Combine(config, "Logging.ini"), "[Logging]\r\nMinimumLevel=Debug\r\n");
        await File.WriteAllTextAsync(Path.Combine(config, "Integrations.ini"), "[Integrations:ConnectWise]\r\nClientSecret=do-not-import\r\n");
        await File.WriteAllTextAsync(Path.Combine(config, "Database.ini"), "[Database]\r\nProvider=SQLite\r\n");

        var store = new SqlSettingsStore(connections, "PC-01");
        var result = await SettingsImport.ImportAsync(store, database, config, defaults, _root.FullName, redaction, "tester", CancellationToken.None);

        result.Imported.Should().Be(2, "only the changed branding value and the logging value differ from the defaults; secrets are never imported");
        File.Exists(Path.Combine(config, "Branding.ini")).Should().BeFalse("imported files move to the backup folder");
        File.Exists(Path.Combine(config, "Database.ini")).Should().BeTrue("the connection stays local");
        File.Exists(Path.Combine(result.BackupFolder!, "Branding.ini")).Should().BeTrue();

        var loaded = await store.TryLoadAsync(database, CancellationToken.None);
        loaded!.Shared.Should().ContainKey("Branding:ApplicationName").WhoseValue.Should().Be("Contoso Agent");
        loaded.Shared.Should().NotContainKey("Integrations:ConnectWise:ClientSecret");
        loaded.Computer.Should().ContainKey("Logging:MinimumLevel").WhoseValue.Should().Be("Debug");
        loaded.Properties.Should().ContainKey(SettingsImport.PropertyPrefix + "PC-01");
        (await new SqlSettingsStore(connections, "PC-02").TryLoadAsync(database, CancellationToken.None))!.Computer.Should().BeEmpty("per-computer values belong to PC-01 only");

        // The loader layers the database values above the shipped defaults.
        var snapshot = ConfigurationLoader.Load(new ConfigurationLoadRequest(_root.FullName, _root.FullName, config, [], new Dictionary<string, string?>(), null, null, "1.0.0",
            defaults, loaded.Shared, loaded.Computer), redaction).Snapshot;
        snapshot.Branding.ApplicationName.Should().Be("Contoso Agent");
        snapshot.Logging.MinimumLevel.Should().Be("Debug");
        snapshot.Entries.Single(entry => entry.Key == "Branding:ApplicationName").Layer.Should().Be("Shared settings");
    }

    public void Dispose()
    {
        global::Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _root.Delete(recursive: true);
    }
}
