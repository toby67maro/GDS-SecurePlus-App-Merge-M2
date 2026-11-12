using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit.Sdk;

namespace EngineeringWorkspace.Infrastructure.Tests;

public sealed class ExternalDatabaseIntegrationTests
{
    [Fact(Skip = "Opt-in live validation: run this test explicitly after configuring SECUREPLUS_SQLSERVER_CONNECTION_STRING.")]
    public async Task SqlServer_connection_is_validated_when_configured()
    {
        await ValidateOptionalConnectionAsync(
            "SECUREPLUS_SQLSERVER_CONNECTION_STRING",
            "SQL Server live integration test skipped because SECUREPLUS_SQLSERVER_CONNECTION_STRING is not set.");
    }

    [Fact(Skip = "Opt-in live validation: run this test explicitly after configuring SECUREPLUS_AZURESQL_CONNECTION_STRING.")]
    public async Task AzureSql_connection_is_validated_when_configured()
    {
        await ValidateOptionalConnectionAsync(
            "SECUREPLUS_AZURESQL_CONNECTION_STRING",
            "Azure SQL live integration test skipped because SECUREPLUS_AZURESQL_CONNECTION_STRING is not set.");
    }

    private static async Task ValidateOptionalConnectionAsync(string variableName, string skipMessage)
    {
        var connectionString = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw SkipException.ForSkip(skipMessage);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1;";
        (await command.ExecuteScalarAsync()).Should().Be(1);
    }
}
