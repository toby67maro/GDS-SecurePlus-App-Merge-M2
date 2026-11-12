using System.Globalization;
using System.Text;
using EngineeringWorkspace.Application.Security;

namespace EngineeringWorkspace.Desktop.Bootstrap;

/// <summary>Writes a diagnostic file when startup fails before logging is ready. Content is redacted.</summary>
public static class BootstrapDiagnostics
{
    public static string? Write(string dataRoot, string title, IEnumerable<string> lines, Exception? exception = null)
    {
        var redaction = new RedactionService();
        var builder = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Secure Plus Agent startup diagnostic - {DateTimeOffset.UtcNow:O}")
            .AppendLine(title)
            .AppendLine();
        foreach (var line in lines) builder.AppendLine(redaction.RedactText(line));
        if (exception is not null) builder.AppendLine().AppendLine(redaction.RedactText(exception.ToString()));

        foreach (var directory in new[] { Path.Combine(dataRoot, "data", "Logs"), Path.GetTempPath() })
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"bootstrap-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.log");
                File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
                return path;
            }
            catch (Exception writeFailure) when (writeFailure is IOException or UnauthorizedAccessException)
            {
                // Try the next location; startup error reporting must not fail because one directory is unwritable.
            }
        }

        return null;
    }
}
