using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Credentials;
using EngineeringWorkspace.Domain.Tenants;
using EngineeringWorkspace.Infrastructure.Microsoft;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Infrastructure.Assessments;

/// <summary>
/// Read-only PowerShell collector (4.0 beta.2): Exchange Online, Security &amp; Compliance (Purview) and Teams settings that Microsoft
/// Graph does not expose. Runs PowerShell 7 with pinned module versions and app-only certificate authentication. The certificate is
/// handed to the script on standard input and never touches the disk or the command line. Only the fixed Get- cmdlets below run, and
/// the Read registration holds the Global Reader role, so the collector cannot change anything. In-depth scans only.
/// </summary>
public sealed partial class PowerShellTenantDataCollector(ISecretStore secrets, ILogger<PowerShellTenantDataCollector> logger)
{
    public const string ExchangeModule = "ExchangeOnlineManagement";
    public const string ExchangeModuleVersion = "3.10.1";
    public const string TeamsModule = "MicrosoftTeams";
    public const string TeamsModuleVersion = "8.0.0";

    /// <summary>Environment variable that points at pwsh.exe when it is not on PATH or in the default folder.</summary>
    public const string PwshVariable = "SECUREPLUS_PWSH";

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(12);

    public static IReadOnlyList<string> ExchangeCmdlets { get; } =
    [
        "Get-OrganizationConfig", "Get-AdminAuditLogConfig", "Get-HostedOutboundSpamFilterPolicy", "Get-RemoteDomain", "Get-ExternalInOutlook",
        "Get-TransportConfig", "Get-DkimSigningConfig", "Get-OwaMailboxPolicy", "Get-SharingPolicy", "Get-RoleAssignmentPolicy",
        "Get-HostedConnectionFilterPolicy", "Get-HostedContentFilterPolicy", "Get-MalwareFilterPolicy", "Get-AntiPhishPolicy", "Get-SafeLinksPolicy",
        "Get-SafeAttachmentPolicy", "Get-AtpPolicyForO365", "Get-EOPProtectionPolicyRule", "Get-TeamsProtectionPolicy"
    ];

    public static IReadOnlyList<string> ComplianceCmdlets { get; } =
        ["Get-DlpCompliancePolicy", "Get-LabelPolicy", "Get-AutoSensitivityLabelPolicy", "Get-RetentionCompliancePolicy"];

    public static IReadOnlyList<string> TeamsCmdlets { get; } =
    [
        "Get-CsTenantFederationConfiguration", "Get-CsTeamsClientConfiguration", "Get-CsTeamsMeetingPolicy", "Get-CsTeamsMessagingPolicy"
    ];

    public static IReadOnlyList<string> DatasetNames { get; } =
    [
        .. ExchangeCmdlets.Select(cmdlet => $"exo:{cmdlet}"), .. ComplianceCmdlets.Select(cmdlet => $"scc:{cmdlet}"), .. TeamsCmdlets.Select(cmdlet => $"teams:{cmdlet}")
    ];

    private static TenantDataset[] Unavailable(DatasetStatus status, string message) =>
        DatasetNames.Select(name => new TenantDataset(name, "PowerShell", status, [], message)).ToArray();

    /// <summary>Reads every PowerShell dataset; problems are reported per dataset, never thrown (Graph results are kept).</summary>
    public async Task<IReadOnlyList<TenantDataset>> CollectAsync(TenantConfiguration tenant, string? initialDomain, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId || tenant.ApplicationRegistration is not { } registration || registration.SecretReference is not { } reference)
            return Unavailable(DatasetStatus.NotAvailable, "The tenant has no app registration.");
        if (string.IsNullOrWhiteSpace(initialDomain))
            return Unavailable(DatasetStatus.NotAvailable, "The tenant's onmicrosoft.com domain could not be read from Microsoft Graph.");
        var stored = await secrets.GetSecretAsync(reference, cancellationToken);
        if (!CertificateCredential.IsCertificate(stored))
            return Unavailable(DatasetStatus.NotAvailable,
                "Exchange, Purview and Teams settings need certificate authentication. Use Update permissions on the tenant to switch the Read registration to a certificate.");
        var pwsh = FindPwsh();
        if (pwsh is null)
            return Unavailable(DatasetStatus.NotAvailable, "PowerShell 7 (pwsh) is not installed. Install it from https://aka.ms/powershell, then run the scan again.");

