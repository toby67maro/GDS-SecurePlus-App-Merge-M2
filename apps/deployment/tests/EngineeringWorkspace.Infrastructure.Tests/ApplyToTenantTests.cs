using System.Reflection;
using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Governance;
using EngineeringWorkspace.Application.Jobs;
using EngineeringWorkspace.Application.Tenants;
using EngineeringWorkspace.Domain.Auditing;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Customers;
using EngineeringWorkspace.Domain.Security;
using EngineeringWorkspace.Domain.Tenants;
using EngineeringWorkspace.Infrastructure.Catalog;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EngineeringWorkspace.Infrastructure.Tests;

public sealed class ApplyToTenantTests
{
    [Fact]
    public async Task Approved_changes_are_merged_filled_with_variables_and_created()
    {
        var catalog = EmbeddedCatalogService.Load();
        var conditionalAccess = catalog.Controls.Where(control => control.Kind == CatalogKinds.ConditionalAccess && control.Body.Length > 0 &&
                                                                  !control.Body.Contains("TermsOfUse", StringComparison.Ordinal) && !control.Body.Contains("GsaSecurity", StringComparison.Ordinal))
            .Take(2).ToArray();
        var folder = catalog.Controls.Where(control => control.GraphResource == "deviceManagement/configurationPolicies" && control.Kind == CatalogKinds.Intune)
            .GroupBy(control => control.Path[..control.Path.LastIndexOf('/')]).First(group => group.Count() >= 3).Take(3).ToArray();
        var termsOfUse = catalog.Controls.FirstOrDefault(control => control.Body.Contains("<TermsOfUseId>", StringComparison.Ordinal));
        conditionalAccess.Should().HaveCount(2);

        // The plan: CA one by one (report-only), the Intune folder merged into one settings catalog policy.
        var units = DeploymentPlanner.Build([.. conditionalAccess, .. folder]);
        units.Should().HaveCount(3);
        var merged = units.Single(unit => unit.Controls.Count == 3);
        merged.Body.Should().Contain("<RING>-");
        System.Text.Json.JsonDocument.Parse(merged.Body).RootElement.GetProperty("settings").GetArrayLength().Should().BeGreaterThanOrEqualTo(3);
        units.Where(unit => unit.Kind == CatalogKinds.ConditionalAccess).Should().OnlyContain(unit => unit.Body.Contains("enabledForReportingButNotEnforced"));

        var tenant = new TenantConfiguration("primary", "Contoso", "contoso.onmicrosoft.com", Guid.NewGuid(), "contoso.com", CloudEnvironment.Commercial,
            new ApplicationRegistration(Guid.NewGuid(), null, TenantAuthenticationType.ClientSecret, new SecretReference(SecretReference.OperatingSystemProvider, "tenant/contoso"),
                null, null, [TenantCapability.TenantDiscovery]))
        {
            DeploymentRegistration = new ApplicationRegistration(Guid.NewGuid(), null, TenantAuthenticationType.Certificate,
                new SecretReference(SecretReference.OperatingSystemProvider, "customer/contoso/primary/deploy-certificate"), "THUMB", DateTimeOffset.UtcNow.AddDays(300),
                PermissionValidationService.DeployCapabilities)
        };
        var variables = new Dictionary<string, string>
        {
            ["RING"] = "Pilot", ["AdministratorGroup"] = "SG-Admins", ["EmergencyAccessAccountsGroup"] = Guid.NewGuid().ToString(), ["ExclusionTempGroup"] = "SG-Temp",
            ["ExclusionPermGroup"] = "SG-Perm", ["SynchronizationServiceAccountsGroup"] = "SG-Sync", ["AADP2Group"] = "SG-P2", [DeploymentVariables.PolicyNamePrefix] = "GDS - "
        };
        var profile = DeploymentVariables.Save(CustomerProfile.Create(new CustomerId("contoso"), "Contoso") with { Tenants = [tenant] }, variables);
        var existingName = "GDS - " + ChangeExecutionService.DisplayName(DeploymentVariables.Apply(units[1].Body, variables))!.Replace("GDS - ", string.Empty, StringComparison.Ordinal);
        var writer = new FakeWriter(existingName);
        var audit = new FakeAudit();
        var assessments = new FakeAssessments();
        var items = conditionalAccess.Concat(folder).Select(control => new ProfileItem("Control", control.Id)).ToList();
        if (termsOfUse is not null) items.Add(new ProfileItem("Control", termsOfUse.Id));
        var now = DateTimeOffset.UtcNow;
        var request = new ChangeRequestRecord(7, "contoso", "Deploy", null, "Medium", null, null, "tester", "approver", null, null, null, ChangeStatusCodes.Approved, null,
            now, now, now, null, items);
        var service = new ChangeExecutionService(new FakeRepository(), new FixedCatalog(catalog), new FakeProfiles(profile), assessments, Stub<IEngagementRepository>(),
            new AuditContext("tester"), TimeProvider.System, NullLogger<ChangeExecutionService>.Instance, writer, audit);

        var result = await service.ApplyToTenantAsync(request, null, CancellationToken.None);

        result.Status.Should().Be(ExecutionStatusCodes.Succeeded, result.Log);
        result.Steps.Should().Contain(step => step.Name == "Deployment access" && step.Status == StepStatus.Succeeded);
        writer.Created.Should().HaveCount(2, "one CA policy already exists by name");
        writer.Created.Should().OnlyContain(item => !item.Body.Contains("<RING>") && item.Body.Contains("\"GDS - ") && item.Body.Contains("Pilot"));
        writer.Created.Select(item => item.Body).Should().Contain(body => body.Contains("group-SG-Admins") || body.Contains("group-SG-Temp"), "group names are resolved to IDs");
        writer.Created.Should().Contain(item => item.Resource == "deviceManagement/configurationPolicies");
        result.Steps.Should().Contain(step => step.Status == StepStatus.Skipped && step.Detail.Contains("already exists"));
        if (termsOfUse is not null) result.Steps.Should().Contain(step => step.Status == StepStatus.Manual && step.Detail.Contains("TermsOfUseId"));
        audit.Events.Should().HaveCount(2).And.OnlyContain(e => e == AuditEventTypes.TenantChangeApplied);
        assessments.Runs.Should().Be(1, "the tenant is re-assessed after a change");
        result.ReassessmentRunId.Should().Be(42);
    }

