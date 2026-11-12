using EngineeringWorkspace.Application.Export;

namespace EngineeringWorkspace.Desktop.Services;

/// <summary>Shared "Export to .csv / .html / .json / .txt" behaviour for every page that exports.</summary>
public sealed class ExportCoordinator(IExportService exporter, IDialogService dialogs)
{
    public static IReadOnlyList<ExportFormat> Formats { get; } = [ExportFormat.Csv, ExportFormat.Html, ExportFormat.Json, ExportFormat.Txt];

    /// <summary>Asks for a file name and writes the document. Returns a status message, or null when the user cancelled.</summary>
    public async Task<string?> ExportAsync(ExportDocument document, ExportFormat format, CancellationToken cancellationToken = default)
    {
        var extension = ExportService.Extension(format);
        var filter = format switch
        {
            ExportFormat.Csv => "CSV files (*.csv)|*.csv",
            ExportFormat.Html => "HTML files (*.html)|*.html",
            ExportFormat.Json => "JSON files (*.json)|*.json",
            _ => "Text files (*.txt)|*.txt"
        };
        var path = dialogs.SaveFile(exporter.SuggestFileName(document, format), filter);
        if (path is null) return null;
        if (!path.EndsWith($".{extension}", StringComparison.OrdinalIgnoreCase)) path += $".{extension}";
        await exporter.WriteAsync(document, format, path, cancellationToken);
        return $"Exported to {path}.";
    }

    /// <summary>Parses a command parameter ("Csv", "Html", "Json", "Txt").</summary>
    public static ExportFormat Parse(object? parameter) =>
        parameter is ExportFormat format ? format : Enum.TryParse<ExportFormat>(parameter?.ToString(), true, out var parsed) ? parsed : ExportFormat.Csv;
}
