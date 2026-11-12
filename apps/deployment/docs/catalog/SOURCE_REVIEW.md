# Catalogue source review

> **Resolved 9 Nov 2026 – catalogue rebuilt from Documents@c13d9a4 (3,138 controls).**
> 1. CA-213 / CA-214 accepted (report-only, frontline QR code group, compliant shared devices, break-glass excluded).
> 2. Conditional Access restored to the shipped GDS baseline (guest exclusions, CA-210 terms-of-use placeholder, CA-502 in its policy sets),
>    keeping the CA-200 rename; every user policy excludes the break-glass group.
> 3. Of the 273 removed Intune controls, 36 Internet Explorer settings and 66 exact duplicates (same setting and value in another control)
>    stay removed; the other 171 were restored (3 truncated file names fixed). 38 aliases of deleted duplicate files are no longer needed
>    (4.0 starts from a fresh database).
> Result versus the 4.0.0-beta.1 catalogue: 102 controls removed (IE and duplicates), CA-213 and CA-214 added, CA-200 renamed; nothing else changed.

(Original review, for the record:)

8–9 Nov 2026. The app's catalogue (`catalog.json.gz`, 4.0.0-beta.1) was built from Documents@f10e83b plus the GDS Conditional Access
baseline repository (GDS-ConditionalAccess-Baseline@cb457f3). Rebuilding it from today's Documents (`adc10f8`, branch
`gds/brave-carson-98ha92`) would change it as below. **Nothing is rebuilt until you approve each group.**

## Summary

| Change | Count | Recommendation |
|---|---|---|
| New Conditional Access policies CA-213, CA-214 (frontline QR code + PIN) | 2 | **Accept** |
| Conditional Access policies changed | 11 | **Reject 9, review 2** (below) |
| Intune controls removed from the source | 273 | **Your decision**: deliberate clean-up, or keep? (240 OpenIntuneBaseline, 33 Microsoft framework) |

## Conditional Access changes

> **9 Nov 2026:** the break-glass exclusion is restored in Documents for CA-209, 210, 600, 601, 602 and 800 (user decision: every CA policy excludes the break-glass group).

Today's Documents copy of the Conditional Access policies differs from the baseline the app ships. Most differences remove protections:

| Policy | Change | Risk | Recommendation |
|---|---|---|---|
| CA-209 Token protection, CA-210 Terms of use, CA-600 Block specific apps, CA-601/602 Insider risk, CA-800 GSA block | **Emergency-access (break-glass) group no longer excluded** (CA-600 loses all exclusions) | Can lock out break-glass accounts | **Reject** (keep shipped) |
| CA-210 Terms of use | Terms-of-use placeholder `<TermsOfUseId>` replaced by the literal `ToU` | Deployment fails | **Reject** |
| CA-200, CA-208, CA-503, CA-504, CA-800 | Guests/external users no longer excluded | Guests must meet member device/MFA rules; may block B2B access | **Review** (keep shipped unless intended) |
| CA-200 | Renamed "…Strong Auth or Trusted **Device or** Location"; body otherwise as above | Name only | **Accept the rename** if intended |
| CA-502 App protection for internal users (iOS/Android) | No longer in any policy set (02, 04, All-Policies, Golden-Tenant): the file was renamed and the policy sets still point at the old name | Drops out of the Golden Tenant | **Reject** (or fix the policy-set file names) |

Recommended fix: make the GDS Conditional Access baseline the single source again (copy the shipped policies back into Documents,
then re-apply CA-213/CA-214 and the CA-200 rename), so a rebuild changes only what you meant to change.

## Intune controls removed from the source

273 controls are no longer in Documents. Today they show in the catalogue (OIB controls stay visible, as decided).

| Category | Removed |
|---|---|
| OIB - SC - Internet Explorer (Legacy) - D - Security | 36 |
| OIB - SC - Device Security - D - Local Security Policies | 20 |
| OIB - ES - Attack Surface Reduction - D - ASR Rules (Audit Mode) | 19 |
| OIB - ES - Windows Firewall - D - Firewall Configuration | 16 |
| OIB - SC - Device Security - D - Security Hardening | 16 |
| MS Framework - App Protection - L2 Enterprise Enhanced Data Protection | 13 |
| OIB - SC - Microsoft Office - U - Config and Experience | 13 |
| OIB - ES - Attack Surface Reduction - D - ASR Rules (L2) | 12 |
| OIB - SC - Device Security - D - Audit and Event Logging | 9 |
| OIB - SC - Device Security - D - Remote Desktop Services and RPC | 8 |
| OIB - SC - Device Security - U - Device Guard, Credential Guard and HVCI | 8 |
| OIB - ES - Defender Antivirus - D - AV Configuration | 7 |
| OIB - SC - Defender Antivirus - D - Additional Configuration | 7 |
| OIB - SC - Microsoft Edge - U - Profiles, Sign-In and Sync | 7 |
| MS Framework - App Protection - L1 Enterprise Basic Data Protection | 6 |
| MS Framework - App Protection - L3 Enterprise High Data Protection | 6 |
| OIB - SC - Windows Update for Business - D - Delivery Optimisation | 5 |
| OIB - SC - Device Security - D - Windows Subsystem for Linux | 4 |
| OIB - SC - Microsoft Edge - U - User Experience | 4 |
| OIB - SC - Windows Update for Business - D - Reports and Telemetry | 4 |
| OIB - SC - Windows User Experience - D - Feature Configuration | 4 |
| MS Framework - Work Profile - L3 High Security - Configuration | 3 |
| OIB - ES - Defender Antivirus - D - Security Experience | 3 |
| OIB - SC - Device Security - U - Windows Spotlight and Org Messages | 3 |
| OIB - SC - Microsoft Edge - U - Password Management | 3 |
| OIB - SC - Microsoft OneDrive - D - Configuration | 3 |
| OIB - SC - Microsoft OneDrive - U - Configuration | 3 |
| MS Framework - Fully Managed - L3 High Security - Configuration | 2 |
| MS Framework - Work Profile - L1 Basic Security - Configuration | 2 |
| OIB - ES - Encryption - D - BitLocker (OS Disk) | 2 |
| OIB - SC - Device Security - D - Administrator Protection | 2 |
| OIB - SC - Device Security - U - Power and Device Lock | 2 |
| OIB - SC - Network Security - D - Disable NTLM | 2 |
| OIB - SC - Windows Hello for Business - D - Cloud Kerberos Trust | 2 |
| OIB - SC - Windows User Experience - D - Automatic Restart Sign-On | 2 |
| MS Framework - Fully Managed - L2 Enhanced Security - Configuration | 1 |
| OIB - Defender Antivirus - D - Antivirus Configuration | 1 |
| OIB - Device Security - D - Connectivity Settings | 1 |
| OIB - Device Security - D - Resource Redirection | 1 |
| OIB - ES - Defender Antivirus Updates - Ring 2 - UAT | 1 |
| OIB - ES - Defender Antivirus Updates - Ring 3 - Production | 1 |
| OIB - ES - Encryption - U - Personal Data Encryption | 1 |
| OIB - ES - Windows Hello for Business - D - WHfB Configuration | 1 |
| OIB - ES - Windows LAPS - D - LAPS Configuration | 1 |
| OIB - Microsoft AutoUpdate - D - MAU Configuration | 1 |
| OIB - Microsoft Edge - U - Profiles, Sign-In and Sync | 1 |
| OIB - SC - Credential Management - D - Passwordless | 1 |
| OIB - SC - Device Security - D - Login and Lock Screen | 1 |
| OIB - SC - Device Security - D - Script File Associations | 1 |
| OIB - SC - Microsoft Accounts - D - Configuration | 1 |

