# Tenant permissions (4.0.0-beta.2)

Secure Plus reaches a customer tenant with **two app registrations** (app-only, certificate credentials):

| Registration | Exists | Holds | Used by |
|---|---|---|---|
| **GDS Secure Plus Read** | From *Connect tenant* on | Read-only Microsoft Graph application permissions, `Exchange.ManageAsApp` (Office 365 Exchange Online and Microsoft Exchange Online Protection) and the **Global Reader** directory role | Quick and in-depth scans, Live Assessment, the tenant inventory, the connection test, the read-only PowerShell collector |
| **GDS Secure Plus Deploy** | Only during a deploy phase (*Enable Deployment Access* … *Remove Deployment Access*) | The write permissions Change Execution uses, plus the reads it needs | Deployment > Change Execution > Apply to Tenant |

Both are created, updated and removed by jobs on **Customer Profiles > Tenant Access** (and *Connect tenant* when a customer is created). A
Global Administrator (or a Privileged Role Administrator together with an Application Administrator) signs in once per job.

**Credentials.** Each registration has one self-signed certificate (RSA 2048, SHA-256, one year). Only the public certificate is added to the app
in Microsoft Entra; the private key is created on the engineer's computer and kept only in the encrypted secret store (shared database, AES-GCM
organisation key). It is never shown, logged, exported or written to disk; the PowerShell collector receives it in memory on standard input.
Creating a certificate replaces the app's older certificates and removes its client secrets. Renew with *Update Read Access* (Read) or by
enabling deployment access again (Deploy); expiry is shown on Customer Profiles and Credential Management.

**Older registrations** (*GDS Secure Plus Agent*, client secret, read and write permissions) keep working for scans. *Update Read Access* renames
the app to *GDS Secure Plus Read*, adds the missing read access, switches to a certificate and **removes the write permissions and client secrets**.
Change Execution does not use it: applying changes needs the Deploy registration.

A more privileged permission (for example `Directory.Read.All` instead of `Organization.Read.All`) also satisfies a read requirement in the
connection test.

## Read registration – Microsoft Graph application permissions

| Capability | Permission | Used for |
|---|---|---|
| TenantDiscovery | `Organization.Read.All` | GET /organization, /domains, /subscribedSkus (tenant identity, domains, licences) |
| EntraAssessment | `Directory.Read.All` | GET /users, /groups, /devices, /domains, /applications, /servicePrincipals (accounts, dynamic groups, stale devices, apps) |
| EntraAssessment | `Policy.Read.All` | GET /policies/authorizationPolicy, /policies/identitySecurityDefaultsEnforcementPolicy (consent, app registration and guest settings) |
| EntraAssessment | `AuditLog.Read.All` | GET /reports/authenticationMethods/userRegistrationDetails and users' signInActivity (MFA registration, dormant accounts) |
| EntraAssessment | `RoleManagement.Read.Directory` | GET /roleManagement/directory/roleAssignments and roleEligibilitySchedules (Global Administrators, permanent admin assignments) |
| ConditionalAccessAssessment | `Policy.Read.All` | GET /identity/conditionalAccess/policies (Conditional Access controls and checks) |
| EntraAssessment | `RoleManagementPolicy.Read.Directory` | GET /policies/roleManagementPolicyAssignments (PIM settings for Global Administrator) |
| EntraAssessment | `AccessReview.Read.All` | GET /identityGovernance/accessReviews/definitions (access reviews) |
| EntraAssessment | `Policy.Read.DeviceConfiguration` | GET /policies/deviceRegistrationPolicy (who can join devices, device limit, local admin) |
| EntraAssessment | `ReportSettings.Read.All` | GET /admin/reportSettings (concealed names in usage reports) |
| EntraAssessment | `OrgSettings-Forms.Read.All` | GET /admin/forms/settings (Microsoft Forms phishing protection) |
| IntuneAssessment | `DeviceManagementRBAC.Read.All` | GET /deviceManagement/roleAssignments, operationApprovalPolicies (Intune RBAC, multi-admin approval) |
| TeamsAssessment | `TeamworkAppSettings.Read.All` | GET /teamwork/teamsAppSettings (Teams org-wide app settings) |
| EntraAssessment | `NetworkAccess.Read.All` | GET /networkAccess/forwardingProfiles (Global Secure Access traffic profiles) |
| EntraAssessment | `BackupRestore-Configuration.Read.All` | GET /solutions/backupRestore (Microsoft 365 Backup) |
| EntraAssessment | `OrgSettings-AppsAndServices.Read.All` | GET /admin/appsAndServices (Microsoft 365 user-owned apps and trials) |
| IntuneAssessment | `CloudPC.Read.All` | GET /deviceManagement/virtualEndpoint (Windows 365 user settings and provisioning policies) |
| IntuneAssessment | `DeviceManagementConfiguration.Read.All` | GET /deviceManagement/configurationPolicies (settings catalog, endpoint security), deviceConfigurations, deviceCompliancePolicies |
| IntuneAssessment | `DeviceManagementApps.Read.All` | GET /deviceAppManagement/androidManagedAppProtections, iosManagedAppProtections, mobileApps |
| IntuneAssessment | `DeviceManagementScripts.Read.All` | GET /deviceManagement/deviceManagementScripts (PowerShell scripts deployed by Intune) |
| IntuneAssessment | `DeviceManagementServiceConfig.Read.All` | GET /deviceManagement/deviceEnrollmentConfigurations (personal device enrollment restrictions) |
| DeviceAssessment | `DeviceManagementManagedDevices.Read.All` | GET /deviceManagement/managedDevices (enrolled devices and compliance state) |
| DefenderAssessment | `SecurityEvents.Read.All` | GET /security/secureScores (Microsoft Secure Score) |
| DefenderAssessment | `AttackSimulation.Read.All` | GET /security/attackSimulation/simulations (attack simulation training) |
| PurviewAssessment | `InformationProtectionPolicy.Read.All` | GET /security/informationProtection/sensitivityLabels (sensitivity labels) |
| PurviewAssessment | `RecordsManagement.Read.All` | GET /security/labels/retentionLabels (retention labels) |
| SharePointAssessment | `SharePointTenantSettings.Read.All` | GET /admin/sharepoint/settings (external sharing) |
| LicenseAssessment | `Organization.Read.All` | GET /subscribedSkus (licences) |
| ApplicationAssessment | `Application.Read.All` | GET /applications and /servicePrincipals (app inventory) |

