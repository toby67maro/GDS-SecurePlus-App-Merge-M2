using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ArchiTech.Api.Data;
using ArchiTech.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace ArchiTech.Api.Integrations;

public sealed record LiveEvidenceRequest(string? TenantId, string? ClientId, bool Confirmed, bool IncludeSecurityDefaults = false, bool IncludeNamedLocations = false, bool IncludeAuthenticationMethods = false);
public sealed record LiveEvidenceResult(Guid TenantId, DateTimeOffset ObservedUtc, ConditionalAccessEvidence Evidence);

public interface ILocalEvidenceWorker
{
    bool IsAvailable(HttpContext context);
    Task<LiveEvidenceResult> ReadAsync(Guid tenantId, Guid clientId, bool includeSecurityDefaults, bool includeNamedLocations, bool includeAuthenticationMethods, CancellationToken cancellationToken);
}

public sealed class LocalEvidenceWorker(IWebHostEnvironment environment, AuthenticationMode authenticationMode) : ILocalEvidenceWorker
{
    private readonly SemaphoreSlim _singleWorker = new(1, 1);
    public bool IsAvailable(HttpContext context)
    {
        if (!environment.IsDevelopment() || !authenticationMode.IsLocalDevelopment ||
            context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
            !context.Request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
            context.Request.Host.Host != "127.0.0.1" && context.Request.Host.Host != "[::1]")
            return false;
        if (!Uri.TryCreate(context.Request.Headers.Origin.ToString(), UriKind.Absolute, out var origin) ||
            origin.Scheme != "http" || origin.UserInfo.Length != 0 ||
            !(origin.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || origin.Host == "127.0.0.1" || origin.Host == "[::1]"))
            return false;
        return origin.Port == context.Request.Host.Port || origin.Port == 5173;
    }

