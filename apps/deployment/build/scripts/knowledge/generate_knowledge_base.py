#!/usr/bin/env python3
"""Builds the Secure Plus knowledge base (phase 1: SCF, NIST SP 800-53 / 800-53A, MITRE ATT&CK) from the Documents repository.

Usage:
  python3 build/scripts/knowledge/generate_knowledge_base.py --documents /path/to/Documents \
      --catalog src/EngineeringWorkspace.Infrastructure/Catalog/catalog.json.gz \
      --output src/EngineeringWorkspace.Infrastructure/KnowledgeBase/knowledge-base.json.gz

Sources (Documents/Frameworks):
  SCF - Secure Controls Frameworks 2026.xlsx       SCF controls, domains, focal documents and their mappings, assessment
                                                   objectives, evidence request list, risk and threat catalogs, compensating
                                                   controls, maturity criteria, control lineage
  NIST_SP-800-53_rev5-derived-OSCAL.xlsx           full text of NIST SP 800-53 Rev 5 controls
  NIST_SP-800-53ar5-assessment-procedures.xlsx     NIST SP 800-53A Rev 5 assessment objectives (examine / interview / test)
  MITRE ATT&CK Enterprise Matrix.xlsx              tactics, techniques, mitigations and the technique-to-mitigation map
The output is deterministic (sorted, no timestamps) so its SHA-256 identifies the content.
"""
import argparse
import gzip
import hashlib
import json
import re
import sys

import openpyxl

KB_VERSION = "2026.3-1"  # SCF 2026.3, generator revision 1

SCF_FILE = "Frameworks/SCF - Secure Controls Frameworks 2026.xlsx"
NIST_TEXT_FILE = "Frameworks/NIST_SP-800-53_rev5-derived-OSCAL.xlsx"
NIST_AO_FILE = "Frameworks/NIST_SP-800-53ar5-assessment-procedures.xlsx"
MITRE_FILE = "Frameworks/MITRE ATT&CK Enterprise Matrix.xlsx"

SCF_CODE = "scf"
CSF_FUNCTIONS = {"govern": "GV", "identify": "ID", "protect": "PR", "detect": "DE", "respond": "RS", "recover": "RC"}
REGULATION_WORDS = ("act", "law", "cfr", "rule", "regulation", "directive", "decree", "ordinance", "bill", " sb", "hb ", "pipa", "gdpr", "ccpa", "hipaa")


def text(value, limit=None):
    if value is None:
        return None
    value = str(value).strip()
    if not value or value.lower() in ("none", "n/a"):
        return None
    return value[:limit] if limit else value


def one_line(value):
    return re.sub(r"\s+", " ", str(value or "")).strip()


def codes(cell):
    """A mapping cell holds one requirement code per line."""
    if cell is None:
        return []
    out = []
    for line in str(cell).replace("\r", "\n").split("\n"):
        line = line.strip()
        if line and line.lower() not in ("none", "n/a", "x"):
            out.append(line[:100])
    return out


def nist_key(code):
    """AC-01(01) and AC-1(1) compare equal."""
    return re.sub(r"(?<![0-9])0+(?=[0-9])", "", code.upper().replace(" ", ""))


def nist_padded(code):
    """AC-2(1) -> AC-02(01), the form used by SCF and NIST SP 800-53A."""
    match = re.match(r"^([A-Z]{2})-(\d+)(?:\((\d+)\))?$", code.strip().upper())
    if not match:
        return code.strip().upper()
    family, number, enhancement = match.groups()
    return f"{family}-{int(number):02d}" + (f"({int(enhancement):02d})" if enhancement else "")


def cmmc_key(code):
    match = re.search(r"(\d+\.\d+\.\d+)", code)
    return match.group(1) if match else code.upper()


