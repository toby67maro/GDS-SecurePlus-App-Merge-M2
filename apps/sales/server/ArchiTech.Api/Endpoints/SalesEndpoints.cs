using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using ArchiTech.Api.Contracts;
using ArchiTech.Api.Data;
using ArchiTech.Api.Domain;
using ArchiTech.Api.Integrations;
using ArchiTech.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace ArchiTech.Api.Endpoints;

public static class SalesEndpoints
{
    public static IEndpointRouteBuilder MapSalesEndpoints(
        this IEndpointRouteBuilder endpoints,
        string databaseProvider)
    {
        endpoints.MapGet("/api/health", (PlannerHandoffService handoff) =>
            Results.Ok(new
            {
                status = "ready",
                databaseProvider,
                plannerHandoff = handoff.IsConfigured ? "configured" : "not-configured"
            })).AllowAnonymous();

        endpoints.MapPost("/api/org-chart/preview", (OrgChartPreviewRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.FileName) || string.IsNullOrWhiteSpace(request.ContentBase64) ||
                request.ContentBase64.Length > 2_800_000)
                return Results.BadRequest(new { message = "Select a CSV or XLSX file no larger than 2 MB." });
            try
            {
                var bytes = Convert.FromBase64String(request.ContentBase64);
                return Results.Ok(new { departments = OrgChartImport.Parse(request.FileName, bytes) });
            }
            catch (Exception exception) when (exception is FormatException or InvalidDataException or
                System.Text.DecoderFallbackException or Microsoft.VisualBasic.FileIO.MalformedLineException or
                System.Xml.XmlException or ArgumentException)
            {
                return Results.BadRequest(new { message = exception is InvalidDataException
                    ? exception.Message : "The file could not be read as a valid CSV or XLSX department list." });
            }
        });

        endpoints.MapPost("/api/estimate", async (
            EstimateRequest request,
            AppDbContext db,
            CancellationToken cancellationToken) =>
        {
            var answers = AnswerCatalog.Normalize(request.Answers);
            if (!answers.IsValid)
            {
                return Results.ValidationProblem(
                    answers.Errors.ToDictionary(pair => pair.Key, pair => pair.Value));
            }

            var catalog = await db.WbsTasks.AsNoTracking().ToListAsync(cancellationToken);
            return Results.Ok(ToEstimateResponse(WbsEstimator.Calculate(catalog, answers.Values)));
        });

        endpoints.MapGet("/api/engagements", async (
            AppDbContext db,
            ClaimsPrincipal user,
            CancellationToken cancellationToken) =>
        {
            var engagements = await OwnerScope.Apply(db.Engagements, user)
                .AsNoTracking()
                .Include(engagement => engagement.Customer)
                .Include(engagement => engagement.Tasks)
                .OrderByDescending(engagement => engagement.CreatedUtc)
                .Select(engagement => new EngagementListItem(
                    engagement.Id,
                    engagement.Customer.Name,
                    engagement.Salesperson,
                    engagement.MeetingDate,
                    engagement.CreatedUtc,
                    engagement.OneTimeHoursMin,
                    engagement.OneTimeHoursMax,
                    engagement.MonthlyHoursMin,
                    engagement.MonthlyHoursMax,
                    engagement.Tasks.Count,
                    engagement.PlannerHandoffStatus,
                    engagement.DeploymentSyncStatus,
                    engagement.OwnerEmail))
                .ToListAsync(cancellationToken);

            return Results.Ok(engagements);
        });

        endpoints.MapGet("/api/engagements/{id:guid}", async (
            Guid id,
            AppDbContext db,
            ClaimsPrincipal user,
            CancellationToken cancellationToken) =>
        {
            var engagement = await LoadEngagementAsync(db, id, user, cancellationToken);
            return engagement is null
                ? Results.NotFound()
                : Results.Ok(ToDetailResponse(engagement));
        });

        endpoints.MapGet("/api/engagements/{id:guid}/package-comparison", async (
            Guid id, AppDbContext db, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var engagement = await LoadEngagementAsync(db, id, user, cancellationToken);
            return engagement is null ? Results.NotFound() : Results.Ok(SecurePlusPackages.Compare(engagement.Answers));
        });

        endpoints.MapGet("/api/engagements/{id:guid}/journey-history", async (
            Guid id, AppDbContext db, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        {
            var customerId = await OwnerScope.Apply(db.Engagements, user).AsNoTracking()
                .Where(item => item.Id == id).Select(item => (Guid?)item.CustomerId)
                .SingleOrDefaultAsync(cancellationToken);
            if (customerId is null) return Results.NotFound();
            var snapshots = await OwnerScope.Apply(db.Engagements, user).AsNoTracking()
                .Where(item => item.CustomerId == customerId)
                .Include(item => item.Answers)
                .OrderBy(item => item.CreatedUtc).ThenBy(item => item.Id)
                .ToArrayAsync(cancellationToken);
            var result = new List<object>();
            Dictionary<string, string[]>? previous = null;
            foreach (var snapshot in snapshots)
            {
                var current = snapshot.Answers.GroupBy(item => item.FieldKey, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key,
                        group => group.Select(item => item.Value).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                        StringComparer.Ordinal);
                var changes = previous is null
                    ? Array.Empty<object>()
                    : previous.Keys.Union(current.Keys, StringComparer.Ordinal)
                        .OrderBy(key => key, StringComparer.Ordinal)
                        .Where(key => !previous.GetValueOrDefault(key, []).SequenceEqual(
                            current.GetValueOrDefault(key, []), StringComparer.OrdinalIgnoreCase))
                        .Select(key => (object)new
                        {
                            field = key,
                            before = previous.GetValueOrDefault(key, []),
                            after = current.GetValueOrDefault(key, [])
                        }).ToArray();
                result.Add(new
                {
                    snapshot.Id, snapshot.CreatedUtc, snapshot.Salesperson,
                    snapshot.OneTimeHoursMin, snapshot.OneTimeHoursMax,
                    snapshot.MonthlyHoursMin, snapshot.MonthlyHoursMax, changes
                });
                previous = current;
            }
            return Results.Ok(result);
        });

        endpoints.MapGet("/api/engagements/{id:guid}/export.json", async (
            Guid id,
            AppDbContext db,
            ClaimsPrincipal user,
            CancellationToken cancellationToken) =>
        {
            var engagement = await LoadEngagementAsync(db, id, user, cancellationToken);
            return engagement is null
                ? Results.NotFound()
                : DownloadJson(
                    new
                    {
                        schemaVersion = 1,
                        exportedUtc = DateTime.UtcNow,
                        engagement = ToDetailResponse(engagement),
                        packageComparison = SecurePlusPackages.Compare(engagement.Answers)
                    },
                    $"{ToFileSlug(engagement.Customer.Name)}-engagement.json");
        });

        endpoints.MapGet("/api/exports/workspace.json", async (
            AppDbContext db,
            DeploymentControlCatalog deploymentCatalog,
            ClaimsPrincipal user,
            CancellationToken cancellationToken) =>
        {
            var seeAll = OwnerScope.CanSeeAll(user);
            var ownedCustomerIds = OwnerScope.Apply(db.Engagements, user).Select(engagement => engagement.CustomerId);
            var customers = await db.Customers
                .AsNoTracking()
                .Where(customer => seeAll || ownedCustomerIds.Contains(customer.Id))
                .OrderBy(customer => customer.Name)
                .Select(customer => new
                {
                    customer.Id,
                    customer.Name,
                    customer.ContactName,
                    customer.ContactEmail,
                    customer.CreatedUtc
                })
                .ToListAsync(cancellationToken);
            var engagements = await OwnerScope.Apply(db.Engagements, user)
                .AsNoTracking()
                .Include(engagement => engagement.Customer)
                .Include(engagement => engagement.Answers)
                .Include(engagement => engagement.Tasks)
                    .ThenInclude(task => task.WbsTask)
                .OrderBy(engagement => engagement.CreatedUtc)
                .ToListAsync(cancellationToken);
            var catalog = await db.WbsTasks
                .AsNoTracking()
                .OrderBy(task => task.Sequence)
                .Select(task => new
                {
                    task.Id,
                    task.Sequence,
                    task.Phase,
                    task.Workstream,
                    task.Name,
                    task.Deliverable,
                    task.RuleKey,
                    task.MinHours,
                    task.MaxHours,
                    task.DurationMin,
                    task.DurationMax,
                    task.SupportHandoff
                })
                .ToListAsync(cancellationToken);

            var canReadDeploymentData = user.IsInRole(DeploymentRoles.Engineer) ||
                                        user.IsInRole(DeploymentRoles.Approver) ||
                                        user.IsInRole(DeploymentRoles.Operator) ||
                                        user.IsInRole(DeploymentRoles.Administrator);
            if (!canReadDeploymentData)
            {
                return DownloadJson(
                    new
                    {
                        schemaVersion = 6,
                        exportedUtc = DateTime.UtcNow,
                        databaseProvider,
                        customers,
                        engagements = engagements.Select(ToDetailResponse).ToArray(),
                        wbsCatalog = catalog,
                        deploymentAccess = false
                    },
                    "archi-tech-sales-workspace.json");
            }

            var deploymentProfiles = await db.DeploymentProfiles
                .AsNoTracking()
                .Include(profile => profile.Findings)
                .Include(profile => profile.SetupSteps)
                .OrderBy(profile => profile.CustomerId)
                .Select(profile => new
                {
                    profile.Id,
                    profile.CustomerId,
                    profile.TenantId,
                    profile.PrimaryDomain,
                    profile.EntraApplicationClientId,
                    profile.CreatedUtc,
                    profile.UpdatedUtc,
                    setupSteps = profile.SetupSteps
                        .OrderBy(step => step.StepKey)
                        .Select(step => new
                        {
                            step.StepKey,
                            step.IsComplete,
                            step.UpdatedUtc
                        }),
                    findings = profile.Findings
                        .OrderBy(finding => finding.ControlId)
                        .Select(finding => new
                        {
                            finding.ControlId,
                            finding.Status,
                            finding.Notes,
                            finding.Evidence,
                            finding.ReviewedBy,
                            finding.UpdatedUtc
                        })
                })
                .ToListAsync(cancellationToken);
            var assessments = await db.AssessmentRuns
                .AsNoTracking()
                .Include(run => run.Results)
                .Include(run => run.Breakdowns)
                .Include(run => run.Recommendations)
                .OrderByDescending(run => run.StartedUtc)
                .Select(run => new
                {
                    run.Id,
                    run.CustomerId,
                    run.EngagementId,
                    run.Name,
                    run.Assessor,
                    run.Source,
                    run.CatalogVersion,
                    run.StartedUtc,
                    run.CompletedUtc,
                    run.IsComplete,
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
                    run.CalculatedUtc,
                    results = run.Results
                        .OrderBy(result => result.ControlId)
                        .Select(result => new
                        {
                            result.ControlId,
                            result.Status,
                            result.Notes,
                            result.Evidence,
                            result.Assessor,
                            result.ReviewedUtc,
                            result.ExceptionReason
                        }),
                    breakdowns = run.Breakdowns
                        .OrderBy(item => item.BreakdownType)
                        .ThenBy(item => item.Label)
                        .Select(item => new
                        {
                            item.BreakdownType,
                            item.Label,
                            item.Percentage,
                            item.TotalPoints,
                            item.EarnedPoints,
                            item.GapCount
                        }),
                    recommendations = run.Recommendations
                        .OrderBy(item => item.Sequence)
                        .Select(item => new { item.Sequence, item.Severity, item.Text })
                })
                .ToListAsync(cancellationToken);
            var changePlans = await db.DeploymentChangePlans
                .AsNoTracking()
                .Include(plan => plan.Items)
                .Include(plan => plan.Events)
                .OrderByDescending(plan => plan.CreatedUtc)
                .Select(plan => new
                {
                    plan.Id,
                    plan.CustomerId,
                    plan.AssessmentRunId,
                    plan.Title,
                    plan.Notes,
                    plan.Status,
                    plan.RequestedById,
                    plan.RequestedBy,
                    plan.CreatedUtc,
                    plan.UpdatedUtc,
                    plan.SubmittedUtc,
                    plan.ReviewedById,
                    plan.ReviewedBy,
                    plan.ReviewedUtc,
                    plan.ReviewDecision,
                    plan.ReviewNotes,
                    items = plan.Items
                        .OrderBy(item => item.Sequence)
                        .Select(item => new
                        {
                            item.ControlId,
                            item.ControlName,
                            item.AssessmentStatus,
                            item.Sequence
                        }),
                    events = plan.Events
                        .OrderBy(item => item.OccurredUtc)
                        .ThenBy(item => item.Id)
                        .Select(item => new
                        {
                            item.EventType,
                            item.ActorId,
                            item.Actor,
                            item.Notes,
                            item.OccurredUtc
                        })
                })
                .ToListAsync(cancellationToken);
            var discoveryResponses = await db.EngagementDecisionResponses
                .AsNoTracking()
                .Where(item => item.CatalogVersion == StandardsCatalogSeeder.CatalogVersion)
                .OrderBy(item => item.EngagementId)
                .ThenBy(item => item.DecisionId)
                .Select(item => new
                {
                    item.EngagementId,
                    item.CatalogVersion,
                    item.DecisionId,
                    item.Status,
                    item.Answer,
                    item.Notes,
                    item.UpdatedBy,
                    item.UpdatedUtc
                })
                .ToListAsync(cancellationToken);
            var profileDiscoveryResponses = await db.ProfileDecisionResponses
                .AsNoTracking()
                .Where(item => item.CatalogVersion == StandardsCatalogSeeder.CatalogVersion)
                .OrderBy(item => item.DeploymentProfileId)
                .ThenBy(item => item.DecisionId)
                .Select(item => new
                {
                    item.DeploymentProfileId,
                    item.CatalogVersion,
                    item.DecisionId,
                    item.Status,
                    item.Answer,
                    item.Notes,
                    item.UpdatedBy,
                    item.UpdatedUtc
                })
                .ToListAsync(cancellationToken);
            var securePlusDecisions = await db.SecurePlusDecisions
                .AsNoTracking()
                .Where(item => item.CatalogVersion == StandardsCatalogSeeder.CatalogVersion)
                .OrderBy(item => item.Id)
                .ToListAsync(cancellationToken);
            var securePlusWorkItems = await db.SecurePlusWorkItems
                .AsNoTracking()
                .Where(item => item.CatalogVersion == StandardsCatalogSeeder.CatalogVersion)
                .OrderBy(item => item.Id)
                .ToListAsync(cancellationToken);
            var securePlusBaselineSettings = await db.SecurePlusBaselineSettings
                .AsNoTracking()
                .Where(item => item.CatalogVersion == StandardsCatalogSeeder.CatalogVersion)
                .OrderBy(item => item.Id)
                .ToListAsync(cancellationToken);
            var targetModelTables = await db.TargetModelTables
                .AsNoTracking()
                .Where(item => item.CatalogVersion == StandardsCatalogSeeder.TargetModelVersion)
                .OrderBy(item => item.Schema)
                .ThenBy(item => item.Name)
                .Select(item => new
                {
                    item.Id,
                    item.CatalogVersion,
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
                .ToListAsync(cancellationToken);
            var standardsReferenceSources = await db.StandardsReferenceSources
                .AsNoTracking()
                .Where(item => item.CatalogVersion == StandardsCatalogSeeder.LibraryInventoryVersion)
                .OrderBy(item => item.Area)
                .ThenBy(item => item.Name)
                .Select(item => new
                {
                    item.Id,
                    item.CatalogVersion,
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
            return DownloadJson(
                new
                {
                    schemaVersion = 9,
                    exportedUtc = DateTime.UtcNow,
                    databaseProvider,
                    customers,
                    engagements = engagements.Select(ToDetailResponse).ToArray(),
                    wbsCatalog = catalog,
                    deploymentCatalog = deploymentCatalog.Controls,
                    deploymentProfiles,
                    assessments,
                    changePlans,
                    securePlusStandards = new
                    {
                        catalogVersion = StandardsCatalogSeeder.CatalogVersion,
                        targetModelVersion = StandardsCatalogSeeder.TargetModelVersion,
                        sourceStatus = "WIP - validate before treating as an approved standard",
                        discoveryDecisions = securePlusDecisions,
                        discoveryResponses,
                        profileDiscoveryResponses,
                        workItems = securePlusWorkItems,
                        baselineSettings = securePlusBaselineSettings,
                        targetModelReference = targetModelTables,
                        sourceInventory = new
                        {
                            catalogVersion = StandardsCatalogSeeder.LibraryInventoryVersion,
                            sources = standardsReferenceSources
                        }
                    },
                    deploymentAccess = true
                },
                "archi-tech-workspace.json");
        });

        endpoints.MapGet("/api/engagements/{id:guid}/roadmap.xlsx", async (
            Guid id,
            AppDbContext db,
            CustomerRoadmapWorkbook roadmap,
            DeploymentControlCatalog controlCatalog,
            ClaimsPrincipal user,
            CancellationToken cancellationToken) =>
        {
            var engagement = await LoadEngagementAsync(db, id, user, cancellationToken);
            return engagement is null
                ? Results.NotFound()
                : Results.File(
                    roadmap.Build(engagement, controlCatalog.Controls),
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    $"{ToFileSlug(engagement.Customer.Name)}-customer-roadmap.xlsx");
        });

        endpoints.MapPost("/api/engagements", CreateEngagementAsync);
        endpoints.MapPost("/api/engagements/{id:guid}/planner-handoff", RetryPlannerHandoffAsync);
        endpoints.MapPost("/api/engagements/{id:guid}/planner-handoff/confirm", ConfirmPlannerHandoffAsync)
            .RequireAuthorization("DeploymentExecution");
        return endpoints;
    }

    private static IResult DownloadJson(object value, string fileName) =>
        Results.File(
            JsonSerializer.SerializeToUtf8Bytes(
                value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            "application/json",
            fileName);

    private static string ToFileSlug(string value)
    {
        var slug = new string(value
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray());
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return slug.Trim('-') is { Length: > 0 } normalized ? normalized : "customer";
    }

    private static async Task<IResult> CreateEngagementAsync(
        CreateEngagementRequest request,
        AppDbContext db,
        PlannerHandoffService handoff,
        DeploymentBridge bridge,
        [Microsoft.AspNetCore.Mvc.FromServices] FabricEventPublisher fabric,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var ownerId = OwnerScope.Id(user);
        if (ownerId is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                title: "A stable authenticated identity is required to submit an engagement.");
        }
        var errors = ValidateRequest(request);
        var answers = AnswerCatalog.Normalize(request.Answers);
        foreach (var (key, messages) in answers.Errors)
        {
            errors[key] = messages;
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var companyName = request.CompanyName!.Trim();
        var normalizedName = NormalizeCompanyName(companyName);
        var customer = await db.Customers
            .FirstOrDefaultAsync(item => item.NormalizedName == normalizedName, cancellationToken);

        if (customer is null)
        {
            customer = new Customer
            {
                Id = Guid.NewGuid(),
                Name = companyName,
                NormalizedName = normalizedName,
                ContactName = NormalizeOptional(request.ContactName),
                ContactEmail = NormalizeOptional(request.ContactEmail),
                CreatedUtc = DateTime.UtcNow
            };
            db.Customers.Add(customer);
        }
        else
        {
            customer.Name = companyName;
            customer.ContactName = NormalizeOptional(request.ContactName) ?? customer.ContactName;
            customer.ContactEmail = NormalizeOptional(request.ContactEmail) ?? customer.ContactEmail;
        }

        if (!await db.DeploymentProfiles.AnyAsync(
                profile => profile.CustomerId == customer.Id,
                cancellationToken))
        {
            db.DeploymentProfiles.Add(new DeploymentProfile
            {
                Id = Guid.NewGuid(),
                CustomerId = customer.Id,
                Customer = customer,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });
        }

        var catalog = await db.WbsTasks.ToListAsync(cancellationToken);
        var estimate = WbsEstimator.Calculate(catalog, answers.Values);
        var engagement = new Engagement
        {
            Id = Guid.NewGuid(),
            Customer = customer,
            Salesperson = request.Salesperson!.Trim(),
            MeetingDate = request.MeetingDate,
            Users = request.Users,
            Endpoints = request.Endpoints,
            Notes = NormalizeOptional(request.Notes),
            OneTimeHoursMin = estimate.OneTimeHoursMin,
            OneTimeHoursMax = estimate.OneTimeHoursMax,
            MonthlyHoursMin = estimate.MonthlyHoursMin,
            MonthlyHoursMax = estimate.MonthlyHoursMax,
            PlannerHandoffStatus = handoff.IsConfigured ? "Pending" : "NotConfigured",
            OwnerId = ownerId,
            OwnerEmail = OwnerScope.Email(user),
            DeploymentSyncStatus = bridge.IsConfigured ? "Pending" : "NotConfigured",
            ExperienceJson = SerializeExperience(request.Experience),
            CreatedUtc = DateTime.UtcNow,
            Answers = answers.Values.SelectMany(pair => pair.Value.Select(value => new EngagementAnswer
            {
                FieldKey = pair.Key,
                Value = value
            })).ToArray(),
            Tasks = estimate.Tasks.Select(task => new EngagementTask
            {
                Id = Guid.NewGuid(),
                WbsTaskId = task.Id,
                WbsTask = task,
                MinHours = task.MinHours,
                MaxHours = task.MaxHours
            }).ToArray()
        };

        db.Engagements.Add(engagement);
        await db.SaveChangesAsync(cancellationToken);

        if (handoff.IsConfigured)
        {
            await ApplyHandoffResultAsync(db, engagement, handoff, cancellationToken);
        }

        if (bridge.IsConfigured)
        {
            await DeploymentSync.ApplyAsync(db, bridge, engagement, cancellationToken);
        }

        await fabric.PublishEngagementAsync("engagement.submitted", engagement, cancellationToken);

        return Results.Created(
            $"/api/engagements/{engagement.Id}",
            ToDetailResponse(engagement));
    }

    private static async Task<IResult> RetryPlannerHandoffAsync(
        Guid id,
        AppDbContext db,
        PlannerHandoffService handoff,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (await LoadEngagementAsync(db, id, user, cancellationToken) is null) return Results.NotFound();
        if (!handoff.IsConfigured)
        {
            return Results.Conflict(new
            {
                message = "Planner handoff is not configured. Set the PlannerFlow authentication mode and endpoint on the API host."
            });
        }

        var claimed = await OwnerScope.Apply(db.Engagements, user)
            .Where(item => item.Id == id && item.PlannerHandoffStatus == "NotConfigured")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.PlannerHandoffStatus, "Pending")
                .SetProperty(item => item.PlannerHandoffMessage, (string?)null),
                cancellationToken);
        if (claimed == 0)
        {
            var current = await LoadEngagementAsync(db, id, user, cancellationToken);
            if (current is null)
            {
                return Results.NotFound();
            }

            return current.PlannerHandoffStatus == "Sent"
                ? Results.Ok(ToDetailResponse(current))
                : Results.Conflict(new
                {
                    message = "A handoff is pending or its remote outcome is uncertain. Reconcile the flow and Planner before taking further action; automatic retry is disabled."
                });
        }

        var engagement = await db.Engagements
            .Include(item => item.Customer)
            .Include(item => item.Answers)
            .Include(item => item.Tasks)
                .ThenInclude(task => task.WbsTask)
            .SingleAsync(item => item.Id == id, cancellationToken);
        await ApplyHandoffResultAsync(db, engagement, handoff, cancellationToken);
        return Results.Ok(ToDetailResponse(engagement));
    }

    private static async Task ApplyHandoffResultAsync(
        AppDbContext db,
        Engagement engagement,
        PlannerHandoffService handoff,
        CancellationToken cancellationToken)
    {
        var result = await handoff.SendAsync(engagement, cancellationToken);
        engagement.PlannerHandoffStatus = result.Status;
        engagement.PlannerPlanId = result.PlanId;
        engagement.PlannerHandoffMessage = result.Message;
        engagement.PlannerHandedOffUtc = result.HandedOffUtc;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<IResult> ConfirmPlannerHandoffAsync(
        Guid id,
        ConfirmPlannerHandoffRequest request,
        ClaimsPrincipal user,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var planId = request.PlanId?.Trim();
        var evidence = request.EvidenceReference?.Trim();
        if (string.IsNullOrEmpty(planId) || planId.Length > 200 ||
            planId.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["planId"] = ["Enter the exact Planner plan ID, without spaces."]
            });
        }
        if (string.IsNullOrEmpty(evidence) || evidence.Length > 60 ||
            evidence.Any(char.IsControl))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["evidenceReference"] = ["Enter a flow run or reconciliation reference (up to 60 characters)."]
            });
        }
        if (!request.ConfirmedTerminalAndComplete)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["confirmedTerminalAndComplete"] = ["Confirm that the run is terminal and that the plan and all scoped tasks were checked."]
            });
        }

        var ids = user.Claims.Where(claim => claim.Type is ClaimTypes.NameIdentifier or "sub")
            .Select(claim => claim.Value).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length != 1 || string.IsNullOrWhiteSpace(ids[0]) || ids[0].Length > 200 ||
            ids[0] != ids[0].Trim() || ids[0].Any(char.IsControl))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                title: "A single stable authenticated identity is required.");
        }

        var message = $"Manually confirmed by {ids[0]}; reference: {evidence}";
        var now = DateTime.UtcNow;
        var changed = await OwnerScope.Apply(db.Engagements, user)
            .Where(item => item.Id == id && item.PlannerHandoffStatus == "Failed")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.PlannerHandoffStatus, "ManualConfirmed")
                .SetProperty(item => item.PlannerPlanId, planId)
                .SetProperty(item => item.PlannerHandoffMessage, message)
                .SetProperty(item => item.PlannerHandedOffUtc, now), cancellationToken);
        var engagement = await LoadEngagementAsync(db, id, user, cancellationToken);
        if (engagement is null) return Results.NotFound();
        if (changed == 0)
        {
            return Results.Conflict(new
            {
                message = "Only failed handoffs can be manually confirmed. Pending handoffs may still be running; do not overwrite or resend them."
            });
        }
        return Results.Ok(ToDetailResponse(engagement));
    }

    private static async Task<Engagement?> LoadEngagementAsync(
        AppDbContext db,
        Guid id,
        ClaimsPrincipal user,
        CancellationToken cancellationToken) =>
        await OwnerScope.Apply(db.Engagements, user)
            .AsNoTracking()
            .Include(engagement => engagement.Customer)
            .Include(engagement => engagement.Answers)
            .Include(engagement => engagement.Tasks)
                .ThenInclude(task => task.WbsTask)
            .SingleOrDefaultAsync(engagement => engagement.Id == id, cancellationToken);

    private static Dictionary<string, string[]> ValidateRequest(CreateEngagementRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        void Required(string key, string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                errors[key] = ["This field is required."];
            }
            else if (value.Trim().Length > maxLength)
            {
                errors[key] = [$"This field must be {maxLength} characters or fewer."];
            }
        }

        Required("companyName", request.CompanyName, 200);
        Required("salesperson", request.Salesperson, 160);
        OptionalLength("contactName", request.ContactName, 160, errors);
        OptionalLength("contactEmail", request.ContactEmail, 254, errors);
        OptionalLength("notes", request.Notes, 4000, errors);

        if (!string.IsNullOrWhiteSpace(request.ContactEmail) &&
            !new EmailAddressAttribute().IsValid(request.ContactEmail.Trim()))
        {
            errors["contactEmail"] = ["Enter a valid email address."];
        }

        if (request.Users < 0)
        {
            errors["users"] = ["Users must be zero or greater."];
        }

        if (request.Endpoints < 0)
        {
            errors["endpoints"] = ["Endpoints must be zero or greater."];
        }

        return errors;
    }

    private static void OptionalLength(
        string key,
        string? value,
        int maxLength,
        Dictionary<string, string[]> errors)
    {
        if (value?.Trim().Length > maxLength)
        {
            errors[key] = [$"This field must be {maxLength} characters or fewer."];
        }
    }

    private static string NormalizeCompanyName(string name) =>
        string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpper(CultureInfo.InvariantCulture);

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static EstimateResponse ToEstimateResponse(WbsEstimate estimate) =>
        new(
            estimate.Tasks.Count,
            estimate.OneTimeHoursMin,
            estimate.OneTimeHoursMax,
            estimate.MonthlyHoursMin,
            estimate.MonthlyHoursMax,
            estimate.ByPhase.Select(phase => new PhaseEstimateResponse(
                phase.Phase,
                phase.TaskCount,
                phase.MinHours,
                phase.MaxHours,
                phase.IsMonthly)).ToArray(),
            estimate.Tasks.Select(ToTaskResponse).ToArray());

    private static EngagementDetailResponse ToDetailResponse(Engagement engagement)
    {
        var tasks = engagement.Tasks
            .OrderBy(task => task.WbsTask.Sequence)
            .Select(task => ToTaskResponse(task.WbsTask, task.MinHours, task.MaxHours))
            .ToArray();
        var phases = engagement.Tasks
            .GroupBy(task => task.WbsTask.Phase)
            .Select(group => new PhaseEstimateResponse(
                group.Key,
                group.Count(),
                group.Sum(task => task.MinHours),
                group.Sum(task => task.MaxHours),
                group.Key.StartsWith(
                    "Ongoing Secure Plus Operations",
                    StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        return new EngagementDetailResponse(
            engagement.Id,
            engagement.CustomerId,
            engagement.Customer.Name,
            engagement.Customer.ContactName,
            engagement.Customer.ContactEmail,
            engagement.Salesperson,
            engagement.MeetingDate,
            engagement.Users,
            engagement.Endpoints,
            engagement.Notes,
            engagement.CreatedUtc,
            engagement.Tasks.Count,
            engagement.OneTimeHoursMin,
            engagement.OneTimeHoursMax,
            engagement.MonthlyHoursMin,
            engagement.MonthlyHoursMax,
            engagement.PlannerHandoffStatus,
            engagement.PlannerPlanId,
            engagement.PlannerHandoffMessage,
            engagement.Answers
                .GroupBy(answer => answer.FieldKey)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(answer => answer.Value).ToArray(),
                    StringComparer.Ordinal),
            phases,
            tasks,
            engagement.DeploymentSyncStatus,
            engagement.DeploymentSyncMessage,
            engagement.OwnerEmail,
            string.IsNullOrWhiteSpace(engagement.ExperienceJson)
                ? null
                : JsonSerializer.Deserialize<JsonElement>(engagement.ExperienceJson));
    }

    private static string? SerializeExperience(ExperienceSelection? experience) =>
        experience is null ? null : JsonSerializer.Serialize(experience, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

    private static WbsTaskResponse ToTaskResponse(WbsTask task) =>
        ToTaskResponse(task, task.MinHours, task.MaxHours);

    private static WbsTaskResponse ToTaskResponse(
        WbsTask task,
        decimal minHours,
        decimal maxHours) =>
        new(
            task.Id,
            task.Sequence,
            task.Phase,
            task.Workstream,
            task.Name,
            task.Deliverable,
            minHours,
            maxHours,
            task.DurationMin,
            task.DurationMax,
            task.SupportHandoff);
}
