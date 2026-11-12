# Secure Plus – Catalogue Import Plan

Companion to [COVERAGE.md](COVERAGE.md).

> **Status 9 Nov 2026 (4.0.0-beta.2):** phase 0 (reload safety, control fields, policy and policy-set editor) is done. Coverage of every area
> was brought forward into beta.2 as 195 tenant settings (`TS-` controls) with the read-only PowerShell collector; see COVERAGE.md section 0.
> TCM is not scheduled. Azure stays manual until Lighthouse in 5.0. The text below is the original proposal.

**Proposal for review, 8 Nov 2026: nothing is imported and no catalogue content changes until
approved.** The only change made with this report is removing the free-text `Licensing` column from `secureplus.CatalogControl` (your request).

## 1. Decisions

| # | Decision | Recommendation | State |
|---|---|---|---|
| D1 | Per-tier values on the control | **No.** The control holds its default recommended value; 4.2 tier templates hold applicability and overrides per tier | Agreed |
| D2 | Source of truth | The **catalogue source** (Documents repo → `catalog.json.gz`), never direct SQL inserts. Lead-added and Lead-edited controls live only in SQL and are protected by D4 | Agreed |
| D3 | OpenIntuneBaseline (GPL-3.0) | OIB controls **stay visible** (marked *Source = OIB*); GDS confirms it is compliant with the licence. New GDS settings are written from Microsoft documentation | Decided 2026-11-08 |
| D4 | Reload safety | A reload replaces only GDS-origin, unmodified controls; Lead controls and Lead edits are never deleted or overwritten (§2) | **Proposed** – required before Leads edit policies |
| D5 | Licensing on the control | **Removed** (V0010). Licensing comes from the knowledge base: `pol.Artifact.MinLicenseSkuId` → `kb.LicenseSku`, and requirement → capability → SKU | Done |
| D6 | IE-mode settings (358) | Keep; tag *Legacy – IE mode*; hidden from default views; not scored | Proposed |
| D7 | Duplicates (AV, firewall, ASR) | One control per setting, Endpoint Security first; other sources become mappings (aliases) | Proposed |
| D8 | Read-only PowerShell assessment | Move from 4.5 to **4.1** (§6) | Agreed |
| D10 | Tenant Configuration Management (TCM) | **Not scheduled.** Evaluation kept in §7 for reference only | Decided 2026-11-08 |
| D9 | Azure | New module in **5.0**: ARM read connection via Azure Lighthouse (§8) | Agreed |

## 2. Source of truth and reload safety (D2, D4)

**What happens today (verified in `SqlCatalogStore`):** a catalogue load deletes every row of `secureplus.CatalogControl` and rewrites it,
supersedes the previous `pol.RepositoryVersion`, and deletes the previous version's `pol.Artifact`, `pol.ArtifactSetting`,
`pol.ArtifactMapping` and `pol.PolicySet*` rows unless other data references them. Nothing is lost today because Leads cannot add or
edit controls yet. **It would delete Lead work as soon as policy or policy-set editing exists**, so D4 must ship with that work.

**Proposal (migration V0011, with the policy and policy-set editor):**

| Item | How it is identified | On reload |
|---|---|---|
| GDS control, unchanged | `Origin = 'GDS'`, `IsModified = 0`, in repository `GDS-CATALOG` | Replaced by the new version |
| GDS control edited by a Lead | `Origin = 'GDS'`, `IsModified = 1`, `ModifiedBy/At` | Kept as edited; the new GDS version is stored beside it and shown as *Update available* (4.2) with a diff |
| Lead-added control | `Origin = 'Lead'`, repository `GDS-LEAD` (its own `pol.Repository`), ID prefix `L-` | Never touched: a GDS reload only supersedes versions of `GDS-CATALOG` |
| GDS control removed from the source | Missing from the new version | Marked `Retired` (kept for history and existing results), not deleted |
| Lead-built policy set | `pol.PolicySet` in `GDS-LEAD` | Never touched; members that point at a retired GDS control are flagged |

`secureplus.CatalogControl` becomes a view (or is rebuilt from both repositories) so that a reload never truncates Lead rows.
A test will load the catalogue twice around a Lead edit and a Lead-added control and assert both survive.

## 3. Source licences (every source)

