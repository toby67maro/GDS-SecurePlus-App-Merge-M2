using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EngineeringWorkspace.Application.Export;

public enum ExportFormat
{
    Csv,
    Html,
    Json,
    Txt
}

/// <summary>A titled table of an export.</summary>
public sealed record ExportTable(string Title, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows);

/// <summary>
/// What a page exports: title, subject (customer), summary figures (KPIs or properties) and tables. The same document
/// is written as CSV, branded HTML, JSON or plain text by <see cref="IExportService"/>.
/// </summary>
public sealed record ExportDocument(
    string Title,
    string? Subject,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<KeyValuePair<string, string>> Summary,
    IReadOnlyList<ExportTable> Tables,
    string? Notes = null);

/// <summary>Writes export documents. One shared exporter for Customer Profiles, Reporting and Specification Comparison.</summary>
public interface IExportService
{
    string Render(ExportDocument document, ExportFormat format);

    /// <summary>Default file name: "SecurePlus-{title}-{subject}-{timestamp}.{ext}".</summary>
    string SuggestFileName(ExportDocument document, ExportFormat format);

    Task WriteAsync(ExportDocument document, ExportFormat format, string path, CancellationToken cancellationToken = default);
}

public sealed class ExportService(ExportBranding branding) : IExportService
{
    private static readonly Regex SecretName = new(@"secret|password|passwd|pwd|certificate|thumbprint|private.?key|token|connection.?string|credential",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public const string Masked = "******** (not exported)";

    public static string Extension(ExportFormat format) => format switch
    {
        ExportFormat.Csv => "csv",
        ExportFormat.Html => "html",
        ExportFormat.Json => "json",
        _ => "txt"
    };

    /// <summary>True when a property name looks like a secret; its value is never exported.</summary>
    public static bool IsSecretName(string name) => SecretName.IsMatch(name);

    public string SuggestFileName(ExportDocument document, ExportFormat format)
    {
        var parts = new[] { "SecurePlus", document.Title, document.Subject, document.GeneratedAtUtc.ToLocalTime().ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => Regex.Replace(part!, @"[^A-Za-z0-9]+", "-").Trim('-'));
        return $"{string.Join("-", parts)}.{Extension(format)}";
    }

    public async Task WriteAsync(ExportDocument document, ExportFormat format, string path, CancellationToken cancellationToken = default)
    {
        var content = Render(document, format);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(format == ExportFormat.Csv), cancellationToken);
    }

    public string Render(ExportDocument document, ExportFormat format)
    {
        ArgumentNullException.ThrowIfNull(document);
        var safe = Redact(document);
        return format switch
        {
            ExportFormat.Csv => Csv(safe),
            ExportFormat.Html => Html(safe),
            ExportFormat.Json => Json(safe),
            _ => Text(safe)
        };
    }

    /// <summary>Masks summary values and table columns whose names look like secrets.</summary>
    private static ExportDocument Redact(ExportDocument document)
    {
        var summary = document.Summary.Select(pair => IsSecretName(pair.Key) && !string.IsNullOrEmpty(pair.Value) ? new KeyValuePair<string, string>(pair.Key, Masked) : pair).ToArray();
        var tables = document.Tables.Select(table =>
        {
            var secret = table.Columns.Select(IsSecretName).ToArray();
            if (!secret.Any(flag => flag)) return table;
            var rows = table.Rows.Select(row => (IReadOnlyList<string?>)row.Select((value, index) => index < secret.Length && secret[index] && !string.IsNullOrEmpty(value) ? Masked : value).ToArray())
                .ToArray();
            return table with { Rows = rows };
        }).ToArray();
        return document with { Summary = summary, Tables = tables };
    }

    private static string CsvField(string? value)
    {
        value ??= string.Empty;
        // Formula injection: a leading =, +, - or @ is neutralised for spreadsheets.
        if (value.Length > 0 && "=+-@".Contains(value[0], StringComparison.Ordinal)) value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
    }

