using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using Microsoft.VisualBasic.FileIO;

namespace ArchiTech.Api.Integrations;

public sealed record OrgChartPreviewRequest(string? FileName, string? ContentBase64);

public static class OrgChartImport
{
    public static string[] Parse(string fileName, byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Choose a non-empty CSV or Excel file no larger than 2 MB.");

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".csv" => ParseCsv(bytes),
            ".xlsx" => ParseWorkbook(bytes),
            _ => throw new InvalidDataException("Only CSV and XLSX department lists are supported for extraction.")
        };
    }

    private static string[] ParseCsv(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        using var parser = new TextFieldParser(reader) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
        parser.SetDelimiters(",");
        var headers = parser.ReadFields() ?? [];
        var index = FindDepartmentColumn(headers);
        var values = new List<string>();
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields() ?? [];
            if (values.Count >= 1000) throw new InvalidDataException("The file contains too many rows.");
            values.Add(index < fields.Length ? fields[index] : "");
        }
        return Normalize(values);
    }

    private static string[] ParseWorkbook(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            if (archive.Entries.Count > 500 || archive.Entries.Sum(entry => entry.Length) > 10 * 1024 * 1024)
                throw new InvalidDataException("The Excel file expands beyond the supported size.");
        }
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var sheets = workbook.Worksheets.Where(sheet => sheet.Visibility == XLWorksheetVisibility.Visible).ToArray();
        if (sheets.Length != 1) throw new InvalidDataException("Use an Excel file with exactly one visible sheet.");
        var sheet = sheets[0];
        var used = sheet.RangeUsed();
        if (used is null || used.LastRow().RowNumber() > 1001 || used.LastColumn().ColumnNumber() > 100)
            throw new InvalidDataException("The Excel file must have a header and at most 1,000 data rows and 100 columns.");
        var headers = Enumerable.Range(1, used.LastColumn().ColumnNumber())
            .Select(column => sheet.Cell(1, column).GetString()).ToArray();
        var index = FindDepartmentColumn(headers) + 1;
        var values = new List<string>();
        for (var row = 2; row <= used.LastRow().RowNumber(); row++)
        {
            var cell = sheet.Cell(row, index);
            if (cell.HasFormula) throw new InvalidDataException("Department cells must contain text, not formulas.");
            values.Add(cell.GetString());
        }
        return Normalize(values);
    }

    private static int FindDepartmentColumn(string[] headers)
    {
        var indexes = headers.Select((value, index) => (value, index))
            .Where(item => item.value.Trim().Equals("Department", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index).ToArray();
        if (indexes.Length != 1)
            throw new InvalidDataException("The first row must contain exactly one column named Department.");
        return indexes[0];
    }

    private static string[] Normalize(IEnumerable<string> values)
    {
        var result = values.Select(value => value.Trim()).Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (result.Length == 0 || result.Length > 20 || result.Any(value => value.Length > 80 || value.Any(char.IsControl)))
            throw new InvalidDataException("Provide 1 to 20 distinct departments, each 80 characters or fewer.");
        return result;
    }
}