| Source | Licence | Use in Secure Plus |
|---|---|---|
| GDS Conditional Access, Endpoint Security, checks, Standards | GDS-owned | Controls |
| OpenIntuneBaseline (1,211 controls today) | GPL-3.0 | Controls stay visible, marked `Source = OIB`; GDS confirms licence compliance (D3) |
| Microsoft security baselines (Windows 11, Server 2025, M365 Apps) | Microsoft Security Compliance Toolkit terms | Setting facts (name, path, recommended value) as controls with source and version |
| Microsoft Intune security frameworks (Android, iOS app protection) | Microsoft (GitHub `microsoft/Intune-Config-Frameworks`) – confirm the repository licence before re-import | Controls |
| Microsoft Learn documentation | Docs content CC BY 4.0; code samples MIT | Setting facts, default values, Graph properties, with attribution in `SourceUrl` |
| CIS Benchmarks (M365, Azure, Power Platform, iOS, macOS, Windows…) | CIS terms of use; **GDS holds CIS membership** (confirmed 2026-11-08) | Knowledge only: ID, title, level, assessment type; no CIS text in customer deliverables (decided 2026-11-08) |
| CISA SCuBA baselines (ScubaGear) | US government work (CISA) – confirm per repository | Reference and IDs for mapping |
| Maester tests | MIT | Reference for check logic; credit in source |
| Microsoft Cloud Security Benchmark / Azure Policy built-ins (5.0) | MIT (`Azure/azure-policy`) | Control facts and policy definition IDs |
| Microsoft365DSC resource schema | MIT | Reference for property names |
| NIST (800-53, CSF 2.0) | Public domain | Framework mappings |
| MITRE ATT&CK | ATT&CK terms of use (royalty-free with attribution) | Threat mappings |
| Secure Controls Framework | CC BY-ND 4.0 | Mappings, unmodified |

Each entry gets a DECISIONS row; licence text is checked again at import time and recorded with the source version.

## 4. Control record

EaaS v3 already has most setting-level fields: `cfg.Setting` (product, capability, data type, allowed values, **DefaultValue**,
Intune setting definition ID, OMA-URI, GPO path, registry, **GraphResource**, **GraphProperty**, **PowerShellCmdlet**, portal path),
`pol.ArtifactSetting` (control → setting, expected value) and `pol.ArtifactMapping` (framework mappings with `ConfidenceCode`
*Source* / *Indicative*). The gap is on the control and in the Secure Plus-only fields.

| Field | Where | Exists? |
|---|---|---|
| Product, feature area | `secureplus.ControlProfile` (new) | No – matches the COVERAGE matrix |
| Setting name / path | `cfg.Setting` via `pol.ArtifactSetting` | Yes (link coverage to be measured in phase 0) |
| Graph property / PowerShell cmdlet | `cfg.Setting.GraphProperty`, `PowerShellCmdlet` | Yes |
| PowerShell parameter | `secureplus.SettingExtension` (new) | No |
| Azure Policy definition ID | `secureplus.SettingExtension` | No |
| Recommended value | `CatalogControl.Value` / `pol.ArtifactSetting.ValueRaw` | Yes |
| Default value | `cfg.Setting.DefaultValue` | Column exists, mostly empty |
| Assess method (Graph / DNS / PowerShell / ARM / Manual) | `ControlProfile.AssessMethod` | Implied by evaluation type today |
| Deploy method (Graph / PowerShell / Azure Policy / ARM / GPO / Manual / None) | `ControlProfile.DeployMethod` | Implied by kind today |
| User impact | `CatalogControl.UserImpact` | Yes |
| Framework mappings + confidence | `pol.ArtifactMapping.ConfidenceCode` (*Indicative* unless the source states it) | Yes |
| Source, version, URL, retrieved date | `ControlProfile` | Source name only today |
| Origin, IsModified, ModifiedBy/At, Retired | `ControlProfile` (D4) | No |
| Legacy tag | `ControlProfile.IsLegacy` (D6) | No |
| Licensing | `pol.Artifact.MinLicenseSkuId` from the knowledge base (D5) | Column exists |

Schema change proposed: **V0011** adds `secureplus.ControlProfile` and `secureplus.SettingExtension` (no change to EaaS tables).

## 5. Connections needed (read first)

All assessment uses the **Read** app registration; write permissions only on the **Write** registration, only for deploy phases, with
step-up MFA (4.0.0-beta.2).

