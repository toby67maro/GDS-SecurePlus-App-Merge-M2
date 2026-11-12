#!/usr/bin/env python3
"""Generates the Secure Plus control catalog embedded in the application.

Sources (the GDS Documents repository, https://github.com/toby67maro/Documents):
  * ModernWork_SecurePlus_Baselines/PolicyRepository  - Conditional Access, Endpoint Security and Intune controls
    (one Microsoft Graph request body per control)
  * ModernWork_SecurePlus_Baselines/PolicySets         - baselines (policy sets) composed of those controls
  * GDS Baseline Settings Map - WIP - TB.xlsx          - Cloud Capsule assessment checks with portal paths
  * SecurePlusStandards.xlsx (Standards_Baseline_WIP)  - Secure Plus engagement standards
  * Frameworks/Microsoft Security Baselines            - Windows 11, Windows Server 2025 and Microsoft 365 Apps baselines

Usage:
  python3 build/scripts/catalog/generate_catalog.py --documents ../Documents \
      --output src/EngineeringWorkspace.Infrastructure/Catalog/catalog.json.gz

The output is deterministic for a given source revision. Framework references, user impact, experience and pain
points of policy controls are derived by documented keyword rules (marked as such in the catalog); they are
indicative guidance, not an authoritative crosswalk.
"""
import argparse
import glob
import gzip
import json
import os
import re
import subprocess
from datetime import datetime, timezone

import openpyxl

# Bump when the seed content or its rules change; the application upserts only what changed.
SEED_VERSION = "4.0.0"

from enrich_catalog import EXPERIENCE_AREAS, SOLUTION_AREAS, baselines, enrich, mark_variants, specifications

# ----------------------------------------------------------------------------------------------------------------
# Indicative framework crosswalk by keyword (first match order does not matter; all matching rows are applied).
# ----------------------------------------------------------------------------------------------------------------
CROSSWALK = [
    (r"\bmfa\b|multifactor|multi-factor|strong auth|authentication strength|phishing.?resistant|passwordless|hello for business",
     ["NIST 800-53 IA-2(1)", "NIST 800-53 IA-2(2)", "CIS v8.1 6.3", "CIS v8.1 6.4", "CIS v8.1 6.5", "ISO 27001:2022 A.8.5", "CMMC 2.0 IA.L2-3.5.3", "PCI DSS 4.0.1 8.4", "HIPAA 164.312(d)"]),
    (r"compliant device|device compliance|compliance polic|require.*compliant|hybrid|domain joined|trusted device",
     ["NIST 800-53 AC-19", "NIST 800-53 CM-6", "CIS v8.1 4.1", "ISO 27001:2022 A.8.1", "CMMC 2.0 AC.L2-3.1.18"]),
    (r"legacy auth|active ?sync|other clients|device code|authentication flow",
     ["NIST 800-53 AC-17", "NIST 800-53 IA-2", "CIS v8.1 4.8", "ISO 27001:2022 A.8.5"]),
    (r"sign-in risk|user risk|risky|identity protection|insider risk",
     ["NIST 800-53 SI-4", "NIST 800-53 IA-2", "CIS v8.1 13.1", "ISO 27001:2022 A.8.16"]),
    (r"admin|privileged|break.?glass|emergency access|\brole",
     ["NIST 800-53 AC-6(5)", "NIST 800-53 AC-2(7)", "CIS v8.1 5.4", "CIS v8.1 6.5", "ISO 27001:2022 A.8.2", "CMMC 2.0 AC.L2-3.1.5"]),
    (r"bitlocker|encrypt|filevault|storage encryption",
     ["NIST 800-53 SC-28", "CIS v8.1 3.6", "CIS v8.1 3.11", "ISO 27001:2022 A.8.24", "CMMC 2.0 SC.L2-3.13.16", "HIPAA 164.312(a)(2)(iv)", "PCI DSS 4.0.1 3.5"]),
    (r"antivirus|defender|malware|cloud protection|real.?time|scan|signature|security intelligence|tamper",
     ["NIST 800-53 SI-3", "CIS v8.1 10.1", "CIS v8.1 10.2", "ISO 27001:2022 A.8.7", "CMMC 2.0 SI.L1-3.14.2", "PCI DSS 4.0.1 5.2"]),
    (r"firewall",
     ["NIST 800-53 SC-7", "CIS v8.1 4.4", "CIS v8.1 4.5", "ISO 27001:2022 A.8.20", "CMMC 2.0 SC.L1-3.13.1", "PCI DSS 4.0.1 1.2"]),
    (r"attack surface|\basr\b|exploit|credential guard|lsa|macro|script",
     ["NIST 800-53 CM-7", "NIST 800-53 SI-3", "CIS v8.1 10.5", "ISO 27001:2022 A.8.7", "MITRE ATT&CK M1040"]),
    (r"update|wufb|ring|patch|feature release|quality",
     ["NIST 800-53 SI-2", "CIS v8.1 7.3", "CIS v8.1 7.4", "ISO 27001:2022 A.8.8", "CMMC 2.0 SI.L1-3.14.1", "PCI DSS 4.0.1 6.3.3"]),
    (r"app protection|data protection|copy|paste|cut|save as|backup|clipboard|print|managed app|data transfer|open-in|sharing",
     ["NIST 800-53 AC-4", "NIST 800-53 SC-8", "CIS v8.1 3.3", "ISO 27001:2022 A.8.12", "CMMC 2.0 AC.L2-3.1.3"]),
    (r"audit|logging|\blog\b|telemetry|diagnostic",
     ["NIST 800-53 AU-2", "NIST 800-53 AU-12", "CIS v8.1 8.2", "ISO 27001:2022 A.8.15", "CMMC 2.0 AU.L2-3.3.1", "PCI DSS 4.0.1 10.2"]),
    (r"password|\bpin\b|passcode|lockout|credential",
     ["NIST 800-53 IA-5", "CIS v8.1 5.2", "ISO 27001:2022 A.5.17", "CMMC 2.0 IA.L2-3.5.7"]),
    (r"sign-in frequency|session|persistent browser|idle|inactivity|screen lock|lock screen|screensaver|timeout",
     ["NIST 800-53 AC-11", "NIST 800-53 AC-12", "CIS v8.1 4.3", "CMMC 2.0 AC.L2-3.1.10"]),
    (r"terms of use|\btou\b|banner|logon message",
     ["NIST 800-53 AC-8", "ISO 27001:2022 A.5.10"]),
    (r"guest|external|b2b|anonymous",
     ["NIST 800-53 AC-2", "NIST 800-53 AC-21", "CIS v8.1 6.1", "ISO 27001:2022 A.5.19"]),
    (r"usb|removable|storage card|external storage",
     ["NIST 800-53 MP-7", "CIS v8.1 10.3", "CMMC 2.0 MP.L2-3.8.7"]),
    (r"smartscreen|phishing|web threat|safe links|browser|edge",
     ["NIST 800-53 SC-7", "NIST 800-53 SI-3", "CIS v8.1 9.2", "CIS v8.1 9.3", "ISO 27001:2022 A.8.23"]),
    (r"endpoint detection|\bedr\b|sample sharing",
     ["NIST 800-53 SI-4", "CIS v8.1 13.2", "ISO 27001:2022 A.8.16"]),
    (r"location|country|named location|trusted ip",
     ["NIST 800-53 AC-17", "NIST 800-53 AC-2(12)"]),
]


