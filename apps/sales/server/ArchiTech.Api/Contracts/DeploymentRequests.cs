namespace ArchiTech.Api.Contracts;

public sealed class CreateDeploymentCustomerRequest
{
    public string? CustomerName { get; init; }
    public string? ContactName { get; init; }
    public string? ContactEmail { get; init; }
}

public sealed class UpdateDeploymentProfileRequest
{
    public string? TenantId { get; init; }
    public string? PrimaryDomain { get; init; }
    public string? EntraApplicationClientId { get; init; }
}

public sealed class UpdateDeploymentSetupStepRequest
{
    public bool IsComplete { get; init; }
}

public sealed class SaveDeploymentFindingRequest
{
    public string? Status { get; init; }
    public string? Notes { get; init; }
    public string? Evidence { get; init; }
}