| Gap | Read connection | Write (later) |
|---|---|---|
| Entra tenant settings (auth methods, authorization, cross-tenant, PIM settings, access reviews, groups, devices) | Graph: `Policy.Read.All` (have), `RoleManagementPolicy.Read.Directory`, `AccessReview.Read.All`, `Directory.Read.All`, `EntitlementManagement.Read.All` | `Policy.ReadWrite.*` per policy type (4.4) |
| Exchange, Defender for Office | EXO PowerShell app-only: `Exchange.ManageAsApp` + **View-Only Configuration** / **Security Reader** role groups | EXO PowerShell (4.5) |
| Purview (DLP, labels, retention, audit) | S&C PowerShell app-only: `Exchange.ManageAsApp` + **View-Only DLP Compliance Management** / **Compliance Data Administrator (read)** role; eDiscovery cmdlets do not support app-only (stay Manual) | S&C PowerShell (4.5) |
| Teams | Teams PowerShell app-only with **Teams Reader** / **Global Reader** | Teams PowerShell (4.5) |
| SharePoint / OneDrive | Graph `SharePointTenantSettings.Read.All` (have: domain restriction); SharePoint admin settings not in Graph → PnP/SPO PowerShell read or Manual | 4.5 |
| Defender for Endpoint tenant settings | Defender for Endpoint API read (`Machine.Read.All`, `SecurityConfiguration.Read.All`); some settings portal-only (Manual) | – |
| Power Platform | Power Platform admin API read (service principal registered as Power Platform management app) | 4.5 |
| M365 admin org settings | Graph where available (`OrgSettings-*.Read.All`), otherwise Manual | – |
| Azure | New ARM connection: **Reader + Security Reader** via Azure Lighthouse (§8) | Azure Policy assignment, JIT only (5.0) |

## 6. Read-only PowerShell assessment (4.1)

- **Exchange Online and Security & Compliance**: app-only certificate authentication on the Read registration (`Exchange.ManageAsApp`),
  service principal added to View-Only role groups only (never Organization Management). The certificate is stored like other tenant
  credentials (encrypted, Read registration only), expiry tracked in Database Health.
- **Teams**: app-only authentication now supports **all** Teams PowerShell cmdlets except a short list. Of those, only the app-management
  cmdlets matter to GDS checks: `Get-AllM365TeamsApps`, `Get-M365TeamsApp`, `Get-M365UnifiedTenantSettings` (org-wide app settings,
  app availability). So only **Teams app permission / org-wide app settings** checks need a **signed-in reader** (Teams Reader or
  Global Reader); every meeting, external access, messaging and channel check runs app-only.
- **Roles**: Global Reader / View-Only roles only; never an admin role for assessment.
- **Modules**: the installer checks and installs **pinned** versions of ExchangeOnlineManagement and MicrosoftTeams on workstations and
  the scheduling PC (offline package option); the app refuses unpinned versions and says which version to install.

## 7. Tenant Configuration Management (TCM) evaluation – not scheduled (reference only)

> **Decision 2026-11-08: TCM is set aside for now.** Nothing below is planned; it is kept so the evaluation does not have to be repeated.

Credit: approach inspired by **TenantBaseline** (ugurkocde, MIT). Facts below are from Microsoft Learn (Nov 2026).

### Findings

- **Status**: the APIs (`configurationSnapshotJob`, `configurationMonitor`, `configurationDrift`, `configurationBaseline`,
  `configurationMonitoringResult`) are documented under Graph **v1.0**, but the authentication setup still says *public preview*.
  Treat as preview: feature flag, fallback always available.
- **Workloads**: Entra, Intune, Exchange Online, Teams, Defender and Purview (Security & Compliance) – **200+ resource types**.
  **Not covered**: SharePoint/OneDrive, Power Platform, Power BI, M365 admin org settings, Global Secure Access, Azure.
- **Read-only by design**: monitors report drift; drift is resolved in the admin centres. TCM never changes tenant configuration.
- **Quotas per tenant**: 30 monitors; fixed 6-hour cycle; 800 monitored resources per day across monitors (up to 200 per monitor
  baseline); 20,000 snapshot resources per month; 12 visible snapshot jobs; snapshots kept 7 days; changing a monitor's baseline deletes
  its results and drifts; resolved drifts deleted after 30 days.