Distinct set: `AccessReview.Read.All`, `Application.Read.All`, `AttackSimulation.Read.All`, `AuditLog.Read.All`, `BackupRestore-Configuration.Read.All`, `CloudPC.Read.All`, `DeviceManagementApps.Read.All`, `DeviceManagementConfiguration.Read.All`, `DeviceManagementManagedDevices.Read.All`, `DeviceManagementRBAC.Read.All`, `DeviceManagementScripts.Read.All`, `DeviceManagementServiceConfig.Read.All`, `Directory.Read.All`, `InformationProtectionPolicy.Read.All`, `NetworkAccess.Read.All`, `OrgSettings-AppsAndServices.Read.All`, `OrgSettings-Forms.Read.All`, `Organization.Read.All`, `Policy.Read.All`, `Policy.Read.DeviceConfiguration`, `RecordsManagement.Read.All`, `ReportSettings.Read.All`, `RoleManagement.Read.Directory`, `RoleManagementPolicy.Read.Directory`, `SecurityEvents.Read.All`, `SharePointTenantSettings.Read.All`, `TeamworkAppSettings.Read.All`

### Exchange Online, Security & Compliance and Teams (read-only PowerShell)

| Resource | Permission or role | Why |
|---|---|---|
| Office 365 Exchange Online (`00000002-0000-0ff1-ce00-000000000000`) | `Exchange.ManageAsApp` (application) | App-only sign-in to Exchange Online PowerShell |
| Microsoft Exchange Online Protection (`00000007-0000-0ff1-ce00-000000000000`) | `Exchange.ManageAsApp` (application) | App-only sign-in to Security & Compliance PowerShell (skipped if the tenant has no such service principal) |
| Microsoft Entra directory role | **Global Reader** (`f2ef992c-3afb-46b9-b7cf-a126ee74c451`) | Read-only access in Exchange Online, Security & Compliance and Teams PowerShell; it cannot change anything |

The collector runs only the `Get-` cmdlets listed in `PowerShellTenantDataCollector` and loads only those cmdlets (`-CommandName`). It needs
PowerShell 7 with ExchangeOnlineManagement 3.10.1 and MicrosoftTeams 8.0.0 (`prerequisites\Install-SecurePlusPowerShellModules.ps1`).

## Deploy registration – Microsoft Graph application permissions

| Capability | Permission | Used for |
|---|---|---|
| PolicyDeployment | `Policy.ReadWrite.ConditionalAccess` | POST /identity/conditionalAccess/policies (created in report-only state) |
| PolicyDeployment | `DeviceManagementConfiguration.ReadWrite.All` | POST /deviceManagement/configurationPolicies, deviceConfigurations, deviceCompliancePolicies, intents (Intune and endpoint security policies) |
| PolicyDeployment | `DeviceManagementApps.ReadWrite.All` | POST /deviceAppManagement/*ManagedAppProtections (app protection policies) |
| PolicyDeployment | `DeviceManagementServiceConfig.ReadWrite.All` | POST /deviceManagement/deviceEnrollmentConfigurations (enrollment restrictions) |
| PolicyDeployment | `Policy.Read.All` | GET /identity/conditionalAccess/policies (existing policy names before creating) |
| PolicyDeployment | `Group.Read.All` | GET /groups (resolve deployment group variables to object IDs) |
| PolicyDeployment | `Organization.Read.All` | GET /organization (sign-in test of the Deploy registration) |

Distinct set: `DeviceManagementApps.ReadWrite.All`, `DeviceManagementConfiguration.ReadWrite.All`, `DeviceManagementServiceConfig.ReadWrite.All`, `Group.Read.All`, `Organization.Read.All`, `Policy.Read.All`, `Policy.ReadWrite.ConditionalAccess`

Each live change also needs the Advanced Mode passcode (step-up MFA in 4.0.0-beta.3) and a confirmation prompt; Conditional Access policies are
always created in report-only state and every write is recorded in the audit log.

## Administrator sign-in

The jobs sign the administrator in with the Microsoft Graph Command Line Tools public client (`14d82eec-204b-4c2f-b7e8-296a70dab67e`) and request the
delegated scopes `Application.ReadWrite.All`, `AppRoleAssignment.ReadWrite.All`, `RoleManagement.ReadWrite.Directory` (to assign Global Reader to the
Read registration) and `User.Read`. Set `[Integrations:Microsoft] AdministratorClientId` to use your own public-client app instead. Both apps are
single-tenant (`AzureADMyOrg`).

## Permissions you may have granted that are not needed

Permissions such as `IdentityRiskEvent.Read.All`, `IdentityRiskyUser.Read.All`, `Reports.Read.All`, `User.Read.All`,
`UserAuthenticationMethod.Read.All` or `RoleManagementAlert.Read.Directory` are not called. *Update Read Access* removes every Microsoft Graph
application permission the Read registration does not need, including write permissions.
