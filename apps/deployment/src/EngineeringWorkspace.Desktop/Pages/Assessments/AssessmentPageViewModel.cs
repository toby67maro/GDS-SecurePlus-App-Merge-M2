using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Desktop.Navigation;

namespace EngineeringWorkspace.Desktop.Pages.Assessments;

/// <summary>An assessment row with the customer's display name.</summary>
public sealed record AssessmentRow(AssessmentSummary Summary, string CustomerName)
{
    public int AssessmentRunId => Summary.AssessmentRunId;
    public string Name => Summary.Name;
    public string CustomerKey => Summary.CustomerKey;
    public string? WorkspaceId => Summary.WorkspaceId;
    public string StatusCode => Summary.StatusCode;
    public decimal? Score => Summary.Score;
    public int? ControlsTotal => Summary.ControlsTotal;
    public int? ControlsPassed => Summary.ControlsPassed;
    public DateTimeOffset RequestedAtUtc => Summary.RequestedAtUtc;
    public DateTimeOffset? CompletedAtUtc => Summary.CompletedAtUtc;
    public string? RequestedBy => Summary.RequestedBy;
    public bool IsSample => Summary.IsSample;
    public string Source => Summary.Source;
    public int? ResultsSaved => Summary.ResultsSaved;
    public string? ErrorMessage => Summary.ErrorMessage;
}

/// <summary>Shared behaviour of pages that list assessments and show the findings of the selected one.</summary>
public abstract partial class AssessmentPageViewModel(IEngagementService engagements) : PageViewModel
{
    protected IEngagementService Engagements { get; } = engagements;

    public ObservableCollection<AssessmentRow> Assessments { get; } = [];
    public ObservableCollection<StoredAssessmentFinding> Findings { get; } = [];

    [ObservableProperty] private AssessmentRow? _selectedAssessment;
    [ObservableProperty] private string _findingsCaption = "Select an assessment to see its findings.";
    [ObservableProperty] private bool _isDatabaseAvailable;

    /// <summary>Customer key → display name, used for the Customer column.</summary>
    protected IReadOnlyDictionary<string, string> CustomerNames { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    partial void OnSelectedAssessmentChanged(AssessmentRow? value) => _ = LoadFindingsAsync(value);

    protected Task LoadAssessmentsAsync(Domain.Common.CustomerId? customerId, CancellationToken cancellationToken) =>
        LoadAssessmentsAsync(customerId is { } id ? [id.Value] : null, cancellationToken);

    /// <summary>Loads the assessments of the given customers (null = every customer).</summary>
    protected async Task LoadAssessmentsAsync(IReadOnlyCollection<string>? customerKeys, CancellationToken cancellationToken)
    {
        IsDatabaseAvailable = Engagements.IsDatabaseAvailable;
        Assessments.Clear();
        Findings.Clear();
        if (!IsDatabaseAvailable)
        {
            StatusMessage = "The application database is not ready, so assessment history is unavailable. Open Settings > Database Configuration.";
            return;
        }

        Domain.Common.CustomerId? single = customerKeys is { Count: 1 } && Domain.Common.CustomerId.TryCreate(customerKeys.First(), out var id) ? id : null;
        var result = await Engagements.ListAssessmentsAsync(single, cancellationToken);
        if (!result.Succeeded)
        {
            StatusMessage = result.Error!.ToString();
            return;
        }

        var keys = customerKeys?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var assessment in result.Value!.Where(item => keys is null || keys.Contains(item.CustomerKey)))
            Assessments.Add(new AssessmentRow(assessment, CustomerNames.GetValueOrDefault(assessment.CustomerKey, assessment.CustomerKey)));
        SelectedAssessment = Assessments.FirstOrDefault(assessment => AssessmentStatusCodes.HasResults(assessment.StatusCode)) ?? Assessments.FirstOrDefault();
    }

    private async Task LoadFindingsAsync(AssessmentRow? assessment)
    {
        Findings.Clear();
        if (assessment is null)
        {
            FindingsCaption = "Select an assessment to see its findings.";
            return;
        }

        var result = await Engagements.ListFindingsAsync(assessment.AssessmentRunId);
        if (!result.Succeeded)
        {
            FindingsCaption = result.Error!.ToString();
            return;
        }

        foreach (var finding in result.Value!) Findings.Add(finding);
        var partial = assessment.StatusCode == AssessmentStatusCodes.Partial ? $" The run stopped part-way: {assessment.ErrorMessage}" : string.Empty;
        FindingsCaption = Findings.Count == 0
            ? assessment.StatusCode == AssessmentStatusCodes.Pending
                ? $"'{assessment.Name}' is pending: it has no findings until the assessment engine runs it."
                : $"'{assessment.Name}' has no findings.{(assessment.ErrorMessage is { } error ? $" {error}" : string.Empty)}"
            : $"{Findings.Count:N0} finding(s) of '{assessment.Name}': {Findings.Count(finding => finding.Result == "Pass"):N0} passed, " +
              $"{Findings.Count(finding => finding.Result != "Pass"):N0} need attention.{partial}";
    }
}
