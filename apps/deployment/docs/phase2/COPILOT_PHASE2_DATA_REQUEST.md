# Copilot Request: Phase 2 Knowledge-Base Data for Secure Plus – Modern Work

> **How to use this prompt.** Paste everything below the line into Copilot. It has a shared **Data Contract** (read first) and eight **Work Packages**.
> - **GitHub Copilot (coding agent) in the `toby67maro/Documents` repository** is best for **WP1–WP4** and **WP8**. They need the PDFs in the repository, public downloads and a re-runnable parser script.
> - **Microsoft 365 Copilot (Researcher)** is best for **WP5–WP7**. They need GDS internal content in SharePoint, Teams and Outlook (the service tier deck, SOW templates, scoring decisions, lessons learned). Ask it to return each file as an Excel table, then save every sheet as CSV using the exact file names below.
> - You can give it one work package at a time. The Data Contract applies to all of them.

---

## Role and Goal

You are a data engineer preparing **load-ready CSV files** for the SecurePlus SQL database (EaaS v3 schema) used by the *GDS Secure Plus – Modern Work* application. A loader written by another engineer will read your files, resolve every **code** to a database ID and insert the rows. That loader is strict: a wrong header, an unknown code or an invented value will be rejected. **Precision matters more than volume. Never guess.**

Phase 1 is already in the database: the Secure Controls Framework (SCF) 2026.3 with its 270 focal documents and mappings, NIST SP 800-53 Rev 5 / 800-53A, and MITRE ATT&CK Enterprise **v20**. Phase 2 adds the CIS Benchmarks, Microsoft licensing, threat-informed mappings, GDS commercial and delivery content, scoring, known issues and deployment rings.

---

## PART A – DATA CONTRACT (applies to every file)

### A1. Output location and layout

Write everything under `Phase2/` in the `toby67maro/Documents` repository (or a SharePoint folder with the same structure):

```
Phase2/
  README.md                      # what you did, sources used, open questions
  manifest.csv                   # one row per output file (see A7)
  load_issues.csv                # every problem you could not resolve (see A8)
  scripts/                       # parser scripts (Python 3.11+), re-runnable
  raw/                           # untouched downloads (CTID JSON/CSV, Microsoft licensing CSV)
  00_reference/                  # platforms, products, capabilities, licences, source documents, mapping sources
  01_cis/                        # CIS Benchmark extraction
  02_ctid/                       # ATT&CK mappings
  03_licensing/                  # Microsoft SKUs and service plans
  04_service_tiers/              # GDS tier features and capabilities
  05_sow/                        # SOW templates, sections, clauses
  06_scoring/                    # scoring model, attributes, rules, precedence, rating scales
  07_known_issues/               # side effects of settings
  08_rings_and_terms/            # deployment rings, publisher licence terms
```

### A2. CSV format (exact)

- Encoding **UTF-8 without BOM**. Line endings **LF**. Delimiter **comma**. Quoting per **RFC 4180**: quote a field when it contains a comma, quote, CR or LF, and double any embedded quotes (`""`). Line breaks inside a quoted field are allowed and must be kept, for example in procedure steps.
- **The first row is the header.** Use the column names exactly as listed: same spelling, same case, same order. Do not add, drop or rename columns. Where a section allows optional extra columns, add them only at the end.
- **Empty field = NULL.** Never write `N/A`, `null`, `none`, `-` or `TBD` in a data column. Put open points in `load_issues.csv`.
- Booleans: `1` or `0`. Dates: `YYYY-MM-DD`. Timestamps: `YYYY-MM-DDTHH:MM:SSZ` (UTC). Decimals use `.` with no thousands separators. Percentages are numbers (`25`, not `25%`).
- Trim leading and trailing whitespace. Collapse runs of spaces inside one-line fields. Replace non-breaking spaces (U+00A0) with normal spaces. Keep other Unicode as it is (for example `’`, `–`, `≥`).
- Respect the **maximum lengths** given as `(n)`. If text is longer, store the full text in the matching `*_text.csv` file if one exists. Otherwise truncate at a word boundary, end with `…`, and log `TRUNCATED` in `load_issues.csv`.
- **Sort rows** by the key columns listed for each file, so that re-runs produce identical files and diffs stay small.
- File names are **lower_snake_case** and exactly as specified.

### A3. Keys: use codes, never database IDs

The database assigns integer IDs. **Your files reference everything by natural key (code).** For example, use `BaselineCode` plus `RecNumber`, not `RecommendationId`, and `FrameworkCode` plus `RequirementCode`, not `RequirementId`.

Code style: **UPPER-KEBAB-CASE** of A–Z, 0–9 and `-` (for example `CIS-WIN11-ENT-5.1.1`, `ENTRA-CA`). Dots are allowed only in version numbers. Codes are stable: never reuse a code for a different thing.

### A4. Codes that already exist in the database (use exactly these)

| Code list | Values |
|---|---|
| **FrameworkCode** (fw.Framework) | `scf` (SCF hub); `general-nist-800-53-r5-2` (NIST 800-53 Rev 5); `general-nist-csf-2-0`; `general-cis-csc-8-1` (CIS Controls v8.1) and its IG sets `general-cis-csc-8-1-ig1/-ig2/-ig3`; `general-iso-27002-2022`; `general-pci-dss-4-0-1`; `usa-federal-dow-cmmc-2-level-1/-2/-3`; `usa-federal-law-hipaa-security-rule-2013`; `usa-federal-dhs-cisa-cpg-2-0`. For other frameworks, write the framework name in `load_issues.csv` and leave the code empty. |
| **RequirementCode** format | NIST: `AC-2`, `AC-2(1)` (no zero padding). CIS Controls: Safeguard `4.1`, Control `4`. SCF: `IAC-06.1`. CSF 2.0: `PR.AA-01`. |
| **TechniqueCode** | ATT&CK IDs `T1078`, `T1078.004`. **TacticCode** `TA0001`. **MitigationCode** `M1032`. |
| **ServiceTierCode** | `Essentials`, `SecurePlus`, `SecurePlusPremium` |
| **EngagementTypeCode** | `SecurePlus`, `Assessment`, `Project` |
| **RelationshipTypeCode** | `Equal`, `SubsetOf`, `SupersetOf`, `IntersectsWith`, `RelatedTo`, `NotRelated` |
| **EntityTypeCode** | `Requirement`, `Recommendation`, `Setting`, `ConfigurationChange`, `StandardItem`, `Artifact`, `Expectation`, `PolicySet` |
| **CloudEnvironmentCode** | `Commercial`, `Gcc`, `GccHigh`, `DoD` |
| **ImplementationStatusCode** | `Implemented`, `PartiallyImplemented`, `Planned`, `NotImplemented`, `RiskAccepted`, `CompensatingControl`, `NotApplicable`, `NotAssessed` |
| **MaturityLevelCode** | `L0` … `L5` (SCF SP-CMM) |
| **MappingSourceCode** (existing) | `SCF-STRM`, `NIST-800-53A`, `MITRE-ATTACK`, `GDS-ALIAS` |
| **RatingScaleCode** (existing) | `SCF-LIKELIHOOD` (1 Unlikely, 2 Possible, 3 Likely) |

### A5. Provenance columns (the last four columns of EVERY data file)

Every file ends with these four columns, in this order:

| Column | Rule |
|---|---|
| `SourceDocumentCode` (80) | Code of the source in `00_reference/source_documents.csv`. Required. |
| `SourceRef` (200) | Where in the source: `p.123` (PDF page), `slide 7`, `Sheet!A12`, `row 345`, a JSON pointer, or a URL fragment. Required. |
| `Confidence` | `Authoritative` (copied from the publisher), `Extracted` (parsed by script from the publisher's document), `Derived` (computed by a stated rule), `GdsDraft` (proposed by you and needing GDS approval). |
| `ReviewStatus` | `Draft`, `NeedsGdsReview` or `Approved`. You may never write `Approved`; only a GDS person can. |

### A6. Rules against invention (very important)

1. **Do not fabricate** IDs, SKUs, GUIDs, URLs, ATT&CK IDs, CIS rec numbers, registry paths, prices or quotes. Every value must come from a source you can cite in `SourceRef`.
2. **Check every URL** you write. If it does not resolve, do not write it; log `URL_UNVERIFIED`.
3. If a GDS internal document cannot be found, **do not make one up**. Produce the file with headers only, plus any rows you can draft from general practice marked `Confidence=GdsDraft, ReviewStatus=NeedsGdsReview`, and log `SOURCE_NOT_FOUND`.
4. **Publisher text licences.** CIS Benchmark text is licensed by CIS: read the *Terms of Use* page inside the PDFs and record the exact terms in `src_licenses.csv`. Verbatim CIS text goes **only** into `01_cis/cis_recommendation_text.csv` with `IsInternalOnly=1`. Summary columns elsewhere must be **short paraphrases in your own words** (at most 300 characters), never copied paragraphs.
5. **No secrets.** Never write client secrets, certificates, passwords, tenant IDs, customer names or personal phone numbers. GDS staff names are allowed only where a column asks for an owner role or author.

### A7. `manifest.csv`

`FileName,TargetTable,RowCount,Sha256,GeneratedBy,GeneratedAt,ScriptPath,SourceDocumentCodes,Notes`

- `FileName` is relative to `Phase2/`. `TargetTable` is, for example, `cfg.Recommendation`.
- `RowCount` excludes the header. `Sha256` is the lowercase hex hash of the file bytes.
- `SourceDocumentCodes` is separated by `;`.

### A8. `load_issues.csv`

`FileName,SourceDocumentCode,SourceRef,SeverityCode,IssueCode,Message`

- `SeverityCode`: `Error` (row not written), `Warning` (row written, value doubtful) or `Info`.
- `IssueCode`, one of: `PARSE_FAILED`, `MISSING_VALUE`, `UNKNOWN_CODE`, `DUPLICATE_KEY`, `TRUNCATED`, `AMBIGUOUS_MATCH`, `URL_UNVERIFIED`, `SOURCE_NOT_FOUND`, `LICENSE_RESTRICTION`, `NEEDS_GDS_DECISION`.

### A9. Self-check before you finish (put the results in README.md)

- Every file parses with Python's `csv` module, the header matches this spec exactly, and the column count is the same on every row.
- No duplicate keys. Each file states its key columns, and they are unique.
- Every code you reference exists, either in the A4 lists or in a `00_reference` file.
- No value exceeds its maximum length, and every enumeration value is one of the values allowed.
- Row counts per file are in the manifest. For CIS, the recommendations counted per benchmark match the PDF's *Recommendation Summary* / *Appendix: Summary Table* (report any difference).
- Spot check: 10 random rows per file compared with the source. List them in README.md as `file | key | OK/Fixed`.

---

## PART B – WORK PACKAGES

### WP0 – Reference files (`00_reference/`), do these first

**`platforms.csv`** → `cfg.Platform`. Key: `PlatformCode`.
`PlatformCode(50),Name(200),PlatformFamilyCode(30),Vendor(100),VersionLabel(50),ParentPlatformCode(50),IsActive` + provenance.

- `PlatformFamilyCode`, one of: `Windows`, `WindowsServer`, `macOS`, `iOS`, `iPadOS`, `Android`, `Browser`, `OfficeApps`, `M365`, `Entra`, `Azure`, `AWS`, `GCP`, `GoogleWorkspace`, `Database`, `Hypervisor`, `ContainerOS`, `PowerPlatform`, `Antivirus`, `AIPlatform`.
- Use at least these codes: `WIN10`, `WIN11`, `WS2019`, `WS2022`, `WS2025`, `MACOS14`, `MACOS15`, `MACOS26`, `IOS17`, `IOS18`, `IOS26`, `IPADOS17`, `IPADOS18`, `IPADOS26`, `ANDROID`, `CHROME`, `EDGE`, `M365APPS`, `M365`, `ENTRA`, `AZURE`, `AWS`, `GCP`, `GCP-COS`, `GWS`, `SQL2016`, `SQL2017`, `SQL2019`, `SQL2022`, `SQL2025`, `ESXI7`, `ESXI8`, `ESXI9`, `POWERPLATFORM`, `DEFENDER-AV`, `MCP-SERVER`.
- `ParentPlatformCode` builds a tree: `WIN11` → `WINDOWS`, `SQL2022` → `SQLSERVER`, `ESXI8` → `ESXI`. Create the parent rows too.

**`products.csv`** → `kb.Product`. Key: `ProductCode`.
`ProductCode(50),Name(200),Vendor(100),ProductFamilyCode(40),AdminCenterName(200),AdminPortalUrl(500),Description(2000)` + provenance.

- Microsoft product codes to use: `ENTRA`, `INTUNE`, `MDE` (Defender for Endpoint), `MDO` (Defender for Office 365), `MDI` (Defender for Identity), `MDCA` (Defender for Cloud Apps), `DEFENDER-XDR`, `DEFENDER-CLOUD`, `SENTINEL`, `PURVIEW`, `EXO` (Exchange Online), `SPO` (SharePoint Online), `ODB` (OneDrive), `TEAMS`, `M365APPS`, `WINDOWS`, `WINDOWS-SERVER`, `AZURE`, `COPILOT`, `POWERPLATFORM`, `DYNAMICS365`, `AVD` (Azure Virtual Desktop), `W365` (Windows 365), `EDGE`. Add others as needed, plus non-Microsoft products that appear in the CIS list (AWS, GCP, Google Workspace, Chrome, VMware ESXi, SQL Server).
- `ProductFamilyCode`: `Identity`, `Endpoint`, `Security`, `Compliance`, `Collaboration`, `Productivity`, `Cloud`, `Database`, `Infrastructure`, `AI`, `Browser`.
- `AdminPortalUrl` must be the real admin centre URL (for example `https://entra.microsoft.com`).

**`capabilities.csv`** → `kb.Capability`. Key: `CapabilityCode`. This is the **master list of capabilities**: WP2, WP3 and WP4 all point to it.
`CapabilityCode(80),ProductCode(50),Name(300),CategoryText(200),Description(2000),DocUrl(1000),PortalPath(1000),Aliases` + provenance.

- Build it from:
  1. `Documents/M365_Services_Solutions_WIP_TB.xlsx` (the GDS services and solutions workbook; take every solution or feature row),
  2. the Microsoft service plans in WP3,
  3. the CTID capability lists in WP2.
- Code pattern `<PRODUCTCODE>-<SHORTNAME>`, for example `ENTRA-CA` (Conditional Access), `ENTRA-PIM`, `ENTRA-ID-PROTECTION`, `ENTRA-SSPR`, `ENTRA-GSA` (Global Secure Access), `INTUNE-COMPLIANCE`, `INTUNE-APP-PROTECTION`, `MDE-ASR`, `MDE-EDR`, `MDE-TVM`, `MDO-SAFE-LINKS`, `MDO-SAFE-ATTACHMENTS`, `PURVIEW-DLP`, `PURVIEW-SENSITIVITY-LABELS`, `PURVIEW-AUDIT-PREMIUM`, `PURVIEW-INSIDER-RISK`, `EXO-ANTI-PHISH`, `MDCA-SESSION-CONTROL`.
- **One capability = one thing an engineer can turn on.** Merge duplicates and put their other names in `Aliases` (separated by `;`).
- Target size: roughly 300–500 rows.

**`source_documents.csv`** → `src.SourceDocument`. Key: `SourceDocumentCode`.
`SourceDocumentCode(80),Title(400),Publisher(200),DocumentTypeCode(30),Version(50),PublishedDate,RetrievedAt,Location(1000),FileName(400),FileHashSha256(64),LicenseCode(50),IsAuthoritative,IsGdsInternal,IsCurrent,SupersededByCode(80),Notes(2000)`. No provenance columns in this file.

- `DocumentTypeCode`: `Benchmark`, `Baseline`, `Framework`, `Mapping`, `Catalog`, `Dataset`, `Deck`, `Template`, `Policy`, `Workbook`, `WebPage`.
- `Location` is the repository path (for example `Frameworks/CIS Benchmarks/CIS_Microsoft_Windows_11_Enterprise_Benchmark_v5.1.1.pdf`) or a URL.
- Compute `FileHashSha256` from the file bytes (64 lowercase hex characters).
- For a CIS benchmark, `SourceDocumentCode` is the same as its `BaselineCode` (see WP1). Every other document gets its own code, for example `MS-LICENSING-SERVICE-PLANS-2026-11`, `CTID-NIST-800-53-R5-ATTACK-V16`, `GDS-SERVICE-TIERS-DECK`.

**`mapping_sources.csv`** → `ref.MappingSource`. Key: `MappingSourceCode`.
`MappingSourceCode(50),Name(200),Publisher(200),AuthorityLevelCode(30),DefaultConfidence,SourceDocumentCode(80),Url(1000)`.

- `AuthorityLevelCode`: `Authoritative`, `Crosswalk`, `Vendor`, `Derived`, `GdsInternal`, `SmeEstimate`. `DefaultConfidence` is 0–100.
- Add at least: `CIS-BENCHMARK` (rec → CIS Controls mappings printed in the benchmarks), `CIS-BENCHMARK-ATTACK` (ATT&CK tables printed in the benchmarks), `CTID-NIST-800-53`, `CTID-M365`, `CTID-AZURE`, `MS-SERVICE-PLANS`, `GDS-SERVICE-TIERS`, `GDS-SCORING`, `GDS-KNOWN-ISSUES`, `MS-LEARN`.

**`src_licenses.csv`** (WP8) → `src.License`. Described under WP8.

---

### WP1 – CIS Benchmarks (`01_cis/`), the biggest package

**Source:** the PDFs in `Frameworks/CIS Benchmarks/` (56 unique benchmarks). `CIS_Microsoft_SQL_Server_2016_Benchmark_v1.4.0_Archive.pdf` and `... (1).pdf` are byte-identical: use the first and ignore the duplicate. `GDS Modern Workplace - Service Tiers.pptx` is in that folder by mistake; it belongs to WP4.

**Method:** write `scripts/parse_cis.py` (Python 3.11+, `pdfplumber` or `PyMuPDF`). It must be deterministic and runnable with `python scripts/parse_cis.py --input "Frameworks/CIS Benchmarks" --output Phase2/01_cis`. Do not hand-type the data. Typical CIS layout, which the parser must handle:

- **Heading:** `1.1.1 (L1) Ensure 'Enforce password history' is set to '24 or more password(s)' (Automated)`. Capture `RecNumber` = `1.1.1`, `LevelText` = `L1` (it can be `L1`, `L2`, `BL` for BitLocker, `NG` for Next Generation, or `L1, L2`), `Title` = the text after the level tag (keep the quotes), and `AssessmentTypeCode` = `Automated` or `Manual`.
- **Labelled blocks after the heading:** `Profile Applicability:` (bullet list of profiles), `Description:`, `Rationale:`, `Impact:`, `Audit:`, `Remediation:`, `Default Value:`, `References:`, `Additional Information:`, `CIS Controls:` (a table with columns *Controls Version | Control | IG1 | IG2 | IG3*), and in newer benchmarks `MITRE ATT&CK Mappings:` (a table with *Techniques / Sub-techniques | Tactics | Mitigations*).
- **Section headings** without a level tag (for example `1 Account Policies`, `1.1 Password Policy`). These make the section tree.
- **Ignore** the table of contents, page headers and footers (`Page 123`, `Internal Only - General`), and the *Terms of Use* page (read it for WP8, but do not output it as data).
- Use the appendix *Recommendation Summary Table* to check the parser's count; differences go to `load_issues.csv`.

**Baseline codes (use exactly these):**

| File | BaselineCode | PlatformCode | IsCurrent | IsArchived | SupersededBy |
|---|---|---|---|---|---|
| CIS AWS Compute Services Benchmark v2.0.0 | CIS-AWS-COMPUTE-2.0.0 | AWS | 1 | 0 | |
| CIS AWS Database Services Benchmark v2.0.0 | CIS-AWS-DATABASE-2.0.0 | AWS | 1 | 0 | |
| CIS_AWS_End_User_Compute_Services v1.2.0 | CIS-AWS-EUC-1.2.0 | AWS | 1 | 0 | |
| CIS_AWS_Storage_Services v1.0.0 | CIS-AWS-STORAGE-1.0.0 | AWS | 1 | 0 | |
| CIS_Amazon_Web_Services_Foundations v7.1.0 | CIS-AWS-FOUNDATIONS-7.1.0 | AWS | 1 | 0 | |
| CIS_Apple_MacOS_14.0_Sonoma_Intune v1.0.0 | CIS-MACOS14-INTUNE-1.0.0 | MACOS14 | 1 | 0 | |
| CIS_Apple_MacOS_15.0_Sequoia_Intune v1.0.0 | CIS-MACOS15-INTUNE-1.0.0 | MACOS15 | 0 | 0 | CIS-MACOS15-INTUNE-1.1.0 |
| CIS_Apple_MacOS_15.0_Sequoia_Intune v1.1.0 | CIS-MACOS15-INTUNE-1.1.0 | MACOS15 | 1 | 0 | |
| CIS_Apple_MacOS_26_Tahoe_Intune v1.0.0 | CIS-MACOS26-INTUNE-1.0.0 | MACOS26 | 1 | 0 | |
| CIS_Apple_iOS_18_and_iPadOS_18_Intune v1.0.0 | CIS-IOS18-INTUNE-1.0.0 | IOS18 | 0 | 0 | CIS-IOS18-INTUNE-2.0.0 |
| CIS_Apple_iOS_18_and_iPadOS_18_Intune v2.0.0 | CIS-IOS18-INTUNE-2.0.0 | IOS18 | 1 | 0 | |
| CIS_Apple_iOS_26_Benchmark v1.0.0 | CIS-IOS26-1.0.0 | IOS26 | 1 | 0 | |
| CIS_Apple_iOS_26_and_iPadOS_26_Intune v1.0.0 | CIS-IOS26-INTUNE-1.0.0 | IOS26 | 1 | 0 | |
| CIS_Apple_iPadOS_26_Benchmark v1.0.0 | CIS-IPADOS26-1.0.0 | IPADOS26 | 1 | 0 | |
| CIS_Google_Android_Benchmark v1.5.0 | CIS-ANDROID-1.5.0 | ANDROID | 0 | 0 | CIS-ANDROID-1.6.0 |
| CIS_Google_Android_Benchmark v1.6.0 | CIS-ANDROID-1.6.0 | ANDROID | 1 | 0 | |
| CIS_Google_Chrome_Benchmark v3.0.0 | CIS-CHROME-3.0.0 | CHROME | 1 | 0 | |
| CIS_Google_Chrome_Group_Policy v1.1.0 | CIS-CHROME-GPO-1.1.0 | CHROME | 1 | 0 | |
| CIS_Google_Cloud_Platform_Foundation v5.0.1 | CIS-GCP-FOUNDATIONS-5.0.1 | GCP | 1 | 0 | |
| CIS_Google_Container-Optimized_OS v1.2.0 | CIS-GCP-COS-1.2.0 | GCP-COS | 1 | 0 | |
| CIS_Google_Workspace_Foundations v1.3.0 | CIS-GWS-1.3.0 | GWS | 0 | 0 | CIS-GWS-1.4.0 |
| CIS_Google_Workspace_Foundations v1.4 | CIS-GWS-1.4.0 | GWS | 1 | 0 | |
| CIS_MCP_Server_Benchmark v1.0.0 | CIS-MCP-SERVER-1.0.0 | MCP-SERVER | 1 | 0 | |
| CIS_Microsoft_365_Foundations v7.0.0 | CIS-M365-FOUNDATIONS-7.0.0 | M365 | 1 | 0 | |
| CIS_Microsoft_Azure_Compute_Services v2.0.0 | CIS-AZURE-COMPUTE-2.0.0 | AZURE | 1 | 0 | |
| CIS_Microsoft_Azure_Database_Services v2.0.0 | CIS-AZURE-DATABASE-2.0.0 | AZURE | 1 | 0 | |
| CIS_Microsoft_Azure_Foundations v7.0.0 | CIS-AZURE-FOUNDATIONS-7.0.0 | AZURE | 1 | 0 | |
| CIS_Microsoft_Azure_Storage_Services v2.0.0 | CIS-AZURE-STORAGE-2.0.0 | AZURE | 1 | 0 | |
| CIS_Microsoft_Defender_Antivirus v1.0.0 | CIS-DEFENDER-AV-1.0.0 | DEFENDER-AV | 1 | 0 | |
| CIS_Microsoft_Dynamics_365_Power_Platform v1.0.1 | CIS-POWERPLATFORM-1.0.1 | POWERPLATFORM | 1 | 0 | |
| CIS_Microsoft_Edge_Benchmark v5.0.0 | CIS-EDGE-5.0.0 | EDGE | 1 | 0 | |
| CIS_Microsoft_Intune_for_Apple_iOS_17_and_iPadOS_17 v2.0.0 | CIS-IOS17-INTUNE-2.0.0 | IOS17 | 1 | 0 | |
| CIS_Microsoft_Intune_for_Edge v1.0.0 | CIS-EDGE-INTUNE-1.0.0 | EDGE | 1 | 0 | |
| CIS_Microsoft_Intune_for_Microsoft_Defender_Antivirus v1.0.0 | CIS-DEFENDER-AV-INTUNE-1.0.0 | DEFENDER-AV | 1 | 0 | |
| CIS_Microsoft_Intune_for_Office v1.1.0 | CIS-M365APPS-INTUNE-1.1.0 | M365APPS | 1 | 0 | |
| CIS_Microsoft_Intune_for_Windows_10 v5.0.0 | CIS-WIN10-INTUNE-5.0.0 | WIN10 | 1 | 0 | |
| CIS_Microsoft_Intune_for_Windows_11 v5.0.0 | CIS-WIN11-INTUNE-5.0.0 | WIN11 | 1 | 0 | |
| CIS_Microsoft_SQL_Server_2016 v1.4.0_Archive | CIS-SQL2016-1.4.0 | SQL2016 | 0 | 1 | |
| CIS_Microsoft_SQL_Server_2017 v1.3.0_Archive | CIS-SQL2017-1.3.0 | SQL2017 | 0 | 1 | |
| CIS_Microsoft_SQL_Server_2019 v1.6.0_FINAL_UPDATE | CIS-SQL2019-1.6.0 | SQL2019 | 1 | 0 | |
| CIS_Microsoft_SQL_Server_2022 v1.3.0 | CIS-SQL2022-1.3.0 | SQL2022 | 1 | 0 | |
| CIS_Microsoft_SQL_Server_2025 v1.0.0 | CIS-SQL2025-1.0.0 | SQL2025 | 1 | 0 | |
| CIS_Microsoft_Windows_11_Enterprise v5.1.1 | CIS-WIN11-ENT-5.1.1 | WIN11 | 1 | 0 | |
| CIS_Microsoft_Windows_11_Stand-alone v2.0.0-ARCHIVE | CIS-WIN11-SA-2.0.0 | WIN11 | 0 | 1 | CIS-WIN11-SA-5.0.0 |
| CIS_Microsoft_Windows_11_Stand-alone v5.0.0 | CIS-WIN11-SA-5.0.0 | WIN11 | 1 | 0 | |
| CIS_Microsoft_Windows_Server_2019_STIG v3.0.0 | CIS-WS2019-STIG-3.0.0 | WS2019 | 1 | 0 | |
| CIS_Microsoft_Windows_Server_2019_Stand-alone v4.0.0 | CIS-WS2019-SA-4.0.0 | WS2019 | 1 | 0 | |
| CIS_Microsoft_Windows_Server_2022 v5.1.0 | CIS-WS2022-5.1.0 | WS2022 | 1 | 0 | |
| CIS_Microsoft_Windows_Server_2022_STIG v1.0.0-ARCHIVE | CIS-WS2022-STIG-1.0.0 | WS2022 | 0 | 1 | CIS-WS2022-STIG-2.0.0 |
| CIS_Microsoft_Windows_Server_2022_STIG v2.0.0 | CIS-WS2022-STIG-2.0.0 | WS2022 | 1 | 0 | |
| CIS_Microsoft_Windows_Server_2022_Stand-alone v2.0.0 | CIS-WS2022-SA-2.0.0 | WS2022 | 1 | 0 | |
| CIS_Microsoft_Windows_Server_2025 v2.1.1 | CIS-WS2025-2.1.1 | WS2025 | 1 | 0 | |
| CIS_Microsoft_Windows_Server_2025_Stand-alone v2.0.0 | CIS-WS2025-SA-2.0.0 | WS2025 | 1 | 0 | |
| CIS VMWare ESXi 7 v1.6.0 | CIS-ESXI7-1.6.0 | ESXI7 | 1 | 0 | |
| CIS_VMware_ESXi_8.0 v1.4.0 | CIS-ESXI8-1.4.0 | ESXI8 | 1 | 0 | |
| CIS_VMware_ESXi_9.0 v1.0.0 | CIS-ESXI9-1.0.0 | ESXI9 | 1 | 0 | |

**Output files (header = exact column list, then the four provenance columns):**

1. **`cis_baselines.csv`** → `cfg.Baseline`. Key: `BaselineCode`.
   `BaselineCode(80),BaselineSourceCode(40),PlatformCode(50),Name(300),Version(30),ReleaseDate,DeliveryMechanismCode(30),TargetCloudCode(20),IsCurrent,IsArchived,SupersededByBaselineCode(80),RecommendationCount,Notes(1000)`
   - `BaselineSourceCode` = `CIS`.
   - `DeliveryMechanismCode`: `GPO` (Windows/Chrome/Edge GPO benchmarks), `Intune` (benchmarks with "Intune" in the name), `CloudConsole` (M365, Azure, AWS, GCP, GWS, Power Platform), `ConfigFile`, `SQL` or `CLI`.
   - `TargetCloudCode` = `Commercial` for cloud benchmarks, otherwise empty.
   - `ReleaseDate` comes from the PDF cover or version history. `RecommendationCount` is the number of rows in `cis_recommendations.csv` for that baseline.

2. **`cis_profiles.csv`** → `cfg.BaselineProfile`. Key: `BaselineCode,ProfileCode`.
   `BaselineCode(80),ProfileCode(60),Name(200),ProfileLevel,QualifierCode(40),IsAddOn,LicenseSkuPartNumber(100),Description(1000)`
   - `Name` is the profile exactly as printed (for example `Level 1 (L1) - Member Server`).
   - `ProfileCode` is short: `L1-MS`, `L1-DC`, `L2-MS`, `L2-DC`, `L1`, `L2`, `BL`, `NG`, `E3-L1`, `E5-L2`, `L1-CORP`, `L1-BYOD`, `STIG-MS`, `STIG-DC` …
   - `ProfileLevel` = 1, 2 or 3; leave it empty for BL/NG.
   - `QualifierCode`: `MemberServer`, `DomainController`, `Enterprise`, `StandAlone`, `Corporate`, `BYOD`, `Supervised`, `Unsupervised`, `E3`, `E5`, `BitLocker`, `NextGeneration`, `STIG`.
   - `IsAddOn` = 1 for BitLocker, Next Generation and STIG add-ons.
   - `LicenseSkuPartNumber` is set only for the M365 E3/E5 profiles (for example `SPE_E3`, `SPE_E5`) and must exist in WP3.

3. **`cis_sections.csv`** → `cfg.BaselineSection`. Key: `BaselineCode,SectionNumber`.
   `BaselineCode(80),SectionNumber(40),ParentSectionNumber(40),Title(400),Description(2000),SortKey(60)`
   - `SortKey` pads each number part to 3 digits (`001.002.010`) so sections sort correctly.
   - `Description` is the short intro paragraph under the section heading, if there is one (paraphrase it, at most 300 characters).

4. **`cis_recommendations.csv`** → `cfg.Recommendation`. Key: `BaselineCode,RecNumber`.
   `BaselineCode(80),RecNumber(60),SectionNumber(40),Title(500),AssessmentTypeCode(20),CategoryText(200),LevelText(60),Summary(2000),RationaleSummary(2000),ImpactSummary(2000),DefaultValue(1000),RecommendedValue(1000),ExternalId(200),MinimumOsVersion(100),HowToVerifyCode(30),IsDeprecated,PageNumber,SourceRowRef(100)`
   - `SectionNumber` is the closest parent section.
   - `CategoryText` is the top-level section title (for example `Account Policies`).
   - `Summary`, `RationaleSummary` and `ImpactSummary` are **paraphrases of at most 300 characters**.
   - `RecommendedValue` is the value the title says should be set (for example `24 or more password(s)`, `Enabled`, `0`).
   - `DefaultValue` is taken from the *Default Value:* block.
   - `ExternalId` is the Microsoft setting identifier if the rec prints one (Intune Settings Catalog name, M365 cmdlet), otherwise empty.
   - `MinimumOsVersion` is filled when the text says, for example, "Windows 11 Release 22H2 or later".
   - `HowToVerifyCode`: `GPO`, `Registry`, `Intune`, `PowerShell`, `Graph`, `Portal`, `CLI`, `SQL`, `ConfigFile` or `Manual`, taken from the first method in *Audit:*.
   - `IsDeprecated` = 1 if the rec is struck out or listed as removed.
   - `PageNumber` is the page where the heading is.

5. **`cis_recommendation_text.csv`** → `cfg.RecommendationSourceText` (verbatim text, **internal only**). Key: `BaselineCode,RecNumber`.
   `BaselineCode,RecNumber,DescriptionText,RationaleText,ImpactText,AuditText,RemediationText,AdditionalInfoText,WarningText,ReferencesText,LicenseCode,IsInternalOnly`
   - Keep the full text with line breaks.
   - `LicenseCode` = `CIS-BENCHMARK`; `IsInternalOnly` = `1`.
   - `WarningText` is any `Warning:` / `Note:` / `Caution:` paragraph.

6. **`cis_recommendation_profiles.csv`** → `cfg.RecommendationProfile`. Key: `BaselineCode,RecNumber,ProfileCode`.
   `BaselineCode,RecNumber,ProfileCode,RecommendedValue`
   - One row for each profile in *Profile Applicability*.
   - Fill `RecommendedValue` only if it differs by profile (for example a Member Server value differs from the Domain Controller value).

7. **`cis_procedures.csv`** → `cfg.RecommendationProcedure`. Key: `BaselineCode,RecNumber,ProcedureTypeCode,MethodCode,Sequence`.
   `BaselineCode,RecNumber,ProcedureTypeCode(20),MethodCode(20),Sequence,StepText,ScriptText,IsReadOnly,Placeholders(500)`
   - Split *Audit:* and *Remediation:* into one row per method.
   - `ProcedureTypeCode`: `Audit`, `Remediation` or `Rollback`.
   - `MethodCode`: `UI`, `GPO`, `Registry`, `Intune`, `PowerShell`, `Graph`, `AzureCLI`, `AWSCLI`, `gcloud`, `SQL`, `ESXCLI`, `Shell` or `Other`.
   - `Sequence` counts from 1 within each type and method.
   - `StepText` holds the prose steps; `ScriptText` holds only the code or command lines (keep indentation).
   - `IsReadOnly` = 1 for Audit rows and 0 for Remediation rows.
   - `Placeholders` lists tokens such as `<tenant>`, `<subscription-id>` and `<server>`, separated by `;`.

8. **`cis_settings.csv`** → `cfg.Setting`. Key: `SettingKey`. These are the canonical technical settings found in the Audit/Remediation text.
   `SettingKey(450),SettingTypeCode(30),PlatformCode(50),ProductCode(50),Name(400),FriendlyName(400),CategoryPath(1000),ScopeCode(20),DataType(30),AllowedValues,DefaultValue(1000),IntuneSettingDefinitionId(400),OmaUri(1000),GpoPath(1000),RegistryHive(10),RegistryKey(1000),RegistryValueName(300),RegistryValueType(20),GraphResource(400),GraphProperty(200),PowerShellCmdlet(400),PortalPath(1000),MinOsVersion(60),Editions(200),IsAdmxBacked,IsDeprecated`
   - **`SettingKey` rules.** These are canonical and compared without regard to case. Use **exactly one** prefix:
     - `reg:HKLM\SOFTWARE\Policies\Microsoft\Windows\System!EnableSmartScreen` (hive abbreviated `HKLM`/`HKCU`/`HKU`, `!` before the value name)
     - `gpo:Computer Configuration\Policies\Administrative Templates\Windows Components\...\<Setting Name>`
     - `intune:<settingDefinitionId>` (for example `intune:device_vendor_msft_policy_config_defender_allowarchivescanning`)
     - `omauri:./Device/Vendor/MSFT/Policy/Config/Defender/AllowArchiveScanning`
     - `graph:<resource>#<property>` (for example `graph:policies/authorizationPolicy#allowInvitesFrom`)
     - `ps:<Cmdlet>#<Parameter>` (for example `ps:Set-OrganizationConfig#AuditDisabled`)
     - `portal:<ProductCode>/<path>`, `azpolicy:<policyDefinitionId>`, `cli:<tool> <command>#<property>`, `sql:<setting>`, `esx:<advanced setting>`
   - `SettingTypeCode` is the prefix name: `Registry`, `GPO`, `Intune`, `OmaUri`, `Graph`, `PowerShell`, `Portal`, `AzurePolicy`, `CLI`, `SQL`, `ESXi`.
   - `ScopeCode`: `Device`, `User` or `Tenant`.
   - `RegistryValueType`: `REG_DWORD`, `REG_SZ`, `REG_MULTI_SZ`, `REG_QWORD` or `REG_EXPAND_SZ`.
   - One setting can be used by many recommendations and many benchmarks. **Deduplicate on `SettingKey`.**

9. **`cis_recommendation_settings.csv`** → `cfg.RecommendationSetting`. Key: `BaselineCode,RecNumber,SettingKey`.
   `BaselineCode,RecNumber,SettingKey,ExpectedValue(1000),OperatorCode(20),ValueUnit(20),IsPrimary`
   - `OperatorCode`: `Equals`, `NotEquals`, `AtLeast`, `AtMost`, `Between`, `In`, `Contains`, `Matches` (regex), `Exists` or `NotExists`.
   - Example: "24 or more" becomes `AtLeast`, `24`, `passwords`.
   - `IsPrimary` = 1 for the main setting of the recommendation.

10. **`cis_controls_mappings.csv`** → `cfg.RecommendationRequirement`. Key: `BaselineCode,RecNumber,FrameworkCode,RequirementCode`.
    `BaselineCode,RecNumber,FrameworkCode,RequirementCode,ControlsVersionLabel(10),IG1,IG2,IG3,RelationshipTypeCode,IsExplicitlyNotMapped`
    - Take these from the *CIS Controls:* table.
    - v8 rows: `FrameworkCode` = `general-cis-csc-8-1`, `ControlsVersionLabel` = `v8`.
    - v7 rows: `FrameworkCode` empty, `ControlsVersionLabel` = `v7`, `RequirementCode` as printed. The loader handles v7.
    - IG1/IG2/IG3 = 1 when the box is ticked.
    - `RelationshipTypeCode` = `RelatedTo` (CIS does not state the type).
    - `IsExplicitlyNotMapped` = 1 when the table says *"0.0 Explicitly Not Mapped"*.

11. **`cis_recommendation_techniques.csv`** → `threat.RecommendationTechnique`. Key: `BaselineCode,RecNumber,TechniqueCode`.
    `BaselineCode,RecNumber,TechniqueCode,TacticCodes,MitigationCode`
    - Taken from the *MITRE ATT&CK Mappings* table, only where the benchmark prints one.
    - `TacticCodes` is separated by `;`.
    - `MappingSourceCode` is implied to be `CIS-BENCHMARK-ATTACK`, so there is no column for it.

12. **`cis_stig_references.csv`** → `cfg.StigReference`. Only for the `*-STIG-*` benchmarks. Key: `BaselineCode,RecNumber,StigId`.
    `BaselineCode,RecNumber,StigId(40),VulId(20),RuleId(60),SeverityCat,StigTitle(300),StigVersion(20),StigRelease(20),BenchmarkDate`
    - `SeverityCat` = 1, 2 or 3 (CAT I/II/III).

**Acceptance for WP1:**
- Every benchmark appears in `cis_baselines.csv`.
- Per-benchmark recommendation counts match the PDF summary table, or the difference is logged.
- At least 95% of Windows/Edge/Chrome GPO recs have a `reg:` or `gpo:` setting.
- At least 90% of Intune recs have an `intune:` or `omauri:` setting.
- At least 80% of M365/Azure recs have a `ps:`, `graph:`, `cli:` or `portal:` setting.

---

### WP2 – Threat-informed defense mappings (`02_ctid/`)

**Sources (download into `raw/` and record each in `source_documents.csv` with its version and retrieval date):**

- MITRE Center for Threat-Informed Defense **Mappings Explorer**, `https://center-for-threat-informed-defense.github.io/mappings-explorer/`, with data in the GitHub repository `center-for-threat-informed-defense/mappings-explorer`. Download the CSV or JSON exports for:
  - **NIST SP 800-53 Rev 5 → ATT&CK** (the newest ATT&CK version available, ideally v16 or later; record the exact version),
  - **Microsoft 365** security stack (M365 capabilities → ATT&CK, with protect/detect/respond and significant/partial/minimal scores),
  - **Microsoft Azure** security stack.
- If CIS publishes a **CIS Controls v8 → ATT&CK** mapping (cisecurity.org), include it the same way under `CIS-CONTROLS-ATTACK`. If it needs a login or is not free, log `SOURCE_NOT_FOUND`.

**Files:**

1. **`ctid_requirement_techniques.csv`** → `threat.RequirementTechnique`. Key: `FrameworkCode,RequirementCode,TechniqueCode,MappingSourceCode`.
   `FrameworkCode,RequirementCode,TechniqueCode,MappingSourceCode,MappingCategoryCode(20),MappingValueCode(20),AttackVersion(10),Comments(1000)`
   - `FrameworkCode` = `general-nist-800-53-r5-2`. Write `RequirementCode` as `AC-2` / `AC-2(1)`.
   - `MappingCategoryCode` is the CTID mapping type in PascalCase (for example `Mitigates`).
   - `MappingValueCode` is empty for 800-53.
   - `AttackVersion` is the ATT&CK version of the mapping file (for example `16.1`).
   - **Keep techniques that do not exist in ATT&CK v20** (renamed or revoked), but log `UNKNOWN_CODE` for them.

2. **`ctid_capabilities.csv`**. This is the CTID's own list, matched to our capabilities. Key: `MappingSourceCode,CtidCapabilityId`.
   `MappingSourceCode,CtidCapabilityId(100),CtidCapabilityName(300),CtidCapabilityGroup(200),CapabilityCode(80),MatchConfidence`
   - `CapabilityCode` must exist in `00_reference/capabilities.csv`. Add missing capabilities there first.
   - `MatchConfidence`: `Exact`, `Close` or `None`.

3. **`ctid_capability_techniques.csv`** → `threat.CapabilityTechnique`. Key: `CapabilityCode,TechniqueCode,MappingSourceCode`.
   `CapabilityCode,TechniqueCode,MappingSourceCode,MappingCategoryCode(20),MappingValueCode(20),AttackVersion(10),Comments(1000)`
   - `MappingSourceCode` is `CTID-M365` or `CTID-AZURE`.
   - `MappingCategoryCode`: `Protect`, `Detect` or `Respond`. `MappingValueCode`: `Significant`, `Partial` or `Minimal`.
   - If one CTID capability maps to two of our capabilities, write a row for each.

---

### WP3 – Microsoft licensing (`03_licensing/`)

**Source:** Microsoft Learn, *Product names and service plan identifiers for licensing* (`https://learn.microsoft.com/entra/identity/users/licensing-service-plan-reference`). Download the linked **CSV** unchanged into `raw/` and record its date. Its columns are `Product_Display_Name, String_Id, GUID, Service_Plan_Name, Service_Plan_Id, Service_Plans_Included_Friendly_Names`. Check those column names against the file you downloaded.

**Files:**

1. **`license_skus.csv`** → `kb.LicenseSku`. Key: `SkuPartNumber`.
   `SkuPartNumber(100),SkuId,Name(200),Vendor(100),LicenseFamilyCode(40),CloudEnvironmentCode(20),IsAddOn,IsModernWorkRelevant,Notes(1000)`
   - `SkuPartNumber` = `String_Id`; `SkuId` = `GUID`; `Name` = `Product_Display_Name`; `Vendor` = `Microsoft`.
   - `LicenseFamilyCode`: `M365Business`, `M365Enterprise`, `M365Frontline`, `M365Education`, `M365Government`, `Office365`, `EMS`, `Entra`, `Intune`, `Defender`, `Purview`, `Teams`, `Windows`, `Copilot`, `PowerPlatform`, `Dynamics`, `Azure`, `Other`.
   - `CloudEnvironmentCode`: `Gcc` / `GccHigh` / `DoD` when the name says so, otherwise `Commercial`.
   - `IsAddOn` = 1 when the SKU needs a base licence (for example `Microsoft 365 E5 Security`, `Entra ID P2`, `Purview` add-ons).
   - `IsModernWorkRelevant` = 1 for the M365, O365, EMS, Entra, Intune, Defender, Purview, Teams, Windows Enterprise and Copilot SKUs that GDS sells or meets. These **must** include `SPB` (Business Premium), `O365_BUSINESS_PREMIUM` (Business Standard), `SPE_E3`, `SPE_E5`, `ENTERPRISEPACK`, `ENTERPRISEPREMIUM`, `IDENTITY_THREAT_PROTECTION` (E5 Security), `EMS`, `EMSPREMIUM`, `AAD_PREMIUM`, `AAD_PREMIUM_P2`, `INTUNE_A`, and the GCC / GCC High variants. Verify each of these part numbers against the CSV; do not assume them.

2. **`service_plans.csv`**. Key: `ServicePlanName`.
   `ServicePlanName(100),ServicePlanId,FriendlyName(300),CapabilityCode(80),ProductCode(50),MatchConfidence`
   - Map each service plan (for example `AAD_PREMIUM_P2`, `MTP`, `ATP_ENTERPRISE`, `INTUNE_A`, `MIP_S_CLP2`, `INFORMATION_PROTECTION_COMPLIANCE`, `M365_AUDIT_PLATFORM`) to one capability. When a plan unlocks several capabilities, add rows to `service_plan_capabilities.csv` (below) instead.
   - `MatchConfidence`: `Exact`, `Close` or `None`.

3. **`service_plan_capabilities.csv`**. Key: `ServicePlanName,CapabilityCode`.
   `ServicePlanName,CapabilityCode,Notes(500)`
   - Use this when one plan unlocks several capabilities. For example, `AAD_PREMIUM_P2` unlocks `ENTRA-PIM`, `ENTRA-ID-PROTECTION` and `ENTRA-ACCESS-REVIEWS`.
   - Base the mapping on the Microsoft Learn *service description* pages (cite each URL in `SourceRef`).

4. **`sku_capabilities.csv`** → `kb.SkuCapability`. Key: `SkuPartNumber,CapabilityCode`.
   `SkuPartNumber,CapabilityCode,ServicePlanName(100)`
   - Built only for the SKUs with `IsModernWorkRelevant=1`: SKU → service plans (raw CSV) → capabilities.
   - This answers the question "Which capabilities does each licence include?"

---

### WP4 – GDS service tiers (`04_service_tiers/`)

**Source:** `Frameworks/CIS Benchmarks/GDS Modern Workplace - Service Tiers.pptx` (it is misfiled in the CIS folder), plus any newer version in SharePoint. Extract the text **slide by slide**. Do not summarise away any feature.

**Files:**

1. **`service_tiers.csv`** (updates `svc.ServiceTier`). Key: `ServiceTierCode`.
   `ServiceTierCode(30),Name(100),Tagline(300),BaseLicenseSkuPartNumber(100),SortOrder,Description(2000)`
   - Exactly three rows: `Essentials`, `SecurePlus`, `SecurePlusPremium`.
   - Use the deck's names, taglines and the base licence each tier assumes (for example `SPB`). If the deck uses different tier names, keep these codes, put the deck name in `Name`, and log it.

2. **`tier_features.csv`** → `svc.TierFeature`. Key: `ServiceTierCode,SortOrder`.
   `ServiceTierCode,SortOrder,FeatureText(300),CapabilityCode(80)`
   - One row per bullet, **verbatim** from the deck (the deck is GDS-owned, so copying is fine), in slide order.
   - `CapabilityCode` is the best match in `capabilities.csv`, or empty if the bullet is a service rather than a product capability (for example "Quarterly business review").

3. **`tier_capabilities.csv`** → `svc.TierCapability`. Key: `ServiceTierCode,CapabilityCode`.
   `ServiceTierCode,CapabilityCode,InclusionCode(20),Notes(500)`
   - `InclusionCode`: `Included`, `Optional`, `AddOn` or `NotIncluded`.
   - Rows the deck states → `Confidence=Authoritative`.
   - Rows inferred from the tier's base licence and `sku_capabilities.csv` (licensed but not marketed) → `Confidence=Derived` and `InclusionCode=Optional`.
   - Every capability that has `IsModernWorkRelevant` SKU coverage gets a row for each of the 3 tiers.

---

### WP5 – SOW templates and clause library (`05_sow/`), Microsoft 365 Copilot

**Sources:** search SharePoint, Teams and OneDrive for GDS statements of work, SOW templates, proposal templates, MSAs, "assumptions", "exclusions", "customer responsibilities" and "acceptance criteria" for Secure Plus / Modern Work / M365 security engagements. Also use `EaaS/GDS_EaaS_Rate_Card_DRAFT.xlsx` and `OLD/GDS - M365 Secure Plus - Engineering Execution Plan.docx`. **Remove customer names and prices** from any text you reuse.

**Files:**

1. **`sow_templates.csv`** → `dlv.SowTemplate`. Key: `SowTemplateCode`.
   `SowTemplateCode(40),Name(200),Version(20),EngagementTypeCode(30),ServiceTierCode(30),DocumentTemplateUrl(1000)`
   - Expect about 3–6 templates (for example `SOW-SECUREPLUS-ONBOARD`, `SOW-ASSESSMENT`, `SOW-PROJECT-CA`).
   - `DocumentTemplateUrl` is the SharePoint URL of the .docx template.

2. **`sow_sections.csv`** → `dlv.SowSection`. Key: `SowTemplateCode,SectionCode`.
   `SowTemplateCode,SectionCode(40),Title(200),SortOrder,ContentTemplate,DataSourceView,IsRequired`
   - `ContentTemplate` is the section text with variable parts replaced by tokens from this **exact token vocabulary**:
     `{{Customer.Name}}`, `{{Customer.Address}}`, `{{Customer.City}}`, `{{Customer.State}}`, `{{Customer.Country}}`, `{{Customer.OrganizationSize}}`, `{{Customer.Industry}}`, `{{Engagement.Type}}`, `{{Engagement.ServiceTier}}`, `{{Engagement.SalesLead}}`, `{{Engagement.TechnicalLead}}`, `{{Engagement.ProjectManager}}`, `{{Engagement.StartDate}}`, `{{Engagement.EndDate}}`, `{{ChangeApprovers}}`, `{{Tenants.List}}`, `{{Licenses.Summary}}`, `{{Scope.Workstreams}}`, `{{Scope.TaskTable}}`, `{{Scope.Deliverables}}`, `{{Estimate.HoursLow}}`, `{{Estimate.HoursHigh}}`, `{{Estimate.Price}}`, `{{Estimate.ContingencyPct}}`, `{{Plan.Milestones}}`, `{{Clauses.Assumption}}`, `{{Clauses.Exclusion}}`, `{{Clauses.CustomerResponsibility}}`, `{{Clauses.AcceptanceCriterion}}`, `{{Clauses.Risk}}`, `{{Clauses.Dependency}}`, `{{Clauses.Term}}`, `{{Today}}`, `{{Gds.SignatoryName}}`, `{{Gds.SignatoryTitle}}`.
     If you need a token that is not listed, use it, list it in README.md and log `NEEDS_GDS_DECISION`.
   - Leave `DataSourceView` empty (the developer fills it).
   - `SectionCode` examples: `INTRO`, `BACKGROUND`, `SCOPE`, `OUT-OF-SCOPE`, `APPROACH`, `DELIVERABLES`, `SCHEDULE`, `ROLES`, `ASSUMPTIONS`, `CUST-RESP`, `ACCEPTANCE`, `CHANGE-CONTROL`, `PRICING`, `TERMS`, `SIGNATURES`.

3. **`clauses.csv`** → `dlv.Clause`. Key: `ClauseCode`.
   `ClauseCode(40),ClauseTypeCode(30),Text,AppliesWhen(300)`
   - `ClauseTypeCode`: `Assumption`, `Exclusion`, `CustomerResponsibility`, `AcceptanceCriterion`, `Risk`, `Dependency` or `Term`.
   - Code prefixes: `ASM-001`, `EXC-001`, `CR-001`, `ACC-001`, `RSK-001`, `DEP-001`, `TRM-001`.
   - **Each clause is one self-contained sentence or short paragraph** and may use tokens.
   - Target about 150–400 clauses. Merge near-duplicates and keep the clearest wording.
   - **`AppliesWhen` grammar** (leave empty when the clause always applies):
     - `ServiceTier IN (SecurePlus, SecurePlusPremium)`
     - `EngagementType = Assessment`
     - `HasLicense(SPE_E5)`
     - `HasCapability(ENTRA-PIM)`
     - `Fact.Users > 300` (facts: `Users`, `Devices`, `Sites`, `Tenants`, `MacDevices`, `MobileDevices`, `Servers`)
     - `Framework = usa-federal-dow-cmmc-2-level-2`
     - `Workstream = <code>`
     - Conditions can be combined with `AND`, `OR` and parentheses.

4. **`clause_links.csv`** → `dlv.ClauseLink`. Key: `ClauseCode,LinkTypeCode,LinkKey`.
   `ClauseCode,LinkTypeCode,LinkKey`
   - `LinkTypeCode`: `ServiceTier` (key = tier code), `Framework` (key = FrameworkCode), `Phase` (key = phase code from `SecurePlusStandards.xlsx`, for example `P0`–`P4`; list the codes you used in README.md), `StandardItem` (key = item code from `SecurePlusStandards.xlsx`), or `WbsTask` (key = WBS task code, only if a WBS document gives codes).
   - One link per row.

---

### WP6 – GDS scoring model (`06_scoring/`), Microsoft 365 Copilot, then GDS review

**Sources:** GDS documents on risk scoring, prioritisation, heat maps or estimation (search SharePoint and Teams); `EaaS/control-attribute-data-sources-report.md`; `EaaS/GDS_ComplianceIQ_Data_Dictionary_v3.csv` (the `score.*` table descriptions); NIST SP 800-30 Rev 1 (likelihood × impact); SCF control weighting (1–10); CMMC SPRS point values (1, 3, 5); Microsoft Secure Score points. If GDS has not decided something, **draft a proposal** marked `Confidence=GdsDraft, ReviewStatus=NeedsGdsReview` and explain it in `06_scoring/PROPOSAL.md` (one page: formula, why, worked example with 3 controls).

**Files:**

1. **`rating_scales.csv`** → `ref.RatingScale`. Key: `RatingScaleCode`.
   `RatingScaleCode(50),Name(200),MinValue,MaxValue,HigherIsWorse,Description(1000)`
   Propose: `GDS-LIKELIHOOD-5`, `GDS-IMPACT-5`, `GDS-EUX-IMPACT-5`, `GDS-DIFFICULTY-5`, `GDS-SEVERITY-4` (used by known issues: Low/Medium/High/Critical), `GDS-COST-BAND-5`, `GDS-CONFIDENCE-4`. Do not redefine `SCF-LIKELIHOOD`.

2. **`rating_scale_values.csv`** → `ref.RatingScaleValue`. Key: `RatingScaleCode,NumericValue`.
   `RatingScaleCode,NumericValue,Label(50),BinLow,BinHigh,ColorHex,Description(500)`
   - `BinLow`/`BinHigh` are the 0–100 normalised range; the bins must cover 0–100 with no gaps.
   - `ColorHex` is `#RRGGBB`: green (best) through amber to red (worst). Use the same palette for every scale.
   - `Description` says what the level means in plain words (for example EUX 4 = "Users see a prompt or must change how they work").

3. **`attributes.csv`** → `score.Attribute`. Key: `AttributeCode`.
   `AttributeCode(40),Name(200),RatingScaleCode(50),DataTypeCode(20),UnitCode(20),IsCustomerSpecific,Description(1000)`
   - About 30 attributes, for example: `RISK_WEIGHT` (SCF weight 1–10), `LIKELIHOOD`, `BUSINESS_IMPACT`, `EUX_IMPACT`, `DIFFICULTY`, `EFFORT_HOURS_MIN`, `EFFORT_HOURS_MAX`, `CALENDAR_DAYS`, `COST_BAND`, `SEVERITY`, `PRIORITY`, `SPRS_POINTS`, `SECURE_SCORE_POINTS`, `CIS_LEVEL`, `CIS_IG`, `REQUIRES_REBOOT`, `REQUIRES_USER_COMMS`, `LICENSE_TIER`, `ATTACK_COVERAGE` (number of techniques countered), `THREAT_LIKELIHOOD` (from SCF), `DETECTION_VALUE` …
   - `DataTypeCode`: `Rating`, `Integer`, `Decimal`, `Boolean`, `Money`, `Text`.
   - `UnitCode`: `Points`, `Hours`, `Days`, `USD`, `Percent`, `Count` or empty.
   - `IsCustomerSpecific` = 1 when the value depends on the customer (for example `BUSINESS_IMPACT`).

4. **`scoring_models.csv`** → `score.ScoringModel`. Key: `ScoringModelCode`.
   `ScoringModelCode(40),Name(200),Version(20),FormulaText,IsDefault,Description(2000)`
   - Propose 1–3 models, for example `GDS-RISK-DEFAULT` (likelihood × impact × SCF weight, normalised 0–100), `CMMC-SPRS` and `QUICK-WINS` (risk ÷ effort).
   - `FormulaText` is plain math using `AttributeCode` names, for example `RiskScore = 100 * (w1*norm(LIKELIHOOD) + ...)`.
   - Exactly one model has `IsDefault=1`.

5. **`scoring_weights.csv`** → `score.ScoringWeight`. Key: `ScoringModelCode,AttributeCode`.
   `ScoringModelCode,AttributeCode,Weight,TransformCode(20)`
   - `Weight` is `decimal(6,3)`, and the weights of each model **sum to 1.000**.
   - `TransformCode`: `Linear`, `Inverse` (lower is better, for example effort), `Log`, `Step` or `None`.

6. **`derivation_rules.csv`** → `score.DerivationRule`. Key: `RuleCode`.
   `RuleCode(40),AttributeCode(40),Priority,AppliesToEntityTypeCode,RuleText(2000),RuleCondition(1000),RuleResult(200),IsActive`
   - These rules fill gaps when no source gives a value. Example: `CIS L2 → EUX_IMPACT = 3`; `CIS L1 → EUX_IMPACT = 2`; `CIS BitLocker → REQUIRES_REBOOT = 1`; `CISA CPG ease "Complex" → DIFFICULTY = 4`; `WBS hours → EFFORT_HOURS_*`; `SCF weight → RISK_WEIGHT`; `NIST High baseline only → BUSINESS_IMPACT ≥ 4`.
   - `Priority` = 1 is tried first.
   - `AppliesToEntityTypeCode` is from A4.
   - `RuleCondition` uses the WP5 `AppliesWhen` style over item facts (for example `Source = CIS AND LevelText = L2`).
   - `RuleResult` is a value or expression (for example `3`).
   - About 40–60 rules. Every rule cites its reasoning in `SourceRef` or PROPOSAL.md.

7. **`source_precedence.csv`** → `score.SourcePrecedence`. Key: `AttributeCode,MappingSourceCode`.
   `AttributeCode,MappingSourceCode,Precedence`
   - When several sources give a value, the lowest `Precedence` number wins (1 = wins).
   - Typical order: `GDS-SCORING` (GDS SME decision) → authoritative publisher (`NIST-800-53A`, `CIS-BENCHMARK`, `MITRE-ATTACK`) → `SCF-STRM` → vendor (`MS-LEARN`) → derived rules.
   - Give every attribute at least two rows.

---

### WP7 – Known issues (`07_known_issues/`)

**Sources:**
- CIS *Impact:* blocks (WP1). These count as known side effects: summarise them, do not copy them.
- Microsoft Learn: security baseline "known issues" notes, Windows release health known issues, the M365 Apps security baseline documentation, Conditional Access troubleshooting / "common issues", Intune "known issues", Defender ASR rule "known issues" and exclusions, and Exchange Online / Teams / SharePoint feature notes.
- GDS sources: `GDS_SOPAC-CA-Policies_2026-10-27.xlsx`, `Conditional Access Standard/CA_Policy_Analysis.xlsx`, `GDS Baseline Settings Map - WIP - TB.xlsx` and `Customer_Deployment_Checklist_v2.0_Marquie.xlsx` (look for notes columns that mention breakage, exclusions, prerequisites or lessons learned).
- GDS Teams and SharePoint lessons learned (Microsoft 365 Copilot).

**Files:**

1. **`known_issues.csv`** → `kb.KnownIssue`. Key: `KnownIssueCode`.
   `KnownIssueCode(20),Title(300),Description,Symptoms(2000),Workaround(2000),SeverityCode(20),PlatformCode(50),ProductCode(50),SourceUrl(1000)`
   - `KnownIssueCode` = `KI-0001` …
   - `SeverityCode`: `Low`, `Medium`, `High` or `Critical` (scale `GDS-SEVERITY-4`). **Critical** = an outage or lockout (for example a CA policy that blocks all admins). **High** = a business app or feature breaks for many users. **Medium** = a workaround is needed. **Low** = cosmetic or a prompt.
   - `Symptoms` is what the user or help desk sees. `Workaround` is the exact fix or exclusion.
   - `SourceUrl` must be a real, checked URL, or empty for GDS internal sources (then `SourceDocumentCode` names the GDS file).
   - Examples of what a good row looks like:
     - "Blocking legacy authentication breaks old multifunction printers that scan to e-mail over basic SMTP AUTH."
     - "The ASR rule 'Block Win32 API calls from Office macros' breaks some Excel add-ins."
     - "Requiring compliant devices blocks guests and unmanaged BYOD browsers unless app-enforced restrictions are used."
     - "Disabling NTLM breaks legacy file shares and some scanners."
     - "Enabling LSA protection blocks unsigned third-party credential providers."
   - Target about 300–800 issues.

2. **`known_issue_links.csv`** → `kb.KnownIssueLink`. Key: `KnownIssueCode` plus the target columns.
   `KnownIssueCode,BaselineCode,RecNumber,SettingKey,ConfigurationChangeCode,FrameworkCode,RequirementCode,Notes(1000)`
   - Fill **exactly one** target group per row:
     - (`BaselineCode` + `RecNumber`) for a CIS rec,
     - `SettingKey` (from `cis_settings.csv`),
     - `ConfigurationChangeCode` (leave empty for now unless GDS gives you codes), or
     - (`FrameworkCode` + `RequirementCode`).
   - One issue can have many link rows. Link every CIS-derived issue to its rec **and** to its primary setting.

---

### WP8 – Publisher licence terms and deployment rings (`08_rings_and_terms/`)

1. **`src_licenses.csv`** → `src.License`. Key: `LicenseCode`. (A copy also goes in `00_reference/`.)
   `LicenseCode(50),Name(200),Url(1000),AllowsCommercialUse,AllowsRedistribution,AllowsDerivatives,AllowsCustomerFacingText,AttributionText(1000),Notes(2000)`
   - Rows: `CIS-BENCHMARK` (read the Terms of Use page inside the PDFs and cisecurity.org terms), `CIS-CONTROLS`, `SCF` (Secure Controls Framework licence), `NIST-PUBLIC-DOMAIN`, `MITRE-ATTACK` (ATT&CK terms of use), `CTID` (licence of the mappings-explorer repository), `MICROSOFT-LEARN` (Microsoft Learn content terms), `MICROSOFT-SCT` (Security Compliance Toolkit download terms), `MICROSOFT-LICENSING-CSV`, `CISA-SCUBA`, `DISA-STIG`, `GDS-INTERNAL`.
   - **Quote the exact clause** that supports each 1/0 in `Notes` and give the URL. If the terms are unclear, write `0` and log `LICENSE_RESTRICTION`.
   - `AllowsCustomerFacingText` = 1 only if the terms allow putting the publisher's wording in deliverables for paying customers.

2. **`deployment_rings.csv`** → `deploy.Ring`. Key: `RingCode`.
   `RingCode(30),Name(100),SortOrder,DefaultStateCode(40),Audience(500),EntryCriteria(1000),ExitCriteria(1000),MinimumSoakDays,RollbackTrigger(1000)`
   - The last five columns are allowed extras.
   - Use the GDS ring policy if one exists in SharePoint or `SecurePlusStandards.xlsx` (look for "pilot", "ring", "wave" and "report-only"). Otherwise propose one based on Microsoft guidance (Windows Autopatch rings and Conditional Access report-only rollout), marked `GdsDraft`.
   - Expected rows, at minimum: `Ring0-IT` (GDS/customer IT pilot), `Ring1-Pilot` (representative users), `Ring2-Broad` (everyone except exclusions), `Ring3-Exceptions` (executives, service accounts and break-glass reviewed last).
   - `DefaultStateCode`: `ReportOnly`, `Audit`, `Enabled` or `Disabled`. It is the state a policy starts in when deployed to that ring (for example CA policies start `ReportOnly` in Ring0).
   - `ExitCriteria` must be measurable (for example "7 days in report-only with zero unexpected blocks in sign-in logs; help desk tickets ≤ 2").

3. **`RING_POLICY.md`**: a one-page plain-language ring policy that matches the CSV: who approves moving to the next ring, communication, rollback, and break-glass exclusions.

---

## PART C – DELIVERY

1. Work in this order: WP0, then WP3, then WP2, then WP1, then WP4, then WP7, then WP5, WP6 and WP8.
2. When you finish each work package, update `manifest.csv`, `load_issues.csv` and `README.md` (sources with retrieval dates, row counts, the spot-check table, open questions for GDS).
3. In GitHub, open **one pull request per work package** into `main` of `toby67maro/Documents`, titled `Phase 2 data: WP<n> <name>`. Do not modify any existing file outside `Phase2/`.
4. List every `NEEDS_GDS_DECISION` and `SOURCE_NOT_FOUND` item at the top of README.md as a checklist for the GDS team.
