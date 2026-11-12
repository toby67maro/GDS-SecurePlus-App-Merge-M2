# Control catalog in the application database

Secure Plus Agent ships the control catalog embedded in the application (`catalog.json.gz`, generated from the GDS
Documents repository). Since version 1.3 the catalog is also written into the application database (SQL Server,
Azure SQL or SQLite) so it can be queried, reported on and joined to assessment results.

## When it is loaded

- When the main window opens and the database does not hold the embedded catalog (new installation, upgrade to a
  version with a newer catalog, or a database restored from elsewhere).
- When the database becomes ready (setup, schema upgrade or repair).
- On demand: **Dashboard > Catalog > Load into database** reloads it.

Loading runs as the background job "Load the catalog into the database" in the activity panel. It is one
transaction: either the whole catalog is written or nothing changes. It is idempotent: the version is identified by
the SHA-256 of the embedded catalog (`pol.RepositoryVersion.ManifestHash`), an unchanged catalog is skipped, and a
reload replaces the previous version without duplicating rows.

Upgrading from 1.2 adds schema version 5 (`V0005__secureplus_catalog.sql`) automatically at the next start.

## Where the data goes

| Table | Content |
|---|---|
| `pol.Repository` | One row, `Code = 'SECUREPLUS-CATALOG'` |
| `pol.RepositoryVersion` | One row per catalog version; the loaded one has `StatusCode = 'Current'` and the catalog hash in `ManifestHash`. A previous version is deleted, or kept as `Superseded` when other data references it |
| `pol.PolicyType` | One row per control kind (`ConditionalAccess`, `EndpointSecurity`, `Intune`, `AssessmentCheck`, `Standard`, `MicrosoftBaseline`) |
| `pol.Category` | One row per kind and category (`Code` like `SP-INT-Device-Restrictions`) |
| `pol.Artifact` | One row per control (3,238 since 2.0; 3,268 in 1.2 before duplicates became aliases): name, value, platform, licence text, description, the Microsoft Graph body in `ContentJson`. `Code` is the control ID, shortened with a hash suffix above 30 characters |
| `pol.ArtifactSetting` | Expected settings: settings catalog definition ID or Graph property in `SettingDefinitionId`/`JsonPath`, the JSON value in `ValueRaw` |
| `pol.PolicySet` | The baselines (00-Foundation ... 08-Endpoint-Attack-Surface-Reduction, All-Policies, Golden-Tenant; Golden-Tenant has `IsApprovedBaseline = 1`) |
| `pol.PolicySetMember` | Controls of each policy set, in order |
| `pol.PolicySetInclude` | Composition (Golden-Tenant includes 00-08) |
| `pol.PolicySetPrerequisite` | Deployment prerequisites of each policy set |
| `fw.Framework`, `fw.FrameworkVersion`, `fw.Requirement` | The frameworks the catalog references (NIST 800-53 Rev 5, CIS Controls v8.1, ISO 27001:2022, CMMC 2.0, PCI DSS 4.0.1, HIPAA, MITRE ATT&CK, Microsoft Security Baselines). Requirements hold codes and titles only (`IsStub = 1`) |
| `pol.ArtifactMapping` | Control-to-requirement mappings, source `ref.MappingSource` `GDS-CATALOG`. `ConfidenceCode = 'Indicative'` for the GDS keyword crosswalk, `'High'` for the Microsoft baselines |
| `gds.Standard`, `gds.StandardItem` | `SP-STANDARDS` (Secure Plus Standards Baseline), `MS-SECURITY-BASELINES` and `SP-ASSESSMENT-CHECKS`, with phase, task, target configuration, validation, evidence, rollback and owner columns |
| `secureplus.CatalogControl` | The flat catalog (what the Catalog page shows): full control ID, `ArtifactId`, kind, category, platform, setting, value, frameworks, severity, user impact, experience, pain points, licensing, evaluation, policy sets |

Existing reference rows (policy types, categories, frameworks, requirements, mapping source, standards) are reused by
their natural keys, so data you add to those tables is kept.

## Example queries (SQL Server)

```sql
-- The catalog as a table, filtered like the Catalog page
SELECT ControlId, KindName, Category, Name, Value, Severity, UserImpact, Experience, PainPoints, Frameworks, PolicySets
FROM secureplus.CatalogControl
WHERE Kind = 'Intune' AND PainPoints IS NOT NULL;

-- Controls of the Golden Tenant baseline (including the sets it composes)
SELECT ps.Code AS PolicySet, a.Code, a.DisplayNameTemplate AS Name
FROM pol.PolicySet ps
JOIN pol.PolicySetInclude i ON i.ParentPolicySetId = ps.PolicySetId
JOIN pol.PolicySetMember m ON m.PolicySetId = i.ChildPolicySetId
JOIN pol.Artifact a ON a.ArtifactId = m.ArtifactId
WHERE ps.Code = 'Golden-Tenant';

-- Controls mapped to a framework requirement
SELECT f.Code AS Framework, r.RequirementCode, c.ControlId, c.Name
FROM pol.ArtifactMapping am
JOIN fw.Requirement r ON r.RequirementId = am.RequirementId
JOIN fw.FrameworkVersion v ON v.FrameworkVersionId = r.FrameworkVersionId
JOIN fw.Framework f ON f.FrameworkId = v.FrameworkId
JOIN secureplus.CatalogControl c ON c.ArtifactId = am.ArtifactId
WHERE f.Code = 'NIST-800-53' AND r.RequirementCode = 'AC-4';

-- Latest assessment results joined to the catalog
SELECT s.CustomerKey, c.KindName, c.Name, r.Result, r.Severity, c.Experience, r.Evidence
FROM secureplus.AssessmentResult r
JOIN secureplus.AssessmentStatus s ON s.AssessmentRunId = r.AssessmentRunId
JOIN secureplus.CatalogControl c ON c.ControlId = r.ControlId
WHERE r.Result = 'Fail';
```

On SQLite the tables are named `"schema.Table"`, for example `SELECT * FROM "secureplus.CatalogControl";`.
