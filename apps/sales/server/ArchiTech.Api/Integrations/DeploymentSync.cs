using ArchiTech.Api.Data;
using ArchiTech.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ArchiTech.Api.Integrations;

public static class DeploymentSync
{
    /// <summary>Pushes one engagement to the deployment database and records the outcome. Never throws for bridge failures.</summary>
    public static async Task ApplyAsync(
        AppDbContext db, DeploymentBridge bridge, Engagement engagement, CancellationToken cancellationToken)
    {
        var result = await bridge.SyncAsync(engagement, cancellationToken);
        engagement.DeploymentSyncStatus = result.Status;
        engagement.DeploymentSyncMessage = result.Message.Length > 300 ? result.Message[..300] : result.Message;
        engagement.DeploymentSyncedUtc = result.Succeeded ? DateTime.UtcNow : engagement.DeploymentSyncedUtc;
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Retries engagements that are Pending or Failed so a database outage never loses a submission.</summary>
public sealed class DeploymentSyncWorker(
    IServiceScopeFactory scopes,
    DeploymentBridge bridge,
    IOptions<BridgeOptions> options,
    ILogger<DeploymentSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!bridge.IsConfigured) return;
        try { await bridge.ApplySchemaAsync(stoppingToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Applying the bridge schema failed; sync will keep retrying.");
        }

        var delay = TimeSpan.FromSeconds(Math.Max(15, options.Value.ReconcileIntervalSeconds));
        using var timer = new PeriodicTimer(delay);
        do
        {
            try { await ReconcileAsync(stoppingToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Deployment reconcile pass failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pending = await db.Engagements
            .Include(e => e.Customer)
            .Include(e => e.Answers)
            .Include(e => e.Tasks).ThenInclude(t => t.WbsTask)
            .Where(e => e.DeploymentSyncStatus != "Synced")
            .OrderBy(e => e.CreatedUtc)
            .Take(Math.Max(1, options.Value.ReconcileBatchSize))
            .ToListAsync(cancellationToken);
        foreach (var engagement in pending)
        {
            await DeploymentSync.ApplyAsync(db, bridge, engagement, cancellationToken);
        }
    }
}
