# Changelog

All notable changes to Secure Plus – Modern Work. Versions follow Semantic Versioning; pre-releases carry a suffix.

## [Unreleased]

## [4.0.0-beta.2] – 2026-11-09

Second 4.0 test build. Upgrades a 4.0.0-beta.1 database (V0010–V0012). See `docs/release-notes/v4.0.0-beta.2.md` and
`docs/testing/4.0.0-beta.2-checklist.md`.

### Added – tenant onboarding and scans
- **Connect tenant** on New Customer: automatic registration (one Global Administrator sign-in), then a quick scan. *Skip – connect later* is available.
- **Quick scan** (users, groups, membership, roles, licences, domains, posture snapshot) and **in-depth scan** (adds Intune devices, policy and app
  assignments, sign-in activity, MFA methods, licences per user, tenant settings and the read-only PowerShell collector).
- Live Assessment runs quick scans. *Assessment Engine* is renamed **Assessment Scan** and offers both scan types; schedules choose one too.
- **Tenant inventory** stored (V0011): users (name, UPN, job title, department, usage location, licences), groups, membership and devices. Removed
  objects are marked, not deleted. Customer Profiles > *Tenant Users and Groups* lists and exports them (unmasked).
- **Entra group pickers** for user groups (Customer Profiles) and for group deployment variables (Change Execution).

### Added – catalogue and editor
- **Settings > Policies and Policy Sets**: Leads create, edit, hide or delete policies and policy sets. Edits are stored apart from the GDS
  catalogue (V0012, append-only history), so a reload or upgrade never overwrites them; *Update available* and *Retired* are flagged.
- New control fields: product, feature area, default value, assess method, deploy method, source version and URL.
- **Tenant settings** (195 `TS-` controls) for every Microsoft area: Entra ID, Conditional Access, Intune, Defender XDR, Exchange Online and
  Defender for Office 365, SharePoint/OneDrive, Teams, Purview, Copilot and AI, Power Platform, Microsoft 365 admin center, Global Secure
  Access, Windows 365/AVD, hybrid identity and Azure. 131 are assessed automatically (93 Graph, 38 PowerShell); 64 are manual checks with click paths.
  A setting that needs a licence the tenant does not have is reported as *Info: not applicable* and not scored.
- **Read-only PowerShell collector** (in-depth scans): Exchange Online, Security & Compliance and Teams settings with app-only certificate sign-in,
  PowerShell 7 and pinned modules (ExchangeOnlineManagement 3.10.1, MicrosoftTeams 8.0.0). Only Get- cmdlets run. The installer can install
  PowerShell 7 and the modules (`prerequisites\Install-SecurePlusPowerShellModules.ps1`).
- *MFA method* option **Shared device: QR code + PIN (single-factor, not MFA)** for frontline shared iPads and phones: never a default, never chosen
  automatically, paired with Conditional Access CA-213/CA-214 for the `FrontlineQrCodeGroup` deployment variable; the SOW states it is not MFA.
- `docs/catalog/COVERAGE.md`, `docs/catalog/IMPORT_PLAN.md` and `docs/catalog/SOURCE_REVIEW.md`.

### Added – tenant access
- **Read registration** ("GDS Secure Plus Read"; the older "GDS Secure Plus Agent" app is renamed on update): read-only Microsoft Graph permissions,
  Exchange.ManageAsApp and the Global Reader role, certificate credential. Updating removes write permissions and client secrets.
- **Deploy registration** ("GDS Secure Plus Deploy"): write permissions only, created by **Enable Deployment Access** for a deploy phase and deleted
  by **Remove Deployment Access** (Customer Profiles > Tenant Access). Change Execution writes only through it.
- **Certificate credentials**: self-signed RSA 2048, one year. Only the public key goes to Microsoft Entra; the private key is kept in the
  encrypted secret store and is never shown, logged or exported.

### Changed
- Conditional Access: every policy excludes the break-glass group (catalogue test, policy editor, deployment pre-check), is deployed report-only and
  is monitored at least 14 days (21 recommended) before a Staged task can be completed.
- Catalogue rebuilt from Documents@5900c7e (3,333 controls): CA-213/CA-214 added; CA-200 renamed "…Strong Auth or Trusted Device or Trusted Location";
  36 Internet Explorer settings and 66 exact duplicate Intune controls removed; tenant settings added.
- The 2022 NAICS sector list is no longer loaded into `ref.CodeValue`; the Industry list replaces it.
- The free-text **Licensing** column is removed from `secureplus.CatalogControl` (V0010) and from the Catalog page and export.
- Reports use the latest in-depth scan; quick scans are used only when there is none.