def frameworks_for(text):
    found = []
    lower = text.lower()
    for pattern, refs in CROSSWALK:
        if re.search(pattern, lower):
            for ref in refs:
                if ref not in found:
                    found.append(ref)
    return found


# ----------------------------------------------------------------------------------------------------------------
# Helpers
# ----------------------------------------------------------------------------------------------------------------
RING = re.compile(r"<RING>|RING")
NUMBER = re.compile(r"^(\d+)-")


def clean_name(name):
    name = (name or "").replace("<RING>-", "").replace("-RING-", "-")
    return re.sub(r"\s+", " ", name).strip()


def strip_number(name):
    return NUMBER.sub("", name, count=1).strip()


def flatten_setting(instance, out):
    """Flattens a settings catalog setting instance into (settingDefinitionId, value) leaves."""
    if not isinstance(instance, dict):
        return
    definition = instance.get("settingDefinitionId")
    choice = instance.get("choiceSettingValue")
    simple = instance.get("simpleSettingValue")
    group = instance.get("groupSettingCollectionValue") or instance.get("groupSettingValue")
    simple_collection = instance.get("simpleSettingCollectionValue")
    choice_collection = instance.get("choiceSettingCollectionValue")
    if choice:
        out.append({"path": definition, "value": choice.get("value")})
        for child in choice.get("children") or []:
            flatten_setting(child, out)
    elif simple is not None:
        out.append({"path": definition, "value": simple.get("value")})
    elif simple_collection:
        values = sorted(str(item.get("value")) for item in simple_collection)
        out.append({"path": definition, "value": values})
    elif choice_collection:
        for item in choice_collection:
            out.append({"path": definition, "value": item.get("value")})
            for child in item.get("children") or []:
                flatten_setting(child, out)
    elif group:
        items = group if isinstance(group, list) else [group]
        for item in items:
            for child in item.get("children") or []:
                flatten_setting(child, out)


IGNORED_PROPERTIES = {"@odata.type", "displayName", "description", "roleScopeTagIds", "scheduledActionsForRule", "id",
                      "createdDateTime", "lastModifiedDateTime", "version", "assignments", "apps", "targetedAppManagementLevels"}


def object_expectations(body):
    return [{"path": key, "value": value} for key, value in body.items() if key not in IGNORED_PROPERTIES and not key.endswith("@odata.type")]