def sheet_rows(workbook, name, header_row):
    rows = list(workbook[name].iter_rows(min_row=header_row, values_only=True))
    return rows[0], rows[1:]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--documents", required=True)
    parser.add_argument("--catalog", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    root = args.documents.rstrip("/") + "/"

    scf = openpyxl.load_workbook(root + SCF_FILE, read_only=True, data_only=True)
    header, control_rows = sheet_rows(scf, "SCF 2026.3", 1)
    header = [one_line(h) for h in header]
    control_rows = [row for row in control_rows if text(row[2])]
    col = {name: index for index, name in enumerate(header)}

    # ----- Mapping sources and rating scales -----
    mapping_sources = [
        {"code": "SCF-STRM", "name": "Secure Controls Framework mappings (2026.3)", "publisher": "SCF Council", "authority": "Crosswalk",
         "confidence": 70, "url": "https://securecontrolsframework.com"},
        {"code": "NIST-800-53A", "name": "NIST SP 800-53A Rev 5 assessment procedures", "publisher": "NIST", "authority": "Authoritative",
         "confidence": 100, "url": "https://csrc.nist.gov/pubs/sp/800/53/a/r5/final"},
        {"code": "MITRE-ATTACK", "name": "MITRE ATT&CK Enterprise", "publisher": "MITRE", "authority": "Authoritative", "confidence": 100,
         "url": "https://attack.mitre.org"},
        {"code": "GDS-ALIAS", "name": "Secure Plus catalog references matched to SCF focal documents", "publisher": "Global Data Systems",
         "authority": "Derived", "confidence": 90, "url": None},
    ]
    rating_scales = [{
        "code": "SCF-LIKELIHOOD", "name": "SCF likelihood if the control fails", "min": 1, "max": 3, "higherIsWorse": True,
        "description": "Likelihood that a risk materializes or a threat succeeds when the SCF control is not implemented (SCF 2026.3).",
        "values": [[1, "Unlikely", 0, 33.33, "#2E7D32"], [2, "Possible", 33.34, 66.66, "#F9A825"], [3, "Likely", 66.67, 100, "#C62828"]],
    }]

    # ----- Frameworks: the SCF hub and every focal document -----
    fd_header, fd_rows = sheet_rows(scf, "Focal Documents (FD)", 1)
    frameworks = [{
        "code": SCF_CODE, "name": "Secure Controls Framework", "short": "SCF 2026.3", "publisher": "SCF Council", "type": "Metaframework",
        "geography": "General", "jurisdiction": None, "isHub": True, "fdi": None, "column": None, "url": "https://securecontrolsframework.com",
        "strmUrl": None, "version": "2026.3", "hasFullText": True,
        "description": "The SCF metaframework: the hub every focal document is mapped to.",
    }]
    column_framework = {}
    for row in fd_rows:
        if not text(row[2]):
            continue
        column_header = one_line(row[1])
        fdi = text(row[2], 80)
        name = one_line(row[4]) or column_header
        geography = text(row[0], 30) or "General"
        lowered = f" {name.lower()} "
        version_match = re.search(r"(\d[\w.:/-]*)$", column_header)
        frameworks.append({
            "code": fdi, "name": name[:400], "short": column_header[:100], "publisher": text(row[3], 200), "geography": geography,
            "type": "Regulation" if any(word in lowered for word in REGULATION_WORDS) else "Standard",
            "jurisdiction": " ".join(column_header.split(" ")[1:2])[:100] if geography in ("USA", "EMEA", "APAC", "Americas") else None,
            "isHub": False, "fdi": fdi, "column": column_header[:200], "url": text(row[5], 1000), "strmUrl": text(row[6], 1000),
            "version": (version_match.group(1) if version_match else "Current")[:50], "hasFullText": False, "description": None,
        })
        column_framework[column_header] = fdi
    mapped_columns = {index: column_framework[name] for index, name in enumerate(header) if name in column_framework}
    missing = [name for name in column_framework if name not in col]
    if missing:
        print(f"warning: {len(missing)} focal documents have no mapping column: {missing[:5]}", file=sys.stderr)

    # ----- Requirements -----
    requirements = {}  # (framework, code) -> record
    # SQL Server compares codes without case and trailing spaces (the default collation), so two codes that differ only
    # that way are one requirement: the first spelling seen wins (the SCF workbook has e.g. 19(3)(c)(i) and 19(3)(c)(I)).
    spelling = {}  # (framework, folded code) -> code

    def add_requirement(framework, code, **fields):
        code = spelling.setdefault((framework, code.rstrip().casefold()), code.rstrip())
        key = (framework, code)
        record = requirements.get(key)
        if record is None:
            record = {"framework": framework, "code": code, "matchKey": code.upper()[:100], "sortKey": code[:100], "level": "Requirement",
                      "title": None, "statement": None, "discussion": None, "isStub": True, "parent": None}
            requirements[key] = record
        for name, value in fields.items():
            if value is not None:
                record[name] = value
        return record

    for row in control_rows:
        scf_code = text(row[col["SCF #"]], 100)
        parent = scf_code.rsplit(".", 1)[0] if "." in scf_code else None
        add_requirement(SCF_CODE, scf_code, title=text(row[col["SCF Control"]], 500), statement=text(row[3]), level="Enhancement" if parent else "Control",
                        isStub=False, parent=parent, sortKey=scf_code)

    nist_fd = column_framework.get("NIST 800-53 R5.2")
    maps = []  # [fromFramework, fromCode, toFramework, toCode, relationship, source]
    for row in control_rows:
        scf_code = text(row[col["SCF #"]], 100)
        for index, framework in mapped_columns.items():
            for code in codes(row[index]):
                code = add_requirement(framework, code)["code"]
                maps.append([SCF_CODE, scf_code, framework, code, "IntersectsWith", "SCF-STRM"])

    # NIST SP 800-53 Rev 5 full text on the SCF focal document NIST 800-53 R5.2.
    nist_text = openpyxl.load_workbook(root + NIST_TEXT_FILE, read_only=True, data_only=True)
    nist_rows = list(nist_text["SP-800-53_rev5"].iter_rows(min_row=2, values_only=True))
    for row in nist_rows:
        if not text(row[0]):
            continue
        code = nist_padded(row[0])
        base = code.split("(")[0]
        add_requirement(nist_fd, code, title=text(row[1], 500), statement=text(row[2]), discussion=text(row[3]), isStub=False,
                        level="Enhancement" if "(" in code else "Control", parent=base if "(" in code else None, matchKey=nist_key(code)[:100])
    for framework in frameworks:
        if framework["code"] == nist_fd:
            framework["hasFullText"] = True
            framework["description"] = "Control text from NIST SP 800-53 Rev 5 (OSCAL); requirement codes as mapped by SCF 2026.3."

    # ----- SCF domains, controls, lineage, compensating controls, maturity criteria -----
    domain_header, domain_rows = sheet_rows(scf, "SCF Domains & Principles", 1)
    domains = []
    domain_by_name = {}
    for row in domain_rows:
        if not text(row[2]):
            continue
        domains.append([text(row[2], 10), text(row[1], 200), text(row[3], 1000), text(row[4], 2000), int(row[0]) if str(row[0]).isdigit() else len(domains) + 1])
        domain_by_name[one_line(row[1]).lower()] = text(row[2], 10)

    def flag(value):
        return bool(text(value))

    scf_controls, compensating, maturity = [], [], []
    for row in control_rows:
        scf_code = text(row[col["SCF #"]], 100)
        pptdf = (text(row[col["PPTDF Applicability"]]) or "").lower()
        csf = CSF_FUNCTIONS.get((text(row[col["NIST CSF Function Grouping"]]) or "").split(",")[0].strip().lower())
        weight = row[col["Relative Control Weighting"]]
        scf_controls.append({
            "code": scf_code, "domain": domain_by_name.get(one_line(row[0]).lower()), "weight": int(weight) if isinstance(weight, (int, float)) else None,
            "cadence": text(row[col["Conformity Validation Cadence"]], 30), "erl": text(row[col["Evidence Request List (ERL) #"]], 200),
            "people": "people" in pptdf, "process": "process" in pptdf, "technology": "technology" in pptdf, "data": "data" in pptdf, "facility": "facility" in pptdf,
            "csf": csf, "tier1": flag(row[16]), "tier2": flag(row[17]), "tier3": flag(row[18]), "question": text(row[12], 1000), "risk": text(row[19]),
            "micro": text(row[7]), "small": text(row[8]), "medium": text(row[9]), "large": text(row[10]), "enterprise": text(row[11]),
            "orphaned": flag(row[34]), "legacy": text(row[4], 20), "errata": text(row[col.get("Errata 2026.3", 430)], 500),
        })
        for sequence, base in ((1, 20), (2, 24)):
            if text(row[base]):
                compensating.append([scf_code, sequence, text(row[base], 100), text(row[base + 1], 300), text(row[base + 2]), text(row[base + 3])])
        for level in range(6):
            if text(row[28 + level]):
                maturity.append([scf_code, f"L{level}", text(row[28 + level])])

    lineage = []
    change_header, change_rows = sheet_rows(scf, "Change Overview", 1)
    for row in change_rows:
        if not text(row[2]):
            continue
        errata = text(row[5]) or ""
        changes = [line.strip(" -").strip() for line in errata.split("\n") if line.strip(" -").strip()]
        lineage.append([text(row[2], 100), text(row[4], 20), (changes[0] if changes else "unchanged")[:30], errata[:1000] or None])

    # SCF CORE sets as profiles of the SCF framework.
    profiles = []
    for name in header:
        if name.startswith("SCF CORE") or name in ("SCF Community Derived", "SCF SCRMS"):
            members = sorted({text(row[col["SCF #"]], 100) for row in control_rows if text(row[col[name]])})
            profiles.append({"framework": SCF_CODE, "code": name.replace("SCF ", "").replace(" ", "-").upper()[:60], "name": name[:200], "type": "SCF Set",
                             "requirements": members})

    # ----- Assessment objectives: SCF and NIST SP 800-53A -----
    objectives = []  # [framework, requirementCode, code, parent, text, sdp, pptdf, rigor, origin, examine, interview, test, expected, sort]
    ao_header, ao_rows = sheet_rows(scf, "Assessment Objectives 2026.3", 1)
    for row in ao_rows:
        if not text(row[1]) or (SCF_CODE, text(row[0], 100)) not in requirements:
            continue
        rigor = row[6]
        objectives.append([SCF_CODE, text(row[0], 100), text(row[1], 60), None, text(row[2]), text(row[4]), text(row[5], 50),
                           int(rigor) if str(rigor).isdigit() else None, text(row[7], 200), None, None, text(row[16]), text(row[17]), text(row[1], 60)])
    nist_ao = openpyxl.load_workbook(root + NIST_AO_FILE, read_only=True, data_only=True)
    nist_objectives = {}
    for row in list(nist_ao.worksheets[0].iter_rows(min_row=2, values_only=True)):
        identifier = text(row[1], 60)
        if not identifier:
            continue
        match = re.match(r"^([A-Z]{2}-\d{2}(?:\(\d{2}\))?)", identifier)
        if not match:
            continue
        control = match.group(1)
        add_requirement(nist_fd, control, matchKey=nist_key(control)[:100])
        record = [nist_fd, control, identifier, None, text(row[4]), None, None, None, "NIST SP 800-53A Rev 5", text(row[5]), text(row[6]),
                  text(row[7]), None, text(row[2], 60) or identifier]
        existing = nist_objectives.get(identifier)
        if existing is None:
            nist_objectives[identifier] = record
            continue
        # Enhancements appear twice: a "Determine if:" line (with examine/interview/test) and the objective text. Merge them.
        existing[4] = " ".join(part for part in (existing[4], record[4]) if part)
        for index in (9, 10, 11):
            existing[index] = existing[index] or record[index]
        existing[13] = min(existing[13], record[13])
    objectives.extend(nist_objectives.values())

    # ----- Evidence request list -----
    erl_header, erl_rows = sheet_rows(scf, "Evidence Request List 2026.3", 1)
    evidence, requirement_evidence = [], []
    for row in erl_rows:
        erl = text(row[1], 20)
        if not erl:
            continue
        evidence.append([erl, text(row[2], 200), text(row[3], 300), text(row[4]), text(row[7], 200)])
        for scf_code in codes(row[5]):
            if (SCF_CODE, scf_code) in requirements:
                requirement_evidence.append([scf_code, erl])

    # ----- Risks, threats and their likelihood per SCF control -----
    risks, threats = [], []
    group = None
    for row in list(scf["Risk Catalog"].iter_rows(values_only=True)):
        if text(row[1]) and re.match(r"^R-[A-Z]{2}-\d+$", str(row[1]).strip()):
            group = text(row[0], 100) or group
            risks.append([text(row[1], 20), group, text(row[3], 200), text(row[4], 2000), CSF_FUNCTIONS.get((text(row[5]) or "").lower(), text(row[5], 10)),
                          text(row[6], 30), text(row[7], 30), text(row[8], 30), text(row[9], 30)])
    group = None
    for row in list(scf["Threat Catalog"].iter_rows(values_only=True)):
        if text(row[1]) and re.match(r"^(NT|MT)-\d+$", str(row[1]).strip()):
            group = text(row[0], 100) or group
            threats.append([text(row[1], 20), group, text(row[2], 200), text(row[3], 2000), text(row[4], 30), text(row[5], 30), text(row[6], 30), text(row[7], 30)])
    risk_codes = {risk[0] for risk in risks}
    threat_codes = {threat[0] for threat in threats}
    control_risks, control_threats = [], []
    for row in control_rows:
        scf_code = text(row[col["SCF #"]], 100)
        for index, name in enumerate(header):
            label = text(row[index], 20)
            if not label:
                continue
            if name.startswith("Risk R-") and name[5:] in risk_codes:
                control_risks.append([scf_code, name[5:], label])
            elif name.startswith("Threat ") and name[7:] in threat_codes:
                control_threats.append([scf_code, name[7:], label])

    # ----- MITRE ATT&CK -----
    mitre = openpyxl.load_workbook(root + MITRE_FILE, read_only=True, data_only=True)
    tactic_header, tactic_rows = sheet_rows(mitre, "Tactics", 2)
    tactics = [[text(row[1], 10), text(row[2], 100), text(row[3], 500), text(row[4]), int(row[0]), text(row[5], 300)] for row in tactic_rows if text(row[1])]
    tactic_by_name = {tactic[1].lower(): tactic[0] for tactic in tactics}
    attack_version = (re.search(r"v(\d+)", str(list(mitre["Mitigations"].iter_rows(max_row=1, values_only=True))[0][0])) or [None, "20"])[1]
    technique_header, technique_rows = sheet_rows(mitre, "Techniques", 2)
    techniques, technique_tactics = [], []
    for row in technique_rows:
        code = text(row[0], 15)
        if not code:
            continue
        techniques.append([code, text(row[3], 15), text(row[1], 300), str(row[2]).strip().lower() == "yes", text(row[6]), text(row[7]), text(row[5], 300),
                           attack_version, text(row[9], 300)])
        for tactic in (text(row[4]) or "").split(","):
            if tactic.strip().lower() in tactic_by_name:
                technique_tactics.append([code, tactic_by_name[tactic.strip().lower()]])
    mitigation_header, mitigation_rows = sheet_rows(mitre, "Mitigations", 2)
    mitigations = [[text(row[0], 10), text(row[1], 200), text(row[2]), text(row[3], 300)] for row in mitigation_rows if text(row[0])]
    map_header, map_rows = sheet_rows(mitre, "Mitigation Map", 2)
    technique_codes = {technique[0] for technique in techniques}
    mitigation_codes = {mitigation[0] for mitigation in mitigations}
    technique_mitigations = sorted({(text(row[0], 15), text(row[2], 10)): text(row[4]) for row in map_rows
                                    if text(row[0]) in technique_codes and text(row[2]) in mitigation_codes}.items())
    technique_mitigations = [[technique, mitigation, how] for (technique, mitigation), how in technique_mitigations]
    attack_column = "MITRE ATT&CK 16"
    requirement_techniques = sorted({(text(row[col["SCF #"]], 100), code) for row in control_rows for code in codes(row[col[attack_column]])
                                     if code in technique_codes}) if attack_column in col else []

    # ----- Catalog references matched to SCF focal documents -----
    catalog = json.load(gzip.open(args.catalog))
    known = [("NIST 800-53", "NIST-800-53", "Rev 5"), ("NIST CSF", "NIST-CSF", "2.0"), ("CIS v8.1", "CIS-CONTROLS", "v8.1"), ("CIS v8", "CIS-CONTROLS", "v8"),
             ("ISO 27001:2022", "ISO-27001", "2022"), ("CMMC 2.0", "CMMC", "2.0"), ("PCI DSS 4.0.1", "PCI-DSS", "4.0.1"), ("PCI DSS 4.0", "PCI-DSS", "4.0"),
             ("HIPAA", "HIPAA", "45 CFR 164"), ("Essential Eight", "ESSENTIAL-EIGHT", "Current")]
    targets = {
        "NIST-800-53": [(column_framework.get("NIST 800-53 R5.2"), nist_key)],
        "NIST-CSF": [(column_framework.get("NIST CSF 2.0"), str.upper)],
        "CIS-CONTROLS": [(column_framework.get("CIS CSC 8.1"), lambda code: code.upper().removesuffix(".0") if code.endswith(".0") else code.upper())],
        "ISO-27001": [(column_framework.get("ISO 27001 2022"), str.upper), (column_framework.get("ISO 27002 2022"), lambda code: code.upper().removeprefix("A."))],
        "CMMC": [(column_framework.get("USA Federal CMMC 2.0 Level 2"), cmmc_key), (column_framework.get("USA Federal CMMC 2.0 Level 1"), cmmc_key)],
        "PCI-DSS": [(column_framework.get("PCI DSS 4.0.1"), str.upper)],
        "HIPAA": [(column_framework.get("USA Federal HIPAA Security Rule NIST SP 800-66 R2"), lambda code: re.sub(r"[^0-9a-z().]", "", code.lower()))],
        "ESSENTIAL-EIGHT": [(column_framework.get("APAC Australia Essential 8 2024"), str.upper)],
    }
    index = {}
    for (framework, code) in requirements:
        index.setdefault(framework, []).append(code)
    aliases, unmatched = set(), 0
    for control in catalog["controls"]:
        for label in control.get("frameworks", []):
            label = label.strip()
            hit = next((item for item in known if label.startswith(item[0]) and (len(label) == len(item[0]) or label[len(item[0])].isspace())), None)
            if not hit:
                continue
            prefix, catalog_code, catalog_version = hit
            requirement = label[len(prefix):].strip() or "General"
            found = False
            for target, normalise in targets.get(catalog_code, []):
                if not target:
                    continue
                if catalog_code == "ISO-27001" and (target == column_framework.get("ISO 27002 2022")) != requirement.upper().startswith("A."):
                    continue
                key = normalise(requirement.removeprefix("A.") if catalog_code == "ISO-27001" else requirement)
                for candidate in index.get(target, []):
                    if normalise(candidate) == key:
                        aliases.add((catalog_code, catalog_version, requirement[:100], target, candidate))
                        found = True
                if found:
                    break
            unmatched += 0 if found else 1
    aliases = sorted(aliases)

    # ----- Assemble -----
    used_frameworks = {framework for framework, _ in requirements}
    frameworks = [framework for framework in frameworks if framework["code"] in used_frameworks or framework["code"] == SCF_CODE]
    requirement_list = sorted(requirements.values(), key=lambda record: (record["framework"], record["sortKey"], record["code"]))
    data = {
        "schemaVersion": 1, "version": KB_VERSION, "attackVersion": attack_version,
        "sources": [SCF_FILE, NIST_TEXT_FILE, NIST_AO_FILE, MITRE_FILE],
        "mappingSources": mapping_sources, "ratingScales": rating_scales, "frameworks": frameworks,
        "requirements": [[r["framework"], r["code"], r["matchKey"], r["sortKey"], r["level"], r["title"], r["statement"], r["discussion"], r["isStub"], r["parent"]]
                         for r in requirement_list],
        "maps": sorted({tuple(item) for item in maps}), "aliases": aliases, "profiles": profiles,
        "domains": domains, "scfControls": scf_controls, "lineage": lineage, "compensating": compensating, "maturity": maturity,
        "objectives": objectives, "evidence": evidence, "requirementEvidence": sorted({tuple(item) for item in requirement_evidence}),
        "risks": risks, "threats": threats, "controlRisks": control_risks, "controlThreats": control_threats,
        "tactics": tactics, "techniques": techniques, "techniqueTactics": sorted({tuple(item) for item in technique_tactics}), "mitigations": mitigations,
        "techniqueMitigations": technique_mitigations, "requirementTechniques": requirement_techniques,
    }
    data["counts"] = {name: len(value) for name, value in data.items() if isinstance(value, list) and name != "sources"}
    payload = json.dumps(data, ensure_ascii=False, separators=(",", ":"), sort_keys=False, default=list).encode("utf-8")
    data_hash = hashlib.sha256(payload).hexdigest()
    with gzip.GzipFile(args.output, "wb", mtime=0) as handle:
        handle.write(payload)
    summary = dict(data["counts"])
    summary["catalogReferencesUnmatched"] = unmatched
    print(json.dumps({"version": KB_VERSION, "sha256": data_hash, "counts": summary}, indent=1))


if __name__ == "__main__":
    main()
