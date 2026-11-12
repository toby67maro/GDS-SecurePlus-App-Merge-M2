namespace ArchiTech.Api.Domain;

public sealed record PackageReference(string Sheet, string Name, IReadOnlyList<string> Components);
public sealed record PackageSignal(string Questionnaire, string Value, string Review, IReadOnlyList<string> ComponentTerms);
public sealed record PackageComparison(
    string Source,
    string Disclaimer,
    IReadOnlyList<PackageSignal> Signals,
    IReadOnlyList<PackageReference> Packages);

public static class SecurePlusPackages
{
    private static readonly string[] Shared =
    [
        "Acronis Microsoft 365 backup", "Acronis workstation backup", "Keeper Enterprise",
        "Exclaimer Email Signature Management - Standard", "Support", "NinjaOne",
        "Blackpoint Cyber", "CloudCapsule Premium"
    ];

    private static readonly string[] Essentials =
    [
        "Acronis Microsoft 365 backup", "Keeper Enterprise", "Exclaimer Email Signature Management - Standard",
        "Support", "Blackpoint Cyber", "CloudCapsule Premium"
    ];

    private static readonly string[] Academic =
        ["Acronis Microsoft 365 backup", "Support", "NinjaOne", "Blackpoint Cyber", "CloudCapsule Premium"];

    private static readonly PackageReference[] Packages =
    [
        new("Secure Plus Complete", "GDS Secure Plus Premium (Complete sheet)",
            [.. Shared, "GDS Direct Routing", "Microsoft 365 E7 (as listed; verify)"]),
        new("Secure Plus Complete", "GDS Secure Plus Premium w/o MS Licenses (Complete sheet)",
            [.. Shared, "GDS Direct Routing"]),
        new("Secure Plus Premium", "GDS Secure Plus Premium",
            [.. Shared, "GDS Direct Routing", "Microsoft 365 E5"]),
        new("Secure Plus Premium", "GDS Secure Plus Premium w/o MS Licenses",
            [.. Shared, "GDS Direct Routing"]),
        new("Secure Plus", "GDS Secure Plus",
            [.. Shared, "Microsoft 365 Business Premium", "Microsoft Defender Suite for Microsoft 365 Business Premium"]),
        new("Secure Plus", "GDS Secure Plus w/o MS Licenses", Shared),
        new("Secure Plus GDS Voice", "GDS SMB Secure Plus GDS Voice",
            [.. Shared, "Microsoft 365 Business Premium", "Microsoft 365 E5 Security", "GDS Hosted Voice"]),
        new("Secure Plus Teams Voice", "GDS SMB Secure Plus Teams Phone w/ Direct Routing",
            [.. Shared, "Microsoft 365 Business Premium", "Microsoft Defender Suite for Microsoft 365 Business Premium",
                "Microsoft Teams Phone Standard with GDS Direct Routing"]),
        new("Essentials", "GDS Essentials", [.. Essentials, "Microsoft 365 F3"]),
        new("Essentials", "GDS Essentials w/o MS Licenses", Essentials),
        new("Academic", "GDS Secure Plus Academic", [.. Academic, "Microsoft 365 A5 (faculty)"]),
        new("Academic", "GDS Secure Plus Academic Student", [.. Academic, "Microsoft 365 A5 (student)"])
    ];

    public static PackageComparison Compare(IEnumerable<EngagementAnswer> answers)
    {
        var labels = new Dictionary<string, (string Label, string Review, string[] Terms)>(StringComparer.Ordinal)
        {
            ["mfa"] = ("Current MFA", "Confirm sign-in requirements and any existing provider.", ["Microsoft 365"]),
            ["edr"] = ("Current endpoint protection", "Compare existing endpoint protection with the listed components; confirm scope.", ["Blackpoint", "Defender"]),
            ["emailSecurity"] = ("Current email security", "Confirm compatibility and whether the existing service must remain.", ["Defender", "Microsoft 365"]),
            ["deployMde"] = ("Defender for Endpoint", "Review licensing and product coverage; a package name alone does not establish entitlement.", ["Defender", "Microsoft 365"]),
            ["intune"] = ("Device management", "Confirm management capabilities and licensing before selecting a package.", ["NinjaOne", "Microsoft 365"]),
            ["aiReadiness"] = ("AI readiness", "Discuss AI readiness separately; it is not established by this bundle list.", []),
            ["orgIndustry"] = ("Industry", "Confirm eligibility and applicability, including academic variants where relevant.", []),
            ["currentLicenses"] = ("Current Microsoft 365 licenses", "Confirm existing entitlements before considering a license-inclusive or no-license variant.", ["Microsoft 365"]),
            ["backupModel"] = ("Current backup", "Review coverage, retention and compatibility with the current backup; listed components do not establish coverage.", ["Acronis"]),
            ["passwordManager"] = ("Current password manager", "Confirm requirements and whether an existing tool must remain.", ["Keeper"]),
            ["voiceNeeds"] = ("Voice requirements", "Compare hosted voice and Teams Phone offerings; confirm current availability and scope.", ["Voice", "Phone", "Routing"])
        };
        var signals = answers.Where(answer => labels.ContainsKey(answer.FieldKey))
            .OrderBy(answer => answer.FieldKey, StringComparer.Ordinal)
            .ThenBy(answer => answer.Value, StringComparer.Ordinal)
            .Select(answer => new PackageSignal(labels[answer.FieldKey].Label, answer.Value,
                labels[answer.FieldKey].Review, labels[answer.FieldKey].Terms))
            .ToArray();
        return new PackageComparison(
            "Secure Plus Bundles.xlsx, supplied 2026-11-09; service names only",
            "Discussion aid, not a recommendation, entitlement or coverage determination. Verify source and current offerings. The Complete and Premium sheets disagree on the Microsoft license. No pricing included.",
            signals, Packages);
    }
}
