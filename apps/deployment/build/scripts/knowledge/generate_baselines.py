#!/usr/bin/env python3
"""Knowledge base phase 2: configuration baselines, GDS delivery content and the question bank.

Reads (from the Documents repository):
  * Frameworks/CIS Benchmarks CSV/*.csv            - the 56 CIS Benchmarks extracted per recommendation (Copilot, 2026-11);
                                                      checked and corrected against the PDFs (see --pdf-text)
  * Frameworks/Microsoft Security Baselines/*      - Windows 11 26H2, Windows Server 2025 v2602, Microsoft 365 Apps 2512
  * SecurePlusStandards.xlsx                       - WBS_WIP (work breakdown structure), Discovery_Decisions (question bank)
  * EaaS/Questionnaire/GDS_EaaS_Question_Bank.json - EaaS discovery and design-workshop forms, rules, personas
  * EaaS/M365_Azure_Config_Method_Matrix.csv       - how each Microsoft 365 / Azure setting group is deployed
and writes a deterministic baselines.json.gz embedded in the application (dataset "knowledge-base-phase-2").

Text is cut to the EaaS v3 column lengths (GDS_ComplianceIQ_Data_Dictionary_v3.csv) and natural keys are de-duplicated
the way SQL Server compares them (case-insensitive, trailing spaces ignored).

Usage:
  python3 build/scripts/knowledge/generate_baselines.py --documents /path/to/Documents --pdf-text /path/to/cis-text \
      --output src/EngineeringWorkspace.Infrastructure/KnowledgeBase/baselines.json.gz
--pdf-text is a folder with `pdftotext -layout` output of each CIS PDF (same base name, .txt), used to verify the
recommendation numbers and registry paths.
"""
import argparse
import collections
import csv
import glob
import gzip
import hashlib
import json
import os
import re
import sys

import openpyxl

try:  # GPO manifests are XML: parse them without external entities.
    import defusedxml.ElementTree as ET
except ImportError:  # pragma: no cover - defusedxml is recommended; the manifests come from Microsoft's own download
    import xml.etree.ElementTree as ET

VERSION = "2026.11-1"
csv.field_size_limit(10 ** 9)

# ----------------------------------------------------------------------------------------------------------------------
# Column lengths from the EaaS v3 data dictionary
# ----------------------------------------------------------------------------------------------------------------------
LENGTHS = {}
TRUNCATED = collections.Counter()


def load_lengths(dictionary):
    for line in open(dictionary, encoding="utf-8-sig").read().splitlines()[1:]:
        parts = line.split("|")
        match = re.match(r"n?varchar\((\d+)\)|n?char\((\d+)\)", parts[8])
        if match:
            LENGTHS[f"{parts[0]}.{parts[1]}.{parts[7]}"] = int(match.group(1) or match.group(2))


def fit(table, column, value):
    """Text cut to the column length (on a word boundary, with an ellipsis); None for empty."""
    if value is None:
        return None
    text = str(value).replace(" ", " ").strip()
    if not text:
        return None
    limit = LENGTHS.get(f"{table}.{column}")
    if limit is None or len(text) <= limit:
        return text
    TRUNCATED[f"{table}.{column}"] += 1
    cut = text[:limit - 1]
    space = cut.rfind(" ")
    return (cut[:space] if space > limit * 0.6 else cut).rstrip() + "…"


def one_line(value):
    return re.sub(r"\s+", " ", value or "").strip()


def first_sentence(value, limit=300):
    text = one_line(value)
    match = re.match(r"(.+?[.!?])(\s|$)", text)
    sentence = match.group(1) if match else text
    if len(sentence) > limit:
        sentence = sentence[:limit - 1].rsplit(" ", 1)[0] + "…"
    return sentence or None


def fold(value):
    return (value or "").rstrip().casefold()


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def sort_key(number):
    return ".".join(part.zfill(3) for part in str(number).split("."))[:60]


class Table:
    """Rows with a case-insensitive natural key: the first spelling wins, later duplicates are dropped."""

    def __init__(self, key_columns):
        self.key_columns = key_columns
        self.rows = []
        self.keys = {}

    def add(self, row):
        key = tuple(fold(row[i]) if isinstance(row[i], str) else row[i] for i in self.key_columns)
        if key in self.keys:
            return self.keys[key]
        self.keys[key] = row
        self.rows.append(row)
        return row

    def __contains__(self, key):
        return tuple(fold(k) if isinstance(k, str) else k for k in key) in self.keys


# ----------------------------------------------------------------------------------------------------------------------
# Reference rows
# ----------------------------------------------------------------------------------------------------------------------
LICENSES = [
    # code, name, url, commercial, redistribution, derivatives, customer-facing text, attribution, notes
    ["CIS-BENCHMARK-TERMS", "CIS Benchmarks terms of use", "https://www.cisecurity.org/terms-and-conditions-table-of-contents", 0, 0, 0, 0,
     "Center for Internet Security, Inc. (CIS) – CIS Benchmarks", "Recommendation text is stored for internal GDS use only (IsInternalOnly = 1). Review the terms in each PDF before quoting CIS text to a customer."],
    ["MICROSOFT-SCT", "Microsoft Security Compliance Toolkit", "https://www.microsoft.com/download/details.aspx?id=55319", 1, 0, 1, 1,
     "Microsoft Security Baselines (Security Compliance Toolkit)", "Baseline values and Group Policy settings published by Microsoft."],
    ["GDS-INTERNAL", "Global Data Systems internal", None, 1, 0, 1, 1, "Global Data Systems", "GDS standards, WBS, question bank and method matrix."],
]
BASELINE_SOURCES = [
    ["CIS", "CIS Benchmarks", "Center for Internet Security", 0, "CIS-BENCHMARK-TERMS", "https://www.cisecurity.org/cis-benchmarks"],
    ["MSFT-SCT", "Microsoft Security Baselines", "Microsoft", 0, "MICROSOFT-SCT", "https://learn.microsoft.com/windows/security/operating-system-security/device-management/windows-security-configuration-framework/windows-security-baselines"],
]
MAPPING_SOURCES = [
    ["CIS-BENCHMARK", "CIS Benchmark mappings to CIS Controls and NIST", "Center for Internet Security", "Authoritative", 90, "https://www.cisecurity.org/cis-benchmarks"],
    ["CIS-BENCHMARK-ATTACK", "CIS Benchmark mappings to MITRE ATT&CK", "Center for Internet Security", "Authoritative", 80, "https://www.cisecurity.org/cis-benchmarks"],
    ["MSFT-SCT", "Microsoft Security Baselines", "Microsoft", "Authoritative", 100, "https://www.microsoft.com/download/details.aspx?id=55319"],
    ["GDS-STANDARDS", "GDS Secure Plus standards, WBS and discovery decisions", "Global Data Systems", "GdsInternal", 90, None],
]
PLATFORMS = [
    # code, name, family, vendor, version, parent
    ["WINDOWS", "Microsoft Windows", "Windows", "Microsoft", None, None],
    ["WIN10", "Windows 10", "Windows", "Microsoft", "10", "WINDOWS"],
    ["WIN11", "Windows 11", "Windows", "Microsoft", "11", "WINDOWS"],
    ["WINDOWS-SERVER", "Windows Server", "WindowsServer", "Microsoft", None, None],
    ["WS2019", "Windows Server 2019", "WindowsServer", "Microsoft", "2019", "WINDOWS-SERVER"],
    ["WS2022", "Windows Server 2022", "WindowsServer", "Microsoft", "2022", "WINDOWS-SERVER"],
    ["WS2025", "Windows Server 2025", "WindowsServer", "Microsoft", "2025", "WINDOWS-SERVER"],
    ["MACOS", "Apple macOS", "macOS", "Apple", None, None],
    ["MACOS14", "macOS 14 Sonoma", "macOS", "Apple", "14", "MACOS"],
    ["MACOS15", "macOS 15 Sequoia", "macOS", "Apple", "15", "MACOS"],
    ["MACOS26", "macOS 26 Tahoe", "macOS", "Apple", "26", "MACOS"],
    ["IOS", "Apple iOS and iPadOS", "iOS", "Apple", None, None],
    ["IOS17", "iOS 17 and iPadOS 17", "iOS", "Apple", "17", "IOS"],
    ["IOS18", "iOS 18 and iPadOS 18", "iOS", "Apple", "18", "IOS"],
    ["IOS26", "iOS 26", "iOS", "Apple", "26", "IOS"],
    ["IPADOS26", "iPadOS 26", "iPadOS", "Apple", "26", "IOS"],
    ["ANDROID", "Google Android", "Android", "Google", None, None],
    ["CHROME", "Google Chrome", "Browser", "Google", None, None],
    ["EDGE", "Microsoft Edge", "Browser", "Microsoft", None, None],
    ["M365APPS", "Microsoft 365 Apps", "OfficeApps", "Microsoft", None, None],
    ["M365", "Microsoft 365", "M365", "Microsoft", None, None],
    ["AZURE", "Microsoft Azure", "Azure", "Microsoft", None, None],
    ["AWS", "Amazon Web Services", "AWS", "Amazon", None, None],
    ["GCP", "Google Cloud Platform", "GCP", "Google", None, None],
    ["GCP-COS", "Google Container-Optimized OS", "ContainerOS", "Google", None, "GCP"],
    ["GWS", "Google Workspace", "GoogleWorkspace", "Google", None, None],
    ["SQLSERVER", "Microsoft SQL Server", "Database", "Microsoft", None, None],
    ["SQL2016", "SQL Server 2016", "Database", "Microsoft", "2016", "SQLSERVER"],
    ["SQL2017", "SQL Server 2017", "Database", "Microsoft", "2017", "SQLSERVER"],
    ["SQL2019", "SQL Server 2019", "Database", "Microsoft", "2019", "SQLSERVER"],
    ["SQL2022", "SQL Server 2022", "Database", "Microsoft", "2022", "SQLSERVER"],
    ["SQL2025", "SQL Server 2025", "Database", "Microsoft", "2025", "SQLSERVER"],
    ["ESXI", "VMware ESXi", "Hypervisor", "Broadcom", None, None],
    ["ESXI7", "VMware ESXi 7", "Hypervisor", "Broadcom", "7", "ESXI"],
    ["ESXI8", "VMware ESXi 8", "Hypervisor", "Broadcom", "8", "ESXI"],
    ["ESXI9", "VMware ESXi 9", "Hypervisor", "Broadcom", "9", "ESXI"],
    ["POWERPLATFORM", "Microsoft Power Platform and Dynamics 365", "PowerPlatform", "Microsoft", None, None],
    ["DEFENDER-AV", "Microsoft Defender Antivirus", "Antivirus", "Microsoft", None, None],
    ["MCP-SERVER", "Model Context Protocol server", "AIPlatform", None, None, None],
]