<details><summary>Every removed control</summary>

**MS Framework - App Protection - L1 Enterprise Basic Data Protection** (6)

- `IN-201020` APP L1 Basic Data Protection-Allowed Data Ingestion Locations (oneDriveForBusiness, sharePoint, camera)
- `IN-201026` APP L1 Basic Data Protection-App Action If Android Device Manufacturer Not Allowed (block)
- `IN-201027` APP L1 Basic Data Protection-Required Android Safety Net Device Attestation Type (basicIntegrityAndDeviceCertification)
- `IN-201028` APP L1 Basic Data Protection-App Action If Android Safety Net Device Attestation Failed (block)
- `IN-201030` APP L1 Basic Data Protection-App Action If Android Safety Net Apps Verification Failed (block)
- `IN-301020` APP L1 Basic Data Protection-Allowed Data Ingestion Locations (oneDriveForBusiness, sharePoint, camera)

**MS Framework - App Protection - L2 Enterprise Enhanced Data Protection** (13)

- `IN-202004` APP L2 Enhanced Data Protection-Allowed Outbound Data Transfer Destinations (managedApps)
- `IN-202005` APP L2 Enhanced Data Protection-Allowed Outbound Clipboard Sharing Level (managedAppsWithPasteIn)
- `IN-202016` APP L2 Enhanced Data Protection-Allowed Data Storage Locations (oneDriveForBusiness, sharePoint)
- `IN-202021` APP L2 Enhanced Data Protection-Allowed Outbound Clipboard Sharing Exception Length (0)
- `IN-202026` APP L2 Enhanced Data Protection-Allowed Data Ingestion Locations (oneDriveForBusiness, sharePoint, camera)
- `IN-202031` APP L2 Enhanced Data Protection-App Action If Android Device Manufacturer Not Allowed (block)
- `IN-202032` APP L2 Enhanced Data Protection-Required Android Safety Net Device Attestation Type (basicIntegrityAndDeviceCertification)
- `IN-202033` APP L2 Enhanced Data Protection-App Action If Android Safety Net Device Attestation Failed (block)
- `IN-202034` APP L2 Enhanced Data Protection-Required Android Safety Net Apps Verification Type (enabled)
- `IN-202035` APP L2 Enhanced Data Protection-App Action If Android Safety Net Apps Verification Failed (block)
- `IN-302005` APP L2 Enhanced Data Protection-Allowed Outbound Clipboard Sharing Level (managedAppsWithPasteIn)
- `IN-302016` APP L2 Enhanced Data Protection-Allowed Data Storage Locations (oneDriveForBusiness, sharePoint)
- `IN-302026` APP L2 Enhanced Data Protection-Allowed Data Ingestion Locations (oneDriveForBusiness, sharePoint, camera)

**MS Framework - App Protection - L3 Enterprise High Data Protection** (6)

- `IN-203005` APP L3 High Data Protection-Allowed Outbound Clipboard Sharing Level (managedAppsWithPasteIn)
- `IN-203017` APP L3 High Data Protection-Allowed Data Storage Locations (oneDriveForBusiness, sharePoint)
- `IN-203031` APP L3 High Data Protection-Allowed Data Ingestion Locations (oneDriveForBusiness, sharePoint)
- `IN-203040` APP L3 High Data Protection-Required Android Safety Net Device Attestation Type (basicIntegrityAndDeviceCertification)
- `IN-203041` APP L3 High Data Protection-App Action If Android Safety Net Device Attestation Failed (block)
- `IN-203043` APP L3 High Data Protection-App Action If Android Safety Net Apps Verification Failed (block)

**MS Framework - Fully Managed - L2 Enhanced Security - Configuration** (1)

- `IN-207003` Fully Managed L2 Configuration-Factory Reset Device Administrator Emails (FactoryResetProtectionAccount)

**MS Framework - Fully Managed - L3 High Security - Configuration** (2)

- `IN-209005` Fully Managed L3 Configuration-Factory Reset Device Administrator Emails (FactoryResetProtectionAccount)
- `IN-209009` Fully Managed L3 Configuration-Password Block Keyguard Features (trustAgents, unredactedNotifications)

**MS Framework - Work Profile - L1 Basic Security - Configuration** (2)

- `IN-211011` Work Profile L1 Configuration-Work Profile Password Minutes Of Inactivity Before Screen Timeout (5)
- `IN-211012` Work Profile L1 Configuration-Work Profile Password Sign In Failure Count Before Factory Reset (10)

**MS Framework - Work Profile - L3 High Security - Configuration** (3)

- `IN-213016` Work Profile L3 Configuration-Work Profile Password Minutes Of Inactivity Before Screen Timeout (5)
- `IN-213018` Work Profile L3 Configuration-Work Profile Password Sign In Failure Count Before Factory Reset (5)
- `IN-213022` Work Profile L3 Configuration-Work Profile Block Personal App Installs From Unknown Sources (True)

