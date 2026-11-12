# Archi-Tech 3.0

Local-first sales intake and Secure Plus engagement estimating with a role-gated deployment workspace. Sales creates customer profiles and engagements from a focused discovery form; deployment staff can review the imported control catalog, maintain assessment history, score readiness, prepare design plans, and route those plans through a separate approval queue. The app can hand off an onboarding plan to Power Automate, generate a customer journey workbook, and export persisted workspace data as JSON. No customer-tenant changes are executed.

## Local setup

Requirements: .NET 10 SDK and Node.js 20.19+ or 22.12+. The default SQLite setup does not require SQL Server or LocalDB.

1. In a terminal at the repository root, start the API:

   ```powershell
   dotnet run --project .\server\ArchiTech.Api\ArchiTech.Api.csproj
   ```

   The API binds to `127.0.0.1:5080` and seeds the sales WBS plus versioned Secure Plus reference catalogs on first startup. By default it creates `server\ArchiTech.Api\App_Data\architech.db`; an explicitly configured provider can use SQL Server instead.

2. In a second terminal, start the React app:

   ```powershell
   cd .\web
   npm install
   npm run dev
   ```

   Open `http://127.0.0.1:5173`. Vite proxies `/api` requests to the local API. To serve the built UI from ASP.NET Core instead, run `npm run build` from `web`, then start the API; generated static files go to the ignored `server\ArchiTech.Api\wwwroot` directory.

SQLite is the default local fallback. Development settings can override it through .NET user secrets outside the repository. User secrets are scoped to the project's `UserSecretsId` and Windows user, so other checkouts of this project under the same account can use the same SQL connection; inspect their effective configuration before starting them. Without the Development override, the default is SQLite, which can create a separate data store. Starting against SQL Server automatically applies migrations and seeds catalogs.

The app persists customer profiles, engagements, normalized answer values, scoped WBS line items, and handoff status in related EF Core tables. User, endpoint, and file-size counts are reference values and do not scale the WBS hours. Customer records are matched by normalized company name; each intake creates a distinct engagement and estimate.

The intake records optional industry, operating model, infrastructure and department names as engagement answers. CSV and XLSX department lists can be previewed using a single `Department` column and require confirmation before they replace the entered list; the original file is not retained. PDF and image charts can be viewed locally for manual transcription, but automated extraction from these formats is not supported. The customer journey compares saved engagement answers and estimate snapshots; it is not an audit of every customer-profile edit or tenant change.

The customer roadmap workbook includes an Experience Map with all 371 assessment catalog control IDs and names as reviewable rows and the intake's departments as columns. On explicit user direction, the Sales-accessible roadmap includes these control names; deployment assessment findings and detailed catalog content remain role-restricted. The controls are not automatically deemed applicable to an industry or customer. Impact starts as **Unknown**; a reviewer chooses High (red), Medium (yellow), or Low (green) in Excel. The ratings are planning judgments, not assessment findings, and workbook edits do not sync back to the app. A package-comparison view and export sheet list non-price service components from the supplied bundle workbook alongside selected intake signals. They are discussion aids, not eligibility, coverage, or licensing determinations; the source sheets have an E5/E7 discrepancy that requires review.

## Sign-in and role access

Local `Development` runs use a development-only identity so the app remains testable without Entra credentials. By default it has all local test roles; this mode is not suitable for a shared network or hosted environment. To test the Sales-only permission boundary locally, set `Authentication__DevelopmentRoles=Sales`; set `Authentication__DevelopmentUserId` to a different value when testing the separate-requester/reviewer rule. The development identity is never available outside the ASP.NET Development environment.

Production uses Microsoft Entra ID OpenID Connect and maps configured security-group object IDs to application roles. Configure the app's single-tenant sign-in registration with redirect URI `<app-base-url>/signin-oidc`, then supply these settings from the host environment/secret store:

```text
Authentication__Mode=Entra
Authentication__TenantId=<organization tenant GUID>
Authentication__ClientId=<web sign-in app client GUID>
Authentication__ClientSecret=<secret-store value>
Authorization__Groups__Sales=<Sales security group object GUID>
Authorization__Groups__Deployment=<Deployment engineer security group object GUID>
Authorization__Groups__Approvers=<Deployment approver security group object GUID>
Authorization__Groups__Operators=<Deployment operator security group object GUID>
Authorization__Groups__Administrators=<Deployment administrator security group object GUID>
```

