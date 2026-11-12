# Secure Plus – Modern Work: Application Blueprint

Global Data Systems (GDS) – Modern Work / Cloud Services. Owner: Toby Blanchard. Status: **Approved** (2026-11-08). The application is a **proof of concept**; this blueprint applies if it is approved for use.
Decisions are recorded in [DECISIONS.md](DECISIONS.md) (entries dated 2026-11-08, "Blueprint").
Nothing here is built yet unless marked **(exists)**. The current release is 3.1.0.

---

## 1. Purpose

Secure Plus is the single tool GDS uses to run a Modern Work engagement, from the first customer conversation to a deployed,
monitored Microsoft 365 tenant.

| Step | Meaning | Where |
|---|---|---|
| DEFINE | What the tenant should look like | Specification |
| MEASURE | What it looks like today | Assessment |
| CLOSE | The gap, safely, approved and tracked | Deployment |
| WATCH | Drift over time; support the customer | Recurring assessments, drift, Quick View |

```
Specification − Assessment = Gap → Roadmap → SOW → Project Plan → Pilot & Validation
    → Change Requests → Deployment → Re-assessment → (repeat; drift watched continuously)
```

## 2. Engagement lifecycle

| # | Phase | Who | In the app | Outcome |
|---|---|---|---|---|
| 0 | Preparation (any time) | Engineer, Lead | Library | Reusable user groups, experience profiles, baselines, specification templates |
| 1 | Discovery | Customer, Sales, Sales Engineer | Discover & Specify (EaaS form import **(exists)**) | Requirements, kinds of users, drivers |
| 2 | Onboarding | Sales Engineer | Customers → Customer Profile, Workspace, Engagement | Profile, tenant workspace, draft specification |
| 3 | Assessment | Engineer | Assess | Persisted results against a specification version |
| 4 | Specification | GDS + customer | Discover & Specify; Specification Comparison | Agreed target state and each group's experience; signed version locked |
| 5 | Statement of Work | GDS; customer signs | Plan → SOW (priced from the rate card) | Signed SOW |
| 6 | Project Plan | GDS → GDS approves → customer approves | Plan → Roadmap, Project Plan | Approved, phased plan |
| 7 | **Pilot & Validation** | Technician, customer | Deploy → Pilot | Readiness gate passed; rings signed off; CA enabled after report-only period |
| 8 | Deployment | Technician; customer approves | Deploy → Change Requests, Execution, Tracker | Changes applied and verified |
| 9 | Recurring assessment, drift, support | Designated scheduling PC; NOC / Help Desk | Operate | History, drift alerts, Quick View |
| 10 | ConnectWise Manage (future) | NOC / Help Desk | Integrations | Ticket shows changes, drift, caller experience, KB articles (no AI) |

### Phase 7 – Pilot & Validation
1. **Readiness gate**: licences, consented permissions, break-glass groups confirmed for this workspace, deployment variables set,
   change window agreed.
2. **Pilot ring**: Conditional Access created report-only; Intune and endpoint security assigned to the pilot ring group.
3. **Validate**: report-only and sign-in impact reviewed, user feedback recorded; go / no-go criteria and a rollback plan per ring.
4. **Promote** ring by ring. No promotion without that ring's customer sign-off (recorded on the engagement).
   Every CA policy is deployed in **audit (report-only) mode** and monitored for **2–3 weeks**: it moves to on only after the **minimum report-only period of 14 days** (21 days recommended); a Staged task with CA cannot be completed earlier. Every CA policy excludes the emergency-access (break-glass) group (except CA-110, which applies to that group).
5. Formal UAT with test scripts is an optional add-on for regulated customers (HIPAA, CMMC).
Ring progress shows in the Tracker and the Project Plan.

## 3. Customers, workspaces and engagements

