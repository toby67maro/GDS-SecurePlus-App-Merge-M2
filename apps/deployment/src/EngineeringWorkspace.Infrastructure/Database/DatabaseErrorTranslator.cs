using System.ComponentModel;
using System.Security.Authentication;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Security;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Identity.Client;

namespace EngineeringWorkspace.Infrastructure.Database;

/// <summary>Database failure translated to category, UI-safe message, safe detail and recommended action.</summary>
public sealed record DatabaseFailure(DatabaseFailureCategory Category, string Message, string? Detail, string RecommendedAction);

/// <summary>Translates provider exceptions. Full exceptions go to the log (redacted); the UI never sees stack traces.</summary>
public sealed class DatabaseErrorTranslator(IRedactionService redaction)
{
    public enum Operation
    {
        Connect,
        CreateDatabase,
        CreateSchema
    }

    public DatabaseFailure Translate(Exception exception, Operation operation)
    {
        switch (exception)
        {
            case DatabaseCredentialUnavailableException credential:
                return new(DatabaseFailureCategory.CredentialUnavailable, "The database credential is not available.", credential.Message, "Save the credential in Credential Management and select its reference.");
            case MsalServiceException msal:
                return new(DatabaseFailureCategory.LoginFailed, "Microsoft Entra rejected the database sign-in.", Safe($"{msal.ErrorCode}: {FirstLine(msal.Message)}"), "Check the client ID, tenant, credential and the database user mapped to this identity.");
            case MsalClientException msalClient:
                return new(DatabaseFailureCategory.LoginFailed, "Microsoft Entra sign-in did not complete.", Safe($"{msalClient.ErrorCode}: {FirstLine(msalClient.Message)}"), "Retry the sign-in or check the Entra settings.");
            case SqlException sql:
                return TranslateSql(sql, operation);
            case SqliteException sqlite:
                return sqlite.SqliteErrorCode switch
                {
                    14 => new(DatabaseFailureCategory.DatabaseMissing, "The SQLite database file does not exist or cannot be opened.", Safe(sqlite.Message), "Check the path, or use Create Database."),
                    3 or 8 => new(DatabaseFailureCategory.NoAccessToDatabase, "The SQLite database file is read-only or access was denied.", Safe(sqlite.Message), "Check file permissions."),
                    26 => new(DatabaseFailureCategory.SchemaIncompatible, "The file is not a SQLite database.", null, "Choose another file name."),
                    _ => new(operation == Operation.CreateSchema ? DatabaseFailureCategory.CannotCreateSchemaObjects : DatabaseFailureCategory.Unexpected,
                        "The SQLite operation failed.", Safe(sqlite.Message), "Review the log for details.")
                };
            case TimeoutException:
                return new(DatabaseFailureCategory.NetworkOrTimeout, "The database operation timed out.", null, "Check the network and increase the timeout if the server is slow.");
            case ArgumentException argument when operation == Operation.Connect:
                return new(DatabaseFailureCategory.Configuration, "The connection settings are not valid.", Safe(argument.Message), "Review the database configuration.");
            case UnauthorizedAccessException or IOException:
                return new(DatabaseFailureCategory.NoAccessToDatabase, "The database file or directory cannot be accessed.", exception.GetType().Name, "Check file and directory permissions.");
            default:
                return new(DatabaseFailureCategory.Unexpected, "The database operation failed unexpectedly.", exception.GetType().Name, "Review the application log for details.");
        }
    }

    private DatabaseFailure TranslateSql(SqlException exception, Operation operation)
    {
        if (exception.InnerException is AuthenticationException || exception.InnerException is Win32Exception { NativeErrorCode: -2146893019 or -2146762487 } ||
            exception.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
            return new(DatabaseFailureCategory.EncryptionOrCertificate, "The encrypted connection could not be established (certificate or TLS failure).", Safe(FirstLine(exception.Message)),
                "Install a trusted server certificate, or for development only enable Trust Server Certificate.");

        var detail = Safe($"SQL error {exception.Number}: {FirstLine(exception.Message)}");
        if (exception.Number == 0 && exception.Message.Contains("network-related or instance-specific", StringComparison.OrdinalIgnoreCase))
            return new(DatabaseFailureCategory.CannotReachServer, "SQL Server could not be reached.", detail, "Check the server name, instance, port, firewall and that the SQL Server service and TCP/IP are running.");
        return exception.Number switch
        {
            2 or 40 or 53 or 64 or 233 or 1225 or 10053 or 10054 or 10060 or 10061 or 11001 or 26 =>
                new(DatabaseFailureCategory.CannotReachServer, "SQL Server could not be reached.", detail, "Check the server name, instance, port, firewall and that the SQL Server service and TCP/IP are running."),
            -2 or 121 or 258 => new(DatabaseFailureCategory.NetworkOrTimeout, "The connection to SQL Server timed out.", detail, "Check the network path and increase the connection timeout."),
            18456 or 18452 or 18470 or 18486 or 18487 or 18488 => new(DatabaseFailureCategory.LoginFailed, "SQL Server rejected the login.", detail, "Check the authentication method, user name and stored credential."),
            4060 => new(DatabaseFailureCategory.NoAccessToDatabase, "The login cannot open the database (it does not exist or access is denied).", detail, "Run Check Database, or grant the login access to the database."),
            916 or 4063 => new(DatabaseFailureCategory.NoAccessToDatabase, "The login has no access to the database.", detail, "Create a database user for the login and grant it access."),
            5170 when operation == Operation.CreateDatabase =>
                new(DatabaseFailureCategory.CannotCreateDatabase, "The database files of an earlier SecurePlus database are still on the server.", detail,
                    "The database was removed but its .mdf/.ldf files were left in the SQL Server data folder (for example after a detach or when SQL Server " +
                    "was stopped). Attach them again in SQL Server Management Studio to keep the data, or move or delete the old SecurePlus.mdf and " +
                    "SecurePlus_log.ldf files, then select Create Database again."),
            262 or 1802 or 5133 or 40544 when operation == Operation.CreateDatabase =>
                new(DatabaseFailureCategory.CannotCreateDatabase, "The SQL account can connect but cannot create the database.", detail, "Grant CREATE DATABASE (SQL Server) or the dbmanager role (Azure SQL), or ask a DBA to create it."),
            262 or 229 or 230 or 2760 or 15247 or 15151 or 1088 or 297 => new(DatabaseFailureCategory.CannotCreateSchemaObjects, "The SQL account cannot create the schema objects.", detail,
                "Grant db_owner or CREATE SCHEMA/CREATE TABLE/ALTER permissions on the database."),
            1222 => new(DatabaseFailureCategory.NetworkOrTimeout, "Another session holds the schema migration lock.", detail, "Wait for the other initialization to finish, then retry."),
            _ => new(operation switch
            {
                Operation.CreateDatabase => DatabaseFailureCategory.CannotCreateDatabase,
                Operation.CreateSchema => DatabaseFailureCategory.CannotCreateSchemaObjects,
                _ => DatabaseFailureCategory.Unexpected
            }, "SQL Server returned an error.", detail, "Review the details and the application log.")
        };
    }

    private string Safe(string? text)
    {
        var redacted = redaction.RedactText(text);
        return redacted.Length > 400 ? redacted[..400] : redacted;
    }

    private static string FirstLine(string text) => text.Split('\n')[0].Trim();
}
