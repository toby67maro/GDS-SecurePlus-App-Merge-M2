using System.Text.Json;
using ArchiTech.Api.Integrations;

namespace ArchiTech.ReadOnlyWorker;

public static class WorkerCommand
{
    private const int MaximumExportBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions OutputOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
        => await RunAsync(args, TextReader.Null, output, error, cancellationToken);

    public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, TextWriter error, CancellationToken cancellationToken,
        ILiveConditionalAccessReader? liveReader = null)
    {
        if (args.Length is >= 3 and <= 6 && args[0] == "read-ca-policies" &&
            args.Skip(3).All(arg => arg is "--include-security-defaults" or "--include-named-locations" or "--include-authentication-methods") &&
            args.Skip(3).Distinct(StringComparer.Ordinal).Count() == args.Length - 3)
            return await LiveConditionalAccessCommand.RunAsync(args[1], args[2], input, output, error,
                liveReader ?? new MsalConditionalAccessReader(), cancellationToken,
                includeSecurityDefaults: args.Contains("--include-security-defaults", StringComparer.Ordinal),
                includeNamedLocations: args.Contains("--include-named-locations", StringComparer.Ordinal),
                includeAuthenticationMethods: args.Contains("--include-authentication-methods", StringComparer.Ordinal));

        if (args.Length != 2 || args[0] != "preview-ca-export" || string.IsNullOrWhiteSpace(args[1]))
        {
            await error.WriteLineAsync("Usage: ArchiTech.ReadOnlyWorker preview-ca-export <complete-graph-export.json>");
            await error.WriteLineAsync("       ArchiTech.ReadOnlyWorker read-ca-policies <tenant-guid> <public-client-guid>");
            await error.WriteLineAsync("       Add --include-security-defaults to also read security defaults.");
            await error.WriteLineAsync("       Add --include-named-locations to also count named locations.");
            await error.WriteLineAsync("       Add --include-authentication-methods to read policy migration state.");
            return 2;
        }

        try
        {
            await using var file = new FileStream(args[1], FileMode.Open, FileAccess.Read, FileShare.Read,
                8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await file.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + count > MaximumExportBytes)
                    throw new InvalidOperationException("The export exceeds the 2 MiB limit.");
                buffer.Write(chunk, 0, count);
            }
            buffer.Position = 0;
            using var document = await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
            var preview = ConditionalAccessEvidenceReader.PreviewExport(document.RootElement);
            await output.WriteLineAsync(JsonSerializer.Serialize(preview, OutputOptions));
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            await error.WriteLineAsync(exception switch
            {
                InvalidOperationException => exception.Message,
                JsonException => "The export is not valid JSON.",
                _ => "The export could not be read. Check the path and read permissions."
            });
            return 1;
        }
    }
}
