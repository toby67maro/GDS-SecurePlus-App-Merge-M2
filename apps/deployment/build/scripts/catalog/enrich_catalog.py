"""Enrichment rules for the Secure Plus seed catalog (schema version 2).

Adds to every control, deterministically:
  * variantOf / variantLabel   - alternative values of the same policy number (see docs/DECISIONS.md)
  * solutionArea / experienceArea
  * implementation             - estimated minutes, difficulty, effort, risk level, benefits, experience impact
  * remediation                - Graph request, PowerShell, JSON body, Intune/portal path, GPO path and registry
  * generated                  - the list of fields produced by GDS rules ("Generated - needs validation")
and the reference datasets solutionAreas, experienceAreas, specifications and baselines.

Everything produced here is indicative guidance derived from the source data and Microsoft documentation patterns;
the application labels it "Generated - needs validation".
"""
import json
import re

SOLUTION_AREAS = [
    {"code": "IDENTITY", "name": "Identity and Access", "description": "Conditional Access, authentication, identity protection and Entra ID configuration."},
    {"code": "PRIVILEGED", "name": "Privileged Access", "description": "Administrator separation, privileged roles and emergency access."},
    {"code": "ENDPOINT-SECURITY", "name": "Endpoint Security", "description": "Microsoft Defender Antivirus, firewall, attack surface reduction, encryption and EDR."},
    {"code": "DEVICE-MANAGEMENT", "name": "Device Management", "description": "Intune compliance, configuration, restrictions and update rings."},
    {"code": "APP-PROTECTION", "name": "Application Protection", "description": "Intune app protection (MAM) for mobile and BYOD scenarios."},
    {"code": "PRODUCTIVITY-APPS", "name": "Microsoft 365 Apps and Browser", "description": "Microsoft 365 Apps, Edge, OneDrive and Office hardening."},
    {"code": "WINDOWS-HARDENING", "name": "Windows Hardening", "description": "Windows and Windows Server security baselines and local security policy."},
    {"code": "COLLABORATION", "name": "Collaboration", "description": "Exchange Online, Teams, SharePoint and OneDrive service configuration."},
    {"code": "DATA-PROTECTION", "name": "Data Protection and Governance", "description": "Purview, information protection, retention and AI readiness."},
    {"code": "SERVICE-GOVERNANCE", "name": "Service Governance and Operations", "description": "Secure Plus engagement phases, governance and ongoing operations."},
]

EXPERIENCE_AREAS = [
    {"code": "SIGN-IN", "name": "Sign-In and Authentication", "description": "How users and administrators sign in, MFA prompts and blocked sign-ins."},
    {"code": "DEVICE", "name": "Device Experience", "description": "Device enrolment, compliance, restrictions, encryption and performance."},
    {"code": "APPS-BROWSER", "name": "Apps and Browser", "description": "Office, Edge and line-of-business application behaviour."},
    {"code": "DATA-HANDLING", "name": "Data Handling and Sharing", "description": "Copy, paste, save, share and external collaboration restrictions."},
    {"code": "UPDATES", "name": "Updates and Maintenance", "description": "Update rings, restarts and maintenance windows."},
    {"code": "COLLABORATION", "name": "Collaboration and Communication", "description": "Mail, Teams and SharePoint behaviour."},
    {"code": "ADMINISTRATION", "name": "Administration", "description": "Administrator and service-desk workflows."},
    {"code": "TRANSPARENT", "name": "Transparent to Users", "description": "Background protection with no visible change for users."},
]

SOLUTION_RULES = [
    ("PRIVILEGED", r"admin protection|privileged|\bpim\b|break.?glass|emergency access|\blaps\b"),
    ("APP-PROTECTION", r"app protection|\bapp l[123]\b|byod|managed app"),
    ("COLLABORATION", r"exchange|teams|sharepoint|onedrive for business|mailbox|\bmail\b"),
    ("DATA-PROTECTION", r"purview|data protection|sensitivity|retention|\bdlp\b|governance & ai|ai readiness"),
    ("PRODUCTIVITY-APPS", r"microsoft office|office 20|microsoft 365 apps|\bedge\b|onedrive|powerpoint|excel|word|outlook|autoupdate|\bmau\b"),
    ("ENDPOINT-SECURITY", r"defender|antivirus|firewall|attack surface|\basr\b|bitlocker|encryption|endpoint detection|\bedr\b|exploit|tamper|smartscreen|windows security"),
    ("DEVICE-MANAGEMENT", r"compliance|wufb|update|restriction|enrol|enroll|autopilot|configuration - |fully managed|work profile|gatekeeper|intune"),
    ("WINDOWS-HARDENING", r"security template|user rights|audit|local security|device security|windows|credential guard|hvci|smb|rdp|remote desktop"),
    ("IDENTITY", r"conditional access|entra|identity|sign-in|authentication|mfa|guest|password"),
]