def ca_summary(body):
    conditions = body.get("conditions") or {}
    users = conditions.get("users") or {}
    apps = conditions.get("applications") or {}
    grant = body.get("grantControls") or {}
    parts = []
    who = users.get("includeUsers") or []
    if users.get("includeRoles"):
        who = who + [f"{len(users['includeRoles'])} admin role(s)"]
    if users.get("includeGuestsOrExternalUsers"):
        who = who + ["guests/external users"]
    parts.append("Users: " + (", ".join(who) or "selected groups"))
    targets = (apps.get("includeApplications") or []) + (apps.get("includeUserActions") or [])
    if apps.get("includeAuthenticationContextClassReferences"):
        targets += ["authentication context"]
    parts.append("Target: " + (", ".join(targets) or "selected apps"))
    if conditions.get("clientAppTypes") and conditions["clientAppTypes"] != ["all"]:
        parts.append("Client apps: " + ", ".join(conditions["clientAppTypes"]))
    if conditions.get("signInRiskLevels"):
        parts.append("Sign-in risk: " + ", ".join(conditions["signInRiskLevels"]))
    if conditions.get("userRiskLevels"):
        parts.append("User risk: " + ", ".join(conditions["userRiskLevels"]))
    platforms = (conditions.get("platforms") or {}).get("includePlatforms")
    if platforms:
        parts.append("Platforms: " + ", ".join(platforms))
    controls = grant.get("builtInControls") or []
    if grant.get("authenticationStrength"):
        controls = controls + ["authentication strength"]
    if grant.get("termsOfUse"):
        controls = controls + ["terms of use"]
    if controls:
        parts.append("Grant: " + f" {grant.get('operator') or 'OR'} ".join(controls))
    session = [key for key, value in (body.get("sessionControls") or {}).items() if value]
    if session:
        parts.append("Session: " + ", ".join(session))
    return "; ".join(parts)


def ca_guidance(body):
    grant = (body.get("grantControls") or {})
    controls = set(grant.get("builtInControls") or [])
    session = body.get("sessionControls") or {}
    conditions = body.get("conditions") or {}
    experience, pains, impact = [], [], "Low"

    def raise_impact(level):
        nonlocal impact
        order = ["Low", "Medium", "High"]
        if order.index(level) > order.index(impact):
            impact = level

    if "block" in controls:
        experience.append("Sign-ins that match the conditions are blocked.")
        pains.append("Users or apps outside the allowed conditions lose access; review exclusions and report-only sign-in logs before enforcing.")
        raise_impact("High")
    if "mfa" in controls or grant.get("authenticationStrength"):
        experience.append("Users complete multifactor authentication when the policy applies.")
        pains.append("Users without registered methods are sent to register; keep emergency-access accounts excluded.")
        raise_impact("Medium")
    if grant.get("authenticationStrength"):
        pains.append("An authentication strength may require phishing-resistant methods (FIDO2, Windows Hello, certificate) that users must enrol first.")
    if {"compliantDevice", "domainJoinedDevice"} & controls:
        experience.append("Access requires an Intune-compliant or hybrid-joined device.")
        pains.append("Unenrolled, personal or non-compliant devices lose access; enrol devices and assign compliance policies first.")
        raise_impact("High" if grant.get("operator") == "AND" or len(controls) == 1 else "Medium")
    if {"approvedApplication", "compliantApplication"} & controls:
        experience.append("Only approved client apps or apps with an app protection policy can connect.")
        pains.append("Users of native mail and third-party apps must switch to Outlook and other Microsoft apps.")
        raise_impact("Medium")
    if "passwordChange" in controls:
        experience.append("Users must change their password.")
        pains.append("Requires self-service password reset and password writeback for synchronized users.")
        raise_impact("Medium")
    if grant.get("termsOfUse"):
        experience.append("Users accept terms of use.")
    if session.get("signInFrequency"):
        experience.append("Users sign in again more often.")
        pains.append("More frequent sign-in prompts; tune the frequency for privileged and unmanaged scenarios.")
        raise_impact("Medium")
    if session.get("persistentBrowser"):
        experience.append("Browser sessions are not kept after the browser closes.")
    if session.get("applicationEnforcedRestrictions") or session.get("cloudAppSecurity"):
        experience.append("Limited web access (no download, print or sync) on unmanaged devices.")
        pains.append("Users on unmanaged devices cannot download or sync files.")
        raise_impact("Medium")
    if conditions.get("signInRiskLevels") or conditions.get("userRiskLevels"):
        pains.append("Requires Microsoft Entra ID P2 licences for risk-based conditions.")
    if body.get("state") == "enabledForReportingButNotEnforced":
        pains.append("Shipped in report-only state: review sign-in logs before turning it on.")
    if not experience:
        experience.append("No visible change for users who meet the conditions.")
    return impact, " ".join(experience), " ".join(pains) or "None identified."


