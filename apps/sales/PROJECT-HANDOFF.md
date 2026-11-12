# Archi-Tech 3.0: current-state and merge handoff

**Snapshot: November 9, 2026.** This is a handoff for a new conversation and a
potential merge with a coworker's similar application. It describes the local
implementation, not a finalized combined-product specification. The project is
being rescoped; do not assume earlier priorities, integration choices, or
permission to make tenant changes carry over without review.

The existing [README](README.md) contains operational setup and feature details.
[External service readiness](docs/external-service-readiness.md) describes
integration safeguards; [standards catalog](docs/standards-catalog.md) describes
source provenance and imported reference data.

## Important handoff warnings

- **The implementation is local and uncommitted.** At this snapshot, `git status`
  showed the application directories, documentation, solution, and `.gitignore`
  as untracked. The repository's last commit is `b8ccd92` (`Initial commit`).
  A clone, a different worktree, GitHub, or a new chat with a different workspace
  may therefore not contain the work. Preserve and review the current working tree
  before any merge, cleanup, branch switch, or other Git operation. Do not commit
  or push without the owner's authorization.
- **Do not copy secrets or customer data into the handoff or a shared repository.**
  Connection settings are in user secrets outside the repository. Local databases,
  backups, certificates, customer data, trigger URLs, session logs, and original
  source documents require separate access and handling decisions.
- **Tenant writes are disabled.** Intake does not assess or alter a customer
  tenant, and the deployment builder does not execute changes. A design approval
  is not permission to execute a script. Preserve this distinction in any merge.
- **Planner work is paused.** The app's current Planner handoff is not configured;
  the separate synthetic test flow was last verified Off. Do not enable the flow,
  send a request, or create/retry a plan without new authorization. The exposed
  test trigger must be rotated before broader use.
- **Worker selection is on hold.** The owner is rescoping the project and has not
  selected the actual isolated execution environment. Do not provision one or
  enable tenant writes as part of the merge without explicit decisions and
  authorization.
- **SQL transport is not production-ready.** The existing Development connection
  was explicitly approved as unencrypted for migration/rehearsal. The owner
  reports a SQL certificate is now live, but the app's encrypted connection,
  trusted certificate chain, and matching server name have not been verified.
  Another engineer owns the encryption work.

## Product intent and current shape

The original first release was an internal sales tool: capture a shorter discovery
intake, create a customer profile and engagement, calculate applicable Secure
Plus work and effort from a WBS, and eventually hand the work to a separate
Planner plan per engagement through Power Automate. Assessment, deployment,
and monitoring were later phases. The app has since grown into a local prototype
covering parts of those later phases, but is not a complete automated assessment
or deployment platform.

The intended end-to-end journey is:

1. Sales records customer, meeting, organization, and discovery details.
2. The app persists the engagement and calculates a snapshot of applicable WBS
   tasks and effort, with monthly operations separate from one-time work.
3. Staff review the estimate, package discussion signals, and customer roadmap.
4. An approved integration may eventually create a distinct Planner plan for the
   engagement. This is currently paused and not ready for customer data.
5. Deployment staff can maintain a customer profile, record discovery answers and
   manual assessment findings, and prepare a reviewable design plan.
6. Only a future, independently approved execution workflow could make tenant
   changes. It does not exist today.

The owner asked to use the Secure Plus bundle workbook and revised discovery
questionnaire as references **without including pricing**. Their contents are
inputs to human review, not authority to change tenant settings or claim that a
customer is entitled to a product.

## Implementation and architecture

| Component | Current implementation |
| --- | --- |
| UI | React 19, TypeScript, Vite; main application in `web\src\App.tsx`. |
| API | ASP.NET Core/.NET 10; endpoint groups under `server\ArchiTech.Api\Endpoints`. |
| Persistence | EF Core 10, SQLite local fallback and SQL Server provider with migrations; normalized operational tables. |
| Reports | ClosedXML Excel workbooks, JSON exports, and internal HTML review/assessment reports. |
| Read-only evidence | Separate .NET worker for selected Microsoft Graph reads; launched from the local Development dashboard or command line. It is not a tenant-write sandbox. |
| Data migration | Separate `ArchiTech.DataTransfer` utility for verified SQLite-to-SQL Server cutover. |
| Automated checks | .NET test project covering estimation, imports, deployment workflows, Graph readers, data transfer, and Planner safeguards. |

Important paths:

| Path | Purpose |
| --- | --- |
| `server\ArchiTech.Api\Program.cs` | Startup, persistence and integration configuration, HTTP client setup. |
| `server\ArchiTech.Api\Domain\` | Intake answer catalog, WBS and assessment logic, package references, entities, and inert plan previews. |
| `server\ArchiTech.Api\Endpoints\SalesEndpoints.cs` | Customers, engagements, estimates, roadmap/export, org-chart preview, journey, and Planner handoff. |
| `server\ArchiTech.Api\Endpoints\AssessmentEndpoints.cs` | Manual assessments, evidence, scoring, snapshots, and reports. |
| `server\ArchiTech.Api\Endpoints\DeploymentChangePlanEndpoints.cs` | Design plan creation, submission, review, events, and inert previews. |
| `server\ArchiTech.Api\Endpoints\StandardsEndpoints.cs` | Standards catalogs, discovery responses, and review-only crosswalk. |
| `server\ArchiTech.Api\Integrations\` | Org-chart import, workbook export, Graph evidence, and Planner integration. |
| `server\ArchiTech.Api\Data\` | EF configuration, SQL Server migrations, and versioned seed catalogs. |
| `server\ArchiTech.ReadOnlyWorker\` | Fixed read-only Graph evidence operations and offline preview. |
| `server\ArchiTech.DataTransfer\` | Guarded, reconciled database transfer utility. |
| `server\ArchiTech.Tests\` | Backend automated tests. |
| `web\src\App.tsx`, `api.ts`, `types.ts` | UI, API client, and frontend models. |

`App.tsx` contains a large portion of the interface. A merge should consider
component boundaries and contracts, not simply replace it or copy another app
over it. The imported ComplianceIQ dictionary is a reference model, **not** the
deployed EF schema.

## Feature inventory: what works and what it means

### Sales intake and estimates

- Customer records are matched by normalized company name; each intake creates
  a separate engagement and persisted estimate. Engagements contain normalized
  answers and scoped WBS line items.
- The shorter intake covers customer and meeting information, identity,
  infrastructure, email, endpoint management, collaboration, and related needs.
  Organization fields include industry, locations, mobile-device counts,
  departments, IT and workplace operating models, infrastructure, licenses,
  backup, password management, and voice needs.
- The separate sales estimate catalog contains **95 WBS tasks**. The app applies
  answer-dependent rules and separates one-time and monthly effort. User, device,
  and similar counts are references; they do not scale WBS hours. The newer
  organization fields likewise do not alter the estimate.
- Regression checkpoints documented in the README are 19 applicable tasks,
  104-198 one-time hours, and 28-60 monthly hours for no optional answers; and
  95 tasks, 1,120-2,230 one-time hours, and 36-76 monthly hours with all
  scenarios enabled. These are estimate rules, not delivery commitments.
- The app does not include pricing.

### Organization charts, packages, and customer journey

- CSV and XLSX department lists can be previewed from a single `Department`
  column and require review before replacing entered departments. The import
  bounds file and expanded sizes, rows, columns, and department values; XLSX
  requires one visible sheet and rejects formulas in the department column.
  The original file is not retained.
- PDF and PNG/JPEG/WebP charts can be viewed locally for **manual transcription**.
  There is no OCR or automatic extraction for these formats and the files are
  not uploaded or stored.
- A curated, non-price package comparison lists service components and
  questionnaire-based discussion signals. It is not a recommendation,
  entitlement, licensing determination, or verified coverage assessment. It does
  not ingest every workbook row or separately present all add-ons.
- The supplied source has a discrepancy: the Complete sheet lists Microsoft 365
  E7 and Premium lists E5. Confirm the intended source values with the business
  before making product or licensing claims.
- Customer journey history compares saved engagement-answer and estimate
  snapshots for the same customer. It is not an audit of every profile edit,
  tenant change, or customer interaction.

### Exports and experience map

- An engagement roadmap Excel workbook contains the scoped WBS, phase narratives,
  stakeholder prompts, estimate ranges, answer-derived hotspots, an Experience
  Map, and package comparison. Prompts are not assigned owners or verified
  impacts; hotspots need customer review.
- The Experience Map uses all **371 assessment catalog control IDs and names**
  as reviewable rows and the intake's departments as columns. The owner explicitly
  approved exposing those IDs and names to Sales in this workbook. Detailed
  deployment catalog content and assessment findings remain role-restricted.
- Applicability begins as `Review` and project impact as `Unknown`. Reviewers can
  choose High/red, Medium/yellow, Low/green, or Unknown/gray in Excel. The colors
  indicate **project impact**, not security-control status. No industry-to-control
  applicability mapping was supplied, so none is asserted.
- Excel edits do **not** sync back into the application; there is no persistent
  in-app impact matrix.
- Engagement and workspace JSON exports exist. Deployment-role workspace export
  includes deployment information; Sales-only exports omit deployment data on
  the server. JSON export is not a supported SQL import format.

### Deployment, discovery, and manual assessment

- Deployment staff can create a customer profile independently or link an
  engagement, record public tenant/client metadata, browse the six-phase
  371-control catalog, and maintain findings. Customers can have assessment
  history, manually recorded evidence and exceptions, weighted scoring, gap
  roadmaps, reports, and comparisons between completed compatible snapshots.
- A new assessment may copy existing profile findings or start every active
  control as `NotAssessed`. A fresh assessment is not an automated live assessment.
  The fleet view summarizes recorded completed assessments, not independently
  verified tenant posture.
- A separate **474-question** WIP discovery catalog supports independent
  engagement-level and customer-profile response logs. They do not automatically
  copy or merge. Discovery questions are not interchangeable with assessment
  controls.
- Additional WIP reference data includes **193 work items**, **532 proposed
  baseline settings**, and a **149-table / 1,206-column** data-dictionary
  reference. These are separately versioned; they do not change the existing
  95-task estimate catalog or 371-control assessment catalog automatically.
- The discovery-to-assessment crosswalk offers exact/format candidate links for
  human review and displays unresolved references. It does not establish
  equivalence, applicability, evidence, or compliance. Source provenance,
  WIP status, commercial rights, and external framework licensing still require
  review before using the data as an approved customer standard.
- Plan builders can assemble review packages from recorded assessment gaps.
  The requester submits a design for a different approver; events record plan
  creation, submission, and review. HTML and comment-only text downloads are
  non-executable. This is **design review only**, not a tenant-bound,
  time-limited authorization to execute a specific template. Event history is
  not a comprehensive execution/security audit.

### Read-only Graph evidence

- A local worker uses operator-initiated interactive delegated sign-in and checks
  the returned tenant and scope. The local Development UI can launch fixed reads
  of Conditional Access policies and optionally security defaults, named-location
  counts, and authentication-methods migration state. The token stays in worker
  memory; evidence is temporary and does not automatically update findings.
- A lab read returned 56 Conditional Access policies, all report-only; security
  defaults disabled; and two named locations. These are historical observations,
  not current tenant posture or evidence of effective compliance.
- The selected authentication-methods read failed because the lab response did
  not contain the expected migration state. The cause was not established.
  Selected read failures discard the combined evidence; they must not be treated
  as compliant or as a known state.
- An offline Graph collection JSON preview and copyable partial evidence for
  `AUTH-004` and `AUTH-002` assist manual review. Counts alone do not prove
  effective MFA coverage or legacy-auth blocking. Offline input does not prove
  tenant identity, freshness, or completeness.
- The app shows candidate evidence mappings for 24 controls; the remaining
  controls are not all mapped or automated. The user's desired direction was to
  represent all 371 controls, use individually validated automated reads where
  possible, and leave the rest explicitly Not Assessed/manual.
- The owner reported that 19 existing delegated lab grants were approved for
  testing and use, with least-privilege review before script execution. This is
  not independent verification of consent, permission for new scopes, or
  authorization for tenant writes. The operator must cancel any consent prompt;
  do not request new consent on the strength of this handoff.
- The read-only worker is a separate process running under the invoking user,
  not an isolated execution boundary, job queue, or script runner.

### Planner and Power Automate

- The API can send a schema-v2 payload with a stable engagement idempotency key,
  customer/engagement details, WBS tasks, and phase grouping. Stakeholder labels
  are not person assignments. The flow and any required organization resources
  are external and are not included in this repository.
- A separate synthetic test flow created a real plan, bucket, and task; the owner
  independently viewed it. A later authorized 12-task test created the plan and
  all 12 tasks, and its SharePoint reservation was marked Completed, but the app's
  20-second HTTP timeout occurred before the flow's response. The flow response
  then failed because the client had disconnected. Thus a local failure does
  **not** prove that no remote plan exists.
- A unique SharePoint operation key prevented duplicate starts in a test against
  an already reserved key, but it does not prove complete idempotent recovery,
  new-key race handling, or safe recovery from partial creation.
- The app atomically claims a first send, treats missing plan IDs as uncertain,
  and blocks blind resend of `Pending` or `Failed` requests. An operator/admin
  can record a manual confirmation for a failed handoff after independently
  reconciling the terminal run, plan, and tasks; it is an attestation, not
  automatic verification. Pending requests remain blocked.
- A signed trigger was approved for synthetic data only and was exposed in
  session diagnostics. Rotate it before broader use and do not copy it into
  code, logs, a document, or a new chat. A certificate-based Entra client is
  implemented but has not been tested end-to-end with a live token and
  authenticated trigger.
- Asynchronous completion, authenticated status or callback, durable
  operation/payload matching, partial-failure recovery, concurrency testing, and
  full-size WBS validation are still required before customer use. Increasing
  the timeout alone is not sufficient. Work is paused pending the rescope.

## Identity, authorization, and safety boundaries

- Local Development uses a simulated identity and can be configured with test
  roles. It is not suitable for network/shared hosting and does not demonstrate
  genuinely independent review.
- Production-oriented sign-in uses Entra OpenID Connect and maps configured
  security-group claims to Sales, Deployment, Approver, Operator, and
  Administrator roles. APIs enforce role policies; hiding UI is not the boundary.
  Configuration, group claims, group overage, and denial cases need real
  environment validation.
- The app's workforce sign-in registration and each customer-tenant Graph
  registration are separate concerns. Registration checklist progress records
  public metadata and manual status; it does not create registrations or verify
  grants.
- Current deployment roles can access deployment profiles across customers.
  Customer/assignment-level authorization has not been implemented. Determine
  the intended access model before sharing or hosting the system.
- No tenant-write template is approved in this app. A future executor must have
  customer-specific authorization, an actual verified isolation boundary,
  immutable individually reviewed templates, exact tenant/template/parameter
  binding, independent execution-specific approval, least privilege, explicit
  operator launch, revalidation, auditing, and failure/rollback procedures.
  Neither the current design-plan approval nor the legacy in-process PowerShell
  executor satisfies these requirements.

## Data stores and local operation

The current Development app was rebuilt and restarted with the approved changes
and was responsive at `http://127.0.0.1:5080/`. Its health endpoint reported
`SqlServer` and `plannerHandoff: not-configured`. This is a point-in-time
observation, not a guarantee the process is still running.