EXPERIENCE_RULES = [
    ("ADMINISTRATION", r"admin|privileged|role|break.?glass|emergency access|service desk|laps"),
    ("SIGN-IN", r"sign-in|signin|mfa|multifactor|authentication|password|\bpin\b|hello|conditional access|session|token|legacy auth"),
    ("DATA-HANDLING", r"copy|paste|save|share|sharing|clipboard|print|backup|data transfer|external|download|removable|usb|dlp"),
    ("UPDATES", r"update|wufb|ring|restart|deferral|driver|feature release|quality"),
    ("COLLABORATION", r"exchange|teams|sharepoint|mail|meeting|calendar"),
    ("APPS-BROWSER", r"office|edge|browser|word|excel|powerpoint|outlook|macro|add-in|internet explorer|onedrive|store|app "),
    ("DEVICE", r"device|bitlocker|encrypt|camera|bluetooth|lock|screen|compliance|firewall|restriction|wsl|sandbox|printing"),
]

GRAPH_VERSION = {"ConditionalAccess": "v1.0"}

INTUNE_PATHS = [
    (r"app protection|managed app", "Intune admin center > Apps > Protection > App protection policies"),
    (r"compliance", "Intune admin center > Devices > Compliance > Policies"),
    (r"wufb|update ring|ring \d", "Intune admin center > Devices > Windows updates > Update rings"),
    (r"antivirus", "Intune admin center > Endpoint security > Antivirus"),
    (r"firewall", "Intune admin center > Endpoint security > Firewall"),
    (r"attack surface|asr", "Intune admin center > Endpoint security > Attack surface reduction"),
    (r"bitlocker|encryption|filevault", "Intune admin center > Endpoint security > Disk encryption"),
    (r"endpoint detection|edr", "Intune admin center > Endpoint security > Endpoint detection and response"),
    (r"laps|account protection|hello", "Intune admin center > Endpoint security > Account protection"),
]


def _match(rules, text, default):
    lowered = text.lower()
    for code, pattern in rules:
        if re.search(pattern, lowered):
            return code
    return default


def solution_area(control):
    kind = control["kind"]
    text = f"{control['category']} {control['name']} {control['setting']} {control['platform']}"
    if kind == "Standard":
        lowered = control["platform"].lower() + " " + control["category"].lower()
        if "data protection" in lowered or "governance" in lowered or "ai" in lowered.split():
            return "DATA-PROTECTION"
        return _match(SOLUTION_RULES[:3], text, "SERVICE-GOVERNANCE")
    if kind == "ConditionalAccess":
        return "PRIVILEGED" if re.search(r"admin|privileged", text.lower()) else "IDENTITY"
    if kind == "EndpointSecurity":
        return "ENDPOINT-SECURITY"
    if kind == "MicrosoftBaseline":
        return "PRODUCTIVITY-APPS" if control["platform"] == "Microsoft 365 Apps" else "WINDOWS-HARDENING"
    if kind == "AssessmentCheck":
        category = control["category"].lower()
        if category in ("exchange", "teams", "sharepoint and onedrive"):
            return "COLLABORATION"
        if category in ("purview", "data protection", "compliance"):
            return "DATA-PROTECTION"
        if category in ("intune",):
            return "DEVICE-MANAGEMENT"
        if category in ("defender", "workload protection"):
            return "ENDPOINT-SECURITY"
        return _match(SOLUTION_RULES[:1], text, "IDENTITY")
    return _match(SOLUTION_RULES, text, "DEVICE-MANAGEMENT")


def experience_area(control):
    if control["kind"] == "Standard":
        return "ADMINISTRATION"
    text = f"{control['name']} {control['setting']} {control['category']} {control.get('experience', '')}"
    if control.get("userImpact") in ("", "None", "Low") and control["kind"] in ("EndpointSecurity", "MicrosoftBaseline") \
            and not re.search(r"block|restrict|prevent|disable|deny", text.lower()):
        return "TRANSPARENT"
    return _match(EXPERIENCE_RULES, text, "TRANSPARENT" if control["kind"] == "EndpointSecurity" else "DEVICE")


def _level(value, mapping, default):
    return mapping.get((value or "").strip().lower(), default)