| | Customer Profile | Workspace | Engagement |
|---|---|---|---|
| Is | Who the customer is | One tenant, permanent | One piece of contracted work |
| Holds | Company, contacts, approver, defaults, discovery answers, parent link | Tenant connection, specification (versioned), assessments, schedules, drift, exceptions, user groups, experience profiles, baselines, deployment variables | SOW, project plan, pilot/rings, change requests, owner, dates, the specification version change it delivers (e.g. v1 → v2) |
| Status | — | Prospect · Active · Managed · Archived | Prospect · Active · On Hold · Lost · Completed |
| Changes | Rarely | Continuously | During the engagement |

```
Customer Profile (optional parent, one level; roll-up toggle in Fleet View and Reporting)
└── Workspace = one tenant (permanent)          e.g. contoso.com
    ├── Specification v1 (signed, locked) → v2 (draft) …
    ├── Assessments (each records the specification version measured), drift, schedules, exceptions
    └── Engagements
        ├── Identity Hardening 2026   (Completed, delivered v1)
        ├── Device Modernization 2027 (Active, delivers v1 → v2)
        └── Managed Service           (permanent once the workspace is Managed)
```

### Rules
- **W1** A workspace targets exactly one tenant, for good. A customer with no tenant yet gets one workspace in status Prospect,
  shown as *tenant not connected*; it cannot be assessed until a tenant is connected, after which the tenant is fixed.
- **W2** Workspace status: Prospect → Active automatically when any engagement becomes Active; Managed when under a managed service;
  Archived ends it (read-only).
- **W2a** When every engagement in a Prospect workspace is Lost, the app prompts to archive the workspace.
- **W3** Every Managed workspace has a permanent *Managed Service* engagement for ongoing change requests, so gating is the same everywhere.
- **W4 Gating by engagement status**

  | Engagement | Assess | Draft CR | Submit / approve CR | Apply to tenant |
  |---|---|---|---|---|
  | Prospect | ✔ | ✔ (for SOW estimates) | ✖ | ✖ |
  | Active | ✔ | ✔ | ✔ | ✔ |
  | On Hold | ✔ | ✔ | ✖ | ✖ |
  | Lost, Completed, or workspace Archived | read-only | ✖ | ✖ | ✖ |

  Admin-only override for GDS-owned demo/lab workspaces, logging who, when and why.
- **S1 Sub-companies**: own contract, contacts, approvers or billing → own Customer Profile linked to a parent; otherwise a workspace
  under the parent. Parent is a self-reference (deeper nesting is a small change later). A profile cannot be its own parent, and a
  child cannot also be a parent.
- **I1 Inheritance**: contacts and approver, user groups, compliance targets and deployment variables are inherited from the customer
  and can be overridden per workspace. Each shows *Inherited* or *Overridden* with *Reset to customer default*; changes are audited.
  Deployment variables from the customer are a starting template only: each workspace confirms its own break-glass groups before any
  deployment. Tenant connection and credentials are never inherited.
- **UI** The global selector at the top selects *Customer › Workspace* (and the engagement where relevant).

### Migration from 3.x
- One workspace per customer and connected tenant, named after the tenant domain; a customer without a tenant gets one Prospect workspace.
- Assessment history and schedules move to the workspace; existing plans and change requests move to an engagement named
  **"Migrated – pre-Workspace"**.
- Backup first; re-runnable (idempotent); per-table counts logged before and after; no source data deleted unless counts match.

## 4. Library: Reference Catalogue and Global Library

| Layer | Who maintains | Storage | Editable |
|---|---|---|---|
| Reference Catalogue | GDS, shipped with the installer **(exists)** | Seed data | Read-only; seed upgrades never touch customer or library data |
| Global Library | Leads / Architects | Shared SQL | Versioned |
| Workspace (local) | Engineers | Shared SQL | Per workspace |

- **Items**: User Groups (kind of user → experience profile), Experience Profiles, Baseline Configurations, Specification Templates.
  The 10 built-in experience profiles stay read-only in the catalogue; engineers clone them.