Configure Entra to emit the security-group object IDs in the sign-in token, or users fail closed without an application role. All customer/workspace APIs require a recognized role; `/api/deployment/*` additionally requires a deployment role, so Sales members cannot read deployment data even by calling its API directly. Group IDs and credentials are not source-code settings. The customer-tenant app registration used for future Graph assessment is a separate, per-customer workflow from this app's sign-in registration.

## Planner handoff

Power Automate is optional until a flow endpoint is available. The UI shows the unconfigured state and saves the customer and estimate normally. Configure the API host with an HTTPS request-trigger URL through its environment, never a source file:

```powershell
$env:PlannerFlow__AuthenticationMode = 'SignedUrlTest'
$env:PlannerFlow__Endpoint = 'https://<your-power-automate-request-trigger>'
dotnet run --project .\server\ArchiTech.Api\ArchiTech.Api.csproj
```

The API sends one engagement payload after saving, with a stable `Idempotency-Key` and the scoped task list. The flow must use `engagementId` as its idempotency key and create or return exactly one distinct Planner plan for that engagement; populate the plan with the supplied tasks. A successful flow must return `{"plannerPlanId":"<id>"}` after completing its work. A success response without a valid ID is treated as an uncertain failure: the plan may exist. Failed or pending handoffs cannot be blindly retried in the app; reconcile the flow and Planner first. Failed handoffs do not discard the saved estimate. Do not log or commit the trigger URL. This integration is limited to creating a Planner plan and tasks; it does not read or change customer tenant configuration.

For a failed handoff, an operator or administrator can record a manual confirmation after checking the terminal run, matching plan, and every scoped task. This records the operator's attestation and a nonsecret reference; it does not verify Planner automatically. Pending handoffs cannot be confirmed or resent because their request might still be running.

The version 2 request body includes `engagementId`, `planTitle`, customer profile/contact details, sales owner, meeting date, user/endpoint reference counts, notes, normalized discovery answers, stakeholder group labels, phase summaries, and `tasks[]` with WBS ID, phase, workstream, title, deliverable, effort, duration, and support-handoff flag. It retains the stable idempotency header and flat task list; `phases[]` provides the proposed Planner bucket names and their task WBS IDs. The flow should create one plan per engagement, create a bucket per phase, add the scoped tasks to those buckets, and route/notify the Sales, Project Management, and Engineering groups according to the organization's own configured identities. The group labels are not user IDs and do not assign people by themselves. Return HTTP 2xx only after the flow has created or found the idempotent plan.

`SignedUrlTest` works only in the Development environment and is for synthetic data. An optional `EntraCertificate` mode sends a workload identity bearer token to an explicitly allowed Power Platform environment host; see [`docs/external-service-readiness.md`](docs/external-service-readiness.md) for configuration and outstanding validation. Selecting that mode does not make the flow safe or ready for customer data.

The flow receives customer contact details and sales notes as part of the handoff; configure its endpoint only for a trusted organization-controlled flow. The current synchronous API call times out after 20 seconds, so a flow that creates many tasks sequentially can continue after the app reports an uncertain outcome. Do not use the test flow for real engagements until timing, recovery, and authentication are addressed. No Planner endpoint or flow is included in this repository.

## Customer roadmap and JSON exports

After an engagement is saved, its detail page provides a downloadable Excel workbook at `/api/engagements/{id}/roadmap.xlsx`. It includes a filterable customer journey/WBS table with the approved starter stakeholder columns (customer leadership, IT/admin, managers, employees, sales, project management, and engineering), phase narratives, effort ranges with visual data bars, and an answer-derived Hotspots sheet. Role cues are planning prompts, not assigned owners or confirmed impacts. Hotspots are based only on selected intake answers and must be validated with the customer; the workbook is not a tenant assessment or a delivery schedule.

The detail page also provides `/api/engagements/{id}/export.json`. The dashboard's **Export workspace JSON** action downloads `/api/exports/workspace.json`, a versioned bundle containing customer profiles, engagement details and answers, scoped estimate tasks and handoff status, the WBS catalog, deployment control catalog, deployment profiles and findings, assessment runs and results, score breakdowns, recommendations, and deployment plan reviews. Deployment roles receive the full bundle; a Sales-only identity receives only customer, engagement, and WBS data, with deployment data omitted server-side. These exports reflect the local persistence model and are intended to support future database/table design.

## Deployment workspace