The current SQL Server target is the dedicated `ArchiTech3` database on
`GDS-CLSV-SQL.gds.pri`. A verified SQLite backup and a rehearsed, reconciled
transfer preceded the approved cutover. The prior SQLite data should not be
resumed as a parallel writer: it would diverge. Confirm current SQL backup,
restore, ownership, retention, and transport-security arrangements before a
shared or production deployment.

The API defaults to SQLite if a provider is not configured. This Windows user's
Development user secrets override it to SQL Server. User secrets are scoped by
project ID and Windows user, so another checkout can inherit the same connection;
conversely, a new machine or account can start against a different SQLite store.
**Inspect effective configuration without printing secrets before startup.**
SQL Server startup applies migrations and seeds catalogs, and therefore writes
to the configured database. SQLite uses `EnsureCreated` and additive startup
schema handling rather than the SQL Server migration path.

Local startup, from the repository root, after confirming the intended provider:

```powershell
dotnet run --project .\server\ArchiTech.Api\ArchiTech.Api.csproj
```

The built UI is served by the API at port 5080. To develop the frontend
separately, run `npm run dev` in `web`; Vite proxies `/api` to the local API.
See the README for prerequisites and first-time dependency setup. A production
frontend build writes to the ignored `server\ArchiTech.Api\wwwroot` directory,
which can replace the assets served by a running app; coordinate with active
users and preserve unsaved edits.

