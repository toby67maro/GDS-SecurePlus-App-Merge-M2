namespace ArchiTech.Api.Domain;

public sealed record AssessmentScoreCalculation(
    double OverallScore,
    double OverallPercentage,
    int TotalControls,
    int ImplementedControls,
    int PartialControls,
    int NotImplementedControls,
    int NotAssessedControls,
    int ExceptionControls,
    int NotApplicableControls,
    string ReadinessRating,
    IReadOnlyList<AssessmentScoreBreakdownValue> Breakdowns,
    IReadOnlyList<AssessmentRecommendationValue> Recommendations,
    IReadOnlyList<string> CriticalGaps);

public sealed record AssessmentScoreBreakdownValue(
    string BreakdownType,
    string Label,
    double Percentage,
    int TotalPoints,
    int EarnedPoints,
    int GapCount);

public sealed record AssessmentRecommendationValue(string Severity, string Text);

public static class AssessmentScoring
{
    public static AssessmentScoreCalculation Calculate(
        IEnumerable<DeploymentControl> controls,
        IReadOnlyDictionary<string, string> statuses)
    {
        var catalog = controls.ToArray();
        var implemented = 0;
        var partial = 0;
        var notImplemented = 0;
        var notAssessed = 0;
        var exceptions = 0;
        var notApplicable = 0;
        var earnedTotal = 0;
        var possibleTotal = catalog.Sum(control => control.PointValue);
        var buckets = new Dictionary<(string Type, string Label), (int Earned, int Total, int Gaps)>();
        var criticalGaps = new List<DeploymentControl>();

        foreach (var control in catalog)
        {
            var status = statuses.GetValueOrDefault(control.ControlId, "NotAssessed");
            if (status is not ("Implemented" or "PartiallyImplemented" or "NotImplemented" or
                "NotAssessed" or "NotApplicable" or "Exception"))
            {
                status = "NotAssessed";
            }

            var points = status switch
            {
                "Implemented" or "NotApplicable" => control.PointValue,
                "PartiallyImplemented" => (int)Math.Round(control.PointValue * 0.5, MidpointRounding.AwayFromZero),
                "Exception" => (int)Math.Round(control.PointValue * 0.75, MidpointRounding.AwayFromZero),
                _ => 0
            };
            earnedTotal += points;

            switch (status)
            {
                case "Implemented": implemented++; break;
                case "PartiallyImplemented": partial++; break;
                case "NotImplemented": notImplemented++; break;
                case "NotAssessed": notAssessed++; break;
                case "NotApplicable": notApplicable++; break;
                case "Exception": exceptions++; break;
            }

            var gap = status is "NotImplemented" or "NotAssessed";
            AddBucket(buckets, "Category", control.Category, points, control.PointValue, gap);
            AddBucket(buckets, "Level", FormatLevel(control.AssessmentLevel), points, control.PointValue, gap);
            AddBucket(buckets, "Phase", FormatPhase(control.DeploymentPhase), points, control.PointValue, gap);
            if (gap && control.PointValue >= 3)
            {
                criticalGaps.Add(control);
            }
        }

        var breakdowns = buckets
            .OrderBy(item => item.Key.Type, StringComparer.Ordinal)
            .ThenBy(item => item.Key.Label, StringComparer.Ordinal)
            .Select(item => new AssessmentScoreBreakdownValue(
                item.Key.Type,
                item.Key.Label,
                item.Value.Total == 0 ? 0 : Math.Round(item.Value.Earned / (double)item.Value.Total * 100, 1),
                item.Value.Total,
                item.Value.Earned,
                item.Value.Gaps))
            .ToArray();
        var percentage = possibleTotal == 0
            ? 0
            : Math.Round(earnedTotal / (double)possibleTotal * 100, 1);
        var readiness = Readiness(percentage);
        var recommendations = Recommend(percentage, breakdowns, implemented, notImplemented + notAssessed)
            .ToArray();

        return new AssessmentScoreCalculation(
            earnedTotal,
            percentage,
            catalog.Length,
            implemented,
            partial,
            notImplemented,
            notAssessed,
            exceptions,
            notApplicable,
            readiness,
            breakdowns,
            recommendations,
            criticalGaps.OrderByDescending(control => control.PointValue)
                .ThenBy(control => control.ControlId, StringComparer.Ordinal)
                .Take(20)
                .Select(control => $"{control.ControlId}: {control.Name}")
                .ToArray());
    }

    public static string Readiness(double percentage) => percentage switch
    {
        >= 90 => "AIReady",
        >= 75 => "LowRisk",
        >= 55 => "ModerateRisk",
        >= 35 => "HighRisk",
        _ => "Critical"
    };

    public static string FormatPhase(string phase)
    {
        var spaced = System.Text.RegularExpressions.Regex.Replace(
            phase.Replace('_', ' '),
            @"([a-z])([A-Z])",
            "$1 $2",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return System.Text.RegularExpressions.Regex.Replace(
            spaced,
            @"^Phase(\d+)",
            "Phase $1 —",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            .Trim();
    }

    private static string FormatLevel(string level) => level switch
    {
        "Secure" => "1 — Secure",
        "Protect" => "2 — Protect",
        "Control" => "3 — Control",
        "Enable" => "4 — Enable",
        _ => level
    };

    private static void AddBucket(
        IDictionary<(string Type, string Label), (int Earned, int Total, int Gaps)> buckets,
        string type,
        string label,
        int earned,
        int total,
        bool gap)
    {
        var key = (type, label);
        buckets.TryGetValue(key, out var current);
        buckets[key] = (current.Earned + earned, current.Total + total, current.Gaps + (gap ? 1 : 0));
    }

    private static IEnumerable<AssessmentRecommendationValue> Recommend(
        double percentage,
        IReadOnlyList<AssessmentScoreBreakdownValue> breakdowns,
        int implemented,
        int gaps)
    {
        var levelScores = breakdowns.Where(item => item.BreakdownType == "Level")
            .ToDictionary(item => item.Label, item => item.Percentage, StringComparer.Ordinal);
        var categoryScores = breakdowns.Where(item => item.BreakdownType == "Category")
            .ToDictionary(item => item.Label, item => item.Percentage, StringComparer.Ordinal);

        if (levelScores.GetValueOrDefault("1 — Secure", 0) < 70)
        {
            yield return new("High", "Prioritize foundational Secure controls before advancing to higher deployment phases.");
        }
        if (categoryScores.GetValueOrDefault("Authentication", 100) < 60)
        {
            yield return new("Critical", "Review authentication gaps, including MFA, legacy authentication, and conditional access, with the customer.");
        }
        if (categoryScores.GetValueOrDefault("ConditionalAccess", 100) < 60)
        {
            yield return new("High", "Review Conditional Access coverage and validate policy impacts before planning changes.");
        }
        if (categoryScores.GetValueOrDefault("DataProtection", 100) < 60)
        {
            yield return new("High", "Review data-protection gaps, including DLP and sensitivity-label requirements.");
        }
        if (percentage is >= 75 and < 90)
        {
            yield return new("Medium", "The assessment is approaching the readiness threshold; review Protect and Control gaps with the customer.");
        }
        if (gaps > implemented)
        {
            yield return new("Medium", "More controls are open or unassessed than implemented; prioritize a customer-reviewed remediation roadmap.");
        }
        if (percentage >= 90)
        {
            yield return new("Informational", "The weighted assessment score is in the AI Ready range; validate all exceptions and evidence before relying on this result.");
        }
    }
}
