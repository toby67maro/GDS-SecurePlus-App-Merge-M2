using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Workspaces;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Customers;
using EngineeringWorkspace.Domain.Workspaces;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Application.Assessments;

/// <summary>Result of loading the sample data set.</summary>
public sealed record SampleDataResult(CustomerProfile Customer, Workspace Workspace, AssessmentSummary? Assessment);

/// <summary>
/// Engagement history for first-run setup, the dashboard and the assessment pages: synchronizes file-based
/// customers and workspaces into the application database, answers "has this customer been assessed" and loads
/// the sample data set.
/// </summary>
public interface IEngagementService
{
    bool IsDatabaseAvailable { get; }

    /// <summary>Records every customer and workspace that exists on disk in the database (idempotent).</summary>
    Task<OperationResult> SynchronizeAsync(CancellationToken cancellationToken = default);

    Task<OperationResult<CustomerEngagementState>> GetCustomerStateAsync(CustomerId customerId, CancellationToken cancellationToken = default);
    Task<OperationResult<IReadOnlyList<AssessmentSummary>>> ListAssessmentsAsync(CustomerId? customerId, CancellationToken cancellationToken = default);
    Task<OperationResult<IReadOnlyList<StoredAssessmentFinding>>> ListFindingsAsync(int assessmentRunId, CancellationToken cancellationToken = default);

    /// <summary>Records an initial assessment request for the customer. It stays Pending until the assessment engine runs it.</summary>
    Task<OperationResult<AssessmentSummary>> RequestInitialAssessmentAsync(CustomerId customerId, WorkspaceId? workspaceId, CancellationToken cancellationToken = default);

    /// <summary>Creates the sample customer, workspace and a completed sample assessment, then opens them.</summary>
    Task<OperationResult<SampleDataResult>> LoadSampleDataAsync(CancellationToken cancellationToken = default);
}

