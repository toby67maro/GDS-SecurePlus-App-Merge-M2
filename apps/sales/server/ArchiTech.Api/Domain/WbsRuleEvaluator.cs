namespace ArchiTech.Api.Domain;

public static class WbsRuleEvaluator
{
    public static bool Applies(
        string ruleKey,
        IReadOnlyDictionary<string, IReadOnlySet<string>> answers)
    {
        bool IsYes(string key) =>
            answers.TryGetValue(key, out var selected) && selected.Contains("yes");

        bool Has(string key, params string[] options) =>
            answers.TryGetValue(key, out var selected) &&
            options.Any(option => selected.Contains(option, StringComparer.OrdinalIgnoreCase));

        bool HasStorage() => answers.TryGetValue("storage", out var storage) && storage.Count > 0;
        bool HasAnyStorageOrPoorSharePoint() => HasStorage() || IsYes("poorSharePoint");
        var thirdPartyMfa = Has("mfa", "duo", "okta", "other");
        var thirdPartyEmailSecurity = Has("emailSecurity", "proofpoint", "mimecast", "egress", "other");

        return ruleKey switch
        {
            "all" => true,
            "onprem" => IsYes("onPremAD") || IsYes("exchangeOnPrem") || Has("storage", "fileserver"),
            "anyStorage" => HasAnyStorageOrPoorSharePoint(),
            "thirdPartyMfa" => thirdPartyMfa,
            "moveMsMfa" => thirdPartyMfa && IsYes("moveToMsMfa"),
            "deployMde" => IsYes("deployMde"),
            "replaceEdr" => Has("edr", "third-party") && IsYes("deployMde"),
            "hasAD" => IsYes("onPremAD"),
            "gpos" => IsYes("onPremAD") && IsYes("gpos"),
            "cloudOnly" => IsYes("onPremAD") && Has("identityTarget", "cloud-only"),
            "exchange" => IsYes("exchangeOnPrem"),
            "migrateExo" => IsYes("exchangeOnPrem") && IsYes("migrateExchange"),
            "sendEmail" => IsYes("devicesSendEmail"),
            "thirdPartyEmailSec" => thirdPartyEmailSecurity,
            "moveDefO365" => thirdPartyEmailSecurity && IsYes("moveEmailSecurity"),
            "fileServer" => Has("storage", "fileserver"),
            "cloudStorage" => Has("storage", "egnyte", "box", "dropbox", "other-cloud"),
            "poorSharePoint" => IsYes("poorSharePoint"),
            "dataWork" => HasAnyStorageOrPoorSharePoint() && IsYes("modernizeData"),
            "dataMigration" => HasStorage() && IsYes("modernizeData"),
            "intune" => IsYes("intune"),
            "whfb" => IsYes("whfb"),
            "apple" => IsYes("apple"),
            "appleOwned" => IsYes("apple") && IsYes("appleOwned"),
            "byod" => IsYes("byod"),
            "redundantTools" => IsYes("redundantTools"),
            "purview" => Has("purview", "labels", "retention", "dlp"),
            "labels" => Has("purview", "labels"),
            "retention" => Has("purview", "retention"),
            "dlp" => Has("purview", "dlp"),
            "usesSharePoint" => IsYes("usesSharePoint"),
            "external" => IsYes("externalCollab"),
            "ai" => IsYes("aiReadiness"),
            "agents" => IsYes("aiReadiness") && IsYes("agents"),
            _ => throw new InvalidDataException($"WBS rule key '{ruleKey}' is not supported.")
        };
    }
}

public sealed record WbsEstimate(
    IReadOnlyList<WbsTask> Tasks,
    IReadOnlyList<PhaseEstimate> ByPhase,
    decimal OneTimeHoursMin,
    decimal OneTimeHoursMax,
    decimal MonthlyHoursMin,
    decimal MonthlyHoursMax);

public sealed record PhaseEstimate(
    string Phase,
    int TaskCount,
    decimal MinHours,
    decimal MaxHours,
    bool IsMonthly);

public static class WbsEstimator
{
    public static WbsEstimate Calculate(
        IEnumerable<WbsTask> catalog,
        IReadOnlyDictionary<string, IReadOnlySet<string>> answers)
    {
        var applicable = catalog
            .Where(task => WbsRuleEvaluator.Applies(task.RuleKey, answers))
            .OrderBy(task => task.Sequence)
            .ToArray();
        var oneTime = applicable.Where(task => !IsMonthly(task)).ToArray();
        var monthly = applicable.Where(IsMonthly).ToArray();
        var phases = applicable
            .GroupBy(task => task.Phase)
            .Select(group => new PhaseEstimate(
                group.Key,
                group.Count(),
                group.Sum(task => task.MinHours),
                group.Sum(task => task.MaxHours),
                IsMonthly(group.First())))
            .ToArray();

        return new WbsEstimate(
            applicable,
            phases,
            oneTime.Sum(task => task.MinHours),
            oneTime.Sum(task => task.MaxHours),
            monthly.Sum(task => task.MinHours),
            monthly.Sum(task => task.MaxHours));
    }

    private static bool IsMonthly(WbsTask task) =>
        task.Phase.StartsWith("Ongoing Secure Plus Operations", StringComparison.OrdinalIgnoreCase);
}
