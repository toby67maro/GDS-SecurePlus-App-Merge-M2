using ArchiTech.Api.Contracts;
using EngineeringWorkspace.Application.Experience;
using EngineeringWorkspace.Application.Reference;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Customers;

namespace ArchiTech.Api.Endpoints;

/// <summary>
/// Secure Plus "experience" (what each kind of user will feel) served from the same catalog and engine the
/// engineers' deployment app uses, so sales and deployment never disagree.
/// </summary>
public static class ExperienceEndpoints
{
    public sealed record EvaluateRequest(ExperienceSelection? Experience, string[]? Frameworks);

    public static IEndpointRouteBuilder MapExperienceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/experience/catalog", () => Results.Ok(new
        {
            industries = IndustryList.Defaults.Where(item => item.IsActive).Select(item => new { item.Code, item.Name }),
            otherIndustryCode = IndustryList.Other,
            frameworks = ExperienceCatalog.ActiveFrameworks.Select(item => new { item.Code, item.Name, item.Description }),
            solutions = ExperienceCatalog.Solutions,
            classifications = ExperienceCatalog.Classifications.Select(item => new
                { item.Code, item.Name, item.Description, item.DefaultProfile }),
            profiles = ExperienceCatalog.Profiles.Select(item => new
                { item.Code, item.Name, item.Description, item.Answers }),
            questions = ExperienceCatalog.Questions.Select(question => new
            {
                question.Code,
                question.Solution,
                question.Topic,
                question.Question,
                question.WhyItMatters,
                question.DefaultOption,
                options = question.Options.Select(option => new
                    { option.Code, option.Label, option.UserExperience, option.Security, option.Friction })
            })
        }));

        endpoints.MapPost("/api/experience/evaluate", (EvaluateRequest request) =>
        {
            var selection = request.Experience;
            if (selection is null) return Results.BadRequest(new { message = "Experience selection is required." });
            var groups = (selection.Groups ?? [])
                .Where(group => !string.IsNullOrWhiteSpace(group.Name))
                .Take(CustomerProfile.MaxUserGroups)
                .Select((group, index) => new UserGroup(
                    $"group-{index + 1}",
                    group.Name!.Trim(),
                    ExperienceCatalog.Classification(group.Classification).Code,
                    UserCount: group.UserCount,
                    ExperienceProfile: group.ExperienceProfile,
                    Answers: group.Answers))
                .ToArray();
            var profile = CustomerProfile.Create(new CustomerId("sales-preview"), "Sales preview") with
            {
                UserGroups = groups,
                ExperienceAnswers = selection.Answers ?? new Dictionary<string, string>()
            };
            var frameworks = (request.Frameworks ?? [])
                .Where(code => ExperienceCatalog.Framework(code) is not null).ToArray();
            var results = ExperienceEngine.EvaluateAll(profile, frameworks).Select(group => new
            {
                group.Key,
                group.Name,
                group.Classification,
                group.UserCount,
                profile = group.Profile.Name,
                security = Math.Round(group.Security),
                friction = Math.Round(group.Friction),
                securityWithFrameworks = Math.Round(group.SecurityWithFrameworks),
                frictionWithFrameworks = Math.Round(group.FrictionWithFrameworks),
                group.Conflicts,
                group.Weaknesses,
                solutions = group.Solutions.Select(score => new
                    { score.Solution, security = Math.Round(score.Security), friction = Math.Round(score.Friction) }),
                answers = group.Answers.Select(answer => new
                {
                    question = answer.Question.Code,
                    topic = answer.Question.Topic,
                    solution = answer.Question.Solution,
                    option = answer.Option.Label,
                    experience = answer.Option.UserExperience,
                    source = answer.Source
                }),
                findings = group.Findings.Select(finding => new
                {
                    question = finding.Question.Topic,
                    option = finding.Option.Label,
                    framework = finding.Framework.Name,
                    effect = finding.Effect.ToString(),
                    finding.Reason,
                    suggestion = finding.CompliantOption?.Label
                })
            });
            return Results.Ok(new { groups = results });
        });
        return endpoints;
    }
}