def setting_guidance(text, description):
    lower = text.lower()
    poc = "poc" in (description or "").lower()
    experience, pains, impact = [], [], "Low"
    if any(word in lower for word in ["antivirus", "defender", "behavior monitoring", "cloud protection", "real-time", "scan", "signature", "security intelligence", "tamper"]):
        experience.append("Microsoft Defender protection runs in the background.")
        pains.append("Scans and cloud lookups can briefly affect performance on low-end devices.")
    if "attack surface" in lower or "asr" in lower:
        experience.append("Risky behaviours such as Office child processes, obfuscated scripts or credential theft are blocked.")
        pains.append("Line-of-business apps, macros or scripts can be blocked; run in audit mode and add exclusions first.")
        impact = "Medium"
    if "firewall" in lower:
        experience.append("Network traffic follows the managed firewall profile.")
        pains.append("Peer-to-peer, local development or legacy apps may need inbound rules.")
        impact = "Medium" if "inbound" in lower or "block" in lower else impact
    if "bitlocker" in lower or "encrypt" in lower or "filevault" in lower:
        experience.append("Drives are encrypted silently; recovery keys are escrowed to Entra ID.")
        pains.append("Startup PIN or USB key requirements add a pre-boot prompt; plan recovery-key access for the service desk.")
    if ("update" in lower and "security intelligence" not in lower) or " ring" in lower or "deadline" in lower:
        experience.append("Devices install updates on the ring's schedule.")
        pains.append("Deadlines force restarts; communicate maintenance windows.")
        impact = "Medium" if "deadline" in lower or "restart" in lower else impact
    if any(word in lower for word in ["copy", "paste", "save as", "print", "backup", "data transfer", "open-in", "clipboard"]):
        experience.append("Corporate data stays inside managed apps.")
        pains.append("Users cannot copy, save or share work data to personal apps or storage.")
        impact = "Medium"
    if re.search(r"\b(password|passcode|pin|screen lock|lock screen|device lock|inactivity)\b", lower) and "bitlocker" not in lower:
        experience.append("Devices require a PIN or password and lock after inactivity.")
        pains.append("Stronger PIN or password rules and shorter lock times affect day-to-day convenience.")
        impact = "Medium" if impact == "Low" else impact
    if "camera" in lower or "bluetooth" in lower or "usb" in lower or "removable" in lower:
        experience.append("Device hardware features are restricted.")
        pains.append("Users lose access to restricted hardware such as cameras or removable storage.")
        impact = "Medium"
    if poc:
        pains.append("Ships with a POC-safe (audit or less disruptive) value; enforce the baseline value after the pilot.")
    if not experience:
        experience.append("No visible change for most users.")
    return impact, " ".join(experience), " ".join(pains) or "None identified."


def severity_for(kind, text):
    lower = text.lower()
    if kind == "ConditionalAccess":
        if any(word in lower for word in ["admin", "break", "legacy", "risk", "block", "mfa", "strong auth"]):
            return "High"
        return "Medium"
    if any(word in lower for word in ["bitlocker", "encrypt", "tamper", "real-time", "realtime", "cloud protection", "credential", "lsa", "firewall enable", "enablefirewall"]):
        return "High"
    if any(word in lower for word in ["attack surface", "asr", "antivirus", "firewall", "password", "update"]):
        return "Medium"
    return "Low"


def graph_resource(kind, body, template):
    if kind == "ConditionalAccess":
        return "identity/conditionalAccess/policies"
    odata = body.get("@odata.type", "")
    if "settings" in body:
        return "deviceManagement/configurationPolicies"
    if "ManagedAppProtection" in odata:
        return "deviceAppManagement/" + ("androidManagedAppProtections" if "android" in odata.lower() else "iosManagedAppProtections")
    if "CompliancePolicy" in odata:
        return "deviceManagement/deviceCompliancePolicies"
    if "deviceManagementScript" in odata:
        return "deviceManagement/deviceManagementScripts"
    if "DriverUpdateProfile" in odata:
        return "deviceManagement/windowsDriverUpdateProfiles"
    return "deviceManagement/deviceConfigurations"


def source_of(kind, category, folder, description):
    if kind == "ConditionalAccess":
        return "GDS Conditional Access baseline"
    if kind == "EndpointSecurity":
        return "GDS Microsoft Defender for Endpoint baseline"
    if folder.startswith("OIB"):
        return "OpenIntuneBaseline (GPL-3.0)"
    if folder.startswith("MS Framework"):
        return "Microsoft Intune security configuration framework (MIT)"
    match = re.search(r"Source (?:baseline )?policy: ([^.]+)", description or "")
    return match.group(1).strip() if match else "GDS baseline"


# ----------------------------------------------------------------------------------------------------------------
# Policy repository
# ----------------------------------------------------------------------------------------------------------------
def policy_files(base, baseline_root=None):
    """Relative path -> file. GDS-ConditionalAccess-Baseline (when given) wins; Documents fills in the rest (it holds
    Intune folders the baseline repository does not)."""
    files = {}
    roots = [os.path.join(base, "ModernWork_SecurePlus_Baselines", "PolicyRepository")]
    if baseline_root:
        roots.append(os.path.join(baseline_root, "PolicyRepository"))
    for repository in roots:
        for path in glob.glob(os.path.join(repository, "**", "*.json"), recursive=True):
            files[os.path.relpath(path, repository).replace(os.sep, "/")] = path
    return files


