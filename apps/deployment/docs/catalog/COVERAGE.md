# Secure Plus – Control Catalogue Coverage Report

Status: analysis of 8 Nov 2026 (sections 1–10, unchanged below), **acted on in 4.0.0-beta.2** (section 0).

## 0. Status after 4.0.0-beta.2 (9 Nov 2026)

The catalogue (3,333 controls, Documents@5900c7e) now has **195 tenant settings** (`TS-` controls, kind *TenantSetting*) that cover every area of
the brief. 131 are assessed automatically: 93 through Microsoft Graph and 38 through the read-only PowerShell collector (Exchange Online,
Security & Compliance, Teams). 64 are manual checks with portal and click path. Source: `Documents/ModernWork_SecurePlus_Baselines/TenantSettings/`.

| Area | Tenant settings | Graph | PowerShell | Manual |
|---|---:|---:|---:|---:|
| Microsoft Entra ID | 34 | 31 | 0 | 3 |
| Conditional Access | 8 | 6 | 0 | 2 |
| Microsoft Intune | 24 | 22 | 0 | 2 |
| Microsoft Defender XDR | 14 | 6 | 0 | 8 |
| Exchange Online and Defender for Office 365 | 23 | 0 | 22 | 1 |
| SharePoint and OneDrive | 12 | 8 | 0 | 4 |
| Microsoft Teams | 14 | 2 | 11 | 1 |
| Microsoft Purview | 10 | 2 | 5 | 3 |
| Copilot and AI | 8 | 1 | 0 | 7 |
| Power Platform | 7 | 0 | 0 | 7 |
| Microsoft 365 admin center | 10 | 8 | 0 | 2 |
| Global Secure Access | 5 | 3 | 0 | 2 |
| Windows 365 and Azure Virtual Desktop | 7 | 2 | 0 | 5 |
| Hybrid identity | 7 | 1 | 0 | 6 |
| Microsoft Azure | 12 | 1 | 0 | 11 |
| **Total** | **195** | **93** | **38** | **64** |

- *Missing* and *Knowledge only* areas in section 4 now have at least assessable or manual tenant settings; Power BI / Fabric is still not covered.
- Settings that need a licence the tenant lacks are reported *Info: not applicable* and not scored.
- Azure is manual (except MFA for Azure management, checked through Conditional Access) until Azure Lighthouse in 5.0.
- Power Platform and most Copilot settings have no app-only read API; they stay manual until a Power Platform admin connection is approved.
- Deployment of tenant settings is manual or by the stated method; Graph deployment of Entra tenant settings is proposed for 4.4, PowerShell
  deployment for 4.5.

## 1. Source and method

- **Controls**: the export of `secureplus.CatalogControl` from the Secure Plus database (8 Nov 2026): **3,238 controls**,
  12 columns. It matches `catalog.json.gz` shipped with 4.0.0-beta.1 control for control (same IDs, kinds and evaluation types).
- **Knowledge**: the CIS and Microsoft benchmark recommendations already loaded in the knowledge base (`baselines.json.gz`, 59 baselines,
  10,507 recommendations). The *CIS recs* column counts recommendations in the **latest** CIS benchmark for the area
  (Microsoft 365 Foundations 7.0.0, Azure Foundations 7.0.0 and Compute/Database/Storage 2.0.0, Power Platform 1.0.1, iOS 26 Intune,
  macOS 26 Intune, Android 1.6.0, Windows Server 2025 2.1.1). They are knowledge only: not controls, not assessed, not deployable.
- **Matching**: each control's name, setting and category were matched to the areas below by keyword, then each area was reviewed by hand.
  Device-side settings (Intune, Endpoint Security, Microsoft baseline) are counted only in endpoint, Windows 365 and hybrid areas;
  a device setting that mentions "guest" or "sharing" (for example *insecure guest logons*, Outlook GPO settings) is not a tenant setting
  and is not counted against Entra, Exchange, SharePoint or Teams. A control can count in more than one area.
- **Assess method** comes from each control's evaluation type: Conditional Access, Endpoint Security and Intune controls are compared with
  the tenant through Microsoft Graph; 50 of the 77 assessment checks are automated (47 Graph, 3 DNS) and 27 return *Manual* with the portal
  path; the 843 Microsoft baseline rows and 532 Standards are *Manual*. **Deploy method**: Graph for Conditional Access, Endpoint Security
  and Intune; GPO for Microsoft baseline rows; Manual for Standards.
