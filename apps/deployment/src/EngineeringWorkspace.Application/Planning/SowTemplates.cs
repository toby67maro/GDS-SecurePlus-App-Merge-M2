using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace EngineeringWorkspace.Application.Planning;

/// <summary>
/// Built-in statement of work templates (drafts for GDS commercial and legal review) and the placeholder filler.
/// Placeholders are written {{Name}}; <see cref="Placeholders"/> lists every one the SOW page fills.
/// </summary>
public static partial class SowTemplates
{
    public static IReadOnlyList<(string Name, string Description)> Placeholders { get; } =
    [
        ("Date", "Today's date"), ("Customer.Name", "Customer name"), ("Customer.Address", "Customer address"), ("Customer.Industry", "Industry"),
        ("Customer.Size", "Organisation size"), ("Customer.PrimaryContact", "Primary contact"), ("Customer.Approvers", "Change approvers"),
        ("Customer.Tenants", "Microsoft 365 tenants"), ("Customer.ServiceTier", "Service tier"), ("Customer.UserCount", "Users in user groups"),
        ("GDS.SalesLead", "GDS sales lead"), ("GDS.TechnicalLead", "GDS technical lead"), ("GDS.ProjectManager", "GDS project manager"),
        ("Assessment.Summary", "Latest assessment: compliance and gaps"), ("Roadmap.Summary", "Roadmap: items, hours and dates"), ("Roadmap.Phases", "Roadmap phases with dates"),
        ("ProjectPlan.Name", "Project plan name"), ("ProjectPlan.Start", "Project start"), ("ProjectPlan.Finish", "Project finish"), ("ProjectPlan.Hours", "Planned hours"),
        ("ProjectPlan.Tasks", "Project plan tasks"), ("Experience.Groups", "User groups and their experience"), ("Experience.Summary", "Experience summary"),
        ("Frameworks", "Compliance targets"), ("Deliverables", "Deliverables")
    ];

