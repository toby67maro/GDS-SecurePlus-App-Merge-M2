using System.Globalization;
using System.Net;
using System.Text;

namespace ArchiTech.Api.Domain;

public static class DeploymentPlanPreview
{
    public static string RenderInertDraft(DeploymentChangePlan plan, DeploymentControlCatalog catalog)
    {
        var catalogById = catalog.Controls.ToDictionary(control => control.ControlId, StringComparer.Ordinal);
        var text = new StringBuilder();
        void Line(string label, string? value)
        {
            var safe = new string((value ?? "").Select(character =>
                char.IsControl(character) || character is '\u2028' or '\u2029' ? ' ' : character).ToArray());
            text.Append("# ").Append(label).Append(": ").Append(safe).Append('\n');
        }

        Line("NOTICE", "Comment-only design draft. Not executable, not an execution approval, and no templates are approved.");
        Line("Customer", plan.Customer.Name);
        Line("Plan", plan.Title);
        Line("Plan ID", plan.Id.ToString("D"));
        Line("Assessment ID", plan.AssessmentRunId.ToString("D"));
        Line("Assessment catalog", plan.AssessmentRun.CatalogVersion);
        Line("Plan status", plan.Status);
        Line("Tenant binding", "Not captured in this design draft; execution approval is not available");
        foreach (var item in plan.Items.OrderBy(item => item.Sequence))
        {
            Line("Control", item.ControlId);
            Line("Recorded name", item.ControlName);
            Line("Recorded status", item.AssessmentStatus);
            Line("Template readiness", "Not approved; manual review required");
            if (catalogById.TryGetValue(item.ControlId, out var control))
            {
                Line("Current recommended state (reference only)", control.RecommendedState);
                Line("Current validation method (reference only)", control.ValidationMethod);
                Line("Current user impact (reference only)", control.UserImpact);
            }
            else
                Line("Current catalog", "Control not found; manual review required");
            Line("Permissions", "Not determined; review required");
            Line("Rollback", "Not determined; review required");
        }
        return text.ToString();
    }

    public static string Render(DeploymentChangePlan plan, DeploymentControlCatalog catalog)
    {
        static string Encode(string? value) => WebUtility.HtmlEncode(value ?? "");
        static string Date(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc)
            .ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

        var catalogById = catalog.Controls.ToDictionary(control => control.ControlId, StringComparer.Ordinal);
        var results = plan.AssessmentRun.Results.ToDictionary(item => item.ControlId, StringComparer.Ordinal);
        var html = new StringBuilder("""
            <!doctype html><html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Archi-Tech deployment review preview</title>
            <style>
            body{font:15px system-ui,sans-serif;color:#1b2b3a;max-width:1100px;margin:2rem auto;padding:0 1rem}
            h1,h2{color:#204c76} table{width:100%;border-collapse:collapse;margin:1rem 0}
            th,td{border:1px solid #d6e0e8;padding:.5rem;text-align:left;vertical-align:top;overflow-wrap:anywhere}
            th{background:#edf4fa} .notice{border-left:4px solid #b4781b;background:#fff8e9;padding:1rem}
            .evidence{white-space:pre-wrap} @media print{body{margin:0} tr{break-inside:avoid}}
            </style></head><body>
            """);
        html.Append("<h1>Deployment review preview</h1><p class=\"notice\">")
            .Append("Internal design review only. This preview is not an executable script and does not authorize or execute tenant changes. ")
            .Append("No approved templates or isolated execution worker are configured. Review sensitive content before sharing.</p>")
            .Append("<p><strong>Customer:</strong> ").Append(Encode(plan.Customer.Name))
            .Append("<br><strong>Plan:</strong> ").Append(Encode(plan.Title))
            .Append("<br><strong>Plan ID:</strong> ").Append(plan.Id)
            .Append("<br><strong>Status:</strong> ").Append(Encode(plan.Status))
            .Append("<br><strong>Requested by:</strong> ").Append(Encode(plan.RequestedBy))
            .Append("<br><strong>Created:</strong> ").Append(Date(plan.CreatedUtc))
            .Append("<br><strong>Assessment:</strong> ").Append(Encode(plan.AssessmentRun.Name))
            .Append(" (").Append(plan.AssessmentRunId).Append(")")
            .Append("<br><strong>Assessment catalog version:</strong> ").Append(Encode(plan.AssessmentRun.CatalogVersion))
            .Append("</p><p class=\"evidence\"><strong>Plan notes:</strong> ").Append(Encode(plan.Notes)).Append("</p>")
            .Append("<h2>Selected controls</h2><p>Names and statuses are recorded in the plan; guidance below comes from the current catalog and may differ from the original assessment catalog.</p>")
            .Append("<table><thead><tr><th>Control and recorded status</th><th>Recorded assessment evidence</th><th>Current catalog guidance</th></tr></thead><tbody>");
        foreach (var item in plan.Items.OrderBy(item => item.Sequence))
        {
            catalogById.TryGetValue(item.ControlId, out var control);
            results.TryGetValue(item.ControlId, out var result);
            html.Append("<tr><td><strong>").Append(Encode(item.ControlId)).Append("</strong><br>")
                .Append(Encode(item.ControlName)).Append("<br>Status: ").Append(Encode(item.AssessmentStatus))
                .Append("</td><td class=\"evidence\">");
            if (result is null)
                html.Append("Assessment evidence unavailable.");
            else
                html.Append("<strong>Evidence:</strong> ").Append(Encode(result.Evidence))
                    .Append("<br><strong>Notes:</strong> ").Append(Encode(result.Notes))
                    .Append("<br><strong>Reviewer:</strong> ").Append(Encode(result.Assessor))
                    .Append(" &mdash; ").Append(Date(result.ReviewedUtc));
            html.Append("</td><td class=\"evidence\">");
            if (control is null)
                html.Append("Control not found in current catalog.");
            else
                html.Append("<strong>Recommended state:</strong> ").Append(Encode(control.RecommendedState))
                    .Append("<br><strong>Validation:</strong> ").Append(Encode(control.ValidationMethod))
                    .Append("<br><strong>User impact:</strong> ").Append(Encode(control.UserImpact))
                    .Append("<br><strong>Reference:</strong> ").Append(Encode(control.SourceReference));
            html.Append("</td></tr>");
        }
        html.Append("</tbody></table><h2>Review history</h2><table><thead><tr><th>Event</th><th>Actor and time</th><th>Rationale</th></tr></thead><tbody>");
        foreach (var item in plan.Events.OrderBy(item => item.OccurredUtc).ThenBy(item => item.Id))
            html.Append("<tr><td>").Append(Encode(item.EventType)).Append("</td><td>")
                .Append(Encode(item.Actor)).Append(" &mdash; ").Append(Date(item.OccurredUtc))
                .Append("</td><td class=\"evidence\">").Append(Encode(item.Notes)).Append("</td></tr>");
        return html.Append("</tbody></table></body></html>").ToString();
    }
}
