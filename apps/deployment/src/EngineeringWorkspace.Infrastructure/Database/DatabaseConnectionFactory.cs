using System.Collections.Concurrent;
using System.Data.Common;
using System.Security;
using EngineeringWorkspace.Application.Credentials;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Domain.Security;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.AppConfig;

namespace EngineeringWorkspace.Infrastructure.Database;

/// <summary>Raised when a credential the connection needs is not available. Never contains the secret.</summary>
public sealed class DatabaseCredentialUnavailableException(string message) : Exception(message);

/// <summary>
/// Builds provider connections from settings. Passwords travel as <see cref="SqlCredential"/>, never in the
/// connection string. Microsoft Entra methods use MSAL through SqlClient's access-token callback, which takes the
/// resource and authority from the server, so no Azure SQL endpoint is hard-coded.
/// </summary>
public sealed class DatabaseConnectionFactory(ISecretStore secrets, PathSettings paths)
{
    private readonly ConcurrentDictionary<string, IPublicClientApplication> _interactiveApps = new(StringComparer.Ordinal);

    public string ResolveSqlitePath(DatabaseSettings settings) =>
        Path.GetFullPath(Path.IsPathRooted(settings.DatabaseName) ? settings.DatabaseName : Path.Combine(paths.DataDirectory, settings.DatabaseName));

    /// <summary>Creates an unopened SQLite connection; <paramref name="allowCreate"/> is true only for Create Database.</summary>
    public SqliteConnection CreateSqlite(DatabaseSettings settings, bool allowCreate) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = ResolveSqlitePath(settings),
            Mode = allowCreate ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = settings.CommandTimeoutSeconds
        }.ToString());

    /// <summary>Creates an unopened SQL Server / Azure SQL connection to the application database or to master.</summary>
    public async Task<SqlConnection> CreateSqlServerAsync(DatabaseSettings settings, bool master, CancellationToken cancellationToken)
    {
        SqlConnectionStringBuilder builder;
        if (settings.ConnectionStringSecretReference is { } connectionStringReference)
        {
            var connectionString = await RequireSecretAsync(connectionStringReference, "connection string", cancellationToken);
            builder = new SqlConnectionStringBuilder(connectionString);
        }
        else
        {
            builder = new SqlConnectionStringBuilder
            {
                DataSource = settings.DisplayServer,
                Encrypt = settings.EncryptConnection ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
                TrustServerCertificate = settings.TrustServerCertificate,
                IntegratedSecurity = settings.Authentication == DatabaseAuthenticationMethod.WindowsIntegrated
            };
        }

        builder.InitialCatalog = master ? "master" : settings.DatabaseName;
        builder.ConnectTimeout = settings.ConnectionTimeoutSeconds;
        builder.CommandTimeout = settings.CommandTimeoutSeconds;
        builder.ConnectRetryCount = settings.RetryCount;
        builder.ConnectRetryInterval = settings.RetryDelaySeconds;
        builder.ApplicationName = "Secure Plus Agent";
        if (settings.ConnectionStringSecretReference is not null) return new SqlConnection(builder.ConnectionString);

        switch (settings.Authentication)
        {
            case DatabaseAuthenticationMethod.SqlLogin:
            {
                var password = await RequireSecretAsync(settings.PasswordSecretReference, "SQL login password", cancellationToken);
                var secure = new SecureString();
                foreach (var character in password) secure.AppendChar(character);
                secure.MakeReadOnly();
                return new SqlConnection(builder.ConnectionString, new SqlCredential(settings.UserName!, secure));
            }
            case DatabaseAuthenticationMethod.EntraInteractive:
            case DatabaseAuthenticationMethod.EntraServicePrincipal:
            case DatabaseAuthenticationMethod.EntraManagedIdentity:
            {
                var connection = new SqlConnection(builder.ConnectionString);
                connection.AccessTokenCallback = (parameters, token) => AcquireEntraTokenAsync(settings, parameters, token);
                return connection;
            }
            default:
                return new SqlConnection(builder.ConnectionString);
        }
    }

    private async Task<SqlAuthenticationToken> AcquireEntraTokenAsync(DatabaseSettings settings, SqlAuthenticationParameters parameters, CancellationToken cancellationToken)
    {
        var scopes = new[] { parameters.Resource.TrimEnd('/') + "/.default" };
        AuthenticationResult result;
        switch (settings.Authentication)
        {
            case DatabaseAuthenticationMethod.EntraServicePrincipal:
            {
                var secret = await RequireSecretAsync(settings.EntraClientSecretReference, "service principal secret", cancellationToken);
                var app = ConfidentialClientApplicationBuilder.Create(settings.EntraClientId).WithClientSecret(secret)
                    .WithAuthority(parameters.Authority).Build();
                result = await app.AcquireTokenForClient(scopes).ExecuteAsync(cancellationToken);
                break;
            }
            case DatabaseAuthenticationMethod.EntraManagedIdentity:
            {
                var identity = string.IsNullOrWhiteSpace(settings.ManagedIdentityClientId)
                    ? ManagedIdentityId.SystemAssigned
                    : ManagedIdentityId.WithUserAssignedClientId(settings.ManagedIdentityClientId);
                var app = ManagedIdentityApplicationBuilder.Create(identity).Build();
                result = await app.AcquireTokenForManagedIdentity(parameters.Resource).ExecuteAsync(cancellationToken);
                break;
            }
            default:
            {
                var app = _interactiveApps.GetOrAdd($"{settings.EntraClientId}|{parameters.Authority}", _ =>
                    PublicClientApplicationBuilder.Create(settings.EntraClientId).WithAuthority(parameters.Authority).WithRedirectUri("http://localhost").Build());
                var account = (await app.GetAccountsAsync()).FirstOrDefault();
                try
                {
                    if (account is null) throw new MsalUiRequiredException("no_account", "Interactive sign-in is required.");
                    result = await app.AcquireTokenSilent(scopes, account).ExecuteAsync(cancellationToken);
                }
                catch (MsalUiRequiredException)
                {
                    result = await app.AcquireTokenInteractive(scopes).WithPrompt(Prompt.SelectAccount).ExecuteAsync(cancellationToken);
                }

                break;
            }
        }

        return new SqlAuthenticationToken(result.AccessToken, result.ExpiresOn);
    }

    private async Task<string> RequireSecretAsync(SecretReference? reference, string description, CancellationToken cancellationToken)
    {
        if (reference is null) throw new DatabaseCredentialUnavailableException($"No secret reference is configured for the {description}.");
        var value = await secrets.GetSecretAsync(reference, cancellationToken);
        return string.IsNullOrEmpty(value)
            ? throw new DatabaseCredentialUnavailableException($"The {description} ({reference}) is not stored on this workstation. Save it in Credential Management.")
            : value;
    }

    /// <summary>Creates a parameter in a provider-neutral way.</summary>
    public static DbParameter Parameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
        return parameter;
    }
}
