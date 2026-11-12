using System.Text.RegularExpressions;

namespace EngineeringWorkspace.Application.Database;

/// <summary>A database configuration problem tied to a Database.ini key.</summary>
public sealed record DatabaseValidationError(string Key, string Message, string? ExpectedFormat = null);

/// <summary>Validates database settings. Provider-specific; never touches the network.</summary>
public static partial class DatabaseConfigurationValidator
{
    public static IReadOnlyList<DatabaseValidationError> Validate(DatabaseSettings settings)
    {
        var errors = new List<DatabaseValidationError>();
        if (settings.Provider == DatabaseProvider.None) return errors;

        if (settings.Provider == DatabaseProvider.SQLite)
        {
            if (string.IsNullOrWhiteSpace(settings.DatabaseName))
                errors.Add(new("DatabaseName", "A SQLite database file path is required.", "A file path such as ./data/Data/secureplus.db"));
            else if (settings.DatabaseName.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                errors.Add(new("DatabaseName", "The SQLite database path contains invalid characters."));
            return errors;
        }

        if (string.IsNullOrWhiteSpace(settings.ServerName))
            errors.Add(new("ServerName", "A server name is required.", "Host name, for example localhost or myserver.database.windows.net"));
        else if (!ServerNamePattern().IsMatch(settings.ServerName))
            errors.Add(new("ServerName", "The server name contains unsupported characters.", "Letters, digits, '.', '-' and '_' (use SqlInstance and Port for instance and port)"));
        if (!string.IsNullOrWhiteSpace(settings.SqlInstance) && !InstancePattern().IsMatch(settings.SqlInstance))
            errors.Add(new("SqlInstance", "The SQL instance name contains unsupported characters.", "Letters, digits, '_' or '$' (max 16)"));
        if (settings.Port is < 1 or > 65535)
            errors.Add(new("Port", "The port must be between 1 and 65535."));
        if (string.IsNullOrWhiteSpace(settings.DatabaseName))
            errors.Add(new("DatabaseName", "A database name is required."));
        else if (!IsValidDatabaseName(settings.DatabaseName))
            errors.Add(new("DatabaseName", "The database name is not a valid identifier.", "Start with a letter or '_', then letters, digits or '_' (max 128)"));

        if (!DatabaseAuthenticationMethods.For(settings.Provider).Contains(settings.Authentication))
            errors.Add(new("Authentication", $"'{settings.Authentication}' is not supported for {settings.Provider}.",
                null));

        switch (settings.Authentication)
        {
            case DatabaseAuthenticationMethod.SqlLogin:
                if (string.IsNullOrWhiteSpace(settings.UserName)) errors.Add(new("UserName", "A SQL login user name is required."));
                if (settings.PasswordSecretReference is null)
                    errors.Add(new("PasswordSecretReference", "Save the SQL password through Credential Management and reference it here.", "os:database/<name>/sql-login"));
                break;
            case DatabaseAuthenticationMethod.EntraInteractive:
                if (!Guid.TryParse(settings.EntraClientId, out _))
                    errors.Add(new("EntraClientId", "Interactive Microsoft Entra sign-in needs the client ID of a public client app registration.", "GUID"));
                break;
            case DatabaseAuthenticationMethod.EntraServicePrincipal:
                if (!Guid.TryParse(settings.EntraClientId, out _)) errors.Add(new("EntraClientId", "The service principal client ID is required.", "GUID"));
                if (!Guid.TryParse(settings.EntraTenantId, out _)) errors.Add(new("EntraTenantId", "The tenant ID of the service principal is required.", "GUID"));
                if (settings.EntraClientSecretReference is null) errors.Add(new("EntraClientSecretReference", "Save the service principal secret through Credential Management and reference it here."));
                break;
            case DatabaseAuthenticationMethod.EntraManagedIdentity:
                if (!string.IsNullOrWhiteSpace(settings.ManagedIdentityClientId) && !Guid.TryParse(settings.ManagedIdentityClientId, out _))
                    errors.Add(new("ManagedIdentityClientId", "The user-assigned managed identity client ID must be a GUID.", "GUID or empty for system-assigned"));
                break;
        }

        if (settings.ConnectionTimeoutSeconds is < 1 or > 300) errors.Add(new("ConnectionTimeoutSeconds", "The connection timeout must be between 1 and 300 seconds."));
        if (settings.CommandTimeoutSeconds is < 1 or > 3600) errors.Add(new("CommandTimeoutSeconds", "The command timeout must be between 1 and 3600 seconds."));
        if (settings.RetryCount is < 0 or > 255) errors.Add(new("RetryCount", "The connect retry count must be between 0 and 255."));
        if (settings.RetryDelaySeconds is < 1 or > 60) errors.Add(new("RetryDelaySeconds", "The connect retry delay must be between 1 and 60 seconds."));
        return errors;
    }

    /// <summary>Strict identifier rule for database names; everything else is rejected before SQL is built.</summary>
    public static bool IsValidDatabaseName(string? name) => name is not null && DatabaseNamePattern().IsMatch(name);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9._\-]{1,253}$", RegexOptions.CultureInvariant)]
    private static partial Regex ServerNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_$]{1,16}$", RegexOptions.CultureInvariant)]
    private static partial Regex InstancePattern();
}