**OIB - Defender Antivirus - D - Antivirus Configuration** (1)

- `IN-505006` OIB Defender Antivirus Antivirus Configuration-Threat Type Setting 1 Potentially Unwanted Application (audit)

**OIB - Device Security - D - Connectivity Settings** (1)

- `IN-602003` OIB Device Security Connectivity Settings-Disconnect remote session on lock for Microsoft identity platform authentication (Enabled)

**OIB - Device Security - D - Resource Redirection** (1)

- `IN-603007` OIB Device Security Resource Redirection-Do not allow supported Plug and Play device redirection (Enabled)

**OIB - ES - Attack Surface Reduction - D - ASR Rules (Audit Mode)** (19)

- `IN-410001` OIB Attack Surface Reduction ASR Rules (Audit Mode)-Block execution of potentially obfuscated scripts (Audit)
- `IN-410002` OIB Attack Surface Reduction ASR Rules (Audit Mode)-Block Win32 API calls from Office macros (Audit)
- `IN-410003` OIB Attack Surface Reduction ASR Rules (Audit Mode)-Block executable files from running unless they meet a prevalence, age, or trusted list c… (Audit)
- `IN-410004` OIB Attack Surface Reduction ASR Rules (Audit Mode)-Block Office communication application from creating child processes (Audit)
- `IN-410005` OIB Attack Surface Reduction ASR Rules (Audit Mode)-Block all Office applications from creating child processes (Audit)
- `IN-410006` OIB Attack Surface Reduction ASR Rules (Audit Mode)-Block Adobe Reader from creating child processes (Audit)
- `IN-410007` OIB Attack Surface Reduction ASR Rules (Audit Mode)-Block credential stealing from the Windows local security authority subsystem (Audit)
- `IN-410008` OIB Attack Surface Reduction ASR Rules (Audit Mode)-Block JavaScript or VBScript from launching downloaded executable content (Audit)
- `IN-410009` OIB Attack Surface Reduction ASR Rules (Audit Mode)-Block untrusted and unsigned processes that run from USB (Audit)
- `IN-410010` OIB Attack Surface Reduction ASR Rules (Audit Mode)-Block persistence through WMI event subscription (Audit)
- `IN-410011` OIB Attack Surface Reduction ASR Rules (Audit Mode)-Block use of copied or impersonated system tools (Audit)
- `IN-410012` OIB Attack Surface Reduction ASR Rules (Audit Mode)-Block abuse of exploited vulnerable signed drivers (Audit)
- `IN-410013` OIB Attack Surface Reduction ASR Rules (Audit Mode)-Block process creations originating from PSExec and WMI commands (Audit)
- `IN-410014` OIB Attack Surface Reduction ASR Rules (Audit Mode)-Block Office applications from creating executable content (Audit)
- `IN-410015` OIB Attack Surface Reduction ASR Rules (Audit Mode)-Block Office applications from injecting code into other processes (Audit)
- `IN-410016` OIB Attack Surface Reduction ASR Rules (Audit Mode)-Block rebooting machine in Safe Mode (Audit)
- `IN-410017` OIB Attack Surface Reduction ASR Rules (Audit Mode)-Use advanced protection against ransomware (Audit)
- `IN-410018` OIB Attack Surface Reduction ASR Rules (Audit Mode)-Block executable content from email client and webmail (Audit)
- `IN-410019` OIB Attack Surface Reduction ASR Rules (Audit Mode)-Enable Controlled Folder Access (Audit Mode)

**OIB - ES - Attack Surface Reduction - D - ASR Rules (L2)** (12)

- `IN-411001` OIB Attack Surface Reduction ASR Rules (L2)-Block execution of potentially obfuscated scripts (Audit)
- `IN-411003` OIB Attack Surface Reduction ASR Rules (L2)-Block executable files from running unless they meet a prevalence, age, or trusted list c… (Audit)
- `IN-411004` OIB Attack Surface Reduction ASR Rules (L2)-Block Office communication application from creating child processes (Audit)
- `IN-411005` OIB Attack Surface Reduction ASR Rules (L2)-Block all Office applications from creating child processes (Audit)
- `IN-411007` OIB Attack Surface Reduction ASR Rules (L2)-Block credential stealing from the Windows local security authority subsystem (Audit)
- `IN-411008` OIB Attack Surface Reduction ASR Rules (L2)-Block JavaScript or VBScript from launching downloaded executable content (Audit)
- `IN-411009` OIB Attack Surface Reduction ASR Rules (L2)-Block untrusted and unsigned processes that run from USB (Audit)
- `IN-411012` OIB Attack Surface Reduction ASR Rules (L2)-Block abuse of exploited vulnerable signed drivers (Audit)
- `IN-411013` OIB Attack Surface Reduction ASR Rules (L2)-Block process creations originating from PSExec and WMI commands (Audit)
- `IN-411014` OIB Attack Surface Reduction ASR Rules (L2)-Block Office applications from creating executable content (Audit)
- `IN-411015` OIB Attack Surface Reduction ASR Rules (L2)-Block Office applications from injecting code into other processes (Audit)
- `IN-411018` OIB Attack Surface Reduction ASR Rules (L2)-Block executable content from email client and webmail (Audit)

**OIB - ES - Defender Antivirus - D - AV Configuration** (7)

- `IN-412002` OIB Defender Antivirus AV Configuration-Allow Behavior Monitoring (Allowed. Turns on real-time behavior mo…)
- `IN-412005` OIB Defender Antivirus AV Configuration-Allow Full Scan Removable Drive Scanning (Allowed. Scans removable drives)
- `IN-412007` OIB Defender Antivirus AV Configuration-Allow Realtime Monitoring (Allowed. Turns on and runs the real-tim…)
- `IN-412026` OIB Defender Antivirus AV Configuration-Remediation action for Low severity threats (Block. Blocks file execution)
- `IN-412027` OIB Defender Antivirus AV Configuration-Remediation action for High severity threats (Quarantine. Moves files to quarantine)
- `IN-412028` OIB Defender Antivirus AV Configuration-Remediation action for Moderate severity threats (Quarantine. Moves files to quarantine)
- `IN-412029` OIB Defender Antivirus AV Configuration-Remediation action for Severe threats (Remove. Removes files from system)