- **Tiers**: today the only tier-like grouping on controls is the *Golden-Tenant* policy set (160 controls) plus the experience profiles.
  Tier templates (applicability and overrides per tier) arrive in 4.2, so the *Tiers* column shows Golden-Tenant membership.
- Status: **Covered** = deployable controls exist for the key settings and are assessed; **Partial** = some controls, key settings
  missing (*assess only* = checks but nothing deployable); **Knowledge only** = only GDS Standards (tasks) or CIS recommendations;
  **Missing** = nothing. *(OIB)* = coverage depends on OpenIntuneBaseline content.

This report was cross-checked against the Copilot draft (8 Nov 2026); differences are listed in section 9.

## 2. What the catalogue contains

| Kind | Controls | Source | In a policy set? | Assess | Deploy |
|---|---|---|---|---|---|
| Intune | 1,595 | OpenIntuneBaseline 1,211 (Windows 928, macOS 174, Android 51, iOS 43, Windows 365 15); Microsoft Android/iOS security frameworks 384 | No | Graph | Graph |
| MicrosoftBaseline | 843 | Microsoft security baselines: Windows 11 (365), Windows Server 2025 (317), Microsoft 365 Apps (161) | No | Manual | GPO |
| Standard | 532 | GDS service tasks, Phase 0–4 (process, not settings) | Phase sets only | Manual | Manual |
| EndpointSecurity | 131 | GDS: Firewall 60, Antivirus 28, ASR 21, Windows Security 13, App and browser control 5, Defender updates 3, EDR 1 | Yes (06–08) | Graph | Graph |
| AssessmentCheck | 77 | GDS tenant checks (50 automated, 27 manual) | No | Graph / DNS / Manual | – |
| ConditionalAccess | 60 | GDS Conditional Access baseline (report-only on deploy) | Yes (00–05) | Graph | Graph |

Only the 191 Conditional Access and Endpoint Security controls belong to baseline policy sets (00–08; Golden-Tenant = 160).
Intune, Microsoft baseline and check controls cannot be added to a baseline today (policy and policy-set editing is the next piece of work).

## 3. Key findings

1. **37% of the catalogue (1,211 controls) comes from OpenIntuneBaseline (GPL-3.0).** macOS, disk encryption, WHfB/LAPS/Credential Guard,
   update rings, Edge, Office and Windows 365 coverage depends on it. Decision: the controls stay visible, marked with their source; GDS
   confirms it is compliant with the licence (IMPORT_PLAN D3).
2. **Microsoft 365 services are assessed, not configured.** Exchange, SharePoint, Teams, Purview and Defender for Office have checks (most
   of them Manual) and **no deployable settings**. 19 areas are *Partial (assess only)*.
3. **Service checks are manual.** 27 of 77 checks return *Manual* with a portal path, and they are exactly the Exchange (except SPF/DKIM/DMARC),
   Teams, Defender for Office, DLP, audit, SharePoint link and backup checks: they need Exchange, Security & Compliance or Teams data that
   Graph v1.0 does not expose. Read-only PowerShell is
   needed (IMPORT_PLAN §5–6).
4. **The 843 Microsoft baseline rows are never assessed automatically** (evaluation *Manual*): they are GPO settings with no Intune
   equivalent recorded. Assessing them needs the Intune security-baseline mapping or device data.
5. **Standards make areas look covered when they are not.** 22 areas are *Knowledge only*: GDS tasks and/or CIS recommendations, no
   technical control.
6. **Endpoint settings are duplicated across sources.** Defender Antivirus: OIB 81, Microsoft baseline 43, Endpoint Security 28. Firewall:
   Endpoint Security 61, OIB 48, Microsoft baseline 35. ASR: OIB 38, Endpoint Security 21, Microsoft baseline 2.
7. **358 Internet Explorer settings (11%)** (Microsoft baseline 236, OIB 122), relevant only to IE mode in Edge.
8. **Licensing was free text.** Conditional Access, Endpoint Security and Intune rows had one SKU string each, Standards had 70+ wordings
   ("Applicable Microsoft Purview DLP licensing", "None for governance"), and the 843 baseline rows and 77 checks had none. Licensing
   belongs to the knowledge base (619 SKUs, 1,224 capabilities, requirement → capability links), so **the `Licensing` column was removed
   from `secureplus.CatalogControl`** (migration V0010) and from the Catalog page and export.