    [Fact]
    public async Task Applying_changes_needs_the_deploy_registration()
    {
        var catalog = new EmbeddedCatalogService().Catalog;
        var control = catalog.Controls.First(item => item.Kind == CatalogKinds.ConditionalAccess);
        var tenant = new TenantConfiguration("primary", "Contoso", "contoso.onmicrosoft.com", Guid.NewGuid(), "contoso.com", CloudEnvironment.Commercial,
            new ApplicationRegistration(Guid.NewGuid(), null, TenantAuthenticationType.Certificate, new SecretReference(SecretReference.OperatingSystemProvider, "customer/contoso/primary/graph-certificate"),
                "THUMB", null, PermissionValidationService.ReadCapabilities));
        var profile = CustomerProfile.Create(new CustomerId("contoso"), "Contoso") with { Tenants = [tenant] };
        var writer = new FakeWriter("none");
        var now = DateTimeOffset.UtcNow;
        var request = new ChangeRequestRecord(8, "contoso", "Deploy", null, "Medium", null, null, "tester", "approver", null, null, null, ChangeStatusCodes.Approved, null,
            now, now, now, null, [new ProfileItem("Control", control.Id)]);
        var service = new ChangeExecutionService(new FakeRepository(), new FixedCatalog(catalog), new FakeProfiles(profile), new FakeAssessments(), Stub<IEngagementRepository>(),
            new AuditContext("tester"), TimeProvider.System, NullLogger<ChangeExecutionService>.Instance, writer, new FakeAudit());

        var result = await service.ApplyToTenantAsync(request, null, CancellationToken.None);

        result.Status.Should().NotBe(ExecutionStatusCodes.Succeeded);
        result.Steps.Should().Contain(step => step.Name == "Deployment access" && step.Status == StepStatus.Failed);
        writer.Created.Should().BeEmpty("the Read registration never writes");
    }

