using FluentAssertions;
using NetArchTest.Rules;
using EngineeringWorkspace.Domain.Workspaces;

namespace EngineeringWorkspace.Architecture.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Domain_does_not_depend_on_application_or_infrastructure()
    {
        var result = Types.InAssembly(typeof(Workspace).Assembly)
            .ShouldNot()
            .HaveDependencyOn("EngineeringWorkspace.Application")
            .GetResult();
        result.IsSuccessful.Should().BeTrue();
        Types.InAssembly(typeof(Workspace).Assembly)
            .ShouldNot()
            .HaveDependencyOn("EngineeringWorkspace.Infrastructure")
            .GetResult()
            .IsSuccessful.Should().BeTrue();
    }
}
