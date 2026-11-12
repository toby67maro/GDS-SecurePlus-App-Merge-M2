using ArchiTech.Api.Contracts;
using ArchiTech.Api.Data;
using ArchiTech.Api.Domain;
using ArchiTech.Api.Security;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace ArchiTech.Api.Endpoints;

public static class DeploymentEndpoints
{
    private static readonly HashSet<string> FindingStatuses =
        ["NotAssessed", "Implemented", "PartiallyImplemented", "NotImplemented", "NotApplicable", "Exception"];
    private static readonly string[] SetupStepKeys =
        ["confirm-tenant", "register-application", "review-permissions", "record-client-id"];

    public static IEndpointRouteBuilder MapDeploymentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/deployment").RequireAuthorization("DeploymentAccess");
        group.MapGet("/profiles", ListProfilesAsync);
        group.MapGet("/fleet", GetFleetAsync);
        group.MapPost("/profiles", CreateProfileAsync)
            .RequireAuthorization("DeploymentAuthor");
        group.MapGet("/profiles/{customerId:guid}", GetProfileAsync);
        group.MapPut("/profiles/{customerId:guid}", UpdateProfileAsync)
            .RequireAuthorization("DeploymentAuthor");
        group.MapGet("/profiles/{customerId:guid}/setup-checklist", GetSetupChecklistAsync);
        group.MapPut("/profiles/{customerId:guid}/setup-checklist/{stepKey}", UpdateSetupStepAsync)
            .RequireAuthorization("DeploymentAuthor");
        group.MapGet("/profiles/{customerId:guid}/controls", GetControlsAsync);
        group.MapPut("/profiles/{customerId:guid}/controls/{controlId}", SaveFindingAsync)
            .RequireAuthorization("DeploymentAuthor");
        group.MapGet("/profiles/{customerId:guid}/roadmap", GetRoadmapAsync);
        return endpoints;
    }

    private static async Task<IResult> GetFleetAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var profiles = await db.DeploymentProfiles.AsNoTracking()
            .Select(profile => new { profile.CustomerId, CustomerName = profile.Customer.Name })
            .ToListAsync(cancellationToken);
        var completed = await db.AssessmentRuns.AsNoTracking()
            .Where(run => run.IsComplete && run.CompletedUtc != null)
            .Select(run => new
            {
                run.Id,
                run.CustomerId,
                run.Name,
                run.Source,
                run.CatalogVersion,
                run.CompletedUtc,
                run.OverallPercentage,
                run.TotalControls,
                run.NotAssessedControls,
                run.ReadinessRating
            })
            .ToListAsync(cancellationToken);
        var latestByCustomer = completed.GroupBy(run => run.CustomerId)
            .ToDictionary(group => group.Key, group => group
                .OrderByDescending(run => run.CompletedUtc)
                .ThenByDescending(run => run.Id)
                .First());
        var customers = profiles
            .OrderBy(profile => profile.CustomerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(profile => profile.CustomerId)
            .Select(profile => new
            {
                profile.CustomerId,
                profile.CustomerName,
                latestCompletedAssessment = latestByCustomer.GetValueOrDefault(profile.CustomerId)
            }).ToArray();

        return Results.Ok(new
        {
            customerCount = customers.Length,
            withCompletedAssessment = customers.Count(item => item.latestCompletedAssessment is not null),
            withoutCompletedAssessment = customers.Count(item => item.latestCompletedAssessment is null),
            customers,
            notice = "Latest completed assessment per customer. Scores reflect recorded findings, not a live tenant assessment; unassessed controls and exceptions affect the scoring model.",
            tenantChangesEnabled = false
        });
    }

    private static async Task<IResult> GetSetupChecklistAsync(
        Guid customerId,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var profile = await db.DeploymentProfiles
            .AsNoTracking()
            .Where(item => item.CustomerId == customerId)
            .Select(item => new { item.Id })
            .SingleOrDefaultAsync(cancellationToken);
        if (profile is null)
        {
            return Results.NotFound(new { message = "Deployment profile not found." });
        }

        var savedSteps = await db.DeploymentProfileSetupSteps
            .AsNoTracking()
            .Where(item => item.DeploymentProfileId == profile.Id)
            .ToDictionaryAsync(item => item.StepKey, cancellationToken);

        return Results.Ok(new
        {
            steps = SetupStepKeys.Select(key => new
            {
                stepKey = key,
                isComplete = savedSteps.TryGetValue(key, out var saved) && saved.IsComplete,
                updatedUtc = savedSteps.TryGetValue(key, out var updated) ? updated.UpdatedUtc : (DateTime?)null
            })
        });
    }

    private static async Task<IResult> UpdateSetupStepAsync(
        Guid customerId,
        string stepKey,
        UpdateDeploymentSetupStepRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!SetupStepKeys.Contains(stepKey, StringComparer.Ordinal))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["stepKey"] = ["Choose a valid app-registration setup step."]
            });
        }

        var profileId = await db.DeploymentProfiles
            .Where(item => item.CustomerId == customerId)
            .Select(item => (Guid?)item.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (profileId is null)
        {
            return Results.NotFound(new { message = "Deployment profile not found." });
        }

        var step = await db.DeploymentProfileSetupSteps
            .SingleOrDefaultAsync(
                item => item.DeploymentProfileId == profileId && item.StepKey == stepKey,
                cancellationToken);
        if (step is null)
        {
            step = new DeploymentProfileSetupStep
            {
                DeploymentProfileId = profileId.Value,
                StepKey = stepKey,
                IsComplete = request.IsComplete,
                UpdatedUtc = DateTime.UtcNow
            };
            db.DeploymentProfileSetupSteps.Add(step);
        }
        else
        {
            step.IsComplete = request.IsComplete;
            step.UpdatedUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateProfileAsync(
        CreateDeploymentCustomerRequest request,
        AppDbContext db,
        DeploymentControlCatalog catalog,
        CancellationToken cancellationToken)
    {
        var customerName = request.CustomerName?.Trim();
        var contactName = NormalizeOptional(request.ContactName);
        var contactEmail = NormalizeOptional(request.ContactEmail);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(customerName))
        {
            errors["customerName"] = ["Enter a customer or organization name."];
        }
        else if (customerName.Length > 200)
        {
            errors["customerName"] = ["Customer name must be 200 characters or fewer."];
        }
        if (contactName?.Length > 160)
        {
            errors["contactName"] = ["Contact name must be 160 characters or fewer."];
        }
        if (contactEmail?.Length > 254)
        {
            errors["contactEmail"] = ["Contact email must be 254 characters or fewer."];
        }
        else if (contactEmail is not null && !new EmailAddressAttribute().IsValid(contactEmail))
        {
            errors["contactEmail"] = ["Enter a valid email address."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var normalizedName = NormalizeCompanyName(customerName!);
        if (await db.Customers.AnyAsync(
                item => item.NormalizedName == normalizedName,
                cancellationToken))
        {
            return Results.Conflict(new
            {
                message = "A customer with this name already exists. Select the existing profile instead."
            });
        }

        var now = DateTime.UtcNow;
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Name = customerName!,
            NormalizedName = normalizedName,
            ContactName = contactName,
            ContactEmail = contactEmail,
            CreatedUtc = now
        };
        var profile = new DeploymentProfile
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            Customer = customer,
            CreatedUtc = now,
            UpdatedUtc = now
        };

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Customers.Add(customer);
        db.DeploymentProfiles.Add(profile);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Results.Created(
            $"/api/deployment/profiles/{customer.Id}",
            new
            {
                customerId = customer.Id,
                customerName = customer.Name,
                customer.ContactName,
                customer.ContactEmail,
                profileId = profile.Id,
                tenantChangesEnabled = false
            });
    }

    private static async Task<IResult> ListProfilesAsync(
        AppDbContext db,
        DeploymentControlCatalog catalog,
        CancellationToken cancellationToken)
    {
        var customers = await db.Customers
            .AsNoTracking()
            .Include(customer => customer.DeploymentProfile)
                .ThenInclude(profile => profile!.Findings)
            .Include(customer => customer.Engagements)
            .OrderBy(customer => customer.Name)
            .ToListAsync(cancellationToken);

        return Results.Ok(customers.Select(customer =>
        {
            var latestEngagement = customer.Engagements
                .OrderByDescending(engagement => engagement.CreatedUtc)
                .FirstOrDefault();
            var profile = customer.DeploymentProfile;
            var statuses = profile?.Findings.ToDictionary(
                finding => finding.ControlId,
                finding => finding.Status,
                StringComparer.Ordinal) ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var score = DeploymentScoring.Calculate(catalog.Controls, statuses);
            return new
            {
                customerId = customer.Id,
                customerName = customer.Name,
                customer.ContactName,
                customer.ContactEmail,
                engagementCount = customer.Engagements.Count,
                latestEngagementId = latestEngagement?.Id,
                latestEngagementUtc = latestEngagement?.CreatedUtc,
                profileId = profile?.Id,
                profile?.TenantId,
                profile?.PrimaryDomain,
                reviewedControls = profile?.Findings.Count ?? 0,
                scorePercentage = score.OverallPercentage,
                createdUtc = profile?.CreatedUtc
            };
        }));
    }

    private static async Task<IResult> GetProfileAsync(
        Guid customerId,
        AppDbContext db,
        DeploymentControlCatalog catalog,
        CancellationToken cancellationToken)
    {
        var customer = await db.Customers
            .AsNoTracking()
            .Include(item => item.DeploymentProfile)
                .ThenInclude(profile => profile!.Findings)
            .Include(item => item.Engagements)
            .SingleOrDefaultAsync(item => item.Id == customerId, cancellationToken);
        if (customer is null)
        {
            return Results.NotFound();
        }

        var profile = customer.DeploymentProfile;
        var findings = profile?.Findings.ToDictionary(
            finding => finding.ControlId,
            finding => finding.Status,
            StringComparer.Ordinal) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var score = DeploymentScoring.Calculate(catalog.Controls, findings);
        var engagements = customer.Engagements
            .OrderByDescending(engagement => engagement.CreatedUtc)
            .Select(engagement => new
            {
                engagement.Id,
                engagement.Salesperson,
                engagement.MeetingDate,
                engagement.CreatedUtc,
                engagement.OneTimeHoursMin,
                engagement.OneTimeHoursMax,
                engagement.MonthlyHoursMin,
                engagement.MonthlyHoursMax,
                engagement.PlannerHandoffStatus
            })
            .ToArray();

        return Results.Ok(new
        {
            profileId = profile?.Id,
            customerId = customer.Id,
            customer.Name,
            customer.ContactName,
            customer.ContactEmail,
            profile?.TenantId,
            profile?.PrimaryDomain,
            profile?.EntraApplicationClientId,
            createdUtc = profile?.CreatedUtc,
            updatedUtc = profile?.UpdatedUtc,
            engagementCount = engagements.Length,
            engagements,
            review = new
            {
                score.OverallPercentage,
                score.TotalControls,
                reviewedControls = findings.Count,
                statusCounts = findings.Values
                    .GroupBy(status => status, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal)
            },
            tenantChangesEnabled = false
        });
    }

    private static async Task<IResult> UpdateProfileAsync(
        Guid customerId,
        UpdateDeploymentProfileRequest request,
        AppDbContext db,
        DeploymentControlCatalog catalog,
        CancellationToken cancellationToken)
    {
        var customer = await db.Customers
            .Include(item => item.DeploymentProfile)
            .SingleOrDefaultAsync(item => item.Id == customerId, cancellationToken);
        if (customer is null)
        {
            return Results.NotFound();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        ValidateMaxLength("tenantId", request.TenantId, 36, errors);
        ValidateMaxLength("primaryDomain", request.PrimaryDomain, 253, errors);
        ValidateMaxLength("entraApplicationClientId", request.EntraApplicationClientId, 36, errors);
        if (!string.IsNullOrWhiteSpace(request.TenantId) &&
            !Guid.TryParse(request.TenantId.Trim(), out _))
        {
            errors["tenantId"] = ["Enter a valid Microsoft Entra tenant GUID."];
        }
        if (!string.IsNullOrWhiteSpace(request.PrimaryDomain) &&
            (request.PrimaryDomain.Trim().Contains('/') ||
             request.PrimaryDomain.Trim().Contains('@') ||
             request.PrimaryDomain.Trim().Contains(' ')))
        {
            errors["primaryDomain"] = ["Enter a domain name, not a URL or email address."];
        }
        if (!string.IsNullOrWhiteSpace(request.EntraApplicationClientId) &&
            !Guid.TryParse(request.EntraApplicationClientId.Trim(), out _))
        {
            errors["entraApplicationClientId"] = ["Enter a valid Entra application client GUID."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var profile = customer.DeploymentProfile;
        if (profile is null)
        {
            profile = new DeploymentProfile
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                CreatedUtc = DateTime.UtcNow
            };
            db.DeploymentProfiles.Add(profile);
        }
        profile.TenantId = NormalizeOptional(request.TenantId);
        profile.PrimaryDomain = NormalizeOptional(request.PrimaryDomain)?.ToLowerInvariant();
        profile.EntraApplicationClientId = NormalizeOptional(request.EntraApplicationClientId);
        profile.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await GetProfileAsync(customerId, db, catalog, cancellationToken);
    }

    private static async Task<IResult> GetControlsAsync(
        Guid customerId,
        int? phase,
        Guid? engagementId,
        bool? scopedOnly,
        string? search,
        string? category,
        AppDbContext db,
        DeploymentControlCatalog catalog,
        CancellationToken cancellationToken)
    {
        var onlyScoped = scopedOnly.GetValueOrDefault();
        var profile = await db.DeploymentProfiles
            .AsNoTracking()
            .Include(item => item.Findings)
            .SingleOrDefaultAsync(item => item.CustomerId == customerId, cancellationToken);
        if (profile is null)
        {
            return Results.NotFound(new { message = "Deployment profile not found." });
        }

        HashSet<string>? scopedWbsIds = null;
        if (onlyScoped)
        {
            var engagementsQuery = db.Engagements.AsNoTracking()
                .Where(engagement => engagement.CustomerId == customerId);
            if (engagementId.HasValue)
            {
                engagementsQuery = engagementsQuery.Where(engagement => engagement.Id == engagementId.Value);
            }
            scopedWbsIds = await engagementsQuery
                .SelectMany(engagement => engagement.Tasks)
                .Select(task => task.WbsTaskId)
                .Distinct()
                .ToHashSetAsync(cancellationToken);
        }

        var findings = profile.Findings.ToDictionary(
            finding => finding.ControlId,
            StringComparer.Ordinal);
        var controls = catalog.Controls.Where(control => control.IsActive);
        if (phase.HasValue)
        {
            controls = controls.Where(control => PhaseNumber(control.DeploymentPhase) == phase.Value);
        }
        if (!string.IsNullOrWhiteSpace(category))
        {
            controls = controls.Where(control => control.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            controls = controls.Where(control =>
                control.ControlId.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) ||
                control.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) ||
                control.Description.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) ||
                control.Category.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        if (scopedWbsIds is not null)
        {
            controls = controls.Where(control =>
                !string.IsNullOrWhiteSpace(control.WbsId) && scopedWbsIds.Contains(control.WbsId));
        }

        var filtered = controls.OrderBy(control => PhaseNumber(control.DeploymentPhase))
            .ThenBy(control => control.Category, StringComparer.Ordinal)
            .ThenBy(control => control.ControlId, StringComparer.Ordinal)
            .ToArray();
        var statusMap = findings.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Status,
            StringComparer.Ordinal);
        var score = DeploymentScoring.Calculate(filtered, statusMap);

        return Results.Ok(new
        {
            controls = filtered.Select(control => new
            {
                control.ControlId,
                control.Name,
                control.Category,
                control.DeploymentPhase,
                phaseNumber = PhaseNumber(control.DeploymentPhase),
                control.AssessmentLevel,
                control.Description,
                control.MicrosoftSolution,
                control.UserImpact,
                control.RecommendedState,
                control.ValidationMethod,
                control.SourceReference,
                control.PointValue,
                control.WbsId,
                control.Dependencies,
                status = findings.TryGetValue(control.ControlId, out var finding)
                    ? finding.Status
                    : "NotAssessed",
                notes = findings.TryGetValue(control.ControlId, out finding) ? finding.Notes : null,
                evidence = findings.TryGetValue(control.ControlId, out finding) ? finding.Evidence : null,
                updatedUtc = findings.TryGetValue(control.ControlId, out finding) ? finding.UpdatedUtc : (DateTime?)null
            }),
            score,
            scope = onlyScoped ? "service-plus-engagement" : "full-catalog",
            count = filtered.Length,
            tenantChangesEnabled = false
        });
    }

    private static async Task<IResult> SaveFindingAsync(
        Guid customerId,
        string controlId,
        SaveDeploymentFindingRequest request,
        AppDbContext db,
        DeploymentControlCatalog catalog,
        CancellationToken cancellationToken)
    {
        var profile = await db.DeploymentProfiles
            .SingleOrDefaultAsync(item => item.CustomerId == customerId, cancellationToken);
        if (profile is null)
        {
            return Results.NotFound(new { message = "Deployment profile not found." });
        }
        if (!catalog.Controls.Any(control =>
                control.IsActive && control.ControlId.Equals(controlId, StringComparison.Ordinal)))
        {
            return Results.NotFound(new { message = "Control is not in the active deployment catalog." });
        }
        if (request.Status is null || !FindingStatuses.Contains(request.Status))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["status"] = ["Choose a valid manual review status."]
            });
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        ValidateMaxLength("notes", request.Notes, 2000, errors);
        ValidateMaxLength("evidence", request.Evidence, 4000, errors);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var finding = await db.DeploymentFindings.SingleOrDefaultAsync(
            item => item.DeploymentProfileId == profile.Id && item.ControlId == controlId,
            cancellationToken);
        if (finding is null)
        {
            finding = new DeploymentFinding
            {
                DeploymentProfileId = profile.Id,
                ControlId = controlId,
                Status = request.Status
            };
            db.DeploymentFindings.Add(finding);
        }

        finding.Status = request.Status;
        finding.Notes = NormalizeOptional(request.Notes);
        finding.Evidence = NormalizeOptional(request.Evidence);
        finding.UpdatedUtc = DateTime.UtcNow;
        profile.UpdatedUtc = finding.UpdatedUtc;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new
        {
            finding.ControlId,
            finding.Status,
            finding.Notes,
            finding.Evidence,
            finding.UpdatedUtc,
            tenantChangesEnabled = false
        });
    }

    private static async Task<IResult> GetRoadmapAsync(
        Guid customerId,
        Guid? engagementId,
        AppDbContext db,
        DeploymentControlCatalog catalog,
        CancellationToken cancellationToken)
    {
        var profile = await db.DeploymentProfiles
            .AsNoTracking()
            .Include(item => item.Customer)
            .Include(item => item.Findings)
            .SingleOrDefaultAsync(item => item.CustomerId == customerId, cancellationToken);
        if (profile is null)
        {
            return Results.NotFound();
        }

        var engagementQuery = db.Engagements.AsNoTracking()
            .Where(item => item.CustomerId == customerId);
        if (engagementId.HasValue)
        {
            engagementQuery = engagementQuery.Where(item => item.Id == engagementId.Value);
        }
        var engagement = await engagementQuery
            .OrderByDescending(item => item.CreatedUtc)
            .Include(item => item.Tasks)
            .FirstOrDefaultAsync(cancellationToken);
        var scopedIds = engagement?.Tasks.Select(task => task.WbsTaskId).ToHashSet(StringComparer.Ordinal)
            ?? [];
        var scoped = engagement is null
            ? catalog.Controls.Where(control => control.IsActive)
            : catalog.Controls.Where(control => control.IsActive && scopedIds.Contains(control.WbsId));
        var statuses = profile.Findings.ToDictionary(
            finding => finding.ControlId,
            finding => finding.Status,
            StringComparer.Ordinal);
        var gaps = scoped.Where(control =>
            statuses.GetValueOrDefault(control.ControlId, "NotAssessed") is
                "NotAssessed" or "NotImplemented" or "PartiallyImplemented")
            .GroupBy(control => control.DeploymentPhase)
            .OrderBy(group => PhaseNumber(group.Key))
            .Select(group => new
            {
                phase = group.Key,
                phaseNumber = PhaseNumber(group.Key),
                controls = group.OrderByDescending(control => control.PointValue)
                    .ThenBy(control => control.ControlId, StringComparer.Ordinal)
                    .Select(control => new
                    {
                        control.ControlId,
                        control.Name,
                        control.Category,
                        control.Description,
                        control.UserImpact,
                        control.PointValue,
                        control.WbsId,
                        status = statuses.GetValueOrDefault(control.ControlId, "NotAssessed"),
                        reason = "Review and validate this gap with the customer before scheduling remediation."
                    })
                    .ToArray()
            })
            .ToArray();
        var score = DeploymentScoring.Calculate(scoped, statuses);

        return Results.Ok(new
        {
            customerId,
            customerName = profile.Customer.Name,
            sourceEngagementId = engagement?.Id,
            sourceEngagementCreatedUtc = engagement?.CreatedUtc,
            scope = engagement is null ? "full-catalog" : "service-plus-engagement",
            score,
            phases = gaps,
            tenantChangesEnabled = false
        });
    }

    private static int PhaseNumber(string phase)
    {
        var marker = "Phase";
        var start = phase.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return start >= 0 &&
               start + marker.Length < phase.Length &&
               char.IsDigit(phase[start + marker.Length])
            ? phase[start + marker.Length] - '0'
            : -1;
    }

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

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeCompanyName(string name) =>
        string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpper(CultureInfo.InvariantCulture);
}