**OIB - ES - Defender Antivirus - D - Security Experience** (3)

- `IN-413001` OIB Defender Antivirus Security Experience-Configuration Protection (Preview) (Tamper Protection (On))
- `IN-413002` OIB Defender Antivirus Security Experience-Disable Family UI ((Enable) The users cannot see the displ…)
- `IN-413003` OIB Defender Antivirus Security Experience-Disable Enhanced Notifications ((Enable) Windows Defender Security Cent…)

**OIB - ES - Defender Antivirus Updates - Ring 2 - UAT** (1)

- `IN-415003` OIB Defender Antivirus Updates Ring 2 UAT-Security Intelligence Updates Channel (Current Channel (Staged) Same as Curren…)

**OIB - ES - Defender Antivirus Updates - Ring 3 - Production** (1)

- `IN-416003` OIB Defender Antivirus Updates Ring 3 Production-Security Intelligence Updates Channel (Configured)

**OIB - ES - Encryption - D - BitLocker (OS Disk)** (2)

- `IN-417004` OIB Encryption BitLocker (OS Disk)-Choose how BitLocker-protected operating system drives can be recovered (Enabled)
- `IN-417008` OIB Encryption BitLocker (OS Disk)-Configure Recovery Password Rotation (Refresh on for Entra ID-joined devices)

**OIB - ES - Encryption - U - Personal Data Encryption** (1)

- `IN-418001` OIB Encryption Personal Data Encryption-Enable Personal Data Encryption (Enable Personal Data Encryption)

**OIB - ES - Windows Firewall - D - Firewall Configuration** (16)

- `IN-420001` OIB Windows Firewall Firewall Configuration-Object Access Audit Filtering Platform Connection (Failure)
- `IN-420002` OIB Windows Firewall Firewall Configuration-Object Access Audit Filtering Platform Packet Drop (Failure)
- `IN-420007` OIB Windows Firewall Firewall Configuration-Domain Profile Firewall Disable Inbound Notifications (True)
- `IN-420010` OIB Windows Firewall Firewall Configuration-Domain Profile Firewall Enable Log Dropped Packets (Enable Logging Of Dropped Packets)
- `IN-420011` OIB Windows Firewall Firewall Configuration-Domain Profile Firewall Enable Log Success Connections (Enable Logging Of Successful Connections)
- `IN-420012` OIB Windows Firewall Firewall Configuration-Domain Profile Firewall Log File Path (SystemRoot System32 logfiles firewall p…)
- `IN-420014` OIB Windows Firewall Firewall Configuration-Private Profile Firewall Disable Inbound Notifications (True)
- `IN-420018` OIB Windows Firewall Firewall Configuration-Private Profile Firewall Enable Log Dropped Packets (Enable Logging Of Dropped Packets)
- `IN-420019` OIB Windows Firewall Firewall Configuration-Private Profile Firewall Enable Log Success Connections (Enable Logging Of Successful Connections)
- `IN-420020` OIB Windows Firewall Firewall Configuration-Private Profile Firewall Log File Path (SystemRoot System32 logfiles firewall p…)
- `IN-420025` OIB Windows Firewall Firewall Configuration-Public Profile Firewall Disable Inbound Notifications (True)
- `IN-420027` OIB Windows Firewall Firewall Configuration-Public Profile Firewall Enable Log Ignored Rules (Disable Logging Of Ignored Rules)
- `IN-420028` OIB Windows Firewall Firewall Configuration-Public Profile Firewall Enable Log Dropped Packets (Enable Logging Of Dropped Packets)
- `IN-420029` OIB Windows Firewall Firewall Configuration-Public Profile Firewall Enable Log Success Connections (Enable Logging Of Successful Connections)
- `IN-420030` OIB Windows Firewall Firewall Configuration-Public Profile Firewall Log File Path (SystemRoot System32 logfiles firewall p…)
- `IN-420031` OIB Windows Firewall Firewall Configuration-Public Profile Firewall Allow Local Ipsec Policy Merge (True)

**OIB - ES - Windows Hello for Business - D - WHfB Configuration** (1)

- `IN-422006` OIB Windows Hello for Business WHfB Configuration-Facial Features Use Enhanced Anti Spoofing (true)

**OIB - ES - Windows LAPS - D - LAPS Configuration** (1)

- `IN-423006` OIB Windows LAPS LAPS Configuration-Automatic Account Management Enabled (The target account will be automaticall…)

**OIB - Microsoft AutoUpdate - D - MAU Configuration** (1)

- `IN-511002` OIB Microsoft AutoUpdate MAU Configuration-Automatically acknowledge data collection policy (Acknowledge - send required data)

**OIB - Microsoft Edge - U - Profiles, Sign-In and Sync** (1)

- `IN-515001` OIB Microsoft Edge Profiles, Sign-In and Sync-Browser sign-in settings (Force users to sign-in to use the brows…)

**OIB - SC - Credential Management - D - Passwordless** (1)

- `IN-424002` OIB Credential Management Passwordless-Enable Passwordless Experience (Enabled. The Passwordless experience wi…)

**OIB - SC - Defender Antivirus - D - Additional Configuration** (7)

- `IN-425001` OIB Defender Antivirus Additional Configuration-Enable Convert Warn To Block (Warn verdicts are converted to block)
- `IN-425002` OIB Defender Antivirus Additional Configuration-Enable Dynamic Signature Dropped Event Reporting (Dynamic Security intelligence update ev…)
- `IN-425004` OIB Defender Antivirus Additional Configuration-Hide Exclusions From Local Admins (If you enable this setting, local admin…)
- `IN-425005` OIB Defender Antivirus Additional Configuration-Oobe Enable Rtp And Sig Update (If you enable this setting, real-time p…)
- `IN-425007` OIB Defender Antivirus Additional Configuration-Quick Scan Include Exclusions (If you set this setting to 1, all files…)
- `IN-425008` OIB Defender Antivirus Additional Configuration-Support Log Location (ProgramData Microsoft IntuneManagementE…)
- `IN-425009` OIB Defender Antivirus Additional Configuration-Disallow Exploit Protection Override ((Enable) Local users cannot make change…)

