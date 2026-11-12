using System.Text.Json;
using EngineeringWorkspace.Domain.Customers;

namespace EngineeringWorkspace.Application.Experience;

/// <summary>
/// Imports the customer configuration produced from the GDS EaaS forms (Documents/EaaS/Questionnaire:
/// GDS_EaaS_Customer_Configuration.schema.json, written by Convert-EaaSFormResults.py from Form 1 Discovery and Form 2
/// Design Workshop). Personas become user groups (with their experience per persona), framework targets become the
/// customer's compliance targets, and the workshop answers become All Users experience answers. Nothing is sent anywhere.
/// </summary>
public static class EaasFormImport
{
    private static readonly Dictionary<string, (string Name, string Classification)> Personas = new(StringComparer.OrdinalIgnoreCase)
    {
        ["exec"] = ("Executives and Assistants", "Executive"), ["office"] = ("Office and Knowledge Workers", "KnowledgeWorker"),
        ["finance"] = ("Finance and Accounting", "RegulatedData"), ["hr"] = ("HR and Payroll", "RegulatedData"), ["sales_field"] = ("Sales and Field Staff", "Sales"),
        ["frontline"] = ("Frontline and Shift Workers", "FrontlineWorker"), ["it_admin"] = ("IT Administrators", "PrivilegedAdmin"),
        ["developers"] = ("Developers and Engineers", "Developer"), ["travel"] = ("International Travelers", "Traveler"),
        ["contractors"] = ("Contractors and Temporary Staff", "Contractor"), ["shared"] = ("Shared and Kiosk Accounts", "SharedDevice"),
    };

