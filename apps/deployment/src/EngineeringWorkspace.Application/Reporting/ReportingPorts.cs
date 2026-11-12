using EngineeringWorkspace.Domain.Common;

namespace EngineeringWorkspace.Application.Reporting;

// Interfaces only; reporting is implemented in M8. Reports use structured data, never UI scraping. Planned
// outputs: interactive, CSV, Excel, HTML and PDF.

public enum ReportFormat
{
    Interactive,
    Csv,
    Excel,
    Html,
    Pdf
}

/// <summary>Structured report data: named columns and rows of values.</summary>
public sealed record ReportData(string ReportId, string Title, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows, DateTimeOffset GeneratedAtUtc);

public interface IReportProvider
{
    string ReportId { get; }
    Task<ReportData> BuildAsync(CustomerId customerId, CancellationToken cancellationToken);
}

public interface IReportExporter
{
    ReportFormat Format { get; }
    Task ExportAsync(ReportData report, Stream destination, CancellationToken cancellationToken);
}

public interface IReportService
{
    Task<ReportData> BuildAsync(string reportId, CustomerId customerId, CancellationToken cancellationToken);
}
