using System.Text.Json;
using ArchiTech.Api.Integrations;
using Microsoft.Identity.Client;

namespace ArchiTech.ReadOnlyWorker;

public sealed record DelegatedGraphToken(string TenantId, IReadOnlyCollection<string> Scopes, string AccessToken);

public interface ILiveConditionalAccessReader
{
    Task<DelegatedGraphToken> SignInAsync(Guid tenantId, Guid clientId, CancellationToken cancellationToken);
    Task<ConditionalAccessEvidence> ReadAsync(string accessToken, bool includeSecurityDefaults, bool includeNamedLocations, bool includeAuthenticationMethods, CancellationToken cancellationToken);
}

public sealed class MsalConditionalAccessReader : ILiveConditionalAccessReader
{
    public async Task<DelegatedGraphToken> SignInAsync(Guid tenantId, Guid clientId, CancellationToken cancellationToken)
    {
        var application = PublicClientApplicationBuilder.Create(clientId.ToString())
            .WithAuthority(AzureCloudInstance.AzurePublic, tenantId.ToString())
            .WithRedirectUri("http://localhost")
            .Build();
        var result = await application.AcquireTokenInteractive(["https://graph.microsoft.com/Policy.Read.All"])
            .WithUseEmbeddedWebView(false)
            .ExecuteAsync(cancellationToken);
        return new DelegatedGraphToken(result.TenantId, result.Scopes.ToArray(), result.AccessToken);
    }

    public async Task<ConditionalAccessEvidence> ReadAsync(string accessToken, bool includeSecurityDefaults, bool includeNamedLocations, bool includeAuthenticationMethods, CancellationToken cancellationToken)
    {
        using var reader = new ConditionalAccessEvidenceReader();
        return await reader.ReadAsync(accessToken, cancellationToken, includeSecurityDefaults, includeNamedLocations, includeAuthenticationMethods);
    }
}

public static class LiveConditionalAccessCommand
{
    public static async Task<int> RunAsync(string tenant, string client, TextReader input, TextWriter output,
        TextWriter error, ILiveConditionalAccessReader reader, CancellationToken cancellationToken,
        bool includeSecurityDefaults = false, bool includeNamedLocations = false, bool includeAuthenticationMethods = false)
    {
        if (!Guid.TryParse(tenant, out var tenantId) || tenantId == Guid.Empty ||
            !Guid.TryParse(client, out var clientId) || clientId == Guid.Empty)
        {
            await error.WriteLineAsync("Provide a non-empty customer tenant GUID and public client GUID.");
            return 2;
        }

        await error.WriteLineAsync(
            $"Customer tenant: {tenantId:D}\nPublic client: {clientId:D}\n" +
            "This requests delegated Microsoft Graph Policy.Read.All for read-only Conditional Access policy inventory and selected details. " +
            (includeSecurityDefaults ? "It also reads whether security defaults are enabled. " : "") +
            (includeNamedLocations ? "It also reads the count of named locations. " : "") +
            (includeAuthenticationMethods ? "It also reads authentication methods policy migration state. " : "") +
            "Microsoft sign-in may also request standard sign-in scopes. " +
            "If any consent prompt appears, CANCEL it; do not approve or request new consent. " +
            "Use only an authorized account and verified customer registration.\n" +
            "Type READ to open Microsoft sign-in, or anything else to cancel:");
        string? confirmation;
        try { confirmation = await input.ReadLineAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Cancelled; no sign-in or Graph request was started.");
            return 2;
        }
        if (!string.Equals(confirmation, "READ", StringComparison.Ordinal))
        {
            await error.WriteLineAsync("Cancelled; no sign-in or Graph request was started.");
            return 2;
        }

        try
        {
            var token = await reader.SignInAsync(tenantId, clientId, cancellationToken);
            if (!Guid.TryParse(token.TenantId, out var resultTenant) || resultTenant != tenantId ||
                string.IsNullOrWhiteSpace(token.AccessToken) ||
                !token.Scopes.Any(scope => scope.Equals("Policy.Read.All", StringComparison.OrdinalIgnoreCase) ||
                    scope.Equals("https://graph.microsoft.com/Policy.Read.All", StringComparison.OrdinalIgnoreCase)))
            {
                await error.WriteLineAsync("Sign-in did not return the requested tenant and delegated scope; no Graph request was made.");
                return 1;
            }

            var evidence = await reader.ReadAsync(token.AccessToken, includeSecurityDefaults, includeNamedLocations, includeAuthenticationMethods, cancellationToken);
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                tenantId = tenantId.ToString("D"),
                observedUtc = DateTimeOffset.UtcNow,
                evidence
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            return 0;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Cancelled; no complete evidence was returned.");
            return 1;
        }
        catch (MsalException)
        {
            await error.WriteLineAsync("Microsoft sign-in failed or was cancelled. No complete evidence was returned; verify the registration, existing grant, account and public-client redirect configuration.");
            return 1;
        }
        catch (GraphEvidenceHttpException exception)
        {
            await error.WriteLineAsync($"ARCHITECH-EVIDENCE-ERROR:{exception.Operation}:http:{(int)exception.StatusCode!.Value}");
            return 1;
        }
        catch (GraphEvidenceMissingMigrationStateException)
        {
            await error.WriteLineAsync("ARCHITECH-EVIDENCE-ERROR:authentication-methods:missing-migration-state");
            return 1;
        }
        catch (GraphEvidenceFormatException exception)
        {
            await error.WriteLineAsync($"ARCHITECH-EVIDENCE-ERROR:{exception.Operation}:invalid-response");
            return 1;
        }
        catch (HttpRequestException exception)
        {
            await error.WriteLineAsync($"Graph policy read failed{(exception.StatusCode is null ? "" : $" (HTTP {(int)exception.StatusCode})")}; no complete evidence was returned.");
            return 1;
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException or IOException)
        {
            await error.WriteLineAsync("The Graph response could not be fully processed; no complete evidence was returned.");
            return 1;
        }
    }
}