**OIB - SC - Device Security - D - Administrator Protection** (2)

- `IN-426001` OIB Device Security Administrator Protection-User Account Control Behavior Of The Elevation Prompt For Administrator Protection (Prompt for credentials on the secure de…)
- `IN-426002` OIB Device Security Administrator Protection-User Account Control Type Of Admin Approval Mode (Admin Approval Mode with Administrator…)

**OIB - SC - Device Security - D - Audit and Event Logging** (9)

- `IN-427001` OIB Device Security Audit and Event Logging-Include command line in process creation events (Enabled)
- `IN-427002` OIB Device Security Audit and Event Logging-Control Event Log behavior when the log file reaches its maximum size (Disabled)
- `IN-427004` OIB Device Security Audit and Event Logging-Control Event Log behavior when the log file reaches its maximum size (Disabled)
- `IN-427006` OIB Device Security Audit and Event Logging-Control Event Log behavior when the log file reaches its maximum size (Disabled)
- `IN-427008` OIB Device Security Audit and Event Logging-Control Event Log behavior when the log file reaches its maximum size (Disabled)
- `IN-427010` OIB Device Security Audit and Event Logging-Account Logon Audit Credential Validation (Success+ Failure)
- `IN-427015` OIB Device Security Audit and Event Logging-Account Management Audit Application Group Management (Success+ Failure)
- `IN-427028` OIB Device Security Audit and Event Logging-Object Access Audit Other Object Access Events (Success+ Failure)
- `IN-427030` OIB Device Security Audit and Event Logging-Policy Change Audit MPSSVC Rule Level Policy Change (Success+ Failure)

**OIB - SC - Device Security - D - Local Security Policies** (20)

- `IN-430003` OIB Device Security Local Security Policies-Accounts Limit Local Account Use Of Blank Passwords To Console Logon Only (Enabled)
- `IN-430004` OIB Device Security Local Security Policies-Interactive Logon Smart Card Removal Behavior (Lock Workstation)
- `IN-430005` OIB Device Security Local Security Policies-Microsoft Network Client Digitally Sign Communications Always (Enable)
- `IN-430006` OIB Device Security Local Security Policies-Microsoft Network Client Send Unencrypted Password To Third Party SMB Servers (Disable)
- `IN-430007` OIB Device Security Local Security Policies-Microsoft Network Server Digitally Sign Communications Always (Enable)
- `IN-430008` OIB Device Security Local Security Policies-Network Access Do Not Allow Anonymous Enumeration Of SAM Accounts (Enabled)
- `IN-430009` OIB Device Security Local Security Policies-Network Access Do Not Allow Anonymous Enumeration Of Sam Accounts And Shares (Enabled)
- `IN-430010` OIB Device Security Local Security Policies-Network Access Restrict Anonymous Access To Named Pipes And Shares (Enable)
- `IN-430011` OIB Device Security Local Security Policies-Network Access Restrict Clients Allowed To Make Remote Calls To SAM (O BAG BAD (A;;RC;;;BA))
- `IN-430012` OIB Device Security Local Security Policies-Network Security Do Not Store LAN Manager Hash Value On Next Password Change (Enable)
- `IN-430013` OIB Device Security Local Security Policies-Network Security LAN Manager Authentication Level (Send LM and NTLMv2 responses only. Refu…)
- `IN-430014` OIB Device Security Local Security Policies-Network Security Minimum Session Security For NTLMSSP Based Clients (Require NTLM and 128-bit encryption)
- `IN-430015` OIB Device Security Local Security Policies-Network Security Minimum Session Security For NTLMSSP Based Servers (Require NTLM and 128-bit encryption)
- `IN-430016` OIB Device Security Local Security Policies-User Account Control Behavior Of The Elevation Prompt For Administrators (Prompt for consent on the secure desktop)
- `IN-430017` OIB Device Security Local Security Policies-User Account Control Behavior Of The Elevation Prompt For Standard Users (Prompt for credentials on the secure de…)
- `IN-430018` OIB Device Security Local Security Policies-User Account Control Detect Application Installations And Prompt For Elevation (Enable)
- `IN-430019` OIB Device Security Local Security Policies-User Account Control Only Elevate UI Access Applications That Are Installed In Secure Loc… (Enabled Application runs with UIAccess…)
- `IN-430020` OIB Device Security Local Security Policies-User Account Control Run All Administrators In Admin Approval Mode (Enabled)
- `IN-430021` OIB Device Security Local Security Policies-User Account Control Switch To The Secure Desktop When Prompting For Elevation (Enabled)
- `IN-430023` OIB Device Security Local Security Policies-User Account Control Virtualize File And Registry Write Failures To Per User Locations (Enabled)

**OIB - SC - Device Security - D - Login and Lock Screen** (1)

- `IN-432008` OIB Device Security Login and Lock Screen-Let Apps Activate With Voice Above Lock (Force deny. Windows apps cannot be acti…)

**OIB - SC - Device Security - D - Remote Desktop Services and RPC** (8)

- `IN-434001` OIB Device Security Remote Desktop Services and RPC-Enable RPC Endpoint Mapper Client Authentication (Enabled)
- `IN-434002` OIB Device Security Remote Desktop Services and RPC-Restrict Unauthenticated RPC clients (Enabled)
- `IN-434003` OIB Device Security Remote Desktop Services and RPC-Do not allow passwords to be saved (Enabled)
- `IN-434005` OIB Device Security Remote Desktop Services and RPC-Always prompt for password upon connection (Enabled)
- `IN-434006` OIB Device Security Remote Desktop Services and RPC-Require secure RPC communication (Enabled)
- `IN-434007` OIB Device Security Remote Desktop Services and RPC-Require use of specific security layer for remote (RDP) connections (Enabled)
- `IN-434008` OIB Device Security Remote Desktop Services and RPC-Require user authentication for remote connections by using Network Level Authentication (Enabled)
- `IN-434009` OIB Device Security Remote Desktop Services and RPC-Set client connection encryption level (Enabled)

**OIB - SC - Device Security - D - Script File Associations** (1)

- `IN-435001` OIB Device Security Script File Associations-Default Associations Configuration (PD94bWwgdmVyc2lvbj0iMS4wIiBlbmNvZGluZz0…)

