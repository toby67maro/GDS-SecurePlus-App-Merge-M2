using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Domain.Security;
using FluentAssertions;

namespace EngineeringWorkspace.Configuration.Tests;

public sealed class ConnectionValidationTests
{
    [Fact]
    public void Sql_password_requires_a_secret_reference()
    {
        var result = DatabaseConfigurationValidator.Validate(new DatabaseSettings
        {
            Provider = DatabaseProvider.SqlServer,
            ServerName = "server",
            DatabaseName = "database",
            Authentication = DatabaseAuthenticationMethod.SqlLogin
        });

        result.Select(error => error.Message).Should().Contain("Save the SQL password through Credential Management and reference it here.");
    }

    [Theory]
    [InlineData("secureplus;DROP DATABASE master")]
    [InlineData("secureplus]")]
    public void Sql_database_names_reject_executable_identifier_content(string databaseName)
    {
        var result = DatabaseConfigurationValidator.Validate(new DatabaseSettings
        {
            Provider = DatabaseProvider.SqlServer,
            ServerName = "server",
            DatabaseName = databaseName,
            Authentication = DatabaseAuthenticationMethod.WindowsIntegrated
        });

        result.Select(error => error.Message).Should().Contain("The database name is not a valid identifier.");
    }

    [Fact]
    public void Secret_references_reject_path_traversal()
    {
        SecretReference.TryParse("os:../password", out _).Should().BeFalse();
        SecretReference.TryParse("os:customer/../password", out _).Should().BeFalse();
    }
}