### Database
- **V0010** drops `secureplus.CatalogControl.Licensing`; **V0011** adds the tenant inventory and scan types; **V0012** adds Lead edits and their
  append-only history.

## [4.0.0-beta.1] – 2026-11-08

First 4.0 test build. See `docs/release-notes/v4.0.0-beta.1.md` and `docs/testing/4.0.0-beta.1-checklist.md`.

### Added
- **Project Plan tasks**: detail pane (pop-out window that remembers size and position, docks back if the monitor is gone),
  inline Start/Finish dates, Up/Down (display order only), Insert above/below, completion check mark, notes, linked change
  requests and controls, and history.
- **Staged** task status: a change created report-only (Conditional Access) or unassigned (Intune) leaves the task Staged; it can
  be completed once every linked control passes.
- **Remediate from a task**: needs an approved linked change request; the confirmation shows tenant, exact changes with their
  before state, method and rollback note, and asks for the passcode. Every attempt goes to task history and the audit ledger.
- **Mark as done** for manual steps: a note is required and one attachment (up to 10 MB) is optional; recorded as
  "Manual – attested".
- **Change request templates**: a protected GDS default, a global list, per-customer copies linked to their source,
  Save as Global, approval email preview.
- **Industry** list (Lead-editable, 20 defaults incl. Other, specify) on Customer Profiles, new customer form, exports, SOW and
  Fleet View (column and filter).
- Network Access (Global Secure Access) solution area; Field / Vessel / Remote Site classification; frameworks CIS M365 L1/L2,
  NIST CSF 2.0, GLBA/FTC.

### Changed
- Experience profiles renamed: GDS - Essentials / Balanced (Recommended) / Locked Down; User Group - Executive, Frontline,
  International Traveler, Privileged Administrator, Developer, Contractor / Vendor, Regulated Data Handler, Remote Site.
- Top bar: customer, workspace and tenant pickers are icon buttons; the customer · tenant and workspace stay visible.
- Essential Eight retired (kept read-only where already selected); PCI DSS 4.0.1 and CIS v8.1 labels aligned; Sales / Field is now Sales.
- NAICS industry code hidden in the UI (values kept).

### Fixed
- Customer Profiles > Integration Settings and Workspace Settings now save, keep existing values and show errors.
- Dashboard tile hover no longer shows "An unexpected error occurred".

### Database
- Migration **V0009**: `secureplus.Industry`, `secureplus.ChangeRequestTemplate`, `secureplus.ProjectTaskEvent` (append-only),
  new `dlv.ProjectTask` columns. 4.0 starts from a fresh database.

## [3.2.0-beta.1.1] – 2026-11-08

### Fixed
- Moving the mouse over Dashboard tiles showed "An unexpected error occurred" twice: the hover lift tried to animate a shadow
  shared by a style (frozen). The tile now animates its own copy of the shadow and returns to the style's shadow; hover effects
  can no longer show an error.

## [3.2.0-beta.1] – 2026-11-08

First step of the approved blueprint (`docs/BLUEPRINT.md`). See `docs/release-notes/v3.2.0-beta.1.md` and
`docs/testing/3.2.0-beta.1-checklist.md`.

### Added
- **Install types**: the installer asks Workstation, Server or All-in-one and the shared SQL Server (tested with Windows
  Authentication); silent switches `/TYPE=`, `/SERVER=`, `/DATABASE=`. Server and All-in-one installs create or upgrade the
  database and load the reference catalog. Workstations never create or upgrade it. `docs/SERVER_INSTALL.md`.
- **Version check**: a workstation older than the database is blocked with *Update required*; a newer one asks for the Server
  install to be upgraded first.
- **Settings > Users & Roles**: per-user roles (Sales, Engineer, Technician/Deployer, NOC, Lead, Manager) by Windows account,
  no AD groups. Grant Database Access creates the Windows login and `secureplus_<role>` membership, or shows the SQL script.
- **Database roles** (migration V0008): tenant credentials readable only by Engineer, Technician/Deployer and Lead; users
  changed only by Leads.
- **Audit ledger**: every audit event, credential use, approval and deployment also goes to `secureplus.AuditLedger`, a SQL
  Server 2022 append-only ledger table that nobody can change or delete.
- **Scheduling computer** (Settings > Database Health): only the chosen PC runs recurring assessments; heartbeat and a warning
  when it stops.
- **Move to SQL Server** (Database Configuration, SQLite only): backs up the file, copies every table, keeps the copy only when
  every row count matches; the SQLite file is never changed.