**OIB - SC - Device Security - D - Security Hardening** (16)

- `IN-436005` OIB Device Security Security Hardening-Turn off Internet download for Web publishing and online ordering wizards (Enabled)
- `IN-436011` OIB Device Security Security Hardening-Remote host allows delegation of non-exportable credentials (Enabled)
- `IN-436016` OIB Device Security Security Hardening-Apply UAC restrictions to local accounts on network logons (Enabled)
- `IN-436019` OIB Device Security Security Hardening-Enable Structured Exception Handling Overwrite Protection (SEHOP) (Enabled)
- `IN-436020` OIB Device Security Security Hardening-MSS (DisableIPSourceRouting IPv6) IP source routing protection level (protects against pa… (Enabled)
- `IN-436021` OIB Device Security Security Hardening-MSS (DisableIPSourceRouting) IP source routing protection level (protects against packet… (Enabled)
- `IN-436022` OIB Device Security Security Hardening-MSS (EnableICMPRedirect) Allow ICMP redirects to override OSPF generated routes (Disabled)
- `IN-436023` OIB Device Security Security Hardening-MSS (NoNameReleaseOnDemand) Allow the computer to ignore NetBIOS name release requests ex… (Enabled)
- `IN-436024` OIB Device Security Security Hardening-MSS (ScreenSaverGracePeriod) The time in seconds before the screen saver grace period exp… (Enabled)
- `IN-436025` OIB Device Security Security Hardening-Prohibit installation and configuration of Network Bridge on your DNS domain network (Enabled)
- `IN-436026` OIB Device Security Security Hardening-Require domain users to elevate when setting a network's location (Enabled)
- `IN-436031` OIB Device Security Security Hardening-Minimize the number of simultaneous connections to the Internet or a Windows Domain (Enabled)
- `IN-436032` OIB Device Security Security Hardening-Prohibit connection to non-domain networks when connected to domain authenticated network (Enabled)
- `IN-436071` OIB Device Security Security Hardening-Configure Xbox Accessory Management Service Startup Mode (Disabled)
- `IN-436078` OIB Device Security Security Hardening-Allow Windows Ink Workspace (ink workspace is enabled (feature is tu…)
- `IN-436079` OIB Device Security Security Hardening-Allow Projection From PC (Your PC can discover and project to oth…)

**OIB - SC - Device Security - D - Windows Subsystem for Linux** (4)

- `IN-440003` OIB Device Security Windows Subsystem for Linux-Allow custom system distribution configuration (Disabled)
- `IN-440004` OIB Device Security Windows Subsystem for Linux-Allow kernel command line configuration (Disabled)
- `IN-440008` OIB Device Security Windows Subsystem for Linux-Allow the Inbox version of the Windows Subsystem for Linux (Disabled)
- `IN-440009` OIB Device Security Windows Subsystem for Linux-Allow user setting firewall configuration (Disabled)

**OIB - SC - Device Security - U - Device Guard, Credential Guard and HVCI** (8)

- `IN-441001` OIB Device Security Device Guard, Credential Guard and HVCI-Configure System Guard Launch (Unmanaged Enables Secure Launch if supp…)
- `IN-441002` OIB Device Security Device Guard, Credential Guard and HVCI-Credential Guard ((Enabled with UEFI lock) Turns on Crede…)
- `IN-441003` OIB Device Security Device Guard, Credential Guard and HVCI-Enable Virtualization Based Security (enable virtualization based security)
- `IN-441004` OIB Device Security Device Guard, Credential Guard and HVCI-Machine Identity Isolation ((Disabled) Machine password is only LSA…)
- `IN-441005` OIB Device Security Device Guard, Credential Guard and HVCI-Require Platform Security Features (Turns on VBS with Secure Boot and direc…)
- `IN-441006` OIB Device Security Device Guard, Credential Guard and HVCI-Configure Lsa Protected Process (Enabled with UEFI lock. LSA will run as…)
- `IN-441007` OIB Device Security Device Guard, Credential Guard and HVCI-Hypervisor Enforced Code Integrity ((Enabled with UEFI lock) Turns on Hyper…)
- `IN-441008` OIB Device Security Device Guard, Credential Guard and HVCI-Require UEFI Memory Attributes Table (Require UEFI Memory Attributes Table)

**OIB - SC - Device Security - U - Power and Device Lock** (2)

- `IN-442001` OIB Device Security Power and Device Lock-Require a password when a computer wakes (on battery) (Enabled)
- `IN-442002` OIB Device Security Power and Device Lock-Require a password when a computer wakes (plugged in) (Enabled)

**OIB - SC - Device Security - U - Windows Spotlight and Org Messages** (3)

- `IN-444003` OIB Device Security Windows Spotlight and Org Messages-Allow Windows Spotlight On Settings (Block)
- `IN-444004` OIB Device Security Windows Spotlight and Org Messages-Disable Cloud Optimized Content (Disabled)
- `IN-444005` OIB Device Security Windows Spotlight and Org Messages-Enable delivery of organizational messages (Enabled)

**OIB - SC - Internet Explorer (Legacy) - D - Security** (36)

- `IN-445001` OIB Internet Explorer (Legacy) Security-Allow software to run or install even if the signature is invalid (Disabled)
- `IN-445004` OIB Internet Explorer (Legacy) Security-Do not allow ActiveX controls to run in Protected Mode when Enhanced Protected Mode is en… (Enabled)
- `IN-445006` OIB Internet Explorer (Legacy) Security-Turn on 64-bit tab processes when running in Enhanced Protected Mode on 64-bit versions o… (Enabled)
- `IN-445010` OIB Internet Explorer (Legacy) Security-Allow cut, copy or paste operations from the clipboard via script (Enabled)
- `IN-445013` OIB Internet Explorer (Legacy) Security-Allow only approved domains to use ActiveX controls without prompt (Enabled)
- `IN-445014` OIB Internet Explorer (Legacy) Security-Allow only approved domains to use the TDC ActiveX control (Enabled)
- `IN-445015` OIB Internet Explorer (Legacy) Security-Allow script-initiated windows without size or position constraints (Enabled)
- `IN-445016` OIB Internet Explorer (Legacy) Security-Allow scripting of Internet Explorer WebBrowser controls (Enabled)
- `IN-445021` OIB Internet Explorer (Legacy) Security-Don't run antimalware programs against ActiveX controls (Enabled)
- `IN-445024` OIB Internet Explorer (Legacy) Security-Enable dragging of content from different domains across windows (Enabled)
- `IN-445025` OIB Internet Explorer (Legacy) Security-Enable dragging of content from different domains within a window (Enabled)
- `IN-445026` OIB Internet Explorer (Legacy) Security-Include local path when user is uploading files to a server (Enabled)
- `IN-445027` OIB Internet Explorer (Legacy) Security-Initialize and script ActiveX controls not marked as safe (Enabled)
- `IN-445032` OIB Internet Explorer (Legacy) Security-Run .NET Framework-reliant components not signed with Authenticode (Enabled)
- `IN-445033` OIB Internet Explorer (Legacy) Security-Run .NET Framework-reliant components signed with Authenticode (Enabled)
- `IN-445040` OIB Internet Explorer (Legacy) Security-Web sites in less privileged Web content zones can navigate into this zone (Enabled)
- `IN-445043` OIB Internet Explorer (Legacy) Security-Don't run antimalware programs against ActiveX controls (Disable)
- `IN-445044` OIB Internet Explorer (Legacy) Security-Initialize and script ActiveX controls not marked as safe (Disable)
- `IN-445046` OIB Internet Explorer (Legacy) Security-Don't run antimalware programs against ActiveX controls (Disable)
- `IN-445062` OIB Internet Explorer (Legacy) Security-Only allow approved domains to use ActiveX controls without prompt (Enable)
- `IN-445063` OIB Internet Explorer (Legacy) Security-Only allow approved domains to use the TDC ActiveX control (Enable)
- `IN-445064` OIB Internet Explorer (Legacy) Security-Allow script-initiated windows without size or position constraints (Disable)
- `IN-445070` OIB Internet Explorer (Legacy) Security-Don't run antimalware programs against ActiveX controls (Disable)
- `IN-445073` OIB Internet Explorer (Legacy) Security-Enable dragging of content from different domains across windows (Disable)
- `IN-445074` OIB Internet Explorer (Legacy) Security-Enable dragging of content from different domains within a window (Disable)
- `IN-445076` OIB Internet Explorer (Legacy) Security-Include local directory path when uploading files to a server (Disable)
- `IN-445077` OIB Internet Explorer (Legacy) Security-Initialize and script ActiveX controls not marked as safe (Disable)
- `IN-445082` OIB Internet Explorer (Legacy) Security-Run .NET Framework-reliant components not signed with Authenticode (Disable)
- `IN-445083` OIB Internet Explorer (Legacy) Security-Run .NET Framework-reliant components signed with Authenticode (Disable)
- `IN-445093` OIB Internet Explorer (Legacy) Security-Web sites in less privileged Web content zones can navigate into this zone (Disable)
- `IN-445094` OIB Internet Explorer (Legacy) Security-Don't run antimalware programs against ActiveX controls (Disable)
- `IN-445095` OIB Internet Explorer (Legacy) Security-Initialize and script ActiveX controls not marked as safe (Disable)
- `IN-445098` OIB Internet Explorer (Legacy) Security-Prevent bypassing SmartScreen Filter warnings about files that are not commonly downloade… (Enabled)
- `IN-445106` OIB Internet Explorer (Legacy) Security-Turn on the auto-complete feature for user names and passwords on forms (Disabled)
- `IN-445107` OIB Internet Explorer (Legacy) Security-Remove Run this time button for outdated ActiveX controls in Internet Explorer (Enabled)
- `IN-445108` OIB Internet Explorer (Legacy) Security-Turn off blocking of outdated ActiveX controls for Internet Explorer (Disabled)

**OIB - SC - Microsoft Accounts - D - Configuration** (1)

- `IN-446005` OIB Microsoft Accounts Configuration-Accounts Block Microsoft Accounts (Users can't add or log on with Microsof…)

**OIB - SC - Microsoft Edge - U - Password Management** (3)

- `IN-451001` OIB Microsoft Edge Password Management-Allow users to be alerted if their passwords are found to be unsafe (Enabled)
- `IN-451002` OIB Microsoft Edge Password Management-Allow users to get a strong password suggestion whenever they are creating an account onl… (Enabled)
- `IN-451003` OIB Microsoft Edge Password Management-Configures a setting that asks users to enter their device password while using password… (Enabled)

**OIB - SC - Microsoft Edge - U - Profiles, Sign-In and Sync** (7)

- `IN-452001` OIB Microsoft Edge Profiles, Sign-In and Sync-Automatically import another browser's data and settings at first run (Disabled)
- `IN-452003` OIB Microsoft Edge Profiles, Sign-In and Sync-Configure whether a user always has a default profile automatically signed in with their… (Enabled)
- `IN-452004` OIB Microsoft Edge Profiles, Sign-In and Sync-Enable profile creation from the Identity flyout menu or the Settings page (Disabled)
- `IN-452006` OIB Microsoft Edge Profiles, Sign-In and Sync-Force synchronization of browser data and do not show the sync consent prompt (Enabled)
- `IN-452007` OIB Microsoft Edge Profiles, Sign-In and Sync-Single sign-on for work or school sites using this profile enabled (Enabled)
- `IN-452008` OIB Microsoft Edge Profiles, Sign-In and Sync-Allow M365 authentication popups in work profiles (Enabled)
- `IN-452010` OIB Microsoft Edge Profiles, Sign-In and Sync-Enable sign-in to Microsoft Edge using non-Microsoft accounts (Disabled)

**OIB - SC - Microsoft Edge - U - User Experience** (4)

- `IN-453003` OIB Microsoft Edge User Experience-Control whether an informational webpage for Edge for Business is shown in the new tab af… (Disabled)
- `IN-453007` OIB Microsoft Edge User Experience-Shows button on native PDF viewer in Microsoft Edge that allows users to sign up for Adob… (Disabled)
- `IN-453009` OIB Microsoft Edge User Experience-Allow notifications on specific sites ([ .]microsoft.com;[ .]cloud.microsoft)
- `IN-453014` OIB Microsoft Edge User Experience-Configure whether the Discover or Work feed tabs are shown on the New Tab Page. (Enabled)

**OIB - SC - Microsoft Office - U - Config and Experience** (13)

- `IN-456002` OIB Microsoft Office Config and Experience-Suppress file format compatibility dialog box for OpenDocument Spreadsheet format (Enabled)
- `IN-456005` OIB Microsoft Office Config and Experience-Allow users who aren’t admins to install language accessory packs (Enabled)
- `IN-456006` OIB Microsoft Office Config and Experience-Notify users if they do not have proofing tools for a language they use (Enabled)
- `IN-456007` OIB Microsoft Office Config and Experience-File links open preference default selection as Desktop App (Enabled)
- `IN-456008` OIB Microsoft Office Config and Experience-Hide Microsoft cloud-based file locations in the Backstage view (Enabled)
- `IN-456010` OIB Microsoft Office Config and Experience-Allow users to receive and respond to in-product surveys from Microsoft (Disabled)
- `IN-456013` OIB Microsoft Office Config and Experience-Automatically configure profile based on Active Directory Primary SMTP address (Enabled)
- `IN-456014` OIB Microsoft Office Config and Experience-Prefer the provided account email in AutoDiscover auth prompts. (Enabled)
- `IN-456017` OIB Microsoft Office Config and Experience-Use Cached Exchange Mode for new and existing Outlook profiles (Enabled)
- `IN-456018` OIB Microsoft Office Config and Experience-Use the Online Global Address List for Nickname Resolution (Enabled)
- `IN-456021` OIB Microsoft Office Config and Experience-Make Outlook the default program for E-mail, Contacts, and Calendar (Enabled)
- `IN-456023` OIB Microsoft Office Config and Experience-Suppress file format compatibility dialog box for OpenDocument Presentation format (Enabled)
- `IN-456025` OIB Microsoft Office Config and Experience-Do not display file format compatibility dialog box for OpenDocument text format (Enabled)

**OIB - SC - Microsoft OneDrive - D - Configuration** (3)

- `IN-458001` OIB Microsoft OneDrive Configuration-Allow syncing OneDrive accounts for only specific organizations (Enabled)
- `IN-458006` OIB Microsoft OneDrive Configuration-Prevent users from redirecting their Windows known folders to their PC (Enabled)
- `IN-458009` OIB Microsoft OneDrive Configuration-Silently sign in users to the OneDrive sync app with their Windows credentials (Enabled)

**OIB - SC - Microsoft OneDrive - U - Configuration** (3)

- `IN-459001` OIB Microsoft OneDrive Configuration-Allow users to choose how to handle Office file sync conflicts (Enabled)
- `IN-459002` OIB Microsoft OneDrive Configuration-Disable the tutorial that appears at the end of OneDrive Setup (Enabled)
- `IN-459003` OIB Microsoft OneDrive Configuration-Prevent users from changing the location of their OneDrive folder (Enabled)

**OIB - SC - Network Security - D - Disable NTLM** (2)

- `IN-462001` OIB Network Security Disable NTLM-Network Security LAN Manager Authentication Level (Send LM and NTLMv2 responses only. Refu…)
- `IN-462003` OIB Network Security Disable NTLM-Network Security Restrict NTLM Outgoing NTLM Traffic To Remote Servers (Allow all)

**OIB - SC - Windows Hello for Business - D - Cloud Kerberos Trust** (2)

- `IN-464001` OIB Windows Hello for Business Cloud Kerberos Trust-Cloud Kerberos Ticket Retrieval Enabled (Enabled)
- `IN-464002` OIB Windows Hello for Business Cloud Kerberos Trust-Use Cloud Trust For On Prem Auth (Enabled)

**OIB - SC - Windows Update for Business - D - Delivery Optimisation** (5)

- `IN-465002` OIB Windows Update for Business Delivery Optimisation-DO Allow VPN Peer Caching (Not allowed)
- `IN-465003` OIB Windows Update for Business Delivery Optimisation-DO Download Mode (HTTP blended with peering behind the sa…)
- `IN-465004` OIB Windows Update for Business Delivery Optimisation-DO Group Id Source (Entra ID Tenant ID)
- `IN-465008` OIB Windows Update for Business Delivery Optimisation-DO Min Battery Percentage Allowed To Upload (40)
- `IN-465012` OIB Windows Update for Business Delivery Optimisation-DO Restrict Peer Selection By (Local discovery (DNS-SD))

**OIB - SC - Windows Update for Business - D - Reports and Telemetry** (4)

- `IN-466001` OIB Windows Update for Business Reports and Telemetry-Allow device name to be sent in Windows diagnostic data (Allowed)
- `IN-466003` OIB Windows Update for Business Reports and Telemetry-Configure Telemetry Opt In Change Notification (Disable telemetry change notifications)
- `IN-466004` OIB Windows Update for Business Reports and Telemetry-Configure Telemetry Opt In Settings Ux (Disable Telemetry opt-in Settings)
- `IN-466005` OIB Windows Update for Business Reports and Telemetry-Allow Temporary Enterprise Feature Control (Allowed)

**OIB - SC - Windows User Experience - D - Automatic Restart Sign-On** (2)

- `IN-467001` OIB Windows User Experience Automatic Restart Sign-On-Configure the mode of automatically signing in and locking last interactive user after a… (Enabled)
- `IN-467002` OIB Windows User Experience Automatic Restart Sign-On-Sign-in and lock last interactive user automatically after a restart (Enabled)

**OIB - SC - Windows User Experience - D - Feature Configuration** (4)

- `IN-468002` OIB Windows User Experience Feature Configuration-Prevent automatic download of applications associated with device metadata (Enabled)
- `IN-468004` OIB Windows User Experience Feature Configuration-Disable Share App Promotions (Promotional Apps on ShareSheet are Disa…)
- `IN-468007` OIB Windows User Experience Feature Configuration-Allow Indexing Encrypted Stores Or Items (Block)
- `IN-468009` OIB Windows User Experience Feature Configuration-Do Not Use Web Results (Not allowed. Queries won't be performed…)

</details>

## What I need

1. CA-213/CA-214: accept?
2. Conditional Access: restore the shipped baseline in Documents (recommended), or accept some changes (which)?
3. The 273 removed Intune controls: intended clean-up (remove from the catalogue), or keep them (restore the files)?