def load_policy_controls(base, baseline_root=None):
    controls = []
    by_path = {}
    files = policy_files(base, baseline_root)
    for relative in sorted(files):
        path = files[relative]
        parts = relative.split("/")
        kind = parts[0]
        with open(path, encoding="utf-8-sig") as handle:
            body = json.load(handle)
        file_name = os.path.splitext(parts[-1])[0]
        number_match = NUMBER.match(file_name)
        number = number_match.group(1) if number_match else ""
        if kind == "Intune":
            platform, folder = parts[1], parts[2] if len(parts) > 3 else ""
            category = folder
        else:
            platform = "Windows" if kind == "EndpointSecurity" else "All"
            folder = parts[1] if len(parts) > 2 else ""
            category = folder
        raw_name = body.get("displayName") or body.get("name") or file_name
        name = strip_number(clean_name(raw_name))
        description = body.get("description") or ""
        template = body.get("templateReference") or {}
        # Setting and value from the file name convention "<Number>-RING-<Category>-<Setting> (<Value>)".
        tail = strip_number(clean_name(file_name))
        value_match = re.search(r"\(([^()]*)\)\s*$", tail)
        value = value_match.group(1) if value_match else (body.get("state") if kind == "ConditionalAccess" else "")
        setting = tail[: value_match.start()].strip() if value_match else tail
        if "-" in setting and kind != "ConditionalAccess":
            setting = setting.split("-", 1)[1].strip() if setting.count("-") >= 1 else setting

        if kind == "ConditionalAccess":
            expected = []
            evaluation = "ConditionalAccess"
            setting = ca_summary(body)
            impact, experience, pains = ca_guidance(body)
        elif "settings" in body:
            expected = []
            for item in body.get("settings") or []:
                flatten_setting(item.get("settingInstance"), expected)
            evaluation = "SettingsCatalog"
            impact, experience, pains = setting_guidance(name, description)
        elif body.get("@odata.type", "").endswith("deviceManagementScript"):
            expected = [{"path": "displayName", "value": clean_name(body.get("displayName", ""))}]
            evaluation = "Script"
            impact, experience, pains = setting_guidance(name, description)
        else:
            expected = object_expectations(body)
            evaluation = "GraphObject"
            impact, experience, pains = setting_guidance(name, description)

        prefix = {"ConditionalAccess": "CA", "EndpointSecurity": "ES", "Intune": "IN"}[kind]
        control = {
            "id": f"{prefix}-{number or file_name[:40]}",
            "number": number,
            "kind": kind,
            "category": category,
            "platform": platform,
            "name": name,
            "setting": setting,
            "value": str(value) if value is not None else "",
            "description": description or f"Conditional Access policy '{name}'. {setting}.",
            "source": source_of(kind, category, folder, description),
            "frameworks": frameworks_for(f"{name} {setting} {category} {description}"),
            "severity": severity_for(kind, f"{name} {setting} {description}"),
            "userImpact": impact,
            "experience": experience,
            "painPoints": pains,
            "licensing": licensing_for(kind, body, name),
            "graphResource": graph_resource(kind, body, template),
            "odataType": body.get("@odata.type") or ("#microsoft.graph.conditionalAccessPolicy" if kind == "ConditionalAccess" else "#microsoft.graph.deviceManagementConfigurationPolicy"),
            "templateFamily": template.get("templateFamily") or "",
            "evaluation": evaluation,
            "expected": expected,
            "path": relative,
            "policySets": [],
            "attributes": {},
            "body": json.dumps(body, separators=(",", ":"), ensure_ascii=False),
        }
        controls.append(control)
        by_path[relative] = control
    return controls, by_path


def licensing_for(kind, body, name):
    lower = name.lower()
    conditions = body.get("conditions") or {}
    if kind == "ConditionalAccess":
        if conditions.get("signInRiskLevels") or conditions.get("userRiskLevels") or "risk" in lower:
            return "Microsoft Entra ID P2"
        if "global secure access" in lower or "network" in lower:
            return "Microsoft Entra Internet/Private Access"
        if "workload" in lower:
            return "Microsoft Entra Workload ID"
        return "Microsoft Entra ID P1"
    if kind == "EndpointSecurity":
        return "Microsoft Intune Plan 1 + Microsoft Defender for Endpoint"
    if "windows 365" in lower:
        return "Windows 365"
    return "Microsoft Intune Plan 1"