# PDF base name -> baseline code, platform, delivery mechanism, archived, superseded by.
CIS_BASELINES = {
    "CIS AWS Compute Services Benchmark v2.0.0 - PDF": ("CIS-AWS-COMPUTE-2.0.0", "AWS", "CloudConsole", 0, None),
    "CIS AWS Database Services Benchmark v2.0.0 - PDF": ("CIS-AWS-DATABASE-2.0.0", "AWS", "CloudConsole", 0, None),
    "CIS_AWS_End_User_Compute_Services_Benchmark_v1.2.0": ("CIS-AWS-EUC-1.2.0", "AWS", "CloudConsole", 0, None),
    "CIS_AWS_Storage_Services_Benchmark_v1.0.0": ("CIS-AWS-STORAGE-1.0.0", "AWS", "CloudConsole", 0, None),
    "CIS_Amazon_Web_Services_Foundations_Benchmark_v7.1.0": ("CIS-AWS-FOUNDATIONS-7.1.0", "AWS", "CloudConsole", 0, None),
    "CIS_Apple_MacOS_14.0_Sonoma_Intune_Benchmark_v1.0.0": ("CIS-MACOS14-INTUNE-1.0.0", "MACOS14", "Intune", 0, None),
    "CIS_Apple_MacOS_15.0_Sequoia_Intune_Benchmark_v1.0.0": ("CIS-MACOS15-INTUNE-1.0.0", "MACOS15", "Intune", 0, "CIS-MACOS15-INTUNE-1.1.0"),
    "CIS_Apple_MacOS_15.0_Sequoia_Intune_Benchmark_v1.1.0": ("CIS-MACOS15-INTUNE-1.1.0", "MACOS15", "Intune", 0, None),
    "CIS_Apple_MacOS_26_Tahoe_Intune_Benchmark_v1.0.0": ("CIS-MACOS26-INTUNE-1.0.0", "MACOS26", "Intune", 0, None),
    "CIS_Apple_iOS_18_and_iPadOS_18_Intune_Benchmark_v1.0.0": ("CIS-IOS18-INTUNE-1.0.0", "IOS18", "Intune", 0, "CIS-IOS18-INTUNE-2.0.0"),
    "CIS_Apple_iOS_18_and_iPadOS_18_Intune_Benchmark_v2.0.0": ("CIS-IOS18-INTUNE-2.0.0", "IOS18", "Intune", 0, None),
    "CIS_Apple_iOS_26_Benchmark_v1.0.0": ("CIS-IOS26-1.0.0", "IOS26", "ConfigFile", 0, None),
    "CIS_Apple_iOS_26_and_iPadOS_26_Intune_Benchmark_v1.0.0": ("CIS-IOS26-INTUNE-1.0.0", "IOS26", "Intune", 0, None),
    "CIS_Apple_iPadOS_26_Benchmark_v1.0.0": ("CIS-IPADOS26-1.0.0", "IPADOS26", "ConfigFile", 0, None),
    "CIS_Google_Android_Benchmark_v1.5.0": ("CIS-ANDROID-1.5.0", "ANDROID", "ConfigFile", 0, "CIS-ANDROID-1.6.0"),
    "CIS_Google_Android_Benchmark_v1.6.0": ("CIS-ANDROID-1.6.0", "ANDROID", "ConfigFile", 0, None),
    "CIS_Google_Chrome_Benchmark_v3.0.0": ("CIS-CHROME-3.0.0", "CHROME", "GPO", 0, None),
    "CIS_Google_Chrome_Group_Policy_Benchmark_v1.1.0": ("CIS-CHROME-GPO-1.1.0", "CHROME", "GPO", 0, None),
    "CIS_Google_Cloud_Platform_Foundation_Benchmark_v5.0.1": ("CIS-GCP-FOUNDATIONS-5.0.1", "GCP", "CloudConsole", 0, None),
    "CIS_Google_Container-Optimized_OS_Benchmark_v1.2.0": ("CIS-GCP-COS-1.2.0", "GCP-COS", "CLI", 0, None),
    "CIS_Google_Workspace_Foundations_Benchmark_v1.3.0": ("CIS-GWS-1.3.0", "GWS", "CloudConsole", 0, "CIS-GWS-1.4.0"),
    "CIS_Google_Workspace_Foundations_Benchmark_v1.4": ("CIS-GWS-1.4.0", "GWS", "CloudConsole", 0, None),
    "CIS_MCP_Server_Benchmark_v1.0.0": ("CIS-MCP-SERVER-1.0.0", "MCP-SERVER", "ConfigFile", 0, None),
    "CIS_Microsoft_365_Foundations_Benchmark_v7.0.0": ("CIS-M365-FOUNDATIONS-7.0.0", "M365", "CloudConsole", 0, None),
    "CIS_Microsoft_Azure_Compute_Services_Benchmark_v2.0.0": ("CIS-AZURE-COMPUTE-2.0.0", "AZURE", "CloudConsole", 0, None),
    "CIS_Microsoft_Azure_Database_Services_Benchmark_v2.0.0": ("CIS-AZURE-DATABASE-2.0.0", "AZURE", "CloudConsole", 0, None),
    "CIS_Microsoft_Azure_Foundations_Benchmark_v7.0.0": ("CIS-AZURE-FOUNDATIONS-7.0.0", "AZURE", "CloudConsole", 0, None),
    "CIS_Microsoft_Azure_Storage_Services_Benchmark_v2.0.0": ("CIS-AZURE-STORAGE-2.0.0", "AZURE", "CloudConsole", 0, None),
    "CIS_Microsoft_Defender_Antivirus_Benchmark_v1.0.0": ("CIS-DEFENDER-AV-1.0.0", "DEFENDER-AV", "GPO", 0, None),
    "CIS_Microsoft_Dynamics_365_Power_Platform_v1.0.1": ("CIS-POWERPLATFORM-1.0.1", "POWERPLATFORM", "CloudConsole", 0, None),
    "CIS_Microsoft_Edge_Benchmark_v5.0.0": ("CIS-EDGE-5.0.0", "EDGE", "GPO", 0, None),
    "CIS_Microsoft_Intune_for_Apple_iOS_17_and_iPadOS_17_Benchmark_v2.0.0": ("CIS-IOS17-INTUNE-2.0.0", "IOS17", "Intune", 0, None),
    "CIS_Microsoft_Intune_for_Edge_Benchmark_v1.0.0": ("CIS-EDGE-INTUNE-1.0.0", "EDGE", "Intune", 0, None),
    "CIS_Microsoft_Intune_for_Microsoft_Defender_Antivirus_Benchmark_v1.0.0": ("CIS-DEFENDER-AV-INTUNE-1.0.0", "DEFENDER-AV", "Intune", 0, None),
    "CIS_Microsoft_Intune_for_Office_Benchmark_v1.1.0": ("CIS-M365APPS-INTUNE-1.1.0", "M365APPS", "Intune", 0, None),
    "CIS_Microsoft_Intune_for_Windows_10_Benchmark_v5.0.0": ("CIS-WIN10-INTUNE-5.0.0", "WIN10", "Intune", 0, None),
    "CIS_Microsoft_Intune_for_Windows_11_Benchmark_v5.0.0": ("CIS-WIN11-INTUNE-5.0.0", "WIN11", "Intune", 0, None),
    "CIS_Microsoft_SQL_Server_2016_Benchmark_v1.4.0_Archive": ("CIS-SQL2016-1.4.0", "SQL2016", "SQL", 1, None),
    "CIS_Microsoft_SQL_Server_2017_Benchmark_v1.3.0_Archive": ("CIS-SQL2017-1.3.0", "SQL2017", "SQL", 1, None),
    "CIS_Microsoft_SQL_Server_2019_Benchmark_v1.6.0_FINAL_UPDATE": ("CIS-SQL2019-1.6.0", "SQL2019", "SQL", 0, None),
    "CIS_Microsoft_SQL_Server_2022_Benchmark_v1.3.0": ("CIS-SQL2022-1.3.0", "SQL2022", "SQL", 0, None),
    "CIS_Microsoft_SQL_Server_2025_Benchmark_v1.0.0": ("CIS-SQL2025-1.0.0", "SQL2025", "SQL", 0, None),
    "CIS_Microsoft_Windows_11_Enterprise_Benchmark_v5.1.1": ("CIS-WIN11-ENT-5.1.1", "WIN11", "GPO", 0, None),
    "CIS_Microsoft_Windows_11_Stand-alone_Benchmark_v2.0.0-ARCHIVE": ("CIS-WIN11-SA-2.0.0", "WIN11", "GPO", 1, "CIS-WIN11-SA-5.0.0"),
    "CIS_Microsoft_Windows_11_Stand-alone_Benchmark_v5.0.0": ("CIS-WIN11-SA-5.0.0", "WIN11", "GPO", 0, None),
    "CIS_Microsoft_Windows_Server_2019_STIG_Benchmark_v3.0.0": ("CIS-WS2019-STIG-3.0.0", "WS2019", "GPO", 0, None),
    "CIS_Microsoft_Windows_Server_2019_Stand-alone_v4.0.0": ("CIS-WS2019-SA-4.0.0", "WS2019", "GPO", 0, None),
    "CIS_Microsoft_Windows_Server_2022_Benchmark_v5.1.0": ("CIS-WS2022-5.1.0", "WS2022", "GPO", 0, None),
    "CIS_Microsoft_Windows_Server_2022_STIG_Benchmark_v1.0.0-ARCHIVE": ("CIS-WS2022-STIG-1.0.0", "WS2022", "GPO", 1, "CIS-WS2022-STIG-2.0.0"),
    "CIS_Microsoft_Windows_Server_2022_STIG_Benchmark_v2.0.0": ("CIS-WS2022-STIG-2.0.0", "WS2022", "GPO", 0, None),
    "CIS_Microsoft_Windows_Server_2022_Stand-alone_Benchmark_v2.0.0": ("CIS-WS2022-SA-2.0.0", "WS2022", "GPO", 0, None),
    "CIS_Microsoft_Windows_Server_2025_Benchmark_v2.1.1": ("CIS-WS2025-2.1.1", "WS2025", "GPO", 0, None),
    "CIS_Microsoft_Windows_Server_2025_Stand-alone_v2.0.0": ("CIS-WS2025-SA-2.0.0", "WS2025", "GPO", 0, None),
    "CIS VMWare ESXi 7 Benchmark V1.6.0 PDF": ("CIS-ESXI7-1.6.0", "ESXI7", "CLI", 0, None),
    "CIS_VMware_ESXi_8.0_Benchmark_v1.4.0_PDF": ("CIS-ESXI8-1.4.0", "ESXI8", "CLI", 0, None),
    "CIS_VMware_ESXi_9.0_Benchmark_v1.0.0": ("CIS-ESXI9-1.0.0", "ESXI9", "CLI", 0, None),
}
CLOUD_PLATFORMS = {"AWS", "AZURE", "GCP", "GWS", "M365", "POWERPLATFORM"}