### Mapping to coverage gaps

| COVERAGE area | TCM | Otherwise |
|---|---|---|
| Entra: authentication methods, authorization policy, cross-tenant, PIM settings, groups, devices, CA | Yes (Entra resources) | Graph (already used for CA) |
| Exchange: forwarding, mailbox audit, external tagging, anti-spam/malware, connectors, transport rules, OWA, calendar sharing, SMTP AUTH | Yes (Exchange resources, Exchange.ManageAsApp + View-Only roles) | EXO PowerShell |
| Defender for Office: Safe Links/Attachments, presets, quarantine, ZAP | Yes (Security & Compliance resources) | EXO PowerShell |
| Purview: DLP, labels, retention, audit | Yes (Security & Compliance resources) | S&C PowerShell (eDiscovery stays Manual) |
| Teams: external/guest, meetings, messaging | Yes (Teams Reader role) | Teams PowerShell app-only |
| Intune / Endpoint Security | Yes | Graph (already used) |
| SharePoint/OneDrive, Power Platform, Power BI, M365 admin, GSA, Azure | **No** | Graph / PnP / Power Platform API / ARM / Manual |

**Result**: TCM can automate **20 of the 27 manual checks** (all Exchange, Defender for Office, Teams, DLP and audit checks). The other 7
need other sources: 2 SharePoint link checks (SharePoint admin / PnP), PIM activation approval and local admin on Entra join (Graph),
cloud app discovery (Defender for Cloud Apps API), Sentinel ingestion (Azure, 5.0) and third-party backup (stays Manual).
TCM can also assess the Exchange, Defender for Office, Purview and Teams areas in COVERAGE. Recommendation: **TCM reduces, does not replace**, read-only
PowerShell in 4.1 – PowerShell stays as the fallback (preview risk, quotas, tenants that decline the TCM principal) and for
SharePoint/PnP.

### Adoption rules

1. Optional collector behind a **per-workspace feature flag**; Secure Plus's own Graph/PowerShell engine is the fallback; no feature depends
   on TCM alone.
2. **SQL is the system of record**: every snapshot, monitoring result and drift is copied into SQL as soon as it is read (snapshots expire
   after 7 days; a baseline change deletes monitor history).
3. **Quotas tracked per tenant** (monitors, resources per day, snapshot resources per month, visible snapshot jobs) and shown on the
   workspace; a job that would exceed a quota is not started.
4. **Monitors only for high-value resources** (Conditional Access, Exchange transport/anti-phish, Teams external access, Purview DLP);
   everything else uses scheduled assessments.
5. **Setup** on Tenant App Registration: *Add TCM service principal* (`03b07b79-c5bc-4b5e-9bfa-13acf4a99998`, plus *M365 Admin Services*
   `6b91db1b-f05b-405a-a0b2-e3f60b28d645` if missing) and *Grant read permissions*. Creating a service principal and assigning app roles
   needs a customer admin (Application Administrator / Privileged Role Administrator), so this is a **one-time interactive step by the
   customer's admin in the app**, not something the Read registration can do.
6. The TCM principal gets **read permissions only** (Graph `*.Read.All`, `Exchange.ManageAsApp` with View-Only/Security Reader roles,
   Teams Reader). Microsoft's sample grants `User.ReadWrite.All`; Secure Plus never grants write permissions to it.

### Permissions on the Read registration

| Action | Permission | Note |
|---|---|---|
| Read monitors, results, drifts | `ConfigurationMonitoring.Read.All` | Read |
| Create snapshot jobs and monitors | `ConfigurationMonitoring.ReadWrite.All` | **Decision needed**: it writes only TCM objects (monitors, snapshot jobs), never tenant configuration. Recommend allowing it on the Read registration as a recorded exception |

### Features and release placement

| Feature | Release |
|---|---|
| TCM snapshot collector for Exchange, Defender for Office, Purview, Teams assessment (feature flag) | 4.1 |
| Permission plan on Tenant App Registration: per tenant, what can be granted automatically, what needs manual steps (Exchange/S&C role groups, Teams Reader), what is missing | 4.1 |
| Drift alerts from monitors: notify only **new** drift, suppress duplicates, state in SQL | 4.1 (with drift) |
| Pre/post snapshot around Remediate and change request deployment: property-level diff as change request evidence and real *before* values | 4.1 (evidence), 4.4 (rollback basis) |
| Tenant *as-built* documentation export from a snapshot (deliverable; scheduled for Managed customers) | 4.2 |
| Global Library items export/import as JSON (TCM baseline format where it fits) | 4.2 |