The transfer utility applies schema migrations before its copy transaction,
checks backup integrity, foreign keys, table/column sets and destination
emptiness, and reconciles copied rows by hash. A failed copy can leave applied
migrations even if row inserts roll back. Future transfer or merge migrations
require an explicitly approved destination, consistent backups, rehearsals,
and validation; do not treat JSON exports as imports.

## Source material and external dependencies

The following user-supplied files were reference inputs in this local session;
their local attachment paths may not exist in a new chat or on a coworker's
machine. Obtain appropriate authorization before sharing them.

- `Secure Plus Discovery - Build Spec.md`, `secure-plus-wbs-tasks.csv`, and
  `secure-plus-discovery-app-source.zip`: original MVP references. The prototype
  was not authoritative where it conflicted with normalized persistence, secret
  handling, or safe integration.
- `Secure Plus Bundles.xlsx` and
  `GDS_Secure_Plus_Discovery_Questionnaire_Revised.docx`: non-price package and
  intake references.
- `architech-deployment-handoff.md`: earlier deployment capabilities and
  safeguards, used as context for the 3.0 deployment workspace.
- The internal Cloud Services Standards workbook and data dictionary supplied
  versioned WIP imports; see `docs\standards-catalog.md` for precise source
  names, import boundaries, and source-rights requirements.
