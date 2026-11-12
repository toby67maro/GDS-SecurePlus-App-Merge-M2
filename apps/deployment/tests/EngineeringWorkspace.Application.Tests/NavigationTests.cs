using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Features;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Application.Workspaces;
using EngineeringWorkspace.Domain.Auditing;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Customers;
using EngineeringWorkspace.Domain.Tenants;
using EngineeringWorkspace.Domain.Workspaces;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EngineeringWorkspace.Application.Tests;

public sealed class NavigationTests
{
    [Fact]
    public async Task Navigation_is_sorted_and_rejects_unknown_routes()
    {
        var definition = new NavigationDefinition(
        [
            new NavigationNodeDefinition { Kind = NavigationNodeKind.Section, Id = "section", DisplayName = "Section", SortOrder = 100 },
            new NavigationNodeDefinition { Kind = NavigationNodeKind.Item, Id = "b", ParentId = "section", DisplayName = "B", Route = "b", SortOrder = 20 },
            new NavigationNodeDefinition { Kind = NavigationNodeKind.Item, Id = "a", ParentId = "section", DisplayName = "A", Route = "a", SortOrder = 10 }
        ]);
        var routes = new RouteRegistry(
        [
            new RouteDescriptor("a", "a", "A", "A", "page.test", null, false, false, true, "M2", "Section"),
            new RouteDescriptor("b", "b", "B", "B", "page.test", null, false, false, true, "M2", "Section")
        ]);
        var service = new NavigationService(definition, routes, new FeatureService(new FeatureSettings(new Dictionary<string, bool>())),
            new NoWorkspace(), new NoCustomer(), new NoAudit(),
            new ApplicationSettings("id", "name", "title", "company", "Development", "a", false, false, "1.0.0"), NullLogger<NavigationService>.Instance);

        NavigationResolver.Items(service.Sections).Select(item => item.Route).Should().ContainInOrder("a", "b");
        (await service.NavigateAsync("unknown")).Succeeded.Should().BeFalse();
        (await service.NavigateAsync("a")).Succeeded.Should().BeTrue();
        service.CurrentRoute!.Route.Should().Be("a");
    }

    private sealed class NoAudit : IAuditService
    {
        public Task RecordAsync(string eventType, AuditOutcome outcome, string summary, IReadOnlyDictionary<string, string?>? properties = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class NoWorkspace : IWorkspaceService
    {
        public Workspace? CurrentWorkspace => null;
        public bool HasActiveWorkspace => false;
        public event EventHandler<WorkspaceContextChanged>? WorkspaceChanged { add { } remove { } }
        public Task<OperationResult<Workspace>> OpenAsync(WorkspaceId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<OperationResult<Workspace>> CreateAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OperationResult<Workspace?>> RestoreLastAsync(CancellationToken cancellationToken = default) => Task.FromResult(OperationResult<Workspace?>.Success(null));
        public Task<OperationResult> SaveAsync(CancellationToken cancellationToken = default) => Task.FromResult(OperationResult.Success());
        public Task<OperationResult> SetCurrentCustomerAsync(CustomerId? customerId, Guid? tenantId, CancellationToken cancellationToken = default) => Task.FromResult(OperationResult.Success());
    }

    private sealed class NoCustomer : ICustomerContextService
    {
        public CustomerProfile? CurrentCustomer => null;
        public TenantConfiguration? CurrentTenant => null;
        public event EventHandler<CustomerContextChanged>? CustomerChanged { add { } remove { } }
        public Task<OperationResult<CustomerProfile>> SelectAsync(CustomerId id, string? tenantKey, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearAsync(string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Refresh(CustomerProfile profile) { }
    }
}