### Risks

Preview status and API changes; quotas on large tenants (20,000 snapshot resources per month); customers declining a Microsoft first-party
principal with read access; Exchange role assignment must be done with PowerShell; data residency of snapshots (Microsoft-held for 7 days);
monitor history lost on baseline change (mitigated by copying to SQL).

## 8. Azure via Lighthouse (5.0)

- **Azure only**: Microsoft 365 keeps per-tenant app registrations.
- Lighthouse delegation gives **Reader + Security Reader** to a dedicated **GDS Azure group**; any write access is an **eligible, just-in-time**
  authorization with MFA and approval, never permanent.
- The GDS group is protected like admins: phishing-resistant MFA, Conditional Access, PIM for Groups, access reviews.
- **Fallback**: a per-tenant app registration with Reader at the management group.
- Assessment reads Azure Policy compliance (MCSB initiative) and resource properties via ARM; deploy (later) = Azure Policy assignment.

## 9. Phased plan

| Phase | Contents | Controls (estimate) | Effort | Release |
|---|---|---|---|---|
| 0 – Foundations | D4 reload safety, `ControlProfile`/`SettingExtension` (V0011), provenance, IE-mode tag, dedupe AV/firewall/ASR, link controls to `cfg.Setting`, stale 3,268 figures fixed | 0 new | 8–10 days | With the policy and policy-set editor (next) |
| 1 – Knowledge import | CIS M365 Foundations 7.0 (160) and Power Platform (16) as knowledge controls (`Kind = Recommendation`, assess/deploy Manual), mapped to areas; SCuBA and Maester IDs as references | ~176 | 5 days | 4.1 |
| 2 – Assess P1 | Automate the 27 manual checks and add assessment for Entra tenant settings, Exchange, Defender for Office, SharePoint, Teams, Purview DLP/labels/retention/audit, Copilot settings – via Graph + read-only PowerShell | ~150 automated (same controls) | 20–25 days | 4.1 (PowerShell), 4.2 (rest) |
| 3a – Endpoint gaps | iOS/iPadOS device restrictions and compliance, App Control for Business, Endpoint Privilege Management, Autopilot/ESP, Linux (MDE), Endpoint Security Device control / Disk encryption / Account protection policies, Intune RBAC and multi-admin approval, AVD | ~200–250 | 20 days | 4.2–4.3 |
| 3b – OIB areas (optional) | OIB controls stay (D3). Only where GDS wants its own wording or values: GDS settings from Microsoft documentation, OIB row kept as a mapping | as needed | as needed | Not scheduled |
| 4 – P2 M365 | Power Platform, Power BI, M365 admin org settings, GSA configuration, Purview advanced (insider risk, DSPM for AI, Customer Lockbox), protected actions, Windows 365/AVD and AI-app CA | ~80–100 | 12 days | 4.3–4.4 |
| 5 – Deploy | Graph-deployable tenant settings (Entra policies) with pre/post snapshot and one-click rollback | – | 15 days | 4.4 |
| 5b – Deploy PowerShell | Exchange, S&C, Teams settings | – | 15 days | 4.5 |
| 6 – Azure | MCSB / Azure Policy + CIS Azure Foundations, Compute, Database, Storage (340 recommendations) | ~300–400 | new module | 5.0 |

Counts are estimates until each source is parsed; phase 1 firms them up. Read-only PowerShell assessment moves from 4.5 to **4.1** (D8).

## 10. Open questions for review

None open. Answered 2026-11-08: TCM not scheduled; GDS holds CIS membership; OIB controls stay visible (licence compliant);
phase 0 ships with the policy and policy-set editor; *Shared device: QR code + PIN* added as an option (not MFA, paired with CA-213/CA-214).

**Found while adding CA-213/CA-214:** the Documents source has changed since the shipped catalogue (built from Documents@f10e83b).
Regenerating today would remove 273 Intune controls (mostly OIB, incl. 36 IE settings) and change 11 (CA-200, CA-208, CA-209, CA-210,
CA-600 among them). Regeneration is held and reviewed at the start of the policy editor work.