The **Deployment** workspace is connected to customers created by Service Plus intake. Deployment Engineers and Administrators can also add a customer profile directly, with optional contact details, without creating a Sales engagement or estimate. It lets staff maintain optional tenant ID, primary domain, and Entra application client ID metadata; choose a Service Plus engagement; and review the imported 371-control, six-phase catalog. Controls can be scoped to the engagement's WBS tasks or viewed across the full catalog, and filtered by phase, category, or search text. Manual statuses and notes are stored as normalized deployment findings. The workspace shows weighted coverage and a review-only gap roadmap grouped by phase.

The deployment catalog was imported as metadata only; no executable remediation scripts were copied. The workspace now includes:

- A separate deployment-only Secure Plus discovery log imported from the 474-question workbook. Responses are saved per engagement in normalized tables; the sales intake remains intentionally shorter. Sales cannot read the discovery catalog or responses through the API.
- A versioned WIP Secure Plus reference catalog with 193 roadmap work items, 532 baseline settings, effort/license/phase summaries, and searchable setting details. These records do not replace the 95-task sales estimating catalog or the existing 371-control assessment catalog.
- A searchable reference view of the 149-table / 1,206-column ComplianceIQ data dictionary. It is stored as design-reference data only; the app does not generate those 149 tables or treat that draft as its live SQL schema.
- A deployment-only workspace JSON export (schema version 9) containing engagement and profile discovery responses, versioned Secure Plus catalogs, the searchable library source inventory, app-registration checklist progress, and each source's review/use boundary. Sales exports continue to omit deployment-only records.
- A manual assessment engine with customer history, evidence and exception rationale, weighted scoring, phase/category/level breakdowns, recommendations, critical gaps, and completed read-only snapshots.
- When creating an assessment, choose to copy existing profile findings or start all active controls as `NotAssessed`. A fresh manual run does not perform live reads; live evidence remains a separate, operator-initiated preview until reviewed.
- A read-only comparison of two completed assessment snapshots for the same customer and matching catalog/control set. It reports recorded status changes and score difference; it is not a live assessment or approved golden-tenant baseline.
- A deployment-only fleet overview listing each customer's latest completed assessment, score, and unassessed control count. Customers without completed assessments are shown separately; scores are manual findings, not verified live tenant posture.
- Engineers can preview aggregate Conditional Access policy evidence from a complete, user-supplied Microsoft Graph collection JSON export (maximum 2 MB). The upload is not persisted, tenant identity and completeness cannot be verified, and no assessment result changes; do not upload credentials or tokens.
- A downloadable internal HTML assessment report with scoring, recommendations, findings, and recorded evidence. Review and remove sensitive information before sharing; it is not a live verification or execution approval.
- A customer app-registration checklist that records only tenant ID, primary domain, public client ID, and per-customer setup-step status. Checklist progress is persisted in the local database and included only in deployment workspace exports. It does not create registrations, validate consent, store credentials, or connect the dashboard to Graph. A complete least-privilege Graph scope mapping is not implemented.
- A partial read-only Graph evidence/permission inventory for 24 controls, with Microsoft documentation links, evidence limitations, and a searchable list of unmapped controls. These are review candidates, not an approved scope list or automated compliance conclusions.
- Offline Conditional Access evidence preview includes selected policy targeting and exclusion details for manual review; absent fields are shown as unknown. It never saves the upload or changes assessment findings.
- A local .NET read-only worker can preview a Conditional Access export or, after explicit operator sign-in, read aggregate Conditional Access policy evidence from Graph. The local Development dashboard can launch that worker for the selected customer. It has no queue, script runner, or tenant-write capability and is not an isolation boundary.
- Full discovery responses can be stored in a customer profile log without a sales engagement. Profile and engagement logs are independent and are not automatically copied or merged.
- A deployment builder that creates a review package from confirmed gaps in a completed assessment, plus an approval queue restricted to the configured approver/admin roles. The requester cannot approve their own plan. Acceptance is design review only, not authorization to execute a tenant change.
- A comment-only text draft can be downloaded from plan history or the review queue. It lists every selected control, marks template readiness and unresolved permissions/rollback, and includes no executable commands. It is not tenant-bound and cannot authorize execution.
- A downloadable HTML review preview for each plan, showing recorded assessment evidence, current catalog guidance, and review history. It is not a script and never authorizes or executes tenant changes; review sensitive content before sharing.
- A persisted append-only plan event history records creation, submission, and reviewer decisions with actor, timestamp, and rationale; it appears in the review UI and workspace JSON export.
- A hard execution block in the API response: there is no isolated worker and no individually reviewed deployment template. Script generation and execution are not enabled. The API reports `executionEnabled: false`, and assessment responses report `tenantChangesEnabled: false`.