- **Assign from Library** puts a linked copy in the workspace (source item and version, locally modified flag).
- **Save as Global** is a proposal: proposer, diff and reason go to an approval queue; a Lead approves or rejects with a comment
  (self-approval allowed and logged).
- **Update available**: a linked copy shows a notice; the engineer reviews the diff and applies or ignores. Bulk action
  *Review and apply to selected workspaces* lists every linked copy as unmodified / locally edited / signed-locked.
  Every apply or ignore is logged. A signed specification never changes silently.
- Each Global item tracks owner, version history, *Used by N workspaces*; delete is blocked while in use.
- **Change request templates (4.0.0-beta.1)**: a protected GDS default plus a global list; each customer gets linked copies
  (source key and version kept), edits are versioned, *Save as Global* adds a customer copy to the global list. *Update available* comes in 4.2.
- **Industry list (4.0.0-beta.1)**: reference data editable by Leads (20 defaults incl. *Other, specify*); replaces NAICS in the UI.
- **Knowledge base editor**: the catalogue's GDS known issues (1,459) and guidance (3,238) **(exist, drafts)** get an edit and review
  page for Leads; used by Quick View and ConnectWise.

## 5. Specification

- Derived per workspace: experiences + baselines + frameworks → controls → requirements → policy sets → policies.
- **Versioned**: one *current* version per workspace; older versions read-only. Signing (SOW approval) locks the version;
  any change creates the next version as a draft needing re-approval.
- Compare any two versions with Specification Comparison (has / missing / different) **(exists for tenant comparison)**.
- The v1 → v2 difference can produce a draft SOW change order (added and removed controls, effort).
- Inputs (4.0.0-beta.1): experience profiles *GDS - Essentials / Balanced (Recommended) / Locked Down* and *User Group - …* profiles;
  classifications incl. Field / Vessel / Remote Site; Network Access (Global Secure Access) solution area; frameworks CIS M365 L1/L2,
  NIST CSF 2.0, GLBA/FTC, PCI DSS 4.0.1, CIS v8.1 (Essential Eight retired: kept read-only where already selected).

## 6. Approvals (no mail server in the app)

- The app builds branded approval emails and documents (Change Request, Project Plan, SOW, Ring sign-off) with a reference ID in the
  subject, e.g. `[GDS CR-0042]`; the engineer sends from Outlook.
- Recording: drag the customer's reply onto the item (classic Outlook), or save it as .eml/.msg and drop or browse (new Outlook).
  The app reads the reference ID, sender and date, pre-fills the approval, and the engineer confirms in one click; the email is
  attached as evidence.
- **Pending approvals** view: sent but unanswered, days waiting.
- No automatic inbox reading. Future: Power Automate Approvals or Microsoft Forms links.

## 7. Deployment

| Method | Status | Notes |
|---|---|---|
| Microsoft Graph (CA, Intune, endpoint security) | **(exists, 3.1)** | CA report-only, Intune unassigned; merge + variables |
| Assign to ring / enable CA | Planned | Respects phase 7 gates and the minimum report-only period |
| Exchange Online / Teams / SharePoint (PnP) PowerShell | Planned, **opt-in per workspace** | Only for settings Graph cannot reach. Off by default: recorded customer consent (approval email attached, who, when), then the role grant (`Exchange.ManageAsApp`, Exchange / Teams Administrator); workspace shows *PowerShell deployment enabled*. Prefer time-bound role assignments tied to the engagement; prompt to remove roles when it completes unless Managed with agreed standing access. Roles verified before each run, otherwise a technician-run script. Report of workspaces holding admin roles, since when, with consent evidence. Certificate-based app auth per tenant; modules bundled at pinned versions (PnP needs PowerShell 7). **Role removal**: by default the app removes the admin roles when the engagement closes and records the removal; PIM time-bound assignment is used only where the customer has Entra ID P2. The access-review report includes a *roles still held* list. **GDAP**: technician-run Exchange and Teams scripts use GDS's GDAP access; PnP over GDAP is proven on one customer first, otherwise technician-run scripts use the customer's own admin account. |
| GPO package export | Planned | Settings, README and source CR ID, stored against the engagement; not pushed live |
| Technician-run scripts | Planned | Mark done, attach output, app re-assesses |