public sealed class EngagementService(
    IEngagementRepository repository,
    ICustomerProfileStore customers,
    IWorkspaceStore workspaceStore,
    IWorkspaceService workspaces,
    ICustomerContextService customerContext,
    TimeProvider timeProvider,
    ILogger<EngagementService> logger) : IEngagementService
{
    public const string SampleCustomerName = "Contoso (Sample)";
    public const string SampleWorkspaceName = "Sample Workspace";

    public bool IsDatabaseAvailable => repository.IsAvailable;

    public async Task<OperationResult> SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        if (!repository.IsAvailable) return Unavailable();
        try
        {
            foreach (var customer in await customers.ListAsync(cancellationToken))
                await repository.UpsertCustomerAsync(customer, customer.Id.Value == SampleCustomerId.Value, cancellationToken);
            foreach (var workspace in await workspaceStore.ListAsync(cancellationToken))
                await repository.UpsertWorkspaceAsync(workspace, cancellationToken);
            return OperationResult.Success();
        }
        catch (Exception exception) when (IsDataFailure(exception))
        {
            logger.LogWarning(exception, "Customer and workspace synchronization with the database failed");
            return OperationResult.Failure("DatabaseSyncFailed", "Customers and workspaces could not be recorded in the database.", exception.Message);
        }
    }

    public Task<OperationResult<CustomerEngagementState>> GetCustomerStateAsync(CustomerId customerId, CancellationToken cancellationToken = default) =>
        RunAsync(() => repository.GetCustomerStateAsync(customerId, cancellationToken), "The customer's assessment history could not be read.");

    public Task<OperationResult<IReadOnlyList<AssessmentSummary>>> ListAssessmentsAsync(CustomerId? customerId, CancellationToken cancellationToken = default) =>
        RunAsync(() => repository.ListAssessmentsAsync(customerId, cancellationToken), "The assessment history could not be read.");

    public Task<OperationResult<IReadOnlyList<StoredAssessmentFinding>>> ListFindingsAsync(int assessmentRunId, CancellationToken cancellationToken = default) =>
        RunAsync(() => repository.ListFindingsAsync(assessmentRunId, cancellationToken), "The assessment findings could not be read.");

    public async Task<OperationResult<AssessmentSummary>> RequestInitialAssessmentAsync(CustomerId customerId, WorkspaceId? workspaceId, CancellationToken cancellationToken = default)
    {
        var profile = await customers.LoadAsync(customerId, cancellationToken);
        if (profile is null) return OperationResult<AssessmentSummary>.Failure("CustomerMissing", $"Customer '{customerId}' does not exist.");
        return await RunAsync(async () =>
        {
            await repository.UpsertCustomerAsync(profile, isSample: false, cancellationToken);
            return await repository.RecordAssessmentAsync(new RecordAssessmentRequest(customerId, workspaceId, "Initial assessment", AssessmentStatusCodes.Pending, "SecurePlus",
                null, [], false, "Requested during first-run setup. Runs when the assessment engine is available for this tenant."), cancellationToken);
        }, "The initial assessment could not be recorded.");
    }

    public async Task<OperationResult<SampleDataResult>> LoadSampleDataAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var customer = await customers.LoadAsync(SampleCustomerId, cancellationToken) ?? CustomerProfile.Create(SampleCustomerId, SampleCustomerName) with
            {
                Notes = "Sample customer created by Secure Plus Agent setup. Safe to delete.",
                Tags = ["sample"],
                Tenants =
                [
                    new Domain.Tenants.TenantConfiguration("primary", "Contoso", "contoso.onmicrosoft.com", null, "contoso.onmicrosoft.com",
                        Domain.Tenants.CloudEnvironment.Commercial, null)
                ]
            };

            AssessmentSummary? assessment = null;
            if (repository.IsAvailable)
            {
                // Record the sample flag first so the mirroring store's later save keeps it.
                await repository.UpsertCustomerAsync(customer, isSample: true, cancellationToken);
            }

            await customers.SaveAsync(customer, cancellationToken);

            var workspaceId = new WorkspaceId(StableIdentifier.FromDisplayName(SampleWorkspaceName));
            var opened = await workspaceStore.ExistsAsync(workspaceId, cancellationToken)
                ? await workspaces.OpenAsync(workspaceId, cancellationToken)
                : await workspaces.CreateAsync(SampleWorkspaceName, cancellationToken);
            if (!opened.Succeeded) return OperationResult<SampleDataResult>.Failure(opened.Error!);

            var selected = await customerContext.SelectAsync(customer.Id, null, "Sample data loaded", cancellationToken);
            if (!selected.Succeeded) return OperationResult<SampleDataResult>.Failure(selected.Error!);
            var workspace = workspaces.CurrentWorkspace ?? opened.Value!;

            if (repository.IsAvailable)
            {
                await repository.UpsertWorkspaceAsync(workspace, cancellationToken);
                var existing = await repository.ListAssessmentsAsync(customer.Id, cancellationToken);
                assessment = existing.FirstOrDefault(item => item.IsSample);
                if (assessment is null)
                {
                    var findings = SampleFindings();
                    var passed = findings.Count(finding => finding.Result == "Pass");
                    assessment = await repository.RecordAssessmentAsync(new RecordAssessmentRequest(customer.Id, workspace.Id, "Sample baseline assessment",
                        AssessmentStatusCodes.Completed, "SecurePlus", Math.Round(100m * passed / findings.Count, 2), findings, true,
                        "Sample data: illustrative results, not from a real tenant."), cancellationToken);
                }
            }

            logger.LogInformation("Sample data loaded at {Time}", timeProvider.GetUtcNow());
            return OperationResult<SampleDataResult>.Success(new SampleDataResult(customer, workspace, assessment));
        }
        catch (Exception exception) when (IsDataFailure(exception))
        {
            logger.LogWarning(exception, "Sample data could not be loaded");
            return OperationResult<SampleDataResult>.Failure("SampleDataFailed", "The sample data could not be loaded.", exception.Message);
        }
    }

    public static CustomerId SampleCustomerId { get; } = new(StableIdentifier.FromDisplayName(SampleCustomerName));

    /// <summary>Illustrative findings across the Secure Plus baseline areas.</summary>
    public static IReadOnlyList<StoredAssessmentFinding> SampleFindings() =>
    [
        new("Entra ID", "Multifactor authentication required for all users", "Pass", "High", null),
        new("Entra ID", "Legacy authentication blocked", "Fail", "High", "Create a Conditional Access policy that blocks legacy authentication clients."),
        new("Entra ID", "Break-glass accounts excluded from Conditional Access", "Pass", "Medium", null),
        new("Conditional Access", "Sign-in risk policy enabled", "Fail", "High", "Enable a sign-in risk policy that requires MFA for medium and high risk."),
        new("Conditional Access", "Compliant device required for administrators", "Fail", "Medium", "Require a compliant or hybrid-joined device for privileged roles."),
        new("Intune", "Windows compliance policy assigned", "Pass", "Medium", null),
        new("Intune", "BitLocker required on Windows devices", "Pass", "High", null),
        new("Defender", "Defender for Office 365 Safe Links enabled", "Fail", "Medium", "Turn on Safe Links for email and Office apps."),
        new("Exchange Online", "External sender tagging enabled", "Pass", "Low", null),
        new("Exchange Online", "Mailbox auditing enabled", "Pass", "Medium", null),
        new("Purview", "Sensitivity labels published", "Fail", "Medium", "Publish the GDS baseline sensitivity labels to all users."),
        new("SharePoint Online", "External sharing limited to existing guests", "Pass", "Medium", null),
        new("Teams", "Anonymous meeting join restricted", "Fail", "Low", "Restrict anonymous users from starting meetings."),
    ];

    private async Task<OperationResult<T>> RunAsync<T>(Func<Task<T>> operation, string message)
    {
        if (!repository.IsAvailable) return OperationResult<T>.Failure(UnavailableError());
        try
        {
            return OperationResult<T>.Success(await operation());
        }
        catch (Exception exception) when (IsDataFailure(exception))
        {
            logger.LogWarning(exception, "{Message}", message);
            return OperationResult<T>.Failure("DatabaseOperationFailed", message, exception.Message);
        }
    }

    private static OperationResult Unavailable() => OperationResult.Failure(UnavailableError());

    private static OperationError UnavailableError() =>
        new("DatabaseUnavailable", "The application database is not ready.", null, "Configure it in Settings > Database Configuration.");

    private static bool IsDataFailure(Exception exception) =>
        exception is not (OperationCanceledException or OutOfMemoryException or ArgumentException);
}