### Changed
- SQLite is no longer offered for new databases (kept for automated tests and existing files until they are moved).
- Organization key can be held only by Engineer, Technician/Deployer and Lead once users are defined.
- CI compiles the installer script.

## [3.1.0] – 2026-11-08

See `docs/release-notes/v3.1.0.md` for details.

### Added
- **Apply to Tenant** (Deployment > Change Execution): approved change requests are created in the customer tenant through Microsoft Graph
  after a destructive confirmation prompt **and** the Advanced Mode passcode. Conditional Access policies are created in report-only state;
  Intune and endpoint security policies as defined, unassigned. Controls already passing are skipped, policies whose name already exists are
  skipped, every create (or failure) is audited (`TenantChangeApplied` / `TenantChangeFailed`), and the tenant is re-assessed afterwards.
- **Merged source policies**: controls from one Policy Repository folder become one policy, as the repository README describes (settings
  catalog settings combined by definition and renumbered; other Graph objects merged by property).
- **Deployment Variables** per customer (ring, administrator/emergency-access/exclusion groups, factory-reset account, terms-of-use and Global
  Secure Access IDs, policy name prefix). Group variables accept a group name, resolved to its ID in the tenant. Policies with a variable still
  unset are reported as manual and not created. Simulation packages now carry the merged, filled policies.
- **PolicyDeployment** permissions (`Policy.ReadWrite.ConditionalAccess`, `DeviceManagementConfiguration.ReadWrite.All`,
  `DeviceManagementApps.ReadWrite.All`, `DeviceManagementServiceConfig.ReadWrite.All`) on every app registration.

### Changed
- JSON written by Change Execution keeps `<` and `>` readable (placeholders were escaped as `\u003C…\u003E`).

## [3.0.0] – 2026-11-08

See `docs/release-notes/v3.0.0.md` for details.

### Added
- **Experience**: user groups in customer profiles (up to 25: executives, frontline, travellers, administrators…); Assessment > Experience
  Configuration (plain-language choices by Microsoft solution, per customer and per group, with profiles, compliance targets and live
  security and friction scores); Reporting > Experience Map (every group's experience, what frameworks such as NIST, CMMC or HIPAA change,
  conflicts and the least disruptive fix); import of the GDS EaaS form results (personas become groups).
- **Roadmap**: tiles for every phase; phases scheduled from a start date on Monday–Friday 8-hour days (dates and hours line up); ordering by
  dependencies, then value per hour; flow timeline with dependency arrows; filters, display options, phases collapsed on first open, export of
  the ordered work packages and a timeline image.
- **Heat Map**: six heat maps (compliance, remediation priority, fleet compliance, compliance trend, ATT&CK threat coverage by service tier,
  experience friction).
- **Deployment > Project Plan** (from the WBS and/or the roadmap, work-day schedule, editable tasks, timeline, saved to dlv.ProjectPlan) and
  **Assessment > SOW** (templates saved to dlv.SowTemplate, filled from the customer, user groups, assessment, roadmap and project plan; HTML and
  Word).
- **Catalog**: every remediation option (Graph REST and PowerShell, JSON, Settings Catalog / OMA-URI, Group Policy, registry .reg and PowerShell,
  portal steps, CIS/Microsoft baseline procedures).
- Interactive charts with hover, lift and click-to-drill-down on the Dashboard, Fleet View, Roadmap, Heat Map, Experience pages and Project Plan.
- 34 built-in themes (Light, Medium, Dark, System, GDS Light/Dark, Aurora (Copilot-inspired), Clay (Claude-inspired), Blueprint (Visio-inspired),
  diagram themes, moods, High Contrast) with a gallery; motion (menus slide open, pages glide in, tiles lift); text can be selected and copied
  anywhere.
- GDS icon and logo: title bar, taskbar, desktop shortcut, installer wizard and MSIX tiles.
- Hidden **Advanced** menu (Data Explorer, Diagnostics) shown after Advanced Mode is enabled with its passcode in Settings > Application Settings.

### Changed
- Menu order Dashboard, Assessment, Deployment, Reporting, Workspace, Settings. Workspace holds Customer Profiles, Specification Profiles,
  Tenant App Registration and Exceptions; Settings is its own menu. Routes changed accordingly (`settings/...`, `workspace/...`); Navigation.ini
  is upgraded automatically (the old file is kept as Navigation.v1.bak).
- Catalog details: section headers are readable in dark themes.

## [2.4.0] – 2026-11-08

See `docs/release-notes/v2.4.0.md` for details.