All methods go through the same gate (engagement Active, role, step-up sign-in), pre-checks, logging (who, when, tenant, CR, method,
result) and re-assessment.

## 8. Operations

- **Scheduling**: recurring assessments and drift checks run on the designated scheduling PC (§10.1).
- **Drift**: a control that was passing now fails. States New → Acknowledged (who, when) → Resolved or Accepted exception; no re-alert
  for the same drift each run. Marked *changed outside Secure Plus* when the app did not make the change, with the window between
  assessments. One click *Create draft change request to restore* (normal approval; no automatic fix).
- **Notifications** to a Teams channel through a Power Automate Workflows webhook: Critical/High immediately, Medium/Low in a daily
  summary. *Settings → Teams Notifications*: step-by-step Workflows guide (recommends a shared/service account as flow owner), flow URL
  (optionally one per severity) stored encrypted with the other secrets (never logged or
  exported; Lead/admin only), Send Test. Adaptive Card
  messages: customer, workspace, control, severity, time, how to open in the app. On failure: retry, log, and show *Teams
  notifications failing* in Database Health.
- **Customer Quick View** (read-only, NOC / Help Desk): search by caller name, email or domain → workspace; caller lookup shows the
  caller's user group, experience profile and the CA / Intune policies that apply (read-only application permissions
  `User.Read.All` and `GroupMember.Read.All`, added in the Quick View release; looked up on demand, nothing stored beyond what is shown,
  every lookup logged; the app lists workspaces still needing consent, generates the consent link or email, and disables caller lookup
  with a clear message until granted); 30-day timeline of app changes, approvals, drift and failed assessments;
  contacts and approver.
- **ConnectWise Manage (future, 4.0)**: company → Customer Profile; ticket contact, domain or configuration item → workspace. Ticket
  shows 30 days of changes and approvals, open drift, caller group and experience, matching KB articles. **No AI.** Later: drift
  creates or updates a ticket.

## 9. Security and roles

### Staff identity
- **Entra ID sign-in at launch**: single-tenant app registration in the GDS tenant, MSAL with WAM (Windows single sign-on).
- Roles (Sales, Engineer, Technician/Deployer, NOC, Lead/Architect, Manager) are assigned **per user** by a Lead in *Users & Roles*:
  the person's Windows account (`GDS\user`, used for SQL database roles) and UPN (used for app sign-in). No AD or Entra groups are
  used (about 15 users). Groups can be added later without other changes if the user base grows.
  *Settings → Users & Roles* shows users, roles, last sign-in and active/disabled; every change is audited.
- **Step-up MFA** before live tenant changes, Library approvals, rate-card access and user/role management.
  The shared Advanced Mode passcode is retired. Implemented with a Conditional Access **authentication context** (e.g. `c1` "GDS
  Secure Plus step-up") in the GDS tenant: a CA policy targeting that context requires MFA (phishing-resistant authentication
  strength recommended) **and sign-in frequency = Every time**. The app requests a fresh token with that context (claims challenge,
  `acrs=c1`) for each step-up action and verifies the `acrs` claim in the token before proceeding, so an earlier MFA sign-in never
  satisfies it. A general "require MFA" policy covers normal app sign-in. Requires Entra ID P1 (GDS tenant).
- **Windows/AD fallback** (no Entra token): the user is identified automatically (no passwords, no free-text user selection) and is
  limited to view, assess, report, Quick View, drafting change requests and building specifications.
- **Break-glass app admin**: a dedicated Entra account in the GDS tenant, excluded from Conditional Access, protected with a FIDO2
  security key, sign-in alerts to the NOC Teams channel. It may sign in to the app; **live deployment is always blocked** for it.
