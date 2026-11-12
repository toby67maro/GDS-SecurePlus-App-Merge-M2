# GDS Secure Plus Platform

Two connected-but-separate apps on the same SQL Server:

| App | Users | Path |
|---|---|---|
| **Sales intake** (ex Archi-Tech 3.0: React + ASP.NET Core) | Salesmen (own engagements), Management (all) | `apps/sales` |
| **Deployment** (GDS Modern Work Secure Plus, WPF, unchanged) | Engineers | `apps/deployment` |
| **Bridge** (SQL tables, proc, views) | both | `database/bridge` |

## How they connect
- Submitting or updating an engagement in the sales app calls `usp_SalesUpsertCustomer`, which merges sales data
  (contacts, industry, user groups, experience answers, `sales-intake` tag) into the deployment customer `ProfileJson`
  with optimistic concurrency. Engineer-owned data is preserved.
- `SalesCustomerMap` / `SalesEngagement` map sales ids to deployment customer keys.
- Failures never block a submission; status is stored (`Pending`/`Synced`/`Failed`) and a background worker retries.
- The sales app reuses the deployment `EngineeringWorkspace.Application` library for the experience catalog and scoring
  (`/api/experience/*`), so reports match the engineer app.
- Views `vw_SalesEngagementOverview` and `vw_CustomerAssessmentSummary` feed reporting.

## Access
- `Sales`: only engagements they own (`OwnerId`).
- `Management` / `DeploymentAdmin`: everything, via `/api/management/*` (hidden dashboard).
- Entra groups map to roles under `Authorization:Groups` (Sales, Management, Deployment..., Administrators).

## Configuration
Set via host secrets, never committed: `ConnectionStrings:Default`, `Authentication:*`,
`Bridge:Enabled`, `Bridge:ConnectionString`, `Bridge:ApplySchema`.
Planner/Power Automate/SharePoint settings are unchanged from Archi-Tech (see `apps/sales/PROJECT-HANDOFF.md`).

## Build & test
```
dotnet test apps/sales/server/ArchiTech.Tests
cd apps/sales/web && npm install && npm run build
```
Status: the bridge SQL (`database/bridge/B0001__sales_bridge.sql`) is untested against a real SQL Server — run it in a
non-production database first. Safety boundaries (tenant writes disabled, Planner paused) are unchanged.
# GDS-SecurePlus-App-Merge-M2
