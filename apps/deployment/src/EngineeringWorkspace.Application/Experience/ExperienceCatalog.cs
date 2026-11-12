namespace EngineeringWorkspace.Application.Experience;

/// <summary>
/// The experience catalog: plain-language questions a CTO or IT lead answers, by Microsoft solution, with what each
/// answer means for users (friction), for protection (security) and for compliance frameworks. Scores are GDS
/// engineering judgement (0-100) and framework notes summarise the requirement; GDS reviews both (docs/DECISIONS.md).
/// </summary>
public static class ExperienceCatalog
{
    public const string AllUsersKey = "all-users";

    public static IReadOnlyList<ExperienceFramework> Frameworks { get; } =
    [
        new("NIST53M", "NIST SP 800-53 Moderate", "Federal moderate baseline (FedRAMP Moderate, state and local government)."),
        new("CMMC2", "CMMC 2.0 Level 2 / NIST SP 800-171", "Defense contractors handling Controlled Unclassified Information (CUI)."),
        new("HIPAA", "HIPAA Security Rule", "Healthcare: protecting electronic protected health information (ePHI)."),
        new("PCI4", "PCI DSS 4.0.1", "Organisations that store, process or transmit cardholder data."),
        new("CISIG1", "CIS Controls v8.1 IG1", "Essential cyber hygiene for every organisation."),
        new("CISIG2", "CIS Controls v8.1 IG2", "Organisations with an IT team and sensitive data."),
        new("CISM365L1", "CIS Microsoft 365 Benchmark Level 1", "Baseline Microsoft 365 hardening; the base of every GDS tier."),
        new("CISM365L2", "CIS Microsoft 365 Benchmark Level 2", "Defence-in-depth Microsoft 365 settings for higher-risk organisations."),
        new("NISTCSF2", "NIST Cybersecurity Framework 2.0", "Govern, Identify, Protect, Detect, Respond, Recover: outcome-based risk management."),
        new("GLBA", "GLBA / FTC Safeguards Rule (16 CFR Part 314)", "Financial institutions protecting customer information (the FTC Safeguards Rule enforces GLBA)."),
        new("ISO27001", "ISO/IEC 27001:2022", "International information security management standard."),
        new("E8ML2", "Essential Eight Maturity Level 2", "Australian Cyber Security Centre mitigation strategies. Retired in 4.0: no new use.", IsRetired: true),
        new("SOX", "SOX IT General Controls", "Public companies: controls over financial reporting systems."),
        new("ITAR", "ITAR / EAR export control", "Export-controlled technical data must stay with authorised U.S. persons."),
    ];

    /// <summary>Frameworks offered for new choices (retired ones are hidden).</summary>
    public static IReadOnlyList<ExperienceFramework> ActiveFrameworks => Frameworks.Where(framework => !framework.IsRetired).ToArray();

    private static FrameworkNote N(string framework, char effect, string reason) =>
        new(framework, effect switch { 'S' => FrameworkEffect.Supports, 'W' => FrameworkEffect.Weakens, 'C' => FrameworkEffect.Conflicts, _ => FrameworkEffect.Neutral }, reason);

    /// <summary>ID-MFA-METHOD option for frontline shared devices (4.0): single-factor, never a default, always paired with CA-213/CA-214.</summary>
    public const string SharedDeviceQrCode = "SHAREDQR";

    private static ExperienceOption O(string code, string label, string experience, int security, int friction, string? configuration, params FrameworkNote[] notes) =>
        new(code, label, experience, security, friction, notes, configuration);

    private static ExperienceQuestion Q(string code, string solution, string topic, string question, string why, string defaultOption, params ExperienceOption[] options) =>
        new(code, solution, topic, question, why, options, defaultOption);

    public static IReadOnlyList<string> Solutions { get; } =
    [
        "Sign-in and Identity (Entra ID)", "Devices (Intune)", "Endpoint Protection (Defender for Endpoint)", "Email (Exchange Online and Defender for Office 365)",
        "Files and Sharing (SharePoint and OneDrive)", "Meetings and Chat (Teams)", "Data Protection (Purview)", "Windows and Office Apps", "Admin Access", "AI and Copilot",
        "Network Access (Global Secure Access)"
    ];

