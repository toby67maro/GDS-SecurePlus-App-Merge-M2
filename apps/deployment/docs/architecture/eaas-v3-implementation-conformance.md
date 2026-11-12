# EaaS v3 implementation conformance

STATUS values: **Implemented** (working code exists), **Partially Implemented**, **Deferred**, **Not Applicable**,
**Conflict** (the implementation deliberately differs from v3; reason given). Live SQL Server, Azure SQL and Microsoft
365 behaviour has not been executed in this environment; rows that depend on it say so.

| EAAS V3 REQUIREMENT | IMPLEMENTED AS | SOURCE FILE | STATUS | NOTES |
|---|---|---|---|---|
| Platform: one SQL Server 2022 / Azure SQL database | SQL Server and Azure SQL providers (SqlClient 7.1.1) | `src/EngineeringWorkspace.Infrastructure/Database/DatabaseConnectionFactory.cs` | Implemented | Not executed against a live server here (no SQL Server reachable); SQLite path exercised end to end. |
| 18 schemas, 204 tables, 1,639 columns | Generated `V0001__eaas_v3_baseline.sql` (SQL Server, SQLite) | `build/scripts/eaas-schema/generate_eaas_v3_schema.py`, `src/EngineeringWorkspace.Infrastructure/Persistence/Schema/` | Implemented | SQLite script applied and validated (204 + extension tables). SQL Server script generated but not executed here. |
| Primary keys and surrogate keys | PK constraints; IDENTITY on single integer non-FK keys | `.../Schema/SqlServer/V0001__eaas_v3_baseline.sql` | Implemented | IDENTITY is derived from "surrogate keys"; v3 does not mark identity columns explicitly. |
| Natural keys UNIQUE | Filtered unique index per table (SQL Server), UNIQUE (SQLite) | same | Implemented | One natural key per table; the dictionary does not distinguish several unique keys. |
| Foreign keys (448) | ALTER TABLE … ADD CONSTRAINT after all tables | same | Partially Implemented | 447 created. `cust.QuestionnaireInstance.QuestionSetId` (smallint) → `eux.QuestionSet` (tinyint) cannot be created: **v3 type mismatch**, recorded in the manifest. |
| Indexed columns | Non-unique indexes | same | Implemented | |
| Temporal history (17 tables) | System-versioned temporal tables, hidden period columns | same | Implemented (SQL Server) | Not Applicable on SQLite. |
| Column nullability, defaults, checks, seed data | — | — | Deferred | Not defined by v3; not invented. |
| 79 staging tables (`stg`), 21 reporting views (`rpt`) | — | — | Deferred | Named in the design, not defined in the dictionary. |
| Removed tables (`kb.PolicyTemplate*`) | Not created | generator | Implemented | |
| Versioned, repeatable initialization | `secureplus.SchemaVersion`, ordered embedded migrations, guarded DDL, single transaction, `sp_getapplock` | `src/EngineeringWorkspace.Infrastructure/Database/SchemaMigrator.cs`, `SchemaMigrations.cs` | Implemented | SECURE PLUS EXTENSION mechanism; v3 defines none. |
| Detect partial / foreign / newer schemas; never drop or downgrade | Read-only classification before any change | `src/EngineeringWorkspace.Infrastructure/Database/DatabaseProvisioningService.cs` | Implemented | Verified on SQLite: newer and foreign databases are refused and left unchanged. |
| Database name `GDS_ComplianceIQ` | Configurable `DatabaseName` | `config/Database.ini` | Not Applicable | Name is configuration, not hard-coded. |
| `cust.Customer`, `cust.Contact` | `CustomerProfile`, `CustomerContact`; `Customer.ini` store; SQLite adapter | `src/EngineeringWorkspace.Domain/Customers/CustomerProfile.cs`, `src/EngineeringWorkspace.Infrastructure/FileSystem/FileStores.cs`, `src/EngineeringWorkspace.Infrastructure/Persistence/SQLiteEaasPersistence.cs` | Partially Implemented | File store is the live store; relational persistence of profiles is M2. |
| `cust.Tenant` (GUID, primary domain, cloud) | `TenantConfiguration`, `CloudEnvironment` | `src/EngineeringWorkspace.Domain/Tenants/TenantConfiguration.cs` | Implemented | Several tenants per customer supported (`TenantKey`). |
| `audit.TenantConnection` (app ID, consent, certificate, last verified) | `ApplicationRegistration`, `TenantConnectionReport` | `src/EngineeringWorkspace.Application/Tenants/TenantModels.cs` | Partially Implemented | Kept in the customer profile and session; writing `audit.TenantConnection` rows is M2. |
| App-only access with certificate from Key Vault; Collector/Deployer multi-tenant apps | Per-customer app registration with client secret (DPAPI) | `src/EngineeringWorkspace.Infrastructure/Microsoft/MicrosoftAuthentication.cs` | Conflict | The implementation prompt requires client secret now; certificate and Key Vault are designed for (`TenantAuthenticationType.Certificate`, `ISecretStore`). Per-customer single-tenant apps are used instead of GDS multi-tenant apps; both fit the same model. |
| Admin consent per customer | Assistant step "Grant admin consent" (appRoleAssignedTo) | `src/EngineeringWorkspace.Infrastructure/Microsoft/GraphAppRegistrationService.cs` | Partially Implemented | Real Graph calls; requires a real administrator session, not executed here. |
| Tenant identity and permission validation | MSAL token + `GET /organization` + token roles | `src/EngineeringWorkspace.Application/Tenants/TenantConnectionService.cs`, `PermissionValidationService.cs` | Implemented | Not executed against a live tenant here. |
| No secrets in SQL | Metadata only (`secureplus.CredentialMetadata`), secrets in DPAPI | `src/EngineeringWorkspace.Infrastructure/Security/DpapiSecretStore.cs` | Implemented | |
| `pol.Repository`, `pol.RepositoryVersion`, `pol.Artifact` | `PolicyArtifact`, scanner, SQLite adapter | `src/EngineeringWorkspace.Infrastructure/Repositories/FilePolicyRepositoryScanner.cs`, `.../Persistence/SQLiteEaasPersistence.cs` | Partially Implemented | Ingestion into the v3 tables is M2. |
| `cust.Exception` | `CustomerException` | `src/EngineeringWorkspace.Domain/Customers/CustomerProfile.cs` | Partially Implemented | Domain model only; Exceptions page is a placeholder (M2). |
| `deploy.ChangeRequest` | `ChangeRequest` | `src/EngineeringWorkspace.Domain/Changes/ChangeRequest.cs` | Partially Implemented | Domain model only (M7). |
| `audit.ApprovedBaseline`, `ComparisonRule`, `Drift` | `GoldenTenantExpectation`, `ComparisonStatus`, `IGoldenTenantComparisonService` | `src/EngineeringWorkspace.Domain/GoldenTenant/`, `src/EngineeringWorkspace.Application/GoldenTenant/` | Deferred | Interfaces only (M5). |
| Questionnaire (`eux.*`, `cust.FormSubmission`, `cust.QuestionnaireInstance`) | — | — | Deferred | M4. |
| Specification / SOW (`spec.*`, `dlv.*`) | `IScriptGenerator` and related ports | `src/EngineeringWorkspace.Application/Scripts/ScriptPorts.cs` | Deferred | M6. |
| Deployment (`deploy.*`) | — | — | Deferred | M7. |
| ConnectWise (`psa.*`) | `IConnectWiseIntegrationService` | `src/EngineeringWorkspace.Application/Integrations/IntegrationPorts.cs` | Deferred | M8. |
| Row-level security (`sec.*`) | — | — | Deferred | Role-based authorization is out of scope. |
| SQLite provider | SQLite connection, schema and adapter | `.../Database/DatabaseConnectionFactory.cs`, `.../Schema/Sqlite/` | Not Applicable | SECURE PLUS EXTENSION for offline development; v3 specifies SQL Server/Azure SQL. |

No row is marked Implemented without working code; live-environment behaviour is listed in the testing handoff.