- **SQL access**: Windows Authentication (§10.1) with per-user Windows logins (`GDS\user`) mapped to database
  roles by Users & Roles; no AD groups (creating logins needs `securityadmin` on the SQL Server).
- Every audit entry records the signed-in user (UPN).

| Role | Home | Can |
|---|---|---|
| Sales | Discover & Specify | Customers, discovery, Experience Map (presentation view), SOW, rates |
| Engineer | Assess / Plan | Assess, specify, plan, draft CRs, propose library items, caller lookup; hours only |
| Technician / Deployer | Deploy | Submit and apply CRs, pilot and rings, caller lookup; hours only |
| NOC / Help Desk | Operate | Quick View and caller lookup (read-only), drift acknowledge, Fleet View |
| Lead / Architect | Library | Approve library items and KB, own standards, rates, Teams settings, demo override |
| Manager / Executive | Reporting | Read-only Fleet View, Heat Map, Reporting, rates |

Menus a role cannot use are hidden. Per-workspace access lists come later.

**Status changes and approvals**

| Action | Who |
|---|---|
| Create workspace / engagement (Prospect) | Sales, Engineer, Lead |
| Engagement Prospect → Active | Sales or Lead, only once a signed SOW approval is recorded |
| Engagement → On Hold / back to Active | Sales, Engineer (owner), Lead |
| Engagement → Lost | Sales, Lead |
| Engagement → Completed | Engineer (owner), Lead, after final re-assessment |
| Workspace → Managed | Lead, Manager (managed service agreement recorded) |
| Workspace → Archived (read-only) / un-archive | Lead, Manager (step-up MFA) |
| Record SOW / change-order approval | Sales, Lead |
| Record Project Plan approval (GDS, then customer) | GDS approval: Lead; customer reply: Engineer, Sales, Lead |
| Record Change Request approval | Engineer, Technician, Lead |
| Record ring sign-off | Technician, Engineer, Lead |

Every status change and recorded approval is audited (who, when, previous value, evidence attached). The person recording an
approval cannot be the customer approver.

### Rate card
- Settings: roles, hourly rates, packages; effective-dated versions; a signed SOW keeps the rates in force when it was signed.
- Visible to Leads, Sales and Managers only (step-up MFA). Never exported, logged, seeded or committed.
- Customer SOWs show prices. SOW option **Show hourly rates** is off by default (totals per phase or package); turned on per SOW for
  time-and-materials, by Lead, Sales or Manager only. The internal *hours by role* export has no rates.

### Other
- **No AI services**: the app calls no AI model or service. AI guidance is a future item needing separate approval.
- Secrets are never exported (unchanged).


## 10. Hosting, installer and scheduler

### Database
- Desktop app + **one shared SQL Server** (standard). The connection is configurable and clearly labelled (banner for non-production)
  so a local SQL Server Express demo can be added later.
- **SQLite is removed from setup and production.** SQLite in-memory is used only for fast logic tests; migrations, constraints, bulk
  inserts, Database Health queries, roles/security and seed import are tested on real SQL Server in CI (container). CI must pass
  before any release.
- One-time **Move to SQL Server** tool: back up the SQLite file, migrate into the right workspaces and engagements, compare per-table
  counts, log, and leave the SQLite file untouched until the user confirms.

### 10.1 Data access and tenant credentials (decided)

**Decision: direct SQL with Windows Authentication on the on-prem SQL Server 2022; no API service, no server worker, no Azure
compute.** Laptops are domain-joined, hybrid-joined and Intune-managed, users are synced from AD, and remote staff use VPN, so
Kerberos works in the office and over VPN. This supersedes the earlier choice of an API service (option C) and of server-side tenant
calls; those remain documented below as a future option.

- **Tenant calls run on the workstation** (as in 3.1): assessments, deployments and caller lookups use the tenant's app credential,
  stored **encrypted in SQL with the shared app key** (the 3.1 model, AES-GCM).
