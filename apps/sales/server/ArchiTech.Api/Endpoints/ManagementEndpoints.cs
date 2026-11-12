using ArchiTech.Api.Data;
using ArchiTech.Api.Integrations;
using Microsoft.EntityFrameworkCore;

namespace ArchiTech.Api.Endpoints;

/// <summary>
/// Cross-salesperson visibility for IT and Management. Every route requires the ManagementAccess policy
/// (Management or Administrator); salespeople never see these routes' data.
/// </summary>
public static class ManagementEndpoints
{
    public static IEndpointRouteBuilder MapManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/management").RequireAuthorization("ManagementAccess");

        group.MapGet("/engagements", async (
            AppDbContext db,
            DeploymentBridge bridge,
            string? salesperson,
            string? syncStatus,
            CancellationToken cancellationToken) =>
        {
            var query = db.Engagements.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(salesperson))
                query = query.Where(e => e.Salesperson == salesperson || e.OwnerEmail == salesperson);
            if (!string.IsNullOrWhiteSpace(syncStatus))
                query = query.Where(e => e.DeploymentSyncStatus == syncStatus);

            var rows = await query
                .OrderByDescending(e => e.CreatedUtc)
                .Select(e => new
                {
                    e.Id,
                    CompanyName = e.Customer.Name,
                    e.CustomerId,
                    e.Salesperson,
                    e.OwnerEmail,
                    e.MeetingDate,
                    e.CreatedUtc,
                    e.Users,
                    e.Endpoints,
                    e.OneTimeHoursMin,
                    e.OneTimeHoursMax,
                    e.MonthlyHoursMin,
                    e.MonthlyHoursMax,
                    TaskCount = e.Tasks.Count,
                    e.PlannerHandoffStatus,
                    e.DeploymentSyncStatus,
                    e.DeploymentSyncedUtc
                })
                .ToListAsync(cancellationToken);

            var assessments = (await bridge.GetOverviewAsync(cancellationToken))
                .Where(r => r.AssessmentStatus is not null)
                .ToDictionary(r => r.SalesEngagementId);
            return Results.Ok(rows.Select(r => new
            {
                r.Id,
                r.CompanyName,
                r.Salesperson,
                r.OwnerEmail,
                r.MeetingDate,
                r.CreatedUtc,
                r.Users,
                r.Endpoints,
                r.OneTimeHoursMin,
                r.OneTimeHoursMax,
                r.MonthlyHoursMin,
                r.MonthlyHoursMax,
                r.TaskCount,
                r.PlannerHandoffStatus,
                r.DeploymentSyncStatus,
                r.DeploymentSyncedUtc,
                assessmentStatus = assessments.GetValueOrDefault(r.Id)?.AssessmentStatus,
                assessmentScore = assessments.GetValueOrDefault(r.Id)?.AssessmentScore
            }));
        });

        group.MapGet("/summary", async (AppDbContext db, CancellationToken cancellationToken) =>
        {
            var engagements = await db.Engagements.AsNoTracking()
                .Select(e => new { e.Salesperson, e.OwnerEmail, e.DeploymentSyncStatus, e.CreatedUtc, e.OneTimeHoursMax })
                .ToListAsync(cancellationToken);
            return Results.Ok(new
            {
                total = engagements.Count,
                last30Days = engagements.Count(e => e.CreatedUtc >= DateTime.UtcNow.AddDays(-30)),
                synced = engagements.Count(e => e.DeploymentSyncStatus == "Synced"),
                needsAttention = engagements.Count(e => e.DeploymentSyncStatus is "Failed" or "Pending"),
                bySalesperson = engagements.GroupBy(e => e.Salesperson)
                    .Select(g => new { salesperson = g.Key, count = g.Count(), hoursMax = g.Sum(e => e.OneTimeHoursMax) })
                    .OrderByDescending(x => x.count),
                bySyncStatus = engagements.GroupBy(e => e.DeploymentSyncStatus)
                    .Select(g => new { status = g.Key, count = g.Count() })
            });
        });

        group.MapPost("/engagements/{id:guid}/resync", async (
            Guid id, AppDbContext db, DeploymentBridge bridge, CancellationToken cancellationToken) =>
        {
            var engagement = await db.Engagements
                .Include(e => e.Customer).Include(e => e.Answers).Include(e => e.Tasks).ThenInclude(t => t.WbsTask)
                .SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
            if (engagement is null) return Results.NotFound();
            if (!bridge.IsConfigured)
                return Results.Conflict(new { message = "The deployment bridge is not configured on this host." });
            await DeploymentSync.ApplyAsync(db, bridge, engagement, cancellationToken);
            return Results.Ok(new { engagement.DeploymentSyncStatus, engagement.DeploymentSyncMessage });
        });

        return endpoints;
    }
}