QUALIFIERS = [
    ("domain controller", "DomainController"), ("member server", "MemberServer"), ("bitlocker", "BitLocker"), ("next generation", "NextGeneration"),
    ("stig", "STIG"), ("e3", "E3"), ("e5", "E5"), ("supervised", "Supervised"), ("byod", "BYOD"), ("unsupervised", "Unsupervised"),
    ("corporate", "Corporate"), ("enterprise", "Enterprise"), ("stand-alone", "StandAlone"), ("standalone", "StandAlone"),
]
REGISTRY = re.compile(r"(HKEY_LOCAL_MACHINE|HKEY_CURRENT_USER|HKEY_USERS|HKLM|HKCU|HKU)\\")
HIVES = {"HKEY_LOCAL_MACHINE": "HKLM", "HKEY_CURRENT_USER": "HKCU", "HKEY_USERS": "HKU", "HKLM": "HKLM", "HKCU": "HKCU", "HKU": "HKU"}
VALUE_TYPE = re.compile(r"\b(REG_(?:DWORD|SZ|MULTI_SZ|QWORD|EXPAND_SZ|BINARY))\b")


KNOWN_KEYS = set()  # registry keys published by Microsoft (security baselines), folded


def registry_from_text(text):
    """Registry key and value from an audit procedure. The PDF wraps long paths across lines: a wrap after a backslash
    joins directly, otherwise the wrap replaced a space ('Windows NT\\Terminal' + 'Services'). When a wrap could be
    either, the spelling Microsoft publishes in its security baselines wins."""
    lines = (text or "").split("\n")
    for index, line in enumerate(lines):
        match = REGISTRY.search(line)
        if not match:
            continue
        pieces = [line[match.start():].strip()]
        follow = index + 1
        while ":" not in pieces[-1].split("\\", 1)[-1] and ":" not in "".join(pieces).split("\\", 1)[-1] and follow < len(lines) and lines[follow].strip() \
                and len(pieces) < 4:
            pieces.append(lines[follow].strip())
            follow += 1
        candidates = []
        for mask in range(1 << (len(pieces) - 1)):
            joined = pieces[0]
            for position, piece in enumerate(pieces[1:]):
                glue = "" if joined.endswith("\\") else (" " if not mask & (1 << position) else "")
                joined += glue + piece
            candidates.append((mask, joined))
        best = None
        for mask, joined in candidates:
            parsed = split_registry(joined)
            if not parsed:
                continue
            if fold(parsed[1]) in KNOWN_KEYS:
                return parsed
            if best is None or mask == 0:
                best = parsed if best is None or mask == 0 else best
        if best:
            return best
    return None


def merge_registry(parsed, extracted):
    """Combines the path read from the audit text with the extracted columns. The audit text keeps the spaces of the key
    ('Session Manager', 'Windows NT\\DNSClient') that the extraction lost; the extraction re-joined value names that wrap
    onto the next line ('DefaultOutbound' + 'Action')."""
    hive, key, value = parsed
    _, old_key, old_value = extracted
    squeeze = lambda text: re.sub(r"\s+", "", text).casefold()
    if squeeze(old_key) != squeeze(key) and not fold(key).startswith(fold(old_key)) and fold(key) not in KNOWN_KEYS and fold(old_key) in KNOWN_KEYS:
        key = old_key
    if old_value and (not value or (fold(old_value).startswith(fold(value)) and len(old_value) > len(value))):
        value = old_value
    return hive, key, value


def split_registry(path):
    path = re.split(r"\s{2,}|\s+(?=Note:)", path)[0].rstrip(" .")
    if ":" not in path:
        return None
    key_part, value = path.split(":", 1)
    hive, _, key = key_part.partition("\\")
    value = value.strip().split(" ")[0].strip("'\".,")
    if not key.strip() or "=" in key:
        return None
    return HIVES.get(hive.upper(), hive.upper()), key.strip("\\ "), value


def pdf_heading(lines, number):
    """Full heading of a recommendation in the PDF text (it can wrap over several lines), without the level tag."""
    for index, line in enumerate(lines):
        match = re.match(r"^\s*" + re.escape(number) + r"\s+(.*)", line)
        if not match:
            continue
        parts = [match.group(1).strip()]
        follow = index + 1
        while not re.search(r"\((Automated|Manual)\)\s*$", parts[-1]) and follow < len(lines) and follow - index < 5 and lines[follow].strip():
            parts.append(lines[follow].strip())
            follow += 1
        if re.search(r"\((Automated|Manual)\)\s*$", parts[-1]):
            return re.sub(r"^\((L\d|BL|NG)\)\s*", "", " ".join(parts))
    return None


def nist_code(reference):
    """'rev5:CM-6 (1)' / 'CM-6(1)' -> 'CM-06(01)' (the knowledge base's NIST 800-53 R5.2 code)."""
    match = re.match(r"(?:rev\d+:)?\s*([A-Z]{2})-(\d+)\s*(?:\((\d+)\))?", reference.strip())
    if not match:
        return None
    family, number, enhancement = match.groups()
    return f"{family}-{int(number):02d}" + (f"({int(enhancement):02d})" if enhancement else "")


def profile_code(name, used):
    text = name.lower()
    level = re.search(r"level\s*(\d)", text)
    parts = [f"L{level.group(1)}"] if level else []
    if "bitlocker" in text:
        parts.append("BL")
    if "next generation" in text:
        parts.append("NG")
    for needle, qualifier in QUALIFIERS:
        if needle in text and qualifier not in ("BitLocker", "NextGeneration"):
            parts.append({"DomainController": "DC", "MemberServer": "MS"}.get(qualifier, qualifier.upper()))
    code = "-".join(dict.fromkeys(parts)) or re.sub(r"[^A-Z0-9]+", "-", name.upper()).strip("-")
    code = code[:60]
    base, counter = code, 2
    while code in used and used[code] != name:
        code = f"{base[:56]}-{counter}"
        counter += 1
    used[code] = name
    return code


# ----------------------------------------------------------------------------------------------------------------------
# Builder
# ----------------------------------------------------------------------------------------------------------------------
class Builder:
    def __init__(self):
        self.sources = Table([0])
        self.platforms = Table([0])
        self.baselines = Table([0])
        self.profiles = Table([0, 1])
        self.sections = Table([0, 1])
        self.gpo_packages = Table([0, 1])
        self.recommendations = Table([0, 1])
        self.source_texts = Table([0, 1])
        self.rec_profiles = Table([0, 1, 2])
        self.rec_requirements = Table([0, 1, 2, 3, 4])
        self.settings = Table([0])
        self.aliases = Table([1, 2])
        self.rec_settings = Table([0, 1, 2])
        self.stig = Table([0, 1, 2])
        self.rec_techniques = Table([0, 1, 2])
        self.issues = []
        self.setting_by_alias = {}

    def source(self, code, title, publisher, kind, version, published, location, path, license_code, gds, current=1, superseded=None, notes=None):
        T = "src.SourceDocument"
        self.sources.add([fit(T, "Code", code), fit(T, "Title", title), fit(T, "Publisher", publisher), kind, fit(T, "Version", version), published,
                          fit(T, "Location", location), fit(T, "FileName", os.path.basename(path) if path else None),
                          sha256(path) if path and os.path.exists(path) else None, license_code, 0 if gds else 1, 1 if gds else 0, current, superseded,
                          fit(T, "Notes", notes)])

    def setting(self, key, kind, platform, name, category=None, scope=None, gpo=None, hive=None, reg_key=None, reg_value=None, reg_type=None,
                intune=None, oma=None, cmdlet=None, min_os=None, admx=None, source=None, friendly=None):
        T = "cfg.Setting"
        key = fit(T, "SettingKey", key)
        if (key,) in self.settings:
            return self.settings.keys[(fold(key),)][0]
        self.settings.add([key, kind, platform, fit(T, "Name", name), fit(T, "FriendlyName", friendly), fit(T, "CategoryPath", category), scope,
                           fit(T, "IntuneSettingDefinitionId", intune), fit(T, "OmaUri", oma), fit(T, "GpoPath", gpo), hive, fit(T, "RegistryKey", reg_key),
                           fit(T, "RegistryValueName", reg_value), reg_type, fit(T, "PowerShellCmdlet", cmdlet), fit(T, "MinOsVersion", min_os), admx, source])
        return key

    def alias(self, setting_key, kind, value, source):
        value = fit("cfg.SettingAlias", "AliasValue", value)
        if value:
            row = self.aliases.add([setting_key, kind, value, source])
            self.setting_by_alias.setdefault((kind, fold(value)), row[0])

    def resolve_setting(self, kind, value):
        return self.setting_by_alias.get((kind, fold(value))) if value else None


# ----------------------------------------------------------------------------------------------------------------------
# Microsoft Security Baselines
# ----------------------------------------------------------------------------------------------------------------------
MS_BASELINES = [
    # folder, workbook, code, name, version, platform, release date, profiles (column header -> profile code)
    ("Windows 11 v26H2 Security Baseline v2", "MS Security Baseline Windows 11 v26H2.xlsx", "MSFT-WIN11-26H2", "Microsoft Security Baseline for Windows 11 26H2",
     "26H2 v2", "WIN11", None, {"Windows 11 26H2": "WIN11", "Windows 11": "WIN11"}),
    ("Windows Server 2025 Security Baseline - 2602", "MS Security Baseline Windows Server 2025 v2602.xlsx", "MSFT-WS2025-2602",
     "Microsoft Security Baseline for Windows Server 2025", "2602", "WS2025", None, {"Member Server": "MS", "Domain Controller": "DC"}),
    ("Microsoft 365 Apps for Enterprise 2512", "Microsoft 365 Apps for enterprise 2512.xlsx", "MSFT-M365APPS-2512",
     "Microsoft Security Baseline for Microsoft 365 Apps for enterprise", "2512", "M365APPS", None, {"MSFT 365 Apps 2512": "M365APPS"}),
]
MS_PROFILE_NAMES = {"WIN11": "Windows 11 client", "MS": "Member Server", "DC": "Domain Controller", "M365APPS": "Microsoft 365 Apps"}
ADMIN_TEMPLATE_SHEETS = {"Machine": ("Computer", "HKLM"), "Computer": ("Computer", "HKLM"), "User": ("User", "HKCU")}


def parse_registry_info(text):
    """Registry values of a policy: 'HKLM\\Software\\...!ValueName' entries (a policy can set several, separated by ';'
    or new lines). Returns [(hive, key, value)], the first being the policy's main value."""
    if not text:
        return []
    entries = []
    for part in re.split(r";\s*|\n|\s+(?=(?:HKLM|HKCU|HKU|MACHINE|USER)\\)", str(text)):
        match = re.match(r"\s*(HKLM|HKCU|HKU|HKEY_LOCAL_MACHINE|HKEY_CURRENT_USER|MACHINE|USER)\\(.+)", part, re.I)
        if not match:
            continue
        hive = {"MACHINE": "HKLM", "USER": "HKCU"}.get(match.group(1).upper(), HIVES.get(match.group(1).upper(), match.group(1).upper()))
        rest = match.group(2).strip()
        if "!" in rest:
            key, value = rest.split("!", 1)
        elif ":" in rest:
            key, value = rest.rsplit(":", 1)
        else:
            key, value = rest.rsplit("\\", 1) if "\\" in rest else (rest, "")
        key, value = key.strip("\\ "), value.strip()
        if key and (hive, key.casefold(), value.casefold()) not in {(h, k.casefold(), v.casefold()) for h, k, v in entries}:
            entries.append((hive, key, value))
    return entries