    public static IReadOnlyList<ExperienceQuestion> Questions { get; } = WithNotes(
    [
        // ---- Sign-in and Identity (Entra ID) ----
        Q("ID-MFA-METHOD", Solutions[0], "MFA method", "How should people prove it's really them when they sign in?",
            "Stolen passwords cause most breaches. The second step stops them, but some methods can be phished or intercepted.", "APP",
            O("SMS", "Text message or phone call codes", "A code arrives by text or call; the user types it in. Works on any phone, but codes can be intercepted (SIM swap) or phished.",
                45, 40, "Authentication methods policy: SMS and voice enabled",
                N("E8ML2", 'C', "ML2 requires phishing-resistant MFA for privileged users and online services handling sensitive data."),
                N("NIST53M", 'W', "SP 800-63B treats SMS/voice as a restricted authenticator."), N("CMMC2", 'W', "3.5.3 is met, but DoD guidance expects stronger, replay-resistant MFA."),
                N("PCI4", 'S', "8.4 requires MFA; SMS is accepted."), N("CISIG1", 'S', "6.3-6.5 require MFA.")),
            O("APP", "Microsoft Authenticator push with number matching", "A notification asks the user to type the number shown on screen into the Authenticator app. Quick and resists push-fatigue attacks.",
                70, 25, "Authentication methods policy: Authenticator (number matching, app context); SMS/voice disabled",
                N("NIST53M", 'S', "IA-2(1)(2) MFA with an approved authenticator."), N("CMMC2", 'S', "3.5.3 multifactor authentication."), N("HIPAA", 'S', "164.312(d) person authentication."),
                N("PCI4", 'S', "8.4 MFA for all access to the cardholder data environment."), N("CISIG1", 'S', "6.3-6.5 MFA."), N("E8ML2", 'W', "Not phishing-resistant; acceptable for standard users only.")),
            O("PASSWORDLESS", "Passwordless first (Authenticator, Windows Hello)", "Users sign in with a fingerprint, face or PIN on their device, or approve in Authenticator; no password to type or forget.",
                85, 10, "Passwordless phone sign-in and Windows Hello for Business; Temporary Access Pass for onboarding",
                N("NIST53M", 'S', "Multifactor, replay-resistant."), N("CMMC2", 'S', "3.5.3 and 3.5.4 replay-resistant."), N("E8ML2", 'S', "Windows Hello for Business is phishing-resistant."),
                N("CISIG2", 'S', "Strong authentication."), N("PCI4", 'S', "8.4 MFA.")),
            O("PHISHRESISTANT", "Phishing-resistant only (security keys, Windows Hello, smart cards)", "Users sign in with a security key, Windows Hello or a certificate. The strongest option; needs a key or a managed device.",
                95, 30, "Conditional Access authentication strength: phishing-resistant MFA (FIDO2, WHfB, certificate-based authentication)",
                N("NIST53M", 'S', "Meets AAL3-style phishing resistance (OMB M-22-09)."), N("CMMC2", 'S', "Exceeds 3.5.3."), N("E8ML2", 'S', "Phishing-resistant MFA."),
                N("ITAR", 'S', "Strong identity assurance for export-controlled data.")),
            O(SharedDeviceQrCode, "Shared device: QR code + PIN (single-factor, not MFA)",
                "Frontline staff on shared iPhones, iPads or Android devices scan the QR code on their badge and type a PIN; no username or password. " +
                "This is NOT multifactor authentication: Microsoft classes it as single-factor. It is allowed only for a frontline group and only on compliant shared devices.",
                55, 10, "Authentication methods policy: QR code enabled for the frontline QR code group only. Conditional Access (required, deployed together): " +
                "CA-213 block the group on devices that are not shared (device filter profileType -ne Shared) and CA-214 require a compliant device for the group. " +
                "Devices in shared device mode, iOS/iPadOS 15+ or Android; F1/F3, Entra ID P1/P2 or Microsoft 365 E3/E5 licences. Not counted as MFA.",
                N("NIST53M", 'C', "Single-factor: does not meet IA-2(1)/(2) multifactor authentication."), N("CMMC2", 'C', "3.5.3 requires multifactor authentication."),
                N("PCI4", 'C', "8.4 requires MFA into the cardholder data environment."), N("HIPAA", 'W', "164.312(d) is met only with the compliant shared-device controls; document the risk decision."),
                N("CISIG1", 'W', "6.3-6.5 expect MFA; acceptable only for frontline shared devices with compensating controls."),
                N("GLBA", 'C', "314.4(c)(5) requires multi-factor authentication for anyone accessing customer information."),
                N("CISM365L1", 'C', "MFA for all users."))),

        Q("ID-SIGNIN-FREQUENCY", Solutions[0], "How often to sign in again", "How often should people be asked to sign in again?",
            "Shorter sessions limit what a stolen device or token can do, but every prompt interrupts work.", "RISK",
            O("EVERY", "Every time they open an app", "Users sign in for each new browser session or app start.", 85, 85, "Conditional Access: sign-in frequency every time, no persistent browser",
                N("PCI4", 'S', "8.2.8 re-authentication after idle."), N("NIST53M", 'S', "AC-12 session termination.")),
            O("WORKDAY", "Once a work day (every 12 hours)", "Users sign in once in the morning; the session lasts the working day.", 75, 45, "Conditional Access: sign-in frequency 12 hours",
                N("NIST53M", 'S', "SP 800-63B AAL2 re-authentication within 12 hours."), N("CMMC2", 'S', "3.1.11 terminate sessions after a defined condition."),
                N("PCI4", 'S', "Combined with screen lock meets 8.2.8."), N("HIPAA", 'S', "164.312(a)(2)(iii) automatic logoff.")),
            O("WEEK", "Once a week", "Users rarely see sign-in prompts on their own devices; they sign in again weekly.", 55, 20, "Conditional Access: sign-in frequency 7 days",
                N("NIST53M", 'W', "Longer than the AAL2 12-hour re-authentication."), N("CMMC2", 'W', "Define and justify the session condition (3.1.11)."), N("PCI4", 'C', "Sessions into the CDE must re-authenticate after 15 minutes idle (8.2.8).")),
            O("RISK", "Only when something looks risky", "Sessions stay signed in on trusted devices (up to 90 days); Microsoft asks again when sign-in risk, location or device changes.",
                65, 10, "Entra ID Protection: risk-based sign-in policy (P2), continuous access evaluation, default 90-day rolling sessions",
                N("NIST53M", 'W', "Needs a documented session-termination rule (AC-12)."), N("CMMC2", 'W', "3.1.11 needs defined conditions; risk-based is accepted when documented."),
                N("PCI4", 'C', "8.2.8 idle re-authentication for CDE access."), N("SOX", 'S', "Risk-based reauthentication with logging."))),

        Q("ID-LOCATIONS", Solutions[0], "Where people can sign in from", "Where in the world should people be able to sign in from?",
            "Most attacks on Microsoft 365 come from countries where the business has no staff.", "BLOCKRISKY",
            O("ANYWHERE", "Anywhere", "No location limits.", 30, 0, null,
                N("ITAR", 'C', "Export-controlled data must not be reachable from embargoed countries or by unauthorised foreign persons."), N("CMMC2", 'W', "3.1.12/3.1.14 control remote access."),
                N("CISIG2", 'W', "13.x network monitoring expects geo-based controls.")),
            O("BLOCKRISKY", "Block high-risk and sanctioned countries", "Sign-ins from countries on the block list are refused; everywhere else works normally.", 60, 5,
                "Conditional Access: block named location of sanctioned / high-risk countries",
                N("ITAR", 'W', "Better, but ITAR needs access limited to authorised persons, not just countries."), N("CMMC2", 'S', "Supports 3.1.12 remote access control.")),
            O("ALLOWLIST", "Only the countries where we operate", "Sign-ins work only from approved countries. Travellers ask IT to add a country before they go.", 80, 25,
                "Conditional Access: allow only named locations; travel exceptions by group",
                N("ITAR", 'S', "Limits access to approved locations."), N("CMMC2", 'S', "3.1.12 and 3.1.14."), N("NIST53M", 'S', "AC-17 remote access.")),
            O("OFFICEONLY", "Only from our offices or company devices", "Sign-in works only on office networks or company-managed devices.", 90, 55,
                "Conditional Access: trusted IP named locations or compliant device",
                N("ITAR", 'S', "Strongest control over access location."), N("CMMC2", 'S', "3.1.12-3.1.15.")),
            O("APPROVEDORCOMPLIANT", "Approved countries, or anywhere on a company device", "Personal devices work only from approved countries; company-managed devices work from any site, so field staff are not locked out.",
                78, 15, "Conditional Access: allow named countries OR require compliant device; block sanctioned countries",
                N("CMMC2", 'S', "3.1.12/3.1.18 remote and mobile access controlled."), N("NIST53M", 'S', "AC-17, AC-19."), N("ITAR", 'W', "Add a per-person control for export-controlled data.")),
            O("COMPLIANTANYWHERE", "Anywhere, but only on a company device with MFA", "No country lock: rigs, vessels and remote sites work from any connection, but only company-managed devices with MFA get in.",
                75, 10, "Conditional Access: require compliant (or hybrid-joined) device and MFA; no location condition; sanctioned countries still blocked",
                N("CMMC2", 'S', "3.1.18 mobile device connection controlled."), N("NIST53M", 'S', "AC-19 access control for mobile devices."), N("ITAR", 'W', "Location is not limited; control access to technical data per person."))),

        Q("ID-SSPR", Solutions[0], "Forgotten passwords", "When someone forgets their password, how do they get back in?",
            "Help-desk resets are slow and a favourite target of social engineering.", "SELF",
            O("HELPDESK", "Call the help desk", "Users call IT; IT verifies them and resets the password.", 55, 60, "Self-service password reset disabled",
                N("NIST53M", 'S', "IA-5 identity verification before reset.")),
            O("SELF", "Reset it themselves with MFA", "Users reset their own password at aka.ms/sspr after proving it's them with their MFA method.", 70, 15,
                "Self-service password reset with two methods; combined registration",
                N("NIST53M", 'S', "IA-5(1) authenticator management."), N("CISIG1", 'S', "5.2 unique passwords; secure reset.")),
            O("NOPASSWORD", "There's nothing to forget (passwordless)", "Users don't use passwords day to day; a Temporary Access Pass recovers a lost device.", 85, 10,
                "Passwordless + Temporary Access Pass for recovery",
                N("E8ML2", 'S', "Phishing-resistant authenticators."))),

        Q("ID-GUESTS", Solutions[0], "Guests and partners", "How should people outside the company (partners, vendors) get access?", "Guests are the most common forgotten accounts.", "INVITED",
            O("OPEN", "Anyone can invite guests", "Any user can invite any outside person; guests keep access until removed.", 30, 0, "External collaboration: anyone can invite; no reviews",
                N("CMMC2", 'C', "3.1.1/3.1.20 control external system connections and authorised users."), N("ITAR", 'C', "Guests may be foreign persons."), N("SOX", 'W', "Access reviews needed.")),
            O("INVITED", "Invited by staff, reviewed every quarter", "Staff invite guests; owners confirm each quarter who still needs access.", 65, 15,
                "Guest invite settings: members; Access reviews quarterly; guests require MFA",
                N("SOX", 'S', "Periodic access review."), N("ISO27001", 'S', "A.5.18 access rights review."), N("CMMC2", 'W', "Also restrict guests from CUI sites.")),
            O("APPROVED", "Only approved partner organisations, with approval", "Only people from approved partner companies can be invited, through an approval workflow, with expiry.", 85, 35,
                "Cross-tenant access allow-list; Entitlement Management access packages with expiry",
                N("CMMC2", 'S', "3.1.20 external connections controlled."), N("ITAR", 'S', "Limits to approved organisations."), N("SOX", 'S', "Approval and expiry.")),
            O("NONE", "No guests", "Outside people can't be added; share by email attachments or a separate portal.", 90, 60, "External collaboration disabled",
                N("ITAR", 'S', "No external accounts."))),

        // ---- Devices (Intune) ----
        Q("DEV-PERSONAL", Solutions[1], "Personal devices", "Can people use their own phones and computers for work?", "Unmanaged devices can't be checked for malware, encryption or updates.", "WEBONLY",
            O("FULL", "Yes, full access from any device", "Email, files and apps work on any device, managed or not.", 25, 0, null,
                N("CMMC2", 'C', "3.1.18/3.1.19 control connection of mobile devices; CUI must not be on unmanaged devices."), N("HIPAA", 'W', "ePHI on unmanaged devices needs safeguards."),
                N("PCI4", 'W', "Unmanaged devices must be outside the CDE."), N("ITAR", 'C', "Export-controlled data on unmanaged devices.")),
            O("APPPROTECT", "Phones: yes, inside protected apps", "Personal phones work through Outlook, Teams and OneDrive with app protection (PIN, no copy to personal apps). Computers must be managed.", 70, 20,
                "Intune app protection policies (MAM) for iOS/Android; Conditional Access: approved client app",
                N("CMMC2", 'S', "Accepted with app protection and CA."), N("HIPAA", 'S', "Data containment."), N("CISIG2", 'S', "Data protection on mobile.")),
            O("WEBONLY", "Browser only, no downloads", "Personal devices can use web versions of Outlook and Office, but can't download or sync files.", 75, 30,
                "Conditional Access: app enforced restrictions for unmanaged devices; Defender for Cloud Apps session controls",
                N("CMMC2", 'S', "CUI not stored on unmanaged devices."), N("HIPAA", 'S', "Prevents local copies of ePHI.")),
            O("MANAGEDONLY", "No, company-managed devices only", "Work apps only open on devices enrolled in Intune and compliant (encrypted, updated, protected).", 90, 50,
                "Conditional Access: require compliant or hybrid-joined device",
                N("CMMC2", 'S', "3.1.18-3.1.19."), N("ITAR", 'S', "Data only on controlled devices."), N("PCI4", 'S', "Only secured devices."))),

        Q("DEV-UPDATES", Solutions[1], "Windows updates", "How quickly should security updates install on computers?", "Unpatched computers are the second most common way in.", "FAST",
            O("USER", "When the user chooses", "Windows reminds users; they install when they want.", 30, 5, "Windows Update defaults (no deadlines)",
                N("E8ML2", 'C', "Patch operating systems within two weeks (48 hours for critical exploited)."), N("CISIG1", 'C', "7.3 automated OS patching."), N("PCI4", 'C', "6.3.3 critical patches within one month.")),
            O("FAST", "Within a week, restart outside work hours", "Updates install automatically within 7 days; restarts are scheduled out of hours, with a 2-day grace.", 80, 25,
                "Windows Update rings / Autopatch: 7-day quality deferral max, deadline 7 + 2 days grace",
                N("E8ML2", 'S', "Within two weeks."), N("CISIG1", 'S', "7.3."), N("PCI4", 'S', "6.3.3."), N("NIST53M", 'S', "SI-2 flaw remediation."), N("CMMC2", 'S', "3.14.1.")),
            O("RINGS", "Pilot group first, everyone within three weeks", "IT and a pilot group get updates first; everyone else within 21 days.", 65, 15,
                "Windows Autopatch rings: Test, First, Fast, Broad (21 days)",
                N("E8ML2", 'W', "Must still meet two weeks for internet-facing and critical."), N("PCI4", 'S', "Within one month.")),
            O("IMMEDIATE", "As soon as released, restart forced", "Updates install the day they release and the computer restarts at the deadline.", 90, 60,
                "Zero deferral, 1-day deadline, no grace",
                N("E8ML2", 'S', "Meets 48-hour patching."))),

        Q("DEV-ENCRYPTION", Solutions[1], "Disk encryption", "Should laptop and desktop drives be encrypted?", "A lost laptop without encryption is a reportable data breach in most regulations.", "SILENT",
            O("NONE", "No", "Drives aren't encrypted.", 10, 0, null,
                N("HIPAA", 'C', "164.312(a)(2)(iv) encryption (addressable, but a lost unencrypted device is a breach)."), N("CMMC2", 'C', "3.13.16 protect CUI at rest; 3.8.6 portable storage."),
                N("PCI4", 'C', "3.5 render stored account data unreadable."), N("NIST53M", 'C', "SC-28 protection of information at rest.")),
            O("SILENT", "Yes, silently (BitLocker with TPM)", "Drives encrypt in the background; users notice nothing.", 85, 0, "BitLocker silent encryption, XTS-AES 256, recovery keys escrowed to Entra ID",
                N("HIPAA", 'S', "Encryption at rest."), N("CMMC2", 'S', "3.13.16 (FIPS mode required for CUI)."), N("PCI4", 'S', "3.5."), N("NIST53M", 'S', "SC-28.")),
            O("PIN", "Yes, with a start-up PIN", "Users enter a PIN when the computer starts, before Windows loads.", 95, 35, "BitLocker TPM + startup PIN",
                N("CMMC2", 'S', "Pre-boot authentication."), N("ITAR", 'S', "Strong protection for laptops that travel."))),

        Q("DEV-LOCALADMIN", Solutions[1], "Installing software", "Can people install software and change settings on their computers?", "Admin rights let malware do anything the user can.", "EPM",
            O("ADMIN", "Yes, everyone is a local administrator", "Users install anything and change any setting.", 10, 0, null,
                N("E8ML2", 'C', "Restrict administrative privileges."), N("CISIG1", 'C', "5.4 restrict administrator privileges."), N("CMMC2", 'C', "3.1.5-3.1.7 least privilege.")),
            O("EPM", "Approved apps self-service; elevation on request", "Users install approved apps from Company Portal; IT approves other elevations quickly.", 85, 25,
                "Company Portal apps; Intune Endpoint Privilege Management; Windows LAPS for the built-in administrator",
                N("E8ML2", 'S', "Admin privileges restricted."), N("CISIG1", 'S', "5.4."), N("CMMC2", 'S', "3.1.5-3.1.7.")),
            O("LOCKED", "No, IT installs everything", "Only IT can install software or change system settings.", 92, 55, "Standard users only; Windows LAPS; Company Portal for required apps",
                N("E8ML2", 'S', "Strongest."), N("CMMC2", 'S', "3.4.9 control user-installed software."))),

        Q("DEV-USB", Solutions[1], "USB drives", "Can people use USB drives and other removable storage?", "USB drives carry data out and malware in.", "READONLY",
            O("ALLOW", "Yes", "Any USB drive works.", 25, 0, null,
                N("CMMC2", 'C', "3.8.7 control removable media; 3.8.8 prohibit portable storage without an owner."), N("HIPAA", 'W', "ePHI could leave on USB."),
                N("NIST53M", 'C', "MP-7 media use.")),
            O("READONLY", "Read only", "Users can open files from USB drives but can't copy work files to them.", 70, 20, "Defender device control / Intune: removable storage write blocked",
                N("CMMC2", 'S', "3.8.7."), N("HIPAA", 'S', "Prevents ePHI leaving.")),
            O("APPROVED", "Only company-issued encrypted drives", "Only USB drives issued by IT (encrypted) work.", 90, 35, "Defender device control allow-list by device ID; BitLocker To Go required",
                N("CMMC2", 'S', "3.8.6-3.8.8."), N("ITAR", 'S', "Controlled media.")),
            O("BLOCK", "No", "USB storage is blocked.", 95, 50, "Removable storage access denied",
                N("CMMC2", 'S', "3.8.7."))),

        Q("DEV-LOCK", Solutions[1], "Screen lock", "When someone walks away, how soon should the screen lock?", "An unlocked screen gives anyone nearby the user's access.", "15MIN",
            O("NEVER", "Never automatically", "The screen stays on until the user locks it.", 10, 0, null,
                N("PCI4", 'C', "8.2.8 lock after 15 minutes idle."), N("CMMC2", 'C', "3.1.10 session lock."), N("NIST53M", 'C', "AC-11 device lock."), N("HIPAA", 'C', "Automatic logoff.")),
            O("15MIN", "After 15 minutes", "Screens lock after 15 minutes without use; Windows Hello unlocks.", 75, 15, "Interactive logon machine inactivity limit 900 seconds",
                N("PCI4", 'S', "8.2.8."), N("CMMC2", 'S', "3.1.10."), N("NIST53M", 'S', "AC-11."), N("HIPAA", 'S', "Automatic logoff.")),
            O("5MIN", "After 5 minutes", "Screens lock quickly; best for shared or public spaces.", 88, 35, "Inactivity limit 300 seconds",
                N("PCI4", 'S', "Exceeds 8.2.8."))),

        // ---- Endpoint Protection ----
        Q("EDR-ACTION", Solutions[2], "Response to attacks", "When an attack is detected on a computer, what should happen?", "Minutes matter: automatic response stops ransomware spreading.", "AUTO",
            O("ALERT", "Alert IT; IT decides", "IT is notified and investigates; the computer keeps working meanwhile.", 50, 0, "Automated investigation: semi (require approval for all folders)",
                N("CISIG2", 'W', "17.x incident response should be timely."), N("NIST53M", 'W', "IR-4 incident handling.")),
            O("AUTO", "Fix it automatically", "Defender removes the threat and undoes its changes automatically; the user may see a notification.", 85, 10,
                "Automated investigation and response: full; tamper protection on",
                N("NIST53M", 'S', "IR-4, SI-3."), N("CMMC2", 'S', "3.14.2 malicious code protection."), N("E8ML2", 'S', "Supports detection and response.")),
            O("ISOLATE", "Fix it and isolate the computer", "An infected computer is cut off from the network (except Defender) until IT releases it.", 92, 40,
                "Full automation plus automatic device isolation for high-severity alerts (Defender XDR attack disruption)",
                N("NIST53M", 'S', "IR-4 containment."), N("CMMC2", 'S', "3.6.1 incident handling."))),

        Q("EDR-ASR", Solutions[2], "Blocking risky behaviour", "Should risky actions (Office macros launching programs, scripts from email) be blocked?", "These are how most ransomware starts.", "BLOCK",
            O("AUDIT", "Only report them", "Nothing is blocked; IT gets reports to review.", 40, 0, "Attack surface reduction rules: audit",
                N("E8ML2", 'W', "Office macro and application hardening expect blocking.")),
            O("BLOCK", "Block the standard risky behaviours", "Common attack techniques are blocked; a few legacy add-ins may need exceptions.", 85, 15, "ASR rules: block (standard set)",
                N("E8ML2", 'S', "User application hardening."), N("CISIG2", 'S', "10.5 anti-exploitation features."), N("NIST53M", 'S', "SI-3, SI-16.")),
            O("STRICT", "Block everything, including unusual programs", "All ASR rules block, including programs that don't meet prevalence or trust criteria.", 92, 40, "All ASR rules block incl. prevalence/age rule",
                N("E8ML2", 'S', "Exceeds."))),

        Q("EDR-APPCONTROL", Solutions[2], "Which programs can run", "Should only approved programs be allowed to run?", "Application control is one of the most effective protections, and the most work to run.", "SMART",
            O("ANY", "Any program", "Any program can run; antivirus checks it.", 35, 0, null,
                N("E8ML2", 'C', "Application control is required at ML1 and above.")),
            O("SMART", "Block known-bad and untrusted downloads", "SmartScreen and Smart App Control block known-bad and unknown downloaded programs; most users never notice.", 65, 10,
                "SmartScreen block; reputation-based protection; ASR prevalence rule",
                N("E8ML2", 'W', "Reputation is not application control."), N("CISIG2", 'S', "2.5 allowlist authorised software (partially).")),
            O("ALLOWLIST", "Only approved programs (allow-list)", "Only programs IT approves (or signed by trusted publishers) run. New tools need a request.", 92, 45,
                "App Control for Business (WDAC) with managed installer (Intune)",
                N("E8ML2", 'S', "Application control."), N("CISIG2", 'S', "2.5-2.7."), N("CMMC2", 'S', "3.4.8 deny-by-exception."))),

        // ---- Email ----
        Q("MAIL-FORWARD", Solutions[3], "Forwarding email outside", "Can people automatically forward their work email to outside addresses?", "Attackers who take over a mailbox set up silent forwarding.", "BLOCK",
            O("ALLOW", "Yes", "Rules can forward mail to any address.", 20, 0, null,
                N("HIPAA", 'C', "ePHI could leave without safeguards."), N("CMMC2", 'C', "3.1.3 control the flow of CUI."), N("CISIG2", 'W', "Exfiltration path.")),
            O("BLOCK", "No (exceptions approved by IT)", "Automatic forwarding outside the company is blocked; IT can approve a specific mailbox.", 85, 10, "Outbound spam policy: automatic forwarding off",
                N("HIPAA", 'S', "Transmission security."), N("CMMC2", 'S', "3.1.3."), N("CISIG2", 'S', "Data loss prevention."))),

        Q("MAIL-LINKS", Solutions[3], "Links and attachments", "How careful should email be with links and attachments?", "Phishing email is still the top way in.", "SAFE",
            O("BASIC", "Standard spam and malware filtering", "Known-bad email is filtered; links and attachments arrive immediately.", 45, 0, "Exchange Online Protection defaults",
                N("CISIG2", 'W', "9.6 block unnecessary file types; 9.7 anti-malware for email.")),
            O("SAFE", "Check links when clicked and test attachments (quick)", "Links are checked when clicked; attachments open in a sandbox first while the message text is delivered at once.", 82, 10,
                "Defender for Office 365: Safe Links, Safe Attachments dynamic delivery, impersonation protection",
                N("CISIG2", 'S', "9.x email protections."), N("NIST53M", 'S', "SI-3, SI-8 spam protection."), N("E8ML2", 'S', "Supports user application hardening.")),
            O("STRICT", "Hold suspicious email for review", "Messages with suspicious links or attachments are held for IT review before delivery.", 92, 35, "Strict preset security policy; quarantine admin-release",
                N("CISIG2", 'S', "Exceeds."))),

        Q("MAIL-RETENTION", Solutions[3], "How long to keep email", "How long must email be kept?", "Regulations and lawsuits require keeping records; keeping everything forever adds risk.", "7Y",
            O("USER", "Users decide", "Users can delete anything at any time.", 30, 0, null,
                N("SOX", 'C', "Retain records relevant to audits (7 years)."), N("HIPAA", 'W', "Documentation retained 6 years."), N("CMMC2", 'W', "3.3.1 retain audit records.")),
            O("1Y", "One year", "Email is kept a year after it's sent or received, even if deleted.", 55, 5, "Retention policy: retain 1 year",
                N("SOX", 'C', "Shorter than 7 years."), N("HIPAA", 'W', "6 years for documentation.")),
            O("7Y", "Seven years", "Email is kept seven years (recoverable for legal and audit), then deleted.", 75, 5, "Retention policy: retain and delete after 7 years; litigation hold available",
                N("SOX", 'S', "Seven-year retention."), N("HIPAA", 'S', "Exceeds 6 years."), N("PCI4", 'S', "Retention defined (3.2.1 minimises card data).")),
            O("FOREVER", "Forever", "Nothing is ever deleted.", 60, 5, "Retention policy: retain forever",
                N("ISO27001", 'W', "A.5.33 records should have defined retention."), N("PCI4", 'W', "3.2.1 keep data only as long as needed."))),

        // ---- Files and Sharing ----
        Q("FILES-SHARING", Solutions[4], "Sharing files outside", "How freely can people share files with people outside the company?", "'Anyone' links are the most common source of accidental data exposure.", "GUESTS",
            O("ANYONE", "Anyone with the link", "Users create links anyone can open, without signing in.", 20, 0, "SharePoint sharing: Anyone links",
                N("CMMC2", 'C', "3.1.22 control publicly accessible information; CUI must not be public."), N("HIPAA", 'C', "ePHI could be exposed."), N("ITAR", 'C', "Uncontrolled access."),
                N("PCI4", 'W', "Card data must not be shared openly.")),
            O("GUESTS", "Named outside people (they sign in)", "Users share with specific outside people, who verify their email.", 65, 10, "SharePoint sharing: new and existing guests; link expiry 30 days",
                N("CMMC2", 'W', "Restrict CUI sites further with sensitivity labels."), N("HIPAA", 'S', "Access limited to named people.")),
            O("EXISTING", "Only guests IT already approved", "Users share only with outside people who already have guest accounts.", 82, 30, "SharePoint sharing: existing guests only",
                N("CMMC2", 'S', "Controlled external access."), N("ITAR", 'W', "Guests still need export-control vetting.")),
            O("INTERNAL", "Nobody outside", "Files can be shared only inside the company.", 92, 55, "SharePoint sharing: only people in your organisation",
                N("ITAR", 'S', "No external sharing."))),

        Q("FILES-SYNC", Solutions[4], "Files on computers", "Can people sync work files to their computers?", "Synced files go wherever the laptop goes.", "MANAGED",
            O("ANY", "Yes, on any computer", "OneDrive syncs on any computer the user signs in on.", 30, 0, null,
                N("CMMC2", 'C', "CUI on unmanaged computers."), N("HIPAA", 'W', "ePHI on unmanaged computers.")),
            O("MANAGED", "Only on company computers (with backup of Desktop and Documents)", "OneDrive syncs on company computers only and backs up Desktop and Documents automatically.", 82, 5,
                "OneDrive: sync only on domain-joined/compliant devices; Known Folder Move",
                N("CMMC2", 'S', "3.1.18 controlled devices."), N("HIPAA", 'S', "Managed devices only."))),

        // ---- Teams ----
        Q("TEAMS-EXTERNAL", Solutions[5], "Chatting with other companies", "Can people chat and call with people at other companies in Teams?", "Attackers now phish through Teams chat.", "ALLOWED",
            O("OPEN", "Yes, with any company", "Users chat with anyone on Teams or Skype.", 35, 0, "Teams external access: all domains",
                N("CMMC2", 'W', "3.1.20 external connections should be limited."), N("CISIG2", 'W', "Unvetted external communication.")),
            O("ALLOWED", "Yes, with approved companies", "Users chat with people at partner companies IT has approved.", 75, 15, "Teams external access: allowed domains list",
                N("CMMC2", 'S', "3.1.20."), N("ITAR", 'W', "Partners must be vetted for export control.")),
            O("OFF", "No", "Teams chat with other companies is off; use email.", 88, 40, "Teams external access: off",
                N("ITAR", 'S', "No external chat."))),

        Q("TEAMS-LOBBY", Solutions[5], "Joining meetings", "Who should wait in the lobby before joining meetings?", "Uninvited people joining meetings can hear confidential discussions.", "EXTERNAL",
            O("NOBODY", "Nobody waits", "Anyone with the link joins directly.", 30, 0, "Meeting lobby: everyone bypasses",
                N("HIPAA", 'W', "Telehealth/ePHI meetings need controlled admission.")),
            O("EXTERNAL", "People from outside wait", "Colleagues join directly; guests and anonymous people wait for the organiser.", 75, 10, "Meeting lobby: people in my org and guests bypass",
                N("HIPAA", 'S', "Controlled admission."), N("CMMC2", 'S', "Controls access to meetings with CUI.")),
            O("EVERYONE", "Everyone waits", "The organiser admits every participant.", 88, 40, "Meeting lobby: only organisers bypass",
                N("ITAR", 'S', "Organiser vets every participant."))),

        Q("TEAMS-RECORDING", Solutions[5], "Recording meetings", "Can meetings be recorded and transcribed?", "Recordings capture everything said, including sensitive data.", "ALLOWED",
            O("ALLOWED", "Yes; recordings expire after 120 days", "Organisers record and transcribe; recordings are deleted automatically after 120 days.", 60, 0,
                "Teams meeting policy: recording on; recording expiration 120 days",
                N("HIPAA", 'W', "Recordings with ePHI need protection and retention rules.")),
            O("ANNOUNCED", "Yes, with consent and sensitivity labels", "Participants are asked to agree before recording; confidential meetings can't be recorded.", 78, 15,
                "Explicit recording consent; sensitivity labels with meeting options (Premium)",
                N("HIPAA", 'S', "Consent and protection."), N("ISO27001", 'S', "A.5.34 privacy.")),
            O("OFF", "No", "Recording and transcription are off.", 85, 30, "Teams meeting policy: recording off",
                N("ITAR", 'S', "No recordings of controlled discussions."))),

        // ---- Data Protection (Purview) ----
        Q("DATA-LABELS", Solutions[6], "Labelling documents", "Should documents and email be labelled by sensitivity (Public, Internal, Confidential)?", "Labels are how protection follows a file wherever it goes.", "DEFAULT",
            O("NONE", "No labels", "Nothing to choose; files aren't classified.", 30, 0, null,
                N("CMMC2", 'W', "3.8.4 mark media with CUI markings."), N("ISO27001", 'W', "A.5.12-5.13 classification and labelling."), N("ITAR", 'C', "Technical data must be marked.")),
            O("DEFAULT", "Labelled automatically (default Internal), users can change", "Every new document is Internal by default; Office suggests Confidential when it spots sensitive data.", 75, 10,
                "Sensitivity labels with default label and client-side auto-labelling recommendations",
                N("CMMC2", 'S', "3.8.4 CUI marking (with a CUI label)."), N("ISO27001", 'S', "A.5.12-5.13."), N("HIPAA", 'S', "Identifies ePHI.")),
            O("MANDATORY", "Users must choose a label for every file", "Users pick a label before saving or sending; Confidential content is encrypted.", 88, 35,
                "Mandatory labelling; encryption on Confidential and Highly Confidential",
                N("ITAR", 'S', "Marking and encryption of technical data."), N("CMMC2", 'S', "3.8.4, 3.13.11."))),

        Q("DATA-DLP", Solutions[6], "Stopping sensitive data leaving", "What should happen when someone tries to send sensitive data (card numbers, health or personal data) outside?", "Most data loss is accidental.", "WARN",
            O("OFF", "Nothing", "Sensitive data can be sent anywhere.", 20, 0, null,
                N("PCI4", 'C', "3.4/4.2 card numbers must not be sent unprotected."), N("HIPAA", 'C', "164.312(e) transmission security."), N("CMMC2", 'W', "3.1.3 CUI flow.")),
            O("WARN", "Warn the user (they can override with a reason)", "A tip explains the risk; the user can send with a business reason, which is logged.", 70, 15,
                "DLP policies: policy tips with override and justification",
                N("PCI4", 'W', "Overrides must be monitored."), N("HIPAA", 'S', "Safeguards with audit."), N("CMMC2", 'S', "3.1.3 with monitoring.")),
            O("BLOCK", "Block it", "The message or share is blocked; the user is told why.", 90, 40, "DLP policies: block (no override) for PCI, PHI, CUI",
                N("PCI4", 'S', "Prevents transmission."), N("HIPAA", 'S', "Transmission security."), N("CMMC2", 'S', "3.1.3."))),

        // ---- Windows and Office apps ----
        Q("APPS-MACROS", Solutions[7], "Office macros", "Can Office documents run macros?", "Macros in documents from the internet are a classic ransomware entry.", "SIGNED",
            O("ALLOW", "Yes, users decide", "Users enable macros when asked.", 15, 0, null,
                N("E8ML2", 'C', "Block macros from the internet; only trusted macros."), N("CISIG2", 'W', "Application hardening.")),
            O("INTERNET", "Block macros in files from the internet", "Macros in downloaded or emailed files are blocked; internal templates still work.", 75, 10, "Office: block macros from the internet",
                N("E8ML2", 'S', "ML1 macro settings."), N("CISIG2", 'S', "Hardening.")),
            O("SIGNED", "Only signed, trusted macros", "Only macros signed by trusted publishers run.", 88, 25, "Office: disable all except digitally signed macros",
                N("E8ML2", 'S', "ML2/3 macro settings."))),

        Q("APPS-BROWSER", Solutions[7], "Web browser", "Which web browser should be used for work?", "Browsers are where users meet the internet.", "EDGE",
            O("ANY", "Any browser", "Users choose their own browser.", 40, 0, null,
                N("CISIG2", 'W', "9.1 ensure use of only fully supported browsers.")),
            O("EDGE", "Microsoft Edge, managed and kept up to date", "Edge is the default with SmartScreen, password protection and work profile.", 80, 5, "Edge security baseline, SmartScreen, Edge for Business",
                N("CISIG2", 'S', "9.1-9.3."), N("E8ML2", 'S', "Web browser hardening."))),

        // ---- Admin access ----
        Q("ADMIN-STANDING", Solutions[8], "Administrator rights", "How should IT administrators get their admin rights?", "Admin accounts are attackers' first target.", "PIM",
            O("STANDING", "Permanent admin accounts", "Admins always have their rights.", 25, 0, null,
                N("NIST53M", 'C', "AC-2(7)/AC-6 least privilege for privileged accounts."), N("CMMC2", 'C', "3.1.6/3.1.7 non-privileged accounts for non-security functions."),
                N("E8ML2", 'C', "Restrict administrative privileges."), N("SOX", 'W', "Privileged access monitoring.")),
            O("PIM", "Just in time, when needed (with approval for the most powerful roles)", "Admins activate a role for a few hours with MFA and a reason; Global Administrator needs approval.", 85, 30,
                "Privileged Identity Management: eligible assignments, MFA and justification on activation, approval for Tier 0",
                N("NIST53M", 'S', "AC-2(7), AC-6(5)."), N("CMMC2", 'S', "3.1.5-3.1.7."), N("E8ML2", 'S', "Restricted, time-bound."), N("SOX", 'S', "Logged, approved access.")),
            O("PAW", "Just in time, from a dedicated secure admin workstation", "Admin work only from a locked-down admin device, with phishing-resistant MFA.", 95, 55,
                "PIM + Conditional Access authentication strength + privileged access workstations",
                N("NIST53M", 'S', "AC-6, SC-7."), N("E8ML2", 'S', "Privileged operating environments."))),

        Q("ADMIN-BREAKGLASS", Solutions[8], "Emergency access", "Should there be emergency 'break glass' admin accounts if MFA or Entra ID fails?", "Without them, a misconfiguration can lock everyone out.", "YES",
            O("NO", "No", "No emergency accounts.", 40, 0, null, N("ISO27001", 'W', "A.5.29 continuity during disruption.")),
            O("YES", "Yes, two monitored accounts with security keys", "Two cloud-only accounts, excluded from Conditional Access, protected by security keys and alerted on use.", 85, 0,
                "Two emergency access accounts, FIDO2, sign-in alerts",
                N("ISO27001", 'S', "Continuity."), N("NIST53M", 'S', "AC-2 emergency accounts."))),

        // ---- AI and Copilot ----
        Q("AI-COPILOT", Solutions[9], "Microsoft 365 Copilot", "Who should use Microsoft 365 Copilot, and with which data?", "Copilot sees everything the user can access, so over-shared files become instantly findable.", "PILOT",
            O("ALL", "Everyone, all data they can access", "Every licensed user gets Copilot right away.", 40, 0, "Copilot licences assigned broadly",
                N("HIPAA", 'W', "Review over-sharing of ePHI first."), N("CMMC2", 'W', "CUI in Copilot responses needs labels and restricted search.")),
            O("PILOT", "Pilot group first, after an over-sharing clean-up", "A pilot group uses Copilot; IT fixes over-shared sites before wider roll-out.", 75, 10,
                "SharePoint Advanced Management: data access governance reports, restricted content discovery; Copilot pilot group",
                N("HIPAA", 'S', "Controlled roll-out."), N("ISO27001", 'S', "Change and risk management.")),
            O("LABELS", "Everyone, but confidential data excluded", "Copilot can't use Highly Confidential content; DLP for Copilot blocks labelled items.", 85, 15,
                "Purview DLP for Microsoft 365 Copilot; sensitivity label exclusions",
                N("CMMC2", 'S', "CUI excluded."), N("HIPAA", 'S', "ePHI excluded.")),
            O("OFF", "Not yet", "Copilot is not enabled.", 70, 20, null)),

        // ---- Network Access (Global Secure Access) ----
        // Licence prerequisites: Microsoft Entra Internet Access / Private Access, or Microsoft Entra Suite. The Microsoft traffic profile
        // needs Entra ID P1. Catalogue controls: CA-800 (block access outside the GSA network), CA-801 (M365 traffic: strong auth),
        // CA-802 (block specific websites); the web content filtering profile is the <GsaSecurityProfileId> deployment variable.
        Q("NET-INTERNET", Solutions[10], "Web filtering", "Should internet traffic go through Microsoft's secure web gateway with web content filtering?",
            "Blocking malicious and unwanted sites at the network edge protects every app and browser, on and off the office network.", "NOTINSCOPE",
            O("NOTINSCOPE", "Not in scope", "No change; web filtering stays with the current firewall or browser settings.", 50, 0, null),
            O("THREATS", "Block known-bad and high-risk categories", "Malware, phishing and adult sites are blocked for everyone on any network; other sites work.", 75, 5,
                "Global Secure Access: Internet Access traffic profile; web content filtering policy (security profile <GsaSecurityProfileId>) linked by Conditional Access (CA-802)",
                N("CISIG2", 'S', "9.2-9.3 DNS and URL filtering."), N("NISTCSF2", 'S', "PR.IR-01 networks protected."), N("CMMC2", 'S', "3.13.1 boundary protection.")),
            O("STRICT", "Allow only approved categories", "Only business categories are reachable; others are blocked and can be requested.", 88, 35,
                "Global Secure Access: Internet Access with allow-list filtering, TLS inspection where licensed",
                N("CISIG2", 'S', "9.3 URL filtering."), N("NIST53M", 'S', "SC-7 boundary protection."))),

        Q("NET-PRIVATE", Solutions[10], "Replacing the VPN", "Should Private Access replace the VPN for reaching internal apps and file servers?",
            "A VPN puts a device on the whole network; Private Access connects a signed-in user to one app at a time, with Conditional Access.", "NOTINSCOPE",
            O("NOTINSCOPE", "Not in scope", "The current VPN stays.", 50, 0, null),
            O("PILOT", "Pilot group alongside the VPN", "A pilot group reaches selected apps through Private Access; everyone else keeps the VPN.", 70, 10,
                "Global Secure Access: Private Access traffic profile, connectors, per-app access for a pilot group",
                N("NISTCSF2", 'S', "PR.AA-05 access permissions enforced."), N("CMMC2", 'S', "3.1.12 remote access controlled.")),
            O("REPLACEVPN", "Replace the VPN", "Internal apps are reached only through Private Access with MFA and device checks; the VPN is retired.", 88, 15,
                "Global Secure Access: Private Access for all internal apps; Conditional Access on each app; VPN decommissioned",
                N("NIST53M", 'S', "AC-17 remote access, SC-7."), N("CMMC2", 'S', "3.1.12-3.1.14."), N("NISTCSF2", 'S', "PR.AA-05."))),

        Q("NET-M365", Solutions[10], "Microsoft 365 traffic", "Should Microsoft 365 traffic go through Global Secure Access with tenant restrictions?",
            "Tenant restrictions stop people signing in to other companies' Microsoft 365 tenants to move data out.", "NOTINSCOPE",
            O("NOTINSCOPE", "Not in scope", "No change.", 50, 0, null),
            O("ON", "Yes, with tenant restrictions and compliant network checks", "Microsoft 365 traffic is protected and only our tenant (and approved partners) can be used from company devices.", 82, 5,
                "Global Secure Access: Microsoft traffic profile, universal tenant restrictions v2, source IP restoration, compliant network check (CA-800, CA-801)",
                N("NISTCSF2", 'S', "PR.DS-02 data in transit protected."), N("CMMC2", 'S', "3.1.3 control CUI flow."), N("ITAR", 'S', "Limits data movement to approved tenants."))),
    ]);

