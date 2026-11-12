using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Domain.Auditing;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Customers;
using EngineeringWorkspace.Domain.Tenants;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Application.Tenants;

/// <summary>Creates customers and saves tenant app registrations. Secrets go straight to the secret store.</summary>
public interface ITenantRegistrationService
{
    Task<IReadOnlyList<CustomerProfile>> ListCustomersAsync(CancellationToken cancellationToken = default);
    Task<OperationResult<CustomerProfile>> CreateCustomerAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the tenant to the customer profile. When <paramref name="clientSecret"/> is provided it is stored in the
    /// secret store first and only its reference is saved; it is never persisted anywhere else.
    /// </summary>
    Task<OperationResult<CustomerProfile>> SaveTenantAsync(CustomerId customerId, TenantConfiguration tenant, string? clientSecret, DateTimeOffset? secretExpiresOnUtc,
        CancellationToken cancellationToken = default);
}

public sealed class TenantRegistrationService(
    ICustomerProfileStore profiles,
    ICustomerContextService customers,
    ITenantCredentialService tenantCredentials,
    IAuditService audit,
    ILogger<TenantRegistrationService> logger) : ITenantRegistrationService
{
    public Task<IReadOnlyList<CustomerProfile>> ListCustomersAsync(CancellationToken cancellationToken = default) => profiles.ListAsync(cancellationToken);

    public async Task<OperationResult<CustomerProfile>> CreateCustomerAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return OperationResult<CustomerProfile>.Failure("CustomerNameRequired", "Enter the customer name.");
        CustomerId id;
        try
        {
            id = new CustomerId(StableIdentifier.FromDisplayName(name));
        }
        catch (DomainException exception)
        {
            return OperationResult<CustomerProfile>.Failure("CustomerNameInvalid", exception.Message);
        }

        if (await profiles.LoadAsync(id, cancellationToken) is not null)
            return OperationResult<CustomerProfile>.Failure("CustomerExists", $"A customer with the ID '{id}' already exists.", null, "Select the existing customer.");
        var profile = CustomerProfile.Create(id, name.Trim());
        await profiles.SaveAsync(profile, cancellationToken);
        logger.LogInformation("Customer profile {CustomerId} created", id.Value);
        return OperationResult<CustomerProfile>.Success(profile);
    }

    public async Task<OperationResult<CustomerProfile>> SaveTenantAsync(CustomerId customerId, TenantConfiguration tenant, string? clientSecret, DateTimeOffset? secretExpiresOnUtc,
        CancellationToken cancellationToken = default)
    {
        var profile = await profiles.LoadAsync(customerId, cancellationToken);
        if (profile is null) return OperationResult<CustomerProfile>.Failure("CustomerMissing", $"Customer '{customerId}' does not exist.");
        if (string.IsNullOrWhiteSpace(tenant.TenantKey) || !StableIdentifier.IsValid(tenant.TenantKey))
            return OperationResult<CustomerProfile>.Failure("TenantKeyInvalid", "The tenant key must be lowercase letters, digits or hyphens (for example primary).");

        var toSave = tenant;
        if (!string.IsNullOrEmpty(clientSecret))
        {
            if (tenant.ApplicationRegistration is null)
                return OperationResult<CustomerProfile>.Failure("ClientIdRequired", "Enter the Client ID before storing a client secret.");
            var stored = await tenantCredentials.StoreClientSecretAsync(customerId.Value, tenant, clientSecret, secretExpiresOnUtc, cancellationToken);
            if (!stored.Succeeded) return OperationResult<CustomerProfile>.Failure(stored.Error!);
            toSave = tenant with
            {
                ApplicationRegistration = tenant.ApplicationRegistration with
                {
                    SecretReference = stored.Value,
                    CredentialExpirationUtc = secretExpiresOnUtc ?? tenant.ApplicationRegistration.CredentialExpirationUtc
                }
            };
        }

        // The Deploy registration is managed by Enable/Remove deployment access only; a form save keeps it.
        var previous = profile.Tenants.FirstOrDefault(existing => string.Equals(existing.TenantKey, toSave.TenantKey, StringComparison.OrdinalIgnoreCase));
        if (toSave.DeploymentRegistration is null && previous?.DeploymentRegistration is { } deployment && previous.TenantId == toSave.TenantId)
            toSave = toSave with { DeploymentRegistration = deployment };
        var tenants = profile.Tenants.Where(existing => !string.Equals(existing.TenantKey, toSave.TenantKey, StringComparison.OrdinalIgnoreCase)).Append(toSave)
            .OrderBy(existing => existing.TenantKey, StringComparer.Ordinal).ToArray();
        var updated = profile with { Tenants = tenants };
        await profiles.SaveAsync(updated, cancellationToken);
        customers.Refresh(updated);
        await audit.RecordAsync(AuditEventTypes.TenantAppRegistrationUpdated, AuditOutcome.Success, $"Tenant app registration '{toSave.TenantKey}' saved for '{profile.Name}'.",
            new Dictionary<string, string?>
            {
                ["CustomerId"] = customerId.Value,
                ["TenantKey"] = toSave.TenantKey,
                ["TenantId"] = toSave.TenantId?.ToString(),
                ["ClientId"] = toSave.ApplicationRegistration?.ClientId.ToString(),
                ["Cloud"] = toSave.CloudEnvironment.ToString(),
                ["SecretReference"] = toSave.ApplicationRegistration?.SecretReference?.ToString(),
                ["SecretUpdated"] = string.IsNullOrEmpty(clientSecret) ? "false" : "true"
            }, cancellationToken);
        return OperationResult<CustomerProfile>.Success(updated);
    }
}