def load_ms_baselines(builder, root):
    base = os.path.join(root, "Frameworks", "Microsoft Security Baselines")
    for folder, workbook, code, name, version, platform, released, columns in MS_BASELINES:
        path = os.path.join(base, folder, "Documentation", workbook)
        builder.source(code, name, "Microsoft", "Baseline", version, released, f"Frameworks/Microsoft Security Baselines/{folder}/Documentation/{workbook}", path,
                       "MICROSOFT-SCT", False)
        wb = openpyxl.load_workbook(path, read_only=True, data_only=True)
        used_profiles = {}
        recs = 0
        for sheet_index, ws in enumerate(wb.worksheets, start=1):
            rows = list(ws.iter_rows(values_only=True))
            if ws.title == "Information" or not rows:
                continue
            header = [str(cell).strip() if cell is not None else "" for cell in rows[0]]
            section = str(sheet_index)
            builder.sections.add([code, section, None, fit("cfg.BaselineSection", "Title", ws.title), None, sort_key(section)])
            value_columns = [(index, columns[title]) for index, title in enumerate(header) if title in columns]
            if not value_columns:
                continue
            for profile in dict.fromkeys(profile for _, profile in value_columns):
                used_profiles[profile] = True
            path_col = next((i for i, h in enumerate(header) if h in ("Policy Path", "Type")), None)
            name_col = next((i for i, h in enumerate(header) if h in ("Policy Setting Name", "Name")), None)
            reg_col = next((i for i, h in enumerate(header) if h.startswith("Registry Information")), None)
            support_col = next((i for i, h in enumerate(header) if h == "Supported On"), None)
            help_col = next((i for i, h in enumerate(header) if h == "Help Text"), None)
            if name_col is None:
                continue
            scope, default_hive = ADMIN_TEMPLATE_SHEETS.get(ws.title, ("Computer", None))
            sequence = 0
            for row_number, row in enumerate(rows[1:], start=2):
                setting_name = one_line(str(row[name_col])) if row[name_col] is not None else ""
                if not setting_name:
                    continue
                policy_path = one_line(str(row[path_col])) if path_col is not None and row[path_col] is not None else ""
                registries = parse_registry_info(row[reg_col]) if reg_col is not None else []
                registry = registries[0] if registries else None
                if ws.title in ADMIN_TEMPLATE_SHEETS:
                    gpo = f"{scope} Configuration\\Policies\\Administrative Templates\\{policy_path}\\{setting_name}"
                elif ws.title.startswith("Security Template"):
                    gpo = f"Computer Configuration\\Policies\\Windows Settings\\Security Settings\\{policy_path}\\{setting_name}"
                elif ws.title.startswith("Advanced Audit"):
                    gpo = f"Computer Configuration\\Policies\\Windows Settings\\Security Settings\\Advanced Audit Policy Configuration\\Audit Policies\\{policy_path}\\{setting_name}"
                elif ws.title == "Firewall":
                    gpo = f"Computer Configuration\\Policies\\Windows Settings\\Security Settings\\Windows Defender Firewall with Advanced Security\\{policy_path}\\{setting_name}"
                else:
                    gpo = f"{ws.title}\\{policy_path}\\{setting_name}" if policy_path else f"{ws.title}\\{setting_name}"
                for extra in registries:
                    KNOWN_KEYS.add(fold(extra[1]))
                if registry and registry[1]:
                    key = f"reg:{registry[0]}\\{registry[1]}!{registry[2]}"
                    setting_key = builder.setting(key, "Registry", platform, setting_name, category=policy_path, scope="User" if registry[0] == "HKCU" else "Device",
                                                  gpo=gpo, hive=registry[0], reg_key=registry[1], reg_value=registry[2],
                                                  min_os=one_line(str(row[support_col]))[:60] if support_col is not None and row[support_col] else None,
                                                  admx=1 if ws.title in ADMIN_TEMPLATE_SHEETS else 0, source=code)
                    for extra in registries:
                        builder.alias(setting_key, "REGISTRY", f"{extra[0]}\\{extra[1]}:{extra[2]}", code)
                else:
                    setting_key = builder.setting(f"gpo:{gpo}", "GPO", platform, setting_name, category=policy_path, scope="Device", gpo=gpo, source=code,
                                                  admx=0)
                builder.alias(setting_key, "GPO", gpo, code)
                values = [(profile, row[index]) for index, profile in value_columns if index < len(row) and row[index] not in (None, "")]
                if not values:
                    continue
                sequence += 1
                rec_number = f"{sheet_index}.{sequence}"
                recommended = one_line(str(values[0][1]))
                recs += 1
                builder.recommendations.add([
                    code, rec_number, section, None, fit("cfg.Recommendation", "Title", f"{setting_name}: {recommended}" if recommended else setting_name), "Automated",
                    fit("cfg.Recommendation", "CategoryText", ws.title), None,
                    fit("cfg.Recommendation", "Summary", first_sentence(row[help_col]) if help_col is not None and row[help_col] else None), None, None, None,
                    fit("cfg.Recommendation", "RecommendedValue", recommended), None, None,
                    fit("cfg.Recommendation", "MinimumOsVersion", one_line(str(row[support_col])) if support_col is not None and row[support_col] else None),
                    "Registry" if registry else "GPO", 0, code, None, fit("cfg.Recommendation", "SourceRowRef", f"{ws.title}!{row_number}")])
                for profile, value in values:
                    builder.rec_profiles.add([code, rec_number, profile, fit("cfg.RecommendationProfile", "RecommendedValue", one_line(str(value)))])
                builder.rec_settings.add([code, rec_number, setting_key, fit("cfg.RecommendationSetting", "ExpectedValue", recommended), "Equals", None, 1])
        for profile in used_profiles:
            builder.profiles.add([code, profile, MS_PROFILE_NAMES.get(profile, profile), None, "MemberServer" if profile == "MS" else "DomainController" if profile == "DC" else None, 0,
                                  None])
        builder.baselines.add([code, "MSFT-SCT", platform, name, version, released, "GPO", None, 1, 0, None, code, recs,
                               f"Workbook {workbook}; every Group Policy setting is in cfg.Setting, the configured ones are recommendations."])
        # GPO backups shipped with the baseline.
        manifest = os.path.join(base, folder, "GPOs", "manifest.xml")
        if os.path.exists(manifest):
            ns = {"m": "http://www.microsoft.com/GroupPolicy/GPOOperations/Manifest"}
            for backup in ET.parse(manifest).getroot().findall("m:BackupInst", ns):
                backup_id = backup.find("m:ID", ns).text.strip("{}").lower()
                guid = backup.find("m:GPOGuid", ns).text.strip("{}").lower()
                display = backup.find("m:GPODisplayName", ns).text
                scope = "User" if display.rstrip().endswith("User") else "Computer"
                builder.gpo_packages.add([code, backup_id, guid, fit("cfg.GpoPackage", "DisplayName", display), scope, f"GPOs/{{{backup_id.upper()}}}/gpreport.xml"])


