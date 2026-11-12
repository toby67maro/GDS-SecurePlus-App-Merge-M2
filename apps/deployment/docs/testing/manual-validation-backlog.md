# Manual validation backlog

The cloud development environment verifies provider-neutral logic and SQLite behavior. The items below require infrastructure or platform capabilities unavailable here.

## WINDOWS WORKSTATION REQUIRED

| Feature | Current automated-test status | External requirement | Exact future validation procedure | Expected result | Failure indicators |
|---|---|---|---|---|---|
| DPAPI CurrentUser protection | Cross-platform storage contracts and metadata tests pass; Windows DPAPI execution is not run here | Windows user profile | Store, replace, resolve, and delete a credential through Credential Management; inspect `%LOCALAPPDATA%\Global Data Systems\Secure Plus - Modern Work\data\Credentials\secrets` | Secret file is DPAPI-protected, replacement is atomic, deletion removes it, and plaintext is absent | `PlatformNotSupportedException`, plaintext file contents, stale secret after delete |
| WPF database and tenant workflows | Windows-target build/publish pass; UI launch is not possible here | Windows desktop session | Open each requested route and execute the documented SQLite workflow | No UI thread blocking; commands and confirmation states match database status | Raw stack trace, combined destructive action, secret redisplay |

## SQL SERVER REQUIRED

| Feature | Current automated-test status | External requirement | Exact future validation procedure | Expected result | Failure indicators |
|---|---|---|---|---|---|
| SQL Server connection and authentication | Provider implementation builds; optional test skips without `SECUREPLUS_SQLSERVER_CONNECTION_STRING` | Reachable SQL Server and approved Windows/SQL login | Configure SQL Server, run Test Connection and Check Database, then inspect sanitized status | Server/authentication/database states are distinct and no password is logged | Login failure reported as connected, password in logs, timeout misclassified |
| SQL Server database creation and schema | SQLite schema and transaction tests pass; SQL Server live test pending | `CREATE DATABASE` and DDL permission | Check a missing database, confirm Create Database, initialize, validate, and rerun validation | Database is created without dropping existing objects; schema is current | Unauthorized creation, destructive overwrite, incompatible marker accepted |

## AZURE SQL REQUIRED

| Feature | Current automated-test status | External requirement | Exact future validation procedure | Expected result | Failure indicators |
|---|---|---|---|---|---|
| Azure SQL SQL-auth connection | Provider implementation builds; optional test skips without `SECUREPLUS_AZURESQL_CONNECTION_STRING` | Reachable Azure SQL logical server and SQL login | Configure Azure SQL with encryption enabled, run Test Connection and Check Database | Connection succeeds with certificate validation and correct state reporting | Certificate bypass required unexpectedly, wrong endpoint, leaked connection details |
| Azure SQL schema permissions | SQLite DDL and FK tests pass; live DDL pending | Database DDL permission | Run Validate Schema, Initialize Schema, and rerun Validate Schema | Initialization is transactional and repeatable | Partial schema, permission failure hidden, newer schema downgraded |

## MICROSOFT 365 TEST TENANT REQUIRED

| Feature | Current automated-test status | External requirement | Exact future validation procedure | Expected result | Failure indicators |
|---|---|---|---|---|---|
| Client-credential authentication | Service logic is test-double ready; no live tenant is contacted | Test tenant, app ID, secret, and consented Graph permission | Configure customer tenant and secret reference, save secret securely, run Test Connection | Authentication succeeds without token display or persistence | Secret redisplayed, token logged, wrong cloud authority |
| Graph organization and tenant identity validation | Response parsing and identity comparison are implemented; live validation pending | Graph-reachable tenant and `Organization.Read.All` application permission | Run tenant validation and compare returned tenant to configured tenant ID | Graph is reachable and tenant identity is valid | Authentication marked connected when Graph or tenant identity fails |

## INTERACTIVE ADMINISTRATOR REQUIRED

| Feature | Current automated-test status | External requirement | Exact future validation procedure | Expected result | Failure indicators |
|---|---|---|---|---|---|
| App registration creation, service principal, permissions, and admin consent | Implemented as real Graph calls, one confirmed step at a time; not executed without an administrator session | Privileged interactive Entra administrator session | Set [Integrations:Microsoft] AdministratorClientId, then run each assistant step on Tenant App Registration | Each creation/consent step reports real API results and stores metadata only | UI claims creation without API result, automatic privileged consent, secret in configuration |
