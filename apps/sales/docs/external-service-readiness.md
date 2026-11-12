# External service readiness

This checklist is preparation only. It does not configure cloud resources, connect to a customer tenant, or enable tenant changes.

## Azure SQL cutover rehearsal

### Before the hosted database is available

- Keep the working local SQLite database as the source of truth until a cutover is approved. Do not point the local app at a production database while both environments are in use.
- Record a cutover window and a named operator. Stop writes to the source during the final copy.
- Take a consistent SQLite backup using SQLite's online backup facility (for example, `sqlite3 architech.db ".backup 'architech-pre-cutover.db'"`). Do not copy only the main database file while a WAL database is live.
- Preserve the backup read-only, record its SHA-256, and test that it opens and has the expected table counts before attempting a transfer.
- Configure the destination connection only through the host's secret store or managed identity. Never put a real connection string in this repository, command history, or a test artifact.
- Review the latest migration script and ensure the Azure SQL database is a dedicated empty target or has an explicitly reviewed baseline.

### Schema rehearsal

From the repository root, with the EF tool restored and a design-time SQL Server connection supplied through the environment:

```powershell
$env:Database__Provider = 'SqlServer'
$env:ConnectionStrings__Default = '<disposable SQL Server connection from a secret store>'
dotnet tool run dotnet-ef migrations has-pending-model-changes --project .\server\ArchiTech.Api\ArchiTech.Api.csproj --startup-project .\server\ArchiTech.Api\ArchiTech.Api.csproj
dotnet tool run dotnet-ef migrations script --idempotent --project .\server\ArchiTech.Api\ArchiTech.Api.csproj --startup-project .\server\ArchiTech.Api\ArchiTech.Api.csproj --output "$env:TEMP\architech-schema.sql"
```

Inspect the generated SQL, apply it first to a disposable SQL Server database, start the API against that database, and confirm the migration history, indexes, foreign keys, WBS seed, and deployment profile backfill. Do not apply a locally generated script to production without a reviewed backup and change window.

### Data-copy requirements

The schema migration creates tables; **it does not copy local SQLite records**. The `ArchiTech.DataTransfer` utility copies a verified SQLite backup into a dedicated, existing, empty SQL Server database. Rehearse and obtain explicit cutover approval first. It preserves the existing GUIDs and relational keys for:

- Customers and deployment profiles.
- Engagements, normalized answer rows, scoped WBS tasks, and Planner handoff state.
- Manual deployment findings and assessment runs/results/breakdowns/recommendations.
- Engagement and profile-level discovery responses, including their catalog keys.
- Deployment change plans, selected control snapshots, and their audit events.

The workspace JSON export is a review/backup artifact, not a supported Azure SQL import format. Do not manually import it into production.

The utility verifies the backup hash, SQLite integrity and foreign keys, and an empty destination; applies SQL migrations; compares table and column sets; and copies all tables in foreign-key order. Inserts preserve identities and use checked constraints in bounded batches within a single transaction. It rejects a SQLite identity sequence greater than its table's maximum ID rather than risk reusing deleted IDs; resolve that case separately before transfer. Every row and column is reconciled by hash before commit. A failed copy rolls back all copied rows, but migrations applied before the transaction remain. `--resume-empty` permits a retry only after confirming the prior migrated destination has the expected migration history and empty tables; it never clears existing data.

Stop all source and destination application writers for final cutover, take a **new** online backup, and retain it until reconciliation is accepted. Ensure no other process can change the backup or destination schema during transfer. Supply the connection through a protected environment or secret store and validate the server certificate. With an already-created dedicated empty database and an approved backup:

```powershell
$env:ARCHITECH_TRANSFER_SQL = '<SQL Server connection supplied by the secret store>'
$env:ARCHITECH_TRANSFER_SHA256 = '<SHA-256 of the approved SQLite backup>'
dotnet run --project .\server\ArchiTech.DataTransfer\ArchiTech.DataTransfer.csproj -- '<full path to SQLite backup>'
```