    public async Task<LiveEvidenceResult> ReadAsync(Guid tenantId, Guid clientId, bool includeSecurityDefaults, bool includeNamedLocations, bool includeAuthenticationMethods, CancellationToken cancellationToken)
    {
        if (!await _singleWorker.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Another local evidence read is already running.");
        try
        {
            var worker = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "ArchiTech.ReadOnlyWorker",
                "bin", "Release", "net10.0", "ArchiTech.ReadOnlyWorker.exe"));
            if (!File.Exists(worker))
                throw new InvalidOperationException("The read-only worker is not built. Build the Release worker as described in the README.");
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = worker,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("read-ca-policies");
            process.StartInfo.ArgumentList.Add(tenantId.ToString("D"));
            process.StartInfo.ArgumentList.Add(clientId.ToString("D"));
            if (includeSecurityDefaults) process.StartInfo.ArgumentList.Add("--include-security-defaults");
            if (includeNamedLocations) process.StartInfo.ArgumentList.Add("--include-named-locations");
            if (includeAuthenticationMethods) process.StartInfo.ArgumentList.Add("--include-authentication-methods");
            try
            {
                if (!process.Start()) throw new InvalidOperationException("The read-only worker could not start.");
            }
            catch (Win32Exception)
            {
                throw new InvalidOperationException("The read-only worker could not start. Check the worker build and host security policy.");
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            try
            {
                var stdout = ReadBoundedAsync(process.StandardOutput, 2 * 1024 * 1024, timeout.Token);
                var stderr = ReadBoundedAsync(process.StandardError, 32768, timeout.Token);
                await process.StandardInput.WriteLineAsync("READ".AsMemory(), timeout.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                var output = await stdout;
                var errorOutput = await stderr;
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(DescribeWorkerFailure(errorOutput));
                var result = JsonSerializer.Deserialize<LiveEvidenceResult>(output, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (result is null || result.TenantId != tenantId || result.Evidence is null ||
                    result.Evidence.AssessmentStatus != "NotAssessed" ||
                    (includeSecurityDefaults && result.Evidence.SecurityDefaultsEnabled is null) ||
                    (includeNamedLocations && result.Evidence.NamedLocationCount is null or < 0) ||
                    (includeAuthenticationMethods && string.IsNullOrWhiteSpace(result.Evidence.AuthenticationMethodsMigrationState)))
                    throw new InvalidOperationException("The worker returned invalid evidence.");
                return result;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        finally { _singleWorker.Release(); }
    }

    internal static string DescribeWorkerFailure(string stderr)
    {
        const string prefix = "ARCHITECH-EVIDENCE-ERROR:";
        var matches = stderr.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 1)
        {
            var parts = matches[0][prefix.Length..].Split(':');
            var operation = parts.Length > 0 ? parts[0] switch
            {
                "policies" => "Conditional Access policies",
                "security-defaults" => "security defaults",
                "named-locations" => "named locations",
                "authentication-methods" => "authentication methods policy",
                _ => null
            } : null;
            if (operation is not null && parts.Length == 3 && parts[1] == "http" &&
                parts[2].Length == 3 && int.TryParse(parts[2], out var status) && status is >= 400 and <= 599)
                return $"Graph {operation} read failed (HTTP {status}). No evidence was returned.";
            if (operation is not null && parts.Length == 2 && parts[1] == "invalid-response")
                return $"Graph {operation} response was incomplete or invalid. No evidence was returned.";
            if (operation == "authentication methods policy" && parts.Length == 2 && parts[1] == "missing-migration-state")
                return "Graph authentication methods policy did not include a migration state. No evidence was returned.";
        }
        return "The read-only worker failed or sign-in was cancelled. Check the existing consent, account role, redirect URI and Graph access.";
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        var result = new System.Text.StringBuilder();
        var exceeded = false;
        int count;
        while ((count = await reader.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (result.Length + count > limit)
                exceeded = true;
            else if (!exceeded)
                result.Append(buffer, 0, count);
        }
        if (exceeded) throw new InvalidOperationException("Worker output exceeded the permitted size.");
        return result.ToString();
    }
}

public static class LocalEvidenceEndpoints
{
    public static void MapLocalEvidenceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/deployment/profiles/{customerId:guid}/conditional-access-evidence/live",
            async (Guid customerId, LiveEvidenceRequest request, HttpContext context, AppDbContext db,
                ILocalEvidenceWorker worker, CancellationToken cancellationToken) =>
            {
                if (!worker.IsAvailable(context))
                    return Results.Problem(statusCode: 403, title: "Live evidence is available only in the local development workspace.");
                context.Response.Headers.CacheControl = "no-store";
                if (!request.Confirmed || !Guid.TryParse(request.TenantId, out var tenant) || tenant == Guid.Empty ||
                    !Guid.TryParse(request.ClientId, out var client) || client == Guid.Empty)
                    return Results.BadRequest(new { message = "Confirm the customer tenant and public client before starting." });
                var profile = await db.DeploymentProfiles.AsNoTracking()
                    .SingleOrDefaultAsync(p => p.CustomerId == customerId, cancellationToken);
                if (profile is null) return Results.NotFound(new { message = "Deployment profile not found." });
                if (!Guid.TryParse(profile.TenantId, out var savedTenant) || savedTenant != tenant ||
                    !Guid.TryParse(profile.EntraApplicationClientId, out var savedClient) || savedClient != client)
                    return Results.Conflict(new { message = "The saved tenant or client changed. Reload and verify the customer profile." });
                try
                {
                    var evidence = await worker.ReadAsync(tenant, client, request.IncludeSecurityDefaults, request.IncludeNamedLocations, request.IncludeAuthenticationMethods, cancellationToken);
                    if (request.IncludeSecurityDefaults && evidence.Evidence.SecurityDefaultsEnabled is null)
                        return Results.Problem(statusCode: 502, title: "The worker did not return the requested security defaults evidence.");
                    if (request.IncludeNamedLocations && evidence.Evidence.NamedLocationCount is null or < 0)
                        return Results.Problem(statusCode: 502, title: "The worker did not return the requested named locations evidence.");
                    if (request.IncludeAuthenticationMethods && string.IsNullOrWhiteSpace(evidence.Evidence.AuthenticationMethodsMigrationState))
                        return Results.Problem(statusCode: 502, title: "The worker did not return the requested authentication methods evidence.");
                    var current = await db.DeploymentProfiles.AsNoTracking()
                        .SingleOrDefaultAsync(p => p.CustomerId == customerId, cancellationToken);
                    if (current is null || !Guid.TryParse(current.TenantId, out var currentTenant) || currentTenant != tenant ||
                        !Guid.TryParse(current.EntraApplicationClientId, out var currentClient) || currentClient != client)
                        return Results.Conflict(new { message = "The customer registration changed during the read; evidence was discarded." });
                    return Results.Ok(new
                    {
                        evidence.TenantId,
                        evidence.ObservedUtc,
                        evidence.Evidence,
                        reviewCandidates = AssessmentEvidenceCandidates.FromConditionalAccess(evidence)
                    });
                }
                catch (InvalidOperationException exception) { return Results.Problem(statusCode: 503, title: exception.Message); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { return Results.Problem(statusCode: 504, title: "The read-only worker timed out."); }
                catch (JsonException)
                { return Results.Problem(statusCode: 502, title: "The read-only worker returned malformed evidence."); }
                catch (IOException)
                { return Results.Problem(statusCode: 502, title: "Communication with the read-only worker failed."); }
            }).RequireAuthorization("DeploymentAuthor");
    }
}
