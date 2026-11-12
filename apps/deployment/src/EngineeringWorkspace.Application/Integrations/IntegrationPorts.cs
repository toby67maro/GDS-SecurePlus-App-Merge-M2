using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Domain.Common;

namespace EngineeringWorkspace.Application.Integrations;

// Interfaces only; Teams and ConnectWise API calls are implemented in M8.

public interface ITeamsIntegrationService
{
    Task<OperationResult> SendNotificationAsync(string channelReference, string message, CancellationToken cancellationToken);
}

public interface IConnectWiseIntegrationService
{
    Task<OperationResult> LinkCompanyAsync(CustomerId customerId, int connectWiseCompanyId, CancellationToken cancellationToken);
    Task<OperationResult<string>> CreateTicketAsync(CustomerId customerId, string summary, string description, CancellationToken cancellationToken);
}