BENEFITS = {
    "IDENTITY": "Reduces the risk of account compromise and unauthorised access to Microsoft 365.",
    "PRIVILEGED": "Protects administrator accounts, the highest-value targets, and limits the blast radius of a compromise.",
    "ENDPOINT-SECURITY": "Prevents, detects and contains malware, ransomware and exploitation on endpoints.",
    "DEVICE-MANAGEMENT": "Keeps devices compliant, patched and configured consistently, so access decisions can trust them.",
    "APP-PROTECTION": "Keeps corporate data inside managed apps on personal and corporate mobile devices.",
    "PRODUCTIVITY-APPS": "Closes common attack paths through documents, macros, add-ins and the browser.",
    "WINDOWS-HARDENING": "Hardens the operating system against credential theft, lateral movement and misuse.",
    "COLLABORATION": "Reduces phishing, data leakage and abuse of mail, Teams and SharePoint.",
    "DATA-PROTECTION": "Classifies, protects and retains data to meet regulatory and AI-readiness requirements.",
    "SERVICE-GOVERNANCE": "Gives the engagement clear ownership, evidence and repeatable operations.",
}


def implementation(control, area):
    kind = control["kind"]
    impact = control.get("userImpact") or ""
    text = f"{control['name']} {control['setting']} {control['value']}".lower()
    minutes = {"ConditionalAccess": 60, "EndpointSecurity": 15, "Intune": 15, "MicrosoftBaseline": 10, "AssessmentCheck": 30, "Standard": 60}.get(kind, 30)
    if kind == "Intune" and re.search(r"app protection|app l[123]", control["category"].lower()):
        minutes = 20
    if kind == "Standard":
        mode = (control.get("attributes") or {}).get("Implementation Mode", "").lower()
        minutes = 60 if "document" in mode else 120 if "configure" in mode or "deploy" in mode else 90
    if kind == "AssessmentCheck":
        steps = len([line for line in (control.get("description") or "").split("\n") if line.strip()])
        minutes = max(15, min(120, steps * 5))
    impact_level = _level(impact, {"high": 3, "medium": 2, "low": 1}, 1)
    blocking = bool(re.search(r"\bblock\b|require compliant|require trusted|deny|disable", text))
    if impact_level == 3 or (kind == "ConditionalAccess" and blocking):
        minutes *= 2
    difficulty = "High" if minutes >= 120 else "Medium" if minutes >= 30 or blocking else "Low"
    effort = "High" if minutes >= 120 else "Medium" if minutes >= 30 else "Low"
    risk = "High" if impact_level == 3 or (kind == "ConditionalAccess" and blocking) else "Medium" if impact_level == 2 or blocking else "Low"
    experience = control.get("experience") or ""
    if not experience:
        experience = "No visible change for users." if risk == "Low" else "Users may notice the change; communicate it before enforcement."
    return {
        "estimatedMinutes": minutes,
        "difficulty": difficulty,
        "effort": effort,
        "riskLevel": risk,
        "benefits": BENEFITS.get(area, BENEFITS["SERVICE-GOVERNANCE"]),
        "experienceImpact": f"{impact or risk} user impact. {experience}".strip(),
    }


def _ps_string(value):
    return "'" + value.replace("'", "''") + "'"


def _graph_scopes(resource):
    if resource.startswith("identity/conditionalAccess"):
        return "Policy.ReadWrite.ConditionalAccess", "Policy.Read.All"
    if resource.startswith("deviceAppManagement"):
        return "DeviceManagementApps.ReadWrite.All", "DeviceManagementApps.Read.All"
    return "DeviceManagementConfiguration.ReadWrite.All", "DeviceManagementConfiguration.Read.All"