def load_policy_sets(root, by_path):
    sets_root = os.path.join(root, "PolicySets")
    sets = []
    unresolved = []
    by_path_folded = {key.casefold(): value for key, value in by_path.items()}
    for path in sorted(glob.glob(os.path.join(sets_root, "*", "policy-set.json"))):
        with open(path, encoding="utf-8-sig") as handle:
            data = json.load(handle)
        ids = []
        for file in data.get("policyFiles") or []:
            normalized = os.path.normpath(os.path.join(os.path.dirname(path), file))
            relative = os.path.relpath(normalized, os.path.join(root, "PolicyRepository")).replace(os.sep, "/")
            # Source defect tolerated: GDS-ConditionalAccess-Baseline policy sets spell the folder "ConditionalAccesss".
            relative = re.sub(r"^ConditionalAccesss/", "ConditionalAccess/", relative)
            control = by_path.get(relative) or by_path_folded.get(relative.casefold())
            if control is not None:
                ids.append(control["id"])
            else:
                unresolved.append(f"{os.path.basename(os.path.dirname(path))}: {relative}")
        sets.append({
            "id": data.get("id") or os.path.basename(os.path.dirname(path)),
            "displayName": data.get("displayName") or "",
            "description": data.get("description") or "",
            "deploymentOrder": data.get("deploymentOrder", 0),
            "requires": data.get("requires") or [],
            "includes": data.get("includes") or [],
            "controlIds": ids,
        })
    by_id = {item["id"]: item for item in sets}

    def resolve(set_id, seen):
        if set_id in seen or set_id not in by_id:
            return []
        seen.add(set_id)
        item = by_id[set_id]
        ids = list(item["controlIds"])
        for child in item["includes"]:
            for control_id in resolve(child, seen):
                if control_id not in ids:
                    ids.append(control_id)
        return ids

    for item in sets:
        item["controlIds"] = resolve(item["id"], set())
    if unresolved:
        print(f"WARNING: {len(unresolved)} policy set file reference(s) do not exist in the policy repository:")
        for line in unresolved:
            print(f"  {line}")
    return sets


# ----------------------------------------------------------------------------------------------------------------
# Spreadsheets
# ----------------------------------------------------------------------------------------------------------------
def text(value):
    if value is None:
        return ""
    return str(value).strip()


def load_checks(base):
    path = os.path.join(base, "GDS Baseline Settings Map - WIP - TB.xlsx")
    workbook = openpyxl.load_workbook(path, read_only=True, data_only=True)
    sheet = workbook["Baseline Settings Map"]
    rows = list(sheet.iter_rows(values_only=True))
    header_index = next(index for index, row in enumerate(rows) if row and text(row[0]) == "Excel Row")
    header = [text(cell) for cell in rows[header_index]]
    controls = []
    for row in rows[header_index + 1:]:
        values = dict(zip(header, (text(cell) for cell in row)))
        check_id = values.get("Check ID (Cloud Capsule)")
        if not check_id:
            continue
        area = values.get("Area", "")
        title = values.get("Check Title", "")
        risk = values.get("Risk", "")
        severity = {"critical": "Critical", "high": "High", "medium": "Medium", "low": "Low"}.get(risk.lower(), "Medium")
        controls.append({
            "id": f"CHK-{check_id}",
            "number": values.get("Control #", ""),
            "kind": "AssessmentCheck",
            "category": area,
            "platform": "Microsoft 365",
            "name": title,
            "setting": values.get("Exact Setting Name", ""),
            "value": values.get("Required Value", ""),
            "description": values.get("Steps", ""),
            "source": "GDS Microsoft 365 security baseline (Cloud Capsule checks)",
            "frameworks": frameworks_for(f"{title} {values.get('Exact Setting Name', '')}"),
            "severity": severity,
            "userImpact": "",
            "experience": "",
            "painPoints": values.get("Notes", ""),
            "licensing": "",
            "graphResource": "",
            "odataType": "",
            "templateFamily": "",
            "evaluation": "TenantCheck",
            "expected": [],
            "path": "",
            "policySets": [],
            "attributes": {
                "Check ID": check_id,
                "Microsoft product": values.get("Microsoft Product", ""),
                "Admin portal": values.get("Admin Portal", ""),
                "Click path": values.get("Click Path (starting at admin.microsoft.com)", ""),
                "Steps": values.get("Steps", ""),
                "Exact setting name": values.get("Exact Setting Name", ""),
                "Required value": values.get("Required Value", ""),
                "Notes": values.get("Notes", ""),
            },
            "body": "",
        })
    return controls


