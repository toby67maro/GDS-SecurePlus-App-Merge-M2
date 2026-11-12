#!/usr/bin/env python3
"""Knowledge base phase 3: licensing, threat coverage, service tiers, delivery checklist, scoring and enrichment.

Sources:
  * build/knowledge-sources/Product names and service plan identifiers for licensing.csv (Microsoft)
  * build/knowledge-sources/ctid/*.json (MITRE CTID Mappings Explorer: Microsoft 365, Azure, NIST 800-53 Rev 5, CIS Controls 8.1.2)
  * Documents: M365_Services_Solutions_WIP_TB.xlsx, Customer_Deployment_Checklist_v2.0_Marquie.xlsx,
    Frameworks/CIS Benchmarks/GDS Modern Workplace - Service Tiers.pptx, Frameworks/CIS_Controls_Version_8.1.2___March_2025 (1).xlsx,
    Frameworks/CMMC 2.0 Compliance Baseline.xlsx, Frameworks/CMMC ITAR Compliance Crosswalk.xlsx, the Microsoft baseline delta workbooks,
    EaaS/Questionnaire/GDS_EaaS_Question_Bank.json, EaaS/control-attribute-data-sources-report.md (scoring method), and the compliance
    worksheets' status lists
  * the embedded catalog (catalog.json.gz) and knowledge-base phases 1 and 2 (to resolve codes)
Writes knowledge-base-phase3.json.gz (dataset "knowledge-base-phase-3").

Usage: python3 build/scripts/knowledge/generate_phase3.py --documents /path/to/Documents \
           --output src/EngineeringWorkspace.Infrastructure/KnowledgeBase/knowledge-base-phase3.json.gz
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

sys.path.insert(0, os.path.dirname(__file__))
import generate_baselines as gb  # noqa: E402  (shared helpers: column lengths, fit, Table, fold)

VERSION = "2026.11-1"
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
SOURCES = os.path.join(ROOT, "build", "knowledge-sources")
fit, fold, one_line, Table = gb.fit, gb.fold, gb.one_line, gb.Table


def slug(text, limit=60):
    return re.sub(r"[^A-Z0-9]+", "-", (text or "").upper().replace("&", "AND")).strip("-")[:limit]


def norm_cmmc(code):
    return re.sub(r"[^A-Z0-9]", "", (code or "").upper())


# ----------------------------------------------------------------------------------------------------------------------
# Reference rows
# ----------------------------------------------------------------------------------------------------------------------
LICENSES = [
    ["NIST-PUBLIC-DOMAIN", "NIST publications (U.S. Government work)", "https://www.nist.gov/oism/copyrights", 1, 1, 1, 1, "National Institute of Standards and Technology",
     "Works of the U.S. Government are not subject to copyright in the United States."],
    ["MITRE-ATTACK-TERMS", "MITRE ATT&CK terms of use", "https://attack.mitre.org/resources/legal-and-branding/terms-of-use/", 1, 1, 1, 1,
     "© The MITRE Corporation. Reproduced with permission of The MITRE Corporation.", "Royalty-free licence with attribution."],
    ["CTID-APACHE-2.0", "Apache License 2.0 (MITRE CTID Mappings Explorer)", "https://github.com/center-for-threat-informed-defense/mappings-explorer/blob/main/LICENSE.txt",
     1, 1, 1, 1, "MITRE Center for Threat-Informed Defense", "Apache 2.0: keep the licence and notices."],
    ["SCF-TERMS", "Secure Controls Framework terms", "https://securecontrolsframework.com/terms-conditions/", 1, 0, 0, 0, "Secure Controls Framework (SCF) Council",
     "Review the SCF terms before redistributing SCF content; the knowledge base stores it for internal use."],
    ["MICROSOFT-LICENSING-CSV", "Microsoft licensing service plan reference", "https://learn.microsoft.com/entra/identity/users/licensing-service-plan-reference",
     1, 0, 1, 1, "Microsoft", "Published by Microsoft for download."],
    ["CIS-CONTROLS-TERMS", "CIS Controls licence (CC BY-NC-ND 4.0)", "https://www.cisecurity.org/controls/cis-controls-faq", 0, 0, 0, 0, "Center for Internet Security",
     "Commercial use needs CIS's prior approval (GDS research report, 2026-11-04)."],
]
MAPPING_SOURCES = [
    ["CTID-M365", "MITRE CTID: Microsoft 365 security stack mappings", "MITRE CTID", "Crosswalk", 80, "https://ctid.mitre.org/mappings/"],
    ["CTID-AZURE", "MITRE CTID: Azure security stack mappings", "MITRE CTID", "Crosswalk", 80, "https://ctid.mitre.org/mappings/"],
    ["CTID-NIST-800-53", "MITRE CTID: NIST SP 800-53 Rev 5 to ATT&CK", "MITRE CTID", "Crosswalk", 85, "https://ctid.mitre.org/mappings/external/nist/"],
    ["CTID-CIS-CONTROLS", "MITRE CTID: CIS Controls 8.1.2 to ATT&CK", "MITRE CTID", "Crosswalk", 85, "https://ctid.mitre.org/mappings/"],
    ["MS-SERVICE-PLANS", "Microsoft licensing service plans", "Microsoft", "Authoritative", 100, "https://learn.microsoft.com/entra/identity/users/licensing-service-plan-reference"],
    ["GDS-CATALOG", "GDS Secure Plus catalog crosswalk", "GDS", "Internal", 50, None],
    ["GDS-CHECKLIST", "GDS Customer Deployment Checklist v2.0", "Global Data Systems", "GdsInternal", 90, None],
    ["GDS-CROSSWALK", "Derived: same technical setting across baselines", "Global Data Systems", "Derived", 70, None],
    ["GDS-SCORING", "GDS scoring method (control-attribute data sources report)", "Global Data Systems", "GdsInternal", 70, None],
    ["CMMC-WORKBOOK", "GDS CMMC 2.0 compliance baseline workbook", "Global Data Systems", "GdsInternal", 90, None],
    ["CIS-CONTROLS-WORKBOOK", "CIS Controls v8.1.2 workbook", "Center for Internet Security", "Authoritative", 100, "https://www.cisecurity.org/controls"],
]
RATING_SCALES = [
    # code, name, min, max, higherIsWorse, description, values [numeric, label, binLow, binHigh, color]
    ["NIST-800-30", "NIST SP 800-30 Rev 1 qualitative scale", 0, 10, True,
     "One scale for likelihood, impact and risk (NIST SP 800-30 Rev 1, Appendices G–I), as recommended by the GDS control-attribute data sources report.",
     [[0, "Very Low", 0, 4, "#2E7D32"], [2, "Low", 5, 20, "#7CB342"], [5, "Moderate", 21, 79, "#F9A825"], [8, "High", 80, 95, "#EF6C00"], [10, "Very High", 96, 100, "#C62828"]]],
    ["GDS-LOW-MEDIUM-HIGH", "Low / Medium / High", 1, 3, True, "Ordinal rating used by the GDS catalog and checklist (user impact, severity, difficulty).",
     [[1, "Low", 0, 33.33, "#2E7D32"], [2, "Medium", 33.34, 66.66, "#F9A825"], [3, "High", 66.67, 100, "#C62828"]]],
]
PRODUCTS = [
    ["POWERBI", "Microsoft Power BI", "Microsoft", "Productivity", "Power BI admin portal", "https://app.powerbi.com/admin-portal", None],
    ["WINDOWS", "Microsoft Windows", "Microsoft", "Endpoint", None, None, None],
    ["AZURE", "Microsoft Azure", "Microsoft", "Cloud", "Azure portal", "https://portal.azure.com", None],
    ["DYNAMICS365", "Microsoft Dynamics 365", "Microsoft", "Productivity", "Power Platform admin center", "https://admin.powerplatform.microsoft.com", None],
    ["M365-OTHER", "Other Microsoft 365 services", "Microsoft", "Productivity", "Microsoft 365 admin center", "https://admin.microsoft.com", None],
    ["MDI", "Microsoft Defender for Identity", "Microsoft", "Security", "Microsoft Defender portal", "https://security.microsoft.com", None],
    ["MDCA", "Microsoft Defender for Cloud Apps", "Microsoft", "Security", "Microsoft Defender portal", "https://security.microsoft.com", None],
    ["MDE", "Microsoft Defender for Endpoint", "Microsoft", "Security", "Microsoft Defender portal", "https://security.microsoft.com", None],
    ["MDO", "Microsoft Defender for Office 365", "Microsoft", "Security", "Microsoft Defender portal", "https://security.microsoft.com", None],
    ["DEFENDER-XDR", "Microsoft Defender XDR", "Microsoft", "Security", "Microsoft Defender portal", "https://security.microsoft.com", None],
    ["ENTRA", "Microsoft Entra ID", "Microsoft", "Identity", "Microsoft Entra admin center", "https://entra.microsoft.com", None],
    ["INTUNE", "Microsoft Intune", "Microsoft", "Endpoint", "Microsoft Intune admin center", "https://intune.microsoft.com", None],
    ["PURVIEW", "Microsoft Purview", "Microsoft", "Compliance", "Microsoft Purview portal", "https://purview.microsoft.com", None],
    ["EXO", "Exchange Online", "Microsoft", "Collaboration", "Exchange admin center", "https://admin.exchange.microsoft.com", None],
    ["SPO", "SharePoint Online and OneDrive", "Microsoft", "Collaboration", "SharePoint admin center", None, None],
    ["TEAMS", "Microsoft Teams", "Microsoft", "Collaboration", "Microsoft Teams admin center", "https://admin.teams.microsoft.com", None],
    ["M365APPS", "Microsoft 365 Apps", "Microsoft", "Productivity", "Microsoft 365 Apps admin center", "https://config.office.com", None],
    ["COPILOT", "Microsoft 365 Copilot", "Microsoft", "AI", "Microsoft 365 admin center", "https://admin.microsoft.com", None],
    ["POWERPLATFORM", "Microsoft Power Platform", "Microsoft", "Productivity", "Power Platform admin center", "https://admin.powerplatform.microsoft.com", None],
    ["SENTINEL", "Microsoft Sentinel", "Microsoft", "Security", "Microsoft Defender portal", "https://security.microsoft.com", None],
    ["M365-ADMIN", "Microsoft 365 admin center", "Microsoft", "Productivity", "Microsoft 365 admin center", "https://admin.microsoft.com", None],
]
PLAN_PRODUCTS = [  # service plan name prefix / substring -> product
    (("AAD_", "ENTRA", "MFA_PREMIUM", "IDENTITY_GOVERNANCE", "ADALLOM_FOR_AAD"), "ENTRA"),
    (("INTUNE", "EMS_"), "INTUNE"),
    (("ATP_ENTERPRISE", "THREAT_INTELLIGENCE", "MDO_", "EOP_"), "MDO"),
    (("WINDEFATP", "MDE_", "DEFENDER_ENDPOINT", "WIN_DEF_ATP"), "MDE"),
    (("ATA", "AATP"), "MDI"),
    (("ADALLOM",), "MDCA"),
    (("MTP",), "DEFENDER-XDR"),
    (("EXCHANGE", "EXO_"), "EXO"),
    (("SHAREPOINT", "ONEDRIVE"), "SPO"),
    (("TEAMS", "MCO"), "TEAMS"),
    (("RMS_", "MIP_", "INFORMATION_PROTECTION", "M365_ADVANCED_AUDITING", "M365_AUDIT", "COMMUNICATIONS_COMPLIANCE", "INSIDER_RISK", "EQUIVIO", "LOCKBOX",
      "PAM_ENTERPRISE", "RECORDS_MANAGEMENT", "DATA_INVESTIGATIONS", "ML_CLASSIFICATION", "CUSTOMER_KEY", "PREMIUM_ENCRYPTION", "INFO_GOVERNANCE",
      "MICROSOFTENDPOINTDLP", "PURVIEW", "COMMUNICATIONS_DLP", "INSIDER"), "PURVIEW"),
    (("OFFICESUBSCRIPTION", "OFFICE_", "OFFICEMOBILE"), "M365APPS"),
    (("WIN10", "WIN11", "WINDOWS", "WIN_ENT", "UNIVERSAL_PRINT", "VIRTUAL_DESKTOP"), "WINDOWS"),
    (("BI_AZURE", "POWER_BI", "PBI_"), "POWERBI"),
    (("POWERAPPS", "FLOW", "POWER_VIRTUAL", "CDS_", "DYN365_CDS"), "POWERPLATFORM"),
    (("DYN365", "CRM", "D365", "DYNAMICS"), "DYNAMICS365"),
    (("COPILOT", "M365_COPILOT"), "COPILOT"),
]
SKU_FAMILIES = [("Business", "M365Business"), ("Microsoft 365 E", "M365Enterprise"), ("Microsoft 365 F", "M365Frontline"), ("Office 365", "Office365"),
                ("Enterprise Mobility", "EMS"), ("Entra", "Entra"), ("Azure Active Directory", "Entra"), ("Intune", "Intune"), ("Defender", "Defender"),
                ("Purview", "Purview"), ("Teams", "Teams"), ("Windows", "Windows"), ("Copilot", "Copilot"), ("Power", "PowerPlatform"), ("Dynamics", "Dynamics"),
                ("Microsoft 365 A", "M365Education"), ("Microsoft 365 G", "M365Government")]
CTID_GROUP_PRODUCTS = {"m365-defender": "DEFENDER-XDR", "entra-id": "ENTRA", "eop": "MDO", "purview": "PURVIEW"}
LICENCE_SKUS = [("Entra ID P2", "AAD_PREMIUM_P2"), ("Entra ID P1", "AAD_PREMIUM"), ("Defender for Endpoint", "DEFENDER_ENDPOINT_P1"), ("Intune Plan 1", "INTUNE_A"),
                ("Entra Internet/Private Access", "Microsoft_Entra_Suite"), ("Entra Workload ID", "Workload_Identities_P2")]
CATALOG_PREFIXES = [("NIST 800-53", "NIST-800-53", "Rev 5"), ("NIST CSF", "NIST-CSF", "2.0"), ("CIS v8.1", "CIS-CONTROLS", "v8.1"), ("CIS v8", "CIS-CONTROLS", "v8"),
                    ("ISO 27001:2022", "ISO-27001", "2022"), ("CMMC 2.0", "CMMC", "2.0"), ("PCI DSS 4.0.1", "PCI-DSS", "4.0.1"), ("PCI DSS 4.0", "PCI-DSS", "4.0"),
                    ("HIPAA", "HIPAA", "45 CFR 164"), ("Essential Eight", "ESSENTIAL-EIGHT", "Current")]
TOOLING_PRODUCTS = [("Conditional Access", "ENTRA"), ("Entra", "ENTRA"), ("Intune", "INTUNE"), ("Defender for Endpoint", "MDE"), ("Defender for Office", "MDO"),
                    ("Defender for Identity", "MDI"), ("Defender for Cloud Apps", "MDCA"), ("Defender XDR", "DEFENDER-XDR"), ("Sentinel", "SENTINEL"), ("Purview", "PURVIEW"),
                    ("Exchange", "EXO"), ("SharePoint", "SPO"), ("OneDrive", "SPO"), ("Teams", "TEAMS"), ("Azure", "AZURE"), ("BitLocker", "WINDOWS"), ("Windows", "WINDOWS")]
POLICY_KINDS = {"ConditionalAccess": "ENTRA", "Intune": "INTUNE", "EndpointSecurity": "INTUNE"}
PLATFORM_CODES = {"Windows": "WINDOWS", "Android": "ANDROID", "macOS": "MACOS", "iOS": "IOS", "Windows 365": "WINDOWS", "Microsoft 365": "M365"}


def catalog_reference(label):
    text = label.strip()
    for prefix, code, version in CATALOG_PREFIXES:
        if text.startswith(prefix) and (len(text) == len(prefix) or text[len(prefix)].isspace()):
            return code, version, text[len(prefix):].strip() or "General"
    return None


def plan_product(plan):
    for tokens, product in PLAN_PRODUCTS:
        if any(token in plan for token in tokens):
            return product
    return "M365-OTHER"


# ----------------------------------------------------------------------------------------------------------------------
def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--documents", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    docs = args.documents.rstrip("/")
    gb.load_lengths(os.path.join(docs, "EaaS", "GDS_ComplianceIQ_Data_Dictionary_v3.csv"))
    kb_dir = os.path.join(ROOT, "src", "EngineeringWorkspace.Infrastructure", "KnowledgeBase")
    phase1 = json.loads(gzip.open(os.path.join(kb_dir, "knowledge-base.json.gz")).read())
    phase2 = json.loads(gzip.open(os.path.join(kb_dir, "baselines.json.gz")).read())
    catalog = json.loads(gzip.open(os.path.join(ROOT, "src", "EngineeringWorkspace.Infrastructure", "Catalog", "catalog.json.gz")).read())
    bank = json.load(open(os.path.join(docs, "EaaS", "Questionnaire", "GDS_EaaS_Question_Bank.json"), encoding="utf-8"))
    requirements = {(row[0], row[1]) for row in phase1["requirements"]}
    techniques = {row[0] for row in phase1["techniques"]}
    issues = []
    out = collections.OrderedDict()
    sources = Table([0])
    objects = Table([0, 1])

    def source(code, title, publisher, kind, version, published, location, path, licence, gds, notes=None):
        sources.add([code, title, publisher, kind, version, published, location, os.path.basename(path) if path else None,
                     gb.sha256(path) if path and os.path.exists(path) else None, licence, 0 if gds else 1, 1 if gds else 0, 1, None, notes])

    def obj(source_code, name, targets, rows, notes=None):
        objects.add([source_code, name, None, targets, rows, notes])

    # ---------------- Products, licensing, capabilities ----------------
    products = {row[0]: row for row in PRODUCTS}
    licensing_path = os.path.join(SOURCES, "Product names and service plan identifiers for licensing.csv")
    source("MS-LICENSING-SERVICE-PLANS", "Product names and service plan identifiers for licensing", "Microsoft", "Dataset", "2026-11-08", None,
           "build/knowledge-sources/Product names and service plan identifiers for licensing.csv", licensing_path, "MICROSOFT-LICENSING-CSV", False)
    licence_rows = list(csv.DictReader(open(licensing_path, encoding="utf-8-sig")))
    skus, capabilities, sku_caps = Table([0]), Table([0]), Table([0, 1])
    for row in licence_rows:
        part, name = row["String_Id"].strip(), one_line(row["Product_Display_Name"])
        if len(part) > 100:  # kb.LicenseSku.SkuPartNumber is NVARCHAR(100) and the natural key: keep it unique
            part = f"{part[:95]}-{hashlib.sha256(part.encode()).hexdigest()[:4].upper()}"
        family = next((code for needle, code in SKU_FAMILIES if needle.lower() in name.lower()), "Other")
        cloud = "GccHigh" if re.search(r"GCC ?High|GCCHIGH", name + part, re.I) else "DoD" if re.search(r"\bDOD\b", name + part) else \
            "Gcc" if re.search(r"\bGCC\b|_GOV|USGOV", name + part, re.I) else "Commercial"
        add_on = 1 if re.search(r"add-on|addon|\bE5 Security\b|\bE5 Compliance\b|\bP2\b(?!.*Business Premium)", name, re.I) and not name.startswith("Microsoft 365 E") else 0
        skus.add([part, fit("kb.LicenseSku", "Name", name), "Microsoft", family, cloud, add_on, fit("kb.LicenseSku", "Notes", f"SkuId {row['GUID'].strip()}")])
        plan, plan_name = row["Service_Plan_Name"].strip(), one_line(row["Service_Plans_Included_Friendly_Names"])
        if not plan:
            continue
        code = fit("kb.Capability", "Code", f"SP-{plan}")
        capabilities.add([code, plan_product(plan), fit("kb.Capability", "Name", plan_name or plan), "Licensed service plan",
                          fit("kb.Capability", "Description", f"Service plan {plan} ({row['Service_Plan_Id'].strip()})."), None, None])
        sku_caps.add([part, code, fit("kb.SkuCapability", "ServicePlanName", plan)])
    obj("MS-LICENSING-SERVICE-PLANS", "Product names and service plan identifiers for licensing.csv", "kb.LicenseSku; kb.Capability; kb.SkuCapability", len(licence_rows))

    # One product-level capability per product (what requirement tooling text names).
    for product in PRODUCTS:
        capabilities.add([f"PRODUCT-{product[0]}", product[0], product[1], "Product", f"{product[1]} as a whole (used where a source names the product, not a feature).",
                          product[5], product[4]])

    # Admin-centre solutions (GDS services and solutions workbook).
    solutions_path = os.path.join(docs, "M365_Services_Solutions_WIP_TB.xlsx")
    source("GDS-M365-SERVICES-SOLUTIONS", "Microsoft 365 services and solutions (WIP)", "Global Data Systems", "Workbook", "WIP", None, "M365_Services_Solutions_WIP_TB.xlsx",
           solutions_path, "GDS-INTERNAL", True)
    wb = openpyxl.load_workbook(solutions_path, read_only=True, data_only=True)
    centre_products = {"security": "DEFENDER-XDR", "intune": "INTUNE", "exchange": "EXO", "entra": "ENTRA", "purview": "PURVIEW", "teams": "TEAMS", "sharepoint": "SPO",
                       "azure": "AZURE", "power": "POWERPLATFORM", "microsoft 365": "M365-ADMIN", "admin": "M365-ADMIN"}
    solution_rows = list(wb["02 - Solutions"].iter_rows(min_row=3, values_only=True))
    for centre, category, name, description, url in [tuple(list(r) + [None] * 5)[:5] for r in solution_rows]:
        if not name:
            continue
        product = next((code for needle, code in centre_products.items() if needle in str(centre or "").lower()), "M365-ADMIN")
        capabilities.add([fit("kb.Capability", "Code", f"SOL-{slug(centre, 20)}-{slug(category, 25)}-{slug(name, 30)}"), product,
                          fit("kb.Capability", "Name", one_line(str(name))), fit("kb.Capability", "CategoryText", one_line(f"{centre} > {category}")),
                          fit("kb.Capability", "Description", one_line(str(description or ""))), fit("kb.Capability", "DocUrl", url),
                          fit("kb.Capability", "PortalPath", one_line(f"{centre} > {category} > {name}"))])
    obj("GDS-M365-SERVICES-SOLUTIONS", "02 - Solutions", "kb.Capability", len(solution_rows))

    # ---------------- MITRE CTID ----------------
    ctid = {os.path.basename(p): json.load(open(p)) for p in glob.glob(os.path.join(SOURCES, "ctid", "*.json"))}
    capability_techniques, requirement_techniques = Table([0, 1, 2]), Table([0, 1, 2, 3])
    extra_techniques = {}
    for name, data in sorted(ctid.items()):
        meta = data["metadata"]
        framework = meta["mapping_framework"]
        code = {"m365": "CTID-M365-ATTACK", "azure": "CTID-AZURE-ATTACK", "nist_800_53": "CTID-NIST-800-53-ATTACK", "cis": "CTID-CIS-CONTROLS-ATTACK"}[framework]
        source(code, f"MITRE CTID mappings: {framework} {meta.get('mapping_framework_version', '')} to ATT&CK {meta['attack_version']}", "MITRE CTID", "Mapping",
               meta.get("mapping_framework_version") or meta["attack_version"], None, f"build/knowledge-sources/ctid/{name}", os.path.join(SOURCES, "ctid", name),
               "CTID-APACHE-2.0", False, f"ATT&CK {meta['attack_version']}; commit in build/knowledge-sources/README.md.")
        obj(code, name, "threat.CapabilityTechnique" if framework in ("m365", "azure") else "threat.RequirementTechnique", len(data["mapping_objects"]))
        for item in data["mapping_objects"]:
            technique = item.get("attack_object_id")
            if not technique or not item.get("capability_id"):
                continue
            if technique not in techniques:
                # Not in the ATT&CK workbook loaded by phase 1: added from the CTID mapping (ID and name).
                extra_techniques.setdefault(technique, [technique, technique.split(".")[0] if "." in technique else None,
                                                        fit("threat.AttackTechnique", "Name", item.get("attack_object_name") or technique), 1 if "." in technique else 0,
                                                        meta["attack_version"], f"https://attack.mitre.org/techniques/{technique.replace('.', '/')}/"])
                issues.append(["INFO", "TECHNIQUE_ADDED", f"{name}: {technique} added from CTID (not in the ATT&CK workbook)"])
            if framework in ("m365", "azure"):
                prefix = "CTID-M365" if framework == "m365" else "CTID-AZURE"
                cap_code = fit("kb.Capability", "Code", f"{prefix}-{item['capability_id']}")
                product = CTID_GROUP_PRODUCTS.get(item.get("capability_group"), "AZURE" if framework == "azure" else "M365-OTHER")
                capabilities.add([cap_code, product, fit("kb.Capability", "Name", item.get("capability_description") or item["capability_id"]),
                                  fit("kb.Capability", "CategoryText", f"MITRE CTID {framework.upper()} security stack: {meta['capability_groups'].get(item.get('capability_group'), item.get('capability_group'))}"
                                      if isinstance(meta.get("capability_groups"), dict) else f"MITRE CTID {framework}"),
                                  None, fit("kb.Capability", "DocUrl", (item.get("references") or [None])[0]), None])
                capability_techniques.add([cap_code, technique, prefix, (item.get("score_category") or "").title() or None, (item.get("score_value") or "").title() or None])
            elif framework == "nist_800_53":
                if ("general-nist-800-53-r5-2", item["capability_id"]) in requirements:
                    requirement_techniques.add(["general-nist-800-53-r5-2", item["capability_id"], technique, "CTID-NIST-800-53", "Mitigates", meta["attack_version"]])
            elif framework == "cis":
                safeguard = item["capability_id"].replace("CIS-", "")
                if ("general-cis-csc-8-1", safeguard) in requirements:
                    requirement_techniques.add(["general-cis-csc-8-1", safeguard, technique, "CTID-CIS-CONTROLS", "Mitigates", meta["attack_version"]])

    # ---------------- Service tiers ----------------
    deck = os.path.join(docs, "Frameworks", "CIS Benchmarks", "GDS Modern Workplace - Service Tiers.pptx")
    source("GDS-SERVICE-TIERS-DECK", "GDS Modern Workplace – Service Tiers", "Global Data Systems", "Deck", None, None,
           "Frameworks/CIS Benchmarks/GDS Modern Workplace - Service Tiers.pptx", deck, "GDS-INTERNAL", True)
    from pptx import Presentation
    texts = []
    for shape in Presentation(deck).slides[0].shapes:
        if shape.has_text_frame:
            texts.append([p.text.strip() for p in shape.text_frame.paragraphs if p.text.strip()])
    blocks = [block for block in texts if block]
    shared = next((block for block in blocks if block[0].startswith("Included in every Modern Workplace tier")), [])
    shared_items = [part.strip() for line in shared[1:] for part in line.split("•") if part.strip()]
    tiers = {"Essentials": "Essentials", "Secure Plus": "SecurePlus", "Secure Plus Premium": "SecurePlusPremium"}
    tier_features, tier_rows = [], []
    for index, block in enumerate(blocks):
        if len(block) == 1 and block[0] in tiers and index + 2 < len(blocks):
            code = tiers[block[0]]
            tier_rows.append([code, blocks[index + 1][0]])
            for order, feature in enumerate(shared_items + blocks[index + 2], start=1):
                tier_features.append([code, order, fit("svc.TierFeature", "FeatureText", feature)])
    obj("GDS-SERVICE-TIERS-DECK", "Slide 1", "svc.ServiceTier; svc.TierFeature; svc.TierCapability", len(tier_features))
    base_skus = {"SecurePlus": ["SPB", "IDENTITY_THREAT_PROTECTION"], "SecurePlusPremium": ["SPE_E5"]}
    plans_by_sku = collections.defaultdict(list)
    for row in sku_caps.rows:
        plans_by_sku[row[0]].append(row[1])
    tier_capabilities = Table([0, 1])
    for tier, sku_list in base_skus.items():
        for sku in sku_list:
            for capability in plans_by_sku.get(sku, []):
                tier_capabilities.add([tier, capability, "Included", fit("svc.TierCapability", "Notes", f"Included in {sku}, the base licence of the tier (Service Tiers deck).")])

    # ---------------- Customer Deployment Checklist v2.0 (GDS standard) ----------------
    checklist_path = os.path.join(docs, "Customer_Deployment_Checklist_v2.0_Marquie.xlsx")
    source("GDS-CUSTOMER-DEPLOYMENT-CHECKLIST", "Customer Deployment Checklist v2.0", "Global Data Systems", "Workbook", "2.0", None,
           "Customer_Deployment_Checklist_v2.0_Marquie.xlsx", checklist_path, "GDS-INTERNAL", True)
    wb = openpyxl.load_workbook(checklist_path, read_only=True, data_only=True)
    checklist_items, checklist_tiers, checklist_guidance, checklist_values = Table([0]), [], [], []
    T = "gds.StandardItem"
    for sheet, header_row, optional in (("Assessment", 2, False), ("Optional Controls", 2, True)):
        rows = list(wb[sheet].iter_rows(values_only=True))
        header = [str(h).strip() if h else "" for h in rows[header_row - 1]]
        col = {h: i for i, h in enumerate(header)}
        obj("GDS-CUSTOMER-DEPLOYMENT-CHECKLIST", sheet, "gds.StandardItem; gds.StandardItemTier; kb.Guidance; score.EnrichmentValue", len(rows) - header_row)
        for sequence, row in enumerate(rows[header_row:], start=1):
            control = row[col["Control ID"]] if "Control ID" in col else None
            if not control:
                continue
            item = f"CDC-{str(control).strip()}"
            if (item,) in checklist_items:
                continue
            phase_text = str(row[col["Phase"]] or "")
            checklist_items.add([item, (1000 if optional else 0) + sequence, fit(T, "PhaseGroupRef", str(row[col["WBS"]] or "") or None), fit(T, "TaskName", phase_text),
                                 fit(T, "ControlCode", str(control)), fit(T, "SettingCategory", row[col["Category"]]), fit(T, "SettingName", one_line(str(row[col["Control Name"]] or ""))),
                                 fit(T, "TargetConfiguration", one_line(str(row[col["Recommended State"]] or ""))),
                                 fit(T, "ConfigurationPath", one_line(str(row[col["Microsoft Solution"]] or ""))),
                                 fit(T, "ValidationMethod", one_line(str(row[col["How to Verify"]] or ""))), 1 if optional else 0,
                                 fit(T, "EngineeringNotes", one_line(str(row[col["Description"]] or ""))), str(row[col["Level"]] or "")])
            for tier in ("SecurePlus", "SecurePlusPremium"):
                checklist_tiers.append([item, tier, "Optional" if optional else "Included"])
            checklist_guidance.append([item, one_line(str(row[col["Description"]] or "")), one_line(str(row[col["User Impact"]] or "")),
                                       row[col["PowerShell Script"]] if "PowerShell Script" in col else None, row[col["Rollback Notes"]] if "Rollback Notes" in col else None])
            if isinstance(row[col["Points"]], (int, float)):
                checklist_values.append(["CHECKLIST_POINTS", "StandardItem", item, str(row[col["Points"]]), float(row[col["Points"]]), None])
            if row[col["User Impact"]]:
                checklist_values.append(["EUX_IMPACT", "StandardItem", item, str(row[col["User Impact"]]), None, str(row[col["User Impact"]])])

    # ---------------- Catalog: configuration changes, framework links, known issues, guidance ----------------
    aliases = {(a[0], a[1], a[2]): (a[3], a[4]) for a in phase1["aliases"]}
    changes, change_requirements, known_issues, issue_links, guidance, item_requirements, catalog_values = Table([0]), Table([0, 1, 2]), Table([0]), [], [], Table([0, 1, 2]), []
    sku_parts = {row[0] for row in skus.rows}
    C = "kb.ConfigurationChange"
    for control in catalog["controls"]:
        kind, cid = control["kind"], control["id"]
        refs = []
        for label in control.get("frameworks") or []:
            parsed = catalog_reference(label)
            if parsed and parsed in aliases:
                refs.append(aliases[parsed])
        implementation = control.get("implementation") if isinstance(control.get("implementation"), dict) else {}
        remediation = control.get("remediation") if isinstance(control.get("remediation"), dict) else {}
        pain = one_line(control.get("painPoints"))
        user_impact = control.get("userImpact") if control.get("userImpact") in ("Low", "Medium", "High") else None
        if kind in POLICY_KINDS:
            licence = control.get("licensing") or ""
            minimum = next((sku for needle, sku in LICENCE_SKUS if needle in licence and sku in sku_parts), None)
            changes.add([fit(C, "Code", cid), fit(C, "Name", control.get("name")), fit(C, "Description", one_line(control.get("description"))), POLICY_KINDS[kind],
                         fit(C, "ChangeTypeCode", kind), "GRAPH", "ReportOnly" if control.get("value") == "enabledForReportingButNotEnforced" else "Enforce", minimum,
                         1 if user_impact in ("Medium", "High") else 0, None, None, 1, "Engineer", "Active", cid])
            for fd, code in refs:
                change_requirements.add([cid, fd, code, "GDS-CATALOG", "RelatedTo", 0, f"catalog {cid} -> {fd} {code}"])
            target = ("ConfigurationChange", cid)
        else:
            for fd, code in refs:
                item_requirements.add([cid, fd, code, "GDS-CATALOG", "RelatedTo"])
            target = ("StandardItem", cid)
        if pain and not re.match(r"^(none( identified)?\.?|n/?a)$", pain, re.I) and len(pain) > 12:
            title = fit("kb.KnownIssue", "Title", f"{cid}: {control.get('name')}")
            known_issues.add([title, pain, None, None, user_impact, PLATFORM_CODES.get(control.get("platform")), POLICY_KINDS.get(kind), "SECUREPLUS-CATALOG"])
            if kind in POLICY_KINDS:
                issue_links.append([title, "ConfigurationChange", cid, "Side effect recorded in the Secure Plus catalog."])
        guidance.append([target[0], target[1], one_line(implementation.get("benefits")), one_line(implementation.get("experienceImpact") or control.get("experience")),
                         one_line(control.get("licensing")) or None,
                         "\n".join(filter(None, [remediation.get("settingPath"), remediation.get("gpoPath"), remediation.get("graphRequest"), remediation.get("steps")
                                                 if isinstance(remediation.get("steps"), str) else None])) or None,
                         remediation.get("validation"), remediation.get("rollback"), pain or None, one_line(control.get("licensing")) or None])
        for attribute, raw in (("EFFORT_MINUTES", implementation.get("estimatedMinutes")), ("DIFFICULTY", implementation.get("difficulty")),
                               ("RISK_LEVEL", implementation.get("riskLevel")), ("SEVERITY", control.get("severity")), ("EUX_IMPACT", user_impact)):
            if raw not in (None, ""):
                catalog_values.append([attribute, "Artifact", cid, str(raw), float(raw) if isinstance(raw, (int, float)) else None,
                                       str(raw) if str(raw) in ("Low", "Medium", "High") else None])
    source("SECUREPLUS-CATALOG", "Secure Plus control catalog (embedded catalog.json.gz)", "Global Data Systems", "Catalog", catalog.get("seedVersion"), None,
           "src/EngineeringWorkspace.Infrastructure/Catalog/catalog.json.gz", os.path.join(ROOT, "src", "EngineeringWorkspace.Infrastructure", "Catalog", "catalog.json.gz"),
           "GDS-INTERNAL", True)
    obj("SECUREPLUS-CATALOG", "controls", "kb.ConfigurationChange; kb.ConfigurationChangeRequirement; kb.KnownIssue; kb.Guidance; gds.StandardItemRequirement",
        len(catalog["controls"]))

    # ---------------- Microsoft baseline changes ----------------
    base = os.path.join(docs, "Frameworks", "Microsoft Security Baselines")
    baseline_changes, previous_baselines = [], []
    CH = "cfg.BaselineChange"
    setting_by_registry = {}
    for alias in phase2["settingAliases"]:
        if alias[1] == "REGISTRY":
            setting_by_registry[fold(alias[2])] = alias[0]

    def registry_key(info):
        parsed = gb.parse_registry_info(info)
        return (parsed[0], f"{parsed[0][0]}\\{parsed[0][1]}:{parsed[0][2]}") if parsed else (None, None)

    delta = os.path.join(base, "Windows 11 v26H2 Security Baseline v2", "Documentation", "Windows 11 25H2 to 26H2 Delta.xlsx")
    source("MSFT-WIN11-25H2-26H2-DELTA", "Windows 11 25H2 to 26H2 delta", "Microsoft", "Baseline", "26H2", None,
           "Frameworks/Microsoft Security Baselines/Windows 11 v26H2 Security Baseline v2/Documentation/Windows 11 25H2 to 26H2 Delta.xlsx", delta, "MICROSOFT-SCT", False)
    previous_baselines.append(["MSFT-WIN11-25H2", "MSFT-SCT", "WIN11", "Microsoft Security Baseline for Windows 11 25H2", "25H2", "MSFT-WIN11-26H2"])
    rows = list(openpyxl.load_workbook(delta, read_only=True, data_only=True).worksheets[0].iter_rows(min_row=2, values_only=True))
    for kind, scope, path, name, old, new in [tuple(list(r) + [None] * 6)[:6] for r in rows]:
        if not kind:
            continue
        _, new_key = registry_key(new if new not in (None, "None") else old)
        kind = {"RegistryChangedSamePolicyName": "Changed"}.get(kind, kind)  # ChangeTypeCode is NVARCHAR(20)
        baseline_changes.append(["MSFT-WIN11-25H2", "MSFT-WIN11-26H2", setting_by_registry.get(fold(new_key)) if new_key else None, kind,
                                 None, None, None if old in (None, "None") else fit(CH, "OldRegistryInfo", str(old)),
                                 None if new in (None, "None") else fit(CH, "NewRegistryInfo", str(new)), one_line(f"{scope}: {path}\\{name}")])
    obj("MSFT-WIN11-25H2-26H2-DELTA", "Sheet1", "cfg.BaselineChange", len(rows))
    for folder, workbook, code, to_code, sheets in (
            ("Windows Server 2025 Security Baseline - 2602", "New Settings in Windows Server 2025 v2602.xlsx", "MSFT-WS2025-NEW-2602", "MSFT-WS2025-2602", None),
            ("Microsoft 365 Apps for Enterprise 2512", "Microsoft 365 Apps for enterprise v5473 to v5516 New Settings.xlsx", "MSFT-M365APPS-NEW-2512", "MSFT-M365APPS-2512", None)):
        path = os.path.join(base, folder, "Documentation", workbook)
        source(code, workbook.rsplit(".", 1)[0], "Microsoft", "Baseline", None, None, f"Frameworks/Microsoft Security Baselines/{folder}/Documentation/{workbook}", path,
               "MICROSOFT-SCT", False)
        for ws in openpyxl.load_workbook(path, read_only=True, data_only=True).worksheets:
            kind = "Removed" if ws.title.lower().startswith("remove") else "Added"
            sheet_rows = list(ws.iter_rows(values_only=True))
            obj(code, ws.title, "cfg.BaselineChange", len(sheet_rows))
            for row in sheet_rows:
                info = next((cell for cell in row if isinstance(cell, str) and re.match(r"HK(LM|CU)\\", cell)), None)
                if not row or not row[0] or not info:
                    continue
                _, key = registry_key(info)
                baseline_changes.append([None, to_code, setting_by_registry.get(fold(key)) if key else None, kind, None, None,
                                         fit(CH, "OldRegistryInfo", str(info)) if kind == "Removed" else None, fit(CH, "NewRegistryInfo", str(info)) if kind == "Added" else None,
                                         one_line(f"{row[0]} {row[1]}: {row[2]}\\{row[3]}")])

    # ---------------- Derived from phase 2: relations and procedures ----------------
    current = {row[0] for row in phase2["baselines"] if row[8] == 1}
    recs = {(row[0], row[1]): row for row in phase2["recommendations"]}
    settings = {row[0]: row for row in phase2["settings"]}
    by_setting = collections.defaultdict(list)
    for row in phase2["recommendationSettings"]:
        if row[0] in current:
            by_setting[row[2]].append(row)
    relations = Table([0, 1, 2, 3, 4])
    for key, rows in by_setting.items():
        rows = sorted(rows, key=lambda r: (r[0], r[1]))[:40]
        for i, a in enumerate(rows):
            for b in rows[i + 1:]:
                if a[0] == b[0]:
                    continue
                same = fold(a[3] or "") == fold(b[3] or "")
                relations.add([a[0], a[1], b[0], b[1], "SameSetting" if same else "DifferentValue",
                               fit("cfg.RecommendationRelation", "Notes", f"Both configure {key}" + ("" if same else f": '{a[3]}' vs '{b[3]}'"))])
    procedures = []
    for row in phase2["recommendationSettings"]:
        rec, setting = recs.get((row[0], row[1])), settings.get(row[2])
        if not rec or not setting or row[6] != 1:
            continue
        expected = row[3] or "the recommended value"
        if setting[11]:
            hive = {"HKLM": "HKEY_LOCAL_MACHINE", "HKCU": "HKEY_CURRENT_USER", "HKU": "HKEY_USERS"}.get(setting[10], setting[10])
            user = setting[10] in ("HKCU", "HKU") or setting[11].startswith("[")
            script = f"Get-ItemPropertyValue -Path 'Registry::{hive}\\{setting[11]}' -Name '{setting[12]}'"
            procedures.append([row[0], row[1], "Audit", "PowerShell", 1, f"Read the registry value and compare it with {expected}." +
                               (" Run as the signed-in user (HKCU)." if user else ""), script, 1, "<USER SID>" if "[USER" in setting[11].upper() else None])
        if setting[9]:
            procedures.append([row[0], row[1], "Remediation", "GPO", 1, f"Set the Group Policy setting {setting[9]} to {expected}.", None, 0, None])
        if setting[0].startswith("intune:"):
            procedures.append([row[0], row[1], "Remediation", "Intune", 1, f"In an Intune Settings Catalog profile, set {setting[0][7:]} to {expected}.", None, 0, None])

    # ---------------- Question bank: expectations, rings ----------------
    areas = {"sign-in": "SIGN-IN", "session": "SIGN-IN", "device": "DEVICE", "app": "APPS-BROWSER", "browser": "APPS-BROWSER", "data": "DATA-HANDLING",
             "shar": "DATA-HANDLING", "update": "UPDATES", "restart": "UPDATES", "mail": "COLLABORATION", "teams": "COLLABORATION", "admin": "ADMINISTRATION",
             "install": "APPS-BROWSER", "usb": "DEVICE", "remov": "DEVICE", "local": "ADMINISTRATION"}
    expectations, answer_expectations, expectation_impacts = [], [], []
    profile_choice = {c["value"]: c for q in bank["questions"] if q["id"] == "F1_EX_01" for c in q["choices"]}
    for profile in bank["experienceProfiles"]:
        aspect = profile["aspect"]
        area = next((code for needle, code in areas.items() if needle in aspect.lower()), "TRANSPARENT")
        policies = [p.strip() for p in re.split(r"[,;]", profile.get("policies") or "") if p.strip()]
        for mode in ("convenience", "balanced", "security"):
            code = fit("eux.Expectation", "Code", f"EXP-{slug(aspect, 18)}-{mode[:4].upper()}")
            expectations.append([code, area, fit("eux.Expectation", "Statement", f"{aspect}: {profile[mode]}"), f"{mode.title()} experience profile (EaaS question bank).",
                                 3 if mode == "balanced" else 2])
            if mode in profile_choice:
                answer_expectations.append(["F1_EX_01", mode, code, 3])
            contradicted = profile.get("contradictions") or ""
            for policy in policies:
                effect = "Contradicts" if re.search(re.escape(policy.split("-")[-1]) + r"[^.;]*contradict[s]? " + mode, contradicted, re.I) else "Supports"
                expectation_impacts.append([code, policy, effect, 2 if effect == "Contradicts" else 1])
    rings = [["PILOT", "Ring 1: Pilot (IT and volunteers)", 1, "ReportOnly"], ["UAT", "Ring 2: UAT (one person per department)", 2, "ReportOnly"],
             ["PROD", "Ring 3: Production (everyone)", 3, "ReportOnly"]]

    # ---------------- Framework properties, scope categories, status labels ----------------
    property_defs, requirement_props, scope_categories = [], [], []
    cis_path = os.path.join(docs, "Frameworks", "CIS_Controls_Version_8.1.2___March_2025 (1).xlsx")
    source("CIS-CONTROLS-8.1.2", "CIS Controls v8.1.2 (March 2025)", "Center for Internet Security", "Framework", "8.1.2", "2025-03-01",
           "Frameworks/CIS_Controls_Version_8.1.2___March_2025 (1).xlsx", cis_path, "CIS-CONTROLS-TERMS", False)
    for code, name, kind in (("CIS_ASSET_CLASS", "CIS asset class", "Text"), ("CIS_SECURITY_FUNCTION", "CIS security function", "Text"),
                             ("CIS_IG1", "CIS Implementation Group 1", "Boolean"), ("CIS_IG2", "CIS Implementation Group 2", "Boolean"),
                             ("CIS_IG3", "CIS Implementation Group 3", "Boolean")):
        property_defs.append([code, name, kind, "Requirement", "general-cis-csc-8-1", f"{name} of each CIS Controls v8.1 safeguard (CIS Controls v8.1.2 workbook)."])
    cis_rows = list(openpyxl.load_workbook(cis_path, read_only=True, data_only=True)["Controls v8.1.2"].iter_rows(min_row=2, values_only=True))
    obj("CIS-CONTROLS-8.1.2", "Controls v8.1.2", "fw.RequirementProperty", len(cis_rows))
    for row in cis_rows:
        safeguard = str(row[1]).strip().replace("\xa0", "") if row[1] else None
        if not safeguard or not re.match(r"^\d+\.\d+$", safeguard) or ("general-cis-csc-8-1", safeguard) not in requirements:
            continue
        for code, value in (("CIS_ASSET_CLASS", row[2]), ("CIS_SECURITY_FUNCTION", row[3])):
            if value:
                requirement_props.append(["general-cis-csc-8-1", safeguard, code, one_line(str(value)), None, None])
        for code, value in (("CIS_IG1", row[6]), ("CIS_IG2", row[7]), ("CIS_IG3", row[8])):
            requirement_props.append(["general-cis-csc-8-1", safeguard, code, None, None, 1 if value and str(value).strip() else 0])
    cmmc_path = os.path.join(docs, "Frameworks", "CMMC 2.0 Compliance Baseline.xlsx")
    source("GDS-CMMC-2.0-BASELINE", "CMMC 2.0 Compliance Baseline", "Global Data Systems", "Workbook", "2.0", None, "Frameworks/CMMC 2.0 Compliance Baseline.xlsx",
           cmmc_path, "GDS-INTERNAL", True)
    cmmc_codes = {norm_cmmc(code): (fw, code) for fw, code in requirements if fw.startswith("usa-federal-dow-cmmc-2-level-")}
    for code, name, kind in (("CMMC_SPRS_POINTS", "SPRS points deducted when not met", "Number"), ("CMMC_PARTIAL_DEDUCTION", "SPRS deduction when partially met", "Number"),
                             ("CMMC_POAM_ELIGIBLE", "Can be on a POA&M", "Boolean"), ("CMMC_BASIC_DERIVED", "Basic or derived requirement (NIST SP 800-171)", "Text"),
                             ("CMMC_NIST_171", "NIST SP 800-171 identifier", "Text")):
        property_defs.append([code, name, kind, "Requirement", None, f"{name} (GDS CMMC 2.0 compliance baseline workbook; 32 CFR 170.24)."])
    wb = openpyxl.load_workbook(cmmc_path, read_only=True, data_only=True)
    sprs_values = []
    requirement_capabilities = Table([0, 1, 2, 3])
    for sheet in ("Level 1", "Level 2", "Level 3"):
        rows = list(wb[sheet].iter_rows(values_only=True))
        header = [str(h).strip() if h else "" for h in rows[1]]
        col = {h: i for i, h in enumerate(header)}
        obj("GDS-CMMC-2.0-BASELINE", sheet, "fw.RequirementProperty; score.EnrichmentValue", len(rows) - 2)
        for row in rows[2:]:
            if not row[0]:
                continue
            target = cmmc_codes.get(norm_cmmc(str(row[0])))
            if not target:
                issues.append(["INFO", "UNMATCHED_REQUIREMENT", f"CMMC workbook {sheet}: {row[0]} has no knowledge-base requirement"])
                continue
            fw, code = target
            if "SPRS Points" in col and isinstance(row[col["SPRS Points"]], (int, float)):
                requirement_props.append([fw, code, "CMMC_SPRS_POINTS", None, float(row[col["SPRS Points"]]), None])
                sprs_values.append(["SPRS_POINTS", "Requirement", (fw, code), str(row[col["SPRS Points"]]), float(row[col["SPRS Points"]]), None])
            if "Partially Met Deduction" in col and isinstance(row[col["Partially Met Deduction"]], (int, float)):
                requirement_props.append([fw, code, "CMMC_PARTIAL_DEDUCTION", None, float(row[col["Partially Met Deduction"]]), None])
            if "POA&M Eligible" in col and row[col["POA&M Eligible"]] is not None:
                requirement_props.append([fw, code, "CMMC_POAM_ELIGIBLE", None, None, 1 if str(row[col["POA&M Eligible"]]).strip().upper().startswith("Y") else 0])
            if "Basic / Derived" in col and row[col["Basic / Derived"]]:
                requirement_props.append([fw, code, "CMMC_BASIC_DERIVED", str(row[col["Basic / Derived"]]), None, None])
            tooling_column = next((h for h in col if h.startswith("Suggested Microsoft Tooling")), None)
            tooling = one_line(str(row[col[tooling_column]] or "")) if tooling_column else ""
            for needle, product in TOOLING_PRODUCTS:
                if tooling and needle.lower() in tooling.lower():
                    requirement_capabilities.add([fw, code, f"PRODUCT-{product}", "CMMC-WORKBOOK", fit("kb.RequirementCapability", "ToolingText", tooling)])
            nist = next((row[col[h]] for h in ("NIST SP 800-171 ID", "NIST SP 800-171 Mapping", "NIST SP 800-172 ID") if h in col and row[col[h]]), None)
            if nist:
                requirement_props.append([fw, code, "CMMC_NIST_171", one_line(str(nist)), None, None])
    itar_path = os.path.join(docs, "Frameworks", "CMMC ITAR Compliance Crosswalk.xlsx")
    source("GDS-ITAR-CROSSWALK", "CMMC ITAR Compliance Crosswalk", "Global Data Systems", "Workbook", None, None, "Frameworks/CMMC ITAR Compliance Crosswalk.xlsx", itar_path,
           "GDS-INTERNAL", True)
    rows = list(openpyxl.load_workbook(itar_path, read_only=True, data_only=True)["Export Control Classification"].iter_rows(values_only=True))
    obj("GDS-ITAR-CROSSWALK", "Export Control Classification", "fw.ScopeCategory", len(rows))
    for row in rows[2:]:
        if row and row[1] and row[2]:
            scope_categories.append(["usa-federal-dow-cmmc-2-level-2", fit("fw.ScopeCategory", "Code", f"USML-{one_line(str(row[1]))}"),
                                     fit("fw.ScopeCategory", "Name", f"USML Category {row[1]}: {one_line(str(row[2]))}"), fit("fw.ScopeCategory", "SuggestedLabel", row[3]),
                                     "ITAR USML category (22 CFR 121.1) from the GDS ITAR crosswalk."])
    scope_categories += [
        ["usa-federal-dow-cmmc-2-level-1", "FCI", "Federal Contract Information", "FCI", "Information provided by or generated for the U.S. Government under contract (FAR 52.204-21)."],
        ["usa-federal-dow-cmmc-2-level-2", "CUI", "Controlled Unclassified Information", "CUI", "Information that requires safeguarding under 32 CFR 2002 (NIST SP 800-171)."],
        ["general-pci-dss-4-0-1", "CDE", "Cardholder data environment", "PCI CDE", "Systems that store, process or transmit cardholder or sensitive authentication data."],
        ["usa-federal-law-hipaa-security-rule-2013", "EPHI", "Electronic protected health information", "ePHI", "PHI created, received, maintained or transmitted electronically."],
    ]
    status_labels = []
    labels = {"Met": "Implemented", "Not Met": "NotImplemented", "Partially Met": "PartiallyImplemented", "Not Applicable": "NotApplicable",
              "In Place": "Implemented", "In Place with CCW": "CompensatingControl", "Not in Place": "NotImplemented", "Not Tested": "NotAssessed",
              "Not Started": "NotImplemented", "In Progress": "Planned", "Implemented": "Implemented", "Yes": "Implemented", "No": "NotImplemented",
              "Partial": "PartiallyImplemented", "N/A": "NotApplicable", "Complete": "Implemented", "Blocked": "Planned", "Customer Declined": "RiskAccepted"}
    for framework, values in (("usa-federal-dow-cmmc-2-level-1", ["Met", "Not Met", "Not Applicable"]),
                              ("usa-federal-dow-cmmc-2-level-2", ["Met", "Not Met", "Partially Met", "Not Applicable"]),
                              ("general-pci-dss-4-0-1", ["In Place", "In Place with CCW", "Not Applicable", "Not Tested", "Not in Place"]),
                              ("general-iso-27002-2022", ["Yes", "Partial", "No"]), ("general-nist-csf-2-0", ["Yes", "No"])):
        for value in values:
            status_labels.append([framework, None, value, labels[value]])
    for value in ["Yes", "No", "Partial", "N/A", "Not Started", "In Progress", "Complete", "Blocked", "Customer Declined"]:
        status_labels.append([None, "GDS", value, labels[value]])

    # ---------------- Scoring model and enrichment (GDS research report) ----------------
    report = os.path.join(docs, "EaaS", "control-attribute-data-sources-report.md")
    source("GDS-CONTROL-ATTRIBUTE-REPORT", "Data sources for control attributes (GDS research)", "Global Data Systems", "Policy", "2026-11-04", "2026-11-04",
           "EaaS/control-attribute-data-sources-report.md", report, "GDS-INTERNAL", True)
    attributes = [
        ["SCF_WEIGHT", "SCF relative control weighting", None, "Integer", "Points", 0, "SCF 1–10 weighting (SCF 2026.3)."],
        ["LIKELIHOOD", "Likelihood", "NIST-800-30", "Rating", None, 0, "NIST SP 800-30 likelihood; SCF threat likelihood when a control fails (Likely = High, Possible = Moderate, Unlikely = Low)."],
        ["IMPACT", "Impact", "NIST-800-30", "Rating", None, 1, "NIST SP 800-30 impact; from the SCF control weight until the customer rates business impact."],
        ["RISK_SCORE", "Risk score", None, "Decimal", "Points", 1, "Likelihood × impact on 0–100."],
        ["SPRS_POINTS", "SPRS points (CMMC 2.0)", None, "Integer", "Points", 0, "Points deducted from 110 when the requirement is not met (32 CFR 170.24)."],
        ["CHECKLIST_POINTS", "Customer Deployment Checklist points", None, "Integer", "Points", 0, "Points of the GDS Customer Deployment Checklist v2.0."],
        ["EUX_IMPACT", "End-user impact", "GDS-LOW-MEDIUM-HIGH", "Rating", None, 0, "Low / Medium / High; CIS Level 1 = Low, Level 2 = Medium (CIS wording via the GDS research report)."],
        ["SEVERITY", "Severity", "GDS-LOW-MEDIUM-HIGH", "Rating", None, 0, "Catalog severity."],
        ["DIFFICULTY", "Implementation difficulty", "GDS-LOW-MEDIUM-HIGH", "Rating", None, 0, "Catalog implementation difficulty."],
        ["RISK_LEVEL", "Change risk level", "GDS-LOW-MEDIUM-HIGH", "Rating", None, 0, "Risk of the change itself (catalog)."],
        ["EFFORT_MINUTES", "Estimated effort (minutes)", None, "Integer", "Minutes", 0, "Catalog estimate per control."],
        ["EFFORT_HOURS_MIN", "Effort hours (minimum)", None, "Decimal", "Hours", 0, "WBS estimate."],
        ["EFFORT_HOURS_MAX", "Effort hours (maximum)", None, "Decimal", "Hours", 0, "WBS estimate."],
        ["CALENDAR_DAYS_MAX", "Calendar days (maximum)", None, "Decimal", "Days", 0, "WBS estimate."],
        ["ATTACK_TECHNIQUES", "ATT&CK techniques countered", None, "Integer", "Count", 0, "Number of ATT&CK techniques the SCF control counters (SCF 2026.3)."],
    ]
    models = [["GDS-RISK-DEFAULT", "GDS default risk model", "1.0",
               "RiskScore = Likelihood(0–10) × Impact(0–10), on 0–100. Impact = SCF weight (1–10) until the customer rates business impact; "
               "weighted for ranking by SPRS points (5/3/1) and checklist points where they exist.", 1,
               "The method recommended by the GDS control-attribute data sources report (NIST SP 800-30 likelihood × impact, weighted by SPRS points, SCF weights "
               "and points). Weights are a starting point for GDS review."]]
    weights = [["GDS-RISK-DEFAULT", "LIKELIHOOD", 0.5, "Linear"], ["GDS-RISK-DEFAULT", "IMPACT", 0.5, "Linear"]]
    rules = [
        ["CIS-L1-EUX-LOW", "EUX_IMPACT", 1, "Recommendation", "CIS Level 1 recommendations: end-user impact Low (CIS: 'practical and prudent').", "LevelText LIKE 'L1%' -> Low"],
        ["CIS-L2-EUX-MEDIUM", "EUX_IMPACT", 2, "Recommendation", "CIS Level 2 recommendations: end-user impact Medium (CIS: 'may negatively inhibit the utility').", "LevelText LIKE '%L2%' -> Medium"],
        ["SCF-WEIGHT-IMPACT", "IMPACT", 1, "Requirement", "Impact of an SCF control = its SCF weight (1–10) until the customer rates business impact.", "IMPACT = SCF_WEIGHT"],
        ["SCF-THREAT-LIKELIHOOD", "LIKELIHOOD", 1, "Requirement", "Likelihood of an SCF control = the highest SCF threat likelihood when it fails: Likely 8, Possible 5, Unlikely 2.",
         "max(ControlThreat) -> 800-30"],
        ["WBS-EFFORT", "EFFORT_HOURS_MIN", 1, "StandardItem", "Effort of a WBS task = its estimated minimum and maximum hours.", "EstHoursMin/EstHoursMax"],
        ["CATALOG-USER-IMPACT", "EUX_IMPACT", 1, "Artifact", "End-user impact of a catalog policy = its catalog user impact.", "userImpact"],
    ]
    precedence = [[attribute, source_code, rank] for attribute, sources_ in (
        ("EUX_IMPACT", ["GDS-CATALOG", "GDS-CHECKLIST", "CIS-BENCHMARK"]), ("LIKELIHOOD", ["SCF-STRM", "GDS-SCORING"]), ("IMPACT", ["GDS-SCORING", "SCF-STRM"]),
        ("SPRS_POINTS", ["CMMC-WORKBOOK"]), ("SEVERITY", ["GDS-CATALOG"]), ("EFFORT_HOURS_MIN", ["GDS-STANDARDS", "GDS-CATALOG"])) for rank, source_code in enumerate(sources_, start=1)]
    enrichment, item_scores = [], []
    likelihood_points = {"Likely": 8, "Possible": 5, "Unlikely": 2}
    worst = {}
    for control, _threat, label in phase1["controlThreats"]:
        worst[control] = max(worst.get(control, 0), likelihood_points.get(label, 0))
    technique_counts = collections.Counter(row[0] for row in phase1["requirementTechniques"])
    scored = []
    for control in phase1["scfControls"]:
        code, weight = control["code"], control.get("weight")
        if weight is not None:
            enrichment.append(["SCF_WEIGHT", "Requirement", ("scf", code), str(weight), float(weight), None, "SCF-STRM", "Authoritative", "High", None])
        if worst.get(code):
            label = {8: "High", 5: "Moderate", 2: "Low"}[worst[code]]
            enrichment.append(["LIKELIHOOD", "Requirement", ("scf", code), label, float(worst[code]), label, "GDS-SCORING", "Rule", "Medium", "SCF-THREAT-LIKELIHOOD"])
        if technique_counts.get(code):
            enrichment.append(["ATTACK_TECHNIQUES", "Requirement", ("scf", code), str(technique_counts[code]), float(technique_counts[code]), None, "SCF-STRM", "Crosswalk", "High", None])
        if weight is not None and worst.get(code):
            scored.append((code, float(weight), float(worst[code]), round(worst[code] * float(weight), 2)))
    for rank, (code, weight, likelihood, risk) in enumerate(sorted(scored, key=lambda s: (-s[3], s[0])), start=1):
        item_scores.append(["GDS-RISK-DEFAULT", "Requirement", ("scf", code), weight, likelihood, weight, risk, rank, "Medium"])
    for value in sprs_values:
        enrichment.append(value + ["CMMC-WORKBOOK", "Authoritative", "High", None])
    for row in phase2["recommendations"]:
        level = row[7] or ""
        if row[0].startswith("CIS-") and re.match(r"L[12]", level):
            label = "Medium" if "L2" in level and "L1" not in level else "Low"
            enrichment.append(["EUX_IMPACT", "Recommendation", (row[0], row[1]), level, None, label, "CIS-BENCHMARK", "Rule", "Medium",
                               "CIS-L2-EUX-MEDIUM" if label == "Medium" else "CIS-L1-EUX-LOW"])
    for task in phase2["wbsTasks"]:
        for attribute, value in (("EFFORT_HOURS_MIN", task[16]), ("EFFORT_HOURS_MAX", task[17]), ("CALENDAR_DAYS_MAX", task[20])):
            if value is not None:
                enrichment.append([attribute, "WbsTask", (task[0], task[1]), str(value), float(value), None, "GDS-STANDARDS", "GdsInternal", "Medium", "WBS-EFFORT"])
    for value in checklist_values:
        enrichment.append(value + ["GDS-CHECKLIST", "GdsInternal", "High", None])
    for value in catalog_values:
        enrichment.append(value + ["GDS-CATALOG", "GdsInternal", "Medium", "CATALOG-USER-IMPACT" if value[0] == "EUX_IMPACT" else None])

    # ---------------- Delivery: complexity factors ----------------
    complexity = sorted({task[23] for task in phase2["wbsTasks"] if task[23]})
    def factor_code(name):
        code = slug(name, 40)
        return code if len(slug(name, 200)) <= 40 else f"{slug(name, 35)}-{hashlib.sha256(name.encode()).hexdigest()[:4].upper()}"

    complexity_factors = [[factor_code(name), fit("dlv.ComplexityFactor", "Name", name),
                           "Complexity driver named in the Secure Plus WBS; multipliers to be set by GDS."] for name in complexity]
    task_factors = [[task[0], task[1], factor_code(task[23])] for task in phase2["wbsTasks"] if task[23]]

    source("GDS-EAAS-QUESTION-BANK", "EaaS question bank", "Global Data Systems", "Dataset", bank.get("version"), bank.get("generated"),
           "EaaS/Questionnaire/GDS_EaaS_Question_Bank.json", os.path.join(docs, "EaaS", "Questionnaire", "GDS_EaaS_Question_Bank.json"), "GDS-INTERNAL", True)
    obj("GDS-EAAS-QUESTION-BANK", "experienceProfiles", "eux.Expectation; eux.AnswerExpectation; eux.ExpectationImpact", len(bank["experienceProfiles"]))
    obj("GDS-EAAS-QUESTION-BANK", "F2_TN_04 (rollout rings)", "deploy.Ring", 3)

    out.update({
        "schemaVersion": 1, "version": VERSION, "licenses": LICENSES, "mappingSources": MAPPING_SOURCES, "ratingScales": RATING_SCALES, "products": PRODUCTS,
        "sources": sorted(sources.rows, key=lambda r: r[0]), "sourceObjects": objects.rows,
        "licenseSkus": sorted(skus.rows, key=lambda r: r[0]), "capabilities": sorted(capabilities.rows, key=lambda r: r[0]), "skuCapabilities": sku_caps.rows,
        "extraTechniques": sorted(extra_techniques.values()), "capabilityTechniques": capability_techniques.rows, "requirementTechniques": requirement_techniques.rows,
        "serviceTiers": tier_rows, "tierFeatures": tier_features, "tierCapabilities": tier_capabilities.rows, "tierBaseSkus": [[t, s[0]] for t, s in base_skus.items()],
        "checklistItems": checklist_items.rows, "checklistTiers": checklist_tiers, "checklistGuidance": checklist_guidance,
        "configurationChanges": changes.rows, "changeRequirements": change_requirements.rows, "itemRequirements": item_requirements.rows,
        "knownIssues": known_issues.rows, "knownIssueLinks": issue_links, "guidance": guidance,
        "previousBaselines": previous_baselines, "baselineChanges": baseline_changes, "relations": relations.rows, "procedures": procedures,
        "expectations": expectations, "answerExpectations": answer_expectations, "expectationImpacts": expectation_impacts, "rings": rings,
        "propertyDefinitions": property_defs, "requirementProperties": requirement_props, "scopeCategories": scope_categories, "statusLabels": status_labels,
        "requirementCapabilities": requirement_capabilities.rows,
        "attributes": attributes, "scoringModels": models, "scoringWeights": weights, "derivationRules": rules, "sourcePrecedence": precedence,
        "enrichment": enrichment, "itemScores": item_scores, "complexityFactors": complexity_factors, "taskFactors": task_factors,
        "loadIssues": issues + [["INFO", "CORRECTED", text] for text in phase2.get("issues", [])],
    })
    for dataset, rows in out.items():
        if isinstance(rows, list):
            for row in rows:
                for index, value in enumerate(row):
                    if isinstance(value, str):
                        row[index] = value.replace(" ", " ")
    out["counts"] = {name: len(value) for name, value in out.items() if isinstance(value, list)}
    out["truncated"] = dict(sorted(gb.TRUNCATED.items()))
    data = json.dumps(out, ensure_ascii=False, separators=(",", ":"), default=list).encode("utf-8")
    with open(args.output, "wb") as handle:
        handle.write(gzip.compress(data, compresslevel=9, mtime=0))
    print(json.dumps({"counts": out["counts"], "truncated": out["truncated"], "sha256": hashlib.sha256(data).hexdigest(), "bytes": os.path.getsize(args.output)}, indent=1))


if __name__ == "__main__":
    main()
