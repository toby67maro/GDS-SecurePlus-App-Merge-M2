using System.Globalization;
using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Configuration;
using EngineeringWorkspace.Domain.Auditing;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Application.Database;

/// <summary>
/// Holds the current database settings and the latest status for the shell. Status checks at startup are
/// read-only and asynchronous; nothing here creates or alters a database.
/// </summary>
public interface IDatabaseStatusService
{
    DatabaseSettings Settings { get; }
    DatabaseStatusReport? LatestStatus { get; }
    event EventHandler<DatabaseStatusReport>? StatusChanged;
    void Publish(DatabaseStatusReport report);
    Task<DatabaseStatusReport> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists non-secret settings to Database.ini and makes them current for this session.</summary>
    Task<OperationResult<string>> SaveSettingsAsync(DatabaseSettings settings, CancellationToken cancellationToken = default);
}

public sealed class DatabaseStatusService(
    DatabaseSettings initialSettings,
    IDatabaseProvisioningService provisioning,
    IConfigurationFileWriter writer,
    IAuditService audit,
    ILogger<DatabaseStatusService> logger) : IDatabaseStatusService
{
    public const string FileName = "Database.ini";
    private DatabaseStatusReport? _latest;

    public DatabaseSettings Settings { get; private set; } = initialSettings;
    public DatabaseStatusReport? LatestStatus => _latest;
    public event EventHandler<DatabaseStatusReport>? StatusChanged;

    public void Publish(DatabaseStatusReport report)
    {
        _latest = report;
        StatusChanged?.Invoke(this, report);
    }

    public async Task<DatabaseStatusReport> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var report = await provisioning.GetStatusAsync(Settings, null, cancellationToken);
        Publish(report);
        return report;
    }

    public async Task<OperationResult<string>> SaveSettingsAsync(DatabaseSettings settings, CancellationToken cancellationToken = default)
    {
        var errors = DatabaseConfigurationValidator.Validate(settings);
        if (errors.Count > 0)
            return OperationResult<string>.Failure("DatabaseConfigurationInvalid", "The database settings are not valid and were not saved.",
                string.Join(" ", errors.Select(error => $"{error.Key}: {error.Message}")), "Correct the highlighted fields.");

        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Provider"] = settings.Provider.ToString(),
            ["ServerName"] = settings.ServerName,
            ["SqlInstance"] = settings.SqlInstance,
            ["Port"] = settings.Port?.ToString(CultureInfo.InvariantCulture),
            ["DatabaseName"] = settings.DatabaseName,
            ["Authentication"] = settings.Authentication.ToString(),
            ["UserName"] = settings.UserName,
            ["PasswordSecretReference"] = settings.PasswordSecretReference?.ToString(),
            ["EntraClientId"] = settings.EntraClientId,
            ["EntraTenantId"] = settings.EntraTenantId,
            ["EntraClientSecretReference"] = settings.EntraClientSecretReference?.ToString(),
            ["ManagedIdentityClientId"] = settings.ManagedIdentityClientId,
            ["EncryptConnection"] = Bool(settings.EncryptConnection),
            ["TrustServerCertificate"] = Bool(settings.TrustServerCertificate),
            ["ConnectionTimeoutSeconds"] = settings.ConnectionTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            ["CommandTimeoutSeconds"] = settings.CommandTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            ["RetryCount"] = settings.RetryCount.ToString(CultureInfo.InvariantCulture),
            ["RetryDelaySeconds"] = settings.RetryDelaySeconds.ToString(CultureInfo.InvariantCulture),
            ["ConnectionStringSecretReference"] = settings.ConnectionStringSecretReference?.ToString(),
            ["AutomaticDatabaseInitialization"] = Bool(settings.AutomaticDatabaseInitialization),
            ["AutomaticSchemaUpgrade"] = Bool(settings.AutomaticSchemaUpgrade)
        };
        var result = await writer.WriteSectionAsync(FileName, "Database", values, cancellationToken);
        if (!result.Succeeded) return result;

        Settings = settings;
        logger.LogInformation("Database settings saved to {Path} (provider {Provider})", result.Value, settings.Provider);
        await audit.RecordAsync(AuditEventTypes.DatabaseConfigurationChanged, AuditOutcome.Success, "Database configuration saved.",
            new Dictionary<string, string?>
            {
                ["Provider"] = settings.Provider.ToString(),
                ["Server"] = settings.DisplayServer,
                ["Database"] = settings.DatabaseName,
                ["Authentication"] = settings.Authentication.ToString(),
                ["File"] = result.Value
            }, cancellationToken);
        return result;
    }

    private static string Bool(bool value) => value ? "true" : "false";
}
