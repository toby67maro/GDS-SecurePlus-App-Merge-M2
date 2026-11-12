# Secure Plus Agent (Global Data Systems)

A Windows desktop workspace engineers use to assess, plan, deploy, report on and manage customer Microsoft
environments. Solution and root namespace: `EngineeringWorkspace` (.NET 10, WPF, MVVM with CommunityToolkit.Mvvm,
Generic Host).

**Version 1.3** loads the control catalog into the application database: policies, expected settings, policy sets,
frameworks and requirements with their mappings, and standards go into the EaaS v3 `pol`, `fw` and `gds` tables plus
the flat `secureplus.CatalogControl` table, automatically and idempotently (see `docs/architecture/catalog-database.md`).

**Version 1.2** added the control catalog (3,268 controls from the GDS Documents repository, available from the first
start), a real assessment engine (Microsoft Graph and public DNS; Conditional Access, Endpoint Security and Intune
controls plus the GDS Microsoft 365 baseline checks), Live Assessment, multi-customer and recurring assessments, the
Golden Tenant baseline and comparison, one-click tenant onboarding (the app registration, Graph permissions, admin
consent and client secret are created automatically after a single administrator sign-in), an activity panel and info
bar, an accordion navigation, Ctrl + mouse wheel zoom and screen-fitted 16:9 windows.

Implemented today: **first-run setup** (database, first customer profile, initial assessment, workspace, sample
data), layered INI configuration with provenance and four-stage validation, configuration-driven navigation,
workspaces and customer profiles with header pickers, **Dashboard Overview** and **Assessment Scan** (assessment
history and findings stored in the database), **Database Configuration** (SQL Server, Azure SQL, SQLite; create
database; EaaS v3 schema install, upgrade and repair; permission checks), **Application Settings**, **Credential
Management** (DPAPI), **Tenant App Registration** (Microsoft Entra / Graph connection test, permission validation,
app-registration assistant), **Appearance** (GDS Light/Dark/System) and **Auditing and Logs**. Other destinations are
labelled placeholders that name their planned milestone; provider scans of a tenant arrive with the assessment
engine (M4), so assessment requests are recorded as Pending until then.

## Install

Download `GDS-SecurePlusAgent-<version>-win-x64-Setup.exe` from the GitHub release and run it as an administrator.
It installs to `C:\Program Files\GDS Secure Plus agent`, adds a Start menu shortcut, and registers **GDS Secure Plus
agent** in Programs and Features (`appwiz.cpl`), where it can be repaired or uninstalled. The installer is
self-contained (the .NET runtime is included). Per-user configuration and data live in
`%LOCALAPPDATA%\Global Data Systems\Secure Plus Agent` (standard users cannot write to Program Files) and are kept on
upgrade and uninstall; data of earlier versions (`...\Secure Plus - Modern Work`) is moved there on first start.

### First-run setup

Until the backend database is installed no menu is shown. Setup walks through:

1. **Check for database access** — "You don't have a database connection setup. Would you like to setup one now?"
   Choose SQL Server, Azure SQL or SQLite and any authentication (Windows, SQL login with user name and password,
   Microsoft Entra interactive, service principal or managed identity), instance, port, encryption and certificate
   options. **Check and install database** connects, checks the tables and the account's permissions, creates the
   database if needed, and installs, upgrades or repairs the schema.
2. **Customer profile** — "You don't have a customer profile added. Would you like to add one now?"
3. **Initial assessment** — the database is checked for the customer's assessments and workspace; if there are none:
   "Would you like to do the initial Assessment scan now?"
4. **Connect the Microsoft 365 tenant** — *Sign in and register automatically*: sign in once as a Global
   Administrator; the **GDS Secure Plus Agent** app registration, its read-only Microsoft Graph permissions, admin
   consent and a client secret are created and the connection is tested. See `docs/configuration/graph-permissions.md`.
5. **Workspace** — select one, or create it if none exists.
6. **Finish** — when no other customers or workspaces exist, the sample customer, workspace and a completed sample
   assessment are loaded.

## Build, test and run

```powershell
dotnet restore
dotnet build
dotnet test
dotnet run --project src/EngineeringWorkspace.Desktop
```

The WPF project targets `net10.0-windows`; `EnableWindowsTargeting` lets non-Windows agents compile it, but the UI
runs only on Windows. Configuration is read from `%LOCALAPPDATA%\Global Data Systems\Secure Plus Agent\config`
(seeded from `config/` on first run). To use the repository's files directly during development:

```powershell
dotnet run --project src/EngineeringWorkspace.Desktop -- --config-dir .\config
```

Publish (self-contained) and package the installer and ZIPs (needs Inno Setup 6, `choco install innosetup`):

```powershell
./scripts/package-windows.ps1 -SkipMsix
```

## External Environment Setup

Never put passwords, client secrets or tokens in INI/JSON files, exports, issue comments or source control. Store them
with **Workspace > Settings > Credential Management** (or the write-only password boxes on the pages below); only a
secret reference such as `os:database/contoso/sql-login` is saved in configuration.

