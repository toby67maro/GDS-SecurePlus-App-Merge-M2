using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EngineeringWorkspace.Application.Governance;

/// <summary>One section of a change request template.</summary>
public sealed record TemplateSection(string Title, string Body);

/// <summary>What a change request template holds: sections and wording, the fields it asks for, the approval text and the approval email.</summary>
public sealed record ChangeRequestTemplateContent(
    IReadOnlyList<TemplateSection> Sections,
    IReadOnlyList<string> Fields,
    string ApprovalText,
    string EmailSubject,
    string EmailBody);

/// <summary>
/// A change request template (secureplus.ChangeRequestTemplate). <see cref="CustomerKey"/> null = global template. A customer copy keeps a
/// link to the global template it came from (<see cref="SourceKey"/> and <see cref="SourceVersion"/>), so the Global Library (4.2) can show
/// "Update available"; updates are never forced. The built-in GDS default (<see cref="IsSystem"/>) always exists and cannot be deleted.
/// </summary>
public sealed record ChangeRequestTemplate(
    string Key,
    string? CustomerKey,
    string Name,
    int Version,
    string? SourceKey,
    int? SourceVersion,
    bool IsSystem,
    ChangeRequestTemplateContent Content)
{
    public bool IsGlobal => CustomerKey is null;
}

public static partial class ChangeRequestTemplates
{
    public const string DefaultKey = "gds-default";

    /// <summary>Placeholders a template can use; filled from the change request and customer.</summary>
    public static IReadOnlyList<string> Placeholders { get; } =
    [
        "Customer", "Tenant", "ChangeRequest.Id", "ChangeRequest.Title", "ChangeRequest.Description", "ChangeRequest.Risk", "ChangeRequest.Impact",
        "ChangeRequest.RollbackPlan", "ChangeRequest.Window", "ChangeRequest.Controls", "ChangeRequest.RequestedBy", "ChangeRequest.Owner", "Approver"
    ];

    /// <summary>The built-in GDS default template.</summary>
    public static ChangeRequestTemplate Default { get; } = new(DefaultKey, null, "GDS Default Change Request", 1, null, null, true, new ChangeRequestTemplateContent(
    [
        new("Summary", "{{ChangeRequest.Title}} for {{Customer}} ({{Tenant}})."),
        new("What Will Change", "{{ChangeRequest.Description}}\n\nControls and policies:\n{{ChangeRequest.Controls}}"),
        new("Why", "This change closes gaps found in the Secure Plus assessment against the agreed specification."),
        new("Risk and User Impact", "Risk: {{ChangeRequest.Risk}}.\n{{ChangeRequest.Impact}}"),
        new("Change Window", "{{ChangeRequest.Window}}"),
        new("Rollback Plan", "{{ChangeRequest.RollbackPlan}}"),
        new("Pilot First", "Conditional Access policies start in report-only mode and Intune policies start unassigned; they are enforced ring by ring after validation."),
    ],
    ["Title", "Description", "Risk", "Impact", "Rollback Plan", "Change Window", "Owner", "Approver"],
    "I approve change request {{ChangeRequest.Id}} for {{Customer}} as described, in the window stated.",
    "[GDS CR-{{ChangeRequest.Id}}] Approval requested: {{ChangeRequest.Title}}",
    "Hello {{Approver}},\n\nGDS requests your approval for the change below to {{Customer}}'s Microsoft 365 tenant ({{Tenant}}).\n\n{{Sections}}\n\n" +
    "To approve, reply to this email with \"Approved\" (keep the reference in the subject). To decline, reply with your reason.\n\nThank you,\n{{ChangeRequest.Owner}}\nGlobal Data Systems"));

    /// <summary>Fills {{placeholders}}; {{Sections}} becomes every section as "Title" + body. Unknown placeholders are left as they are.</summary>
    public static string Fill(string text, IReadOnlyDictionary<string, string> values, IReadOnlyList<TemplateSection>? sections = null)
    {
        var filled = Placeholder().Replace(text ?? string.Empty, match =>
        {
            var name = match.Groups[1].Value;
            if (name == "Sections" && sections is not null)
                return string.Join("\n\n", sections.Select(section => $"{section.Title}\n{Fill(section.Body, values)}"));
            return values.TryGetValue(name, out var value) ? value : match.Value;
        });
        return filled;
    }

    /// <summary>The sections as editable text: each section starts with "## Title".</summary>
    public static string SectionsToText(IReadOnlyList<TemplateSection> sections)
    {
        var builder = new StringBuilder();
        foreach (var section in sections) builder.Append(CultureInfo.InvariantCulture, $"## {section.Title}\n{section.Body.TrimEnd()}\n\n");
        return builder.ToString().TrimEnd() + "\n";
    }

    public static IReadOnlyList<TemplateSection> SectionsFromText(string text)
    {
        var sections = new List<TemplateSection>();
        string? title = null;
        var body = new StringBuilder();
        foreach (var line in (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (title is not null) sections.Add(new TemplateSection(title, body.ToString().Trim()));
                title = line[3..].Trim();
                body.Clear();
            }
            else
            {
                body.Append(line).Append('\n');
            }
        }

        if (title is not null) sections.Add(new TemplateSection(title, body.ToString().Trim()));
        else if (body.ToString().Trim().Length > 0) sections.Add(new TemplateSection("Details", body.ToString().Trim()));
        return sections;
    }

    [GeneratedRegex(@"\{\{([A-Za-z.]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}

public interface IChangeRequestTemplateRepository
{
    bool IsAvailable { get; }

    /// <summary>Global templates (the GDS default is created when missing).</summary>
    Task<IReadOnlyList<ChangeRequestTemplate>> ListGlobalAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChangeRequestTemplate>> ListForCustomerAsync(string customerKey, CancellationToken cancellationToken = default);

    /// <summary>Saves a template (version + 1). The GDS default cannot be changed (save a copy instead).</summary>
    Task<ChangeRequestTemplate> SaveAsync(ChangeRequestTemplate template, CancellationToken cancellationToken = default);

    /// <summary>Copies a global template to the customer, keeping a link to the source and its version.</summary>
    Task<ChangeRequestTemplate> AssignAsync(string customerKey, string globalKey, CancellationToken cancellationToken = default);

    /// <summary>Saves a customer template as a new global template; the customer copy is linked to it.</summary>
    Task<ChangeRequestTemplate> SaveAsGlobalAsync(ChangeRequestTemplate customerTemplate, string name, CancellationToken cancellationToken = default);

    /// <summary>Deletes a template; the GDS default cannot be deleted.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}
