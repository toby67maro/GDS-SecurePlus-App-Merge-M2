using System.Globalization;

namespace ArchiTech.Api.Domain;

public sealed record NormalizedAnswers(
    IReadOnlyDictionary<string, IReadOnlySet<string>> Values,
    IReadOnlyDictionary<string, string[]> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public static class AnswerCatalog
{
    private static readonly HashSet<string> YesNo =
        ["yes", "no"];

    private static readonly Dictionary<string, HashSet<string>> Choices = new(StringComparer.Ordinal)
    {
        ["identityTarget"] = ["hybrid", "cloud-only", "undecided"],
        ["mfa"] = ["microsoft", "duo", "okta", "other", "none"],
        ["emailSecurity"] = ["microsoft", "proofpoint", "mimecast", "egress", "other"],
        ["edr"] = ["defender", "third-party", "none"],
        ["storage"] = ["fileserver", "egnyte", "box", "dropbox", "other-cloud"],
        ["purview"] = ["labels", "retention", "dlp"],
        ["orgItModel"] = ["internal", "outsourced", "both", "neither", "unknown"],
        ["orgWorkModel"] = ["office", "remote", "hybrid", "unknown"],
        ["orgInfrastructure"] = ["on-premises", "cloud", "both", "unknown"],
        ["currentLicenses"] = ["business-basic", "business-standard", "business-premium", "e3-e5", "other", "unknown", "not-applicable"],
        ["backupModel"] = ["managed", "native-manual", "none", "unknown"],
        ["passwordManager"] = ["yes", "no", "unknown"],
        ["voiceNeeds"] = ["hosted", "teams", "neither", "undecided"]
    };

    private static readonly HashSet<string> MultiValue =
        ["storage", "purview", "orgDepartments", "currentLicenses"];

    private static readonly HashSet<string> Numeric =
        ["dcCount", "fileServerTB", "orgLocations", "orgMobileDevices"];

    private static readonly HashSet<string> FreeText =
        ["orgIndustry", "orgDepartments"];

    private static readonly HashSet<string> YesNoFields =
    [
        "onPremAD", "gpos", "moveToMsMfa", "exchangeOnPrem", "migrateExchange",
        "devicesSendEmail", "moveEmailSecurity", "deployMde", "intune", "whfb",
        "apple", "appleOwned", "byod", "poorSharePoint", "modernizeData",
        "usesSharePoint", "externalCollab", "aiReadiness", "agents", "redundantTools"
    ];

    private static readonly HashSet<string> AllFields =
        YesNoFields.Concat(Choices.Keys).Concat(Numeric).Concat(FreeText).ToHashSet(StringComparer.Ordinal);

    public static NormalizedAnswers Normalize(IReadOnlyDictionary<string, string[]?>? rawAnswers)
    {
        var values = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (rawAnswers is null)
        {
            return new NormalizedAnswers(values, errors);
        }

        foreach (var (key, rawValues) in rawAnswers)
        {
            if (!AllFields.Contains(key))
            {
                errors[key] = ["This answer is not part of the sales intake."];
                continue;
            }

            var selected = (rawValues ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (selected.Length == 0)
            {
                continue;
            }

            if (key == "orgDepartments" &&
                (selected.Length > 20 || selected.Any(value => value.Length > 80)))
            {
                errors[key] = ["Enter at most 20 departments, each 80 characters or fewer."];
                continue;
            }

            if (key == "currentLicenses" && selected.Length > 1 &&
                selected.Any(value => value.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("not-applicable", StringComparison.OrdinalIgnoreCase)))
            {
                errors[key] = ["Unknown or not applicable cannot be combined with a license selection."];
                continue;
            }

            if (!MultiValue.Contains(key) && selected.Length > 1)
            {
                errors[key] = ["Choose one answer."];
                continue;
            }

            var invalid = selected.Any(value => !IsValidValue(key, value));
            if (invalid)
            {
                errors[key] = ["Select a valid option for this answer."];
                continue;
            }

            if (selected.Any(value => value.Length > 240))
            {
                errors[key] = ["Answer values must be 240 characters or fewer."];
                continue;
            }

            values[key] = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        RemoveHiddenAnswers(values);
        return new NormalizedAnswers(values, errors);
    }

    private static bool IsValidValue(string key, string value)
    {
        if (FreeText.Contains(key))
        {
            return !value.Any(char.IsControl);
        }
        if (YesNoFields.Contains(key))
        {
            return YesNo.Contains(value, StringComparer.OrdinalIgnoreCase);
        }

        if (Choices.TryGetValue(key, out var choices))
        {
            return choices.Contains(value);
        }

        return Numeric.Contains(key) &&
               decimal.TryParse(
                   value,
                   NumberStyles.AllowDecimalPoint,
                   CultureInfo.InvariantCulture,
                   out var number) &&
               number >= 0 &&
               (key is not ("dcCount" or "orgLocations" or "orgMobileDevices") ||
                decimal.Truncate(number) == number);
    }

    private static void RemoveHiddenAnswers(Dictionary<string, IReadOnlySet<string>> values)
    {
        var hasOnPremAd = IsYes(values, "onPremAD");
        var hasThirdPartyMfa = HasAny(values, "mfa", "duo", "okta", "other");
        var hasThirdPartyEmailSecurity =
            HasAny(values, "emailSecurity", "proofpoint", "mimecast", "egress", "other");
        var hasStorage = values.TryGetValue("storage", out var storage) && storage.Count > 0;
        var hasPoorSharePoint = IsYes(values, "poorSharePoint");
        var pursuingAi = IsYes(values, "aiReadiness");

        if (!hasOnPremAd)
        {
            values.Remove("dcCount");
            values.Remove("gpos");
            values.Remove("identityTarget");
        }

        if (!hasThirdPartyMfa)
        {
            values.Remove("moveToMsMfa");
        }

        if (!IsYes(values, "exchangeOnPrem"))
        {
            values.Remove("migrateExchange");
        }

        if (!hasThirdPartyEmailSecurity)
        {
            values.Remove("moveEmailSecurity");
        }

        if (!IsYes(values, "apple"))
        {
            values.Remove("appleOwned");
        }

        if (!values.TryGetValue("storage", out var selectedStorage) ||
            !selectedStorage.Contains("fileserver"))
        {
            values.Remove("fileServerTB");
        }

        if (!hasStorage && !hasPoorSharePoint)
        {
            values.Remove("modernizeData");
        }

        if (!pursuingAi)
        {
            values.Remove("agents");
        }
    }

    private static bool IsYes(
        IReadOnlyDictionary<string, IReadOnlySet<string>> values,
        string key) =>
        values.TryGetValue(key, out var selected) && selected.Contains("yes");

    private static bool HasAny(
        IReadOnlyDictionary<string, IReadOnlySet<string>> values,
        string key,
        params string[] choices) =>
        values.TryGetValue(key, out var selected) && choices.Any(selected.Contains);
}
