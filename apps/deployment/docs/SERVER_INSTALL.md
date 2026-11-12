# Secure Plus – Server Install (3.2 and 4.0)

> **4.0.0-beta.2:** upgrades a 4.0.0-beta.1 database to V0012. A 3.x database is not supported: start from a fresh database (the installer creates V0001–V0012).
>
> **PowerShell for in-depth scans (4.0.0-beta.2):** on every computer that runs scans (desktops and the scheduling computer) tick *Install PowerShell modules* in the installer, or run
> `prerequisites\Install-SecurePlusPowerShellModules.ps1` from the install folder in an elevated prompt. It installs PowerShell 7 (winget) and
> ExchangeOnlineManagement 3.10.1 and MicrosoftTeams 8.0.0 for all users. Outbound HTTPS to `www.powershellgallery.com` is needed once; scans then need
> `outlook.office365.com`, `ps.compliance.protection.outlook.com` and `api.interfaces.records.teams.microsoft.com` in addition to Microsoft Graph.

Secure Plus uses one shared **SQL Server 2022** database. Every desktop connects to it with the user's **Windows account**
(office network or VPN). Tenant calls (assessments, deployments) run on the desktop, as in 3.1; one **scheduling computer**
runs recurring assessments.

## Prerequisites
| Item | Detail |
|---|---|
| SQL Server | SQL Server 2022 (any edition, including Express) on a domain-joined server. 2022 is needed for the append-only audit ledger; older versions work with a weaker, permission-based audit table. |
| Network | TCP 1433 (or your instance port) from office and VPN ranges to the SQL Server only. |
| Accounts | The person running the **Server** install needs `dbcreator` (to create the database) and, to grant access from the app, `securityadmin`. Everyone else only needs a login the app (or a DBA) creates. |
| Backups | Configure SQL Server backups of the Secure Plus database (full daily, log as appropriate). |
| Certificate | If SQL Server has no certificate from your CA, the app connects with *Trust server certificate* on. Install a CA certificate to turn that off. |

## Install order
1. **Server install** (one computer, for example the SQL Server itself or an admin PC):
   `GDS-SecurePlusAgent-<version>-win-x64-Setup.exe` → **Server** → SQL Server and database name (default `SecurePlus`).
   Setup tests the connection, then creates the database if it is missing, applies the schema migrations and loads the
   reference catalog (`--provision-database`). Re-running it is safe.
2. **Workstations**: run the same installer → **Workstation** → the same SQL Server. Workstations never create or upgrade
   the database.
3. **Upgrades**: always upgrade the **Server install first**, then workstations. A workstation older than the database is
   blocked with *Update required*; a workstation newer than the database asks for the Server install to be upgraded.

### Silent install (Intune or scripts)
```
GDS-SecurePlusAgent-<version>-win-x64-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /TYPE=Server /SERVER=sql01.gds.local /DATABASE=SecurePlus
GDS-SecurePlusAgent-<version>-win-x64-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /TYPE=Workstation /SERVER=sql01.gds.local
```
`/TYPE` is `Workstation`, `Server` or `AllInOne`. The choice is saved in
`%ProgramData%\Global Data Systems\Secure Plus Agent\install.json` and `HKLM\SOFTWARE\Global Data Systems\Secure Plus Agent`,
and is offered again on the next upgrade. The server can also be typed in Settings > Database Configuration.
Uninstalling never touches the database.

## Users and roles (no AD groups)
1. On first use, open **Settings > Users & Roles** and add yourself (`GDS\yourname`) as **Lead**. Until the first user is
   added, everyone has full access (as in 3.1).
2. Add each person (about 15) with one role: Sales, Engineer, Technician/Deployer, NOC, Lead or Manager.
3. Select **Grant Database Access** for each person. With `securityadmin` the app creates the Windows login, the database
   user and the role membership (`secureplus_<role>`). Without it, select **Show SQL Script** and send it to your DBA.
4. Once people connect with their own logins, the database enforces:
   - tenant credentials (`secureplus.TenantSecret`) only for Engineer, Technician/Deployer and Lead;
   - users and roles changed only by Leads;
   - the audit ledger cannot be changed or deleted by anyone (SQL Server 2022 ledger table).

## Scheduling computer
Pick one always-on computer (for example the NOC PC) and in **Settings > Database Health > Scheduling Computer** select
**Use This Computer** then **Set Scheduling Computer**. Only that computer runs recurring assessments; it checks in every
few minutes. If it stops for more than 15 minutes, Database Health shows a warning. Open Secure Plus there once so its
Windows task is registered.

## Moving from SQLite
If a computer still uses a SQLite file (3.1 and earlier), open **Settings > Database Configuration > Move to SQL Server**.
The file is backed up and never changed; the copy is kept only when every table's row count matches. Then choose
*Switch to SQL Server* and restart Secure Plus.
