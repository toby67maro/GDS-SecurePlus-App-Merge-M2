using System.Globalization;
using System.Text;
using System.Text.Json;
using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Application.Planning;

namespace EngineeringWorkspace.Desktop.Pages.Catalog;

/// <summary>
/// Builds every remediation option for a control: Graph API (REST), Graph PowerShell, Intune JSON, Settings Catalog /
/// OMA-URI (CSP), Group Policy, Registry (.reg and PowerShell), portal steps, baseline audit/remediation procedures and
/// deployment/rollback. Uses the control's own data and, where the knowledge base knows the setting, its GPO path,
/// registry value, OMA-URI and the CIS / Microsoft baseline procedures. Derived content is marked "Generated".
/// </summary>
public static class RemediationOptions
{
    /// <summary>Setting definition IDs (Intune Settings Catalog) and their configured values found in the control's Graph body.</summary>
    public static IReadOnlyList<(string Definition, string? Value)> SettingDefinitions(string? body)
    {
        var found = new List<(string, string?)>();
        if (string.IsNullOrWhiteSpace(body)) return found;
        try
        {
            using var document = JsonDocument.Parse(body);
            void Walk(JsonElement element)
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.Object:
                        if (element.TryGetProperty("settingDefinitionId", out var id) && id.ValueKind == JsonValueKind.String)
                        {
                            string? value = null;
                            foreach (var name in new[] { "choiceSettingValue", "simpleSettingValue" })
                                if (element.TryGetProperty(name, out var holder) && holder.ValueKind == JsonValueKind.Object && holder.TryGetProperty("value", out var raw))
                                    value = raw.ValueKind == JsonValueKind.String ? raw.GetString() : raw.ToString();
                            found.Add((id.GetString()!, value));
                        }

                        foreach (var property in element.EnumerateObject()) Walk(property.Value);
                        break;
                    case JsonValueKind.Array:
                        foreach (var item in element.EnumerateArray()) Walk(item);
                        break;
                }
            }