    private static readonly Dictionary<string, string> Frameworks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cis"] = "CISIG1", ["nist_80053"] = "NIST53M", ["nist_800171"] = "CMMC2", ["cmmc"] = "CMMC2", ["hipaa"] = "HIPAA", ["pci"] = "PCI4", ["iso27001"] = "ISO27001",
        ["sox"] = "SOX", ["itar"] = "ITAR", ["glba"] = "GLBA", ["ftc"] = "GLBA", ["nist_csf"] = "NISTCSF2", ["cis_m365"] = "CISM365L1",
    };

    public sealed record ImportResult(CustomerProfile Profile, IReadOnlyList<string> Notes);

    public static ImportResult Import(CustomerProfile customer, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var notes = new List<string>();
        string? Text(params string[] path)
        {
            var element = root;
            foreach (var name in path)
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element)) return null;
            return element.ValueKind switch { JsonValueKind.String => element.GetString(), JsonValueKind.Number => element.GetRawText(), JsonValueKind.True => "true", JsonValueKind.False => "false", _ => null };
        }

        IReadOnlyList<string> List(params string[] path)
        {
            var element = root;
            foreach (var name in path)
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element)) return [];
            return element.ValueKind == JsonValueKind.Array ? element.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray() : [];
        }

        // All Users answers from the workshop.
        var answers = new Dictionary<string, string>(customer.DefaultExperienceAnswers, StringComparer.OrdinalIgnoreCase);
        void Set(string question, string? option, string why)
        {
            if (option is null) return;
            answers[question] = option;
            notes.Add($"All Users – {ExperienceCatalog.Question(question)?.Topic ?? question}: {ExperienceCatalog.Question(question)?.Option(option).Label} ({why}).");
        }

        var methods = List("auth", "allowedMethods");
        Set("ID-MFA-METHOD", methods.Count == 0 ? null
            : methods.Contains("sms") || methods.Contains("voice") ? "SMS"
            : methods.Any(method => method is "fido2" or "certificate") && !methods.Contains("authenticator") ? "PHISHRESISTANT"
            : methods.Any(method => method is "whfb" or "passkey_app") || Text("auth", "passwordless") is "yes" or "pilot" ? "PASSWORDLESS" : "APP", "allowed MFA methods");
        Set("ID-SIGNIN-FREQUENCY", Text("session", "unmanagedFrequency") switch
        {
            "every" or "always" or "1h" or "4h" => "EVERY", "1d" or "12h" or "8h" => "WORKDAY", "7d" => "WEEK", "default" or "90d" => "RISK", _ => null
        }, "session frequency");
        Set("ID-LOCATIONS", Text("network", "countryBlock") switch
        {
            "none" or "no" => "ANYWHERE", "block_high_risk" or "block_listed" => "BLOCKRISKY", "allow_listed" or "allow_only" => "ALLOWLIST", _ => null
        }, "country blocking");
        Set("ID-SSPR", Text("auth", "sspr") switch { "yes" or "planned" => "SELF", "no" => "HELPDESK", _ => null }, "self-service password reset");
        Set("ID-GUESTS", Text("guests", "inviters") switch { "anyone" or "everyone" => "OPEN", "members" => "INVITED", "admins" or "approval" => "APPROVED", "none" => "NONE", _ => null },
            "who can invite guests");
        Set("DEV-PERSONAL", Text("byod", "model") switch
        {
            "full" => "FULL", "apps_only" or "app_protection" => "APPPROTECT", "web_only" or "browser" => "WEBONLY", "none" or "corp_only" => "MANAGEDONLY", _ => null
        }, "personal devices");
        Set("DEV-ENCRYPTION", Text("windows", "bitlocker") switch { "silent" or "yes" => "SILENT", "pin" => "PIN", "no" => "NONE", _ => null }, "BitLocker");
        Set("DEV-LOCALADMIN", Text("devices", "localAdminTarget") switch { "all" or "keep" => "ADMIN", "add_only" or "epm" => "EPM", "none" => "LOCKED", _ => null }, "local admin target");
        Set("DEV-USB", Text("windows", "removableStorage") switch { "allow" => "ALLOW", "read_only" => "READONLY", "block_untrusted" or "approved" => "APPROVED", "block" => "BLOCK", _ => null },
            "removable storage");
        Set("DEV-LOCK", Text("minimums", "windows", "lockMinutes") switch { "5" => "5MIN", "10" or "15" => "15MIN", _ => null }, "screen lock minutes");
        Set("APPS-MACROS", Text("windows", "macros") switch { "allow" => "ALLOW", "block_internet" => "INTERNET", "block" or "signed" => "SIGNED", _ => null }, "Office macros");
        Set("FILES-SHARING", Text("data", "externalSharing") switch
        {
            "anyone" => "ANYONE", "new_and_existing" or "guests" => "GUESTS", "existing" => "EXISTING", "none" or "internal" => "INTERNAL", _ => null
        }, "external sharing");
        Set("DATA-LABELS", Text("data", "labels") switch { "no" => "NONE", "want" or "yes" => "DEFAULT", "mandatory" => "MANDATORY", _ => null }, "sensitivity labels");
        Set("AI-COPILOT", Text("windows", "copilot") switch { "m365_copilot" => "PILOT", "none" or "block" => "OFF", _ => null }, "Copilot");
        if (Text("tasks", "createBreakGlass") == "true" || Text("identity", "breakGlass") == "no") Set("ADMIN-BREAKGLASS", "YES", "create emergency access accounts");

        // Personas become user groups (existing groups with the same name are updated, not duplicated).
        var groups = customer.Groups.ToList();
        var userCount = int.TryParse(Text("customer", "userCount"), out var count) ? count : (int?)null;
        var personaDetails = root.TryGetProperty("experience", out var experience) && experience.TryGetProperty("personas", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(item => item.TryGetProperty("persona", out _)).ToDictionary(item => item.GetProperty("persona").GetString() ?? string.Empty, item => item,
                StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in List("personas", "selected").Where(Personas.ContainsKey))
        {
            if (groups.Count >= CustomerProfile.MaxUserGroups)
            {
                notes.Add($"Skipped persona '{code}': the customer already has {CustomerProfile.MaxUserGroups} groups.");
                continue;
            }

            var (name, classification) = Personas[code];
            var groupAnswers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (personaDetails.TryGetValue(code, out var detail))
            {
                string? Field(string field) => detail.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (Field("prompts") is { } prompts)
                    groupAnswers["ID-SIGNIN-FREQUENCY"] = prompts switch { "every_app" => "EVERY", "daily" => "WORKDAY", "weekly" => "WEEK", _ => "RISK" };
                if (Field("offsite") is { } offsite)
                    groupAnswers["DEV-PERSONAL"] = offsite switch { "full" => "APPPROTECT", "compliant_only" => "MANAGEDONLY", "web_only" => "WEBONLY", "none" => "MANAGEDONLY", _ => "APPPROTECT" };
                if (Field("data") is "regulated") groupAnswers["DATA-DLP"] = "BLOCK";
                if (Field("travel") is "often") classification = "Traveler";
                if (Field("device") is "byod" && !groupAnswers.ContainsKey("DEV-PERSONAL")) groupAnswers["DEV-PERSONAL"] = "WEBONLY";
            }

            var key = code.Replace('_', '-');
            var existing = groups.FindIndex(group => string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase) || group.Key == key);
            var group = new UserGroup(key, name, classification, $"Imported from the EaaS forms (persona '{code}').", null,
                ExperienceCatalog.Classification(classification).DefaultProfile, groupAnswers);
            if (existing >= 0) groups[existing] = group with { Key = groups[existing].Key, UserCount = groups[existing].UserCount };
            else groups.Add(group);
            notes.Add($"User group '{name}' ({ExperienceCatalog.Classification(classification).Name}) with {groupAnswers.Count} persona-specific choice(s).");
        }

        // Compliance targets.
        var targets = List("frameworks", "targets").Concat(List("frameworks", "confirmed")).Select(code => Frameworks.GetValueOrDefault(code)).OfType<string>().Distinct().ToArray();
        var settings = new Dictionary<string, string>(customer.WorkspaceSettings, StringComparer.OrdinalIgnoreCase);
        if (targets.Length > 0)
        {
            var current = (settings.GetValueOrDefault("Experience.Frameworks") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            settings["Experience.Frameworks"] = string.Join(",", current.Concat(targets).Distinct(StringComparer.OrdinalIgnoreCase));
            notes.Add($"Compliance targets: {string.Join(", ", targets.Select(code => ExperienceCatalog.Framework(code)?.Name ?? code))}.");
        }

        var unmapped = List("frameworks", "targets").Where(code => !Frameworks.ContainsKey(code)).ToArray();
        if (unmapped.Length > 0) notes.Add($"Targets without an experience mapping yet (kept in the form only): {string.Join(", ", unmapped)}.");
        if (userCount is not null) notes.Add($"The forms report {userCount:N0} users in total.");
        return new ImportResult(customer with { ExperienceAnswers = answers, UserGroups = groups, WorkspaceSettings = settings }, notes);
    }
}