# ----------------------------------------------------------------------------------------------------------------------
# CIS Benchmarks
# ----------------------------------------------------------------------------------------------------------------------
def load_cis(builder, root, pdf_text, requirements, techniques, mitigations):
    folder = os.path.join(root, "Frameworks", "CIS Benchmarks CSV")
    seen_pdf_hashes = {}
    stats = collections.Counter()
    for path in sorted(glob.glob(os.path.join(folder, "CIS_*.csv"))):
        if any(token in path for token in ("Column_Dictionary", "Index")):
            continue
        rows = list(csv.DictReader(open(path, encoding="utf-8-sig")))
        if not rows:
            continue
        pdf_name = os.path.splitext(rows[0]["SourceFileName"])[0]
        if rows[0]["SourceFileSha256"] in seen_pdf_hashes:
            stats["duplicate files skipped"] += 1
            continue
        seen_pdf_hashes[rows[0]["SourceFileSha256"]] = pdf_name
        if pdf_name not in CIS_BASELINES:
            print(f"warning: no baseline code for {pdf_name}", file=sys.stderr)
            continue
        code, platform, delivery, archived, superseded = CIS_BASELINES[pdf_name]
        first = rows[0]
        builder.sources.add([code, fit("src.SourceDocument", "Title", first["BaselineName"]), "Center for Internet Security", "Benchmark", first["BaselineVersion"],
                             first["BaselineReleaseDate"] or None, f"Frameworks/CIS Benchmarks/{first['SourceFileName']}", first["SourceFileName"],
                             first["SourceFileSha256"], "CIS-BENCHMARK-TERMS", 1, 0, 0 if superseded or archived else 1, superseded,
                             f"Extracted to Frameworks/CIS Benchmarks CSV/{os.path.basename(path)} (parser {first['ParserVersion']}); checked against the PDF."])

        # Recommendation numbers and registry paths are checked against the PDF text.
        pdf_numbers = set()
        text_path = os.path.join(pdf_text, pdf_name + ".txt") if pdf_text else None
        pdf_flat = ""
        if text_path and os.path.exists(text_path):
            lines = open(text_path, encoding="utf-8", errors="replace").read().splitlines()
            pdf_flat = re.sub(r"\s+", "", "\n".join(lines)).casefold()
            for index, line in enumerate(lines):
                if "Profile Applicability:" in line:
                    for back in range(index - 1, max(index - 12, 0), -1):
                        match = re.match(r"^\s*(\d+(?:\.\d+)+)\s+\S", lines[back])
                        if match:
                            pdf_numbers.add(match.group(1))
                            break
        csv_numbers = {row["RecNumber"] for row in rows}
        missing = sorted(pdf_numbers - csv_numbers)
        used_profiles = {}
        sections = {}
        recs = 0
        previous = None
        for row in rows:
            number = row["RecNumber"].strip()
            if pdf_numbers and number not in pdf_numbers:
                # The extractor took a number from the title; the PDF heading order gives the real one.
                prefix = row["SectionNumber"].strip() or (previous.rsplit(".", 1)[0] if previous and "." in previous else "~")
                fixed = next((candidate for candidate in missing if candidate.startswith(prefix + ".")), None) or (missing[0] if len(missing) == 1 else None)
                if fixed:
                    heading = pdf_heading(lines, fixed)
                    if heading:
                        row = dict(row, Title=heading)
                    builder.issues.append(f"{code} {number} -> {fixed}: recommendation number (and title) corrected from the PDF heading")
                    missing.remove(fixed)
                    number = fixed
                    stats["recommendation numbers corrected"] += 1
            previous = number
            # Section tree (immediate parent section plus every ancestor in the section path).
            path_parts = [part.strip() for part in (row["SectionPath"] or "").split(" > ") if part.strip()]
            for part in path_parts:
                match = re.match(r"(\d+(?:\.\d+)*)\s+(.*)", part)
                if match and match.group(1) not in sections:
                    parent = match.group(1).rsplit(".", 1)[0] if "." in match.group(1) else None
                    sections[match.group(1)] = [code, match.group(1), parent, fit("cfg.BaselineSection", "Title", match.group(2)), None, sort_key(match.group(1))]
            section = row["SectionNumber"].strip() or None
            if section and section not in sections:
                sections[section] = [code, section, row["ParentSectionNumber"].strip() or None, fit("cfg.BaselineSection", "Title", row["SectionTitle"]),
                                     None, sort_key(section)]
            if section and row["SectionDescription"].strip() and sections[section][4] is None:
                sections[section][4] = fit("cfg.BaselineSection", "Description", first_sentence(row["SectionDescription"], 500))
            recs += 1
            title = re.sub(r"^\((L\d|BL|NG)\)\s*", "", one_line(row["Title"]))
            builder.recommendations.add([
                code, number, section, None, fit("cfg.Recommendation", "Title", title), row["AssessmentTypeCode"] or None,
                fit("cfg.Recommendation", "CategoryText", row["CategoryText"]), fit("cfg.Recommendation", "LevelText", row["LevelText"].replace("|", ", ")),
                fit("cfg.Recommendation", "Summary", first_sentence(row["DescriptionText"] or row["Summary"])),
                fit("cfg.Recommendation", "RationaleSummary", first_sentence(row["RationaleText"] or row["RationaleSummary"])),
                fit("cfg.Recommendation", "ImpactSummary", first_sentence(row["ImpactText"] or row["ImpactSummary"])),
                fit("cfg.Recommendation", "DefaultValue", one_line(row["DefaultValue"])), fit("cfg.Recommendation", "RecommendedValue", one_line(row["RecommendedValue"])),
                fit("cfg.Recommendation", "GridId", row["GridId"]), fit("cfg.Recommendation", "ExternalId", f"{code}|{number}"),
                fit("cfg.Recommendation", "MinimumOsVersion", row["MinimumOsVersion"]),
                fit("cfg.Recommendation", "HowToVerifyCode", (row["AuditMethodCodes"] or "").split("|")[0].strip() or None),
                1 if row["IsDeprecated"] == "1" else 0, code, int(row["PageNumber"]) if row["PageNumber"].strip().isdigit() else None,
                fit("cfg.Recommendation", "SourceRowRef", f"{os.path.basename(path)}#{row['RowId']}")])
            builder.source_texts.add([code, number, row["DescriptionText"] or None, row["RationaleText"] or None, row["ImpactText"] or None, row["AuditText"] or None,
                                      row["RemediationText"] or None, row["AdditionalInfoText"] or None, row["WarningText"] or None, row["ReferencesText"] or None,
                                      "CIS-BENCHMARK-TERMS", 1])
            for profile_name in filter(None, (part.strip() for part in (row["ProfileApplicability"] or "").split("|"))):
                profile = profile_code(profile_name, used_profiles)
                level = re.search(r"level\s*(\d)", profile_name, re.I)
                qualifier = next((q for needle, q in QUALIFIERS if needle in profile_name.lower()), None)
                builder.profiles.add([code, profile, fit("cfg.BaselineProfile", "Name", profile_name), int(level.group(1)) if level else None, qualifier,
                                      1 if qualifier in ("BitLocker", "NextGeneration", "STIG") else 0, None])
                builder.rec_profiles.add([code, number, profile, None])

            # Framework mappings printed in the benchmark.
            for safeguard in filter(None, (part.strip() for part in (row["CisControlsV8"] or "").split("|"))):
                if ("general-cis-csc-8-1", safeguard) in requirements:
                    builder.rec_requirements.add([code, number, "general-cis-csc-8-1", safeguard, "CIS-BENCHMARK", "RelatedTo", "v8"])
                    stats["CIS Controls v8 links"] += 1
                else:
                    stats["CIS Controls v8 safeguards not in the knowledge base"] += 1
            for reference in filter(None, (part.strip() for part in re.split(r"[|;,]", row["NistSp80053Refs"] or ""))):
                nist = nist_code(reference)
                if nist and ("general-nist-800-53-r5-2", nist) in requirements:
                    builder.rec_requirements.add([code, number, "general-nist-800-53-r5-2", nist, "CIS-BENCHMARK", "RelatedTo", "R5"])
                    stats["NIST SP 800-53 links"] += 1
            for reference in filter(None, (part.strip() for part in re.split(r"[|;,]", row["NistCsfRefs"] or ""))):
                if ("general-nist-csf-2-0", reference) in requirements:
                    builder.rec_requirements.add([code, number, "general-nist-csf-2-0", reference, "CIS-BENCHMARK", "RelatedTo", "2.0"])
                    stats["NIST CSF 2.0 links"] += 1
            mitigation = next((m for m in (row["MitreMitigations"] or "").split("|") if m.strip() in mitigations), None)
            for technique in filter(None, (part.strip() for part in (row["MitreTechniques"] or "").split("|"))):
                if technique in techniques:
                    builder.rec_techniques.add([code, number, technique, mitigation.strip() if mitigation else None])

            # DISA STIG references (STIG benchmarks).
            stig_ids = [part.strip() for part in (row["StigId"] or "").split("|") if part.strip()]
            vul_ids = [part.strip() for part in (row["StigVulId"] or "").split("|")]
            rule_ids = [part.strip() for part in (row["StigRuleId"] or "").split("|")]
            for index, stig_id in enumerate(stig_ids):
                builder.stig.add([code, number, fit("cfg.StigReference", "StigId", stig_id), fit("cfg.StigReference", "VulId", vul_ids[index] if index < len(vul_ids) else None),
                                  fit("cfg.StigReference", "RuleId", rule_ids[index] if index < len(rule_ids) else None),
                                  int(row["StigSeverityCat"]) if row["StigSeverityCat"].strip().isdigit() else None, fit("cfg.StigReference", "StigTitle", row["StigTitle"]),
                                  fit("cfg.StigReference", "StigVersion", row["StigVersion"]), fit("cfg.StigReference", "StigRelease", row["StigRelease"]),
                                  row["StigBenchmarkDate"] or None])

            # The technical setting: registry (verified against the PDF; re-read from the audit text when the path was mangled),
            # else the Settings Catalog path, OMA-URI or Group Policy path.
            reg = None
            parsed = registry_from_text(row["AuditText"]) or registry_from_text(row["RemediationText"]) if REGISTRY.search((row["AuditText"] or "") + (row["RemediationText"] or "")) else None
            extracted = (row["RegistryHive"].strip() or "HKLM", row["RegistryKey"].strip(), row["RegistryValueName"].strip()) if row["RegistryPath"].strip() else None
            if parsed and extracted:
                parsed = merge_registry(parsed, extracted)
            if parsed:
                if extracted and (fold(extracted[1]), fold(extracted[2])) != (fold(parsed[1]), fold(parsed[2])):
                    builder.issues.append(f"{code} {number}: registry '{extracted[1]}:{extracted[2]}' corrected to '{parsed[1]}:{parsed[2]}' from the audit text")
                    stats["registry paths corrected"] += 1
                reg = parsed
            elif extracted and extracted[1]:
                reg = (HIVES.get(extracted[0].upper(), extracted[0].upper()), extracted[1], extracted[2])
                stats["registry paths taken as extracted (no path in the audit text)"] += 1
            value_type = VALUE_TYPE.search(row["AuditText"] or "")
            gpo = one_line(row["GpoPath"]) or None
            catalog_path = one_line(row["SettingsCatalogPath"]) or None
            oma = one_line(row["OmaUri"]) or None
            setting_name = re.sub(r"^\(?(L\d|BL|NG)\)?\s*", "", re.sub(r"\s*\((Automated|Manual)\)\s*$", "", title))
            setting_key = None
            if reg and reg[1]:
                setting_key = builder.resolve_setting("REGISTRY", f"{reg[0]}\\{reg[1]}:{reg[2]}") or builder.setting(
                    f"reg:{reg[0]}\\{reg[1]}!{reg[2]}", "Registry", platform, setting_name, category=row["SectionPath"], scope="User" if reg[0] == "HKCU" else "Device",
                    gpo=gpo, hive=reg[0], reg_key=reg[1], reg_value=reg[2], reg_type=value_type.group(1) if value_type else None, oma=oma,
                    min_os=row["MinimumOsVersion"] or None, source=code, admx=1 if gpo and "Administrative Templates" in gpo else 0)
                builder.alias(setting_key, "REGISTRY", f"{reg[0]}\\{reg[1]}:{reg[2]}", code)
            elif catalog_path:
                setting_key = builder.resolve_setting("SETTINGS_CATALOG", catalog_path) or builder.setting(
                    f"intune:{catalog_path}", "Intune", platform, setting_name, category=catalog_path, scope="Device", oma=oma, source=code)
            elif oma:
                setting_key = builder.resolve_setting("OMA_URI", oma) or builder.setting(f"omauri:{oma}", "OmaUri", platform, setting_name, scope="Device", oma=oma, source=code)
            elif gpo:
                setting_key = builder.resolve_setting("GPO", gpo) or builder.setting(f"gpo:{gpo}", "GPO", platform, setting_name, scope="Device", gpo=gpo, source=code)
            if setting_key:
                if gpo:
                    builder.alias(setting_key, "GPO", gpo, code)
                if catalog_path:
                    builder.alias(setting_key, "SETTINGS_CATALOG", catalog_path, code)
                if oma:
                    builder.alias(setting_key, "OMA_URI", oma, code)
                if row["GridId"].strip():
                    builder.alias(setting_key, "GRID", row["GridId"].strip(), code)
                expected = one_line(row["RecommendedValue"]) or None
                operator = "AtLeast" if expected and re.search(r"\bor more\b|\bor greater\b|at least", expected, re.I) else \
                    "AtMost" if expected and re.search(r"\bor (fewer|less)\b|at most", expected, re.I) else "Equals"
                builder.rec_settings.add([code, number, setting_key, fit("cfg.RecommendationSetting", "ExpectedValue", expected), operator, None, 1])
                stats["recommendations with a technical setting"] += 1
        for section in sections.values():
            builder.sections.add(section)
        released = first["BaselineReleaseDate"] or None
        builder.baselines.add([code, "CIS", platform, fit("cfg.Baseline", "Name", first["BaselineName"]), first["BaselineVersion"], released, delivery,
                               "Commercial" if platform in CLOUD_PLATFORMS else None, 0 if (superseded or archived) else 1, archived, superseded, code, recs,
                               fit("cfg.Baseline", "Notes", f"{recs} recommendations from {first['SourceFileName']}.")])
        stats["recommendations"] += recs
    return stats


# ----------------------------------------------------------------------------------------------------------------------
# GDS delivery content: phases, workstreams, WBS, discovery decisions
# ----------------------------------------------------------------------------------------------------------------------
def phase_code(name):
    match = re.match(r"Phase\s+(\d+)", name or "")
    return f"P{match.group(1)}" if match else None


def workstream_code(name):
    return re.sub(r"[^A-Z0-9]+", "-", (name or "").upper().replace("&", "AND")).strip("-")[:40]