            Walk(document.RootElement);
        }
        catch (JsonException)
        {
            // Not JSON: nothing to extract.
        }

        return found.DistinctBy(pair => pair.Item1, StringComparer.OrdinalIgnoreCase).Take(60).ToArray();
    }

    /// <summary>The OMA-URI a Settings Catalog definition ID corresponds to (derived; casing per the CSP documentation).</summary>
    public static string? OmaUriFor(string definition)
    {
        var id = definition.ToLowerInvariant();
        string prefix;
        if (id.StartsWith("device_vendor_msft_", StringComparison.Ordinal)) prefix = "./Device/Vendor/MSFT/";
        else if (id.StartsWith("user_vendor_msft_", StringComparison.Ordinal)) prefix = "./User/Vendor/MSFT/";
        else return null;
        var rest = id[(id.IndexOf("msft_", StringComparison.Ordinal) + 5)..];
        if (rest.Contains('~', StringComparison.Ordinal)) return $"{prefix}{rest.Replace('_', '/')} (ADMX-backed: configure with the ADMX ingestion CSP)";
        if (rest.StartsWith("policy_config_", StringComparison.Ordinal))
        {
            var remainder = rest["policy_config_".Length..];
            var split = remainder.IndexOf('_', StringComparison.Ordinal);
            return split < 0 ? $"{prefix}Policy/Config/{remainder}" : $"{prefix}Policy/Config/{remainder[..split]}/{remainder[(split + 1)..]}";
        }

        return prefix + rest.Replace('_', '/');
    }

    private static string Hive(string? hive) => (hive ?? "HKLM").ToUpperInvariant() switch
    {
        "HKLM" or "HKEY_LOCAL_MACHINE" => "HKEY_LOCAL_MACHINE",
        "HKCU" or "HKEY_CURRENT_USER" => "HKEY_CURRENT_USER",
        "HKU" or "HKEY_USERS" => "HKEY_USERS",
        var other => other
    };

    private static string ShortHive(string? hive) => Hive(hive) switch { "HKEY_LOCAL_MACHINE" => "HKLM", "HKEY_CURRENT_USER" => "HKCU", "HKEY_USERS" => "HKU", var other => other };

    /// <summary>A .reg file and the equivalent PowerShell for a registry-backed setting.</summary>
    public static string RegistryScript(KnowledgeSetting setting, string? value)
    {
        var key = $"{Hive(setting.RegistryHive)}\\{setting.RegistryKey?.Trim('\\')}";
        var type = (setting.RegistryValueType ?? "REG_DWORD").ToUpperInvariant();
        var name = setting.RegistryValueName ?? "(Default)";
        var text = new StringBuilder();
        text.AppendLine("Windows Registry Editor Version 5.00").AppendLine().AppendLine($"[{key}]");
        if (type.Contains("DWORD", StringComparison.Ordinal) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            text.AppendLine($"\"{name}\"=dword:{number:x8}");
        else if (type.Contains("DWORD", StringComparison.Ordinal))
            text.AppendLine($"\"{name}\"=dword:<value>");
        else
            text.AppendLine($"\"{name}\"=\"{value ?? "<value>"}\"");
        var psType = type.Contains("DWORD", StringComparison.Ordinal) ? "DWord" : type.Contains("MULTI", StringComparison.Ordinal) ? "MultiString" : type.Contains("EXPAND", StringComparison.Ordinal) ? "ExpandString" : "String";
        text.AppendLine().AppendLine("# PowerShell (run elevated)")
            .AppendLine($"$path = 'Registry::{key}'")
            .AppendLine("New-Item -Path $path -Force | Out-Null")
            .AppendLine($"Set-ItemProperty -Path $path -Name '{name}' -Value {(psType == "DWord" ? value ?? "<value>" : $"'{value ?? "<value>"}'")} -Type {psType}")
            .AppendLine().AppendLine("# Command prompt")
            .AppendLine($"reg add \"{ShortHive(setting.RegistryHive)}\\{setting.RegistryKey?.Trim('\\')}\" /v \"{name}\" /t {(type.StartsWith("REG_", StringComparison.Ordinal) ? type : "REG_" + type)} /d {value ?? "<value>"} /f");
        return text.ToString();
    }

    /// <summary>Tabs that come from the control itself (no database needed).</summary>
    public static IReadOnlyList<RemediationTab> FromControl(CatalogControl control)
    {
        var tabs = new List<RemediationTab>();
        var remediation = control.Remediation;
        void Tab(string title, string content, string language, bool generated)
        {
            if (!string.IsNullOrWhiteSpace(content)) tabs.Add(new RemediationTab(title, content, language, generated));
        }

        var resource = string.IsNullOrWhiteSpace(control.GraphResource) ? null : control.GraphResource.Trim('/');
        var body = Pretty(control.Body);
        if (resource is not null)
            Tab("Graph API (REST)", $"POST https://graph.microsoft.com/beta/{resource}\nContent-Type: application/json\n\n{body}".TrimEnd(), "http",
                string.IsNullOrEmpty(remediation.GraphRequest));
        Tab("Graph PowerShell", remediation.GraphCommand.Length > 0
            ? remediation.GraphCommand
            : resource is null ? string.Empty
                : $"# Microsoft Graph PowerShell SDK\nConnect-MgGraph -Scopes 'DeviceManagementConfiguration.ReadWrite.All','Policy.ReadWrite.ConditionalAccess'\n" +
                  $"$body = Get-Content -Raw -Path '.\\{Safe(control.Id)}.json'\n" +
                  $"Invoke-MgGraphRequest -Method POST -Uri 'https://graph.microsoft.com/beta/{resource}' -Body $body -ContentType 'application/json'",
            "powershell", remediation.GraphCommand.Length == 0);
        Tab("PowerShell Script", remediation.PowerShell, "powershell", false);
        Tab("JSON (Intune / Graph import file)", remediation.JsonBody.Length > 0 ? remediation.JsonBody : body, "json", false);

        var definitions = SettingDefinitions(control.Body);
        if (definitions.Count > 0)
            Tab("Settings Catalog / OMA-URI (CSP)", string.Join("\n\n", definitions.Select(pair =>
                $"Setting definition: {pair.Definition}\n" + (pair.Value is null ? string.Empty : $"Value: {pair.Value}\n") +
                (OmaUriFor(pair.Definition) is { } uri ? $"OMA-URI (derived): {uri}" : "OMA-URI: not a policy CSP setting (Settings Catalog only)"))), "text", true);
        Tab("Group Policy (GPO)", remediation.GpoPath, "text", false);
        if (remediation.Registry.Length > 0) Tab("Registry", remediation.Registry, "text", false);
        Tab("Portal Steps", string.Join("\n\n", new[] { remediation.SettingPath, remediation.Steps }.Where(text => text.Length > 0)), "text", false);
        Tab("Deployment and Rollback", string.Join("\n\n", new[]
        {
            remediation.DeploymentMethod.Length > 0 ? $"Deployment method: {remediation.DeploymentMethod}" : string.Empty,
            remediation.Validation.Length > 0 ? $"Validation: {remediation.Validation}" : string.Empty,
            remediation.Evidence.Length > 0 ? $"Evidence: {remediation.Evidence}" : string.Empty,
            remediation.Rollback.Length > 0 ? $"Rollback: {remediation.Rollback}" : string.Empty
        }.Where(text => text.Length > 0)), "text", false);
        return tabs;
    }

    /// <summary>Keys to look up in the knowledge base: Settings Catalog definitions, OMA-URIs, registry and GPO paths.</summary>
    public static IReadOnlyCollection<string> KnowledgeKeys(CatalogControl control)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (definition, _) in SettingDefinitions(control.Body))
        {
            keys.Add(definition);
            if (OmaUriFor(definition) is { } uri && !uri.Contains(' ', StringComparison.Ordinal)) keys.Add(uri);
        }

        if (control.Remediation.GpoPath.Length > 0) keys.Add(control.Remediation.GpoPath);
        if (control.Remediation.Registry.Length > 0) keys.Add(control.Remediation.Registry);
        if (control.Setting.Length > 0) keys.Add(control.Setting);
        return keys;
    }

    /// <summary>Tabs from the knowledge base settings the control configures.</summary>
    public static IReadOnlyList<RemediationTab> FromKnowledge(CatalogControl control, IReadOnlyList<KnowledgeSetting> settings)
    {
        if (settings.Count == 0) return [];
        var values = SettingDefinitions(control.Body).ToDictionary(pair => pair.Definition, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var tabs = new List<RemediationTab>();
        var gpo = settings.Where(setting => !string.IsNullOrWhiteSpace(setting.GpoPath))
            .Select(setting => $"{setting.Name}\nGroup Policy: {setting.GpoPath}" + (setting.RecommendedValues.Count > 0 ? $"\nBaselines: {string.Join("; ", setting.RecommendedValues)}" : string.Empty))
            .ToArray();
        if (gpo.Length > 0) tabs.Add(new RemediationTab("Group Policy (knowledge base)", string.Join("\n\n", gpo), "text", false));
        var registry = settings.Where(setting => !string.IsNullOrWhiteSpace(setting.RegistryKey)).Select(setting =>
        {
            var value = (setting.IntuneDefinitionId is { } id && values.TryGetValue(id, out var configured) ? Numeric(configured) : null)
                        ?? setting.RecommendedValues.Select(text => Numeric(text[(text.LastIndexOf(':') + 1)..].Trim())).FirstOrDefault(number => number is not null);
            return $"; {setting.Name}\n{RegistryScript(setting, value)}";
        }).ToArray();
        if (registry.Length > 0) tabs.Add(new RemediationTab("Registry (.reg and PowerShell)", string.Join("\n\n", registry), "powershell", true));
        var csp = settings.Where(setting => !string.IsNullOrWhiteSpace(setting.OmaUri) || !string.IsNullOrWhiteSpace(setting.IntuneDefinitionId))
            .Select(setting => $"{setting.Name}\n" + (setting.OmaUri is null ? string.Empty : $"OMA-URI: {setting.OmaUri}\n") +
                               (setting.IntuneDefinitionId is null ? string.Empty : $"Settings Catalog: {setting.IntuneDefinitionId}")).ToArray();
        if (csp.Length > 0) tabs.Add(new RemediationTab("OMA-URI (knowledge base)", string.Join("\n\n", csp), "text", false));
        var cmdlets = settings.Where(setting => !string.IsNullOrWhiteSpace(setting.PowerShellCmdlet)).Select(setting => $"# {setting.Name}\n{setting.PowerShellCmdlet}").ToArray();
        if (cmdlets.Length > 0) tabs.Add(new RemediationTab("PowerShell Cmdlets (knowledge base)", string.Join("\n\n", cmdlets), "powershell", false));
        var procedures = settings.SelectMany(setting => setting.Procedures).ToArray();
        if (procedures.Length > 0)
            tabs.Add(new RemediationTab("Baseline Procedures (CIS / Microsoft)", string.Join("\n\n", procedures.Select(procedure =>
                $"{procedure.Recommendation}\n{procedure.Type} ({procedure.Method}): {procedure.Text}" + (procedure.Script is { Length: > 0 } script ? $"\n{script}" : string.Empty))),
                "text", false));
        return tabs;
    }

    private static string? Numeric(string? text) => int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
        ? number.ToString(CultureInfo.InvariantCulture)
        : text?.Trim().ToLowerInvariant() switch { "enabled" or "true" => "1", "disabled" or "false" => "0", _ => null };

    private static string Pretty(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using var document = JsonDocument.Parse(body);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private static string Safe(string id) => string.Concat(id.Split(Path.GetInvalidFileNameChars()));
}
