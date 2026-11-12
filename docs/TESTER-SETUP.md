# Tester setup (demo)

For internal testing only. Both apps use the shared SQL Server with your Windows login, so you must be
on the GDS network (or VPN) and your account must have access to `ArchiTech3` and `SecurePlus`.
Test submissions write to the real databases.

## Prerequisites
- .NET 10 SDK
- Node.js 22.12+ (20.19+ also works)
- Git

## 1. Sales form (salesmen)
```powershell
git clone https://github.com/MarquieI/GDS-SecurePlus-Platform.git
cd GDS-SecurePlus-Platform
.\scripts\Start-SalesForm.ps1
```
Open http://localhost:5080. The script prompts for an optional Planner flow URL; press Enter to skip.
Ask the project owner for the URL if you want the Planner handoff. It is held in memory only.

Local mode has no sign-in: every tester appears as "Local developer" with the development roles, so all
testers share the same owner and see each other's engagements. Real per-salesman separation needs Entra
sign-in (`Authentication:Mode=Production`, see DEPLOYMENT.md).

## 2. Deployment app (engineers)
```powershell
.\scripts\Install-DeploymentApp.ps1 -Launch
```
This publishes the app, installs it for the current user, writes its database config, and adds a desktop
shortcut. No .NET SDK on the machine? Have someone run the script once and share `artifacts\deployment-app`,
then run `.\scripts\Install-DeploymentApp.ps1 -PublishedFrom <folder>` (needs the .NET 10 Desktop Runtime).

## Test flow
1. Submit an engagement in the form.
2. Check its sync badge on the engagement page turns `Synced`.
3. Open the customer in the Deployment app.

## Troubleshooting
- Cannot connect to SQL: check VPN and database access; the connection uses Windows login.
- Port in use: `.\scripts\Start-SalesForm.ps1 -Port 5090`.
- Corporate policy blocks winget: install .NET and Node from their installers or portable zips.
