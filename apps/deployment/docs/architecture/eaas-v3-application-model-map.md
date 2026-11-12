# EaaS v3 application-model map

How Secure Plus - Modern Work maps to the EaaS v3 design. Every row is labelled **EAAS V3 DEFINED** (the concept
comes from the v3 dictionary or design) or **SECURE PLUS EXTENSION** (an application concept, with the reason).
Sources are listed in `eaas-v3-database-analysis.md`.

## Application model (Secure Plus Application Model.vsdm)

The application model's left-hand menu is exactly the navigation implemented in `RouteCatalog`/`Navigation.ini`:
Dashboard (Overview, Road Map, Catalog, Fleet View), Assessment (Assessment Scan, Live Assessment), Planning
(Script Generator, Config File Generator), Deployment (Change Request, Change Execution, Tracker), Reporting
(Control Completion View, Solution Completion View, Specification Comparison) and Workspace (Customer Profiles:
Tenant App Registration, Import, Export, Exceptions; Settings: Workspace Configuration Import/Export, Teams
Integration, ConnectWise Integration, Golden Tenant Configuration, Database Configuration, Auditing and Logs).
Credential Management and Appearance are **SECURE PLUS EXTENSION** pages (required to manage secrets and the GDS theme).

## Domain and data

| Application concept | v3 mapping | Label | Notes |
|---|---|---|---|
| `CustomerProfile` (`Domain/Customers`) | `cust.Customer`, `cust.Contact` | EAAS V3 DEFINED | Profile also carries tags, notes and settings dictionaries (extension fields, file-stored). |
| `TenantConfiguration` | `cust.Tenant` (TenantGuid, PrimaryDomain, CloudEnvironmentCode) | EAAS V3 DEFINED | `TenantKey` (stable key within a customer) is a Secure Plus extension because one customer can have several tenants and v3 keys tenants by surrogate ID only. |
| `CloudEnvironment` (Commercial, GCC, GCC High, DoD) | `cust.Tenant.CloudEnvironmentCode` ("Commercial / GCC / GCC High") | EAAS V3 DEFINED | DoD added because Microsoft Graph defines a separate DoD endpoint. |
| `ApplicationRegistration` | `audit.TenantConnection` (AppId, AppRoleCode, certificate thumbprint/expiry, consent) | EAAS V3 DEFINED (partially) | Client secret references, service principal ID and required capabilities are extensions; v3 prefers certificate auth (see conformance: Conflict). |
| `CustomerException` | `cust.Exception` | EAAS V3 DEFINED | Requested/approved/effective/expiration and status follow v3 columns (`ApprovedByContactId`, `ApprovedAt`, `ExpiresOn`). |
| `ChangeRequest` | `deploy.ChangeRequest` | EAAS V3 DEFINED | Tasks, approvals and execution records are modelled minimally until M7. |
| `GoldenTenantExpectation`, `ComparisonStatus` | `audit.ApprovedBaseline`, `audit.ComparisonRule`, `audit.Drift`, `pol.PolicySet` (Golden-Tenant) | EAAS V3 DEFINED | Comparison engine deferred to M5. |
| `PolicyArtifact`, repository scanner | `pol.Repository`, `pol.RepositoryVersion`, `pol.Artifact` | EAAS V3 DEFINED | Scanner records companion scripts without executing them. |
| `Workspace` | — | SECURE PLUS EXTENSION | Engineer working context; stored as `Workspace.ini` and `secureplus.Workspace`. |
| `CredentialMetadata`, `ISecretStore`, `SecretReference` | — | SECURE PLUS EXTENSION | v3 stores no secrets; the architecture's Key Vault is reachable later through `ISecretStore` without changing consumers. |
| `AuditEvent` | — | SECURE PLUS EXTENSION | User/application audit; v3 `audit.*` is tenant-state audit. |
| Schema version history | — | SECURE PLUS EXTENSION | `secureplus.SchemaVersion`; v3 has no migration mechanism. |
| Appearance (`ApplicationProperties.json`) | — | SECURE PLUS EXTENSION | UI concern outside the data model. |

## Workflow (EaaS_Workflow_and_Architecture.vsdx)

| EaaS stage / engine | Secure Plus destination | Label | Status |
|---|---|---|---|
| Discovery engine, questionnaire (`eux.*`, `cust.QuestionResponse`, `cust.FormSubmission`) | Assessment, Customer Profile Import | EAAS V3 DEFINED | Placeholder (M4 / M2) |
| Live Assessment collectors (multi-tenant Collector app, read) | Live Assessment, Tenant App Registration | EAAS V3 DEFINED | Tenant connection test implemented; collectors M4 |
| Specification / SOW engine (`spec.*`, `dlv.*`) | Planning | EAAS V3 DEFINED | Placeholder (M6) |
| Deployment orchestrator (multi-tenant Deployer app, write; `deploy.*`) | Deployment | EAAS V3 DEFINED | Placeholder (M7) |
| Drift and Golden Tenant comparison (`audit.*`) | Reporting → Specification Comparison | EAAS V3 DEFINED | Implemented (v2.0.0) |
| ConnectWise (`psa.*`) | ConnectWise Integration | EAAS V3 DEFINED | Placeholder (M8) |
| Secrets in Azure Key Vault | Credential Management (`ISecretStore`, DPAPI today) | SECURE PLUS EXTENSION | DPAPI implemented; Key Vault designed for |

## Security decisions

Secret values exist only in `ISecretStore` (Windows DPAPI, current user). Configuration, customer profiles,
workspace files, database rows, exports, logs and audit carry `SecretReference` values at most. Tokens never leave
the Infrastructure boundary and are never persisted.
