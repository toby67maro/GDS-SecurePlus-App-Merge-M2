# EaaS v3 database analysis

What the EaaS v3 design actually defines, and how Secure Plus - Modern Work initializes it.

## Sources inspected

The whole `EaaS/` folder of [toby67maro/Documents](https://github.com/toby67maro/Documents/tree/f10e83b06f6a285365b60c1bc670a48c91e28b48/EaaS) at commit `f10e83b`:

| File | Version | Status in this analysis |
|---|---|---|
| `GDS_ComplianceIQ_Data_Dictionary_v3.csv` | v3 (current) | **Authoritative.** Read completely: 1,642 column rows, 207 table entries (204 live + 3 marked `Removed`). Copied verbatim to `schema/eaas-v3/` (SHA-256 recorded in `schema/eaas-v3/SOURCE.md`). |
| `GDS_ComplianceIQ_Database_Design_v3.vsdx` | v3 (current) | Read (page text extracted from the Visio XML). States the platform, totals, key conventions and v3 changes. |
| `GDS_ComplianceIQ_Database_Design_v3_preview.pdf` | v3 | **Not readable**: the file is protected with Microsoft Azure Information Protection. Nothing from it is used. |
| `GDS_ComplianceIQ_Data_Dictionary_v2.csv`, `..._v2_preview.pdf`, `GDS_ComplianceIQ_Database_Design_v2 (1).vsdx` | v2 (superseded) | Used only to confirm what v3 changed. |
| `EaaS_Workflow_and_Architecture.vsdx` (and `(1)` copy), `EaaS_Workflow_and_Architecture_preview.pdf` | current | Visio text read (identity model, data stores, engines). |
| `Secure Plus Application Model.vsdm` | current | Visio text read (application menu and engagement lifecycle). |
| `GDS Structure.vsdx` | current | Visio text read (service tiers); colors used for the GDS theme. |
| `M365_Azure_Config_as_Code_Gap_Report.md`, `M365_Azure_Config_Method_Matrix.csv`, `control-attribute-data-sources-report.md` | current | Read for deployment-method context; they define no tables. |
| `Questionnaire/*` (schema, question bank, forms, samples, converter) | current | Read for the questionnaire model; they define JSON, not SQL. |
| `EaaS_Executive_Briefing.pptx`, `EaaS_Program_Roadmap_and_Plan.xlsx`, `GDS_EaaS_Rate_Card_DRAFT.xlsx`, `ModernWork_SecurePlus_*.xlsx` | — | Not used for the schema (business content; the `.pptx` is AIP-protected). |

No SQL scripts, DACPACs, migrations or initialization mechanism exist anywhere in the Documents repository, so none could be reused.

## What v3 defines

**Platform.** "One SQL Server 2022 / Azure SQL database · 18 schemas · 204 tables · 1,639 columns · 79 staging tables (stg) · 21 reporting views (rpt)" (Database Design v3, page 1). The architecture diagram names the database `GDS_ComplianceIQ` on Azure SQL. SQL Server 2022 / Azure SQL is therefore the EaaS v3 provider; SQLite is a Secure Plus extension.

**Schemas and tables (from the dictionary).** `cust` 26, `pol` 23, `dlv` 20, `fw` 17, `cfg` 17, `kb` 13 live (16 entries, 3 removed), `threat` 12, `eux` 12, `ref` 11, `spec` 9, `deploy` 8, `score` 7, `svc` 7, `audit` 6, `src` 5, `gds` 4, `sec` 4, `psa` 3. The dictionary gives 1,642 rows; the design page says 1,639 columns — the difference is the three `Removed` placeholder rows.

**Keys.** Every table has a primary key (53 composite). The dictionary marks 448 foreign keys, each pointing at a table with a single-column primary key, and natural-key columns (`IsUniqueKeyPart`) in 94 tables. The design states the conventions: "surrogate keys, natural keys UNIQUE, temporal history on specs, artifacts, runs and drift".

**Versioning / history.** 17 tables carry `TemporalHistory = Y` (for example `pol.Artifact`, `spec.Specification`, `deploy.DeploymentRun`, `audit.Drift`, `cust.Finding`).

**v3 changes from v2.** New `pol` (23), `spec` (9), `deploy` (8), `audit` (6), `psa` (3) schemas; six new `cust` and three new `eux` tables; seven v2 tables gain `ArtifactId → pol.Artifact`; `kb.PolicyTemplate`, `kb.PolicyTemplateSetting` and `kb.ConfigurationChangePolicyTemplate` are removed.

**Tenant and credential data.** `cust.Tenant` holds customer, tenant GUID, primary domain and cloud code. `audit.TenantConnection` holds non-secret connection metadata (app role, app ID, certificate thumbprint and expiry, consent status, GDAP relationship, last verification), unique per tenant and app role. The architecture states "App-only, certificate from Key Vault" and two multi-tenant apps (Collector: read; Deployer: write). No table stores a secret, password, token or private key.

**Initialization.** v3 defines structure only. It does not define nullability (other than primary keys), defaults, identity generation, check constraints, seed data, the 79 staging tables, the 21 reporting views, or any migration/upgrade mechanism.

## How Secure Plus initializes it

`build/scripts/eaas-schema/generate_eaas_v3_schema.py` reads the v3 dictionary and writes:

- `src/EngineeringWorkspace.Infrastructure/Persistence/Schema/SqlServer/V0001__eaas_v3_baseline.sql`
- `src/EngineeringWorkspace.Infrastructure/Persistence/Schema/Sqlite/V0001__eaas_v3_baseline.sql`
- `src/EngineeringWorkspace.Infrastructure/Persistence/Schema/eaas-v3-schema-manifest.json` (expected tables and columns, used by Validate Schema)

Derivation rules (each is the most literal reading of the dictionary; nothing is invented):

| v3 statement | Generated as |
|---|---|
| Schema / Table / ColumnName / DataType | `CREATE SCHEMA` (18) and `CREATE TABLE` with the exact names and SQL Server types. |
| `IsPrimaryKey` | Primary-key constraint; those columns are `NOT NULL`. All other columns are `NULL` because v3 records no other nullability. |
| "surrogate keys" | A single-column integer primary key that is not also a foreign key is `IDENTITY(1,1)` (SQLite: `INTEGER PRIMARY KEY`). |
| `IsUniqueKeyPart` ("natural keys UNIQUE") | One natural-key unique index per table. Because the columns are nullable, SQL Server uses a filtered unique index (`WHERE … IS NOT NULL`); SQLite's `UNIQUE` already ignores NULLs. |
| `IsIndexed` | Non-unique index per column. |
| `IsForeignKey` / `ForeignKeyTarget` | Foreign key to the target's single-column primary key, added after all tables exist. |
| `TemporalHistory = Y` | SQL Server system-versioned temporal table with hidden `ValidFrom`/`ValidTo` period columns and a `<Table>History` table. Not available in SQLite. |
| `V3Change = Removed` | Not created. |

Every generated statement is guarded (`IF SCHEMA_ID(...) IS NULL`, `IF OBJECT_ID(...) IS NULL`, `IF NOT EXISTS`, `CREATE TABLE IF NOT EXISTS`), so the script never drops, alters or overwrites an existing object.

**Conflict found in v3.** `cust.QuestionnaireInstance.QuestionSetId` is `smallint` but references `eux.QuestionSet.QuestionSetId`, which is `tinyint`. SQL Server cannot create a foreign key between different types, so that single constraint is skipped and recorded in the manifest (`foreignKeyConflicts`). The column keeps its v3 type. The design owner should correct one of the two types in a future dictionary version.

## Versioning, repeatability and safety

- Migrations are embedded resources applied in order: `V0001` (EaaS v3 baseline), `V0002` (Secure Plus extension tables). The required schema version is 2.
- `secureplus.SchemaVersion` (Version, Description, SHA-256 checksum, AppliedAtUtc, AppliedBy, ApplicationVersion) records each applied migration. It is created inside the same transaction as the first migration.
- All pending migrations run in **one transaction**; on SQL Server an exclusive `sp_getapplock` prevents two workstations from initializing at once. Any error rolls everything back, so a failed run leaves no partial schema.
- Before initializing, the database is classified read-only:
  - no Secure Plus objects → **Schema Missing** (Initialize Schema offered);
  - version table present, older version → **Migration Required**;
  - same version → **Ready** (Validate Schema additionally checks every expected table and column from the manifest and compares migration checksums);
  - newer version → **"Database schema is newer than this application supports."** and nothing is changed;
  - EaaS-named tables without a version table, or the pre-release `__secureplus_schema` marker → **Incompatible Schema** ("not initialized by this application"); nothing is changed.

## SECURE PLUS EXTENSION objects

| Object | Why it exists |
|---|---|
| `secureplus.SchemaVersion` | v3 has no migration history; required for versioned, repeatable initialization. |
| `secureplus.Workspace` | Engineer workspaces are an application concept with no v3 table. |
| `secureplus.CredentialMetadata` | Credential identity and expiry (never the secret). v3 has no credential table; `audit.TenantConnection` covers only certificate metadata. |
| `secureplus.AuditEvent` | Application audit trail; v3 `audit.*` tables model tenant snapshots and drift, not user actions. |
| SQLite provider | v3 specifies SQL Server 2022 / Azure SQL. SQLite gives engineers an offline local database with the same v3 table and column names (`"schema.Table"` naming because SQLite has no schemas). |

## Gaps (deferred)

- Nullability, defaults, check constraints and seed/reference data are not in v3 and are not invented.
- The 79 `stg` staging tables and 21 `rpt` views are named in the design but not defined in the dictionary.
- The application does not yet read or write most v3 tables; current persistence uses the file stores (workspace, customer, credential metadata) and the SQLite adapter for the core slice (M2).