All `/api/deployment/*` routes are server-authorized for deployment roles; hiding navigation is not the security boundary. Sales can still create and view engagement data, but cannot read deployment profiles or assessment data. Before production, configure and test Entra group claims, callback URLs, and secret-store values, and review customer-level access scoping. Do not expose the Development identity outside a local developer machine. Any future tenant-changing workflow must retain a separate reviewer, require explicit operator launch through the isolated worker, and validate a reviewed template and least-privilege permissions.

### Read-only worker

For the local dashboard, build the worker in Release configuration, then restart the API if it was already running:

```powershell
dotnet build .\server\ArchiTech.ReadOnlyWorker\ArchiTech.ReadOnlyWorker.csproj -c Release
```

In the customer deployment profile, save and verify the tenant GUID and public client GUID. On the assessment tab, optionally select the security defaults read, acknowledge the consent warning and select **Sign in and read selected evidence**. The API launches only the fixed local worker executable; this integration is limited to loopback requests in local Development authentication mode and is not available in hosted/production mode. The result is displayed temporarily and never saved to findings or assessment snapshots. Review report-only, disabled, unknown and enabled policy counts and selected per-policy details manually. The optional security defaults read uses the same delegated `Policy.Read.All` permission, and returns only its enabled state; if either requested read fails, no partial result is displayed. The operator confirmed that the security defaults state appeared in a live lab read. Policy names and configuration can be sensitive. The live reader redacts user, group, role and application identifiers, rejects more than 1,000 policies or incomplete responses, and does not infer a control result.

The optional named locations count uses the same delegated permission and requests only location IDs to count them; names, addresses, and IDs are not returned to the dashboard. It supports bounded pagination and rejects duplicate IDs, incomplete reads, or more than 1,000 locations. If any selected read fails, the combined result is discarded. The operator confirmed that the count appeared in a live lab read. Counts do not establish whether locations are trusted or used by an enforced policy.

The live preview offers partial evidence text for `AUTH-004` and `AUTH-002`, with the tenant, observation time, endpoint and coverage limitations. An operator may copy it into a manually selected assessment after verifying the tenant and reviewing the evidence. Copying does not save a finding or set a status; the limited policy counts do not establish MFA or legacy-auth enforcement.

The optional authentication methods policy read also uses `Policy.Read.All` (a higher-privileged documented alternative to `Policy.Read.AuthenticationMethod`) and displays only its migration state. Microsoft documents Global Reader or Authentication Policy Administrator as supported least-privileged roles for this endpoint. Graph returns expanded method configuration and targeting data to the local worker; the worker does not output or save that data. In the lab, the response did not contain a migration state, so this selected read fails explicitly and no combined result is returned. Deselect it to read the other evidence. The absence does not establish the migration state, MFA coverage, or method configuration.

From the repository root, run the worker manually with a complete UTF-8 Graph collection JSON export containing a `value` array:

```powershell
dotnet run --project .\server\ArchiTech.ReadOnlyWorker\ArchiTech.ReadOnlyWorker.csproj -- preview-ca-export 'C:\path\to\policies.json'
```

The preview prints JSON to standard output and returns a nonzero exit code for unreadable, malformed, incomplete, or larger-than-2 MiB exports. It does not save the export or result, verify the source tenant or freshness, update findings, or infer compliance. Policy names and other preview details may be sensitive: protect the file, terminal and any redirected output, and never supply credentials or tokens.

To explicitly start the separate **live read-only** command:

```powershell
dotnet run --project .\server\ArchiTech.ReadOnlyWorker\ArchiTech.ReadOnlyWorker.csproj -- read-ca-policies '<customer-tenant-guid>' '<public-client-guid>'
```

Append `--include-security-defaults`, `--include-named-locations`, and/or `--include-authentication-methods` to the worker command to explicitly request those additional reads.

If Microsoft sign-in remains pending after canceling a prompt, select **Cancel pending read** in the dashboard. This aborts the local request and signals the worker to stop; close any remaining Microsoft browser window separately. Cancellation does not establish whether an earlier Graph request had already started.

