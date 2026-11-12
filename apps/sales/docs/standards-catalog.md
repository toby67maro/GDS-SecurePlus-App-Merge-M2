# Standards catalog ingestion

The deployment workspace now contains a versioned, additive import from the Cloud Services Standards library. The source workbook is treated as WIP, not as approved customer configuration.

## Imported sources

`server\ArchiTech.Api\Data\standards\` contains generated, versioned data files:

- `secure-plus-discovery-decisions-v1.json`: 474 discovery decisions from `SecurePlusStandards.xlsx` / `Discovery_Decisions`.
- `secure-plus-wbs-v1.json`: 193 work items from `WBS_WIP`.
- `secure-plus-baseline-settings-v1.json`: 532 proposed settings from `Standards_Baseline_WIP`.
- `target-model-v2-reference.csv`: 1,206 column definitions across 149 tables from `GDS_ComplianceIQ_Data_Dictionary_v2.csv`.

The first three datasets use catalog version `SecurePlus-WIP-1`. The model dictionary uses `ComplianceIQ-DataDictionary-v2`. Source IDs and composite version keys are preserved so later revisions can be added without overwriting prior catalog versions or engagement responses. `StandardsCatalogSeeder` validates expected row counts and seeds these records only into empty catalog tables; a partial population fails loudly and must be reconciled before startup continues.

The data dictionary is stored in normalized reference tables (`TargetModelTables`, `TargetModelColumns`) and exposed as a searchable design reference. It is not the deployed application schema and does not create its 149 proposed tables. The current EF Core model remains the operational data model.

## Application behavior and boundaries

- The Sales intake remains the shorter, existing form and continues to calculate from the separate 95-row WBS estimate catalog.
- Full discovery is available only under `/api/deployment/*`. Deployment roles can read it; deployment engineers/admins can save decisions in separate per-engagement or per-profile logs. Profile logs work without a sales engagement, and answers are not copied or merged between logs. Sales-only workspace exports omit the catalog and discovery answers.
- Charts and searchable read-only lists use the imported WBS and settings data. These WIP records do not change sales estimates or the existing 371-control assessment catalog.
- Baseline settings are display-only. The import does not add Graph access, tenant write actions, script execution, worker infrastructure, or an approval bypass.
- The full-discovery view includes an assessment crosswalk for human review. Workbook references such as `GBL-P0-PKO-001` can be matched to baseline IDs such as `GBL-P0-T1-PKO-001` by omitting the task segment; these are explicitly labeled format candidates, not verified equivalences. Exact baseline references are labeled separately. A matching control ID is not proof of equivalent scope or valid evidence, and unresolved references remain visible. Neither the crosswalk nor discovery answers automatically changes assessment findings.
- Deployment Engineers and Administrators can create a customer profile directly from the deployment workspace without a Sales intake. This creates only a customer and empty deployment profile; it does not create an engagement, estimate, Planner handoff, or tenant connection. Existing normalized customer-name matches are rejected to prevent duplicate profiles.
- A deployment-only source register inventories library materials by area and review status. It identifies sources actually imported, source artifacts awaiting review, items not locally staged, and restricted content without copying their full contents into the app.
- Deployment workspace JSON exports use schema version 9 and include the versioned standards data, independent engagement and profile discovery responses, source register, and persisted app-registration checklist progress. Sales exports continue to omit deployment-only data.

## Provenance and licensing

The SharePoint workbook and data dictionary remain internal source artifacts; the repository contains only the extracted catalog sheets/reference rows needed by the app. Framework documents and CIS benchmark text are not embedded in the application. The source research recommends using CIS identifiers and links with GDS-authored summaries until commercial-use rights are confirmed. The source SharePoint link is available from the deployment catalog UI.

## Additional library inventory

The following items were present in the linked [Cloud Services Standards library](https://getgds.sharepoint.com/:f:/r/teams/cloudservices/Shared%20Documents/General/Cloud%20Services%20Standards?d=w861fcd5976834014b82ef037857502c4&csf=1&web=1&e=xFVeSo) when inventoried. Their contents are not promoted into the operational assessment, sales-estimate, or executable-settings catalogs by this release.

- Root materials: `Conditional Access Standard`, `Customer_Deployment_Checklist_v2.0_Marquie.xlsx`, `GDS - M365 Secure Plus - Team Module Guide.docx`, `GDS Baseline Settings Map - WIP - TB.xlsx`, `GDS_SOPAC-CA-Policies_2026-10-27.xlsx`, `M365_Secure_Configuration_Baseline_Entra_ID.docx`, and `M365_Services_Solutions_WIP_TB.xlsx`. `SecurePlusStandards.xlsx` is the imported workbook described above.
- EaaS materials: `GDS_ComplianceIQ_Data_Dictionary_v2.csv` is imported as a design reference. The database design preview, Visio diagrams, Secure Plus application model, and control-attribute source research remain reference artifacts, not live schema or assessed control results.
- Frameworks: CIS Controls 8.1.2, CMMC 1.0/2.0 and ITAR crosswalks, CSF 2.0, HIPAA, ISO 27001:2022, MITRE ATT&CK, NIST SP 800-53 exports, PCI DSS 4.0.1, Secure Controls Framework 2026, and SOX ITGC materials. The CIS Benchmarks folder and its benchmark text are excluded pending commercial-use rights review.
- Microsoft Security Baselines: Microsoft 365 Apps for Enterprise 2512, Windows 11 v26H2 Security Baseline v2, and Windows Server 2025 Security Baseline 2602. These remain source references; their settings are not tenant-ready deployment actions.
- `OLD` is an archive and is not imported.

Before any of these materials become an application catalog, reconcile the source version and owner, intended customer applicability, overlap and crosswalk quality, WIP status, and license/redistribution terms. Keep external framework text out of customer-facing or commercial features unless the relevant rights are confirmed. New catalogs must be versioned and separate from the current sales WBS and 371-control assessment catalog until reviewed.

The app's 36-row source register is a provenance inventory, not a claim that every file's content has been extracted. The only content imports represented there are the 474 discovery decisions, 193 WIP work items, 532 proposed settings, and 1,206 data-dictionary columns described above. All other items remain metadata-only pending source validation and licensing review.

Per the user's direction, remaining source content stays metadata-only until reuse rights, source provenance, and data sensitivity are verified.

## Database update

SQL Server deployments apply the generated `SecurePlusStandardsCatalog` migration through EF Core migrations. SQLite local startup creates the same additive tables through `EnsureCreated` plus `CREATE TABLE IF NOT EXISTS` statements, then seeds the catalog. Existing customer and engagement data is not re-created or overwritten by this import.
