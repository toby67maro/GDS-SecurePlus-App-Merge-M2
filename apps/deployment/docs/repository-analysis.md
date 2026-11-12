# Repository analysis

## Sources inspected

- `toby67maro/GDS-ModernWork-SecurePlus-App`: initially contained only a README and first commit.
- `toby67maro/Documents`: authoritative documentation repository; inspected its README, questionnaire documentation/schema, and `ModernWork_SecurePlus_Baselines` tree.
- `toby67maro/GDS-ModernWork-SecurePlus-PS`: companion scripts and customer-variable examples.
- `toby67maro/GDS-ConditionalAccess-Baseline`: related deployment tooling and baseline documentation.
- `toby67maro/SecurePlus-ModernWork`: repository was accessible but contained no implementation files beyond its README at inspection time.

## Relevant content found

`Documents/ModernWork_SecurePlus_Baselines` contains `PolicyRepository`, `CustomerVariables`, `PolicySets`, `Scripts`, and documentation-oriented content. The policy repository contains JSON artifacts grouped by technology and policy area. Companion PowerShell deployment helpers are present in the related PS repository. The Documents repository also contains the EaaS questionnaire, question bank, customer configuration JSON schema, sample configuration, service/solution workbooks, and framework/control references.

## Schemas and conventions

The EaaS customer configuration schema is JSON Schema draft 2020-12. Its required top-level values include `schemaVersion`, `generatedAt`, `selection`, `skipped`, and `customerVariables`. Customer-variable keys are designed to feed the policy-set deployment tooling. Policy files are JSON and are not assumed to share one universal schema; artifact identifiers and display names must therefore be inferred conservatively and unknown properties preserved.

## Relationships

Questionnaire results produce customer configuration and policy selection data. Customer variables and policy-name overrides are consumed by deployment scripts. Policy sets reference policy artifacts, while controls and solutions are represented in the broader Documents catalog/workbook material. JSON artifacts may have related PowerShell files; the application foundation associates likely companions by filename/directory but never executes them during scanning.

## Ambiguities and assumptions

- The GitHub repositories do not expose the original local Windows paths from the request; repository paths are therefore configuration-driven.
- Some workbook and office-document semantics could not be fully extracted in this environment. Their existence is recorded, but no unsupported content has been fabricated.
- The initial application uses safe, generic JSON identity extraction (`id`, `displayName`, `name`) and reports malformed JSON rather than assuming a policy schema.
- Theme colors now live in `ApplicationProperties.json`; the built-in GDS palette is derived from colors in GDS-authored Visio and Excel files (see `docs/architecture/theme-system.md`). getgds.com was not reachable from the build environment and no approved brand guide was found.

## Inaccessible or deferred content

No external tenant, database, secret store, or PowerShell execution environment was accessed. The scanner records companion scripts without executing them. Full workbook, PDF, Visio, and Office semantic extraction remains a future repository-indexing milestone.
