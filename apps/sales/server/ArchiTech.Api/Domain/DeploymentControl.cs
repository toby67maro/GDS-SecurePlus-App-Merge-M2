namespace ArchiTech.Api.Domain;

public sealed record DeploymentControl(
    string ControlId,
    string Name,
    string Category,
    string DeploymentPhase,
    string AssessmentLevel,
    string Description,
    string MicrosoftSolution,
    string UserImpact,
    string RecommendedState,
    string ValidationMethod,
    string SourceReference,
    int PointValue,
    bool IsActive,
    string WbsId,
    IReadOnlyList<string> Dependencies);

public sealed class DeploymentControlCatalog(IReadOnlyList<DeploymentControl> controls)
{
    public IReadOnlyList<DeploymentControl> Controls { get; } = controls;

    public static DeploymentControlCatalog Load(string path)
    {
        var json = File.ReadAllText(path);
        var controls = System.Text.Json.JsonSerializer.Deserialize<List<DeploymentControl>>(
            json,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("The deployment control catalog is empty or invalid.");

        if (controls.Count != 371 ||
            controls.Select(control => control.ControlId).Distinct(StringComparer.Ordinal).Count() != controls.Count)
        {
            throw new InvalidOperationException("The imported deployment catalog must contain 371 unique controls.");
        }

        return new DeploymentControlCatalog(controls);
    }
}

public static class DeploymentScoring
{
    public static DeploymentScore Calculate(
        IEnumerable<DeploymentControl> controls,
        IReadOnlyDictionary<string, string> statuses)
    {
        var points = controls.Sum(control => control.PointValue);
        var earned = 0d;
        var statusCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Implemented"] = 0,
            ["PartiallyImplemented"] = 0,
            ["NotImplemented"] = 0,
            ["NotAssessed"] = 0,
            ["NotApplicable"] = 0,
            ["Exception"] = 0
        };

        foreach (var control in controls)
        {
            var status = statuses.GetValueOrDefault(control.ControlId, "NotAssessed");
            if (!statusCounts.ContainsKey(status))
            {
                status = "NotAssessed";
            }
            statusCounts[status]++;
            earned += status switch
            {
                "Implemented" or "NotApplicable" => control.PointValue,
                "PartiallyImplemented" => Math.Round(control.PointValue * 0.5, MidpointRounding.AwayFromZero),
                "Exception" => Math.Round(control.PointValue * 0.75, MidpointRounding.AwayFromZero),
                _ => 0
            };
        }

        return new DeploymentScore(
            points == 0 ? 0 : Math.Round(earned / points * 100, 1),
            controls.Count(),
            statusCounts);
    }
}

public sealed record DeploymentScore(
    double OverallPercentage,
    int TotalControls,
    IReadOnlyDictionary<string, int> StatusCounts);