def load_standards(base):
    path = os.path.join(base, "SecurePlusStandards.xlsx")
    workbook = openpyxl.load_workbook(path, read_only=True, data_only=True)
    sheet = workbook["Standards_Baseline_WIP"]
    rows = list(sheet.iter_rows(values_only=True))
    header = [text(cell) for cell in rows[0]]
    controls = []
    counters = {}
    for row in rows[1:]:
        values = dict(zip(header, (text(cell) for cell in row)))
        baseline_id = values.get("Baseline ID")
        if not baseline_id:
            continue
        counters[baseline_id] = counters.get(baseline_id, 0) + 1
        name = values.get("Setting Name") or values.get("Control") or baseline_id
        attributes = {key: value for key, value in values.items() if key and value}
        controls.append({
            "id": f"STD-{baseline_id}-{counters[baseline_id]:02d}",
            "number": values.get("Control", ""),
            "kind": "Standard",
            "category": values.get("Workstream", ""),
            "platform": values.get("Phase", ""),
            "name": name,
            "setting": values.get("Setting Category", ""),
            "value": values.get("Target Configuration or Required Action", ""),
            "description": values.get("Target Configuration or Required Action", ""),
            "source": "GDS Secure Plus standards",
            "frameworks": frameworks_for(f"{name} {values.get('Setting Category', '')} {values.get('Target Configuration or Required Action', '')}"),
            "severity": "Medium",
            "userImpact": values.get("Implementation Mode", ""),
            "experience": values.get("Implementation Mode", ""),
            "painPoints": "; ".join(part for part in [values.get("Exception Criteria", ""), values.get("Exclusions", "")] if part and part != "None"),
            "licensing": values.get("License Requirement", ""),
            "graphResource": "",
            "odataType": "",
            "templateFamily": "",
            "evaluation": "Manual",
            "expected": [],
            "path": "",
            "policySets": [values.get("Phase", "")] if values.get("Phase") else [],
            "attributes": attributes,
            "body": "",
        })
    return controls


def load_microsoft_baselines(base):
    controls = []
    specs = [
        ("Windows 11 v26H2 Security Baseline v2", "MS Security Baseline Windows 11 v26H2.xlsx", "Windows 11", "MSB-W11"),
        ("Windows Server 2025 Security Baseline - 2602", "MS Security Baseline Windows Server 2025 v2602.xlsx", "Windows Server 2025", "MSB-WS25"),
        ("Microsoft 365 Apps for Enterprise 2512", "Microsoft 365 Apps for enterprise 2512.xlsx", "Microsoft 365 Apps", "MSB-M365A"),
    ]
    for folder, workbook_name, platform, prefix in specs:
        path = os.path.join(base, "Frameworks", "Microsoft Security Baselines", folder, "Documentation", workbook_name)
        if not os.path.exists(path):
            continue
        workbook = openpyxl.load_workbook(path, read_only=True, data_only=True)
        index = 0
        for sheet in workbook.worksheets:
            if sheet.title in ("Information",):
                continue
            rows = list(sheet.iter_rows(values_only=True))
            if not rows:
                continue
            header = [text(cell) for cell in rows[0]]
            if "Policy Setting Name" not in header:
                continue
            value_column = 2
            for row in rows[1:]:
                values = [text(cell) for cell in row]
                if len(values) <= value_column or not values[1]:
                    continue
                configured = values[value_column]
                if not configured or configured == "None":
                    continue
                index += 1
                record = dict(zip(header, values))
                name = values[1]
                controls.append({
                    "id": f"{prefix}-{index:04d}",
                    "number": str(index),
                    "kind": "MicrosoftBaseline",
                    "category": f"{sheet.title}: {values[0]}",
                    "platform": platform,
                    "name": name,
                    "setting": values[0],
                    "value": configured,
                    "description": record.get("Help Text", "")[:1500],
                    "source": f"Microsoft security baseline: {folder}",
                    "frameworks": ["Microsoft Security Baseline"] + frameworks_for(f"{name} {values[0]}"),
                    "severity": severity_for("MicrosoftBaseline", f"{name} {values[0]}"),
                    "userImpact": "",
                    "experience": "",
                    "painPoints": record.get("Comments", ""),
                    "licensing": "",
                    "graphResource": "",
                    "odataType": "",
                    "templateFamily": "",
                    "evaluation": "Manual",
                    "expected": [],
                    "path": "",
                    "policySets": [],
                    "attributes": {key: value[:500] for key, value in record.items() if key and value and key != "Help Text"},
                    "body": "",
                })
    return controls