def load_gds(root):
    path = os.path.join(root, "SecurePlusStandards.xlsx")
    wb = openpyxl.load_workbook(path, read_only=True, data_only=True)
    phases, workstreams = {}, {}

    def phase(name):
        code = phase_code(name)
        if code and code not in phases:
            phases[code] = ["SECUREPLUS", code, fit("dlv.Phase", "Name", one_line(name)), int(code[1:]), None]
        return code

    def workstream(name):
        if not name:
            return None
        code = workstream_code(name)
        workstreams.setdefault(code, [code, fit("dlv.Workstream", "Name", one_line(name)), None])
        return code

    wbs_rows = list(wb["WBS_WIP"].iter_rows(values_only=True))
    header = list(wbs_rows[0])
    col = {name: index for index, name in enumerate(header) if name}
    tasks, coverage, outline_counter = [], [], collections.Counter()
    deliverables = {}
    T = "dlv.WbsTask"
    for row in wbs_rows[1:]:
        if not row[col["Baseline ID"]]:
            continue
        task_code = str(row[col["Baseline ID"]]).strip()
        task_num = int(row[col["Task Num"]]) if isinstance(row[col["Task Num"]], (int, float)) else row[col["Task Num"]]
        outline_counter[task_num] += 1
        outline = f"{task_num}.{outline_counter[task_num]}"

        def num(name):
            value = row[col[name]]
            return float(value) if isinstance(value, (int, float)) else None

        low, high = num("Est Hours Min"), num("Est Hours Max")
        deliverable = one_line(str(row[col["Deliverable"]] or "")) or None
        if deliverable:
            deliverables.setdefault(fold(deliverable), task_code)
        tasks.append([
            "SP-WBS", task_code, None, outline, phase(row[col["Phase"]]), workstream(row[col["Workstream"]]), fit(T, "Bucket", one_line(row[col["Workstream"]])),
            fit(T, "TaskName", one_line(str(row[col["Task Name"]]))), fit(T, "SubTaskName", one_line(str(row[col["Sub-Task Name"]] or ""))),
            fit(T, "GroupAbbrev", row[col["Group Abbreviation"]]), fit(T, "ControlCode", row[col["Control"]]),
            fit(T, "ControlDescription", one_line(str(row[col["Control Description"]] or ""))), fit(T, "Deliverable", deliverable),
            fit(T, "DependencyText", one_line(str(row[col["Dependency"]] or ""))), fit(T, "AppliesWhen", one_line(str(row[col["Applies When"]] or ""))),
            fit(T, "LicenseRequirementText", one_line(str(row[col["License Requirement"]] or ""))), low, high, round((low + high) / 2, 1) if low is not None and high is not None else None,
            num("Calendar Duration Min (Days)"), num("Calendar Duration Max (Days)"),
            1 if str(row[col["Parallelizable?"]]).strip().lower() == "yes" else 0, 1 if str(row[col["Support Handoff?"]]).strip().lower() == "yes" else 0,
            fit(T, "ComplexityDriver", one_line(str(row[col["Complexity Driver"]] or ""))), fit(T, "LeadershipNotes", one_line(str(row[col["Leadership Notes"]] or ""))),
            sort_key(f"{task_num}.{outline_counter[task_num]}")])
        coverage.append(["SP-WBS", task_code, one_line(str(row[col["Phase-Group-Number Reference"]]))])
    dependencies = []
    for task in tasks:
        predecessor = deliverables.get(fold(task[13])) if task[13] else None
        if predecessor and predecessor != task[1]:
            dependencies.append(["SP-WBS", task[1], predecessor])

    # Discovery decisions -> question bank.
    decision_rows = list(wb["Discovery_Decisions"].iter_rows(values_only=True))
    dcol = {name: index for index, name in enumerate(decision_rows[0]) if name}
    questions, links = [], []
    Q = "eux.Question"
    for order, row in enumerate(decision_rows[1:], start=1):
        if not row[dcol["Decision ID"]]:
            continue
        code = str(row[dcol["Decision ID"]]).strip()

        def text(name, limit_column):
            return fit(Q, limit_column, one_line(str(row[dcol[name]] or "")))

        answer_type = one_line(str(row[dcol["Expected Answer Type"]] or ""))
        options_text = one_line(str(row[dcol["Suggested Answer Options or Examples"]] or ""))
        if len(answer_type) > LENGTHS.get("eux.Question.AnswerTypeCode", 40):
            options_text = f"Expected answer: {answer_type}. {options_text}".strip()
        questions.append(["SP-DISCOVERY", code, None, "SECUREPLUS", phase(row[dcol["Phase"]]), text("Decision Category", "CategoryText"), text("Decision or Topic", "Topic"),
                          text("Customer Question", "QuestionText"), text("Why This Must Be Asked", "WhyAsked"), fit(Q, "AnswerTypeCode", answer_type),
                          fit(Q, "SuggestedOptionsText", options_text), text("Configuration Impact", "ConfigurationImpact"),
                          text("Customer Decision Owner", "DecisionOwnerRole"), text("Required Participants", "RequiredParticipants"), text("Required By", "RequiredBy"),
                          text("Evidence Required", "EvidenceRequired"), text("Default If Undecided", "DefaultIfUndecided"), text("Engineering Notes", "EngineeringNotes"), order])
        links.append([code, one_line(str(row[dcol["Phase-Group-Number Reference"]] or ""))])
    source = ["GDS-SECUREPLUS-STANDARDS", "Secure Plus Standards workbook (standards baseline, WBS, discovery decisions)", "Global Data Systems", "Workbook", None, None,
              "SecurePlusStandards.xlsx", "SecurePlusStandards.xlsx", sha256(path), "GDS-INTERNAL", 0, 1, 1, None, "Sheets WBS_WIP and Discovery_Decisions."]
    return phases, workstreams, tasks, dependencies, coverage, questions, links, source


# ----------------------------------------------------------------------------------------------------------------------
# EaaS question bank (forms, rules, personas)
# ----------------------------------------------------------------------------------------------------------------------
ANSWER_TYPES = {"radiogroup": "SingleChoice", "checkbox": "MultipleChoice", "text": "Text", "comment": "LongText", "number": "Number", "matrixdropdown": "Matrix"}


def load_question_bank(root):
    path = os.path.join(root, "EaaS", "Questionnaire", "GDS_EaaS_Question_Bank.json")
    bank = json.load(open(path, encoding="utf-8"))
    modules, questions, options, rules, personas = [], [], [], [], []
    order = 0
    for form in bank["forms"]:
        for section in form["sections"]:
            order += 1
            modules.append([fit("eux.QuestionModule", "Code", section["id"]), fit("eux.QuestionModule", "Name", f"{form['title']}: {section['title']}"),
                            fit("eux.QuestionModule", "Description", section.get("description")), order])
    Q = "eux.Question"
    for order, question in enumerate(bank["questions"], start=1):
        why = "; ".join(filter(None, [question.get("drives"), question.get("eux")])) or None
        questions.append(["EAAS-FORMS", question["id"], question.get("sec"), None, None, fit(Q, "CategoryText", question.get("section_title")),
                          fit(Q, "Topic", question.get("form")), fit(Q, "QuestionText", one_line(question["text"])), fit(Q, "WhyAsked", why),
                          ANSWER_TYPES.get(question["type"], question["type"]),
                          fit(Q, "SuggestedOptionsText", "; ".join(choice.get("text", "") for choice in question.get("choices", [])) or None),
                          fit(Q, "ConfigurationImpact", "Configures: " + ", ".join(question["maps"]) if question.get("maps") else None),
                          fit(Q, "DecisionOwnerRole", question.get("skip_owner")), None, None, None, fit(Q, "DefaultIfUndecided", question.get("skip_default")),
                          fit(Q, "EngineeringNotes", ("Visible when " + question["visibleIf"]) if question.get("visibleIf") else None), order])
        for sequence, choice in enumerate(question.get("choices", []), start=1):
            options.append([question["id"], sequence, fit("eux.AnswerOption", "OptionText", choice.get("text")), fit("eux.AnswerOption", "OptionValue", choice.get("value")),
                            1 if question.get("skip_default") == choice.get("value") else 0])
    for priority, rule in enumerate(bank["rules"], start=1):
        actions = "; ".join(f"{action['action']} {action.get('target', '')}{(' = ' + str(action['value'])) if 'value' in action else ''}" for action in rule["then"])
        rules.append([None, fit("eux.QuestionRule", "ConditionExpression", rule["when"]), priority,
                      fit("eux.QuestionRule", "Notes", f"{rule['id']} ({rule['category']}): {actions}. {rule.get('rationale', '')}")])
    for persona in bank["personas"]:
        name = persona["persona"]
        personas.append([fit("eux.Persona", "Code", persona["value"]), fit("eux.Persona", "Name", name), fit("eux.Persona", "Description", persona.get("policy_profile")),
                         1 if re.search(r"admin|privileged|it ", name, re.I) else 0, 1 if re.search(r"guest|contractor|vendor|partner|external", name, re.I) else 0])
    source = ["GDS-EAAS-QUESTION-BANK", "EaaS question bank (Form 1 discovery, Form 2 design workshop)", "Global Data Systems", "Dataset", bank.get("version"),
              bank.get("generated"), "EaaS/Questionnaire/GDS_EaaS_Question_Bank.json", "GDS_EaaS_Question_Bank.json", sha256(path), "GDS-INTERNAL", 0, 1, 1, None,
              f"{len(bank['questions'])} questions, {len(bank['rules'])} rules, {len(bank['personas'])} personas."]
    return modules, questions, options, rules, personas, source


