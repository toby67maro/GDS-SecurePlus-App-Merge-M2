using System.Security.Claims;
using ArchiTech.Api.Contracts;
using ArchiTech.Api.Data;
using ArchiTech.Api.Domain;
using ArchiTech.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace ArchiTech.Api.Endpoints;

public static class DeploymentChangePlanEndpoints
{
    private const string Draft = "Draft";
    private const string PendingReview = "PendingReview";
    private const string Accepted = "Accepted";
    private const string Rejected = "Rejected";

    public static IEndpointRouteBuilder MapDeploymentChangePlanEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/deployment")
            .RequireAuthorization("DeploymentAccess");
        group.MapGet("/profiles/{customerId:guid}/change-plans", ListAsync);
        group.MapPost("/profiles/{customerId:guid}/change-plans", CreateAsync)
            .RequireAuthorization("DeploymentAuthor");
        group.MapGet("/change-plans/pending", ListPendingAsync)
            .RequireAuthorization("DeploymentApproval");
        group.MapGet("/change-plans/{planId:guid}", GetAsync);
        group.MapGet("/change-plans/{planId:guid}/preview.html", PreviewAsync);
        group.MapGet("/change-plans/{planId:guid}/draft.txt", InertDraftAsync);
        group.MapPost("/change-plans/{planId:guid}/submit", SubmitAsync)
            .RequireAuthorization("DeploymentAuthor");
        group.MapPost("/change-plans/{planId:guid}/decision", DecideAsync)
            .RequireAuthorization("DeploymentApproval");
        return endpoints;
    }

    private static async Task<IResult> InertDraftAsync(
        Guid planId, AppDbContext db, DeploymentControlCatalog catalog, CancellationToken cancellationToken)
    {
        var plan = await db.DeploymentChangePlans.AsNoTracking()
            .Include(item => item.Customer)
            .Include(item => item.AssessmentRun)
            .Include(item => item.Items)
            .SingleOrDefaultAsync(item => item.Id == planId, cancellationToken);
        if (plan is null) return Results.NotFound(new { message = "Deployment plan not found." });
        return Results.File(
            System.Text.Encoding.UTF8.GetBytes(DeploymentPlanPreview.RenderInertDraft(plan, catalog)),
            "text/plain; charset=utf-8",
            $"architech-inert-draft-{plan.Id}.txt");
    }

    private static async Task<IResult> ListAsync(
        Guid customerId,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await db.Customers.AnyAsync(customer => customer.Id == customerId, cancellationToken))
        {
            return Results.NotFound();
        }

        var plans = await db.DeploymentChangePlans.AsNoTracking()
            .Where(plan => plan.CustomerId == customerId)
            .OrderByDescending(plan => plan.CreatedUtc)
            .Include(plan => plan.Items)
            .Include(plan => plan.Events)
            .ToListAsync(cancellationToken);
        return Results.Ok(plans.Select(ToResponse));
    }

    private static async Task<IResult> ListPendingAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var plans = await db.DeploymentChangePlans.AsNoTracking()
            .Where(plan => plan.Status == PendingReview)
            .OrderBy(plan => plan.SubmittedUtc)
            .Include(plan => plan.Customer)
            .Include(plan => plan.Items)
            .Include(plan => plan.Events)
            .ToListAsync(cancellationToken);
        return Results.Ok(plans.Select(ToResponse));
    }

    private static async Task<IResult> GetAsync(
        Guid planId,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var plan = await LoadAsync(db, planId, cancellationToken);
        return plan is null ? Results.NotFound() : Results.Ok(ToResponse(plan));
    }

    private static async Task<IResult> PreviewAsync(
        Guid planId,
        AppDbContext db,
        DeploymentControlCatalog catalog,
        CancellationToken cancellationToken)
    {
        var plan = await db.DeploymentChangePlans.AsNoTracking()
            .Include(item => item.Customer)
            .Include(item => item.AssessmentRun).ThenInclude(run => run.Results)
            .Include(item => item.Items)
            .Include(item => item.Events)
            .SingleOrDefaultAsync(item => item.Id == planId, cancellationToken);
        if (plan is null)
            return Results.NotFound(new { message = "Deployment plan not found." });

        return Results.File(
            System.Text.Encoding.UTF8.GetBytes(DeploymentPlanPreview.Render(plan, catalog)),
            "text/html; charset=utf-8",
            $"architech-plan-preview-{plan.Id}.html");
    }

    private static async Task<IResult> CreateAsync(
        Guid customerId,
        CreateDeploymentChangePlanRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        DeploymentControlCatalog catalog,
        CancellationToken cancellationToken)
    {
        var actorId = ActorId(user);
        if (actorId is null) return MissingIdentity();
        if (!await db.Customers.AnyAsync(customer => customer.Id == customerId, cancellationToken))
        {
            return Results.NotFound(new { message = "Customer profile not found." });
        }
        if (request.AssessmentRunId == Guid.Empty)
        {
            return Validation("assessmentRunId", "Choose a completed assessment.");
        }
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 160)
        {
            return Validation("title", "Provide a plan title of 1 to 160 characters.");
        }
        if (request.Notes?.Trim().Length > 2000)
        {
            return Validation("notes", "Plan notes must be 2000 characters or fewer.");
        }
        var requestedIds = request.ControlIds ?? [];
        if (requestedIds.Length is 0 or > 100)
        {
            return Validation("controlIds", "Select between 1 and 100 assessed controls.");
        }
        if (requestedIds.Any(string.IsNullOrWhiteSpace))
        {
            return Validation("controlIds", "Control IDs cannot be empty.");
        }
        if (requestedIds.Distinct(StringComparer.Ordinal).Count() != requestedIds.Length)
        {
            return Validation("controlIds", "A control can only be included once.");
        }

        var assessment = await db.AssessmentRuns
            .Include(run => run.Results)
            .SingleOrDefaultAsync(
                run => run.Id == request.AssessmentRunId && run.CustomerId == customerId,
                cancellationToken);
        if (assessment is null)
        {
            return Validation("assessmentRunId", "Choose an assessment belonging to this customer.");
        }
        if (!assessment.IsComplete)
        {
            return Validation("assessmentRunId", "Complete the assessment before building a review plan.");
        }

        var resultById = assessment.Results.ToDictionary(item => item.ControlId, StringComparer.Ordinal);
        var controlsById = catalog.Controls
            .Where(control => control.IsActive)
            .ToDictionary(control => control.ControlId, StringComparer.Ordinal);
        var items = new List<DeploymentChangePlanItem>(requestedIds.Length);
        foreach (var controlId in requestedIds)
        {
            if (!controlsById.TryGetValue(controlId, out var control) ||
                !resultById.TryGetValue(controlId, out var result))
            {
                return Validation("controlIds", $"Control '{controlId}' is not in this assessment.");
            }
            if (result.Status is not ("NotImplemented" or "PartiallyImplemented"))
            {
                return Validation("controlIds", $"Control '{controlId}' is not a confirmed gap in the completed assessment.");
            }
            items.Add(new DeploymentChangePlanItem
            {
                ControlId = control.ControlId,
                ControlName = control.Name,
                AssessmentStatus = result.Status,
                Sequence = items.Count + 1
            });
        }

        var now = DateTime.UtcNow;
        var plan = new DeploymentChangePlan
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            AssessmentRunId = assessment.Id,
            Title = request.Title.Trim(),
            Notes = Normalize(request.Notes),
            Status = Draft,
            RequestedById = actorId,
            RequestedBy = ActorName(user),
            CreatedUtc = now,
            UpdatedUtc = now,
            Items = items,
            Events =
            [
                NewEvent("DraftCreated", user, actorId, now, "Draft created from a completed assessment.")
            ]
        };
        db.DeploymentChangePlans.Add(plan);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/deployment/change-plans/{plan.Id}", ToResponse(plan));
    }

    private static async Task<IResult> SubmitAsync(
        Guid planId,
        ClaimsPrincipal user,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var actorId = ActorId(user);
        if (actorId is null) return MissingIdentity();
        var plan = await LoadAsync(db, planId, cancellationToken);
        if (plan is null)
        {
            return Results.NotFound();
        }
        if (!string.Equals(plan.Status, Draft, StringComparison.Ordinal))
        {
            return Results.Conflict(new { message = "Only a draft plan can be submitted for review." });
        }
        if (!HasUsableActorId(plan.RequestedById))
            return Results.Conflict(new { message = "The plan has no verifiable requester identity and cannot be submitted." });
        if (!string.Equals(plan.RequestedById, actorId, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Forbid();
        }

        plan.Status = PendingReview;
        plan.SubmittedUtc = DateTime.UtcNow;
        plan.UpdatedUtc = plan.SubmittedUtc.Value;
        plan.Events.Add(NewEvent("Submitted", user, actorId, plan.SubmittedUtc.Value, null));
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new { message = "The plan changed during submission. Reload it before trying again." });
        }
        return Results.Ok(ToResponse(plan));
    }

    private static async Task<IResult> DecideAsync(
        Guid planId,
        DecideDeploymentChangePlanRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var actorId = ActorId(user);
        if (actorId is null) return MissingIdentity();
        var plan = await LoadAsync(db, planId, cancellationToken);
        if (plan is null)
        {
            return Results.NotFound();
        }
        if (!string.Equals(plan.Status, PendingReview, StringComparison.Ordinal))
        {
            return Results.Conflict(new { message = "Only pending plans can be reviewed." });
        }
        if (!HasUsableActorId(plan.RequestedById))
            return Results.Conflict(new { message = "The plan has no verifiable requester identity and cannot be reviewed." });
        if (string.Equals(plan.RequestedById, actorId, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Forbid();
        }
        if (request.Decision is not ("Accept" or "Reject"))
        {
            return Validation("decision", "Choose Accept or Reject.");
        }
        if (string.IsNullOrWhiteSpace(request.Notes) || request.Notes.Trim().Length > 2000)
        {
            return Validation("notes", "Provide review rationale of 1 to 2000 characters.");
        }

        plan.Status = request.Decision == "Accept" ? Accepted : Rejected;
        plan.ReviewDecision = request.Decision;
        plan.ReviewNotes = request.Notes.Trim();
        plan.ReviewedById = actorId;
        plan.ReviewedBy = ActorName(user);
        plan.ReviewedUtc = DateTime.UtcNow;
        plan.UpdatedUtc = plan.ReviewedUtc.Value;
        plan.Events.Add(NewEvent(
            request.Decision == "Accept" ? "Accepted" : "Rejected",
            user,
            actorId,
            plan.ReviewedUtc.Value,
            plan.ReviewNotes));
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new { message = "The plan changed during review. Reload it before trying again." });
        }
        return Results.Ok(ToResponse(plan));
    }

    private static Task<DeploymentChangePlan?> LoadAsync(
        AppDbContext db,
        Guid planId,
        CancellationToken cancellationToken) =>
        db.DeploymentChangePlans
            .Include(plan => plan.Customer)
            .Include(plan => plan.Items)
            .Include(plan => plan.Events)
            .SingleOrDefaultAsync(plan => plan.Id == planId, cancellationToken);

    private static object ToResponse(DeploymentChangePlan plan) => new
    {
        plan.Id,
        plan.CustomerId,
        customerName = plan.Customer?.Name,
        plan.AssessmentRunId,
        plan.Title,
        plan.Notes,
        plan.Status,
        plan.RequestedBy,
        plan.CreatedUtc,
        plan.UpdatedUtc,
        plan.SubmittedUtc,
        plan.ReviewedBy,
        plan.ReviewedUtc,
        plan.ReviewDecision,
        plan.ReviewNotes,
        items = plan.Items.OrderBy(item => item.Sequence).Select(item => new
        {
            item.ControlId,
            item.ControlName,
            item.AssessmentStatus,
            item.Sequence
        }),
        events = plan.Events.OrderBy(item => item.OccurredUtc).ThenBy(item => item.Id).Select(item => new
        {
            item.EventType,
            item.ActorId,
            item.Actor,
            item.Notes,
            item.OccurredUtc
        }),
        executionEnabled = false,
        executionBlockReason = "Design acceptance is not execution approval. Tenant writes remain disabled: reviewed immutable templates, tenant- and parameter-bound execution approval, an explicit operator launch, and a verified isolated execution boundary are not implemented."
    };

    private static IResult Validation(string key, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [key] = [message] });

    private static string? ActorId(ClaimsPrincipal user)
    {
        var ids = user.Claims
            .Where(claim => claim.Type is ClaimTypes.NameIdentifier or "sub")
            .Select(claim => claim.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return ids.Length == 1 && HasUsableActorId(ids[0]) ? ids[0] : null;
    }

    private static bool HasUsableActorId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 200 &&
        string.Equals(id, id.Trim(), StringComparison.Ordinal) &&
        !string.Equals(id, "unknown", StringComparison.OrdinalIgnoreCase);

    private static IResult MissingIdentity() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden,
            title: "A single, stable authenticated identity is required for design review.");

    private static string ActorName(ClaimsPrincipal user) =>
        user.Identity?.Name ??
        user.FindFirstValue(ClaimTypes.NameIdentifier) ??
        "Unknown user";

    private static DeploymentChangePlanEvent NewEvent(
        string eventType,
        ClaimsPrincipal user,
        string actorId,
        DateTime occurredUtc,
        string? notes) => new()
    {
        EventType = eventType,
        ActorId = actorId,
        Actor = ActorName(user),
        OccurredUtc = occurredUtc,
        Notes = Normalize(notes)
    };

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
