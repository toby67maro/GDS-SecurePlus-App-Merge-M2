using System.Text;
using ArchiTech.Api.Integrations;
using ClosedXML.Excel;

namespace ArchiTech.Tests;

public sealed class OrgChartImportTests
{
    [Fact]
    public void Csv_reads_department_column_with_quotes_and_deduplicates()
    {
        var bytes = Encoding.UTF8.GetBytes("Person,Department\r\nOne,\"Sales, East\"\r\nTwo,Finance\r\nThree,finance\r\n");
        Assert.Equal(["Sales, East", "Finance"], OrgChartImport.Parse("chart.csv", bytes));
    }

    [Fact]
    public void Excel_reads_one_visible_department_sheet()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Organization");
        sheet.Cell(1, 1).Value = "Person";
        sheet.Cell(1, 2).Value = "Department";
        sheet.Cell(2, 2).Value = "Operations";
        sheet.Cell(3, 2).Value = "Finance";
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        Assert.Equal(["Operations", "Finance"], OrgChartImport.Parse("chart.xlsx", stream.ToArray()));
    }

    [Fact]
    public void Rejects_ambiguous_headers_unsupported_format_and_too_many_departments()
    {
        Assert.Throws<InvalidDataException>(() =>
            OrgChartImport.Parse("chart.csv", Encoding.UTF8.GetBytes("Department,Department\nA,B")));
        Assert.Throws<InvalidDataException>(() => OrgChartImport.Parse("chart.pdf", [1]));
        var rows = "Department\n" + string.Join('\n', Enumerable.Range(1, 21).Select(i => $"Team {i}"));
        Assert.Throws<InvalidDataException>(() => OrgChartImport.Parse("chart.csv", Encoding.UTF8.GetBytes(rows)));
    }

    [Fact]
    public void Rejects_excel_department_formulas()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Organization");
        sheet.Cell(1, 1).Value = "Department";
        sheet.Cell(2, 1).FormulaA1 = "\"Finance\"";
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        Assert.Throws<InvalidDataException>(() => OrgChartImport.Parse("chart.xlsx", stream.ToArray()));
    }
}
