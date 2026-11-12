using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Jobs;
using EngineeringWorkspace.Application.Tenants;
using EngineeringWorkspace.Domain.Auditing;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Customers;
using EngineeringWorkspace.Domain.Security;
using EngineeringWorkspace.Domain.Tenants;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EngineeringWorkspace.Application.Tests;

/// <summary>4.0 beta.2: Read and Deploy registrations, certificate credentials (against a fake Microsoft Entra).</summary>
public sealed class TenantRegistrationSplitTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly CustomerId Customer = new("contoso");

    private sealed class FakeEntra : IAppRegistrationService
    {
        public readonly Dictionary<Guid, (CreatedApplication App, HashSet<string> Graph, HashSet<string> Other, int Secrets, int Certificates)> Apps = [];
        public readonly List<string> Calls = [];
        public readonly HashSet<string> Roles = [];

        public string? UnavailableReason => null;
        public IReadOnlyList<string> AdministratorScopes => [];

        public Task<OperationResult<AdministratorSession>> ConnectAdministratorAsync(Guid? tenantId, CloudEnvironment cloud, CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult<AdministratorSession>.Success(new AdministratorSession("s", TenantId, "admin@contoso.com", cloud, [])));

        public Task<OperationResult<TenantOrganization>> GetOrganizationAsync(AdministratorSession session, CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult<TenantOrganization>.Success(new TenantOrganization(TenantId, "Contoso", ["contoso.com"], "contoso.com")));

        public Task<OperationResult<CreatedApplication?>> FindApplicationAsync(AdministratorSession session, Guid? clientId, string displayName, CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult<CreatedApplication?>.Success(Apps.Values.Select(app => app.App)
                .FirstOrDefault(app => clientId is { } id ? app.ClientId == id : app.DisplayName == displayName)));

        public Task<OperationResult<CreatedApplication>> CreateApplicationAsync(AdministratorSession session, string displayName, CancellationToken cancellationToken)
        {
            var app = new CreatedApplication(Guid.NewGuid(), Guid.NewGuid(), displayName);
            Apps[app.ObjectId] = (app, [], [], 0, 0);
            Calls.Add($"create {displayName}");
            return Task.FromResult(OperationResult<CreatedApplication>.Success(app));
        }

        public Task<OperationResult<Guid>> EnsureServicePrincipalAsync(AdministratorSession session, Guid clientId, CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult<Guid>.Success(clientId));

        private Guid ObjectIdFor(Guid servicePrincipalId) => Apps.Values.First(app => app.App.ClientId == servicePrincipalId).App.ObjectId;

        public Task<OperationResult> ConfigureGraphApplicationPermissionsAsync(AdministratorSession session, Guid applicationObjectId, IReadOnlyList<string> permissions,
            CancellationToken cancellationToken)
        {
            Apps[applicationObjectId].Graph.UnionWith(permissions);
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult<IReadOnlyList<string>>> GrantAdminConsentAsync(AdministratorSession session, Guid servicePrincipalId, IReadOnlyList<string> permissions,
            CancellationToken cancellationToken) => Task.FromResult(OperationResult<IReadOnlyList<string>>.Success(permissions));

        public Task<OperationResult<IReadOnlyList<string>>> RevokeGraphPermissionsAsync(AdministratorSession session, Guid applicationObjectId, Guid servicePrincipalId,
            IReadOnlyList<string> keep, CancellationToken cancellationToken)
        {
            var graph = Apps[applicationObjectId].Graph;
            var removed = graph.Where(permission => !keep.Contains(permission)).Order(StringComparer.Ordinal).ToArray();
            graph.ExceptWith(removed);
            return Task.FromResult(OperationResult<IReadOnlyList<string>>.Success(removed));
        }

        public Task<OperationResult<bool>> ConfigureResourcePermissionsAsync(AdministratorSession session, Guid applicationObjectId, string resourceAppId, IReadOnlyList<string> permissions,
            CancellationToken cancellationToken)
        {
            Apps[applicationObjectId].Other.UnionWith(permissions.Select(permission => $"{resourceAppId}/{permission}"));
            return Task.FromResult(OperationResult<bool>.Success(true));
        }

        public Task<OperationResult<IReadOnlyList<string>>> GrantResourceConsentAsync(AdministratorSession session, Guid servicePrincipalId, string resourceAppId,
            IReadOnlyList<string> permissions, CancellationToken cancellationToken) => Task.FromResult(OperationResult<IReadOnlyList<string>>.Success(permissions));

        public Task<OperationResult<bool>> AssignDirectoryRoleAsync(AdministratorSession session, Guid servicePrincipalId, string roleTemplateId, CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult<bool>.Success(Roles.Add($"{servicePrincipalId}/{roleTemplateId}")));

        public async Task<OperationResult<CreatedCertificate>> CreateCertificateCredentialAsync(AdministratorSession session, Guid applicationObjectId, string subjectName,
            TimeSpan lifetime, Func<string, DateTimeOffset?, CancellationToken, Task<OperationResult<SecretReference>>> protect, CancellationToken cancellationToken)
        {
            var expires = DateTimeOffset.UtcNow.Add(lifetime);
            var stored = await protect("pfx:AAAA", expires, cancellationToken);
            var app = Apps[applicationObjectId];
            var removed = app.Secrets;
            Apps[applicationObjectId] = app with { Secrets = 0, Certificates = 1 };
            return OperationResult<CreatedCertificate>.Success(new CreatedCertificate("ABCDEF0123456789", expires, stored.Value!, removed));
        }

        public Task<OperationResult<CreatedClientSecret>> CreateClientSecretAsync(AdministratorSession session, Guid applicationObjectId, string displayName, TimeSpan lifetime,
            Func<string, DateTimeOffset?, CancellationToken, Task<OperationResult<SecretReference>>> protect, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("4.0 beta.2 registrations never create client secrets.");

        public Task<OperationResult> RenameApplicationAsync(AdministratorSession session, Guid applicationObjectId, string displayName, CancellationToken cancellationToken)
        {
            var app = Apps[applicationObjectId];
            Apps[applicationObjectId] = app with { App = app.App with { DisplayName = displayName } };
            Calls.Add($"rename {displayName}");
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> DeleteApplicationAsync(AdministratorSession session, Guid applicationObjectId, CancellationToken cancellationToken)
        {
            Calls.Add($"delete {Apps[applicationObjectId].App.DisplayName}");
            Apps.Remove(applicationObjectId);
            return Task.FromResult(OperationResult.Success());
        }

        public Task SignOutAsync(AdministratorSession session) => Task.CompletedTask;
    }

    private sealed class Profiles(CustomerProfile profile) : ICustomerProfileStore
    {
        public CustomerProfile Profile { get; private set; } = profile;
        public Task<IReadOnlyList<CustomerProfile>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CustomerProfile>>([Profile]);
        public Task<CustomerProfile?> LoadAsync(CustomerId id, CancellationToken cancellationToken = default) => Task.FromResult<CustomerProfile?>(Profile);

        public Task SaveAsync(CustomerProfile profile, CancellationToken cancellationToken = default)
        {
            Profile = profile;
            return Task.CompletedTask;
        }
    }

    private sealed class Credentials : ITenantCredentialService
    {
        public readonly Dictionary<string, string> Stored = [];

        public Task<OperationResult<SecretReference>> StoreClientSecretAsync(string customerId, TenantConfiguration tenant, string clientSecret, DateTimeOffset? expiresOnUtc,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("No client secrets in 4.0 beta.2.");

        public Task<OperationResult<SecretReference>> StoreCertificateAsync(string customerId, TenantConfiguration tenant, string purpose, string storedValue, DateTimeOffset? expiresOnUtc,
            CancellationToken cancellationToken = default)
        {
            var reference = new SecretReference(SecretReference.OperatingSystemProvider, $"customer/{customerId}/{tenant.TenantKey}/{purpose}-certificate");
            Stored[reference.ToString()] = storedValue;
            return Task.FromResult(OperationResult<SecretReference>.Success(reference));
        }

        public Task<OperationResult> DeleteCredentialAsync(SecretReference reference, CancellationToken cancellationToken = default)
        {
            Stored.Remove(reference.ToString());
            return Task.FromResult(OperationResult.Success());
        }
    }

    private sealed class Tenants(Profiles profiles) : ITenantRegistrationService
    {
        public Task<IReadOnlyList<CustomerProfile>> ListCustomersAsync(CancellationToken cancellationToken = default) => profiles.ListAsync(cancellationToken);
        public Task<OperationResult<CustomerProfile>> CreateCustomerAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async Task<OperationResult<CustomerProfile>> SaveTenantAsync(CustomerId customerId, TenantConfiguration tenant, string? clientSecret, DateTimeOffset? secretExpiresOnUtc,
            CancellationToken cancellationToken = default)
        {
            var previous = profiles.Profile.Tenants.FirstOrDefault(existing => existing.TenantKey == tenant.TenantKey);
            if (tenant.DeploymentRegistration is null && previous?.DeploymentRegistration is { } deployment) tenant = tenant with { DeploymentRegistration = deployment };
            await profiles.SaveAsync(profiles.Profile with { Tenants = [.. profiles.Profile.Tenants.Where(existing => existing.TenantKey != tenant.TenantKey), tenant] }, cancellationToken);
            return OperationResult<CustomerProfile>.Success(profiles.Profile);
        }
    }

    private sealed class Connections : ITenantConnectionService
    {
        public readonly List<ApplicationRegistration?> Tested = [];
        public event EventHandler<TenantConnectionReport>? ConnectionTested { add { } remove { } }
        public TenantConnectionReport? GetLastReport(string customerId, string tenantKey) => null;

        public Task<TenantConnectionReport> TestConnectionAsync(string customerId, TenantConfiguration tenant, CancellationToken cancellationToken = default)
        {
            Tested.Add(tenant.ApplicationRegistration);
            return Task.FromResult(new TenantConnectionReport(TenantConnectionState.Connected, TenantReadiness.NotRun, [], null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null));
        }
    }

    private sealed class Audit : IAuditService
    {
        public readonly List<string> Events = [];

        public Task RecordAsync(string eventType, AuditOutcome outcome, string summary, IReadOnlyDictionary<string, string?>? properties = null,
            CancellationToken cancellationToken = default)
        {
            Events.Add(summary);
            return Task.CompletedTask;
        }
    }

    private static async Task<JobSnapshot> RunAsync(JobService jobs, JobSnapshot queued)
    {
        for (var wait = 0; wait < 200; wait++)
        {
            var job = jobs.Jobs.First(item => item.Id == queued.Id);
            if (job.IsFinished) return job;
            await Task.Delay(25);
        }

        throw new TimeoutException("The job did not finish.");
    }

    [Fact]
    public async Task Connecting_creates_a_read_only_registration_with_a_certificate_and_exchange_access()
    {
        using var jobs = new JobService(TimeProvider.System, NullLogger<JobService>.Instance);
        var entra = new FakeEntra();
        var profiles = new Profiles(CustomerProfile.Create(Customer, "Contoso"));
        var credentials = new Credentials();
        var connections = new Connections();
        var service = new TenantOnboardingService(jobs, entra, new PermissionValidationService(), credentials, new Tenants(profiles), profiles, connections, new Audit(),
            NullLogger<TenantOnboardingService>.Instance);

        var job = await RunAsync(jobs, service.RegisterAutomatically(Customer, null, CloudEnvironment.Commercial));

        job.State.Should().Be(JobState.Succeeded, job.Error);
        var app = entra.Apps.Values.Single();
        app.App.DisplayName.Should().Be(ITenantOnboardingService.ApplicationDisplayName);
        app.Graph.Should().NotBeEmpty().And.OnlyContain(permission => !permission.Contains("ReadWrite"), "the Read registration cannot change the tenant");
        app.Other.Should().BeEquivalentTo([$"{TenantResources.ExchangeOnlineAppId}/Exchange.ManageAsApp", $"{TenantResources.ExchangeOnlineProtectionAppId}/Exchange.ManageAsApp"]);
        entra.Roles.Should().ContainSingle(role => role.EndsWith(TenantResources.GlobalReaderRoleId, StringComparison.Ordinal));

        var tenant = profiles.Profile.PrimaryTenant!;
        tenant.ApplicationRegistration!.AuthenticationType.Should().Be(TenantAuthenticationType.Certificate);
        tenant.ApplicationRegistration.SecretReference!.ToString().Should().EndWith("graph-certificate");
        tenant.ApplicationRegistration.CertificateReference.Should().Be("ABCDEF0123456789", "the thumbprint (public) is shown; the private key stays in the secret store");
        tenant.ApplicationRegistration.RequiredCapabilities.Should().NotContain(TenantCapability.PolicyDeployment);
        tenant.DeploymentRegistration.Should().BeNull("write access exists only during deploy phases");
        credentials.Stored.Should().ContainKey(tenant.ApplicationRegistration.SecretReference.ToString());
    }

    [Fact]
    public async Task An_older_agent_registration_is_renamed_loses_its_write_permissions_and_its_secret()
    {
        using var jobs = new JobService(TimeProvider.System, NullLogger<JobService>.Instance);
        var entra = new FakeEntra();
        var legacy = new CreatedApplication(Guid.NewGuid(), Guid.NewGuid(), ITenantOnboardingService.LegacyApplicationDisplayName);
        entra.Apps[legacy.ObjectId] = (legacy, ["Policy.ReadWrite.ConditionalAccess", "DeviceManagementConfiguration.ReadWrite.All", "Organization.Read.All"], [], 1, 0);
        var secret = new SecretReference(SecretReference.OperatingSystemProvider, "customer/contoso/primary/graph-client-secret");
        var tenant = new TenantConfiguration("primary", "Contoso", "Contoso", TenantId, "contoso.com", CloudEnvironment.Commercial,
            new ApplicationRegistration(legacy.ClientId, legacy.ClientId, TenantAuthenticationType.ClientSecret, secret, null, DateTimeOffset.UtcNow.AddDays(400),
                [TenantCapability.TenantDiscovery]));
        var profiles = new Profiles(CustomerProfile.Create(Customer, "Contoso") with { Tenants = [tenant] });
        var credentials = new Credentials();
        credentials.Stored[secret.ToString()] = "old-secret";
        var service = new TenantOnboardingService(jobs, entra, new PermissionValidationService(), credentials, new Tenants(profiles), profiles, new Connections(), new Audit(),
            NullLogger<TenantOnboardingService>.Instance);

        var job = await RunAsync(jobs, service.UpdatePermissions(Customer));

        job.State.Should().Be(JobState.Succeeded, job.Error);
        entra.Calls.Should().Contain($"rename {ITenantOnboardingService.ApplicationDisplayName}");
        var app = entra.Apps[legacy.ObjectId];
        app.Graph.Should().NotContain(["Policy.ReadWrite.ConditionalAccess", "DeviceManagementConfiguration.ReadWrite.All"]);
        app.Secrets.Should().Be(0, "the certificate replaces the client secret");
        job.Steps.Single(step => step.Name == "Remove write permissions").Detail.Should().Contain("Policy.ReadWrite.ConditionalAccess");
        credentials.Stored.Should().NotContainKey(secret.ToString(), "the old client secret is deleted from the secret store");
        profiles.Profile.PrimaryTenant!.ApplicationRegistration!.AuthenticationType.Should().Be(TenantAuthenticationType.Certificate);
    }

    [Fact]
    public async Task Deployment_access_is_a_separate_registration_that_is_deleted_when_the_phase_ends()
    {
        using var jobs = new JobService(TimeProvider.System, NullLogger<JobService>.Instance);
        var entra = new FakeEntra();
        var profiles = new Profiles(CustomerProfile.Create(Customer, "Contoso"));
        var credentials = new Credentials();
        var connections = new Connections();
        var audit = new Audit();
        var service = new TenantOnboardingService(jobs, entra, new PermissionValidationService(), credentials, new Tenants(profiles), profiles, connections, audit,
            NullLogger<TenantOnboardingService>.Instance);
        (await RunAsync(jobs, service.RegisterAutomatically(Customer, null, CloudEnvironment.Commercial))).State.Should().Be(JobState.Succeeded);
        var read = profiles.Profile.PrimaryTenant!.ApplicationRegistration!;

        var enabled = await RunAsync(jobs, service.EnableDeployment(Customer));

        enabled.State.Should().Be(JobState.Succeeded, enabled.Error);
        var tenant = profiles.Profile.PrimaryTenant!;
        tenant.ApplicationRegistration.Should().Be(read, "the Read registration is unchanged");
        var deploy = tenant.DeploymentRegistration!;
        deploy.ClientId.Should().NotBe(read.ClientId);
        deploy.RequiredCapabilities.Should().Equal(TenantCapability.PolicyDeployment);
        deploy.SecretReference!.ToString().Should().EndWith("deploy-certificate");
        var deployApp = entra.Apps.Values.Single(app => app.App.ClientId == deploy.ClientId);
        deployApp.App.DisplayName.Should().Be(ITenantOnboardingService.DeployApplicationDisplayName);
        deployApp.Graph.Should().Contain("Policy.ReadWrite.ConditionalAccess");
        deployApp.Other.Should().BeEmpty("the Deploy registration has no Exchange access");
        entra.Roles.Should().NotContain(role => role.StartsWith(deploy.ClientId.ToString(), StringComparison.Ordinal), "the Deploy registration holds no directory role");
        connections.Tested.Last()!.ClientId.Should().Be(deploy.ClientId, "the sign-in test uses the Deploy registration");

        var removed = await RunAsync(jobs, service.RemoveDeployment(Customer));

        removed.State.Should().Be(JobState.Succeeded, removed.Error);
        entra.Calls.Should().Contain($"delete {ITenantOnboardingService.DeployApplicationDisplayName}");
        entra.Apps.Values.Should().ContainSingle().Which.App.ClientId.Should().Be(read.ClientId);
        credentials.Stored.Should().NotContainKey(deploy.SecretReference.ToString());
        profiles.Profile.PrimaryTenant!.DeploymentRegistration.Should().BeNull();
        profiles.Profile.PrimaryTenant.ApplicationRegistration.Should().Be(read);
        audit.Events.Should().Contain(summary => summary.Contains("Deployment access enabled")).And.Contain(summary => summary.Contains("Deployment access removed"));
    }
}