    [Fact]
    public void Read_and_deploy_registrations_split_the_permissions()
    {
        var permissions = new PermissionValidationService();
        // 4.0 beta.2: the Read registration holds only read permissions; write permissions are the Deploy registration's alone.
        var read = permissions.RequiredApplicationPermissions(PermissionValidationService.ReadCapabilities);
        read.Should().NotContain(permission => permission.Contains("ReadWrite", StringComparison.Ordinal));
        permissions.RequiredApplicationPermissions([TenantCapability.TenantDiscovery]).Should().BeEquivalentTo(read, "older registrations are checked against the read set");
        var deploy = permissions.RequiredApplicationPermissions(PermissionValidationService.DeployCapabilities);
        deploy.Should().Contain(["Policy.ReadWrite.ConditionalAccess", "DeviceManagementConfiguration.ReadWrite.All", "DeviceManagementApps.ReadWrite.All"]);
        deploy.Where(permission => permission.Contains("ReadWrite", StringComparison.Ordinal)).Should().HaveCount(4);
        deploy.Except(["Policy.Read.All", "Group.Read.All", "Organization.Read.All"]).Should().OnlyContain(permission => permission.Contains("ReadWrite", StringComparison.Ordinal),
            "the Deploy registration reads only what the writer needs");
        ChangeExecutionService.CreateBody("""{"id":"1","name":"Policy","createdDateTime":"x","settings":[]}""").Should().NotContain("\"id\"").And.NotContain("createdDateTime");
        ChangeExecutionService.DisplayName("""{"displayName":"CA001"}""").Should().Be("CA001");
    }

    private static T Stub<T>() where T : class => DispatchProxy.Create<T, DefaultProxy>();

    public class DefaultProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var type = targetMethod!.ReturnType;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [value]);
            }

            return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
        }
    }

    private sealed class FixedCatalog(ControlCatalog catalog) : ICatalogService
    {
        public ControlCatalog Catalog => catalog;
    }

    private sealed class FakeWriter(string existingName) : ITenantChangeWriter
    {
        public List<(string Resource, string Body)> Created { get; } = [];

        public Task<IReadOnlySet<string>> ExistingNamesAsync(TenantConfiguration tenant, string apiVersion, string resource, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { existingName });

        public Task<string?> ResolveGroupIdAsync(TenantConfiguration tenant, string displayName, CancellationToken cancellationToken) =>
            Task.FromResult<string?>($"group-{displayName}");

        public Task<TenantWriteResult> CreateAsync(TenantConfiguration tenant, string apiVersion, string resource, string jsonBody, CancellationToken cancellationToken)
        {
            Created.Add((resource, jsonBody));
            return Task.FromResult(TenantWriteResult.Created("new-id"));
        }
    }

    private sealed class FakeAudit : IAuditService
    {
        public List<string> Events { get; } = [];

        public Task RecordAsync(string eventType, AuditOutcome outcome, string summary, IReadOnlyDictionary<string, string?>? properties = null, CancellationToken cancellationToken = default)
        {
            Events.Add(eventType);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAssessments : IAssessmentService
    {
        public int Runs { get; private set; }
        public event EventHandler<AssessmentCompletedEventArgs>? Completed { add { } remove { } }
        public JobSnapshot Queue(CustomerId customerId, int? pendingRunId = null, string? reason = null) => throw new NotSupportedException();
        public IReadOnlyList<JobSnapshot> QueueMany(IEnumerable<CustomerId> customerIds, string? reason = null) => throw new NotSupportedException();
        public Task<int> QueuePendingAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<AssessmentRunOutcome> RunAsync(AssessmentRunRequest request, IProgress<AssessmentProgressUpdate>? progress, CancellationToken cancellationToken)
        {
            Runs++;
            return Task.FromResult(new AssessmentRunOutcome(request.CustomerId, 42, AssessmentStatusCodes.Completed, 90m, 10, 1, "Assessed.", null));
        }
    }

    private sealed class FakeProfiles(CustomerProfile profile) : ICustomerProfileStore
    {
        public Task<IReadOnlyList<CustomerProfile>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CustomerProfile>>([profile]);
        public Task<CustomerProfile?> LoadAsync(CustomerId id, CancellationToken cancellationToken = default) => Task.FromResult<CustomerProfile?>(profile);
        public Task SaveAsync(CustomerProfile value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeRepository : IChangeRequestRepository
    {
        public bool IsAvailable => true;
        public Task<IReadOnlyList<ChangeRequestRecord>> ListChangeRequestsAsync(string? customerKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ChangeRequestRecord>>([]);
        public Task<ChangeRequestRecord> SaveChangeRequestAsync(ChangeRequestRecord record, CancellationToken cancellationToken = default) => Task.FromResult(record);
        public Task DeleteChangeRequestAsync(int changeRequestId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ChangeExecutionRecord>> ListExecutionsAsync(int? changeRequestId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ChangeExecutionRecord>>([]);
        public Task<int> StartExecutionAsync(int changeRequestId, string mode, string actor, CancellationToken cancellationToken = default) => Task.FromResult(1);
        public Task CompleteExecutionAsync(int executionId, string status, string log, int? reassessmentRunId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