### .NET 10 prerequisite

The MSIX and the portable ZIP are self-contained and need nothing else. The framework-dependent ZIP needs the .NET 10
Windows Desktop Runtime; run its prerequisite script once (elevated prompt appears):

```powershell
.\prerequisites\Install-DotNetDesktopRuntime.ps1
```

It detects `Microsoft.WindowsDesktop.App` 10.x, downloads the installer named in Microsoft's release metadata (or uses
`-OfflineInstallerPath`), verifies the SHA-512 hash and Microsoft signature, installs silently and reports whether a
restart is needed. See `docs/packaging/windows-distribution.md`.

### Local SQL Server (developer PC)

From an elevated PowerShell in the repository:

```powershell
.\build\scripts\Install-LocalSqlServer.ps1
```

It reuses an existing SQL Server 2022+ instance or installs SQL Server Express (`-Edition Developer` for Developer
edition) with Windows authentication and you as sysadmin, enables TCP/IP and prints the server name (for example
`localhost\SQLEXPRESS`). It does not create the application database. `-SqlAuthentication` enables mixed mode and
prompts for the `sa` password without storing it (SQL Server setup receives it on its command line).

### Database configuration

Open **Workspace > Settings > Database Configuration**, choose the provider and fill in only the fields it shows:

- **SQL Server** — server, optional instance and port, database name; authentication `WindowsIntegrated`, `SqlLogin`
  (user name + password stored through the page), `EntraInteractive` (public-client app ID; browser sign-in) or
  `EntraServicePrincipal` (client ID, tenant ID, stored secret). Keep **Encrypt** on; enable **Trust server
  certificate** only for a local development instance with a self-signed certificate.
- **Azure SQL** — `<server>.database.windows.net`, database name, and `EntraInteractive`, `EntraServicePrincipal`,
  `EntraManagedIdentity` (hosted use) or `SqlLogin`. The identity needs a database user (`CREATE USER ... FROM
  EXTERNAL PROVIDER`) or the `dbmanager` role to create databases.
- **SQLite** — a file path (relative paths resolve against the data directory). Local development only; EaaS v3
  specifies SQL Server 2022 / Azure SQL.

**Save settings** writes the non-secret values to your `Database.ini`. Then use the operations, each shown only when
it applies:

1. **Test Connection** — server reachability and sign-in (master).
2. **Check Database** — database exists and opens; reads the schema version.
3. **Create Database** — if missing; shows server, database, authentication and the exact operation and asks for confirmation.
4. **Validate Schema** — compares every expected EaaS v3 and Secure Plus table and column.
5. **Initialize Schema** — if the schema is missing or older; applies the versioned migrations in one transaction after confirmation.
6. **Repair Schema** — if expected tables or columns are missing; re-applies the idempotent migration scripts and adds missing optional columns.
7. **Refresh Status** — read-only status.

**Install or repair database** runs all of these in order. The readiness checklist includes **Permissions**: SELECT,
INSERT, UPDATE and DELETE are needed every day; CREATE TABLE, CREATE SCHEMA, ALTER ANY SCHEMA and REFERENCES are
needed to install, upgrade or repair the schema (for example `db_datareader` + `db_datawriter` + `db_ddladmin`).

The status panel shows provider, server, database, authentication, encryption, server version, schema version and
migration state, last successful connection, last attempt and a sanitized error with the recommended action, plus a
readiness checklist. Existing objects are never dropped or overwritten, and a database with a newer schema is never
modified ("Database schema is newer than this application supports."). Details: `docs/architecture/eaas-v3-database-analysis.md`.

### Microsoft 365 app registration

On **Workspace > Customer Profiles > Tenant App Registration**:

1. Create or select the customer and choose **Use as current customer**.
2. Enter the tenant display name and name, **Tenant ID**, primary domain and cloud (Commercial, GCC, GCC High, DoD).
3. For an existing app registration enter the **Client ID**, the client secret (write-only, stored immediately with
   DPAPI and never shown again) and its expiration, and **Save**.
4. **Test Connection** acquires an app-only token with MSAL, calls `GET /organization` through Microsoft Graph, checks
   that the tenant matches and validates the required application permissions from the token. Tokens are never shown,
   logged or saved. The current operations need `Organization.Read.All` (application) with admin consent; capabilities
   without implemented operations show "not yet defined".

The **Create / Configure App Registration assistant** runs one confirmed step at a time with a real administrator
sign-in: connect, validate the administrator context, create the app registration, create/locate the service
principal, configure Graph application permissions, grant admin consent, create a client secret (stored immediately,
returned once), associate it with the customer, test authentication and validate permissions. Set
`[Integrations:Microsoft] AdministratorClientId` in `Integrations.ini` to a public-client app registration with the
`http://localhost` redirect URI first. Granting admin consent requires a Privileged Role Administrator or Global
Administrator.

### Credential storage

Secrets are protected with Windows DPAPI for the current Windows user under
`%LOCALAPPDATA%\Global Data Systems\Secure Plus Agent\data\Credentials`. Only the same Windows user can
decrypt them; after a profile reset or on another machine, enter them again. Credential Management shows each
credential's type, customer, tenant, dates and status (Configured, Missing, Expires Soon, Expired, Invalid) and never
the value. See `docs/configuration/secrets-reference.md`.

