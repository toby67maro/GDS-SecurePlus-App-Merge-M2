# Architecture

```
Salesman browser ──► apps/sales (React + ASP.NET Core)  ──► Sales DB  (Engagements, Customers, ...)
Management browser ─►   /api/management/*  (Management | DeploymentAdmin only)
                            │  DeploymentBridge (usp_SalesUpsertCustomer, optimistic concurrency)
                            ▼
                     Bridge tables/views (database/bridge/B0001__sales_bridge.sql)
                            ▼
Engineer ──► apps/deployment (WPF, unchanged) ──► Secure Plus customer tables (EaaS v3)
```

## Table mapping
| Bridge object | Purpose |
|---|---|
| `SalesCustomerMap` | sales `CustomerId` -> Secure Plus `CustomerKey` |
| `SalesEngagement` | sales `EngagementId` -> customer, owner, status, timestamps |
| `usp_SalesUpsertCustomer` | merge sales fields into `ProfileJson` (errors 51000/51001/51002) |
| `vw_SalesEngagementOverview` | management reporting (sync + assessment status/score) |
| `vw_CustomerAssessmentSummary` | per-customer assessment rollup for the engagement report |

Merged into `ProfileJson`: contact (if email absent), tag `sales-intake`, `organization.salesLead`/`industryKey`/`industryOther`,
`experienceAnswers`, `userGroups` (by name, max 25). Tenants and engineer edits are preserved.

## Roles (Entra groups under `Authorization:Groups`)
| Role | Access |
|---|---|
| `Sales` | own engagements only |
| `Management` | all engagements (`/api/management/*`, "All engagements" nav item) |
| `DeploymentEngineer/Approver/Operator` | deployment workspace (unchanged) |
| `DeploymentAdmin` | everything |

Engagements with no owner (pre-merge data) are visible only to Management/Admin.

## Sync behaviour
Submission saves first, then syncs. Failure -> `DeploymentSyncStatus=Failed` + message; a hosted worker retries Pending/Failed
every `Bridge:ReconcileIntervalSeconds`. Management can force "Resync".

## Safety boundaries
Tenant writes disabled; Planner trigger paused until rotated; no secrets, customer data or trigger URLs in git.
