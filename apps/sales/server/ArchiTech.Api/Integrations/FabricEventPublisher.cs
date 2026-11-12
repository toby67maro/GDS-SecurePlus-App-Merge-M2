using System.Text.Json;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using ArchiTech.Api.Domain;
using Microsoft.Extensions.Options;

namespace ArchiTech.Api.Integrations;

/// <summary>
/// Optional publisher to a Microsoft Fabric Eventstream. Paste the Eventstream custom endpoint's
/// Event Hubs compatible connection string and entity name into configuration; leave it empty to disable.
/// </summary>
public sealed class FabricOptions
{
    public const string SectionName = "Fabric";

    public bool Enabled { get; set; }
    public string? ConnectionString { get; set; }
    public string? EventHubName { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
}

public sealed class FabricEventPublisher(IOptions<FabricOptions> options, ILogger<FabricEventPublisher> logger)
    : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly FabricOptions settings = options.Value;
    private readonly object gate = new();
    private EventHubProducerClient? client;

    public bool IsConfigured => settings.Enabled && !string.IsNullOrWhiteSpace(settings.ConnectionString);

    /// <summary>Sends one event. Never throws and never blocks a submission for longer than the timeout.</summary>
    public async Task PublishEngagementAsync(string eventType, Engagement engagement, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return;
        try
        {
            var payload = new
            {
                eventType,
                eventId = Guid.NewGuid(),
                occurredUtc = DateTime.UtcNow,
                engagementId = engagement.Id,
                customerId = engagement.CustomerId,
                customerName = engagement.Customer?.Name,
                salesperson = engagement.Salesperson,
                ownerEmail = engagement.OwnerEmail,
                meetingDate = engagement.MeetingDate,
                users = engagement.Users,
                endpoints = engagement.Endpoints,
                oneTimeHoursMin = engagement.OneTimeHoursMin,
                oneTimeHoursMax = engagement.OneTimeHoursMax,
                monthlyHoursMin = engagement.MonthlyHoursMin,
                monthlyHoursMax = engagement.MonthlyHoursMax,
                taskCount = engagement.Tasks?.Count ?? 0,
                plannerHandoffStatus = engagement.PlannerHandoffStatus,
                deploymentSyncStatus = engagement.DeploymentSyncStatus,
                createdUtc = engagement.CreatedUtc
            };
            var data = new EventData(JsonSerializer.SerializeToUtf8Bytes(payload, Json)) { ContentType = "application/json" };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(2, settings.TimeoutSeconds)));
            await Producer().SendAsync([data], timeout.Token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning(exception, "Publishing engagement {EngagementId} to Fabric failed; the submission is unaffected.", engagement.Id);
        }
    }

    private EventHubProducerClient Producer()
    {
        lock (gate)
        {
            return client ??= string.IsNullOrWhiteSpace(settings.EventHubName)
                ? new EventHubProducerClient(settings.ConnectionString)
                : new EventHubProducerClient(settings.ConnectionString, settings.EventHubName);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (client is not null) await client.DisposeAsync();
    }
}