9. **A catalogue reload deletes and rewrites `secureplus.CatalogControl` and the previous `pol.*` repository version.** There is no Lead
   editing of controls yet, so nothing is lost today, but this must change before Leads can add or edit controls (IMPORT_PLAN §1, D4).
10. **Data gaps:** no framework mapping on 443 Intune rows, 215 Standards, 19 checks and 1 CA policy; 420 baseline rows map only to
    "Microsoft Security Baseline"; severity is 58% Low, only 4 Critical (all checks).
11. **The count is 3,238, not 3,268.** 3,268 is the 1.2 figure (before release 2.0 merged identical duplicates into aliases). No controls
    are missing. `docs/architecture/catalog-database.md` is corrected in this change; README and WORKPLAN describe 1.2 and stay as history.
12. **Corrected assumptions:** device code flow, authentication transfer, token protection and guest terms of use **are** covered
    (CA-306, CA-307, CA-209, CA-210). Disk encryption, account protection and update rings **are** covered, through OIB Intune settings rather
    than Endpoint Security policies.

## 4. Summary

| Status | Areas | Meaning |
|---|---|---|
| Covered | 16 | Deployable controls exist (5 of them only through OIB) |
| Partial | 37 | Some controls; key settings missing (19 of them assess only) |
| Knowledge only | 22 | Only GDS Standards and/or CIS recommendations |
| Missing | 9 | Nothing |
| **Total** | **84** | Every area in the brief appears, plus Power BI / Fabric |

## 5. Coverage matrix

Columns: CA = Conditional Access, ES = Endpoint Security, Check (auto) = assessment checks (automated), Standard = GDS task,
CIS recs = knowledge-base recommendations (not controls).

### Entra ID