def remediation(control):
    kind = control["kind"]
    attributes = control.get("attributes") or {}
    result = {}
    generated = []
    body = control.get("body") or ""
    resource = control.get("graphResource") or ""
    if body and resource:
        version = GRAPH_VERSION.get(kind, "beta")
        uri = f"https://graph.microsoft.com/{version}/{resource}"
        write_scope, read_scope = _graph_scopes(resource)
        result["graphRequest"] = f"POST {uri}\nContent-Type: application/json\n\n(request body: see JSON Body)"
        try:
            pretty = json.dumps(json.loads(body), indent=2, ensure_ascii=False)
        except ValueError:
            pretty = body
        result["jsonBody"] = pretty
        name = control["name"]
        result["graphCommand"] = (
            f"# Microsoft Graph PowerShell SDK\n"
            f"Connect-MgGraph -Scopes {write_scope}\n"
            f"Invoke-MgGraphRequest -Method POST -Uri '{uri}' -Body (Get-Content -Raw '.\\{control['id']}.json') -ContentType 'application/json'")
        state_note = "# The policy is created in report-only state; review sign-in logs before enabling it.\n" if kind == "ConditionalAccess" else ""
        result["powerShell"] = (
            f"#Requires -Version 7\n#Requires -Modules Microsoft.Graph.Authentication\n"
            f"# {control['id']} - {name}\n"
            f"# Replace the <placeholders> (groups, rings, named locations) with tenant values before running.\n"
            f"{state_note}"
            f"Connect-MgGraph -Scopes {write_scope} -NoWelcome\n"
            f"$uri = '{uri}'\n"
            f"$body = Get-Content -Raw -Path '.\\{control['id']}.json'\n"
            f"$existing = Invoke-MgGraphRequest -Method GET -Uri \"$uri`?`$top=999\"\n"
            f"$displayName = ($body | ConvertFrom-Json).displayName ?? ($body | ConvertFrom-Json).name\n"
            f"if ($existing.value | Where-Object {{ ($_.displayName ?? $_.name) -eq $displayName }}) {{\n"
            f"    Write-Warning \"'$displayName' already exists; no change made.\"\n"
            f"}} else {{\n"
            f"    Invoke-MgGraphRequest -Method POST -Uri $uri -Body $body -ContentType 'application/json' | Out-Null\n"
            f"    Write-Host \"Created '$displayName'.\"\n"
            f"}}\n"
            f"# Read-only verification scope: {read_scope}")
        generated += ["graphRequest", "graphCommand", "powerShell"]
        if kind == "ConditionalAccess":
            result["settingPath"] = "Microsoft Entra admin center > Entra ID > Conditional Access > Policies"
        elif kind == "EndpointSecurity":
            result["settingPath"] = f"Intune admin center > Endpoint security > {control['category']}"
        else:
            path = next((path for pattern, path in INTUNE_PATHS if re.search(pattern, control["category"].lower())),
                        "Intune admin center > Devices > Manage devices > Configuration")
            result["settingPath"] = f"{path} > {control['category']}"
        generated.append("settingPath")
    if kind == "MicrosoftBaseline":
        policy_path = attributes.get("Policy Path", "")
        scope = "User Configuration" if "HKCU" in attributes.get("Registry Information", "") else "Computer Configuration"
        if policy_path:
            is_template = "HK" in attributes.get("Registry Information", "")
            prefix = "Administrative Templates > " if is_template else "Windows Settings > Security Settings > "
            result["gpoPath"] = f"{scope} > Policies > {prefix}{policy_path.replace(chr(92), ' > ')} > {control['name']}"
            generated.append("gpoPath")
        registry = attributes.get("Registry Information", "")
        keys = re.findall(r"(HK(?:LM|CU)\\[^!\s]+)!([^\s]+)", registry)
        if keys:
            lines = [f"# {control['id']} - {control['name']} = {control['value']}",
                     "# Registry values set by this baseline setting. Confirm the data type and value in the baseline workbook before use."]
            for hive_path, value_name in keys:
                ps_path = hive_path.replace("HKLM\\", "HKLM:\\").replace("HKCU\\", "HKCU:\\")
                lines.append(f"New-Item -Path {_ps_string(ps_path)} -Force | Out-Null")
                lines.append(f"# Set-ItemProperty -Path {_ps_string(ps_path)} -Name {_ps_string(value_name)} -Value <value for '{control['value']}'>")
            result["registry"] = "\n".join(f"{path}!{name}" for path, name in keys)
            result["powerShell"] = "\n".join(lines)
            generated.append("powerShell")
        result["settingPath"] = "Intune admin center > Devices > Configuration > Create > Settings catalog (search for the setting name)"
        generated.append("settingPath")
    if kind == "AssessmentCheck":
        if attributes.get("Click path"):
            result["settingPath"] = attributes["Click path"]
        if attributes.get("Steps"):
            result["steps"] = attributes["Steps"]
    if kind == "Standard":
        for key, target in (("Configuration or Review Path", "settingPath"), ("Deployment Method", "deploymentMethod"),
                            ("Validation Method", "validation"), ("Rollback Method", "rollback"), ("Evidence Required", "evidence")):
            if attributes.get(key):
                result[target] = attributes[key]
    return result, generated