Test application startup, engagement and assessment views, SQL constraints, and identity sequences on the rehearsal destination; compare critical exports and business results before cutover. SQL Server and SQLite have different type and collation rules, so an import error requires review rather than silently dropping rows. SQL Server startup applies migrations and seeds catalogs, and therefore is itself a write operation. The application must not be switched to the destination until the owner approves the copy and cutover.

## Planner handoff readiness

The current API sends schema-version-2 payloads to a configured HTTPS Power Automate endpoint. Use synthetic customer data until the organization-owned flow is approved. Before configuring the endpoint:

- Choose the Microsoft 365 group that owns the plans (one shared group or a group per customer). Confirm the group can create Planner plans and that the required Planner licensing is available.
- Decide which organization identities own/receive notifications for Sales, Project Management, and Engineering. The payload's stakeholder labels are categories, not directory identities or assignments.
- Have the flow atomically reserve or look up `engagementId` before creating a plan. Concurrent retries with the same idempotency key must return the same plan ID and must not create duplicate plans, buckets, or tasks.
- Create one phase bucket per distinct `phases[].name`, then create every `tasks[]` entry exactly once in its matching bucket. Reconcile the phase `taskWbsIds` against the flat task list.
- Return HTTP 2xx with `{"plannerPlanId":"<id>"}` only after the idempotent plan is found or created and its requested tasks are persisted; return a failure when the flow cannot complete. A missing or invalid ID is an uncertain outcome, not proof that no plan was created.
- Confirm which customer contact and sales-note fields the flow actually needs. The current payload contains contact details and notes; keep the flow organization-owned, minimize access/retention, and do not forward this payload to personal connectors.
- Protect the trigger URL as a secret, configure it only in the API host, and test success, timeout, non-2xx, malformed response, duplicate retry, and concurrent duplicate requests with synthetic records.
- A timeout, communication failure, non-2xx response, malformed success, or interrupted `Pending` request can leave a plan or partial tasks remotely. Inspect and reconcile the flow and Planner before retrying; the request's idempotency key alone cannot prevent duplicates unless the flow implements durable, atomic idempotency and recovery.
- The app atomically claims a `NotConfigured` engagement before its first configured send. It blocks resending `Pending` or `Failed` engagements because their remote outcome may be uncertain; use an authorized reconciliation procedure, not a blind retry.
- Operators or administrators may record a **manual confirmation** for a `Failed` handoff after checking a terminal flow run, the matching plan, and every scoped task. The app records the plan ID, authenticated actor, reference, and confirmation time; it does not independently verify them or send another request. Use a nonsecret run or reconciliation reference, never a trigger URL or token. `Pending` remains blocked because the app cannot establish that an interrupted request has stopped. Development mode uses a shared simulated identity and is not an independent audit.
- The API currently waits only 20 seconds. In a synthetic 12-task test, the caller timed out after 20.1 seconds, while the flow recorded the plan as completed and then failed its Response action because the client had disconnected. This demonstrates that a completed remote handoff can be reported locally as uncertain. The test did not establish timing or recoverability for a full WBS. Implement asynchronous completion and verified reconciliation before relying on the flow for app engagements; increasing the timeout alone does not solve interrupted or partially completed runs.
- The test trigger URL was exposed in session diagnostics and must be rotated before broader use. The API's Planner HTTP client disables redirects and built-in HTTP client logging to avoid sending or logging the signed URL; infrastructure and other telemetry must also be reviewed.

### Planned authenticated, asynchronous handoff

The app now has a certificate-based Entra client, but the flow integration and the asynchronous design below are not fully implemented or approved for customer data:

- For the public cloud certificate client, configure `PlannerFlow:AuthenticationMode` as `EntraCertificate`, `PlannerFlow:Endpoint` as the authenticated unsigned trigger URL, `PlannerFlow:AllowedHost` as its exact `.environment.api.powerplatform.com` host, and `PlannerFlow:TenantId` and `ClientId` on the API host. Choose exactly one certificate source: `CertificateThumbprint` for a valid private-key certificate in the API process user's CurrentUser\My certificate store, or `CertificatePath` and, if needed, `CertificatePassword` for a PFX. The private key must stay outside the repository; use a protected secret store and restrict access. This client requests `https://service.flow.microsoft.com//.default`; other clouds and trigger host formats are not supported. The app rejects signed URLs in this mode and disables redirects. No live token or authenticated trigger test has been performed.
- Use an organization-owned workload identity and configure the HTTP trigger for **Specific users in my tenant**, with the workload service principal's object ID explicitly listed. An empty allowed-users list permits the whole tenant. Verify the token audience and the required issuer, tenant, and object ID claims; see [Microsoft's HTTP trigger authentication guidance](https://learn.microsoft.com/en-us/power-automate/oauth-authentication). Keep the URL and tokens out of logs and source control, and rotate the exposed test URL. Never submit tokens to external token-inspection websites.
- Persist a uniquely keyed operation and immutable payload before dispatch. Have the flow validate the payload and reserve that key durably before returning an acceptance response; acceptance must not be presented as plan completion. A lost acceptance response still requires reconciliation, not an automatic resend.
- Record progress and remote IDs durably. A separately authenticated, authorized completion channel or status lookup must establish the outcome for the same operation and payload, and the API must verify and persist it before showing `Sent`. Protect updates against replay and conflicting or stale results; log the actor, operation, and outcome without customer payloads or credentials.
- Treat timeouts, lost callbacks, and partial writes as uncertain. Reservation uniqueness does not close the gap between creating a Planner resource and recording its ID. Do not retry an uncertain create until the existing resources have been positively reconciled; completed duplicate operations must return the original result only when their payload matches.
- Before enabling for customers, validate concurrent new-key requests, duplicate and mismatched payloads, malformed payloads, partial creates, interrupted runs, lost completion signals, and realistic WBS sizes. Establish least-privilege ownership, retention, access controls, and a reviewed operational reconciliation procedure. Do not reuse the synthetic flow's personal connection or exposed trigger as the production integration.

## Read-only Graph assessment preparation

The local Development dashboard can initiate a customer-specific, read-only Conditional Access policy inventory using an existing delegated grant. Additional discovery endpoints require a control-to-evidence inventory and security review before selecting scopes or requesting admin consent. Do not infer Graph permissions from a control label, and do not add write scopes to an assessment registration.

### Archi-Tech 2.0 comparison

The legacy source's `TenantAuthService.AssessmentScopes` requests these 19 **delegated** Microsoft Graph scopes together:

`AccessReview.Read.All`, `Application.Read.All`, `AuditLog.Read.All`,
`DeviceManagementConfiguration.Read.All`, `DeviceManagementManagedDevices.Read.All`,
`Directory.Read.All`, `IdentityRiskyUser.Read.All`, `Organization.Read.All`,
`Policy.Read.All`, `Policy.Read.AuthenticationMethod`, `Policy.Read.IdentityProtection`,
`Reports.Read.All`, `RoleAssignmentSchedule.Read.Directory`,
`RoleEligibilitySchedule.Read.Directory`, `RoleManagement.Read.Directory`,
`SecurityEvents.Read.All`, `User.Read`, `User.Read.All`, and
`UserAuthenticationMethod.Read.All`.

The GDS Lab screenshot appears broadly similar, but it does not visibly list `RoleManagement.Read.Directory` and shows a separate truncated `RoleManagementAlert.Read...` entry. Some names in the screenshot are truncated. Verify the registration's exact configured permissions and granted consents before comparing them; this is not a request to add, remove, or grant any permission.

The legacy README describes 123 Graph-assessable controls; its source includes individual readers and a wider set of PowerShell templates. These counts describe the old implementation, not verified coverage or the scope requirements for 3.0. In particular, the legacy `AUTH-004` reader marks MFA as implemented if it finds any enabled policy requiring MFA; that fact alone does not establish coverage for all users or account for exclusions. Review each reader's endpoint, pagination, licensing, privilege requirements, evidence and conclusion before adopting it. A denied or incomplete read must remain not assessed, not silently become compliant.

| Workflow | 2.0 | 3.0 |
|---|---|---|
| Customer profiles and assessments | Local profiles, manual findings and live Graph readers | Local profiles, manual findings and assessment history; local dashboard and standalone worker support CA policy counts and selected details only |
| Readiness and roadmap | Scores, analysis and phased roadmap | Scores, analysis, review-only roadmap and sales WBS linkage |
| Reports | Technical, customer, executive and roadmap HTML formats | Internal assessment HTML download; customer, executive and roadmap formats not yet equivalent |
| App registration | Interactive desktop public-client sign-in and setup guidance | Public identifiers and checklist; separate worker supports interactive public-client sign-in, with no registration or consent management |
| Deployment builder and review | Inert script previews, approvals and separately gated deployment | Assessment-based review packages, independent approval history, and non-executable HTML previews; no script generation or execution |
| Tenant changes | Legacy Conditional Access deployment flow exists; catalog audit reports zero approved remediation templates | Disabled: isolated worker and individually approved templates not available |
| Other legacy features | Golden-tenant comparison, fleet view, PowerShell companion, Conditional Access drafts, optional integrations | Same-customer snapshot comparison and a local fleet overview are available; golden baselines, PowerShell companion, CA drafts and integrations are not restored |

This is a capability comparison, not a promise of complete parity. The legacy in-process execution design is not an approved substitute for the isolated worker. The screenshot's existing grants do not authorize new consent or prove that this app can use those permissions.

### First Graph evidence adapter

The fixture-tested, read-only Conditional Access policy evidence reader calls `GET https://graph.microsoft.com/v1.0/identity/conditionalAccess/policies`, follows only same-endpoint Graph pagination, rejects redirects and incomplete/malformed responses, and produces counts and selected policy details without declaring compliance. If explicitly selected, it additionally calls `GET https://graph.microsoft.com/v1.0/policies/identitySecurityDefaultsEnforcementPolicy` and returns only its enabled state. Microsoft documents delegated `Policy.Read.All` as least privilege for [this endpoint](https://learn.microsoft.com/en-us/graph/api/identitysecuritydefaultsenforcementpolicy-get?view=graph-rest-1.0). Failure of either selected read discards the combined result. Details include names, states, grant controls, client types, all-user/application flags and exclusion counts, not user, group, role or application IDs. They may still be sensitive and are not saved. A local worker acquires a delegated token interactively for these reads and verifies the returned tenant and scope. The local Development dashboard can launch it after an operator confirms the customer and identifiers; no findings are saved. The operator must cancel any consent screen; the worker cannot detect or automatically cancel that screen in the system browser. The standalone count-only worker was verified against the lab tenant, and the operator confirmed that expanded policy details and the optional security defaults state appeared in live lab dashboard reads. None of this establishes policy compliance or full discovery coverage.

An additional opt-in calls `GET https://graph.microsoft.com/v1.0/identity/conditionalAccess/namedLocations?$select=id` and returns only the count, not the IDs, names, IP ranges or countries. Microsoft documents delegated `Policy.Read.All` and supported user roles for [this endpoint](https://learn.microsoft.com/en-us/graph/api/conditionalaccessroot-list-namedlocations?view=graph-rest-1.0). The reader rejects redirects, invalid pagination, duplicate IDs, oversized pages, and more than 1,000 locations. If this selected read fails, no combined result is returned. The operator confirmed that the count appeared in a live lab dashboard read. A count does not establish trusted status, policy targeting, or enforcement.

Another opt-in reads `GET https://graph.microsoft.com/v1.0/policies/authenticationMethodsPolicy` and returns only the policy migration state. Microsoft documents `Policy.Read.AuthenticationMethod` as the least-privileged delegated permission and `Policy.Read.All` as a higher-privileged alternative; this worker requests only its existing `Policy.Read.All` scope. The documented least-privileged user roles are Global Reader and Authentication Policy Administrator; see [Get authenticationMethodsPolicy](https://learn.microsoft.com/en-us/graph/api/authenticationmethodspolicy-get?view=graph-rest-1.0). Graph automatically returns expanded method configurations and target data to the worker; these are neither output nor saved. Failed, malformed, redirected, or oversized responses discard the combined evidence; unrecognized migration states are shown as unknown. In a live lab read, the response lacked a migration state, so the app reported this explicitly and discarded the combined result. The cause is not yet established; the missing property must not be interpreted as any particular migration state. It does not evaluate MFA enforcement, allowed methods, targeting, or compliance.

The assessment UI also supports a separate offline preview of an existing complete Graph collection JSON export (a top-level `value` array). Selected policy details show state, grant controls and operator, client types, all-user/all-application targeting flags, and exclusion counts without exposing user, group or role IDs. Missing or null fields are shown as unknown. This is not a full policy definition: group, guest, location, risk and other conditions still require review. The preview does not call Graph, persist the upload, or change findings. The operator must independently verify tenant identity, freshness, and completeness; a file with `@odata.nextLink` is rejected. Policy data may be sensitive, so use only a trusted local app and do not upload tokens or credentials. The preview does not demonstrate live connection or assessment parity.

Microsoft documents delegated **Policy.Read.All** as the least-privileged Graph scope for this one endpoint, and the signed-in user also needs a supported Entra role, such as Security Reader. This is not a full-app permission list or authorization to grant consent. See [List Conditional Access policies](https://learn.microsoft.com/en-us/graph/api/conditionalaccessroot-list-policies?view=graph-rest-1.0).

The app's registration screen lists first-pass **partial evidence** candidates for 24 controls. Most reference the Conditional Access policy-list endpoint; other candidates include named locations, authentication methods migration state, Intune compliance policy inventory and MFA registration reports, each with separately documented endpoint permissions and role or licensing requirements. Registration reports include user information and omit disabled users; review sensitivity, access and retention before any collection. A policy list does not prove that a control is satisfied; targeting, exclusions, grant operators, other controls and tenant conditions require review. The evidence adapter returns selected policy details, MFA/legacy-block counts, and optionally security defaults, a named location count, or authentication methods migration state; it does not read the other candidate endpoints or evaluate controls. These mappings do not constitute a full control-to-permission inventory, and remaining active controls are listed as unmapped.

### Initial tenant-admin review package

The user selected a **single-tenant registration in the customer tenant** as the intended model for the first read-only assessment. The user identified a lab registration and reported 19 existing delegated Graph grants, then confirmed that those grants are approved for testing and use, with a least-privilege review planned before script execution. This confirmation does not authorize new consent or tenant writes. The user allowed interactive sign-in with a warning to cancel any consent prompt. The app does not independently verify the registration identity, configuration, grants, or their approval.

- **Read-only operations:** `GET https://graph.microsoft.com/v1.0/identity/conditionalAccess/policies` and, when explicitly selected, `GET https://graph.microsoft.com/v1.0/policies/identitySecurityDefaultsEnforcementPolicy`, `GET https://graph.microsoft.com/v1.0/identity/conditionalAccess/namedLocations?$select=id`, and `GET https://graph.microsoft.com/v1.0/policies/authenticationMethodsPolicy`.
- **Graph delegated permission:** `Policy.Read.All`, documented as least-privileged for the Conditional Access and security defaults endpoints and as a higher-privileged alternative for the authentication methods endpoint. It is not restricted to these endpoints. A signed-in user also needs a supported role for each selected endpoint; authentication methods has different role requirements.
- **Not included:** application permissions, Graph write scopes, other assessment controls, Planner permissions, and any automatic change to the tenant. Other controls require separately documented permissions and customer review.
- **Not finalized:** verification of the registration and public-client redirect configuration, customer authorization, data handling and live-tenant acceptance. The worker uses MSAL in-memory token handling and Microsoft system-browser sign-in; the operator must cancel any consent prompt. Do not grant consent based on this partial package.
- **Failure and evidence boundary:** access denied, missing permissions, invalid tenant identity, incomplete pages, or ambiguous evidence must remain not assessed; policy inventory alone cannot establish control compliance.

For each candidate control, document:

| Field | Required decision |
|---|---|
| Control ID and catalog version | Stable reference to the exact control being assessed. |
| Evidence question | What observable tenant fact would support a result, and what cannot be inferred. |
| Graph resource and endpoint | Exact resource/API version from current Microsoft documentation. |
| Permission type and minimum scope | Delegated or application permission, least privilege, and whether an app-only design is necessary. |
| Consent and ownership | Customer tenant administrator who approves the permission and the customer registration that owns it. |
| Data handling | Returned properties, sensitivity, retention, access, and evidence redaction requirements. |
| Failure semantics | How denied consent, unsupported APIs, throttling, and incomplete evidence remain NotAssessed instead of being reported as compliant. |
| Test evidence | Synthetic/fixture response, scope review, and negative test proving no tenant write is issued. |

Start with read-only inventory candidates (for example, directory configuration and device/security posture), then validate each endpoint and permission against current Microsoft Graph documentation. The wizard currently stores public identifiers only; it does not create registrations, handle consent, store credentials, or call Graph.

## Workforce RBAC and customer data

- Configure Entra groups for Sales, Deployment engineers, Approvers, Operators, and Administrators; record the approved owner and membership-review cadence for each.
- Verify the sign-in token includes the configured group object IDs, including the tenant's group-overage behavior. Test direct API requests, not only hidden navigation.
- Confirm whether each deployment role can access every customer or should be restricted to assigned customers. Current deployment APIs grant deployment-role access across customer profiles; assignment-level customer authorization must be designed and implemented before a hosted rollout if broader access is not intended.
- Confirm reviewer membership is separate from plan requesters. Plan approval only accepts/rejects a design package; it does not enable script execution. Creating, submitting, and reviewing plans require a single stable authenticated subject; older plans with an unknown requester cannot be submitted or reviewed. Concurrent submissions or decisions are rejected when the plan status has changed, and the losing audit event is rolled back. Local Development's shared identity is for local testing, not independent review.
- Keep tenant execution disabled until an isolated worker, per-control reviewed templates, operator launch policy, and auditable approval-to-execution binding exist.

## Selected interactive local worker

The selected direction for future tenant writes is an operator-signed-in workflow with execution isolated separately from orchestration; a dedicated Hyper-V VM is not mandatory. A .NET Worker Service can orchestrate queued work under a separate least-privilege identity, but is not itself a security boundary. A suitable container, job, or VM boundary must be designed and verified before tenant-write execution is enabled.

A separate local .NET worker provides the offline Conditional Access export preview and an operator-initiated live read of CA policy counts and selected details. The local Development dashboard can launch the latter; neither path updates assessment persistence or runs scripts. It runs under the invoking user's account, not a restricted worker identity, and is not a security boundary. See the README for usage, consent warning and data-handling limits.

Before enabling execution:

- Provide and verify an appropriate isolated execution environment and authorize the operator's access. Document the host, reset process, network restrictions, identity and authentication strategy, and patching and module versions. Do not repurpose existing infrastructure without its owner's approval.
- Design an authenticated, tenant-bound communication channel with no arbitrary script submission. Define exactly how the operator signs into the customer from the guest without exposing credentials or tokens to the app, logs, or unrelated jobs. Confirm actual tenant identity and operator authorization before any execution.
- Approve each immutable script template with its exact version/hash, parameters, permissions, preconditions, impact, verification, rollback, and expiry. The legacy catalog audit reports zero approved remediation templates; a reviewed assessment plan or a signed-in session does not approve an executable template.
- Require an independent review of the exact tenant, template version/hash and parameter values, and a separate explicit operator launch. Revalidate those bindings and permissions immediately before execution, fail closed, and record auditable outcomes without retaining secrets.
- Exercise the isolation boundary and the complete workflow with synthetic data before testing any approved change in a lab tenant. The existing disabled execution control is intentional; there is no tenant execution worker or launch endpoint yet.