- **Mitigations**
  - Database roles assigned per user (`GDS\user`); only the Engineer and Technician/Deployer roles may read the credential table (`DENY SELECT` for
    Sales, NOC, Manager); the app key is enrolled only on machines of those roles.
  - Every credential use (tenant, user UPN, action) is written to the ledger audit table.
  - Live changes still need the Deployer role, engagement Active, and step-up MFA (authentication context).
  - Audit log, approvals and deployment history are append-only ledger tables; rate tables `DENY SELECT` for Engineer,
    Technician/Deployer, NOC.
  - **Remediate from a project task (4.0.0-beta.1)**: only with an Approved/Scheduled linked change request, scoped to the task's controls;
    a confirmation shows tenant, exact changes with their *before* state, method and rollback note, and needs the passcode (step-up MFA from
    4.0.0-beta.3). Conditional Access is created report-only and Intune policies unassigned, so the task becomes *Staged*; every attempt goes
    to task history (append-only) and the audit ledger.
  - **Two registrations per tenant (4.0.0-beta.2)**: *GDS Secure Plus Read* (read-only Graph, Exchange.ManageAsApp, Global Reader) for scans,
    and *GDS Secure Plus Deploy* (write permissions only) that exists only during a deploy phase (*Enable / Remove Deployment Access* on
    Customer Profiles). Change Execution writes only through the Deploy registration. See `docs/configuration/graph-permissions.md`.
  - **Certificate credentials (4.0.0-beta.2)**: self-signed RSA 2048, one year; public key in Entra, private key only in the encrypted secret
    store (never exported or written to disk); expiry on Customer Profiles and Credential Management. Older client secrets are removed on update.
  - Accepted risk (recorded in DECISIONS): a user with the app key and SQL rights to the credential table can use GDS's app in
    customer tenants outside the app; roles in SQL can be read directly with a SQL tool within what each role is granted.
- **Recurring assessments and drift** run on one **designated scheduling PC** (e.g. the always-on NOC desktop), marked in Settings.
  Other desktops never run schedules (SQL single-runner lock as a backstop). The PC writes a heartbeat; Database Health and a Teams
  alert warn if it stops. Drift detection only happens while that PC is on.
- **Teams webhook URL** is stored with the other secrets (encrypted in SQL), visible to Lead/admin only.
- **Planned later**: move the scheduler to Azure and SQL to Azure SQL once testing is complete. **Future option**: an API service and server worker on a GDS server or Azure App Service, with credentials in Key
  Vault and role enforcement per call; and Azure SQL with Entra auth. The data layer stays behind interfaces so this remains possible.

### Installer: install type chosen at start
| | Server | Workstation | All-in-one (lab/test) |
|---|---|---|---|
| SQL | Connect and test; create database if missing → migrations → seed (idempotent, versioned) with progress, summary, log; create database roles mapped to role groups; optional VIEW SERVER STATE grant | Server address entered manually during setup (pre-filled when Intune/GPO or the `/server=` switch supplies one, and editable) and tested before continuing; changeable later in Settings → Database Configuration; never creates the database, runs migrations or imports seed | Both |
| Scheduler | — (no service) | The designated scheduling PC is marked in Settings | Marked in Settings |
| PowerShell modules (pinned: Exchange Online, Teams, PnP) | — | Installed for Technician/Deployer machines (tenant calls run on the workstation) | Installed |
| Desktop UI | Optional | Yes | Yes |

- Silent switches, e.g. `SecurePlusSetup.exe /type=Server /sqlserver=... /quiet`, `SecurePlusSetup.exe /type=Workstation /server=... /quiet`.
- Upgrade order: server first, then workstations. On launch the app compares its version with the schema version: *Update required*
  (blocked) if older, a warning if newer.
- Install type shown in *Settings → About*; changeable through Modify. Uninstalling a workstation never touches the database.
- `docs/SERVER_INSTALL.md`: prerequisites, ports, service account, certificates, upgrade order.

