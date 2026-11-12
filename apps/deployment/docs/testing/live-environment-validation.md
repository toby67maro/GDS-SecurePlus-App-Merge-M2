# Live environment validation

This procedure validates the current foundation against real infrastructure. Do not place real secrets in `config/`, source files, exports, or issue comments.

For items not currently executable in the cloud environment, use `docs/testing/manual-validation-backlog.md`.

## Part A - SQL Server

### Prerequisites

- Windows workstation running the published application.
- SQL Server or Azure SQL endpoint, database administrator or delegated database-creation permission, and network access.
- For Windows authentication: the signed-in Windows account must have the required SQL permissions.
- For SQL authentication: a SQL login and password; save the password through **Workspace > Settings > Credential Management** using a reference such as `os:sql/contoso`.

### Procedure

1. Open **Workspace > Settings > Database Configuration**.
2. Select `SQL Server` or `Azure SQL`.
3. Enter server, optional instance/port, and the intended database name. Use only a normal SQL identifier; do not include SQL statements.
4. Select `WindowsIntegrated`, `SqlLogin` or an Entra method. For SQL login, enter the user name, type the password in the password box and select **Store password**; only its secret reference is saved.
5. Set Encrypt and Trust Server Certificate according to the server certificate policy. Prefer encryption with certificate validation.
6. Select **Test Connection** to test the server (master) connection and sign-in. Select **Check Database** to check that the database exists and can be opened and to read the schema version.
7. If missing, select **Create Database** and confirm the displayed server, database, and authentication method.
8. Select **Validate Schema**. If the schema is missing, select **Initialize Schema**.

Expected overall states are `Not Configured`, `Disconnected`, `Connection Failed`, `Database Missing`, `Schema Missing`, `Migration Required`, `Incompatible Schema` or `Ready`. Initialize Schema applies the generated EaaS v3 baseline (204 tables) and the Secure Plus extension tables in one transaction and records them in `secureplus.SchemaVersion` (see `docs/architecture/eaas-v3-database-analysis.md`).

Verify in SSMS with read-only inspection:

```sql
SELECT name FROM sys.databases WHERE name = N'<database-name>';
USE [<database-name>];
SELECT * FROM [secureplus].[SchemaVersion];
SELECT COUNT(*) FROM sys.tables;
```

Common errors: server unreachable (network/firewall/instance), login failed (credential or authentication mismatch), database missing/access denied (database or permissions), create denied (CREATE DATABASE permission), schema denied (DDL permission), and encryption certificate failure (certificate trust configuration).

## Part B - Microsoft 365 test tenant

### Prerequisites

- Customer tenant ID, tenant domain, client/application ID, and a client secret created for that app registration.
- The app registration must have the required Microsoft Graph application permission for the operation being tested and admin consent.
- Use a dedicated test credential with an expiration date. Do not use a production secret for first validation.

### Procedure

1. Open **Workspace > Customer Profiles > Tenant App Registration**.
2. Create or select the customer, then enter the tenant display name, tenant ID, primary domain, cloud environment and client ID.
3. Enter the client secret in the password field and select **Save**. The secret is stored with DPAPI as `os:customer/<customer>/<tenant>/graph-client-secret` and the field is cleared.
4. Select **Test Connection**.
5. Confirm the result distinguishes authentication from Graph permission validation. A valid token with insufficient Graph permission is not a connected/ready result.

Expected success is `Connected`: MSAL client-credential token, `GET /organization` through the Microsoft Graph SDK, tenant identity match and `Organization.Read.All` (or a higher permission) in the token's roles. The app-registration assistant creates applications, service principals, permissions, consent and secrets only one confirmed step at a time with a real administrator sign-in.

Common errors: invalid client secret, expired secret, wrong tenant authority, missing admin consent, insufficient `Organization.Read.All`, wrong national-cloud endpoint, or blocked outbound HTTPS.

## Part C - Security validation

Inspect these locations after testing:

- `config/*.ini` and `config/Environments/*.ini`
- `%LOCALAPPDATA%\Global Data Systems\Secure Plus - Modern Work\data\Credentials\secrets\`
- `%LOCALAPPDATA%\Global Data Systems\Secure Plus - Modern Work\data\Credentials\metadata\`
- logs and audit under `%LOCALAPPDATA%\Global Data Systems\Secure Plus - Modern Work\data\Logs\` and `data\Audit\`
- customer and workspace files under `data\Customers\` and `data\Workspaces\`
- `git grep -n -i 'ClientSecret=\|Password=\|access_token\|refresh_token'`

Secret files are DPAPI-protected for the current Windows user. Metadata files contain identifiers and references only. The application does not provide a “show secret” operation. Confirm that exports, logs, diagnostics, and audit records contain no secret, token, password, or authorization header.