| Area | Status | CA | ES | Intune | MS baseline | Check (auto) | Standard | CIS recs | Assess | Deploy | Tiers | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Authentication methods | Knowledge only | 0 | 0 | 0 | 0 | 0 | 8 | 7 | Manual | Manual | – | No authentication-methods policy control or check (passkeys/FIDO2, TAP, QR code, SMS/voice off, system-preferred MFA). CIS M365 has the recommendations. |
| Authorization policy & user settings (consent, app registration, guest access, tenant creation) | Partial | 0 | 0 | 0 | 0 | 3 (3) | 4 | 8 | Graph / Manual | Manual | – | 3 Graph checks (user consent, app registration, guest access level). Nothing deployable; admin consent workflow, tenant creation, LinkedIn missing. |
| Cross-tenant access / B2B | Partial | 4 | 0 | 0 | 0 | 2 (0) | 1 | 3 | Graph / Manual | Graph | Golden-Tenant 3 | CA policies for external users (CA-210, 212, 507, 508). Cross-tenant access settings and invitation domain restrictions not captured. |
| PIM role settings | Partial | 0 | 0 | 0 | 0 | 2 (1) | 5 | 2 | Graph / Manual | Manual | – | 2 checks (approval on activation is manual; no permanent assignment is Graph). PIM role settings not captured as deployable settings. |
| Access reviews | Knowledge only | 0 | 0 | 0 | 0 | 0 | 6 | 2 | Manual | Manual | – | Standards and CIS only. |
| Entitlement management | Knowledge only | 0 | 0 | 0 | 0 | 0 | 0 | 0 | none | none | – | Standards only. |
| Password protection / lockout | Partial | 0 | 0 | 0 | 0 | 2 (2) | 3 | 5 | Graph / Manual | Manual | – | 1 Graph check (passwords don't expire). Banned passwords and smart lockout values not captured. |
| SSPR | Knowledge only | 0 | 0 | 0 | 0 | 0 | 1 | 4 | Manual | Manual | – | Standards and CIS only. |
| Device settings (join, device limit, local admin) | Partial | 1 | 0 | 0 | 0 | 1 (0) | 0 | 5 | Graph / Manual | Graph | – | CA-211 (MFA to register/join) and a manual local-admin check. Join rights, device limit, BitLocker key read not captured. |
| Group settings | Partial | 0 | 0 | 0 | 0 | 2 (2) | 2 | 4 | Graph / Manual | Manual | – | Dynamic-groups check only; group creation, naming, expiry missing. |
| Workload identities | Partial | 0 | 0 | 0 | 0 | 0 | 0 | 7 | Manual | none | – | CA-700/701. App credential types, lifetimes and owners not checked. |
| Identity Protection (risk) | **Covered** | 11 | 0 | 0 | 0 | 1 (1) | 7 | 3 | Graph / Manual | Graph | Golden-Tenant 6 | 11 risk-based CA policies + high-risk-users check. |
| Entra Connect / Cloud Sync | Knowledge only | 0 | 0 | 0 | 0 | 1 (1) | 0 | 2 | Graph / Manual | none | – | Only the 'privileged accounts are cloud-only' check. Sync health and Tier 0 protection not covered. |
| Break-glass / emergency access | **Covered** | 0 | 0 | 0 | 0 | 1 (1) | 3 | 2 | Graph / Manual | Manual | – | CA-110 + break-glass check + Standards. |

### Conditional Access

| Area | Status | CA | ES | Intune | MS baseline | Check (auto) | Standard | CIS recs | Assess | Deploy | Tiers | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Core policies (MFA, admins, devices, locations, sessions, legacy auth) | **Covered** | 49 | 0 | 0 | 0 | 14 (14) | 28 | 15 | Graph / Manual | Graph | Golden-Tenant 26 | Most of the 60 GDS CA policies. Assessed and deployed through Graph (report-only first). |
| Device code flow / authentication transfer | **Covered** | 2 | 0 | 0 | 0 | 1 (1) | 0 | 2 | Graph / Manual | Graph | – | CA-306, CA-307 + check. |
| Token protection | **Covered** | 1 | 0 | 0 | 0 | 0 | 0 | 1 | Graph / Manual | Graph | – | CA-209 (preview). |
| Protected actions | **Missing** | 0 | 0 | 0 | 0 | 0 | 0 | 0 | none | none | – | No protected-actions policy or authentication context. |
| Guest terms of use | **Covered** | 1 | 0 | 0 | 0 | 0 | 0 | 0 | Graph | Graph | Golden-Tenant 1 | CA-210. |
| Windows 365 / AVD sign-in | **Missing** | 0 | 0 | 0 | 0 | 0 | 0 | 0 | none | none | – | No CA policy targeting Windows 365 / AVD. |
| Generative-AI apps | **Missing** | 0 | 0 | 0 | 0 | 0 | 0 | 0 | none | none | – | No CA policy for AI apps (GSA web filtering would cover it). |

### Intune / endpoint

| Area | Status | CA | ES | Intune | MS baseline | Check (auto) | Standard | CIS recs | Assess | Deploy | Tiers | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Disk encryption (BitLocker/FileVault) | **Covered** (OIB) | 0 | 0 | 35 | 22 | 1 (1) | 15 | 1 | Graph / Manual | Graph / GPO | – | BitLocker and FileVault settings come from OpenIntuneBaseline; Microsoft baseline rows + check. No Endpoint Security 'Disk encryption' policy of GDS's own. |
| Account protection (WHfB, Credential Guard, LAPS) | **Covered** (OIB) | 0 | 0 | 22 | 3 | 1 (1) | 1 | 1 | Graph / Manual | Graph / GPO | – | WHfB, LAPS, Credential Guard from OIB; LAPS/LSA from the Microsoft baseline; LAPS check. |
| Device control (removable storage) | Partial | 0 | 2 | 10 | 6 | 0 | 4 | 0 | Graph / Manual | Graph / GPO | Golden-Tenant 2 | Device-installation restriction (Microsoft baseline) and some items; no Endpoint Security 'Device control' policy. |
| App Control for Business / AppLocker | **Missing** (clients) | 0 | 0 | 0 | 14 | 0 | 0 | 0 | Manual | GPO | – | Only AppLocker for domain controllers (Server 2025 baseline). |
| Endpoint Privilege Management | **Missing** | 0 | 0 | 0 | 0 | 0 | 0 | 0 | none | none | – | Nothing (UAC settings are not EPM). |
| EDR | Partial | 0 | 1 | 0 | 1 | 1 (1) | 4 | 0 | Graph / Manual | Graph / GPO | Golden-Tenant 1 | 1 EDR policy + onboarding check. EDR in block mode, device groups missing. |
| Update rings / Autopatch | **Covered** (OIB) | 0 | 0 | 70 | 0 | 2 (2) | 2 | 0 | Graph / Manual | Graph | – | WUfB rings, driver and macOS updates (OIB) + 2 checks. Autopatch not covered. |
| Autopilot / ESP / enrollment restrictions | Partial | 0 | 0 | 0 | 0 | 1 (1) | 4 | 1 | Graph / Manual | Manual | – | Personal-device enrolment check only; no Autopilot profile or ESP. |
| Compliance policies (per platform) | **Covered** | 14 | 0 | 105 | 0 | 3 (3) | 45 | 1 | Graph / Manual | Graph | Golden-Tenant 10 | Android (Microsoft framework), macOS/Windows/W365 (OIB) + checks. iOS device compliance missing. |
| macOS | **Covered** (OIB) | 0 | 0 | 174 | 0 | 0 | 0 | 100 | Graph / Manual | Graph | – | All 174 macOS settings come from OpenIntuneBaseline. |
| iOS/iPadOS device (MDM) | Knowledge only | 0 | 0 | 0 | 0 | 0 | 0 | 94 | Manual | none | – | App protection only; no device restrictions or compliance for enrolled iPhones/iPads. CIS iOS 26 Intune benchmark in the knowledge base. |
| Android Enterprise | **Covered** | 0 | 0 | 154 | 0 | 0 | 0 | 45 | Graph / Manual | Graph | – | Microsoft Android Enterprise framework (fully managed L1–L3, work profile L1/L3). |
| Linux | **Missing** | 0 | 0 | 0 | 0 | 0 | 0 | 0 | none | none | – | Matches are Windows Subsystem for Linux; no Linux device controls. |
| App protection (MAM) | **Covered** | 2 | 0 | 324 | 0 | 2 (2) | 0 | 0 | Graph | Graph | Golden-Tenant 1 | Microsoft app-protection framework L1–L3 (230) + OIB BYOD (94) + checks. |
| Intune RBAC / multi-admin approval | **Missing** | 0 | 0 | 0 | 0 | 0 | 0 | 0 | none | none | – | Nothing. |
| Clean-up rules / Remote Help / Cloud PKI | Partial | 0 | 0 | 0 | 0 | 1 (1) | 0 | 0 | Graph | none | – | Stale-device check only. |

### Defender XDR

| Area | Status | CA | ES | Intune | MS baseline | Check (auto) | Standard | CIS recs | Assess | Deploy | Tiers | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Defender for Office (presets, Safe Links/Attachments, priority accounts, quarantine, ZAP, AIR) | Partial (assess only) | 0 | 0 | 0 | 0 | 4 (0) | 3 | 9 | Manual | Manual | – | Safe Links, Safe Attachments, ZAP and phishing checks (Manual today). No preset policies, priority accounts or quarantine policies as settings. |
| Defender for Endpoint tenant settings | Partial | 0 | 1 | 2 | 0 | 3 (2) | 1 | 0 | Graph / Manual | Graph | Golden-Tenant 1 | Tamper protection and onboarding checks; tenant advanced features not captured. |
| Defender for Identity | Knowledge only | 0 | 0 | 0 | 0 | 0 | 1 | 0 | Manual | Manual | – | Standards only. |
| Defender for Cloud Apps (connectors, session/OAuth policies, app governance) | Partial | 3 | 0 | 0 | 0 | 1 (0) | 23 | 1 | Graph / Manual | Graph | – | CA-403–405 session controls + discovery check. No MDA policies or app governance. |
| Vulnerability management | Knowledge only | 0 | 0 | 0 | 0 | 0 | 8 | 0 | Manual | Manual | – | Standards only. |
| Attack simulation / disruption / notifications / unified RBAC | Partial (assess only) | 0 | 0 | 0 | 0 | 1 (1) | 0 | 1 | Graph / Manual | none | – | Attack simulation check (Graph). |

### Exchange Online

| Area | Status | CA | ES | Intune | MS baseline | Check (auto) | Standard | CIS recs | Assess | Deploy | Tiers | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| SMTP AUTH / POP / IMAP / modern auth | Partial | 1 | 0 | 0 | 0 | 1 (1) | 4 | 4 | Graph / Manual | Graph | – | Legacy-auth check (Graph, from Conditional Access) and CA-300. SMTP AUTH / POP / IMAP per mailbox not covered. |
| Auto-forwarding | Partial (assess only) | 0 | 0 | 0 | 0 | 1 (0) | 1 | 1 | Manual | Manual | – | Forwarding check (Manual). |
| Mailbox audit | Partial (assess only) | 0 | 0 | 0 | 0 | 1 (0) | 1 | 3 | Manual | Manual | – | Mailbox audit check (Manual). |
| External tagging / MailTips | Partial (assess only) | 0 | 0 | 0 | 0 | 1 (0) | 0 | 2 | Manual | none | – | External sender check (Manual). |
| Shared mailbox sign-in | Knowledge only | 0 | 0 | 0 | 0 | 0 | 0 | 1 | Manual | none | – | CIS only. |
| Anti-spam / malware / attachment filter / connection filter | Partial (assess only) | 0 | 0 | 0 | 0 | 4 (0) | 3 | 8 | Manual | Manual | – | Attachment filter, inbound spam checks (Manual). |
| SPF / DKIM / DMARC / MTA-STS / direct send | Partial (assess only) | 0 | 0 | 0 | 0 | 4 (3) | 3 | 4 | DNS / Manual | Manual | – | SPF, DKIM, DMARC checks (DNS); direct send (Manual). MTA-STS missing. |
| Transport rules / connectors | Knowledge only | 0 | 0 | 0 | 0 | 1 (0) | 3 | 1 | Manual | Manual | – | Standards and CIS only. |
| OWA / add-ins / external calendar sharing | Partial (assess only) | 0 | 0 | 0 | 0 | 1 (0) | 0 | 4 | Manual | none | – | Calendar-sharing check (Manual). OWA policies and add-in restrictions missing. |

### SharePoint / OneDrive

| Area | Status | CA | ES | Intune | MS baseline | Check (auto) | Standard | CIS recs | Assess | Deploy | Tiers | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| External sharing / links / guest re-auth | Partial (assess only) | 0 | 0 | 0 | 0 | 7 (3) | 51 | 19 | Graph / Manual | Manual | – | Domain restriction (Graph) + link checks (Manual). |
| Unmanaged devices / idle sign-out / legacy auth | Partial | 1 | 0 | 0 | 0 | 0 | 0 | 1 | Graph / Manual | Graph | Golden-Tenant 1 | CA-505 app-enforced restrictions. SharePoint unmanaged-device and idle sign-out settings missing. |
| Infected files | Knowledge only | 0 | 0 | 0 | 0 | 0 | 0 | 1 | Manual | none | – | CIS only. |
| OneDrive sync / KFM / sync domain restriction / retention | Partial | 0 | 0 | 45 | 0 | 4 (2) | 7 | 5 | Graph / Manual | Graph | – | OneDrive client/KFM settings (OIB) + sync check (Graph). Tenant sync-domain restriction and OneDrive retention missing. |
| Container labels / site creation / SharePoint Advanced Management | Knowledge only | 0 | 0 | 0 | 0 | 0 | 3 | 0 | Manual | Manual | – | Standards only. |

### Teams

| Area | Status | CA | ES | Intune | MS baseline | Check (auto) | Standard | CIS recs | Assess | Deploy | Tiers | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| External / guest access / trial tenants | Partial (assess only) | 0 | 0 | 0 | 0 | 4 (0) | 1 | 5 | Manual | Manual | – | 3 checks (Manual). |
| Meetings (lobby, anonymous join, recording, presenters) | Partial (assess only) | 0 | 0 | 0 | 0 | 4 (0) | 5 | 9 | Manual | Manual | – | 3 checks (Manual). |
| App permission / third-party storage | Partial (assess only) | 0 | 0 | 0 | 0 | 1 (0) | 0 | 2 | Manual | none | – | Third-party storage check (Manual). App permission policies missing. |
| Email into channel / town halls / Premium / Rooms and phones / reporting | Knowledge only | 0 | 0 | 0 | 0 | 0 | 0 | 2 | Manual | none | – | CIS only. |

### Purview

| Area | Status | CA | ES | Intune | MS baseline | Check (auto) | Standard | CIS recs | Assess | Deploy | Tiers | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| DLP (all locations incl. endpoint and Copilot) | Partial (assess only) | 0 | 0 | 0 | 0 | 1 (0) | 39 | 3 | Manual | Manual | – | 1 check + Standards. No DLP policy definitions. |
| Sensitivity labels / label policies / auto-labelling | Partial (assess only) | 0 | 0 | 0 | 0 | 1 (1) | 7 | 2 | Graph / Manual | Manual | – | 1 Graph check + Standards. |
| Retention / records | Partial (assess only) | 0 | 0 | 0 | 0 | 1 (1) | 31 | 0 | Graph / Manual | Manual | – | 1 Graph check + Standards. |
| Audit / audit retention | Partial (assess only) | 0 | 0 | 0 | 0 | 1 (0) | 2 | 1 | Manual | Manual | – | Audit logging check (Manual). Audit retention policies missing. |
| Insider risk / communication compliance | Partial | 2 | 0 | 0 | 0 | 0 | 10 | 0 | Graph / Manual | Graph | Golden-Tenant 2 | CA-601/602 (insider-risk CA) + Standards. |
| eDiscovery / legal hold | Knowledge only | 0 | 0 | 0 | 0 | 0 | 20 | 0 | Manual | Manual | – | Standards only. |
| DSPM for AI / Customer Lockbox / information barriers / Customer Key | Knowledge only | 0 | 0 | 0 | 0 | 0 | 0 | 1 | Manual | none | – | CIS (Customer Lockbox) only. |

### Copilot & AI

| Area | Status | CA | ES | Intune | MS baseline | Check (auto) | Standard | CIS recs | Assess | Deploy | Tiers | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Copilot access, web search, agents, Copilot Studio sharing, Restricted Search, interaction audit | Knowledge only | 0 | 0 | 0 | 0 | 0 | 43 | 1 | Manual | Manual | – | Standards only (Windows Copilot app settings in OIB are client-side). No tenant Copilot, agent or Restricted Search settings. |

### Power Platform

| Area | Status | CA | ES | Intune | MS baseline | Check (auto) | Standard | CIS recs | Assess | Deploy | Tiers | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| DLP / environments / tenant isolation / Managed Environments / Dataverse audit / trials | Knowledge only | 0 | 0 | 0 | 0 | 0 | 0 | 16 | Manual | none | – | CIS Power Platform benchmark (16) is knowledge only. |

### Power BI / Fabric

| Area | Status | CA | ES | Intune | MS baseline | Check (auto) | Standard | CIS recs | Assess | Deploy | Tiers | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Tenant settings (publish to web, guest access, shareable links, R/Python visuals) | Knowledge only | 0 | 0 | 0 | 0 | 0 | 0 | 4 | Manual | none | – | CIS M365 section only (not in the original list). |

### M365 admin center

| Area | Status | CA | ES | Intune | MS baseline | Check (auto) | Standard | CIS recs | Assess | Deploy | Tiers | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Org settings (self-service purchase, password expiry, idle timeout, Forms phishing, Bookings, release preference) | **Missing** | 0 | 0 | 0 | 0 | 0 | 0 | 2 | Manual | none | – | Nothing. |
| Microsoft 365 Backup / third-party backup | Partial (assess only) | 0 | 0 | 0 | 0 | 1 (0) | 10 | 0 | Manual | Manual | – | Third-party backup check (Manual) + Standards. |

### Global Secure Access

| Area | Status | CA | ES | Intune | MS baseline | Check (auto) | Standard | CIS recs | Assess | Deploy | Tiers | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Forwarding profiles / web filtering / TLS inspection / Private Access / universal tenant restrictions / compliant network | Partial | 3 | 0 | 0 | 0 | 0 | 0 | 0 | Graph | Graph | – | CA-800–802. No traffic profiles, filtering, TLS inspection or Private Access configuration. |

### Windows 365 / AVD

| Area | Status | CA | ES | Intune | MS baseline | Check (auto) | Standard | CIS recs | Assess | Deploy | Tiers | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Cloud PC settings / AVD | **Covered** (OIB) | 0 | 0 | 15 | 0 | 0 | 0 | 0 | Graph | Graph | – | Windows 365 redirection/connectivity settings (OIB). AVD missing. |

### Hybrid / on-prem

| Area | Status | CA | ES | Intune | MS baseline | Check (auto) | Standard | CIS recs | Assess | Deploy | Tiers | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Server / DC hardening (GPO baselines) | **Covered** | 0 | 0 | 0 | 317 | 0 | 0 | 428 | Manual | GPO | – | Microsoft Windows Server 2025 baseline (317, GPO/manual), incl. DC AppLocker. |
| NTLM / legacy protocols | **Covered** | 0 | 0 | 16 | 45 | 0 | 0 | 0 | Graph / Manual | Graph / GPO | – | NTLM/SMB/Lanman settings (Microsoft baseline + OIB). |
| Entra Connect Tier 0 / Seamless SSO key rollover / Exchange hybrid / AD tiering | **Missing** | 0 | 0 | 0 | 0 | 0 | 0 | 0 | none | none | – | Nothing. |

### Azure

| Area | Status | CA | ES | Intune | MS baseline | Check (auto) | Standard | CIS recs | Assess | Deploy | Tiers | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Governance (management groups, Azure Policy + MCSB, RBAC, PIM for Azure resources, locks, budgets, Lighthouse) | Knowledge only | 0 | 0 | 0 | 0 | 0 | 1 | 66 | Manual | Manual | – | CIS Azure Foundations only. 5.0. |
| Defender for Cloud plans and contacts | Knowledge only | 0 | 0 | 0 | 0 | 0 | 1 | 4 | Manual | Manual | – | CIS only. 5.0. |
| Logging (Activity Log, Entra logs, Log Analytics, Sentinel, alerts, retention) | Partial (assess only) | 0 | 0 | 0 | 0 | 1 (0) | 0 | 24 | Manual | none | – | Sentinel ingestion check (Manual). 5.0. |
| Network (NSG exposure, Bastion, private endpoints, flow logs, firewall/DDoS/WAF) | Knowledge only | 0 | 0 | 0 | 0 | 0 | 0 | 25 | Manual | none | – | CIS only. 5.0. |
| Key Vault / Storage / Compute / Databases / App Service / AKS / Backup / AI services | Knowledge only | 0 | 0 | 0 | 0 | 0 | 0 | 220 | Manual | none | – | CIS Azure Compute/Database/Storage only. 5.0. |
| Azure management MFA | Partial (assess only) | 0 | 0 | 0 | 0 | 1 (1) | 1 | 1 | Graph / Manual | Manual | – | MFA-for-Azure-management check (Graph, from Conditional Access). |

## 6. Other areas in the catalogue (not in the brief)

| Area | Intune | MS baseline | Endpoint Security | of which OIB | CIS recs (knowledge base) |
|---|---|---|---|---|---|
| Windows client – Windows 11 hardening (security baseline, OIB device security) | 310 | 365 | 0 | 310 | 419 |
| Windows client – Defender Antivirus / Firewall / ASR | 0 | 0 | 131 | 0 | 55 |
| Microsoft 365 Apps – Office hardening | 163 | 161 | 0 | 163 | 238 |
| Browsers – Microsoft Edge | 145 | 0 | 0 | 145 | 138 |
| Legacy – Internet Explorer (IE mode only) | 122 | 236 | 0 | 122 | 0 |

## 7. Duplicates and legacy settings

| Setting family | Endpoint Security | OIB (Intune) | Microsoft baseline | Proposal |
|---|---|---|---|---|
| Defender Antivirus | 28 | 81 | 43 | One control per setting (Endpoint Security first); other sources become mappings |
| Windows Firewall | 61 | 48 | 35 | Same |
| Attack surface reduction | 21 | 38 | 2 | Same |
| Internet Explorer | – | 122 | 236 | Tag *Legacy – IE mode*; hidden from default views; not scored |

## 8. Data quality

| Field | Gap |
|---|---|
| Frameworks | Empty on 443 Intune, 215 Standards, 19 checks, 1 CA; 420 Microsoft baseline rows only "Microsoft Security Baseline" |
| Severity | Low 1,894 · Medium 1,077 · High 263 · Critical 4 (checks only) |
| Licensing | Free text, removed (finding 8) |
| Provenance | Source name only; no source version, URL or retrieval date per control |
| Default value | Not recorded (only the recommended value) |
| Assess / deploy method | Implied by kind and evaluation type; not stored per control |

## 9. Differences from the Copilot draft

| Copilot | This report |
|---|---|
| Counts by keyword over all kinds | Device-side matches excluded from tenant areas (e.g. "insecure guest logon" is not cross-tenant access); Copilot's Exchange/Teams/SharePoint device counts were noise it flagged by hand |
| "Find the 30 missing controls" | None missing: 3,268 is a stale 1.2 figure (finding 11) |
| No CIS column | CIS recommendations already in the knowledge base are counted per area |
| No assess/deploy/tier columns | Added, from the controls' evaluation types and policy sets |
| 73 areas | 84 areas: all areas in the brief, Conditional Access core policies, Power BI / Fabric |
| Licensing gaps to fill | Licensing removed from the control; comes from the knowledge base |

All other Copilot findings (OIB share, duplicates, IE-mode settings, data gaps, corrected assumptions) were confirmed against the export.

## 10. Re-run against your own database

```sql
SELECT ControlId, Kind, KindName, Category, Name, Platform, Setting, Value,
       Frameworks, PolicySets, Severity, Evaluation, IsAutomatic
FROM secureplus.CatalogControl;
```