### Scheduler
- Recurring assessments and drift checks run on the designated scheduling PC (see §10.1); heartbeat and single-runner lock in SQL.
- `docs/SERVER_INSTALL.md`: SQL Server prerequisites, database roles and per-user logins, backups, the scheduling PC, upgrade order.

## 11. Navigation (lifecycle menus, all collapsed at launch)

| Menu | Pages |
|---|---|
| Home | Role-based home page |
| Customers | Customer Profiles, Workspaces, Engagements, Tenant App Registration |
| Discover & Specify | Discovery (EaaS import), User Groups & Experience Profiles, Experience Configuration, Specification (versions) |
| Assess | Assessment Engine, Live Assessment, Assessments, Recurring Schedules |
| Plan | Roadmap, SOW, Project Plan, Pending Approvals |
| Deploy | Pilot & Validation, Change Requests, Change Execution, Tracker |
| Operate | Quick View, Drift, Fleet View, Exceptions |
| Reporting | Completion views, Specification Comparison, Experience Map (presentation mode), Heat Map |
| Library | Reference Catalogue (Catalog), Global Library, Approval Queue, Knowledge Base |
| Settings | Application, Database Configuration and Health, Rate Card, Teams Notifications, Credentials, Auditing and Logs, Appearance, Advanced (role) |

## 12. Release plan (approved 2026-11-08; each release usable on its own)

3.2 continues as **4.0** (the number reflects a fresh database: 4.0 starts from an empty SQL database, no 3.x data migration).

| Release | Contents |
|---|---|
| 3.2.0-beta.1 | Shared on-prem SQL Server, Server/Workstation installer, per-user database roles, ledger, scheduling PC, SQLite move tool **(shipped)** |
| **4.0.0-beta.1** | Dashboard hover fix; Integration/Workspace Settings fix; top bar icon pickers; change request templates (per customer + global); Project Plan task detail, pop-out, inline dates, Up/Down, Insert, completion, Staged state, Remediate from task (passcode), attested manual steps; experience profile renames and settings; classifications, Global Secure Access area, framework updates; Industry list (replaces NAICS in the UI). Schema V0009 |
| **4.0.0-beta.2** *(shipped 2026-11-09, schema V0012)* | (1) Tenant onboarding: *Connect tenant* on New Customer, **quick scan** (users, groups, membership, roles, licences, domains, posture) and **in-depth scan** (adds Intune devices, policy/app assignments, sign-in activity, MFA methods, licences per user); Live Assessment runs quick scans; Assessment Engine renamed **Assessment Scan** (quick or in-depth, schedules too); Entra group dropdowns for user groups and deployment variables. (2) Catalogue foundations (reload safety, control fields, Documents source review and rebuild) and the policy / policy-set editor. (3) Coverage of every Microsoft product area in `docs/catalog/COVERAGE.md` (Azure as Manual checks until 5.0) with read-only PowerShell assessment (Exchange, Security & Compliance, Teams): 195 tenant settings, 131 automatic. (4) Read and Deploy app registrations with certificate credentials |
| 4.0.0-beta.3 | Entra sign-in, roles in the app, step-up MFA replacing the passcode, GDAP status, TPM key protection and rotation, scheduler service account |
| 4.0.0-beta.4 | Workspaces as tenants, engagements, gating, migration, parent/child customers, inheritance (incl. Industry), lifecycle menus and role homes |
| 4.0.0 | Final release |
| 4.1 | Drift, Customer Quick View, caller lookup |
| 4.2 | Specification versions; Global Library with *Update available* (incl. CR templates); KB editor; specification builder with tiers, overlays and industry suggestions (tier templates hold control applicability and overrides); Heat Map and Reporting industry filters; **experience mapping** (answer → control mapping, question scope, profile corrections, new questions, personas – proposal first) |
| 4.3 | Rate card, priced SOW, approvals |
| 4.4 | Pilot & Validation, ring assignment (completes Staged tasks), CA enablement, one-click rollback of Graph changes *(proposed: Graph deployment of Entra tenant settings; P2 M365 areas)* |
| 4.5 | PowerShell deployment (Exchange, Security & Compliance, Teams), GPO export |
| 4.6 | Move to Azure |
| 5.0 | Azure module: ARM read via Azure Lighthouse (Reader + Security Reader, JIT write), MCSB / Azure Policy + CIS Azure |
| 5.x | ConnectWise (data only) |
| Not scheduled | AI (needs separate approval; the app uses no AI services) |