    public static IReadOnlyList<SowTemplate> BuiltIn { get; } =
    [
        new("GDS-SP-STANDARD", "GDS Secure Plus – Statement of Work", "1.0-draft", "SecurePlus", "SecurePlus",
        [
            new("OVERVIEW", "1. Overview", 1,
                "This Statement of Work (SOW), dated {{Date}}, describes the Microsoft 365 Secure Plus services Global Data Systems (GDS) will deliver to {{Customer.Name}}.\n\n" +
                "Secure Plus builds out and secures the customer's Microsoft 365 tenant and tailors the experience to each group of users, so security strengthens without getting in the way of work.", true),
            new("CUSTOMER", "2. Customer Environment", 2,
                "Customer: {{Customer.Name}}\nAddress: {{Customer.Address}}\nIndustry: {{Customer.Industry}}\nOrganisation size: {{Customer.Size}}\nMicrosoft 365 tenants: {{Customer.Tenants}}\n" +
                "Service tier: {{Customer.ServiceTier}}\nCompliance targets: {{Frameworks}}", true),
            new("CURRENT", "3. Current State", 3, "{{Assessment.Summary}}", true),
            new("EXPERIENCE", "4. Tailored User Experience", 4,
                "GDS designs the experience for every group of users, not one size for all. The groups and the experience agreed for each are:\n\n{{Experience.Groups}}\n\n{{Experience.Summary}}", true),
            new("SCOPE", "5. Scope of Work", 5,
                "GDS will remediate the gaps found in the assessment, in the order below (dependencies first, then the most risk reduced per hour):\n\n{{Roadmap.Phases}}\n\n{{Roadmap.Summary}}", true),
            new("PLAN", "6. Project Plan and Schedule", 6,
                "Plan: {{ProjectPlan.Name}}\nStart: {{ProjectPlan.Start}}  Finish: {{ProjectPlan.Finish}}\nPlanned effort: {{ProjectPlan.Hours}} hours (Monday to Friday, 8-hour days)\n\n{{ProjectPlan.Tasks}}", false),
            new("DELIVERABLES", "7. Deliverables", 7, "{{Deliverables}}", true),
            new("ROLES", "8. Roles and Responsibilities", 8,
                "GDS project manager: {{GDS.ProjectManager}}\nGDS technical lead: {{GDS.TechnicalLead}}\nGDS sales lead: {{GDS.SalesLead}}\n" +
                "Customer primary contact: {{Customer.PrimaryContact}}\nCustomer change approvers: {{Customer.Approvers}}\n\n" +
                "The customer provides timely access, decisions and change approvals. Every change to the tenant is approved by a change approver before GDS makes it.", true),
            new("CHANGE", "9. Change Management", 9,
                "Changes are delivered through GDS change requests: pilot group first, then the wider organisation, with communication to affected users and a rollback plan for every change.", true),
            new("ASSUMPTIONS", "10. Assumptions and Exclusions", 10,
                "• Licences required for the agreed experience are in place before the related work starts.\n• Work is performed remotely during business hours unless agreed otherwise.\n" +
                "• Third-party applications, on-premises infrastructure and data migration are out of scope unless listed above.\n• Estimates are based on the latest assessment; a re-assessment may change them.", true),
            new("ACCEPTANCE", "11. Acceptance", 11,
                "Each phase is accepted when its changes are deployed and a re-assessment confirms them. Signatures:\n\n{{Customer.Name}}: ______________________  Date: ________\n\nGlobal Data Systems: ______________________  Date: ________", true),
        ], IsBuiltIn: true),
        new("GDS-SP-ASSESSMENT", "GDS Secure Plus – Assessment Only", "1.0-draft", "Assessment", null,
        [
            new("OVERVIEW", "1. Overview", 1, "GDS will assess the Microsoft 365 tenant of {{Customer.Name}} against the Secure Plus baselines and {{Frameworks}}, dated {{Date}}.", true),
            new("APPROACH", "2. Approach", 2,
                "Read-only assessment through a Microsoft Graph app registration with least-privilege, read-only permissions. No changes are made to the tenant.", true),
            new("EXPERIENCE", "3. Experience Workshop", 3, "GDS facilitates an experience workshop to agree the experience of each user group:\n\n{{Experience.Groups}}", true),
            new("DELIVERABLES", "4. Deliverables", 4,
                "• Assessment report with compliance by framework, solution area and severity\n• Experience Map for every user group\n• Remediation roadmap with a work-day schedule\n• Executive briefing", true),
            new("ROLES", "5. Contacts", 5, "Customer: {{Customer.PrimaryContact}}\nGDS: {{GDS.TechnicalLead}}, {{GDS.SalesLead}}", true),
        ], IsBuiltIn: true),
        new("GDS-SP-PREMIUM", "GDS Secure Plus Premium – Statement of Work", "1.0-draft", "SecurePlus", "SecurePlusPremium",
        [
            new("OVERVIEW", "1. Overview", 1,
                "GDS will deliver Secure Plus Premium to {{Customer.Name}}: everything in Secure Plus plus advanced threat protection, data protection and compliance automation. Dated {{Date}}.", true),
            new("CURRENT", "2. Current State", 2, "{{Assessment.Summary}}", true),
            new("EXPERIENCE", "3. Tailored User Experience", 3, "{{Experience.Groups}}", true),
            new("SCOPE", "4. Scope and Schedule", 4, "{{Roadmap.Phases}}\n\n{{ProjectPlan.Tasks}}", true),
            new("DELIVERABLES", "5. Deliverables", 5, "{{Deliverables}}", true),
            new("ROLES", "6. Roles", 6, "GDS: {{GDS.ProjectManager}} (PM), {{GDS.TechnicalLead}} (technical lead)\nCustomer: {{Customer.PrimaryContact}}; approvers {{Customer.Approvers}}", true),
            new("ACCEPTANCE", "7. Acceptance", 7, "{{Customer.Name}}: ______________________  Date: ________\nGlobal Data Systems: ______________________  Date: ________", true),
        ], IsBuiltIn: true),
    ];

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.]+)\s*\}\}")]
    private static partial Regex PlaceholderPattern();

    /// <summary>Fills {{placeholders}}; unknown ones are left visible as [Name] so nothing silently disappears.</summary>
    public static string Fill(string text, IReadOnlyDictionary<string, string> values) =>
        PlaceholderPattern().Replace(text, match => values.TryGetValue(match.Groups[1].Value, out var value) && !string.IsNullOrWhiteSpace(value) ? value : $"[{match.Groups[1].Value}]");

    public static IReadOnlyList<(string Title, string Text)> Render(SowTemplate template, IReadOnlyDictionary<string, string> values) =>
        template.Sections.OrderBy(section => section.Order).Select(section => (section.Title, Fill(section.Content, values))).ToArray();

    /// <summary>A branded HTML document (opens in any browser and in Word; print to PDF from either).</summary>
    public static string ToHtml(string title, string customer, IReadOnlyList<(string Title, string Text)> sections)
    {
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>").Append(WebUtility.HtmlEncode(title)).Append("</title><style>")
            .Append("body{font-family:'Segoe UI',Arial,sans-serif;color:#1f1f1f;max-width:900px;margin:40px auto;line-height:1.5}")
            .Append("header{border-bottom:4px solid #002060;padding-bottom:12px;margin-bottom:24px}h1{color:#002060;margin:0}h2{color:#156082;margin-top:28px}")
            .Append(".sub{color:#4d4d4d}pre{white-space:pre-wrap;font-family:inherit}</style></head><body><header><h1>")
            .Append(WebUtility.HtmlEncode(title)).Append("</h1><div class=\"sub\">").Append(WebUtility.HtmlEncode(customer)).Append(" · Global Data Systems</div></header>");
        foreach (var (heading, text) in sections)
            html.Append("<h2>").Append(WebUtility.HtmlEncode(heading)).Append("</h2><pre>").Append(WebUtility.HtmlEncode(text)).Append("</pre>");
        return html.Append("</body></html>").ToString();
    }
}