def load_tenant_settings(base):
    """Tenant settings (4.0 beta.2): ModernWork_SecurePlus_Baselines/TenantSettings/*.json, one file per product.

    Each control states how it is assessed: a rule over a Graph, DNS or read-only PowerShell dataset ("assess": {"method", "dataset",
    "path", "op", "value", "match", "where"}) or Manual (with the portal and click path), and how it is deployed.
    """
    folder = os.path.join(base, "ModernWork_SecurePlus_Baselines", "TenantSettings")
    controls = []
    for path in sorted(glob.glob(os.path.join(folder, "*.json"))):
        with open(path, encoding="utf-8-sig") as handle:
            document = json.load(handle)
        product = document["product"]
        for item in document["controls"]:
            assess = item.get("assess") or {"method": "Manual"}
            method = assess.get("method", "Manual")
            rule = {key: assess[key] for key in ("dataset", "path", "op", "value", "match", "where", "requires", "all") if key in assess}
            automatic = method != "Manual" and ("dataset" in assess or "all" in assess)
            deploy = item.get("deploy") or {"method": "Manual"}
            attributes = {
                "Microsoft product": product,
                "Feature area": item["area"],
                "Admin portal": item.get("portal", ""),
                "Click path": item.get("clickPath", ""),
                "Deploy": deploy.get("how", ""),
            }
            if automatic:
                attributes["Rule"] = json.dumps(rule, ensure_ascii=False)
            if item.get("cis"):
                attributes["CIS"] = item["cis"]
            if item.get("azurePolicy"):
                attributes["Azure Policy definition"] = item["azurePolicy"]
            if item.get("license"):
                attributes["License requirement"] = item["license"]
            controls.append({
                "id": item["id"],
                "number": item["id"].split("-")[-1],
                "kind": "TenantSetting",
                "category": f"{product}: {item['area']}",
                "platform": document.get("platform", "Microsoft 365"),
                "name": item["name"],
                "setting": item.get("setting", ""),
                "value": item.get("value", ""),
                "description": item.get("why", ""),
                "source": item.get("source", document.get("source", "GDS tenant settings")),
                "frameworks": item.get("frameworks") or frameworks_for(f"{item['name']} {item.get('setting', '')}"),
                "severity": item.get("severity", "Medium"),
                "userImpact": item.get("userImpact", "Low"),
                "experience": item.get("experience", ""),
                "painPoints": item.get("painPoints", ""),
                "licensing": item.get("license", ""),
                "graphResource": assess.get("graph", ""),
                "odataType": "",
                "templateFamily": "",
                "evaluation": "TenantSetting" if automatic else "Manual",
                "expected": [],
                "path": os.path.relpath(path, base).replace(os.sep, "/"),
                "policySets": [],
                "attributes": attributes,
                "body": "",
                "product": product,
                "featureArea": item["area"],
                "defaultValue": item.get("default", ""),
                "assessMethod": method,
                "deployMethod": deploy.get("method", "Manual"),
                "sourceVersion": item.get("sourceVersion", document.get("sourceVersion", "")),
                "sourceUrl": item.get("url", ""),
            })
    return controls

def dedupe(controls):
    """Control numbers repeat across a few folders; suffix repeats so every ID is unique and stable."""
    seen = {}
    for control in controls:
        base_id = control["id"]
        seen[base_id] = seen.get(base_id, 0) + 1
        if seen[base_id] > 1:
            control["id"] = f"{base_id}-{seen[base_id]}"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--documents", required=True, help="Path to a clone of the Documents repository.")
    parser.add_argument("--baseline", help="Path to a clone of GDS-ConditionalAccess-Baseline (PolicyRepository and PolicySets); "
                                           "defaults to Documents/ModernWork_SecurePlus_Baselines.")
    parser.add_argument("--output", required=True, help="Output catalog.json.gz path.")
    args = parser.parse_args()
    base = os.path.abspath(args.documents)
    baseline_root = os.path.abspath(args.baseline) if args.baseline else None

    policy_controls, by_path = load_policy_controls(base, baseline_root)
    policy_controls, aliases = mark_variants(policy_controls)
    sets = load_policy_sets(baseline_root or os.path.join(base, "ModernWork_SecurePlus_Baselines"), by_path)
    membership = {}
    for item in sets:
        for control_id in item["controlIds"]:
            membership.setdefault(control_id, []).append(item["id"])
    for control in policy_controls:
        control["policySets"] = membership.get(control["id"], [])

    controls = policy_controls + load_checks(base) + load_standards(base) + load_microsoft_baselines(base) + load_tenant_settings(base)
    dedupe(controls)
    enrich(controls)
    def revision_of(path):
        try:
            return subprocess.run(["git", "-C", path, "rev-parse", "--short", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
        except (OSError, subprocess.CalledProcessError):
            return "unknown"

    revision = revision_of(base)
    source = f"toby67maro/Documents@{revision}"
    if baseline_root:
        source += f"; toby67maro/GDS-ConditionalAccess-Baseline@{revision_of(baseline_root)}"

    catalog = {
        "schemaVersion": 2,
        "seedVersion": SEED_VERSION,
        "source": source,
        "generatedAtUtc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "derivedFieldsNote": "Frameworks, user impact, experience and pain points of policy controls are derived by GDS keyword rules (indicative guidance, not an authoritative crosswalk).",
        "policySets": sets,
        "solutionAreas": SOLUTION_AREAS,
        "experienceAreas": EXPERIENCE_AREAS,
        "specifications": specifications(sets, controls),
        "baselines": baselines(sets, controls),
        "aliases": aliases,
        "controls": controls,
    }
    os.makedirs(os.path.dirname(os.path.abspath(args.output)), exist_ok=True)
    payload = json.dumps(catalog, ensure_ascii=False, separators=(",", ":"), sort_keys=False).encode("utf-8")
    with gzip.GzipFile(args.output, "wb", mtime=0) as handle:
        handle.write(payload)
    kinds = {}
    for control in controls:
        kinds[control["kind"]] = kinds.get(control["kind"], 0) + 1
    print(f"{len(controls)} controls ({kinds}), {len(sets)} policy sets, {len(payload) / 1024:.0f} KiB JSON -> {os.path.getsize(args.output) / 1024:.0f} KiB gzip")


if __name__ == "__main__":
    main()