Catalogue coverage and import phases: `docs/catalog/COVERAGE.md`, `docs/catalog/IMPORT_PLAN.md` (proposal, 8 Nov 2026). Reload safety for Lead-edited controls ships with the policy and policy-set editor.

Working rules: feature branches; no change to the shared production database or the current release without approval of each phase.

### 4.0.0-beta.2 test checklist
See `docs/testing/4.0.0-beta.2-checklist.md`.

### 4.0.0-beta.1 test checklist
See `docs/testing/4.0.0-beta.1-checklist.md`.

### 3.2.0-beta.1 test checklist (shared SQL and installer)
Each beta has its own checklist in `docs/testing/`; beta.1 must pass all of these before beta.2 starts:
1. Server install on a clean Windows Server: SQL connection test, database created, migrations and seed (idempotent re-run), log and summary.
2. Workstation install: manual server address, Intune/`/server=` pre-fill, connection test; never creates or migrates the database; PowerShell modules optional.
3. Version check: older app blocked (*Update required*), newer app warned.
4. Database roles assigned per user (`GDS\user`); ledger tables reject UPDATE/DELETE; `DENY SELECT` on rates works for the denied roles.
5. Credential table readable only by Engineer and Technician/Deployer roles; app key enrolled only on their machines; every credential use in the ledger audit.
6. Designated scheduling PC: only it runs schedules; heartbeat shown in Database Health; Teams alert when it stops.
7. SQL single-runner lock: two desktops cannot run the same schedule.
8. Failure cases: scheduling PC off, SQL unreachable, VPN dropped.
9. SQLite *Move to SQL Server* tool: counts compared, SQLite file untouched.
10. 3.1 features regression: full automated suite on SQL Server in CI, plus a manual smoke test of each menu.

## 13. Glossary

| Term | Meaning |
|---|---|
| Customer Profile | Who the customer is |
| Workspace | One customer tenant, permanently, with its specification and history |
| Engagement | One contracted piece of work in a workspace (SOW, plan, change requests). Used everywhere (UI, SOW, emails, docs, exports, code); customer documents may say "Engagement (project)" on first use |
| Reference Catalogue | Shipped, read-only reference data |
| Staged | A project task whose change was created report-only / unassigned; completed once the linked controls pass |
| Manual – attested | A manual step recorded with a required note (and optional attachment); never counted as verified |
| Industry | Customer industry from the Lead-editable list (replaces NAICS in the UI) |
| Global Library | GDS-built reusable items, versioned, approved by Leads |
| User Group | A category of the customer's people (e.g. Executives, Frontline, Contractors), mapped to an Experience Profile |
| Experience Profile | A defined set of choices for what a kind of user sees and can do (sign-in, devices, apps, data access) |
| Baseline Configuration | A reusable bundle of controls and policies (GDS standard or tailored) |
| Specification | The customer's agreed target state; versioned, locked when signed |
| Specification Template | A Global Library starting point for new specifications |
| Assessment | A point-in-time measurement of a tenant against a specification version |
| Gap | Specification minus assessment |
| Change Request | One approved unit of work closing part of the gap |
| Ring | A group of users receiving a change at the same time (pilot, then wider) |
| Report-only | CA state that evaluates but does not enforce |
| Deployment Variable | Customer value filled into policy templates (ring, groups, IDs, name prefix) |
| Exception | An accepted risk: a control deliberately not met |
| Drift | A tenant moving away from its specification after deployment |
| Change Window | Agreed time for applying changes |
