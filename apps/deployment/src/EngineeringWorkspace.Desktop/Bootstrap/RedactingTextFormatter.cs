using System.Globalization;
using EngineeringWorkspace.Application.Security;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace EngineeringWorkspace.Desktop.Bootstrap;

/// <summary>
/// Serilog formatter that renders each event (including exceptions) and passes the text through the central
/// redaction service before it is written, so passwords, tokens and secrets never reach a log file.
/// </summary>
public sealed class RedactingTextFormatter(IRedactionService redaction) : ITextFormatter
{
    private readonly MessageTemplateTextFormatter _inner = new(
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}", CultureInfo.InvariantCulture);

    public void Format(LogEvent logEvent, TextWriter output)
    {
        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        _inner.Format(logEvent, buffer);
        output.Write(redaction.RedactText(buffer.ToString()));
    }
}