# ----------------------------------------------------------------------------------------------------------------------
# Deployment method matrix
# ----------------------------------------------------------------------------------------------------------------------
METHODS = [
    # code, name, auth model, permission notes, supports drift read, match (lower-case substrings of PrimaryMethod)
    ["GRAPH", "Microsoft Graph REST (JSON)", "AppOnly", "Application permissions per resource; most write operations need *.ReadWrite.All.", 1, ["graph"]],
    ["EXO-POWERSHELL", "Exchange Online PowerShell", "AppOnly", "Exchange.ManageAsApp plus an Entra role.", 1, ["exchange", "connect-exchangeonline"]],
    ["SCC-POWERSHELL", "Security & Compliance PowerShell", "AppOnly", "Certificate-based app-only with the Compliance Administrator role.", 1, ["security & compliance", "purview", "ippssession"]],
    ["TEAMS-POWERSHELL", "Microsoft Teams PowerShell", "AppOnly", "Application permissions for the cmdlets that support them; some need delegated.", 1, ["microsoftteams", "teams"]],
    ["SPO-POWERSHELL", "SharePoint Online / PnP PowerShell", "AppOnly", "Sites.FullControl.All or SharePoint admin role.", 1, ["sharepoint", "pnp", "spo"]],
    ["DEFENDER-API", "Defender / Security APIs", "AppOnly", "Defender for Endpoint or XDR API application permissions.", 1, ["defender", "security center", "mdca", "securitycenter"]],
    ["INTUNE", "Intune (Graph deviceManagement)", "AppOnly", "DeviceManagement*.ReadWrite.All.", 1, ["intune"]],
    ["ARM", "Azure Resource Manager / Bicep", "AppOnly", "Azure RBAC role on the subscription or resource group.", 1, ["arm", "bicep", "azure"]],
    ["POWER-PLATFORM", "Power Platform admin API / PowerShell", "Delegated", "Power Platform administrator.", 1, ["power"]],
    ["ENTRA-CONNECT", "Microsoft Entra Connect (on-premises)", "OnPremises", "Runs on the Entra Connect server.", 0, ["adsync", "entra connect"]],
    ["PSA-API", "ConnectWise PSA REST API", "ApiKey", "Integrator API member with the needed security role.", 1, ["cw", "connectwise"]],
    ["SAAS-API", "Third-party SaaS API", "ApiKey", "Vendor API key or OAuth client.", 1, ["saas", "keeper", "acronis", "exclaimer"]],
    ["FORMS", "Microsoft Forms (manual or Graph export)", "Delegated", "Forms owner.", 0, ["forms"]],
    ["ATTESTATION", "Portal only (attestation)", "Manual", "An engineer configures the setting in the portal and records evidence.", 0, ["manual", "portal"]],
]
PRODUCTS = {
    # service in the matrix -> product code, family, admin centre, url
    "Microsoft Entra ID": ("ENTRA", "Identity", "Microsoft Entra admin center", "https://entra.microsoft.com"),
    "Microsoft Entra Tenant Governance": ("ENTRA", "Identity", "Microsoft Entra admin center", "https://entra.microsoft.com"),
    "Microsoft Intune": ("INTUNE", "Endpoint", "Microsoft Intune admin center", "https://intune.microsoft.com"),
    "Windows Autopatch": ("INTUNE", "Endpoint", "Microsoft Intune admin center", "https://intune.microsoft.com"),
    "Windows 365": ("W365", "Endpoint", "Microsoft Intune admin center", "https://intune.microsoft.com"),
    "Microsoft Purview": ("PURVIEW", "Compliance", "Microsoft Purview portal", "https://purview.microsoft.com"),
    "Exchange Online": ("EXO", "Collaboration", "Exchange admin center", "https://admin.exchange.microsoft.com"),
    "SharePoint Online and OneDrive": ("SPO", "Collaboration", "SharePoint admin center", None),
    "Microsoft Teams": ("TEAMS", "Collaboration", "Microsoft Teams admin center", "https://admin.teams.microsoft.com"),
    "Teams Phone": ("TEAMS", "Collaboration", "Microsoft Teams admin center", "https://admin.teams.microsoft.com"),
    "Teams Rooms": ("TEAMS", "Collaboration", "Microsoft Teams admin center", "https://admin.teams.microsoft.com"),
    "Defender for Endpoint / Business": ("MDE", "Security", "Microsoft Defender portal", "https://security.microsoft.com"),
    "Defender XDR": ("DEFENDER-XDR", "Security", "Microsoft Defender portal", "https://security.microsoft.com"),
    "Defender for Office 365": ("MDO", "Security", "Microsoft Defender portal", "https://security.microsoft.com"),
    "Defender for Identity": ("MDI", "Security", "Microsoft Defender portal", "https://security.microsoft.com"),
    "Defender for Cloud Apps": ("MDCA", "Security", "Microsoft Defender portal", "https://security.microsoft.com"),
    "Microsoft Sentinel": ("SENTINEL", "Security", "Microsoft Defender portal", "https://security.microsoft.com"),
    "Microsoft 365 admin center": ("M365-ADMIN", "Productivity", "Microsoft 365 admin center", "https://admin.microsoft.com"),
    "Microsoft 365 Apps admin center": ("M365APPS", "Productivity", "Microsoft 365 Apps admin center", "https://config.office.com"),
    "Microsoft Forms": ("FORMS", "Productivity", "Microsoft Forms", "https://forms.office.com"),
    "Microsoft Bookings": ("BOOKINGS", "Productivity", "Microsoft 365 admin center", "https://admin.microsoft.com"),
    "Microsoft Viva": ("VIVA", "Productivity", "Microsoft 365 admin center", "https://admin.microsoft.com"),
    "Power Platform": ("POWERPLATFORM", "Productivity", "Power Platform admin center", "https://admin.powerplatform.microsoft.com"),
    "Copilot Studio / Power Platform": ("COPILOT-STUDIO", "AI", "Power Platform admin center", "https://admin.powerplatform.microsoft.com"),
    "Microsoft 365 Copilot": ("COPILOT", "AI", "Microsoft 365 admin center", "https://admin.microsoft.com"),
    "Microsoft 365 Copilot / Agent 365": ("COPILOT", "AI", "Microsoft 365 admin center", "https://admin.microsoft.com"),
    "Microsoft Graph": ("GRAPH", "Cloud", None, "https://graph.microsoft.com"),
    "Microsoft Graph TCM": ("GRAPH", "Cloud", None, "https://graph.microsoft.com"),
    "Microsoft 365 Backup": ("M365-BACKUP", "Productivity", "Microsoft 365 admin center", "https://admin.microsoft.com"),
    "ConnectWise PSA": ("CW-PSA", "Operations", "ConnectWise Manage", None),
}
PRODUCT_NAMES = {"ENTRA": "Microsoft Entra ID", "INTUNE": "Microsoft Intune", "W365": "Windows 365", "PURVIEW": "Microsoft Purview", "EXO": "Exchange Online",
                 "SPO": "SharePoint Online and OneDrive", "TEAMS": "Microsoft Teams", "MDE": "Microsoft Defender for Endpoint", "DEFENDER-XDR": "Microsoft Defender XDR",
                 "MDO": "Microsoft Defender for Office 365", "MDI": "Microsoft Defender for Identity", "MDCA": "Microsoft Defender for Cloud Apps",
                 "SENTINEL": "Microsoft Sentinel", "M365-ADMIN": "Microsoft 365 admin center", "M365APPS": "Microsoft 365 Apps", "FORMS": "Microsoft Forms",
                 "BOOKINGS": "Microsoft Bookings", "VIVA": "Microsoft Viva", "POWERPLATFORM": "Microsoft Power Platform", "COPILOT-STUDIO": "Microsoft Copilot Studio",
                 "COPILOT": "Microsoft 365 Copilot", "GRAPH": "Microsoft Graph", "M365-BACKUP": "Microsoft 365 Backup", "CW-PSA": "ConnectWise PSA"}


def yes_no(value):
    text = (value or "").strip().lower()
    return 1 if text.startswith("yes") else 0 if text.startswith("no") else None


def load_method_matrix(root):
    path = os.path.join(root, "EaaS", "M365_Azure_Config_Method_Matrix.csv")
    rows = list(csv.DictReader(open(path, encoding="utf-8-sig"), delimiter="|"))
    products, coverage = {}, Table([1])
    for row in rows:
        service = row["Service"].strip()
        product = PRODUCTS.get(service)
        if product:
            code, family, admin, url = product
            name = PRODUCT_NAMES.get(code, service)
        else:
            code = workstream_code(service)[:50]
            family, admin, url, name = ("Operations" if row["Area"] in ("PSA", "MSP Access", "Third-party", "Backup") else "Cloud"), None, None, service
        vendor = "ConnectWise" if code == "CW-PSA" else "Microsoft" if service.startswith(("Microsoft", "Defender", "Exchange", "SharePoint", "Teams", "Windows", "Power", "Copilot")) \
            else None
        products.setdefault(code, [code, fit("kb.Product", "Name", name), vendor, family, admin, url, None])
        primary = row["PrimaryMethod"].lower()
        method = next((m[0] for m in METHODS if any(token in primary for token in m[5])), "ATTESTATION")
        json_native = (row["JsonNative"] or "").split(" ")[0].strip() or None
        T = "deploy.ServiceMethodCoverage"
        access = (row["ReadWrite"] or "").strip()
        lowered = access.lower()
        access_code = "ReadWrite" if lowered.startswith("read+write") else "ReadOnly" if lowered.startswith("read") else "Write" if lowered.startswith("write") \
            else "Manual" if lowered.startswith(("manual", "setup", "create")) else "NotApplicable" if lowered.startswith("n/a") else "Unverified" if lowered else None
        notes = [f"Access: {access}" if access and access_code and access != access_code.replace("ReadWrite", "Read+Write") else None,
                 f"Application permission: {row['AppOnlyUnattended'].strip()}" if "(" in (row["AppOnlyUnattended"] or "") else None,
                 row["PortalOnlyGapExamples"], row["Notes"]]
        coverage.add([code, fit(T, "SettingGroup", f"{service}: {row['SettingGroup'].strip()}"), method, fit(T, "JsonNativeCode", json_native),
                      yes_no(row["AppOnlyUnattended"]), access_code,
                      fit(T, "PortalOnlyGaps", one_line("; ".join(filter(None, notes)))),
                      fit(T, "DriftOption", row["DriftMonitoringOption"]), fit(T, "RepoStatusCode", None), fit(T, "SourceUrl", row["SourceUrl"]),
                      fit(T, "ConfidenceCode", (row["Confidence"] or "").upper() or None)])
    methods = [[m[0], m[1], m[2], m[3], m[4]] for m in METHODS]
    source = ["GDS-CONFIG-METHOD-MATRIX", "Microsoft 365 and Azure configuration method matrix", "Global Data Systems", "Workbook", None, None,
              "EaaS/M365_Azure_Config_Method_Matrix.csv", "M365_Azure_Config_Method_Matrix.csv", sha256(path), "GDS-INTERNAL", 0, 1, 1, None,
              f"{len(rows)} setting groups."]
    return sorted(products.values()), methods, coverage.rows, source