Verify the customer tenant and registration before typing `READ`. Microsoft sign-in can display a consent screen: **cancel it**, do not grant or request additional consent. The system browser is controlled by Microsoft; the worker cannot detect or automatically cancel a consent screen. The registration must support public-client system-browser sign-in with `http://localhost` as a mobile/desktop redirect URI, and the operator needs an existing delegated `Policy.Read.All` grant and supported directory role. No registration changes are made by this command. MSAL also requests standard sign-in scopes; no additional Graph permissions are intentionally requested. The token stays in worker memory and is not printed or passed through the web app. The worker checks MSAL's returned tenant and scopes before the policy read, and outputs counts and selected policy details marked `NotAssessed`; it does not save findings. Failed or incomplete reads return an error instead of partial evidence. The standalone count-only command was tested against the lab tenant; the operator also confirmed that expanded policy details appeared in the local dashboard. This confirmation does not establish policy compliance or full discovery coverage. The dashboard integration is local only. Neither command is a background service or an execution sandbox.

The Secure Plus source workbook's discovery, WBS, and baseline-settings sheets are labeled WIP and are surfaced with that status. Validate the workbook and licensing/provenance before treating it as an approved customer standard. Framework documents and CIS benchmark text are not copied into the runtime catalog; source identifiers and references should be used until commercial-use terms are confirmed. The existing sales estimate and assessment catalogs remain separate/versioned to prevent unreviewed WIP data from changing prior estimates or readiness scores. See [`docs/standards-catalog.md`](docs/standards-catalog.md) for the additional SharePoint inventory and its import boundaries.

See [`docs/standards-catalog.md`](docs/standards-catalog.md) for the exact imported datasets, versioning, licensing boundaries, and migration behavior.

## Estimate behavior

The intake keeps the workbook's applicable-when rules and calculates monthly Secure Plus operations separately from one-time phases. Automated checks cover both supplied workbook checkpoints:

- No optional discovery answers: 19 applicable tasks, 104–198 one-time hours, and 28–60 monthly hours.
- Every scenario enabled: 95 applicable tasks, 1,120–2,230 one-time hours, and 36–76 monthly hours.

## Azure SQL migration

The API is configured through EF Core's provider boundary. SQLite is the local provider; `SqlServer` is the hosted provider and has an initial SQL Server migration under `server\ArchiTech.Api\Data\SqlServerMigrations`. Before deploying:

1. Provision Azure SQL, networking, and an Entra ID identity with least-privilege database access. Configure the Entra workforce application and security groups as described above; verify the emitted group claims and role denial behavior before exposing the app beyond its intended users.
2. Store the connection string/identity configuration in the hosting platform's secret store (for example, Key Vault), not in this repository. Set `Database__Provider=SqlServer` and `ConnectionStrings__Default` in the API environment.
3. Restore the local EF tool and apply the SQL Server migration from the repository root:

   ```powershell
   $env:Database__Provider = 'SqlServer'
   $env:ConnectionStrings__Default = '<connection string supplied by the secret store>'
   dotnet tool restore
   dotnet ef database update --project .\server\ArchiTech.Api\ArchiTech.Api.csproj --startup-project .\server\ArchiTech.Api\ArchiTech.Api.csproj --context AppDbContext
   ```

   The API also applies migrations on startup for the SQL Server provider and seeds the sales WBS and versioned standards catalogs from the included catalog files.
4. Configure and test the Power Automate HTTPS endpoint and its idempotency behavior in the hosted environment.

The checked-in migrations define the SQL Server schema; the deployment-workspace and Secure Plus standards migrations add the deployment and discovery/catalog tables. Existing SQL Server customers receive deployment profiles during API startup. Local SQLite uses EF Core `EnsureCreated` plus additive table creation/backfill and does not use the SQL Server migrations. Moving existing local customer/engagement data to SQL Server requires the separate `ArchiTech.DataTransfer` utility and an explicitly approved cutover; the API does not silently copy or convert the SQLite database. Generate and review a new SQL Server migration whenever the EF model changes.

For the Archi-Tech 2.0 capability and scope comparison, transfer utility, cutover rehearsal, source-data protection, migration-script checks, row reconciliation, Planner flow acceptance criteria, read-only Graph evidence mapping, and RBAC/customer-scope readiness, see [`docs/external-service-readiness.md`](docs/external-service-readiness.md). The workspace JSON export is not an Azure SQL import format.

## Validation

From the repository root:

```powershell
dotnet test .\ArchiTech.slnx
cd .\web
npm run typecheck
npm run build
```