    /// <summary>
    /// 4.0 framework notes for CIS Microsoft 365 Benchmark L1/L2, NIST CSF 2.0 and GLBA / FTC Safeguards Rule. Drafts from GDS engineering
    /// judgement, added only where the option clearly supports, weakens or conflicts with the framework; GDS reviews them (docs/DECISIONS.md).
    /// </summary>
    private static (string Question, string Option, FrameworkNote Note)[] AddedNotes() =>
    [
        ("ID-MFA-METHOD", "SMS", N("CISM365L1", 'W', "Weak methods (SMS and voice) should be disabled in the authentication methods policy.")),
        ("ID-MFA-METHOD", "SMS", N("GLBA", 'S', "314.4(c)(5) requires MFA for anyone accessing customer information; SMS is MFA, though weak.")),
        ("ID-MFA-METHOD", "APP", N("CISM365L1", 'S', "MFA for all users; number matching on Authenticator.")),
        ("ID-MFA-METHOD", "APP", N("GLBA", 'S', "314.4(c)(5) multi-factor authentication.")),
        ("ID-MFA-METHOD", "APP", N("NISTCSF2", 'S', "PR.AA-03 users are authenticated.")),
        ("ID-MFA-METHOD", "PASSWORDLESS", N("GLBA", 'S', "314.4(c)(5) multi-factor authentication.")),
        ("ID-MFA-METHOD", "PHISHRESISTANT", N("CISM365L2", 'S', "Phishing-resistant MFA (authentication strength) for administrators.")),
        ("ID-MFA-METHOD", "PHISHRESISTANT", N("GLBA", 'S', "314.4(c)(5) multi-factor authentication.")),
        ("ID-SSPR", "SELF", N("CISM365L1", 'S', "Self-service password reset enabled with MFA.")),
        ("ID-GUESTS", "OPEN", N("CISM365L1", 'W', "Guest invitations should be limited to admins or specific roles.")),
        ("ID-GUESTS", "OPEN", N("GLBA", 'W', "314.4(c)(1) access to customer information only for authorised users.")),
        ("ID-GUESTS", "INVITED", N("CISM365L2", 'S', "Access reviews for guest users.")),
        ("DEV-PERSONAL", "FULL", N("GLBA", 'W', "314.4(c)(1)/(c)(2) access controls and inventory of devices holding customer information.")),
        ("DEV-PERSONAL", "FULL", N("NISTCSF2", 'W', "PR.AA-05 access permissions should consider device trust.")),
        ("DEV-ENCRYPTION", "NONE", N("GLBA", 'C', "314.4(c)(3) encrypt customer information at rest.")),
        ("DEV-ENCRYPTION", "SILENT", N("GLBA", 'S', "314.4(c)(3) encryption at rest.")),
        ("DEV-ENCRYPTION", "PIN", N("GLBA", 'S', "314.4(c)(3) encryption at rest.")),
        ("DEV-UPDATES", "USER", N("NISTCSF2", 'W', "PR.PS-02 software is maintained and updated.")),
        ("DEV-UPDATES", "FAST", N("NISTCSF2", 'S', "PR.PS-02 software maintained.")),
        ("DEV-UPDATES", "RINGS", N("NISTCSF2", 'S', "PR.PS-02 software maintained.")),
        ("EDR-ACTION", "AUTO", N("NISTCSF2", 'S', "RS.MI-01 incidents are contained.")),
        ("EDR-ACTION", "ISOLATE", N("NISTCSF2", 'S', "RS.MI-01 incidents are contained.")),
        ("MAIL-FORWARD", "ALLOW", N("CISM365L1", 'W', "Automatic forwarding to external domains should be blocked.")),
        ("MAIL-FORWARD", "ALLOW", N("GLBA", 'W', "Uncontrolled forwarding can disclose customer information.")),
        ("MAIL-FORWARD", "BLOCK", N("CISM365L1", 'S', "External auto-forwarding blocked.")),
        ("MAIL-LINKS", "SAFE", N("CISM365L2", 'S', "Safe Links and Safe Attachments policies enabled.")),
        ("MAIL-LINKS", "STRICT", N("CISM365L2", 'S', "Safe Links and Safe Attachments policies enabled.")),
        ("FILES-SHARING", "ANYONE", N("CISM365L1", 'W', "Anyone (anonymous) links should be restricted.")),
        ("FILES-SHARING", "ANYONE", N("GLBA", 'C', "Anonymous links can expose customer information to anyone.")),
        ("TEAMS-EXTERNAL", "OPEN", N("CISM365L1", 'W', "External access should be restricted to allowed domains.")),
        ("TEAMS-LOBBY", "NOBODY", N("CISM365L1", 'W', "Anonymous and external users should wait in the lobby.")),
        ("TEAMS-LOBBY", "EXTERNAL", N("CISM365L1", 'S', "Only people in the organisation bypass the lobby.")),
        ("TEAMS-LOBBY", "EVERYONE", N("CISM365L1", 'S', "Only people in the organisation bypass the lobby.")),
        ("DATA-LABELS", "DEFAULT", N("NISTCSF2", 'S', "ID.AM-07 data is inventoried and classified.")),
        ("DATA-LABELS", "MANDATORY", N("NISTCSF2", 'S', "ID.AM-07 data is inventoried and classified.")),
        ("DATA-DLP", "OFF", N("GLBA", 'C', "314.4(c) safeguards to protect customer information.")),
        ("DATA-DLP", "OFF", N("NISTCSF2", 'W', "PR.DS-01/02 data at rest and in transit protected.")),
        ("DATA-DLP", "WARN", N("GLBA", 'S', "Safeguards with monitoring (314.4(c)(8) activity logging).")),
        ("DATA-DLP", "BLOCK", N("GLBA", 'S', "Safeguards for customer information.")),
        ("DATA-DLP", "BLOCK", N("NISTCSF2", 'S', "PR.DS-01/02.")),
        ("ADMIN-STANDING", "STANDING", N("CISM365L1", 'W', "Keep 2-4 global administrators; use least-privileged roles.")),
        ("ADMIN-STANDING", "PIM", N("CISM365L2", 'S', "Privileged Identity Management for privileged roles.")),
        ("ADMIN-STANDING", "PIM", N("NISTCSF2", 'S', "PR.AA-05 least privilege.")),
        ("ADMIN-STANDING", "PAW", N("CISM365L2", 'S', "Privileged Identity Management for privileged roles.")),
        ("ADMIN-BREAKGLASS", "YES", N("CISM365L1", 'S', "Two emergency access accounts.")),
        ("AI-COPILOT", "ALL", N("GLBA", 'W', "Review over-sharing of customer information before broad roll-out.")),
    ];