        var script = Path.Combine(Path.GetTempPath(), $"secureplus-collector-{Guid.NewGuid():N}.ps1");
        try
        {
            await using (var resource = typeof(PowerShellTenantDataCollector).Assembly.GetManifestResourceStream("EngineeringWorkspace.Assessments.ReadOnlyCollector.ps1")
                                        ?? throw new InvalidOperationException("The PowerShell collector script is missing from the build."))
            await using (var file = File.Create(script))
                await resource.CopyToAsync(file, cancellationToken);

            var input = JsonSerializer.Serialize(new
            {
                tenantId = tenantId.ToString(),
                appId = registration.ClientId.ToString(),
                organization = initialDomain,
                cloud = tenant.CloudEnvironment.ToString(),
                pkcs12 = CertificateCredential.Pkcs12Base64(stored!),
                exchange = ExchangeCmdlets,
                compliance = ComplianceCmdlets,
                teams = TeamsCmdlets,
                exchangeModuleVersion = ExchangeModuleVersion,
                teamsModuleVersion = TeamsModuleVersion
            });
            stored = null;
            progress?.Report("Reading Exchange Online, Purview and Teams settings with read-only PowerShell...");
            var lines = await RunAsync(pwsh, script, input, cancellationToken);
            var datasets = Parse(lines);
            var result = DatasetNames.Select(name => datasets.GetValueOrDefault(name) ??
                                                     new TenantDataset(name, "PowerShell", DatasetStatus.Failed, [], "The PowerShell collector returned no result for this dataset.")).ToArray();
            logger.LogInformation("PowerShell collector read {Collected} of {Total} datasets for tenant {TenantId}",
                result.Count(dataset => dataset.IsCollected), result.Length, tenantId);
            return result;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            logger.LogWarning("PowerShell collector failed for tenant {TenantId}: {Message}", tenantId, exception.Message);
            return Unavailable(DatasetStatus.Failed, $"The PowerShell collector failed: {exception.Message}");
        }
        finally
        {
            try
            {
                File.Delete(script);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Datasets from the script's JSON lines (other output is ignored).</summary>
    public static IReadOnlyDictionary<string, TenantDataset> Parse(IEnumerable<string> lines)
    {
        var datasets = new Dictionary<string, TenantDataset>(StringComparer.Ordinal);
        foreach (var line in lines.Where(line => line.TrimStart().StartsWith('{')))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("dataset", out var nameElement) || nameElement.GetString() is not { } name || !DatasetNames.Contains(name)) continue;
                var status = root.TryGetProperty("status", out var statusElement) && Enum.TryParse<DatasetStatus>(statusElement.GetString(), out var parsed) ? parsed : DatasetStatus.Failed;
                var items = root.TryGetProperty("items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array
                    ? itemsElement.EnumerateArray().Select(item => item.Clone()).ToArray()
                    : [];
                var message = root.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String ? messageElement.GetString() : null;
                var cmdlet = name[(name.IndexOf(':', StringComparison.Ordinal) + 1)..];
                datasets[name] = new TenantDataset(name, $"PowerShell {cmdlet}", status, items, message);
            }
            catch (JsonException)
            {
            }
        }

        return datasets;
    }

    private async Task<IReadOnlyList<string>> RunAsync(string pwsh, string script, string input, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(pwsh)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell 7 could not be started.");
        var output = new List<string>();
        var errors = new StringBuilder();
        var reading = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line) output.Add(line);
        }, cancellationToken);
        var readingErrors = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
                if (errors.Length < 4000) errors.AppendLine(line);
        }, cancellationToken);
        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(reading, readingErrors);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"The PowerShell collector did not finish within {Timeout.TotalMinutes:0} minutes.");
        }

        if (errors.Length > 0) logger.LogDebug("PowerShell collector standard error: {Errors}", Redact(errors.ToString()));
        return output;
    }

    private static string Redact(string text) => Base64Run().Replace(text, "[redacted]");

    [GeneratedRegex("[A-Za-z0-9+/=]{200,}")]
    private static partial Regex Base64Run();

    private static string? FindPwsh()
    {
        var configured = Environment.GetEnvironmentVariable(PwshVariable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var names = OperatingSystem.IsWindows() ? new[] { "pwsh.exe" } : ["pwsh"];
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        foreach (var name in names)
        {
            var candidate = Path.Combine(folder.Trim('"'), name);
            if (File.Exists(candidate)) return candidate;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var installed = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        return File.Exists(installed) ? installed : null;
    }

    /// <summary>The tenant's initial (onmicrosoft.com) domain from the Graph organization dataset.</summary>
    public static string? InitialDomain(TenantSnapshot snapshot) =>
        snapshot.Get(TenantDatasets.Organization).Items
            .SelectMany(item => TenantSettingRules.Select(item, "verifiedDomains[isInitial=true].name"))
            .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : null)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
}

/// <summary>Microsoft Graph plus, for in-depth scans, the read-only PowerShell collector.</summary>
public sealed class CompositeTenantDataCollector(GraphTenantDataCollector graph, PowerShellTenantDataCollector powerShell, TimeProvider timeProvider) : ITenantDataCollector
{
    public async Task<TenantSnapshot> CollectAsync(TenantConfiguration tenant, TenantScanOptions options, IProgress<(double Progress, string Activity)>? progress, CancellationToken cancellationToken)
    {
        var inDepth = ScanTypes.Normalize(options.ScanType) == ScanTypes.InDepth;
        var graphProgress = progress is null || !inDepth ? progress : new Progress<(double Progress, string Activity)>(value => progress.Report((value.Progress * 0.8, value.Activity)));
        var snapshot = await graph.CollectAsync(tenant, options, graphProgress, cancellationToken);
        if (!inDepth) return snapshot;

        progress?.Report((80, "Reading Exchange Online, Purview and Teams settings (read-only PowerShell)..."));
        var datasets = await powerShell.CollectAsync(tenant, PowerShellTenantDataCollector.InitialDomain(snapshot),
            progress is null ? null : new Progress<string>(activity => progress.Report((85, activity))), cancellationToken);
        var merged = new Dictionary<string, TenantDataset>(snapshot.Datasets, StringComparer.Ordinal);
        foreach (var dataset in datasets) merged[dataset.Name] = dataset;
        progress?.Report((95, "Tenant data collected."));
        return new TenantSnapshot(snapshot.TenantId, timeProvider.GetUtcNow(), merged);
    }
}
