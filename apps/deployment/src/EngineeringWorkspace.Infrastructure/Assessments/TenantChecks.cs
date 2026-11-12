using System.Globalization;
using System.Text.Json;
using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Catalog;
using static EngineeringWorkspace.Infrastructure.Assessments.AssessmentEvaluator;

namespace EngineeringWorkspace.Infrastructure.Assessments;

/// <summary>
/// The GDS Microsoft 365 baseline checks (catalog kind AssessmentCheck, keyed by the Cloud Capsule check ID),
/// evaluated from Microsoft Graph and public DNS. Checks that need Exchange Online or Teams PowerShell, or a portal
/// review, are reported as Manual with the catalog's click path.
/// </summary>
public static class TenantChecks
{
    private const string GlobalAdministrator = "62e90394-69f5-4237-9190-012177145e10";
    private const string AzureManagementApp = "797f4846-ba00-4fd7-ba43-dac1f8f63013";
    private const string IntuneEnrollmentApp = "d4ebce55-015a-49b5-a083-c84d1797ae8c";
    private const string PhishingResistantStrength = "00000000-0000-0000-0000-000000000004";
    private const string RestrictedGuestRole = "10dae51f-b6af-4016-8d66-8c2a99b929b3";
    private const string MostRestrictedGuestRole = "2af84b1e-32c8-42b7-82bc-daa82404023b";