# Target column of each position of each output dataset ("table:column"; None = a code resolved by the loader).
COLUMNS = {
    "licenses": ["src.License:Code", "src.License:Name", "src.License:Url", None, None, None, None, "src.License:AttributionText", "src.License:Notes"],
    "sources": ["src.SourceDocument:Code", "src.SourceDocument:Title", "src.SourceDocument:Publisher", "src.SourceDocument:DocumentTypeCode", "src.SourceDocument:Version",
                None, "src.SourceDocument:Location", "src.SourceDocument:FileName", "src.SourceDocument:FileHashSha256", None, None, None, None, None, "src.SourceDocument:Notes"],
    "platforms": ["cfg.Platform:Code", "cfg.Platform:Name", "cfg.Platform:PlatformFamilyCode", "cfg.Platform:Vendor", "cfg.Platform:VersionLabel", None],
    "baselines": ["cfg.Baseline:Code", None, None, "cfg.Baseline:Name", "cfg.Baseline:Version", None, "cfg.Baseline:DeliveryMechanismCode", "cfg.Baseline:TargetCloudCode",
                  None, None, None, None, None, "cfg.Baseline:Notes"],
    "profiles": [None, "cfg.BaselineProfile:Code", "cfg.BaselineProfile:Name", None, "cfg.BaselineProfile:QualifierCode", None, "cfg.BaselineProfile:Description"],
    "sections": [None, "cfg.BaselineSection:SectionNumber", None, "cfg.BaselineSection:Title", "cfg.BaselineSection:Description", "cfg.BaselineSection:SortKey"],
    "gpoPackages": [None, None, None, "cfg.GpoPackage:DisplayName", "cfg.GpoPackage:ScopeCode", "cfg.GpoPackage:GpReportFile"],
    "recommendations": [None, "cfg.Recommendation:RecNumber", None, None, "cfg.Recommendation:Title", "cfg.Recommendation:AssessmentTypeCode", "cfg.Recommendation:CategoryText",
                        "cfg.Recommendation:LevelText", "cfg.Recommendation:Summary", "cfg.Recommendation:RationaleSummary", "cfg.Recommendation:ImpactSummary",
                        "cfg.Recommendation:DefaultValue", "cfg.Recommendation:RecommendedValue", "cfg.Recommendation:GridId", "cfg.Recommendation:ExternalId",
                        "cfg.Recommendation:MinimumOsVersion", "cfg.Recommendation:HowToVerifyCode", None, None, None, "cfg.Recommendation:SourceRowRef"],
    "recommendationProfiles": [None, None, None, "cfg.RecommendationProfile:RecommendedValue"],
    "recommendationRequirements": [None, None, None, None, None, None, "cfg.RecommendationRequirement:ControlsVersionLabel"],
    "settings": ["cfg.Setting:SettingKey", "cfg.Setting:SettingTypeCode", None, "cfg.Setting:Name", "cfg.Setting:FriendlyName", "cfg.Setting:CategoryPath", "cfg.Setting:ScopeCode",
                 "cfg.Setting:IntuneSettingDefinitionId", "cfg.Setting:OmaUri", "cfg.Setting:GpoPath", "cfg.Setting:RegistryHive", "cfg.Setting:RegistryKey",
                 "cfg.Setting:RegistryValueName", "cfg.Setting:RegistryValueType", "cfg.Setting:PowerShellCmdlet", "cfg.Setting:MinOsVersion", None, None],
    "settingAliases": ["cfg.Setting:SettingKey", "cfg.SettingAlias:AliasTypeCode", "cfg.SettingAlias:AliasValue", None],
    "recommendationSettings": [None, None, "cfg.Setting:SettingKey", "cfg.RecommendationSetting:ExpectedValue", "cfg.RecommendationSetting:OperatorCode",
                               "cfg.RecommendationSetting:ValueUnit", None],
    "stigReferences": [None, None, "cfg.StigReference:StigId", "cfg.StigReference:VulId", "cfg.StigReference:RuleId", None, "cfg.StigReference:StigTitle",
                       "cfg.StigReference:StigVersion", "cfg.StigReference:StigRelease", None],
    "phases": ["dlv.Phase:PhaseSetCode", "dlv.Phase:Code", "dlv.Phase:Name", None, "dlv.Phase:Description"],
    "workstreams": ["dlv.Workstream:Code", "dlv.Workstream:Name", "dlv.Workstream:Description"],
    "wbsTemplates": ["dlv.WbsTemplate:Code", "dlv.WbsTemplate:Name", "dlv.WbsTemplate:Version", None, None, "dlv.WbsTemplate:Notes"],
    "wbsTasks": [None, "dlv.WbsTask:TaskCode", None, "dlv.WbsTask:OutlineNumber", None, None, "dlv.WbsTask:Bucket", "dlv.WbsTask:TaskName", "dlv.WbsTask:SubTaskName",
                 "dlv.WbsTask:GroupAbbrev", "dlv.WbsTask:ControlCode", "dlv.WbsTask:ControlDescription", "dlv.WbsTask:Deliverable", "dlv.WbsTask:DependencyText",
                 "dlv.WbsTask:AppliesWhen", "dlv.WbsTask:LicenseRequirementText", None, None, None, None, None, None, None, "dlv.WbsTask:ComplexityDriver",
                 "dlv.WbsTask:LeadershipNotes", "dlv.WbsTask:SortKey"],
    "questionSets": ["eux.QuestionSet:Code", "eux.QuestionSet:Name", "eux.QuestionSet:PurposeCode", "eux.QuestionSet:Version", None],
    "questionModules": ["eux.QuestionModule:Code", "eux.QuestionModule:Name", "eux.QuestionModule:Description", None],
    "questions": [None, "eux.Question:Code", None, None, None, "eux.Question:CategoryText", "eux.Question:Topic", "eux.Question:QuestionText", "eux.Question:WhyAsked",
                  "eux.Question:AnswerTypeCode", "eux.Question:SuggestedOptionsText", "eux.Question:ConfigurationImpact", "eux.Question:DecisionOwnerRole",
                  "eux.Question:RequiredParticipants", "eux.Question:RequiredBy", "eux.Question:EvidenceRequired", "eux.Question:DefaultIfUndecided",
                  "eux.Question:EngineeringNotes", None],
    "answerOptions": [None, None, "eux.AnswerOption:OptionText", "eux.AnswerOption:OptionValue", None],
    "questionRules": [None, "eux.QuestionRule:ConditionExpression", None, "eux.QuestionRule:Notes"],
    "personas": ["eux.Persona:Code", "eux.Persona:Name", "eux.Persona:Description", None, None],
    "products": ["kb.Product:Code", "kb.Product:Name", "kb.Product:Vendor", "kb.Product:ProductFamilyCode", "kb.Product:AdminCenterName", "kb.Product:AdminPortalUrl",
                 "kb.Product:Description"],
    "deploymentMethods": ["deploy.DeploymentMethod:Code", "deploy.DeploymentMethod:Name", "deploy.DeploymentMethod:AuthModelCode", "deploy.DeploymentMethod:PermissionNotes", None],
    "methodCoverage": ["kb.Product:Code", "deploy.ServiceMethodCoverage:SettingGroup", "deploy.DeploymentMethod:Code", "deploy.ServiceMethodCoverage:JsonNativeCode", None,
                       "deploy.ServiceMethodCoverage:ReadWriteCode", "deploy.ServiceMethodCoverage:PortalOnlyGaps", "deploy.ServiceMethodCoverage:DriftOption",
                       "deploy.ServiceMethodCoverage:RepoStatusCode", "deploy.ServiceMethodCoverage:SourceUrl", "deploy.ServiceMethodCoverage:ConfidenceCode"],
}


# ----------------------------------------------------------------------------------------------------------------------
def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--documents", required=True)
    parser.add_argument("--pdf-text", default=None)
    parser.add_argument("--knowledge-base", default=os.path.join(os.path.dirname(__file__), "../../../src/EngineeringWorkspace.Infrastructure/KnowledgeBase/knowledge-base.json.gz"))
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    root = args.documents.rstrip("/")
    load_lengths(os.path.join(root, "EaaS", "GDS_ComplianceIQ_Data_Dictionary_v3.csv"))

    kb = json.loads(gzip.open(args.knowledge_base).read())
    requirements = {(row[0], row[1]) for row in kb["requirements"]}
    techniques = {row[0] for row in kb["techniques"]}
    mitigations = {row[0] for row in kb["mitigations"]}

    builder = Builder()
    for row in PLATFORMS:
        builder.platforms.add(row)
    load_ms_baselines(builder, root)
    cis_stats = load_cis(builder, root, args.pdf_text, requirements, techniques, mitigations)
    phases, workstreams, tasks, dependencies, wbs_coverage, decisions, decision_links, gds_source = load_gds(root)
    modules, eaas_questions, options, rules, personas, bank_source = load_question_bank(root)
    products, methods, method_coverage, matrix_source = load_method_matrix(root)
    for source in (gds_source, bank_source, matrix_source):
        builder.sources.add(source)

    def ordered(rows, key=lambda row: [str(part) for part in row]):
        return sorted(rows, key=key)

    output = {
        "schemaVersion": 1, "version": VERSION,
        "licenses": LICENSES, "mappingSources": MAPPING_SOURCES, "baselineSources": BASELINE_SOURCES,
        "sources": ordered(builder.sources.rows, key=lambda row: row[0]), "platforms": builder.platforms.rows,
        "baselines": ordered(builder.baselines.rows, key=lambda row: row[0]), "profiles": ordered(builder.profiles.rows),
        "sections": ordered(builder.sections.rows, key=lambda row: (row[0], row[5])), "gpoPackages": ordered(builder.gpo_packages.rows),
        "recommendations": builder.recommendations.rows, "sourceTexts": builder.source_texts.rows, "recommendationProfiles": builder.rec_profiles.rows,
        "recommendationRequirements": builder.rec_requirements.rows, "settings": ordered(builder.settings.rows, key=lambda row: row[0]),
        "settingAliases": ordered(builder.aliases.rows), "recommendationSettings": builder.rec_settings.rows, "stigReferences": builder.stig.rows,
        "recommendationTechniques": builder.rec_techniques.rows,
        "phases": sorted(phases.values(), key=lambda row: row[3]), "workstreams": sorted(workstreams.values()),
        "wbsTemplates": [["SP-WBS", "Secure Plus work breakdown structure", "WIP", "SP-STANDARDS", "GDS-SECUREPLUS-STANDARDS",
                          "Sheet WBS_WIP of SecurePlusStandards.xlsx; tasks are linked to the Secure Plus standard items with the same phase-group reference."]],
        "wbsTasks": tasks, "wbsDependencies": dependencies, "wbsCoverage": wbs_coverage,
        "questionSets": [["SP-DISCOVERY", "Secure Plus discovery decisions", "Discovery", "WIP", "GDS-SECUREPLUS-STANDARDS"],
                         ["EAAS-FORMS", "EaaS discovery and design workshop forms", "Questionnaire", "1.0", "GDS-EAAS-QUESTION-BANK"]],
        "questionModules": modules, "questions": decisions + eaas_questions, "answerOptions": options, "questionRules": rules, "questionLinks": decision_links,
        "personas": personas, "products": products, "deploymentMethods": methods, "methodCoverage": method_coverage,
    }
    # Every text value is cut to its column (SQL Server rejects longer values; SQLite does not check).
    for dataset, columns in COLUMNS.items():
        for row in output[dataset]:
            for position, column in enumerate(columns):
                if column and position < len(row) and isinstance(row[position], str):
                    row[position] = fit(*column.split(":"), row[position])
    output["counts"] = {name: len(value) for name, value in output.items() if isinstance(value, list)}
    output["cis"] = dict(cis_stats)
    output["truncated"] = dict(sorted(TRUNCATED.items()))
    output["issues"] = builder.issues
    data = json.dumps(output, ensure_ascii=False, separators=(",", ":"), sort_keys=False).encode("utf-8")
    with open(args.output, "wb") as handle:
        handle.write(gzip.compress(data, compresslevel=9, mtime=0))
    print(json.dumps({"counts": output["counts"], "cis": output["cis"], "truncated": output["truncated"], "issues": len(builder.issues),
                      "sha256": hashlib.sha256(data).hexdigest(), "bytes": os.path.getsize(args.output)}, indent=1))


if __name__ == "__main__":
    main()
