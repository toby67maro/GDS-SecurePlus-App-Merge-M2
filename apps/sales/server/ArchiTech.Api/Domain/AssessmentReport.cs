using System.Globalization;
using System.Net;
using System.Text;

namespace ArchiTech.Api.Domain;

public static class AssessmentReport
{
    public static string Render(AssessmentRun run, DeploymentControlCatalog catalog)
    {
        static string Encode(string? value) => WebUtility.HtmlEncode(value ?? "");
        static string Date(DateTime? value) =>
            value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : "Not completed";

        var controls = catalog.Controls.ToDictionary(item => item.ControlId, StringComparer.Ordinal);
        var html = new StringBuilder();
        html.Append("""
            <!doctype html><html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Archi-Tech assessment report</title>
            <style>
            body{font:15px system-ui,sans-serif;color:#1b2b3a;max-width:1100px;margin:2rem auto;padding:0 1rem}
            h1,h2{color:#204c76} table{width:100%;border-collapse:collapse;margin:1rem 0}
            th,td{border:1px solid #d6e0e8;padding:.5rem;text-align:left;vertical-align:top;overflow-wrap:anywhere}
            th{background:#edf4fa} .notice{border-left:4px solid #b4781b;background:#fff8e9;padding:1rem}
            .evidence{white-space:pre-wrap} @media print{body{margin:0} tr{break-inside:avoid}}
            </style></head><body>
            """);
        html.Append("<h1>Assessment report: ").Append(Encode(run.Customer.Name)).Append("</h1>")
            .Append("<p><strong>Assessment:</strong> ").Append(Encode(run.Name))
            .Append(" &nbsp; <strong>State:</strong> ").Append(run.IsComplete ? "Completed snapshot" : "In progress")
            .Append(" &nbsp; <strong>Source:</strong> ").Append(Encode(run.Source))
            .Append("<br><strong>Assessor:</strong> ").Append(Encode(run.Assessor))
            .Append(" &nbsp; <strong>Started:</strong> ").Append(Date(run.StartedUtc))
            .Append(" &nbsp; <strong>Completed:</strong> ").Append(Date(run.CompletedUtc))
            .Append("<br><strong>Catalog:</strong> ").Append(Encode(run.CatalogVersion)).Append("</p>")
            .Append("<p class=\"notice\">Internal assessment data; review before sharing. This report reflects recorded findings, not a live tenant verification or authorization to change a tenant. Not assessed controls are not confirmed gaps. Exceptions and not-applicable controls contribute points under the current scoring model.</p>")
            .Append("<h2>Readiness</h2><p><strong>")
            .Append(run.OverallPercentage.ToString("0.0", CultureInfo.InvariantCulture)).Append("%</strong> &mdash; ")
            .Append(Encode(run.ReadinessRating)).Append(" &nbsp; ")
            .Append(run.TotalControls.ToString(CultureInfo.InvariantCulture)).Append(" controls</p>")
            .Append("<p>Implemented: ").Append(run.ImplementedControls)
            .Append(" | Partial: ").Append(run.PartialControls)
            .Append(" | Not implemented: ").Append(run.NotImplementedControls)
            .Append(" | Not assessed: ").Append(run.NotAssessedControls)
            .Append(" | Exceptions: ").Append(run.ExceptionControls)
            .Append(" | Not applicable: ").Append(run.NotApplicableControls).Append("</p>");

        html.Append("<h2>Score breakdown</h2><table><thead><tr><th>Type</th><th>Area</th><th>Score</th><th>Open</th></tr></thead><tbody>");
        foreach (var item in run.Breakdowns.OrderBy(item => item.BreakdownType, StringComparer.Ordinal)
                     .ThenBy(item => item.Label, StringComparer.Ordinal))
        {
            html.Append("<tr><td>").Append(Encode(item.BreakdownType)).Append("</td><td>")
                .Append(Encode(item.Label)).Append("</td><td>")
                .Append(item.Percentage.ToString("0.0", CultureInfo.InvariantCulture)).Append("%</td><td>")
                .Append(item.GapCount).Append("</td></tr>");
        }
        html.Append("</tbody></table><h2>Recommendations</h2><ul>");
        foreach (var item in run.Recommendations.OrderBy(item => item.Sequence))
        {
            html.Append("<li><strong>").Append(Encode(item.Severity)).Append(":</strong> ")
                .Append(Encode(item.Text)).Append("</li>");
        }
        html.Append("</ul><h2>Control findings</h2><table><thead><tr><th>Control</th><th>Status</th><th>Evidence, notes and exception rationale</th></tr></thead><tbody>");
        foreach (var result in run.Results.OrderBy(item => item.ControlId, StringComparer.Ordinal))
        {
            controls.TryGetValue(result.ControlId, out var control);
            html.Append("<tr><td><strong>").Append(Encode(result.ControlId)).Append("</strong><br>")
                .Append(Encode(control?.Name ?? result.ControlId)).Append("</td><td>")
                .Append(Encode(result.Status)).Append("</td><td class=\"evidence\">");
            if (!string.IsNullOrWhiteSpace(result.Evidence))
                html.Append("<strong>Evidence:</strong> ").Append(Encode(result.Evidence)).Append("<br>");
            if (!string.IsNullOrWhiteSpace(result.Notes))
                html.Append("<strong>Notes:</strong> ").Append(Encode(result.Notes)).Append("<br>");
            if (!string.IsNullOrWhiteSpace(result.ExceptionReason))
                html.Append("<strong>Exception rationale:</strong> ").Append(Encode(result.ExceptionReason)).Append("<br>");
            html.Append("<strong>Reviewed by:</strong> ").Append(Encode(result.Assessor))
                .Append(" &mdash; ").Append(Date(result.ReviewedUtc)).Append("</td></tr>");
        }
        return html.Append("</tbody></table></body></html>").ToString();
    }
}
