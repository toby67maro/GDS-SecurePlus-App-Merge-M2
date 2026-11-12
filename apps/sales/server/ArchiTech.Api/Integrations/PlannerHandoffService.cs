using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Azure.Identity;
using ArchiTech.Api.Domain;
using Microsoft.Extensions.Options;

namespace ArchiTech.Api.Integrations;

public sealed record PlannerHandoffResult(
    string Status,
    string? PlanId,
    string? Message,
    DateTime? HandedOffUtc);

public sealed class PlannerHandoffService(
    HttpClient httpClient,
    IOptions<PlannerFlowOptions> options,
    IPlannerFlowTokenProvider? tokenProvider = null)
{
    private readonly PlannerFlowOptions _options = options.Value;

    public bool IsConfigured => _options.IsConfigured;

    public async Task<PlannerHandoffResult> SendAsync(
        Engagement engagement,
        CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            return new PlannerHandoffResult("NotConfigured", null, null, null);
        }

        var tasks = engagement.Tasks
            .OrderBy(task => task.WbsTask.Sequence)
            .Select(task => new PlannerTaskHandoff(
                task.WbsTask.Id,
                task.WbsTask.Phase,
                task.WbsTask.Workstream,
                task.WbsTask.Name,
                task.WbsTask.Deliverable,
                task.MinHours,
                task.MaxHours,
                task.WbsTask.DurationMin,
                task.WbsTask.DurationMax,
                task.WbsTask.SupportHandoff))
            .ToArray();
        var answers = engagement.Answers
            .GroupBy(answer => answer.FieldKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(answer => answer.Value).ToArray(),
                StringComparer.Ordinal);
        var phases = tasks
            .GroupBy(task => task.Phase, StringComparer.Ordinal)
            .Select(group => new PlannerPhaseHandoff(
                group.Key,
                group.Select(task => task.WbsId).ToArray(),
                group.Sum(task => task.MinHours),
                group.Sum(task => task.MaxHours)))
            .ToArray();
        var payload = new PlannerEngagementHandoff(
            2,
            engagement.Id,
            engagement.Customer.Name,
            $"Secure Plus onboarding - {engagement.Customer.Name}",
            new PlannerCustomerProfile(
                engagement.CustomerId,
                engagement.Customer.Name,
                engagement.Customer.ContactName,
                engagement.Customer.ContactEmail),
            engagement.Salesperson,
            engagement.MeetingDate,
            engagement.Users,
            engagement.Endpoints,
            engagement.Notes,
            engagement.OneTimeHoursMin,
            engagement.OneTimeHoursMax,
            engagement.MonthlyHoursMin,
            engagement.MonthlyHoursMax,
            ["Sales", "Project Management", "Engineering"],
            answers,
            phases,
            tasks);

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", engagement.Id.ToString("D"));

        if (_options.AuthenticationMode == "EntraCertificate")
        {
            try
            {
                if (tokenProvider is null)
                {
                    return new PlannerHandoffResult("Failed", null,
                        "Planner authentication is not available; no flow request was sent.", null);
                }
                var accessToken = await tokenProvider.GetTokenAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    return new PlannerHandoffResult("Failed", null,
                        "Planner authentication returned no token; no flow request was sent.", null);
                }
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            }
            catch (AuthenticationFailedException)
            {
                return new PlannerHandoffResult("Failed", null,
                    "Planner authentication failed; no flow request was sent. Check the workload identity and certificate configuration.", null);
            }
            catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
            {
                return new PlannerHandoffResult("Failed", null,
                    "Planner authentication certificate could not be loaded; no flow request was sent.", null);
            }
        }

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                return new PlannerHandoffResult(
                    "Failed", null,
                    "Power Automate accepted the request without confirming completion. The plan or some tasks may exist; reconcile before taking further action.",
                    null);
            }
            if (!response.IsSuccessStatusCode)
            {
                return new PlannerHandoffResult(
                    "Failed",
                    null,
                    $"Power Automate returned HTTP {(int)response.StatusCode}. The engagement and estimate are saved, but the plan or some tasks may exist. Verify the flow and its idempotency before retrying.",
                    null);
            }

            var planId = await ReadPlanIdAsync(response, cancellationToken);
            if (string.IsNullOrWhiteSpace(planId) || planId.Length > 200)
            {
                return new PlannerHandoffResult(
                    "Failed",
                    null,
                    "Power Automate returned success without a valid plan ID. The plan may exist; verify the flow before retrying.",
                    null);
            }
            return new PlannerHandoffResult("Sent", planId, null, DateTime.UtcNow);
        }
        catch (HttpRequestException)
        {
            return new PlannerHandoffResult(
                "Failed",
                null,
                "Power Automate communication failed. The engagement and estimate are saved, but the plan or some tasks may exist. Verify the flow and its idempotency before retrying.",
                null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PlannerHandoffResult(
                "Failed",
                null,
                "Power Automate did not respond before the request timed out. The engagement and estimate are saved, but the plan or some tasks may exist. Verify the flow and its idempotency before retrying.",
                null);
        }
    }

    private static async Task<string?> ReadPlanIdAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is 0)
        {
            return null;
        }

        try
        {
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("plannerPlanId", out var planId) &&
                   planId.ValueKind == JsonValueKind.String
                ? planId.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record PlannerEngagementHandoff(
        int SchemaVersion,
        Guid EngagementId,
        string CompanyName,
        string PlanTitle,
        PlannerCustomerProfile Customer,
        string Salesperson,
        DateOnly? MeetingDate,
        int? Users,
        int? Endpoints,
        string? Notes,
        decimal OneTimeHoursMin,
        decimal OneTimeHoursMax,
        decimal MonthlyHoursMin,
        decimal MonthlyHoursMax,
        IReadOnlyList<string> StakeholderGroups,
        IReadOnlyDictionary<string, string[]> Answers,
        IReadOnlyList<PlannerPhaseHandoff> Phases,
        IReadOnlyList<PlannerTaskHandoff> Tasks);

    private sealed record PlannerCustomerProfile(
        Guid CustomerId,
        string CompanyName,
        string? ContactName,
        string? ContactEmail);

    private sealed record PlannerPhaseHandoff(
        string Name,
        IReadOnlyList<string> TaskWbsIds,
        decimal MinHours,
        decimal MaxHours);

    private sealed record PlannerTaskHandoff(
        string WbsId,
        string Phase,
        string Workstream,
        string Title,
        string Deliverable,
        decimal MinHours,
        decimal MaxHours,
        string DurationMin,
        string DurationMax,
        bool SupportHandoff);
}