    private static IReadOnlyList<ExperienceQuestion> WithNotes(IReadOnlyList<ExperienceQuestion> questions)
    {
        var added = AddedNotes();
        foreach (var (question, option, _) in added)
            if (!questions.Any(item => item.Code == question && item.Options.Any(choice => choice.Code == option)))
                throw new InvalidOperationException($"Framework note for unknown option {question}/{option}.");
        return questions.Select(question => question with
        {
            Options = question.Options.Select(option => option with
            {
                Frameworks = [.. option.Frameworks, .. added.Where(note => note.Question == question.Code && note.Option == option.Code).Select(note => note.Note)]
            }).ToArray()
        }).ToArray();
    }

    public static IReadOnlyList<ExperienceProfile> Profiles { get; } = BuildProfiles();

    public static IReadOnlyList<UserClassification> Classifications { get; } =
    [
        new("Executive", "Executive", "Leaders and their assistants: highest-value targets who expect the least interruption.", "EXECUTIVE"),
        new("KnowledgeWorker", "Knowledge Worker", "Office and hybrid staff on company computers.", "BALANCED"),
        new("FrontlineWorker", "Frontline Worker", "Shift, retail, warehouse or field staff, often on shared devices.", "FRONTLINE"),
        new("Traveler", "International Traveler", "People who regularly work from other countries.", "TRAVELER"),
        new("PrivilegedAdmin", "Privileged Administrator", "IT and security administrators.", "ADMIN"),
        new("Developer", "Developer", "People who need to install and run their own tools.", "DEVELOPER"),
        new("Contractor", "Contractor / Vendor", "Non-employees with temporary access.", "CONTRACTOR"),
        new("RemoteWorker", "Remote / Hybrid Worker", "Staff who mostly work from home.", "BALANCED"),
        new("SharedDevice", "Shared Device / Kiosk", "Shared computers, kiosks and meeting rooms.", "FRONTLINE"),
        new("RegulatedData", "Finance / Regulated Data", "Teams handling card, health, financial or controlled data.", "REGULATED"),
        new("Clinical", "Clinical / Healthcare", "Clinicians handling patient data on the move.", "REGULATED"),
        new("Sales", "Sales", "Customer-facing sales staff on phones and laptops.", "BALANCED"),
        new("RemoteSite", "Field / Vessel / Remote Site", "Oil and gas, marine and vessel, remote and low-bandwidth sites; devices may be shared.", "REMOTESITE"),
        new("Custom", "Custom", "Any other kind of user.", "BALANCED"),
    ];