### Added
- Knowledge base, phase 3 (about 110,000 rows in 45 tables): Microsoft licensing (619 SKUs, 1,224 capabilities, 5,972
  SKU-to-service-plan links); MITRE CTID threat coverage (1,630 capability-technique and 6,387 NIST/CIS-technique links);
  service tiers (features and base-licence capabilities); the Customer Deployment Checklist v2.0 as a GDS standard;
  1,786 configuration changes with 10,663 framework links, 1,459 known issues and 3,238 guidance rows from the catalog;
  Microsoft baseline deltas; 21,754 cross-baseline relations; 12,515 audit and remediation procedures; experience
  expectations; rollout rings; CIS and CMMC requirement attributes, scope categories and status labels; the scoring
  model with 28,664 enrichment values and 1,591 risk-ranked SCF controls; WBS complexity factors; the load log.
- Assessment results are published to the EaaS tables after each completed run: open findings, derived requirement
  results, score snapshots and observation snapshots. Technicians get the Engineer role.

### Changed
- Service tier taglines and descriptions follow the Service Tiers deck.

## [2.3.0] – 2026-11-08

See `docs/release-notes/v2.3.0.md` for details.

### Added
- Knowledge base, phase 2 (about 90,000 rows in 34 tables): 56 CIS Benchmarks and the Microsoft security baselines for
  Windows 11 26H2, Windows Server 2025 and Microsoft 365 Apps (10,507 recommendations, 8,695 technical settings with
  21,431 aliases, 181 profiles, 2,796 sections, 843 STIG references, 7,966 links to CIS Controls v8.1, 1,290 to NIST SP
  800-53 and 1,132 to NIST CSF 2.0); the Secure Plus WBS (193 tasks linked to the GDS standard items); 613 questions (474
  discovery decisions and the EaaS forms with answers, rules and personas); the deployment method matrix (115 setting
  groups, 14 methods, 37 products); source documents and licences.
- `docs/knowledge-base/CIS_CSV_EVALUATION.md`: how the Copilot CIS CSV files were checked and corrected.
- Opt-in SQL Server test now loads both knowledge-base phases.

### Fixed
- Knowledge base phase 1 could not record "waiting for the catalog" on SQL Server (hash longer than the column).

## [2.2.1] – 2026-11-08

See `docs/release-notes/v2.2.1.md` for details.

