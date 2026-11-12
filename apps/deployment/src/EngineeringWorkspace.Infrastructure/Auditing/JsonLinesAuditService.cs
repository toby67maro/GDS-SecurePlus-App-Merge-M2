using System.Globalization;
using System.Text;
using System.Text.Json;
using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Domain.Auditing;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Infrastructure.Auditing;

/// <summary>
/// Appends audit events as JSON lines to data/Audit/audit-yyyyMMdd.jsonl. Summaries and properties pass through
/// the central redaction service. Persistence failures are logged and never thrown.
/// </summary>
public sealed class JsonLinesAuditService(
    string auditDirectory,
    AuditContext context,
    IRedactionService redaction,
    TimeProvider timeProvider,
    ILogger<JsonLinesAuditService> logger) : IAuditService, IAuditReader
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    private static readonly UTF8Encoding Encoding = new(false);
    private const int AppendAttempts = 5;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task RecordAsync(string eventType, AuditOutcome outcome, string summary, IReadOnlyDictionary<string, string?>? properties = null, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var safeProperties = (properties ?? new Dictionary<string, string?>())
            .ToDictionary(pair => pair.Key, pair => redaction.RedactValue(pair.Key, pair.Value), StringComparer.Ordinal);
        var auditEvent = new AuditEvent(Guid.NewGuid(), eventType, now, context.Actor, context.WorkspaceId, context.CustomerId, context.SessionCorrelationId,
            outcome, redaction.RedactText(summary), safeProperties);
        var line = JsonSerializer.Serialize(auditEvent, Options) + "\n";
        var path = Path.Combine(auditDirectory, $"audit-{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.jsonl");

        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                Directory.CreateDirectory(auditDirectory);
                await AppendAsync(path, Encoding.GetBytes(line), cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Audit event {EventType} not written: the operation was cancelled", eventType);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Audit event {EventType} could not be written to {Path}", eventType, path);
        }
    }

    public async Task<IReadOnlyList<AuditEvent>> ReadRecentAsync(int maximumCount, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(auditDirectory)) return [];
        var events = new List<AuditEvent>();
        foreach (var file in Directory.EnumerateFiles(auditDirectory, "audit-*.jsonl").OrderByDescending(file => file, StringComparer.Ordinal))
        {
            string[] lines;
            try
            {
                lines = await ReadLinesAsync(file, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Audit file {Path} could not be read", file);
                continue;
            }

            for (var index = lines.Length - 1; index >= 0 && events.Count < maximumCount; index--)
            {
                if (lines[index].Length == 0) continue;
                try
                {
                    if (JsonSerializer.Deserialize<AuditEvent>(lines[index], Options) is { } auditEvent) events.Add(auditEvent);
                }
                catch (JsonException)
                {
                    logger.LogWarning("Skipped a malformed audit line in {Path}", file);
                }
            }

            if (events.Count >= maximumCount) break;
        }

        return events;
    }

    /// <summary>
    /// Appends while allowing others to read and write the file: the Auditing and Logs page, another Secure Plus window and
    /// the scheduled-assessment host share today's file. A sharing violation is retried briefly before it is reported.
    /// </summary>
    private static async Task AppendAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
                await stream.WriteAsync(bytes, cancellationToken);
                return;
            }
            catch (IOException) when (attempt < AppendAttempts)
            {
                await Task.Delay(50 * attempt, cancellationToken);
            }
        }
    }

    /// <summary>Reads every line without locking the file against the writer.</summary>
    private static async Task<string[]> ReadLinesAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        using var reader = new StreamReader(stream, Encoding);
        var text = await reader.ReadToEndAsync(cancellationToken);
        return text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
    }
}