- A local Archi-Tech 2.0 checkout was used for comparison. Its in-process script
  executor is not an isolation boundary, and some assessment readers make unsafe
  conclusions or contain incorrect mappings. Do not copy or run them unchanged.

External resources are not recreated by a source checkout: SQL Server and its
database, workforce and customer Entra registrations and approvals, Power
Automate flow, Planner group and plans, SharePoint reservation list, host user
secrets, and a synthetic certificate in the original user's certificate store.
The synthetic certificate is not a production credential. Never assume
credentials, access, consent, data ownership, or resource state transfer to a
new environment.

## Latest verification and known gaps

- Prior to the approved restart, the backend Release suite passed **137 tests**
  and the frontend production build passed. A separate SQLite-backed synthetic
  browser session exercised intake, departments, package comparison, journey,
  CSV preview, and workbook download.
- After restart, the Debug API build and frontend build succeeded. The local
  health endpoint reported SQL Server and Planner unconfigured, and the browser
  showed the new organization intake. These checks do not prove production
  readiness, SQL TLS, live Planner recovery, or full assessment coverage.
- No commit or push was made. This document and the rest of the implementation
  must be deliberately preserved before moving the work elsewhere.

## Open work and merge decisions

These are candidates for the rescope, not authorization to proceed:

1. **Compare the two applications:** obtain the coworker's architecture,
   repository state, feature inventory, deployment target, data model, identity
   model, and licensing/source rights. Decide which app owns each workflow and
   which contracts and UI are retained; do not assume either model wins wholesale.
2. **Protect and reconcile data:** identify the authoritative customer,
   engagement, assessment, discovery, and plan records; agree on identifiers,
   versions, provenance, migration/rollback, duplicate handling, and cutover.
   Preserve existing SQL and local backups. Rehearse on isolated data.
3. **Set security and hosting requirements:** decide customer-level authorization,
   real Entra role/group handling, secrets storage, TLS and trusted SQL identity,
   logging, data retention, backups, and operational ownership. Local Development
   authentication and the previous unencrypted SQL setting are not a hosted
   security model.
4. **Choose assessment scope:** review each automated evidence mapping,
   permissions, customer authorization, data sensitivity, and failure behavior.
   Expand only validated read-only operations; preserve `NotAssessed` when
   evidence is absent or ambiguous. Resolve the authentication-methods response
   issue without inferring an answer.
5. **Decide reporting and experience-map scope:** determine whether PDF/image
   extraction, persistent impact ratings, workbook round-tripping, richer
   customer/executive reports, and complete journey/audit history are required.
   Review package source discrepancies and keep pricing excluded unless the
   owner changes that requirement.
6. **Revisit Planner only when authorized:** rotate the exposed synthetic trigger,
   choose ownership and authentication, implement reliable asynchronous
   completion and reconciliation, and test partial failures, concurrency, and
   realistic payload sizes with synthetic data before considering customer data.
7. **Choose the worker environment later:** separately review container/job,
   isolated guest/container on an approved host, or dedicated VM. Establish
   customer authorization, isolation, approved templates, execution-specific
   approval, operator launch, and auditable outcomes before any tenant write.
8. **Complete production engineering:** review accessibility, UI structure,
   observability without secrets, deployment/upgrade and rollback processes,
   concurrency, authorization tests, integration tests, and an end-to-end
   acceptance plan for the merged product.

Useful existing validation commands, from the repository root:

```powershell
dotnet test .\server\ArchiTech.Tests\ArchiTech.Tests.csproj -c Release --no-restore
npm run typecheck --prefix .\web
npm run build --prefix .\web
```

Build/test only after checking the target environment and active users. Release
tests avoid the running Debug API DLL lock; the frontend build updates the
normal served static output. Commands requiring missing dependencies need the
appropriate approved restore on the target machine.