### Fixed
- The knowledge base failed to load on SQL Server ("Cannot insert duplicate key row in object 'fw.Requirement' with unique
  index 'UX_Requirement_NaturalKey'"): four requirement codes in the SCF workbook differ only in letter case, which SQL
  Server treats as equal. They are merged, and the loader now compares keys the way SQL Server does on every database.
- First-run setup: buttons that ask for confirmation (Create Database, Install or Repair) failed with "Cannot set Owner
  property to a Window that has not been shown previously". Dialogs now belong to the window that is showing.
- "The process cannot access the file 'audit-….jsonl' because it is being used by another process" when opening
  Auditing and Logs: the audit file is now shared between the page, other Secure Plus windows and the scheduled host.
- Assessments: Intune settings catalog and endpoint security policies (`configurationPolicies`) and assigned apps were
  requested from Graph v1.0, where they do not exist, and were always missing; they now come from the beta endpoint.
- Create Database on a server that still has the files of a removed SecurePlus database now explains how to fix it.

### Added
- `DeviceManagementScripts.Read.All` in the app registration, so Intune PowerShell scripts are assessed. Existing
  customers: open **Tenant App Registration** and select **Update Permissions Automatically** once per tenant.
- Opt-in SQL Server test of the catalog and knowledge base (`SECUREPLUS_SQLSERVER_TEST_SERVER`).

## [2.2.0] – 2026-11-08

See `docs/release-notes/v2.2.0.md` for details.

### Added
- Knowledge base, phase 1: the Secure Controls Framework 2026.3, NIST SP 800-53 Rev 5 (full text) and SP 800-53A, and MITRE
  ATT&CK Enterprise are loaded into the EaaS v3 tables (`fw.*`, `threat.*`, `ref.*`) – about 330,000 rows in 27 tables:
  271 frameworks, 44,262 requirements, 76,023 SCF mappings, 11,296 assessment objectives, 422 evidence artifacts, the
  risk and threat catalogs with 175,010 likelihoods, 706 ATT&CK techniques with tactics and mitigations.
- The Secure Plus catalog's framework references are linked to SCF, so every control reaches SCF controls, risks, threats
  and ATT&CK techniques.
- Database Health > Data Coverage shows the knowledge base version and offers Reload Knowledge Base.

### Changed
- `docs/DATA_COVERAGE.md`: 132 tables remain empty (was 155); 50 are planned for later knowledge-base phases.

## [2.1.0] – 2026-11-08

See `docs/release-notes/v2.1.0.md` for details.

### Added
- The SecurePlus database is the system of record: customer profiles, workspaces, settings, themes and tenant secrets
  are stored there and shared by every computer. Each computer's local files are imported once and kept as a backup.
- Organization key: tenant secrets are stored in the database encrypted with AES-256-GCM; created once, entered on
  each computer (Credential Management or setup).
- Customer Profiles: New Customer form; industry (2022 NAICS sector), organization size, address, phone, e-mail,
  website, CRM ID; sales lead, technical lead / assigned engineer, project manager, service tier; contacts editor
  with phone, mobile, primary contact, change approver and decision owner.
- Workspace Configuration: create, edit, open and archive workspaces with a customer and a theme; themes (create,
  preview, default, delete); switching workspace switches the theme.
- First-run setup asks whether the database is Production or Test; Test shows an orange TEST banner.
- Database Health > Data Coverage (row counts of every table and who fills it), `scripts/sql/Audit-SecurePlusDatabase.sql`
  and `docs/DATA_COVERAGE.md`.
- Seeded reference data: NAICS sectors, organization sizes, contact roles and other code lists, implementation
  statuses, maturity levels, relationship types, service tiers, application roles and experience areas.

### Changed
- `cust.Customer`, `cust.Contact`, `cust.Tenant`, `cust.Engagement`, `audit.TenantConnection`, `cust.TenantLicense`,
  `kb.LicenseSku`, `cust.Exception`, `deploy.ChangeRequest`, `sec.AppUser` are now filled.
- Workspace Configuration lists settings by category in plain language (no file names) with the computers they
  apply to and who changed them last.
- The Environment setting is replaced by the database's Production/Test mode.

### Fixed
- Customer contacts were never saved (the profile file had no contacts section).

## [2.0.0] – 2026-11-07

See `docs/release-notes/v2.0.0.md` for details.

### Added
- Change Request workflow (Draft → Submitted → Approved/Rejected → Scheduled → Completed), Change Execution (pre-checks,
  plan, log, deployment package, Record As Applied with re-assessment) and Tracker.
- Customer Profiles (search, inline edit, audit, export without secrets), Specification Profiles (builder, clone,
  assign/unassign) and Exceptions.
- Workspace Configuration (edit, JSON export, validated JSON import with preview) and Baseline Configurations (build,
  clone, edit, delete, activate; per-customer compliance panel).
- Database Health for SQL Server and Azure SQL with thresholds, 45-second refresh and graceful degradation.
- `docs/UI_STYLE_GUIDE.md`, GDS header wordmark.

### Changed
- Title Case sweep of labels, headings, buttons and columns; the shared search box everywhere.
- Golden Tenant Configuration is replaced by Baseline Configurations; Workspace Configuration Import/Export and Customer
  Profile Import/Export pages are replaced by the new pages.

### Removed
- The placeholder page: every menu entry now opens a working page.
- Logo entries in the default `Branding.ini` are commented until logo files are supplied (no missing-asset warnings).

## [2.0.0-beta.1] – 2026-11-07

Pre-release of 2.0. See `docs/release-notes/v2.0.0-beta.1.md` for details.

### Added
- Versioned, idempotent reference seed in the installer (controls, policy sets, specifications, baselines, solution and
  experience areas, remediation content and implementation details) with setup progress and build validation.
- Recurring assessments: Hourly/Daily/Weekly/Monthly scheduler, run history and a Windows Task Scheduler background runner.
- Live Assessment run list (up to 25 tenants) with throttled, cancellable runs.
- Roadmap, Heat Map and Fleet View; six completion reports; Specification Comparison.
- Shared search box, multi-select filter, customer-scoped pages and exporter (.csv, .html, .json, .txt).
- Windows CI workflow with a data-driven smoke test of every page.

### Changed
- Assessment results are written in chunks; partial runs are kept and marked Partial; live runs are stored.
- Catalog filters default to (All); Framework is a multi-select; Control Details are collapsible sections.
- Golden Tenant Comparison is renamed Specification Comparison. Menu sections start collapsed.
- The score covers the active baseline configurations.

### Fixed
- Duplicate catalogue IDs (`ES-1317` / `ES-1317-2` and 67 others): identical copies merged into the base ID with aliases.
- Upgrading no longer leaves removed pages in the menu (Navigation.ini is upgraded; unknown routes are warnings).

### Removed
- Placeholder pages Script Generator, Config File Generator, Teams Integration and ConnectWise Integration from the menu.
