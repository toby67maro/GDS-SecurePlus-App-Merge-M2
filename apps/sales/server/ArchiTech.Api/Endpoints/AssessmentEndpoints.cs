using System.Security.Claims;
using ArchiTech.Api.Contracts;
using ArchiTech.Api.Data;
using ArchiTech.Api.Domain;
using ArchiTech.Api.Integrations;
using Microsoft.EntityFrameworkCore;

namespace ArchiTech.Api.Endpoints;

public static class AssessmentEndpoints
{
    private static readonly HashSet<string> Statuses =
        ["NotAssessed", "Implemented", "PartiallyImplemented", "NotImplemented", "NotApplicable", "Exception"];

    public static IEndpointRouteBuilder MapAssessmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/deployment")
            .RequireAuthorization("DeploymentAccess");
        group.MapGet("/profiles/{customerId:guid}/assessments", ListAssessmentsAsync);
        group.MapPost("/profiles/{customerId:guid}/assessments", CreateAssessmentAsync)
            .RequireAuthorization("DeploymentAuthor");
        group.MapGet("/assessments/{assessmentId:guid}", GetAssessmentAsync);
        group.MapGet("/assessments/{assessmentId:guid}/report.html", GetAssessmentReportAsync);
        group.MapGet("/profiles/{customerId:guid}/assessment-comparison", CompareAssessmentsAsync);
        group.MapGet("/assessment-permission-mappings", GetPermissionMappings);
        group.MapPost("/profiles/{customerId:guid}/conditional-access-evidence/preview", PreviewConditionalAccessEvidenceAsync)
            .RequireAuthorization("DeploymentAuthor");
        group.MapPut("/assessments/{assessmentId:guid}/controls/{controlId}", UpdateResultAsync)
            .RequireAuthorization("DeploymentAuthor");
        group.MapPost("/assessments/{assessmentId:guid}/complete", CompleteAssessmentAsync)
            .RequireAuthorization("DeploymentAuthor");
        return endpoints;
    }

    private static IResult GetPermissionMappings(DeploymentControlCatalog catalog)
    {
        var mappings = GraphEvidenceMappings.Candidates
            .ToDictionary(item => item.ControlId, StringComparer.Ordinal);
        var active = catalog.Controls.Where(control => control.IsActive).ToArray();
        return Results.Ok(new
        {
            catalogVersion = GraphEvidenceMappings.CatalogVersion,
            activeControlCount = active.Length,
            partialEvidenceMappingCount = active.Count(control => mappings.ContainsKey(control.ControlId)),
            unmappedControlCount = active.Count(control => !mappings.ContainsKey(control.ControlId)),
            mappings = active.Where(control => mappings.ContainsKey(control.ControlId))
                .OrderBy(control => control.ControlId, StringComparer.Ordinal)
                .Select(control => new
                {
                    control.ControlId,
                    control.Name,
                    mappingStatus = "PartialEvidenceOnly",
                    evidenceQuestion = mappings[control.ControlId].EvidenceQuestion,
                    limitations = mappings[control.ControlId].EvidenceLimitations,
                    endpoint = mappings[control.ControlId].Endpoint,
                    delegatedPermission = mappings[control.ControlId].DelegatedPermission,
                    userRoleRequirement = mappings[control.ControlId].UserRoleRequirement,
                    documentationUrl = mappings[control.ControlId].DocumentationUrl
                }),
            unmappedControls = active.Where(control => !mappings.ContainsKey(control.ControlId))
                .OrderBy(control => control.ControlId, StringComparer.Ordinal)
                .Select(control => new { control.ControlId, control.Name, control.Category }),
            notice = "These candidate mappings are pending security and customer review. This is not a complete permission list or authorization to grant consent. The local reader returns aggregate Conditional Access policy counts only; it does not assess controls or evaluate the other conditions identified here.",
            liveAssessmentEnabled = false,
            tenantChangesEnabled = false
        });
    }

    private static async Task<IResult> CompareAssessmentsAsync(
        Guid customerId,
        Guid baselineId,
        Guid targetId,
        AppDbContext db,
        DeploymentControlCatalog catalog,
        CancellationToken cancellationToken)
    {
        if (baselineId == targetId)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["targetId"] = ["Choose two different assessment snapshots."]
            });

        var runs = await db.AssessmentRuns.AsNoTracking()
            .Include(run => run.Results)
            .Where(run => run.CustomerId == customerId && (run.Id == baselineId || run.Id == targetId))
            .ToListAsync(cancellationToken);
        if (runs.Count != 2)
            return Results.NotFound(new { message = "Both assessments must belong to this customer." });
        var baseline = runs.Single(run => run.Id == baselineId);
        var target = runs.Single(run => run.Id == targetId);
        if (!baseline.IsComplete || !target.IsComplete)
            return Results.Conflict(new { message = "Both assessments must be completed snapshots." });
        if (!string.Equals(baseline.CatalogVersion, target.CatalogVersion, StringComparison.Ordinal))
            return Results.Conflict(new { message = "The snapshots use different catalog versions and cannot be compared." });

        var before = baseline.Results.ToDictionary(item => item.ControlId, StringComparer.Ordinal);
        var after = target.Results.ToDictionary(item => item.ControlId, StringComparer.Ordinal);
        if (before.Count != after.Count || before.Keys.Any(key => !after.ContainsKey(key)))
            return Results.Conflict(new { message = "The snapshots contain different control sets and cannot be compared." });

        var controls = catalog.Controls.ToDictionary(item => item.ControlId, StringComparer.Ordinal);
        var changed = before.Keys.Where(key => !string.Equals(before[key].Status, after[key].Status, StringComparison.Ordinal))
            .OrderBy(key => key, StringComparer.Ordinal)
            .Select(key => new
            {
                controlId = key,
                name = controls.TryGetValue(key, out var control) ? control.Name : key,
                baselineStatus = before[key].Status,
                targetStatus = after[key].Status
            }).ToArray();

        return Results.Ok(new
        {
            customerId,
            baseline = new { baseline.Id, baseline.Name, baseline.CompletedUtc, baseline.OverallPercentage },
            target = new { target.Id, target.Name, target.CompletedUtc, target.OverallPercentage },
            baseline.CatalogVersion,
            controlCount = before.Count,
            unchangedCount = before.Count - changed.Length,
            scoreDifference = Math.Round(target.OverallPercentage - baseline.OverallPercentage, 1),
            changes = changed,
            notice = "Recorded status changes only, not a live tenant assessment or approved golden baseline. Current catalog names are displayed; review evidence before drawing conclusions.",
            tenantChangesEnabled = false
        });
    }

    private static async Task<IResult> PreviewConditionalAccessEvidenceAsync(
        Guid customerId,
        HttpRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        const int maxBytes = 2 * 1024 * 1024;
        if (!await db.DeploymentProfiles.AsNoTracking().AnyAsync(
                item => item.CustomerId == customerId, cancellationToken))
            return Results.NotFound(new { message = "Deployment profile not found." });
        if (request.ContentLength > maxBytes)
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "The export exceeds the 2 MB limit.");
        if (!request.HasJsonContentType())
            return Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Provide a JSON export.");

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "The export exceeds the 2 MB limit.");
            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        try
        {
            using var json = await System.Text.Json.JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
            return Results.Ok(ConditionalAccessEvidenceReader.PreviewExport(json.RootElement));
        }
        catch (System.Text.Json.JsonException)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["export"] = ["The export is not valid JSON."] });
        }
        catch (InvalidOperationException error)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["export"] = [error.Message] });
        }
    }

    private static async Task<IResult> ListAssessmentsAsync(
        Guid customerId,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await db.Customers.AnyAsync(customer => customer.Id == customerId, cancellationToken))
        {
            return Results.NotFound();
        }

        var assessments = await db.AssessmentRuns.AsNoTracking()
            .Where(run => run.CustomerId == customerId)
            .OrderByDescending(run => run.StartedUtc)
            .Select(run => new
            {
                run.Id,
                run.Name,
                run.Source,
                run.Assessor,
                run.StartedUtc,
                run.CompletedUtc,
                run.IsComplete,
                run.OverallPercentage,
                run.TotalControls,
                run.ImplementedControls,
                run.PartialControls,
                run.NotImplementedControls,
                run.NotAssessedControls,
                run.ReadinessRating
            })
            .ToListAsync(cancellationToken);
        return Results.Ok(assessments);
    }

    private static async Task<IResult> CreateAssessmentAsync(
        Guid customerId,
        CreateAssessmentRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        DeploymentControlCatalog catalog,
        CancellationToken cancellationToken)
    {
        if (!await db.Customers.AnyAsync(customer => customer.Id == customerId, cancellationToken))
        {
            return Results.NotFound(new { message = "Customer profile not found." });
        }

        if (!string.IsNullOrWhiteSpace(request.Name) && request.Name.Trim().Length > 160)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = ["Assessment name must be 160 characters or fewer."]
            });
        }

        if (request.EngagementId.HasValue &&
            !await db.Engagements.AnyAsync(
                engagement => engagement.Id == request.EngagementId &&
                              engagement.CustomerId == customerId,
                cancellationToken))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["engagementId"] = ["Choose an engagement belonging to this customer."]
            });
        }

        var now = DateTime.UtcNow;
        var assessor = ActorName(user);
        var profileFindings = request.StartUnassessed
            ? new Dictionary<string, DeploymentFinding>(StringComparer.Ordinal)
            : await db.DeploymentFindings.AsNoTracking()
                .Where(finding => finding.DeploymentProfile.CustomerId == customerId)
                .ToDictionaryAsync(finding => finding.ControlId, finding => finding, StringComparer.Ordinal, cancellationToken);
        var run = new AssessmentRun
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            EngagementId = request.EngagementId,
            Name = string.IsNullOrWhiteSpace(request.Name)
                ? $"Assessment {now:yyyy-MM-dd HH:mm} UTC"
                : request.Name.Trim(),
            Assessor = assessor,
            Source = "Manual",
            CatalogVersion = "2.0",
            StartedUtc = now,
            ReadinessRating = "Critical",
            Results = catalog.Controls.Where(control => control.IsActive).Select(control =>
            {
                var existing = profileFindings.GetValueOrDefault(control.ControlId);
                return new AssessmentResult
                {
                    ControlId = control.ControlId,
                    Status = existing?.Status ?? "NotAssessed",
                    Notes = existing?.Notes,
                    Evidence = existing?.Evidence,
                    Assessor = existing?.ReviewedBy ?? assessor,
                    ReviewedUtc = existing?.UpdatedUtc ?? now,
                    ExceptionReason = null
                };
            }).ToArray()
        };

        ApplyScore(run, AssessmentScoring.Calculate(
            catalog.Controls,
            run.Results.ToDictionary(result => result.ControlId, result => result.Status, StringComparer.Ordinal)));
        db.AssessmentRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/deployment/assessments/{run.Id}", ToResponse(run, catalog));
    }

    private static async Task<IResult> GetAssessmentAsync(
        Guid assessmentId,
        AppDbContext db,
        DeploymentControlCatalog catalog,
        CancellationToken cancellationToken)
    {
        var run = await LoadAssessmentAsync(db, assessmentId, cancellationToken);
        return run is null
            ? Results.NotFound()
            : Results.Ok(ToResponse(run, catalog));
    }

    private static async Task<IResult> GetAssessmentReportAsync(
        Guid assessmentId,
        AppDbContext db,
        DeploymentControlCatalog catalog,
        CancellationToken cancellationToken)
    {
        var run = await LoadAssessmentAsync(db, assessmentId, cancellationToken);
        if (run is null)
        {
            return Results.NotFound(new { message = "Assessment not found." });
        }

        var report = AssessmentReport.Render(run, catalog);
        return Results.File(
            System.Text.Encoding.UTF8.GetBytes(report),
            "text/html; charset=utf-8",
            $"architech-assessment-{run.Id}.html");
    }

    private static async Task<IResult> UpdateResultAsync(
        Guid assessmentId,
        string controlId,
        UpdateAssessmentResultRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        DeploymentControlCatalog catalog,
        CancellationToken cancellationToken)
    {
        var run = await LoadAssessmentAsync(db, assessmentId, cancellationToken);
        if (run is null)
        {
            return Results.NotFound();
        }
        if (run.IsComplete)
        {
            return Results.Conflict(new { message = "Completed assessments are read-only. Create a new assessment to record changes." });
        }

        var result = run.Results.SingleOrDefault(item =>
            item.ControlId.Equals(controlId, StringComparison.Ordinal));
        if (result is null || !catalog.Controls.Any(control =>
                control.ControlId.Equals(controlId, StringComparison.Ordinal) && control.IsActive))
        {
            return Results.NotFound(new { message = "Control is not in this assessment's active catalog." });
        }
        if (request.Status is null || !Statuses.Contains(request.Status))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["status"] = ["Choose a valid assessment status."]
            });
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        ValidateMaxLength("notes", request.Notes, 2000, errors);
        ValidateMaxLength("evidence", request.Evidence, 4000, errors);
        ValidateMaxLength("exceptionReason", request.ExceptionReason, 1000, errors);
        if (request.Status == "Exception" && string.IsNullOrWhiteSpace(request.ExceptionReason))
        {
            errors["exceptionReason"] = ["Provide a reason for a control exception."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        result.Status = request.Status;
        result.Notes = Normalize(request.Notes);
        result.Evidence = Normalize(request.Evidence);
        result.ExceptionReason = Normalize(request.ExceptionReason);
        result.Assessor = ActorName(user);
        result.ReviewedUtc = DateTime.UtcNow;
        ApplyScore(run, AssessmentScoring.Calculate(
            catalog.Controls.Where(control => control.IsActive),
            run.Results.ToDictionary(item => item.ControlId, item => item.Status, StringComparer.Ordinal)));
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToResponse(run, catalog));
    }

    private static async Task<IResult> CompleteAssessmentAsync(
        Guid assessmentId,
        AppDbContext db,
        DeploymentControlCatalog catalog,
        CancellationToken cancellationToken)
    {
        var run = await LoadAssessmentAsync(db, assessmentId, cancellationToken);
        if (run is null)
        {
            return Results.NotFound();
        }
        if (run.IsComplete)
        {
            return Results.Conflict(new { message = "This assessment is already complete." });
        }

        ApplyScore(run, AssessmentScoring.Calculate(
            catalog.Controls.Where(control => control.IsActive),
            run.Results.ToDictionary(item => item.ControlId, item => item.Status, StringComparer.Ordinal)));
        run.IsComplete = true;
        run.CompletedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToResponse(run, catalog));
    }

    private static async Task<AssessmentRun?> LoadAssessmentAsync(
        AppDbContext db,
        Guid assessmentId,
        CancellationToken cancellationToken) =>
        await db.AssessmentRuns
            .Include(run => run.Customer)
            .Include(run => run.Engagement)
            .Include(run => run.Results)
            .Include(run => run.Breakdowns)
            .Include(run => run.Recommendations)
            .SingleOrDefaultAsync(run => run.Id == assessmentId, cancellationToken);

    private static void ApplyScore(AssessmentRun run, AssessmentScoreCalculation score)
    {
        run.OverallScore = score.OverallScore;
        run.OverallPercentage = score.OverallPercentage;
        run.TotalControls = score.TotalControls;
        run.ImplementedControls = score.ImplementedControls;
        run.PartialControls = score.PartialControls;
        run.NotImplementedControls = score.NotImplementedControls;
        run.NotAssessedControls = score.NotAssessedControls;
        run.ExceptionControls = score.ExceptionControls;
        run.NotApplicableControls = score.NotApplicableControls;
        run.ReadinessRating = score.ReadinessRating;
        run.CalculatedUtc = DateTime.UtcNow;

        run.Breakdowns.Clear();
        foreach (var item in score.Breakdowns)
        {
            run.Breakdowns.Add(new AssessmentScoreBreakdown
            {
                BreakdownType = item.BreakdownType,
                Label = item.Label,
                Percentage = item.Percentage,
                TotalPoints = item.TotalPoints,
                EarnedPoints = item.EarnedPoints,
                GapCount = item.GapCount
            });
        }
        run.Recommendations.Clear();
        for (var index = 0; index < score.Recommendations.Count; index++)
        {
            var item = score.Recommendations[index];
            run.Recommendations.Add(new AssessmentRecommendation
            {
                Sequence = index + 1,
                Severity = item.Severity,
                Text = item.Text
            });
        }
    }

    private static object ToResponse(AssessmentRun run, DeploymentControlCatalog catalog)
    {
        var controls = catalog.Controls.ToDictionary(control => control.ControlId, StringComparer.Ordinal);
        return new
        {
            run.Id,
            run.CustomerId,
            customerName = run.Customer?.Name,
            run.EngagementId,
            engagementName = run.Engagement is null
                ? null
                : $"{run.Engagement.Salesperson} · {run.Engagement.MeetingDate?.ToString("yyyy-MM-dd") ?? "Date not set"}",
            run.Name,
            run.Assessor,
            run.Source,
            run.CatalogVersion,
            run.StartedUtc,
            run.CompletedUtc,
            run.IsComplete,
            score = new
            {
                run.OverallScore,
                run.OverallPercentage,
                run.TotalControls,
                run.ImplementedControls,
                run.PartialControls,
                run.NotImplementedControls,
                run.NotAssessedControls,
                run.ExceptionControls,
                run.NotApplicableControls,
                run.ReadinessRating,
                run.CalculatedUtc
            },
            breakdowns = run.Breakdowns.OrderBy(item => item.BreakdownType, StringComparer.Ordinal)
                .ThenBy(item => item.Label, StringComparer.Ordinal)
                .Select(item => new
                {
                    item.BreakdownType,
                    item.Label,
                    item.Percentage,
                    item.TotalPoints,
                    item.EarnedPoints,
                    item.GapCount
                }),
            recommendations = run.Recommendations.OrderBy(item => item.Sequence)
                .Select(item => new { item.Severity, item.Text }),
            criticalGaps = run.Results
                .Where(result => result.Status is "NotImplemented" or "NotAssessed" &&
                                 controls.TryGetValue(result.ControlId, out var control) &&
                                 control.PointValue >= 3)
                .OrderByDescending(result => controls[result.ControlId].PointValue)
                .ThenBy(result => result.ControlId, StringComparer.Ordinal)
                .Take(20)
                .Select(result => new
                {
                    result.ControlId,
                    name = controls[result.ControlId].Name,
                    controls[result.ControlId].PointValue,
                    result.Status
                }),
            controls = run.Results.OrderBy(result =>
                    controls.TryGetValue(result.ControlId, out var control) ? PhaseNumber(control.DeploymentPhase) : -1)
                .ThenBy(result =>
                    controls.TryGetValue(result.ControlId, out var control) ? control.Category : string.Empty,
                    StringComparer.Ordinal)
                .ThenBy(result => result.ControlId, StringComparer.Ordinal)
                .Select(result => new
                {
                    result.ControlId,
                    name = controls.TryGetValue(result.ControlId, out var control) ? control.Name : result.ControlId,
                    category = control?.Category,
                    deploymentPhase = control?.DeploymentPhase,
                    pointValue = control?.PointValue ?? 0,
                    wbsId = control?.WbsId,
                    status = result.Status,
                    result.Notes,
                    result.Evidence,
                    result.Assessor,
                    result.ReviewedUtc,
                    result.ExceptionReason
                }),
            tenantChangesEnabled = false
        };
    }

    private static string ActorName(ClaimsPrincipal user) =>
        user.Identity?.Name ??
        user.FindFirstValue(ClaimTypes.NameIdentifier) ??
        "Unknown user";

    private static int PhaseNumber(string phase)
    {
        const string prefix = "Phase";
        var index = phase.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        return index >= 0 &&
               int.TryParse(phase.AsSpan(index + prefix.Length), out var number)
            ? number
            : -1;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ValidateMaxLength(
        string key,
        string? value,
        int maximum,
        Dictionary<string, string[]> errors)
    {
        if (value?.Trim().Length > maximum)
        {
            errors[key] = [$"This field must be {maximum} characters or fewer."];
        }
    }
}