    /// <summary>Highly privileged directory role templates.</summary>
    private static readonly HashSet<string> PrivilegedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        GlobalAdministrator,
        "e8611ab8-c189-46e8-94e1-60213ab1f814", // Privileged Role Administrator
        "7be44c8a-adaf-4e2a-84d6-ab2649e08a13", // Privileged Authentication Administrator
        "194ae4cb-b126-40b2-bd5b-6091b380977d", // Security Administrator
        "b1be1c3e-b65d-4f19-8427-f6fa0d97feb9", // Conditional Access Administrator
        "29232cdf-9323-42fd-ade2-1d097af3e4de", // Exchange Administrator
        "f28a1f50-f6e7-4571-818b-6a12f2af6b6c", // SharePoint Administrator
        "fe930be7-5e62-47db-91af-98c3a49a38b1", // User Administrator
        "9b895d92-2cd3-44c7-9d02-a6ac2d5ea5c3", // Application Administrator
        "158c047a-c907-4556-b7ef-446551a6b5f7", // Cloud Application Administrator
        "3a2c62db-5318-420d-8d74-23affee5d9d5", // Intune Administrator
        "c4e39bd9-1100-46d3-8c65-fb160da0071f", // Authentication Administrator
    };

    private sealed record Outcome(string Result, string Evidence, string? Recommendation = null);

    private delegate Outcome Check(TenantSnapshot snapshot);

    private static Outcome Pass(string evidence) => new(AssessmentResultValues.Pass, evidence);
    private static Outcome Fail(string evidence, string? recommendation = null) => new(AssessmentResultValues.Fail, evidence, recommendation);
    private static Outcome Partial(string evidence, string? recommendation = null) => new(AssessmentResultValues.Partial, evidence, recommendation);

    private static readonly Dictionary<string, (string[] Datasets, Check Check)> Checks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mfa_is_enforced_for_all_users"] = (Ca(), s => CaCheck(s, p => AllUsers(p) && AllApps(p) && RequiresMfa(p), "requires MFA for all users and all cloud apps")),
        ["mfa_is_enforced_for_azure_management"] = (Ca(), s => CaCheck(s, p => AllUsers(p) && (AllApps(p) || Apps(p).Contains(AzureManagementApp, StringComparer.OrdinalIgnoreCase)) && RequiresMfa(p),
            "requires MFA for Azure management")),
        ["mfa_is_enforced_on_accounts_with_highly_privileged_roles"] = (Ca(), s => CaCheck(s, p => (Roles(p).Contains(GlobalAdministrator, StringComparer.OrdinalIgnoreCase) || AllUsers(p)) && AllApps(p) && RequiresMfa(p),
            "requires MFA for privileged roles")),
        ["legacy_authentication_shall_be_blocked"] = (Ca(), s => CaCheck(s, p => ClientApps(p).Any(app => app is "exchangeActiveSync" or "other") && Grant(p).Contains("block"),
            "blocks legacy authentication (Exchange ActiveSync and other clients)")),
        ["mfa_shall_be_required_to_enroll_devices_to_azure_ad"] = (Ca(), s => CaCheck(s, p => UserActions(p).Contains("urn:user:registerdevice") && RequiresMfa(p),
            "requires MFA to register or join devices")),
        ["mfa_shall_be_required_for_intune_enrollment"] = (Ca(), s => CaCheck(s, p => Apps(p).Contains(IntuneEnrollmentApp, StringComparer.OrdinalIgnoreCase) && RequiresMfa(p),
            "requires MFA for Microsoft Intune Enrollment")),
        ["managed_devices_shall_be_required_for_authentication"] = (Ca(), s => CaCheck(s, p => Grant(p).Overlaps(["compliantDevice", "domainJoinedDevice"]) && (AllApps(p) || Apps(p).Contains("Office365")),
            "requires a compliant or hybrid-joined device")),
        ["noncompliant_devices_shall_be_blocked_from_accessing_corporate_resources"] = (Ca(), s => CaCheck(s, p => Grant(p).Contains("compliantDevice"),
            "requires a compliant device")),
        ["ensure_phishingresistant_mfa_strength_is_required_for_administrators"] = (Ca(), s => CaCheck(s, p => Roles(p).Any() && Strength(p) == PhishingResistantStrength,
            "requires phishing-resistant authentication strength for administrators")),
        ["high_risk_users_shall_be_blocked"] = (Ca(), s => CaCheck(s, p => Strings(Property(p, "conditions"), "userRiskLevels").Contains("high") && Grant(p).Overlaps(["block", "passwordChange"]),
            "blocks (or forces a password change for) high-risk users")),
        ["browser_sessions_shall_not_be_persistent_for_privileged_users"] = (Ca(), s => CaCheck(s, p => Roles(p).Any() &&
            (Property(Property(p, "sessionControls"), "persistentBrowser").ValueKind == JsonValueKind.Object || Property(Property(p, "sessionControls"), "signInFrequency").ValueKind == JsonValueKind.Object),
            "limits browser session persistence or sign-in frequency for administrators")),
        ["device_code_blocked"] = (Ca(), s => CaCheck(s, p => Text(Property(Property(p, "conditions"), "authenticationFlows"), "transferMethods").Contains("deviceCodeFlow", StringComparison.OrdinalIgnoreCase) && Grant(p).Contains("block"),
            "blocks the device code flow")),
        ["require_app_protection_policy_for_android_and_ios"] = (Ca(), s => CaCheck(s, p => Platforms(p).Overlaps(["android", "iOS"]) && Grant(p).Overlaps(["compliantApplication", "approvedApplication"]),
            "requires app protection policies on Android and iOS")),

        ["users_are_enrolled_in_mfa_and_covered_by_a_policy"] = ([TenantDatasets.RegistrationDetails], MfaRegistration),
        ["ensure_that_between_two_and_four_global_admins_are_designated"] = ([TenantDatasets.RoleAssignments], GlobalAdminCount),
        ["highly_privileged_accounts_shall_be_cloudonly"] = ([TenantDatasets.RoleAssignments], CloudOnlyAdmins),
        ["users_assigned_highly_privileged_roles_shall_not_have_permanent_permissions"] = ([TenantDatasets.RoleAssignments, TenantDatasets.RoleEligibilitySchedules], PermanentAdmins),
        ["break_glass_users_are_created_for_emergency_access"] = ([TenantDatasets.Users], BreakGlass),
        ["only_admins_shall_be_allowed_to_register_3rd_party_applications"] = ([TenantDatasets.AuthorizationPolicy], s =>
            Bool(DefaultPermissions(s), "allowedToCreateApps") == false ? Pass("Users cannot register applications.") : Fail("Users can register applications.", "Set 'Users can register applications' to No.")),
        ["non-admin_users_shall_be_prevented_from_providing_consent_to_3rd_party_applications"] = ([TenantDatasets.AuthorizationPolicy], s =>
        {
            var policies = Strings(DefaultPermissions(s), "permissionGrantPoliciesAssigned").Where(p => p.StartsWith("ManagePermissionGrantsForSelf", StringComparison.OrdinalIgnoreCase)).ToArray();
            return policies.Length == 0 ? Pass("User consent to applications is not allowed.") : Fail($"User consent is allowed ({string.Join(", ", policies)}).", "Set user consent to 'Do not allow user consent'.");
        }),
        ["guest_users_have_limited_access_to_properties_and_memberships_of_directory_objects"] = ([TenantDatasets.AuthorizationPolicy], s =>
        {
            var role = Text(First(s, TenantDatasets.AuthorizationPolicy), "guestUserRoleId");
            return role is RestrictedGuestRole or MostRestrictedGuestRole ? Pass("Guest access is limited.") : Fail("Guests have the same access as members.", "Set guest user access to 'limited access' or 'restricted'.");
        }),
        ["passwords_shall_not_expire"] = ([TenantDatasets.Domains], s =>
        {
            var expiring = s.Get(TenantDatasets.Domains).Items.Where(d => Property(d, "isVerified").ValueKind == JsonValueKind.True && Property(d, "passwordValidityPeriodInDays") is { ValueKind: JsonValueKind.Number } days && days.GetInt64() < 2147483647)
                .Select(d => Text(d, "id")).ToArray();
            return expiring.Length == 0 ? Pass("Passwords are set never to expire.") : Fail($"Passwords expire on: {string.Join(", ", expiring)}.", "Set the password expiration policy to 'never expire'.");
        }),
        ["dormant_accounts_are_disabled_after_45_days"] = ([TenantDatasets.Users], DormantAccounts),
        ["devices_shall_be_deleted_that_havent_checked_in_for_over_30_days"] = ([TenantDatasets.Devices], s =>
        {
            var cutoff = s.CollectedAtUtc.AddDays(-30);
            var stale = s.Get(TenantDatasets.Devices).Items.Count(d => Date(d, "approximateLastSignInDateTime") is { } last && last < cutoff);
            return stale == 0 ? Pass("No device has been inactive for more than 30 days.") : Fail($"{stale} device(s) inactive for more than 30 days.", "Disable, then delete stale devices.");
        }),
        ["dynamic_groups_are_leveraged_for_automated_group_management"] = ([TenantDatasets.DynamicGroups], s =>
        {
            var count = s.Get(TenantDatasets.DynamicGroups).Items.Count;
            return count > 0 ? Pass($"{count} dynamic group(s).") : Fail("No dynamic groups.", "Create dynamic user and device groups for policy targeting.");
        }),
        ["all_corporate_approved_applications_are_cataloged_and_periodically_reviewed"] = ([TenantDatasets.Applications], s =>
            new Outcome(AssessmentResultValues.Info, $"{s.Get(TenantDatasets.Applications).Items.Count} app registration(s) to review.", "Review app registrations and enterprise apps; remove unused ones.")),

        ["an_spf_policys_that_designates_approved_ips_is_published"] = ([TenantDatasets.DnsRecords], s => DnsCheck(s, "spf", "SPF")),
        ["dmarc_is_configured_for_every_custom_domain"] = ([TenantDatasets.DnsRecords], s => DnsCheck(s, "dmarc", "DMARC")),
        ["dkim_is_configured_for_every_custom_domain"] = ([TenantDatasets.DnsRecords], s => DnsCheck(s, "dkimSelector1", "DKIM selector1")),

        ["windows_update_rings_shall_be_configured_for_windows_devices"] = ([TenantDatasets.DeviceConfigurations], s => Exists(s, TenantDatasets.DeviceConfigurations,
            d => Text(d, "@odata.type").Contains("windowsUpdateForBusinessConfiguration", StringComparison.OrdinalIgnoreCase), "Windows Update ring", "Create Windows Update rings for pilot and broad rings.")),
        ["update_policies_shall_be_configured_for_apple_devices"] = ([TenantDatasets.DeviceConfigurations, TenantDatasets.ConfigurationPolicies], s =>
            AnyConfigured(s, d => Text(d, "@odata.type").Contains("UpdateConfiguration", StringComparison.OrdinalIgnoreCase) && Text(d, "@odata.type").Contains("ios", StringComparison.OrdinalIgnoreCase),
                "softwareupdate", "Apple software update policy")),
        ["managed_devices_are_enrolled_in_mdm"] = ([TenantDatasets.ManagedDevices], s =>
        {
            var count = s.Get(TenantDatasets.ManagedDevices).Items.Count;
            return count > 0 ? Pass($"{count} device(s) enrolled in Intune.") : Fail("No devices are enrolled in Intune.", "Enable automatic MDM enrollment and enroll devices.");
        }),
        ["personal_devices_should_be_restricted_from_enrolling_into_the_mdm_solution"] = ([TenantDatasets.EnrollmentConfigurations], PersonalEnrollment),
        ["security_baselines_should_be_configured_for_windows_devices"] = ([TenantDatasets.ConfigurationPolicies], s => Exists(s, TenantDatasets.ConfigurationPolicies,
            d => Text(Property(d, "templateReference"), "templateFamily").Equals("baseline", StringComparison.OrdinalIgnoreCase), "Windows security baseline", "Deploy the Windows security baseline.")),
        ["devices_compliance_policies_shall_be_configured_for_every_supported_device_platform"] = ([TenantDatasets.CompliancePolicies], CompliancePlatforms),
        ["encryption_shall_be_required_on_all_devices"] = ([TenantDatasets.CompliancePolicies, TenantDatasets.ConfigurationPolicies], s =>
            AnyConfigured(s, d => Bool(d, "bitLockerEnabled") == true || Bool(d, "storageRequireEncryption") == true, "bitlocker_requiredeviceencryption", "Device encryption requirement")),
        ["lockout_screen_and_password_settings_shall_be_configured_for_each_device"] = ([TenantDatasets.CompliancePolicies], s => Exists(s, TenantDatasets.CompliancePolicies,
            d => Bool(d, "passwordRequired") == true, "compliance policy requiring a password", "Require a password or PIN in every platform's compliance policy.")),
        ["app_protection_policies_should_be_created_for_mobile_devices"] = ([TenantDatasets.AndroidAppProtections, TenantDatasets.IosAppProtections], s =>
        {
            var android = s.Get(TenantDatasets.AndroidAppProtections).Items.Count;
            var ios = s.Get(TenantDatasets.IosAppProtections).Items.Count;
            return android > 0 && ios > 0 ? Pass($"{android} Android and {ios} iOS app protection policies.")
                : android + ios > 0 ? Partial($"{android} Android and {ios} iOS app protection policies.", "Create app protection policies for both Android and iOS.")
                : Fail("No app protection policies.", "Create app protection policies for Android and iOS.");
        }),
        ["authorized_applications_should_be_deployed_to_managed_devices"] = ([TenantDatasets.AssignedApps], s =>
        {
            var count = s.Get(TenantDatasets.AssignedApps).Items.Count;
            return count > 0 ? Pass($"{count} assigned app(s).") : Fail("No apps are assigned to devices or users.", "Assign the approved apps as required.");
        }),
        ["ensure_local_administrator_password_solution_is_enabled"] = ([TenantDatasets.ConfigurationPolicies], s => SettingExists(s, "device_vendor_msft_laps_policies", "Windows LAPS")),
        ["onedrive_sync_requirement"] = ([TenantDatasets.ConfigurationPolicies], s => SettingExists(s, "onedrive", "OneDrive configuration")),

        ["external_sharing_should_be_limited_to_approved_domains_and_security_groups_per_interagency_collaboration_needs"] = ([TenantDatasets.SharePointSettings], s =>
        {
            var capability = Text(First(s, TenantDatasets.SharePointSettings), "sharingCapability");
            return capability is "externalUserAndGuestSharing" ? Fail("Anyone links are allowed (externalUserAndGuestSharing).", "Limit sharing to new and existing guests or lower.")
                : Pass($"SharePoint sharing: {capability}.");
        }),

        ["microsoft_defender_antivirus_is_deployed_and_managed_through_microsoft_intune"] = ([TenantDatasets.ConfigurationPolicies], s => TemplateFamily(s, "endpointSecurityAntivirus", "Defender Antivirus policy")),
        ["devices_shall_be_enrolled_for_defender_for_business_or_defender_for_endpoint"] = ([TenantDatasets.ConfigurationPolicies], s => TemplateFamily(s, "endpointSecurityEndpointDetectionAndResponse", "EDR onboarding policy")),
        ["firewall_policy_configured_for_defender"] = ([TenantDatasets.ConfigurationPolicies], s => SettingExists(s, "vendor_msft_firewall_mdmstore", "Windows Firewall policy")),
        ["turn_on_tamper_protection"] = ([TenantDatasets.ConfigurationPolicies], s => SettingExists(s, "tamperprotection", "Tamper protection")),
        ["attack_surface_reduction_rules_shall_be_configured"] = ([TenantDatasets.ConfigurationPolicies], s => SettingExists(s, "attacksurfacereductionrules", "Attack surface reduction rules")),
        ["attack_simulations_shall_be_periodically_conducted"] = ([TenantDatasets.AttackSimulations], s =>
        {
            var recent = s.Get(TenantDatasets.AttackSimulations).Items.Count(d => Date(d, "createdDateTime") is { } created && created > s.CollectedAtUtc.AddYears(-1));
            return recent > 0 ? Pass($"{recent} attack simulation(s) in the last year.") : Fail("No attack simulation in the last year.", "Run an attack simulation training campaign at least annually.");
        }),

        ["information_protection_labels_shall_be_configured"] = ([TenantDatasets.SensitivityLabels], s =>
        {
            var count = s.Get(TenantDatasets.SensitivityLabels).Items.Count;
            return count > 0 ? Pass($"{count} sensitivity label(s).") : Fail("No sensitivity labels.", "Create and publish sensitivity labels.");
        }),
        ["retention_policies_shall_be_configured"] = ([TenantDatasets.RetentionLabels], s =>
        {
            var count = s.Get(TenantDatasets.RetentionLabels).Items.Count;
            return count > 0 ? Pass($"{count} retention label(s).") : Partial("No retention labels found (retention policies are not readable through Graph).", "Confirm retention policies for Exchange, SharePoint, OneDrive and Teams in Purview.");
        }),
    };

    public static AssessmentResultItem Evaluate(CatalogControl control, TenantSnapshot snapshot)
    {
        var checkId = control.Attributes.TryGetValue("Check ID", out var id) ? id : control.Id["CHK-".Length..];
        if (!Checks.TryGetValue(checkId, out var check))
        {
            var portal = control.Attributes.GetValueOrDefault("Admin portal");
            var path = control.Attributes.GetValueOrDefault("Click path");
            return Item(control, AssessmentResultValues.Manual, $"Verify manually in {portal}: {path}".Trim(), $"Required: {control.Value}");
        }

        foreach (var name in check.Datasets)
        {
            var dataset = snapshot.Get(name);
            if (!dataset.IsCollected) return NotEvaluated(control, dataset);
        }

        try
        {
            var outcome = check.Check(snapshot);
            return Item(control, outcome.Result, outcome.Evidence, outcome.Result == AssessmentResultValues.Pass ? null : outcome.Recommendation ?? $"Required: {control.Value}");
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or FormatException or JsonException)
        {
            return Item(control, AssessmentResultValues.NotEvaluated, $"The check could not be evaluated: {exception.Message}", null);
        }
    }

    /// <summary>Tenant facts recorded with every assessment (Secure Score, licences, devices).</summary>
    public static IEnumerable<AssessmentResultItem> Informational(TenantSnapshot snapshot)
    {
        if (snapshot.Get(TenantDatasets.SecureScores) is { IsCollected: true, Items.Count: > 0 } scores)
        {
            var score = scores.Items[0];
            var current = Property(score, "currentScore");
            var max = Property(score, "maxScore");
            if (current.ValueKind == JsonValueKind.Number && max.ValueKind == JsonValueKind.Number && max.GetDouble() > 0)
                yield return new AssessmentResultItem("INFO-secure-score", "Microsoft Secure Score", "Microsoft Secure Score", AssessmentResultValues.Info, "Info",
                    $"{current.GetDouble():0.#} of {max.GetDouble():0.#} ({100 * current.GetDouble() / max.GetDouble():0.#}%).", null);
        }

        if (snapshot.Get(TenantDatasets.SecurityDefaults) is { IsCollected: true, Items.Count: > 0 } defaults)
            yield return new AssessmentResultItem("INFO-security-defaults", "Entra ID", "Security defaults", AssessmentResultValues.Info, "Info",
                Property(defaults.Items[0], "isEnabled").ValueKind == JsonValueKind.True ? "Security defaults are on." : "Security defaults are off (expected when Conditional Access is used).", null);

        if (snapshot.Get(TenantDatasets.ManagedDevices) is { IsCollected: true } devices && devices.Items.Count > 0)
        {
            var compliant = devices.Items.Count(d => Text(d, "complianceState") == "compliant");
            yield return new AssessmentResultItem("INFO-device-compliance", "Intune", "Device compliance", AssessmentResultValues.Info, "Info",
                $"{compliant} of {devices.Items.Count} managed devices compliant ({100.0 * compliant / devices.Items.Count:0.#}%).", null);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Conditional Access helpers
    // ------------------------------------------------------------------------------------------------------------

    private static string[] Ca() => [TenantDatasets.ConditionalAccessPolicies];

    private static Outcome CaCheck(TenantSnapshot snapshot, Func<JsonElement, bool> matches, string description)
    {
        var policies = snapshot.Get(TenantDatasets.ConditionalAccessPolicies).Items.Where(matches).ToArray();
        var enabled = policies.FirstOrDefault(policy => Text(policy, "state") == "enabled");
        if (enabled.ValueKind == JsonValueKind.Object) return Pass($"Policy '{Text(enabled, "displayName")}' {description}.");
        var reportOnly = policies.FirstOrDefault(policy => Text(policy, "state") == "enabledForReportingButNotEnforced");
        if (reportOnly.ValueKind == JsonValueKind.Object)
            return Partial($"Policy '{Text(reportOnly, "displayName")}' {description} but is in report-only state.", "Turn the policy on after reviewing report-only results.");
        return policies.Length > 0
            ? Fail($"Policy '{Text(policies[0], "displayName")}' {description} but is off.", "Turn the policy on.")
            : Fail($"No Conditional Access policy {description}.", "Create the policy (report-only first, then on).");
    }

    private static IEnumerable<string> IncludeUsers(JsonElement policy) => Strings(Property(Property(policy, "conditions"), "users"), "includeUsers");
    private static IEnumerable<string> Roles(JsonElement policy) => Strings(Property(Property(policy, "conditions"), "users"), "includeRoles");
    private static IEnumerable<string> Apps(JsonElement policy) => Strings(Property(Property(policy, "conditions"), "applications"), "includeApplications");
    private static IEnumerable<string> UserActions(JsonElement policy) => Strings(Property(Property(policy, "conditions"), "applications"), "includeUserActions");
    private static IEnumerable<string> ClientApps(JsonElement policy) => Strings(Property(policy, "conditions"), "clientAppTypes");
    private static HashSet<string> Platforms(JsonElement policy) => Strings(Property(Property(policy, "conditions"), "platforms"), "includePlatforms").ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string> Grant(JsonElement policy) => Strings(Property(policy, "grantControls"), "builtInControls").ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static string Strength(JsonElement policy) => Text(Property(Property(policy, "grantControls"), "authenticationStrength"), "id");
    private static bool AllUsers(JsonElement policy) => IncludeUsers(policy).Contains("All", StringComparer.OrdinalIgnoreCase);
    private static bool AllApps(JsonElement policy) => Apps(policy).Contains("All", StringComparer.OrdinalIgnoreCase);
    private static bool RequiresMfa(JsonElement policy) => Grant(policy).Contains("mfa") || Strength(policy).Length > 0;

    // ------------------------------------------------------------------------------------------------------------
    // Directory checks
    // ------------------------------------------------------------------------------------------------------------

    private static Outcome MfaRegistration(TenantSnapshot snapshot)
    {
        var members = snapshot.Get(TenantDatasets.RegistrationDetails).Items.Where(user => Text(user, "userType") != "guest").ToArray();
        if (members.Length == 0) return new Outcome(AssessmentResultValues.Info, "No member users returned.");
        var capable = members.Count(user => Property(user, "isMfaCapable").ValueKind == JsonValueKind.True);
        var percent = 100.0 * capable / members.Length;
        var evidence = $"{capable} of {members.Length} members are MFA capable ({percent:0.#}%).";
        return capable == members.Length ? Pass(evidence)
            : percent >= 90 ? Partial(evidence, "Have the remaining users register at aka.ms/mysecurityinfo.")
            : Fail(evidence, "Run an MFA registration campaign and require registration with Conditional Access.");
    }

    private static IEnumerable<JsonElement> Assignments(TenantSnapshot snapshot, Func<string, bool> role) =>
        snapshot.Get(TenantDatasets.RoleAssignments).Items.Where(assignment => role(Text(assignment, "roleDefinitionId")));

    private static Outcome GlobalAdminCount(TenantSnapshot snapshot)
    {
        var principals = Assignments(snapshot, role => role.Equals(GlobalAdministrator, StringComparison.OrdinalIgnoreCase)).Select(a => Text(a, "principalId")).Distinct().Count();
        var evidence = $"{principals} Global Administrator assignment(s).";
        return principals is >= 2 and <= 4 ? Pass(evidence) : Fail(evidence, principals < 2 ? "Designate at least two Global Administrators." : "Reduce Global Administrators to four or fewer; use least-privileged roles.");
    }

    private static Outcome CloudOnlyAdmins(TenantSnapshot snapshot)
    {
        var synced = Assignments(snapshot, PrivilegedRoles.Contains)
            .Select(a => Property(a, "principal"))
            .Where(principal => Property(principal, "onPremisesSyncEnabled").ValueKind == JsonValueKind.True)
            .Select(principal => Text(principal, "userPrincipalName")).Distinct().ToArray();
        return synced.Length == 0 ? Pass("Privileged role holders are cloud-only accounts.")
            : Fail($"{synced.Length} privileged account(s) are synchronized from on-premises: {string.Join(", ", synced.Take(5))}.", "Use cloud-only accounts for privileged roles.");
    }

    private static Outcome PermanentAdmins(TenantSnapshot snapshot)
    {
        var permanent = Assignments(snapshot, PrivilegedRoles.Contains).Select(a => Text(a, "principalId")).Distinct().Count();
        var eligible = snapshot.Get(TenantDatasets.RoleEligibilitySchedules).Items.Count(e => PrivilegedRoles.Contains(Text(e, "roleDefinitionId")));
        var evidence = $"{permanent} permanently assigned and {eligible} eligible privileged role assignment(s).";
        return eligible > 0 && permanent <= 2 ? Pass(evidence)
            : eligible > 0 ? Partial(evidence, "Convert remaining permanent privileged assignments to eligible (PIM), keeping only break-glass accounts permanent.")
            : Fail(evidence, "Use Privileged Identity Management eligible assignments for privileged roles.");
    }

    private static Outcome BreakGlass(TenantSnapshot snapshot)
    {
        var candidates = snapshot.Get(TenantDatasets.Users).Items.Where(user =>
        {
            var text = $"{Text(user, "displayName")} {Text(user, "userPrincipalName")}".ToLowerInvariant();
            return text.Contains("break", StringComparison.Ordinal) || text.Contains("emergency", StringComparison.Ordinal) || text.Contains("bg-", StringComparison.Ordinal);
        }).ToArray();
        return candidates.Length >= 2 ? Pass($"{candidates.Length} likely emergency-access account(s) found by name.")
            : new Outcome(AssessmentResultValues.Manual, $"{candidates.Length} account(s) named like an emergency-access account.", "Confirm two cloud-only emergency-access accounts exist and are excluded from Conditional Access.");
    }

    private static Outcome DormantAccounts(TenantSnapshot snapshot)
    {
        var users = snapshot.Get(TenantDatasets.Users).Items.Where(user => Property(user, "accountEnabled").ValueKind == JsonValueKind.True && Text(user, "userType") == "Member").ToArray();
        if (!users.Any(user => Property(user, "signInActivity").ValueKind == JsonValueKind.Object))
            return new Outcome(AssessmentResultValues.NotEvaluated, "Sign-in activity is not available (needs AuditLog.Read.All and Microsoft Entra ID P1).");
        var cutoff = snapshot.CollectedAtUtc.AddDays(-45);
        var dormant = users.Count(user => Date(Property(user, "signInActivity"), "lastSignInDateTime") is { } last ? last < cutoff : Date(user, "createdDateTime") < cutoff);
        return dormant == 0 ? Pass("No enabled member account is dormant for more than 45 days.")
            : Fail($"{dormant} enabled account(s) have not signed in for more than 45 days.", "Review and disable dormant accounts (access reviews or a lifecycle workflow).");
    }

    private static JsonElement DefaultPermissions(TenantSnapshot snapshot) => Property(First(snapshot, TenantDatasets.AuthorizationPolicy), "defaultUserRolePermissions");

    // ------------------------------------------------------------------------------------------------------------
    // Intune and DNS helpers
    // ------------------------------------------------------------------------------------------------------------

    private static Outcome PersonalEnrollment(TenantSnapshot snapshot)
    {
        var restrictions = snapshot.Get(TenantDatasets.EnrollmentConfigurations).Items
            .Where(c => Text(c, "@odata.type").Contains("PlatformRestriction", StringComparison.OrdinalIgnoreCase)).ToArray();
        var blocked = restrictions.Count(c => Text(c, "@odata.type").Contains("PlatformRestrictionConfiguration", StringComparison.OrdinalIgnoreCase) &&
                                              Property(Property(c, "platformRestriction"), "personalDeviceEnrollmentBlocked").ValueKind == JsonValueKind.True
                                              || ContainsTrue(c, "personalDeviceEnrollmentBlocked"));
        return blocked > 0 ? Pass($"Personal device enrollment is blocked in {blocked} restriction(s).")
            : Fail("Personal devices can enroll.", "Block personally owned devices in the enrollment platform restrictions.");
    }

    private static bool ContainsTrue(JsonElement element, string property) => element.GetRawText().Contains($"\"{property}\":true", StringComparison.OrdinalIgnoreCase);

    private static Outcome CompliancePlatforms(TenantSnapshot snapshot)
    {
        var types = snapshot.Get(TenantDatasets.CompliancePolicies).Items.Select(p => Text(p, "@odata.type").ToLowerInvariant()).ToArray();
        string[] platforms = ["windows10", "ios", "android", "macos"];
        var covered = platforms.Where(platform => types.Any(type => type.Contains(platform, StringComparison.Ordinal))).ToArray();
        var evidence = covered.Length == 0 ? "No compliance policies." : $"Compliance policies for: {string.Join(", ", covered)}.";
        return covered.Length >= 3 ? Pass(evidence)
            : covered.Length > 0 ? Partial(evidence, "Create a compliance policy for every platform in use.")
            : Fail(evidence, "Create compliance policies for Windows, iOS, Android and macOS.");
    }

    private static Outcome Exists(TenantSnapshot snapshot, string dataset, Func<JsonElement, bool> predicate, string what, string recommendation)
    {
        var found = snapshot.Get(dataset).Items.Where(predicate).ToArray();
        return found.Length > 0 ? Pass($"{found.Length} {what}(s): {string.Join(", ", found.Take(3).Select(item => Text(item, "displayName") is { Length: > 0 } name ? name : Text(item, "name")))}.")
            : Fail($"No {what}.", recommendation);
    }

    private static Outcome AnyConfigured(TenantSnapshot snapshot, Func<JsonElement, bool> objectPredicate, string settingFragment, string what)
    {
        var objects = snapshot.Get(TenantDatasets.DeviceConfigurations).Items.Concat(snapshot.Get(TenantDatasets.CompliancePolicies).Items).Where(objectPredicate).ToArray();
        if (objects.Length > 0) return Pass($"{what}: {Text(objects[0], "displayName")}.");
        var setting = SettingExists(snapshot, settingFragment, what);
        return setting;
    }

    private static Outcome SettingExists(TenantSnapshot snapshot, string fragment, string what)
    {
        var policies = snapshot.Get(TenantDatasets.ConfigurationPolicies).Items
            .Where(policy => Property(policy, "settings").GetRawText().Contains(fragment, StringComparison.OrdinalIgnoreCase)).Select(policy => Text(policy, "name")).ToArray();
        return policies.Length > 0 ? Pass($"{what} configured in: {string.Join(", ", policies.Take(3))}.") : Fail($"No {what} found.", $"Configure {what} in Intune.");
    }

    private static Outcome TemplateFamily(TenantSnapshot snapshot, string family, string what) =>
        Exists(snapshot, TenantDatasets.ConfigurationPolicies, policy => Text(Property(policy, "templateReference"), "templateFamily").Equals(family, StringComparison.OrdinalIgnoreCase),
            what, $"Create and assign a {what} in Intune endpoint security.");

    private static Outcome DnsCheck(TenantSnapshot snapshot, string property, string record)
    {
        var domains = snapshot.Get(TenantDatasets.DnsRecords).Items;
        if (domains.Count == 0) return new Outcome(AssessmentResultValues.Info, "No custom domains (only onmicrosoft.com).");
        var missing = domains.Where(domain => Text(domain, property).Length == 0).Select(domain => Text(domain, "domain")).ToArray();
        if (property == "dmarc")
        {
            var monitorOnly = domains.Where(domain => Text(domain, "dmarc").Contains("p=none", StringComparison.OrdinalIgnoreCase)).Select(domain => Text(domain, "domain")).ToArray();
            if (missing.Length == 0 && monitorOnly.Length > 0)
                return Partial($"DMARC is monitor-only (p=none) for: {string.Join(", ", monitorOnly)}.", "Move DMARC to p=quarantine, then p=reject.");
        }

        return missing.Length == 0 ? Pass($"{record} published for {domains.Count} custom domain(s).")
            : Fail($"{record} missing for: {string.Join(", ", missing.Take(10))}.", $"Publish {record} for every custom domain.");
    }

    // ------------------------------------------------------------------------------------------------------------
    // JSON helpers
    // ------------------------------------------------------------------------------------------------------------

    private static JsonElement First(TenantSnapshot snapshot, string dataset) =>
        snapshot.Get(dataset).Items.FirstOrDefault();

    private static bool? Bool(JsonElement element, string name) => Property(element, name).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private static DateTimeOffset? Date(JsonElement element, string name) =>
        Text(element, name) is { Length: > 0 } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : null;
}