    private static IReadOnlyList<ExperienceProfile> BuildProfiles()
    {
        ExperienceProfile P(string code, string name, string description, params (string Question, string Option)[] answers) =>
            new(code, name, description, answers.ToDictionary(answer => answer.Question, answer => answer.Option, StringComparer.OrdinalIgnoreCase));
        return
        [
            P("FRICTIONLESS", "GDS - Essentials", "Default posture for the Essentials tier: productivity first with essential protections; known gaps for regulated work.",
                ("ID-MFA-METHOD", "APP"), ("ID-SIGNIN-FREQUENCY", "RISK"), ("ID-LOCATIONS", "BLOCKRISKY"), ("ID-SSPR", "SELF"), ("ID-GUESTS", "INVITED"),
                ("DEV-PERSONAL", "APPPROTECT"), ("DEV-UPDATES", "RINGS"), ("DEV-ENCRYPTION", "SILENT"), ("DEV-LOCALADMIN", "EPM"), ("DEV-USB", "ALLOW"), ("DEV-LOCK", "15MIN"),
                ("EDR-ACTION", "AUTO"), ("EDR-ASR", "AUDIT"), ("EDR-APPCONTROL", "SMART"), ("MAIL-FORWARD", "BLOCK"), ("MAIL-LINKS", "SAFE"), ("MAIL-RETENTION", "1Y"),
                ("FILES-SHARING", "GUESTS"), ("FILES-SYNC", "MANAGED"), ("TEAMS-EXTERNAL", "OPEN"), ("TEAMS-LOBBY", "EXTERNAL"), ("TEAMS-RECORDING", "ALLOWED"),
                ("DATA-LABELS", "NONE"), ("DATA-DLP", "WARN"), ("APPS-MACROS", "SIGNED"), ("APPS-BROWSER", "EDGE"), ("ADMIN-STANDING", "PIM"), ("ADMIN-BREAKGLASS", "YES"), ("AI-COPILOT", "PILOT"), ("NET-INTERNET", "NOTINSCOPE"), ("NET-PRIVATE", "NOTINSCOPE"), ("NET-M365", "NOTINSCOPE")),
            P("BALANCED", "GDS - Balanced (Recommended)", "Default posture for the Secure Plus tier: strong protection with few interruptions.",
                ("ID-MFA-METHOD", "PASSWORDLESS"), ("ID-SIGNIN-FREQUENCY", "RISK"), ("ID-LOCATIONS", "BLOCKRISKY"), ("ID-SSPR", "SELF"), ("ID-GUESTS", "INVITED"),
                ("DEV-PERSONAL", "APPPROTECT"), ("DEV-UPDATES", "FAST"), ("DEV-ENCRYPTION", "SILENT"), ("DEV-LOCALADMIN", "EPM"), ("DEV-USB", "READONLY"), ("DEV-LOCK", "15MIN"),
                ("EDR-ACTION", "AUTO"), ("EDR-ASR", "BLOCK"), ("EDR-APPCONTROL", "SMART"), ("MAIL-FORWARD", "BLOCK"), ("MAIL-LINKS", "SAFE"), ("MAIL-RETENTION", "7Y"),
                ("FILES-SHARING", "GUESTS"), ("FILES-SYNC", "MANAGED"), ("TEAMS-EXTERNAL", "ALLOWED"), ("TEAMS-LOBBY", "EXTERNAL"), ("TEAMS-RECORDING", "ALLOWED"),
                ("DATA-LABELS", "DEFAULT"), ("DATA-DLP", "WARN"), ("APPS-MACROS", "SIGNED"), ("APPS-BROWSER", "EDGE"), ("ADMIN-STANDING", "PIM"), ("ADMIN-BREAKGLASS", "YES"), ("AI-COPILOT", "PILOT"), ("NET-INTERNET", "NOTINSCOPE"), ("NET-PRIVATE", "NOTINSCOPE"), ("NET-M365", "NOTINSCOPE")),
            P("LOCKEDDOWN", "GDS - Locked Down", "Default posture for the Secure Plus Premium tier: maximum protection for highly regulated environments; users accept more steps.",
                ("ID-MFA-METHOD", "PHISHRESISTANT"), ("ID-SIGNIN-FREQUENCY", "WORKDAY"), ("ID-LOCATIONS", "ALLOWLIST"), ("ID-SSPR", "NOPASSWORD"), ("ID-GUESTS", "APPROVED"),
                ("DEV-PERSONAL", "MANAGEDONLY"), ("DEV-UPDATES", "FAST"), ("DEV-ENCRYPTION", "PIN"), ("DEV-LOCALADMIN", "LOCKED"), ("DEV-USB", "APPROVED"), ("DEV-LOCK", "5MIN"),
                ("EDR-ACTION", "ISOLATE"), ("EDR-ASR", "STRICT"), ("EDR-APPCONTROL", "ALLOWLIST"), ("MAIL-FORWARD", "BLOCK"), ("MAIL-LINKS", "STRICT"), ("MAIL-RETENTION", "7Y"),
                ("FILES-SHARING", "EXISTING"), ("FILES-SYNC", "MANAGED"), ("TEAMS-EXTERNAL", "ALLOWED"), ("TEAMS-LOBBY", "EVERYONE"), ("TEAMS-RECORDING", "ANNOUNCED"),
                ("DATA-LABELS", "MANDATORY"), ("DATA-DLP", "BLOCK"), ("APPS-MACROS", "SIGNED"), ("APPS-BROWSER", "EDGE"), ("ADMIN-STANDING", "PAW"), ("ADMIN-BREAKGLASS", "YES"), ("AI-COPILOT", "LABELS"), ("NET-INTERNET", "NOTINSCOPE"), ("NET-PRIVATE", "NOTINSCOPE"), ("NET-M365", "ON")),
            P("EXECUTIVE", "User Group - Executive", "Strongest sign-in protection with the fewest prompts: passwordless, risk-based, protected travel.",
                ("ID-MFA-METHOD", "PHISHRESISTANT"), ("ID-SIGNIN-FREQUENCY", "RISK"), ("ID-LOCATIONS", "BLOCKRISKY"), ("ID-SSPR", "NOPASSWORD"), ("ID-GUESTS", "INVITED"),
                ("DEV-PERSONAL", "APPPROTECT"), ("DEV-UPDATES", "FAST"), ("DEV-ENCRYPTION", "SILENT"), ("DEV-LOCALADMIN", "LOCKED"), ("DEV-USB", "READONLY"), ("DEV-LOCK", "15MIN"),
                ("EDR-ACTION", "AUTO"), ("EDR-ASR", "BLOCK"), ("EDR-APPCONTROL", "SMART"), ("MAIL-FORWARD", "BLOCK"), ("MAIL-LINKS", "SAFE"), ("MAIL-RETENTION", "7Y"),
                ("FILES-SHARING", "GUESTS"), ("FILES-SYNC", "MANAGED"), ("TEAMS-EXTERNAL", "ALLOWED"), ("TEAMS-LOBBY", "EXTERNAL"), ("TEAMS-RECORDING", "ANNOUNCED"),
                ("DATA-LABELS", "DEFAULT"), ("DATA-DLP", "WARN"), ("APPS-MACROS", "SIGNED"), ("APPS-BROWSER", "EDGE"), ("ADMIN-STANDING", "PIM"), ("ADMIN-BREAKGLASS", "YES"), ("AI-COPILOT", "LABELS"), ("NET-INTERNET", "NOTINSCOPE"), ("NET-PRIVATE", "NOTINSCOPE"), ("NET-M365", "NOTINSCOPE")),
            P("FRONTLINE", "User Group - Frontline", "Shared devices, stores and kiosks: fast sign-in, web and mobile apps, short sessions, no local data; sign-in from approved countries or anywhere on a company device.",
                ("ID-MFA-METHOD", "APP"), ("ID-SIGNIN-FREQUENCY", "WORKDAY"), ("ID-LOCATIONS", "APPROVEDORCOMPLIANT"), ("ID-SSPR", "SELF"), ("ID-GUESTS", "NONE"),
                ("DEV-PERSONAL", "APPPROTECT"), ("DEV-UPDATES", "FAST"), ("DEV-ENCRYPTION", "SILENT"), ("DEV-LOCALADMIN", "LOCKED"), ("DEV-USB", "BLOCK"), ("DEV-LOCK", "5MIN"),
                ("EDR-ACTION", "AUTO"), ("EDR-ASR", "BLOCK"), ("EDR-APPCONTROL", "ALLOWLIST"), ("MAIL-FORWARD", "BLOCK"), ("MAIL-LINKS", "SAFE"), ("MAIL-RETENTION", "1Y"),
                ("FILES-SHARING", "INTERNAL"), ("FILES-SYNC", "MANAGED"), ("TEAMS-EXTERNAL", "OFF"), ("TEAMS-LOBBY", "EXTERNAL"), ("TEAMS-RECORDING", "OFF"),
                ("DATA-LABELS", "DEFAULT"), ("DATA-DLP", "BLOCK"), ("APPS-MACROS", "SIGNED"), ("APPS-BROWSER", "EDGE"), ("ADMIN-STANDING", "PIM"), ("ADMIN-BREAKGLASS", "YES"), ("AI-COPILOT", "OFF"), ("NET-INTERNET", "NOTINSCOPE"), ("NET-PRIVATE", "NOTINSCOPE"), ("NET-M365", "NOTINSCOPE")),
            P("TRAVELER", "User Group - International Traveler", "Works abroad safely: company devices, phishing-resistant MFA, approved countries per trip.",
                ("ID-MFA-METHOD", "PHISHRESISTANT"), ("ID-SIGNIN-FREQUENCY", "WORKDAY"), ("ID-LOCATIONS", "ALLOWLIST"), ("ID-SSPR", "SELF"), ("ID-GUESTS", "INVITED"),
                ("DEV-PERSONAL", "MANAGEDONLY"), ("DEV-UPDATES", "FAST"), ("DEV-ENCRYPTION", "PIN"), ("DEV-LOCALADMIN", "LOCKED"), ("DEV-USB", "APPROVED"), ("DEV-LOCK", "5MIN"),
                ("EDR-ACTION", "ISOLATE"), ("EDR-ASR", "BLOCK"), ("EDR-APPCONTROL", "SMART"), ("MAIL-FORWARD", "BLOCK"), ("MAIL-LINKS", "SAFE"), ("MAIL-RETENTION", "7Y"),
                ("FILES-SHARING", "GUESTS"), ("FILES-SYNC", "MANAGED"), ("TEAMS-EXTERNAL", "ALLOWED"), ("TEAMS-LOBBY", "EXTERNAL"), ("TEAMS-RECORDING", "ANNOUNCED"),
                ("DATA-LABELS", "DEFAULT"), ("DATA-DLP", "WARN"), ("APPS-MACROS", "SIGNED"), ("APPS-BROWSER", "EDGE"), ("ADMIN-STANDING", "PIM"), ("ADMIN-BREAKGLASS", "YES"), ("AI-COPILOT", "PILOT"), ("NET-INTERNET", "NOTINSCOPE"), ("NET-PRIVATE", "NOTINSCOPE"), ("NET-M365", "NOTINSCOPE")),
            P("ADMIN", "User Group - Privileged Administrator", "Separate, just-in-time admin access from secure workstations.",
                ("ID-MFA-METHOD", "PHISHRESISTANT"), ("ID-SIGNIN-FREQUENCY", "EVERY"), ("ID-LOCATIONS", "ALLOWLIST"), ("ID-SSPR", "HELPDESK"), ("ID-GUESTS", "APPROVED"),
                ("DEV-PERSONAL", "MANAGEDONLY"), ("DEV-UPDATES", "IMMEDIATE"), ("DEV-ENCRYPTION", "PIN"), ("DEV-LOCALADMIN", "LOCKED"), ("DEV-USB", "BLOCK"), ("DEV-LOCK", "5MIN"),
                ("EDR-ACTION", "ISOLATE"), ("EDR-ASR", "STRICT"), ("EDR-APPCONTROL", "ALLOWLIST"), ("MAIL-FORWARD", "BLOCK"), ("MAIL-LINKS", "STRICT"), ("MAIL-RETENTION", "7Y"),
                ("FILES-SHARING", "EXISTING"), ("FILES-SYNC", "MANAGED"), ("TEAMS-EXTERNAL", "ALLOWED"), ("TEAMS-LOBBY", "EXTERNAL"), ("TEAMS-RECORDING", "ANNOUNCED"),
                ("DATA-LABELS", "MANDATORY"), ("DATA-DLP", "BLOCK"), ("APPS-MACROS", "SIGNED"), ("APPS-BROWSER", "EDGE"), ("ADMIN-STANDING", "PAW"), ("ADMIN-BREAKGLASS", "YES"), ("AI-COPILOT", "LABELS"), ("NET-INTERNET", "NOTINSCOPE"), ("NET-PRIVATE", "NOTINSCOPE"), ("NET-M365", "ON")),
            P("DEVELOPER", "User Group - Developer", "Freedom to install tools with elevation on request, strong sign-in and monitoring.",
                ("ID-MFA-METHOD", "PASSWORDLESS"), ("ID-SIGNIN-FREQUENCY", "RISK"), ("ID-LOCATIONS", "BLOCKRISKY"), ("ID-SSPR", "SELF"), ("ID-GUESTS", "INVITED"),
                ("DEV-PERSONAL", "MANAGEDONLY"), ("DEV-UPDATES", "FAST"), ("DEV-ENCRYPTION", "SILENT"), ("DEV-LOCALADMIN", "EPM"), ("DEV-USB", "READONLY"), ("DEV-LOCK", "15MIN"),
                ("EDR-ACTION", "AUTO"), ("EDR-ASR", "BLOCK"), ("EDR-APPCONTROL", "SMART"), ("MAIL-FORWARD", "BLOCK"), ("MAIL-LINKS", "SAFE"), ("MAIL-RETENTION", "7Y"),
                ("FILES-SHARING", "GUESTS"), ("FILES-SYNC", "MANAGED"), ("TEAMS-EXTERNAL", "ALLOWED"), ("TEAMS-LOBBY", "EXTERNAL"), ("TEAMS-RECORDING", "ALLOWED"),
                ("DATA-LABELS", "DEFAULT"), ("DATA-DLP", "WARN"), ("APPS-MACROS", "SIGNED"), ("APPS-BROWSER", "EDGE"), ("ADMIN-STANDING", "PIM"), ("ADMIN-BREAKGLASS", "YES"), ("AI-COPILOT", "PILOT"), ("NET-INTERNET", "NOTINSCOPE"), ("NET-PRIVATE", "NOTINSCOPE"), ("NET-M365", "NOTINSCOPE")),
            P("CONTRACTOR", "User Group - Contractor / Vendor", "Least access for the shortest time: web-only, no downloads, expiring access.",
                ("ID-MFA-METHOD", "APP"), ("ID-SIGNIN-FREQUENCY", "WORKDAY"), ("ID-LOCATIONS", "ALLOWLIST"), ("ID-SSPR", "HELPDESK"), ("ID-GUESTS", "APPROVED"),
                ("DEV-PERSONAL", "WEBONLY"), ("DEV-UPDATES", "FAST"), ("DEV-ENCRYPTION", "SILENT"), ("DEV-LOCALADMIN", "LOCKED"), ("DEV-USB", "BLOCK"), ("DEV-LOCK", "5MIN"),
                ("EDR-ACTION", "ISOLATE"), ("EDR-ASR", "BLOCK"), ("EDR-APPCONTROL", "ALLOWLIST"), ("MAIL-FORWARD", "BLOCK"), ("MAIL-LINKS", "STRICT"), ("MAIL-RETENTION", "7Y"),
                ("FILES-SHARING", "INTERNAL"), ("FILES-SYNC", "MANAGED"), ("TEAMS-EXTERNAL", "OFF"), ("TEAMS-LOBBY", "EVERYONE"), ("TEAMS-RECORDING", "OFF"),
                ("DATA-LABELS", "DEFAULT"), ("DATA-DLP", "BLOCK"), ("APPS-MACROS", "SIGNED"), ("APPS-BROWSER", "EDGE"), ("ADMIN-STANDING", "PIM"), ("ADMIN-BREAKGLASS", "YES"), ("AI-COPILOT", "OFF"), ("NET-INTERNET", "NOTINSCOPE"), ("NET-PRIVATE", "NOTINSCOPE"), ("NET-M365", "NOTINSCOPE")),
            P("REMOTESITE", "User Group - Remote Site", "Oil and gas, marine and vessels, remote and low-bandwidth sites: sign in from anywhere on a company device with MFA, tolerant of patchy links (risk-based sessions, cached credentials), updates in maintenance windows with Delivery Optimization, OneDrive Files On-Demand and bandwidth limits, shared devices on rigs and vessels supported.",
                ("ID-MFA-METHOD", "PASSWORDLESS"), ("ID-SIGNIN-FREQUENCY", "RISK"), ("ID-LOCATIONS", "COMPLIANTANYWHERE"), ("ID-SSPR", "SELF"), ("ID-GUESTS", "INVITED"),
                ("DEV-PERSONAL", "APPPROTECT"), ("DEV-UPDATES", "RINGS"), ("DEV-ENCRYPTION", "SILENT"), ("DEV-LOCALADMIN", "EPM"), ("DEV-USB", "READONLY"), ("DEV-LOCK", "15MIN"),
                ("EDR-ACTION", "AUTO"), ("EDR-ASR", "BLOCK"), ("EDR-APPCONTROL", "SMART"), ("MAIL-FORWARD", "BLOCK"), ("MAIL-LINKS", "SAFE"), ("MAIL-RETENTION", "7Y"),
                ("FILES-SHARING", "GUESTS"), ("FILES-SYNC", "MANAGED"), ("TEAMS-EXTERNAL", "ALLOWED"), ("TEAMS-LOBBY", "EXTERNAL"), ("TEAMS-RECORDING", "ALLOWED"),
                ("DATA-LABELS", "DEFAULT"), ("DATA-DLP", "WARN"), ("APPS-MACROS", "SIGNED"), ("APPS-BROWSER", "EDGE"), ("ADMIN-STANDING", "PIM"), ("ADMIN-BREAKGLASS", "YES"), ("AI-COPILOT", "PILOT"), ("NET-INTERNET", "NOTINSCOPE"), ("NET-PRIVATE", "NOTINSCOPE"), ("NET-M365", "NOTINSCOPE")),
            P("REGULATED", "User Group - Regulated Data Handler", "Card, health, financial or controlled data: labels, DLP, managed devices and retention.",
                ("ID-MFA-METHOD", "PHISHRESISTANT"), ("ID-SIGNIN-FREQUENCY", "WORKDAY"), ("ID-LOCATIONS", "ALLOWLIST"), ("ID-SSPR", "SELF"), ("ID-GUESTS", "APPROVED"),
                ("DEV-PERSONAL", "MANAGEDONLY"), ("DEV-UPDATES", "FAST"), ("DEV-ENCRYPTION", "SILENT"), ("DEV-LOCALADMIN", "LOCKED"), ("DEV-USB", "APPROVED"), ("DEV-LOCK", "15MIN"),
                ("EDR-ACTION", "ISOLATE"), ("EDR-ASR", "BLOCK"), ("EDR-APPCONTROL", "ALLOWLIST"), ("MAIL-FORWARD", "BLOCK"), ("MAIL-LINKS", "SAFE"), ("MAIL-RETENTION", "7Y"),
                ("FILES-SHARING", "EXISTING"), ("FILES-SYNC", "MANAGED"), ("TEAMS-EXTERNAL", "ALLOWED"), ("TEAMS-LOBBY", "EXTERNAL"), ("TEAMS-RECORDING", "ANNOUNCED"),
                ("DATA-LABELS", "MANDATORY"), ("DATA-DLP", "BLOCK"), ("APPS-MACROS", "SIGNED"), ("APPS-BROWSER", "EDGE"), ("ADMIN-STANDING", "PIM"), ("ADMIN-BREAKGLASS", "YES"), ("AI-COPILOT", "LABELS"), ("NET-INTERNET", "NOTINSCOPE"), ("NET-PRIVATE", "NOTINSCOPE"), ("NET-M365", "ON")),
        ];
    }

    public static ExperienceQuestion? Question(string code) => Questions.FirstOrDefault(question => string.Equals(question.Code, code, StringComparison.OrdinalIgnoreCase));

    public static ExperienceProfile Profile(string? code) =>
        Profiles.FirstOrDefault(profile => string.Equals(profile.Code, code, StringComparison.OrdinalIgnoreCase)) ?? Profiles.First(profile => profile.Code == "BALANCED");

    public static UserClassification Classification(string? code) =>
        Classifications.FirstOrDefault(item => string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase) || string.Equals(item.Name, code, StringComparison.OrdinalIgnoreCase))
        ?? Classifications[^1];

    public static ExperienceFramework? Framework(string code) => Frameworks.FirstOrDefault(framework => string.Equals(framework.Code, code, StringComparison.OrdinalIgnoreCase));
}
