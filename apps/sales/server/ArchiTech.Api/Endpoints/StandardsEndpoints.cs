using System.Security.Claims;
using ArchiTech.Api.Contracts;
using ArchiTech.Api.Data;
using ArchiTech.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ArchiTech.Api.Endpoints;

public static class StandardsEndpoints
{
    private static readonly HashSet<string> ResponseStatuses =
        ["Draft", "Resolved", "Deferred"];

    public static IEndpointRouteBuilder MapStandardsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/deployment")
            .RequireAuthorization("DeploymentAccess");
        group.MapGet("/engagements/{engagementId:guid}/discovery", GetDiscoveryAsync);
        group.MapPut(
                "/engagements/{engagementId:guid}/discovery/{decisionId}",
                SaveDecisionAsync)
            .RequireAuthorization("DeploymentAuthor");
        group.MapGet("/profiles/{customerId:guid}/discovery", GetProfileDiscoveryAsync);
        group.MapPut("/profiles/{customerId:guid}/discovery/{decisionId}", SaveProfileDecisionAsync)
            .RequireAuthorization("DeploymentAuthor");
        group.MapGet("/standards/secure-plus/summary", GetCatalogSummaryAsync);
        group.MapGet("/standards/secure-plus/work-items", ListWorkItemsAsync);
        group.MapGet("/standards/secure-plus/settings", ListBaselineSettingsAsync);
        group.MapGet("/standards/secure-plus/assessment-crosswalk", GetAssessmentCrosswalkAsync);
        group.MapGet("/standards/sources", ListReferenceSourcesAsync);
        group.MapGet("/standards/target-model", ListTargetModelAsync);
        return endpoints;
    }

    private static async Task<IResult> GetAssessmentCrosswalkAsync(
        AppDbContext db, DeploymentControlCatalog catalog, CancellationToken cancellationToken)
    {
        var decisions = await db.SecurePlusDecisions.AsNoTracking()
            .Where(item => item.CatalogVersion == StandardsCatalogSeeder.CatalogVersion)
            .ToListAsync(cancellationToken);
        var settings = await db.SecurePlusBaselineSettings.AsNoTracking()
            .Where(item => item.CatalogVersion == StandardsCatalogSeeder.CatalogVersion)
            .ToListAsync(cancellationToken);
        var crosswalk = DiscoveryCrosswalk.Build(decisions, settings, catalog.Controls);
        return Results.Ok(new
        {
            catalogVersion = StandardsCatalogSeeder.CatalogVersion,
            assessmentCatalogVersion = GraphEvidenceMappings.CatalogVersion,
            decisionCount = decisions.Count,
            activeControlCount = catalog.Controls.Count(item => item.IsActive),
            decisionsWithCandidates = crosswalk.Links.Select(item => item.DecisionId).Distinct(StringComparer.Ordinal).Count(),
            assessmentControlsWithCandidates = crosswalk.Links.Where(item => item.AssessmentControlExists)
                .Select(item => item.ControlId).Distinct(StringComparer.Ordinal).Count(),
            unresolvedReferenceCount = crosswalk.UnresolvedReferences.Count,
            notice = "Reference-format candidates and matching control IDs require human review. They do not establish equivalence, applicability, evidence, assessment status, or authorization.",
            links = crosswalk.Links,
            unresolvedReferences = crosswalk.UnresolvedReferences
        });
    }

    private static async Task<IResult> GetDiscoveryAsync(
        Guid engagementId,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await db.Engagements.AnyAsync(
                engagement => engagement.Id == engagementId,
                cancellationToken))
        {
            return Results.NotFound(new { message = "Engagement not found." });
        }

        var responses = await db.EngagementDecisionResponses.AsNoTracking()
            .Where(item => item.EngagementId == engagementId &&
                           item.CatalogVersion == StandardsCatalogSeeder.CatalogVersion)
            .ToListAsync(cancellationToken);
        return await GetDiscoveryCoreAsync(engagementId, null, responses, db, cancellationToken);
    }

    private static async Task<IResult> GetProfileDiscoveryAsync(
        Guid customerId, AppDbContext db, CancellationToken cancellationToken)
    {
        var profileId = await FindProfileIdAsync(customerId, db, cancellationToken);
        if (profileId is null)
            return Results.NotFound(new { message = "Deployment profile not found." });
        var responses = await db.ProfileDecisionResponses.AsNoTracking()
            .Where(item => item.DeploymentProfileId == profileId &&
                           item.CatalogVersion == StandardsCatalogSeeder.CatalogVersion)
            .ToListAsync(cancellationToken);
        return await GetDiscoveryCoreAsync(null, customerId, responses, db, cancellationToken);
    }

    private static async Task<IResult> GetDiscoveryCoreAsync(
        Guid? engagementId, Guid? customerId, IEnumerable<IDiscoveryDecisionResponse> storedResponses,
        AppDbContext db, CancellationToken cancellationToken)
    {
        var decisions = await db.SecurePlusDecisions.AsNoTracking()
            .Where(item => item.CatalogVersion == StandardsCatalogSeeder.CatalogVersion)
            .OrderBy(item => item.Phase)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var responses = storedResponses.ToDictionary(item => item.DecisionId, StringComparer.Ordinal);

        var items = decisions.Select(decision =>
        {
            responses.TryGetValue(decision.Id, out var response);
            return new
            {
                decision.Id,
                decision.BaselineReferences,
                decision.Phase,
                decision.Category,
                decision.Topic,
                decision.Question,
                decision.Rationale,
                decision.AnswerType,
                decision.SuggestedAnswers,
                decision.ConfigurationImpact,
                decision.DecisionOwner,
                decision.RequiredParticipants,
                decision.RequiredBy,
                decision.EvidenceRequired,
                decision.DefaultIfUndecided,
                decision.EngineeringNotes,
                status = response?.Status ?? "NotStarted",
                answer = response?.Answer,
                notes = response?.Notes,
                updatedBy = response?.UpdatedBy,
                updatedUtc = response?.UpdatedUtc
            };
        }).ToArray();

        return Results.Ok(new
        {
            engagementId,
            customerId,
            catalogVersion = StandardsCatalogSeeder.CatalogVersion,
            sourceStatus = "WIP - validate before treating as an approved standard",
            totalQuestions = items.Length,
            resolvedQuestions = items.Count(item => item.status == "Resolved"),
            draftQuestions = items.Count(item => item.status == "Draft"),
            deferredQuestions = items.Count(item => item.status == "Deferred"),
            notStartedQuestions = items.Count(item => item.status == "NotStarted"),
            tenantChangesEnabled = false,
            questions = items
        });
    }

    private static async Task<IResult> SaveDecisionAsync(
        Guid engagementId,
        string decisionId,
        SaveEngagementDecisionRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await db.Engagements.AnyAsync(
                engagement => engagement.Id == engagementId,
                cancellationToken))
        {
            return Results.NotFound(new { message = "Engagement not found." });
        }

        return await SaveDecisionCoreAsync(engagementId, null, decisionId, request, user, db, cancellationToken);
    }

    private static async Task<IResult> SaveProfileDecisionAsync(
        Guid customerId, string decisionId, SaveEngagementDecisionRequest request,
        ClaimsPrincipal user, AppDbContext db, CancellationToken cancellationToken)
    {
        var profileId = await FindProfileIdAsync(customerId, db, cancellationToken);
        if (profileId is null)
            return Results.NotFound(new { message = "Deployment profile not found." });
        return await SaveDecisionCoreAsync(null, profileId, decisionId, request, user, db, cancellationToken);
    }

    private static Task<Guid?> FindProfileIdAsync(Guid customerId, AppDbContext db, CancellationToken cancellationToken) =>
        db.DeploymentProfiles.AsNoTracking().Where(profile => profile.CustomerId == customerId)
            .Select(profile => (Guid?)profile.Id).SingleOrDefaultAsync(cancellationToken);

    private static async Task<IResult> SaveDecisionCoreAsync(
        Guid? engagementId, Guid? profileId, string decisionId, SaveEngagementDecisionRequest request,
        ClaimsPrincipal user, AppDbContext db, CancellationToken cancellationToken)
    {
        var decisionExists = await db.SecurePlusDecisions.AnyAsync(
            item => item.Id == decisionId &&
                    item.CatalogVersion == StandardsCatalogSeeder.CatalogVersion,
            cancellationToken);
        if (!decisionExists)
        {
            return Results.NotFound(new { message = "Discovery decision is not in the active catalog." });
        }

        var status = request.Status?.Trim();
        if (status is null || !ResponseStatuses.Contains(status))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["status"] = ["Choose Draft, Resolved, or Deferred."]
            });
        }

        var answer = Normalize(request.Answer);
        var notes = Normalize(request.Notes);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        ValidateMaxLength("answer", answer, 4000, errors);
        ValidateMaxLength("notes", notes, 2000, errors);
        if (status == "Resolved" && answer is null)
        {
            errors["answer"] = ["Add the customer decision before marking this item resolved."];
        }
        if (status == "Deferred" && notes is null)
        {
            errors["notes"] = ["Record why this decision is deferred."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        IDiscoveryDecisionResponse? response;
        if (engagementId is Guid engagement)
        {
            response = await db.EngagementDecisionResponses.SingleOrDefaultAsync(
                item => item.EngagementId == engagement &&
                        item.CatalogVersion == StandardsCatalogSeeder.CatalogVersion &&
                        item.DecisionId == decisionId, cancellationToken);
            if (response is null)
            {
                var created = new EngagementDecisionResponse
                {
                    EngagementId = engagement,
                    CatalogVersion = StandardsCatalogSeeder.CatalogVersion,
                    DecisionId = decisionId,
                    Status = status,
                    UpdatedBy = ActorName(user)
                };
                db.EngagementDecisionResponses.Add(created);
                response = created;
            }
        }
        else
        {
            var profile = profileId!.Value;
            response = await db.ProfileDecisionResponses.SingleOrDefaultAsync(
                item => item.DeploymentProfileId == profile &&
                        item.CatalogVersion == StandardsCatalogSeeder.CatalogVersion &&
                        item.DecisionId == decisionId, cancellationToken);
            if (response is null)
            {
                var created = new ProfileDecisionResponse
                {
                    DeploymentProfileId = profile,
                    CatalogVersion = StandardsCatalogSeeder.CatalogVersion,
                    DecisionId = decisionId,
                    Status = status,
                    UpdatedBy = ActorName(user)
                };
                db.ProfileDecisionResponses.Add(created);
                response = created;
            }
        }

        response.Status = status;
        response.Answer = answer;
        response.Notes = notes;
        response.UpdatedBy = ActorName(user);
        response.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new
        {
            response.DecisionId,
            response.Status,
            response.Answer,
            response.Notes,
            response.UpdatedBy,
            response.UpdatedUtc
        });
    }

    private static async Task<IResult> GetCatalogSummaryAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var workItems = await db.SecurePlusWorkItems.AsNoTracking()
            .Where(item => item.CatalogVersion == StandardsCatalogSeeder.CatalogVersion)
            .ToListAsync(cancellationToken);
        var settings = await db.SecurePlusBaselineSettings.AsNoTracking()
            .Where(item => item.CatalogVersion == StandardsCatalogSeeder.CatalogVersion)
            .ToListAsync(cancellationToken);
        var sources = await db.StandardsReferenceSources.AsNoTracking()
            .Where(item => item.CatalogVersion == StandardsCatalogSeeder.LibraryInventoryVersion)
            .ToListAsync(cancellationToken);
        var phaseSummary = workItems
            .GroupBy(item => item.Phase)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new
            {
                phase = group.Key,
                taskCount = group.Count(),
                hoursMin = group.Sum(item => item.EstimatedHoursMin),
                hoursMax = group.Sum(item => item.EstimatedHoursMax)
            });
        var modeSummary = settings
            .GroupBy(item => item.ImplementationMode)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new { mode = group.Key, count = group.Count() });
        var licenseSummary = settings
            .GroupBy(item => LicenseFamily(item.LicenseRequirement))
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new { family = group.Key, count = group.Count() });
        var sourceAreaSummary = sources
            .GroupBy(item => item.Area)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new { area = group.Key, count = group.Count() });
        var sourceStatusSummary = sources
            .GroupBy(item => item.Status)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new { status = group.Key, count = group.Count() });
        var modelTableCount = await db.TargetModelTables.CountAsync(
            item => item.CatalogVersion == StandardsCatalogSeeder.TargetModelVersion,
            cancellationToken);
        var modelColumnCount = await db.TargetModelColumns.CountAsync(
            item => item.CatalogVersion == StandardsCatalogSeeder.TargetModelVersion,
            cancellationToken);

        return Results.Ok(new
        {
            catalogVersion = StandardsCatalogSeeder.CatalogVersion,
            sourceStatus = "WIP - engineering and customer review required",
            discoveryQuestionCount = await db.SecurePlusDecisions.CountAsync(
                item => item.CatalogVersion == StandardsCatalogSeeder.CatalogVersion,
                cancellationToken),
            workItemCount = workItems.Count,
            workItemHoursMin = workItems.Sum(item => item.EstimatedHoursMin),
            workItemHoursMax = workItems.Sum(item => item.EstimatedHoursMax),
            baselineSettingCount = settings.Count,
            targetModelTableCount = modelTableCount,
            targetModelColumnCount = modelColumnCount,
            targetModelVersion = StandardsCatalogSeeder.TargetModelVersion,
            phases = phaseSummary,
            implementationModes = modeSummary,
            licenseFamilies = licenseSummary,
            sourceInventoryVersion = StandardsCatalogSeeder.LibraryInventoryVersion,
            sourceCount = sources.Count,
            sourceAreas = sourceAreaSummary,
            sourceStatuses = sourceStatusSummary,
            tenantChangesEnabled = false,
            modelReferenceOnly = true
        });
    }

    private static async Task<IResult> ListReferenceSourcesAsync(
        string? area,
        string? status,
        string? search,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var query = db.StandardsReferenceSources.AsNoTracking()
            .Where(item => item.CatalogVersion == StandardsCatalogSeeder.LibraryInventoryVersion);
        if (!string.IsNullOrWhiteSpace(area))
        {
            query = query.Where(item => item.Area == area);
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(item => item.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(item =>
                item.Name.Contains(term) ||
                item.FileName.Contains(term) ||
                item.Area.Contains(term) ||
                item.Description.Contains(term) ||
                item.UsageBoundary.Contains(term));
        }

        var sources = await query
            .OrderBy(item => item.Area)
            .ThenBy(item => item.Name)
            .Select(item => new
            {
                item.Id,
                item.Area,
                item.Name,
                item.FileName,
                item.Format,
                item.SourceVersion,
                item.Status,
                item.UsageBoundary,
                item.Description,
                item.ImportedRecordCount,
                item.RecordUnit
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(new
        {
            catalogVersion = StandardsCatalogSeeder.LibraryInventoryVersion,
            inventoryOnly = true,
            sourceCount = sources.Count,
            sources
        });
    }

    private static async Task<IResult> ListWorkItemsAsync(
        string? phase,
        string? search,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var query = db.SecurePlusWorkItems.AsNoTracking()
            .Where(item => item.CatalogVersion == StandardsCatalogSeeder.CatalogVersion);
        if (!string.IsNullOrWhiteSpace(phase))
        {
            query = query.Where(item => item.Phase == phase);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(item =>
                item.Id.Contains(term) ||
                item.TaskName.Contains(term) ||
                item.SubTaskName.Contains(term) ||
                item.Control.Contains(term) ||
                item.ControlDescription.Contains(term) ||
                item.LicenseRequirement.Contains(term));
        }

        var items = await query.OrderBy(item => item.Phase)
            .ThenBy(item => item.TaskNumber)
            .ThenBy(item => item.SubTaskNumber)
            .Select(item => new
            {
                item.Id,
                item.PhaseReference,
                item.Phase,
                item.Workstream,
                item.TaskNumber,
                item.TaskName,
                item.SubTaskNumber,
                item.SubTaskName,
                item.Control,
                item.ControlDescription,
                item.Deliverable,
                item.Dependency,
                item.AppliesWhen,
                item.LicenseRequirement,
                item.EstimatedHoursMin,
                item.EstimatedHoursMax,
                item.DurationDaysMin,
                item.DurationDaysMax,
                item.Parallelizable,
                item.SupportHandoff,
                item.ComplexityDriver
            })
            .ToListAsync(cancellationToken);
        return Results.Ok(new
        {
            catalogVersion = StandardsCatalogSeeder.CatalogVersion,
            sourceStatus = "WIP",
            count = items.Count,
            workItems = items
        });
    }

    private static async Task<IResult> ListBaselineSettingsAsync(
        string? phase,
        string? mode,
        string? search,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var query = db.SecurePlusBaselineSettings.AsNoTracking()
            .Where(item => item.CatalogVersion == StandardsCatalogSeeder.CatalogVersion);
        if (!string.IsNullOrWhiteSpace(phase))
        {
            query = query.Where(item => item.Phase == phase);
        }
        if (!string.IsNullOrWhiteSpace(mode))
        {
            query = query.Where(item => item.ImplementationMode == mode);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(item =>
                item.Control.Contains(term) ||
                item.SettingName.Contains(term) ||
                item.TargetAction.Contains(term) ||
                item.LicenseRequirement.Contains(term) ||
                item.Workstream.Contains(term));
        }

        var items = await query.OrderBy(item => item.Phase)
            .ThenBy(item => item.Control)
            .ThenBy(item => item.SettingName)
            .Select(item => new
            {
                item.Id,
                item.BaselineReference,
                item.Phase,
                item.Workstream,
                item.Control,
                item.SettingCategory,
                item.SettingName,
                item.TargetAction,
                item.ReviewPath,
                item.DeploymentMethod,
                item.ValidationMethod,
                item.EvidenceRequired,
                item.AppliesWhen,
                item.LicenseRequirement,
                item.ImplementationMode,
                item.PilotGroup,
                item.Exclusions,
                item.ExceptionCriteria,
                item.RollbackMethod,
                item.ReviewCadence,
                item.SupportOwner
            })
            .ToListAsync(cancellationToken);
        return Results.Ok(new
        {
            catalogVersion = StandardsCatalogSeeder.CatalogVersion,
            sourceStatus = "WIP - configuration actions are reference-only and cannot execute",
            count = items.Count,
            settings = items
        });
    }

    private static async Task<IResult> ListTargetModelAsync(
        string? search,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var tables = await db.TargetModelTables.AsNoTracking()
            .Where(item => item.CatalogVersion == StandardsCatalogSeeder.TargetModelVersion)
            .Include(item => item.Columns)
            .OrderBy(item => item.Schema)
            .ThenBy(item => item.Name)
            .ToListAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            tables = tables.Where(item =>
                    item.Schema.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    item.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    item.Description.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    item.Columns.Any(column =>
                        column.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                        column.DataType.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                        (column.ForeignKeyTarget?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)))
                .ToList();
        }

        return Results.Ok(new
        {
            modelReferenceOnly = true,
            catalogVersion = StandardsCatalogSeeder.TargetModelVersion,
            sourceStatus = "Design reference only - not the application's deployed schema",
            count = tables.Count,
            tables = tables.Select(item => new
            {
                item.Schema,
                item.Name,
                item.Description,
                item.EstimatedRows,
                item.PrimarySources,
                item.TemporalHistory,
                columns = item.Columns.OrderBy(column => column.ColumnOrdinal).Select(column => new
                {
                    column.ColumnOrdinal,
                    column.Name,
                    column.DataType,
                    column.IsPrimaryKey,
                    column.IsForeignKey,
                    column.ForeignKeyTarget,
                    column.IsUniqueKeyPart,
                    column.IsIndexed
                })
            })
        });
    }

    private static string ActorName(ClaimsPrincipal user) =>
        user.Identity?.Name ??
        user.FindFirstValue(ClaimTypes.NameIdentifier) ??
        "Unknown user";

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string LicenseFamily(string requirement)
    {
        if (requirement.Contains("none", StringComparison.OrdinalIgnoreCase) ||
            requirement.Contains("no specific license", StringComparison.OrdinalIgnoreCase) ||
            requirement.Contains("no premium license", StringComparison.OrdinalIgnoreCase))
        {
            return "No specific license listed";
        }

        if (requirement.Contains("Entra", StringComparison.OrdinalIgnoreCase)) return "Microsoft Entra";
        if (requirement.Contains("Intune", StringComparison.OrdinalIgnoreCase)) return "Microsoft Intune";
        if (requirement.Contains("Defender", StringComparison.OrdinalIgnoreCase)) return "Microsoft Defender";
        if (requirement.Contains("Purview", StringComparison.OrdinalIgnoreCase)) return "Microsoft Purview";
        if (requirement.Contains("Microsoft 365", StringComparison.OrdinalIgnoreCase) ||
            requirement.Contains("Exchange", StringComparison.OrdinalIgnoreCase) ||
            requirement.Contains("SharePoint", StringComparison.OrdinalIgnoreCase) ||
            requirement.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) ||
            requirement.Contains("Teams", StringComparison.OrdinalIgnoreCase) ||
            requirement.Contains("Copilot", StringComparison.OrdinalIgnoreCase))
        {
            return "Microsoft 365 workloads";
        }

        return "Other / tenant-dependent";
    }

    private static void ValidateMaxLength(
        string key,
        string? value,
        int maximum,
        Dictionary<string, string[]> errors)
    {
        if (value?.Length > maximum)
        {
            errors[key] = [$"This field must be {maximum} characters or fewer."];
        }
    }
}