    private static string Csv(ExportDocument document)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CsvField(document.Title));
        if (document.Subject is not null) builder.AppendLine($"Customer,{CsvField(document.Subject)}");
        builder.AppendLine($"Generated,{CsvField(document.GeneratedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))}");
        foreach (var pair in document.Summary) builder.AppendLine($"{CsvField(pair.Key)},{CsvField(pair.Value)}");
        foreach (var table in document.Tables)
        {
            builder.AppendLine();
            if (document.Tables.Count > 1) builder.AppendLine(CsvField(table.Title));
            builder.AppendLine(string.Join(",", table.Columns.Select(CsvField)));
            foreach (var row in table.Rows) builder.AppendLine(string.Join(",", row.Select(CsvField)));
        }

        return builder.ToString();
    }

    private static string Text(ExportDocument document)
    {
        var builder = new StringBuilder();
        builder.AppendLine(document.Title);
        builder.AppendLine(new string('=', Math.Min(100, document.Title.Length)));
        if (document.Subject is not null) builder.AppendLine($"Customer: {document.Subject}");
        builder.AppendLine($"Generated: {document.GeneratedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
        if (document.Summary.Count > 0)
        {
            builder.AppendLine();
            var width = document.Summary.Max(pair => pair.Key.Length);
            foreach (var pair in document.Summary) builder.AppendLine($"{pair.Key.PadRight(width)} : {pair.Value}");
        }

        foreach (var table in document.Tables)
        {
            builder.AppendLine();
            builder.AppendLine(table.Title);
            builder.AppendLine(new string('-', Math.Min(100, table.Title.Length)));
            var widths = table.Columns.Select((column, index) => Math.Min(60, Math.Max(column.Length, table.Rows.Select(row => index < row.Count ? (row[index] ?? string.Empty).Length : 0).DefaultIfEmpty(0).Max())))
                .ToArray();
            string Line(IEnumerable<string?> cells) => string.Join("  ", cells.Select((cell, index) => Fit(cell ?? string.Empty, widths[index]).PadRight(widths[index]))).TrimEnd();
            builder.AppendLine(Line(table.Columns));
            builder.AppendLine(string.Join("  ", widths.Select(width => new string('-', width))));
            foreach (var row in table.Rows) builder.AppendLine(Line(row));
        }

        if (!string.IsNullOrWhiteSpace(document.Notes))
        {
            builder.AppendLine();
            builder.AppendLine(document.Notes);
        }

        return builder.ToString();

        static string Fit(string value, int width)
        {
            value = value.Replace('\r', ' ').Replace('\n', ' ');
            return value.Length <= width ? value : value[..Math.Max(0, width - 1)] + "…";
        }
    }

    private static string Json(ExportDocument document)
    {
        var payload = new
        {
            title = document.Title,
            customer = document.Subject,
            generatedAtUtc = document.GeneratedAtUtc.UtcDateTime,
            summary = document.Summary.ToDictionary(pair => pair.Key, pair => pair.Value),
            tables = document.Tables.Select(table => new
            {
                title = table.Title,
                rows = table.Rows.Select(row => table.Columns.Select((column, index) => (column, value: index < row.Count ? row[index] : null))
                    .GroupBy(pair => pair.column).ToDictionary(group => group.Key, group => group.First().value))
            }),
            notes = document.Notes
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private string Html(ExportDocument document)
    {
        static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
        var builder = new StringBuilder();
        builder.Append($$"""
                         <!DOCTYPE html>
                         <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
                         <title>{{E(document.Title)}}</title>
                         <style>
                         body{font-family:'Segoe UI',Arial,sans-serif;margin:0;color:#1F1F1F;background:#F3F6FA}
                         header{background:{{branding.Primary}};color:#fff;padding:18px 28px;border-bottom:4px solid {{branding.Accent}}}
                         header .brand{font-size:13px;letter-spacing:.08em;text-transform:uppercase;color:{{branding.Accent}}}
                         header h1{margin:4px 0 0;font-size:24px;font-weight:600}
                         header .meta{font-size:13px;opacity:.85;margin-top:4px}
                         main{padding:20px 28px}
                         .kpis{display:flex;flex-wrap:wrap;gap:12px;margin-bottom:20px}
                         .kpi{background:#fff;border:1px solid #D9D9D9;border-left:4px solid {{branding.Secondary}};border-radius:6px;padding:10px 14px;min-width:160px}
                         .kpi .k{font-size:12px;color:#4D4D4D}.kpi .v{font-size:18px;font-weight:600;color:{{branding.Primary}}}
                         h2{font-size:17px;color:{{branding.Primary}};margin:24px 0 8px}
                         table{border-collapse:collapse;width:100%;background:#fff;font-size:13px}
                         th{background:{{branding.Secondary}};color:#fff;text-align:left;padding:7px 9px;font-weight:600}
                         td{border-bottom:1px solid #E4E8EE;padding:6px 9px;vertical-align:top}
                         tr:nth-child(even) td{background:#F7FAFC}
                         footer{color:#7F7F7F;font-size:12px;padding:12px 28px 24px}
                         </style></head><body>
                         <header><div class="brand">{{E(branding.CompanyName)}} · {{E(branding.ProductName)}}</div><h1>{{E(document.Title)}}</h1>
                         <div class="meta">{{(document.Subject is null ? string.Empty : $"Customer: {E(document.Subject)} · ")}}Generated {{E(document.GeneratedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))}}</div></header>
                         <main>
                         """);
        if (document.Summary.Count > 0)
        {
            builder.Append("<div class=\"kpis\">");
            foreach (var pair in document.Summary) builder.Append($"<div class=\"kpi\"><div class=\"k\">{E(pair.Key)}</div><div class=\"v\">{E(pair.Value)}</div></div>");
            builder.Append("</div>");
        }

        foreach (var table in document.Tables)
        {
            builder.Append($"<h2>{E(table.Title)}</h2><table><thead><tr>");
            foreach (var column in table.Columns) builder.Append($"<th>{E(column)}</th>");
            builder.Append("</tr></thead><tbody>");
            foreach (var row in table.Rows)
            {
                builder.Append("<tr>");
                foreach (var cell in row) builder.Append($"<td>{E(cell)}</td>");
                builder.Append("</tr>");
            }

            builder.Append("</tbody></table>");
        }

        if (!string.IsNullOrWhiteSpace(document.Notes)) builder.Append($"<p>{E(document.Notes)}</p>");
        builder.Append($"</main><footer>{E(branding.CompanyName)} {E(branding.ProductName)} · secrets are never exported.</footer></body></html>");
        return builder.ToString();
    }
}

/// <summary>Brand values used in HTML exports (from the theme and branding configuration).</summary>
public sealed record ExportBranding(string CompanyName, string ProductName, string Primary, string Secondary, string Accent)
{
    public static ExportBranding Gds { get; } = new("Global Data Systems", "Secure Plus – Modern Work", "#002060", "#156082", "#72E1F5");
}