### Security considerations

- Every administrative or destructive action states what will happen and requires confirmation.
- Logs, audit, diagnostics and exceptions pass through one redaction service; the UI never shows stack traces.
- Exports never include secrets, tokens, SQL passwords or private keys.
- Configuration validation rejects plaintext values in secret-bearing keys and a credential store inside a Git working tree.
- Live validation procedures: `docs/testing/live-environment-validation.md`.

## Catalog, assessments and the Golden Tenant

- **Dashboard > Catalog** lists every control with its baselines (policy sets), indicative framework references, user
  impact, experience, pain points, licensing, Graph definition and the current customer's latest result. Filter by
  kind, category, platform, policy set, severity, evaluation, result or framework, and export to CSV. Regenerate the
  catalog after the Documents repository changes:
  `python3 build/scripts/catalog/generate_catalog.py --documents ../Documents --output src/EngineeringWorkspace.Infrastructure/Catalog/catalog.json.gz`.
- The catalog is also loaded into the application database (in the background when the database lacks the current
  catalog, or with **Catalog > Load into database**); tables and example queries: `docs/architecture/catalog-database.md`.
- **Assessment > Assessment Scan** assesses one or many customers in the background, runs pending assessments and
  manages recurring schedules (daily, weekly, monthly; they run while the app is open and catch up at the next start).
- **Assessment > Live Assessment** reads the selected tenant now without storing anything.
- **Workspace > Settings > Golden Tenant Configuration** chooses the policy sets of the Golden Tenant baseline (default
  `Golden-Tenant`), which scores assessments; **Reporting > Golden Tenant Comparison** shows each customer's gaps.
- The **Activity** panel (bottom right) shows every background task with its steps; the info bar announces
  completions and, when idle, suggests next actions (for example customers not assessed for 30 days).

## Application Settings

**Workspace > Settings > Application Settings** edits the INI files in the configuration directory: `Application.ini`
(Environment — Development, Test, Staging or Production — names, start page, restore behaviour, paths, workspace,
import/export and credential settings), `Branding.ini`, `Logging.ini`, `Features.ini`, `Integrations.ini` and
`Repositories.ini`. Each value shows where its effective value comes from. Values are validated before saving and
apply after **Restart now**.

## Layout

Drag the splitters beside the navigation pane and the activity panel to resize them. Every table has a grip under it to
change its height and an **Expand** button that opens it in a full-screen window. Main menus expand and collapse with
their arrows; opening one collapses the others unless it is pinned with its lock icon. Hold **Ctrl** and turn the mouse
wheel (or Ctrl + Plus / Minus, Ctrl + 0 to reset) to zoom. Windows open at 16:9 inside the screen's work area.

## Customizing Secure Plus Appearance

Appearance is set in `ApplicationProperties.json` in the configuration directory
(`%LOCALAPPDATA%\Global Data Systems\Secure Plus Agent\config\ApplicationProperties.json`). The file is
optional: without it the built-in GDS Light and GDS Dark themes apply. Override only what you need:

```json
{
  "schemaVersion": 1,
  "appearance": {
    "theme": { "mode": "Dark", "allowUserSelection": true },
    "typography": { "baseFontSize": 15 },
    "dark": { "colors": { "primary": "#72E1F5" } }
  }
}
```

- **Light / Dark / System** — `appearance.theme.mode`; System follows the Windows app theme. With
  `allowUserSelection: true` the header selector and the Appearance page switch the mode for the session without
  rewriting the file.
- **Fallback** — a missing or malformed file, or an unsupported `schemaVersion`, uses the GDS defaults; an invalid value
  (for example a color that is not `#RRGGBB`/`#AARRGGBB`) falls back individually; unknown properties are ignored. All
  problems are listed on the Appearance page and in the log and never stop the application.
- **Reload** — edit the file and select **Reload Appearance** on **Workspace > Settings > Appearance**.
- **Restore GDS defaults** — **Reset to Built-In GDS Defaults** applies them for the session without changing the
  file; **Create Properties File From Current Defaults** writes a complete file (asks before overwriting).

Every property, default and range: `docs/configuration/application-properties-reference.md`. Theme design and color
sources: `docs/architecture/theme-system.md`.

## Documentation

- Architecture: `docs/architecture/architecture-overview.md`, `dependency-rules.md`, `theme-system.md`, ADRs in `docs/architecture/architecture-decisions/`
- Catalog in SQL: `docs/architecture/catalog-database.md`
- EaaS v3: `docs/architecture/eaas-v3-database-analysis.md`, `eaas-v3-application-model-map.md`, `eaas-v3-implementation-conformance.md`
- Configuration: `docs/configuration/application-ini-reference.md`, `navigation-ini-reference.md`, `secrets-reference.md`, `application-properties-reference.md`, `repository-ini-reference.md`
- Packaging: `docs/packaging/windows-distribution.md`
