using EngineeringWorkspace.Application.Credentials;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Domain.Common;

namespace EngineeringWorkspace.Application.Assessments;

public enum InsightKind
{
    Information,
    Suggestion,
    Warning
}

/// <summary>A message for the info bar when nothing is running, optionally with an action.</summary>
public sealed record Insight(InsightKind Kind, string Text, string? ActionLabel = null, CustomerId? AssessCustomer = null, string? Route = null);

/// <summary>Statistics and suggestions shown between task messages (for example customers not assessed recently).</summary>
public interface IInsightService
{
    Task<IReadOnlyList<Insight>> GetInsightsAsync(CancellationToken cancellationToken = default);
}

public sealed class InsightService(
    ICustomerProfileStore customers,
    IEngagementRepository repository,
    ICredentialService credentials,
    TimeProvider timeProvider) : IInsightService
{
    /// <summary>A customer not assessed for this long gets a suggestion.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

    public async Task<IReadOnlyList<Insight>> GetInsightsAsync(CancellationToken cancellationToken = default)
    {
        var insights = new List<Insight>();
        var now = timeProvider.GetUtcNow();
        var profiles = await customers.ListAsync(cancellationToken);
        if (!repository.IsAvailable)
        {
            insights.Add(new Insight(InsightKind.Warning, "The application database is not ready, so assessment history is unavailable.", "Open Database Configuration", null,
                "settings/database-configuration"));
            return insights;
        }

        var assessments = await repository.ListAssessmentsAsync(null, cancellationToken);
        foreach (var profile in profiles.Where(profile => !profile.Tags.Contains("sample")))
        {
            var latest = assessments.Where(item => item.CustomerKey == profile.Id.Value && item.StatusCode == AssessmentStatusCodes.Completed && !item.IsSample)
                .OrderByDescending(item => item.CompletedAtUtc).FirstOrDefault();
            var connected = profile.PrimaryTenant?.ApplicationRegistration?.SecretReference is not null;
            if (!connected)
            {
                insights.Add(new Insight(InsightKind.Suggestion, $"{profile.Name} has no connected tenant yet. Register the Secure Plus app to start assessing it.",
                    "Connect tenant", null, "workspace/tenant-app-registration"));
            }
            else if (latest is null)
            {
                insights.Add(new Insight(InsightKind.Suggestion, $"{profile.Name} has never been assessed. Would you like to launch an assessment now?", "Assess now", profile.Id));
            }
            else if (latest.CompletedAtUtc is { } completed && now - completed > StaleAfter)
            {
                insights.Add(new Insight(InsightKind.Suggestion,
                    $"{profile.Name} hasn't had an assessment in {(int)(now - completed).TotalDays} days (last score {latest.Score:0.#}%). Would you like to launch one now?", "Assess now", profile.Id));
            }
            else if (latest.Score is { } score)
            {
                insights.Add(new Insight(InsightKind.Information, $"{profile.Name}: latest score {score:0.#}% on {latest.CompletedAtUtc?.ToLocalTime():d}."));
            }
        }

        var pending = assessments.Count(item => item.StatusCode == AssessmentStatusCodes.Pending);
        if (pending > 0) insights.Add(new Insight(InsightKind.Suggestion, $"{pending} assessment(s) are pending.", "Run pending", null, "assessment/engine"));

        foreach (var credential in (await credentials.ListAsync(cancellationToken)).Where(item => item.Status is Domain.Credentials.CredentialStatus.ExpiresSoon or Domain.Credentials.CredentialStatus.Expired))
            insights.Add(new Insight(InsightKind.Warning, $"Credential '{credential.Metadata.DisplayName}' {(credential.Status == Domain.Credentials.CredentialStatus.Expired ? "has expired" : "expires soon")}.",
                "Open Credential Management", null, "settings/credential-management"));

        var completedScores = assessments.Where(item => item.StatusCode == AssessmentStatusCodes.Completed && item.Score is not null && !item.IsSample).ToArray();
        if (completedScores.Length > 0)
            insights.Add(new Insight(InsightKind.Information, $"{profiles.Count} customer(s), {completedScores.Length} completed assessment(s), average score {completedScores.Average(item => item.Score!.Value):0.#}%."));
        return insights;
    }
}