def mark_variants(controls):
    """Two source files with the same policy number.

    * Same Microsoft Graph body: a duplicate (the file name claims another value but the content is identical). The
      first file wins; the other ID is kept only as an alias so stored results that use it still resolve.
    * Different body: a genuine alternative of the same policy number. It keeps a -2, -3... ID and points back to the
      base control with variantOf / variantLabel.

    Returns (controls, aliases). Re-running on the same sources always produces the same IDs."""
    kept, aliases, seen = [], [], {}
    for control in controls:
        base_id = control["id"]
        if base_id not in seen:
            seen[base_id] = [control]
            kept.append(control)
            continue
        duplicate = next((other for other in seen[base_id] if other.get("body") and other.get("body") == control.get("body")), None)
        alias_id = f"{base_id}-{len(seen[base_id]) + 1}"
        if duplicate is not None:
            aliases.append({"alias": alias_id, "controlId": duplicate["id"], "sourcePath": control["path"],
                            "reason": f"Identical Microsoft Graph body to {duplicate['id']}; the file name claims '{control['value']}' but the content sets "
                                      f"'{duplicate['value']}'."})
            seen[base_id].append(control)
            continue
        control["id"] = alias_id
        control["variantOf"] = base_id
        control["variantLabel"] = control.get("value") or control["name"]
        seen[base_id].append(control)
        kept.append(control)
    ids = [control["id"] for control in kept]
    if len(ids) != len(set(ids)):
        raise SystemExit("Duplicate control IDs remain after variant handling.")
    return kept, aliases


def enrich(controls):
    for control in controls:
        area = solution_area(control)
        control["solutionArea"] = area
        control["experienceArea"] = experience_area(control)
        control["implementation"] = implementation(control, area)
        control["remediation"], generated = remediation(control)
        control["generated"] = ["implementation", "solutionArea", "experienceArea"] + generated
        control.setdefault("variantOf", "")
        control.setdefault("variantLabel", "")


def specifications(policy_sets, controls):
    """Standard (unassigned) specifications seeded with the application: tiers built from the GDS policy sets."""
    def frameworks_of(ids):
        lookup = {control["id"]: control for control in controls}
        labels = set()
        for control_id in ids:
            labels.update(lookup[control_id]["frameworks"]) if control_id in lookup else None
        return sorted(labels)

    def experiences_of(ids):
        lookup = {control["id"]: control for control in controls}
        return sorted({lookup[i]["experienceArea"] for i in ids if i in lookup})

    sets = {item["id"]: item for item in policy_sets}
    tiers = [
        ("SPEC-SP-FOUNDATION", "Secure Plus Foundation", "Identity foundation: emergency access, MFA, legacy authentication and identity protection.",
         ["00-Foundation", "01-Identity-Protection"]),
        ("SPEC-SP-STANDARD", "Secure Plus Standard", "Foundation plus device, privileged access and application protection.",
         ["00-Foundation", "01-Identity-Protection", "02-Device-Protection", "03-Privileged-Access", "04-Application-Protection"]),
        ("SPEC-SP-ADVANCED", "Secure Plus Advanced (Golden Tenant)", "The complete GDS Golden Tenant: identity, devices, privileged access, apps and endpoint security.",
         ["Golden-Tenant"]),
    ]
    result = []
    for code, name, description, set_ids in tiers:
        ids = []
        for set_id in set_ids:
            for control_id in sets.get(set_id, {}).get("controlIds", []):
                if control_id not in ids:
                    ids.append(control_id)
        labels = frameworks_of(ids)
        result.append({
            "code": code, "name": name, "description": description, "policySets": set_ids, "controlIds": ids,
            "experiences": experiences_of(ids), "frameworks": labels,
        })
    return result


def baselines(policy_sets, controls):
    msb = [control["id"] for control in controls if control["kind"] == "MicrosoftBaseline"]
    std = [control["id"] for control in controls if control["kind"] == "Standard"]
    chk = [control["id"] for control in controls if control["kind"] == "AssessmentCheck"]
    return [
        {"code": "BL-GOLDEN-TENANT", "name": "GDS Golden Tenant", "description": "Every GDS policy set: the reference configuration customers are measured against.",
         "isActive": True, "policySets": ["Golden-Tenant"], "controlIds": chk},
        {"code": "BL-MS-SECURITY-BASELINES", "name": "Microsoft Security Baselines", "description": "Windows 11, Windows Server 2025 and Microsoft 365 Apps security baseline settings.",
         "isActive": False, "policySets": [], "controlIds": msb},
        {"code": "BL-SECURE-PLUS-STANDARDS", "name": "Secure Plus Standards", "description": "GDS Secure Plus engagement standards (phases 0-4).",
         "isActive": False, "policySets": [], "controlIds": std},
    ]
