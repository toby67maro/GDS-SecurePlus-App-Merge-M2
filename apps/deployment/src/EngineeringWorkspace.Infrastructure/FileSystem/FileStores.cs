using System.Globalization;
using System.Text.Json;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Application.State;
using EngineeringWorkspace.Application.Workspaces;
using EngineeringWorkspace.Configuration.Ini;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Customers;
using EngineeringWorkspace.Domain.Security;
using EngineeringWorkspace.Domain.Tenants;
using EngineeringWorkspace.Domain.Workspaces;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Infrastructure.FileSystem;

/// <summary>Workspaces as data/Workspaces/{id}/Workspace.ini (also the Workspace.ini configuration layer).</summary>
public sealed class IniWorkspaceStore(PathSettings paths) : IWorkspaceStore
{
    public string GetDirectory(WorkspaceId id) => Path.Combine(paths.WorkspaceDirectory, id.Value);

    public Task<bool> ExistsAsync(WorkspaceId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(FilePath(id)));

    public async Task<IReadOnlyList<Workspace>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(paths.WorkspaceDirectory)) return [];
        var workspaces = new List<Workspace>();
        foreach (var directory in Directory.EnumerateDirectories(paths.WorkspaceDirectory).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!WorkspaceId.TryCreate(Path.GetFileName(directory), out var id)) continue;
            if (await LoadAsync(id, cancellationToken) is { } workspace) workspaces.Add(workspace);
        }

        return workspaces.OrderBy(workspace => workspace.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<Workspace?> LoadAsync(WorkspaceId id, CancellationToken cancellationToken = default)
    {
        var path = FilePath(id);
        if (!File.Exists(path)) return null;
        var document = IniDocument.Load(await File.ReadAllBytesAsync(path, cancellationToken));
        if (document.Errors.Count > 0) throw new InvalidDataException($"Workspace.ini of '{id}' is invalid: {document.Errors[0].Message}");
        var values = document.Entries.ToDictionary(entry => $"{entry.Section}:{entry.Key}", entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        string? Get(string key) => values.TryGetValue($"Workspace:{key}", out var value) && value.Length > 0 ? value : null;
        return new Workspace(
            id,
            Get("Name") ?? id.Value,
            GetDirectory(id),
            CustomerId.TryCreate(Get("CurrentCustomerId"), out var customer) ? customer : null,
            Guid.TryParse(Get("TenantId"), out var tenant) ? tenant : null,
            Get("Environment") ?? "Development",
            Get("DatabaseReference"),
            Get("GoldenTenantReference"),
            document.Entries.Where(entry => string.Equals(entry.Section, "Integrations", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase),
            ParseDate(Get("CreatedAtUtc")),
            ParseDate(Get("ModifiedAtUtc")));
    }

    public async Task SaveAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        var path = FilePath(workspace.Id);
        var document = File.Exists(path) ? IniDocument.Load(await File.ReadAllBytesAsync(path, cancellationToken)) : IniDocument.Parse(
            "; Workspace file. Also a configuration layer: other sections here override application settings for this workspace.\r\n");
        document.Set("Workspace", "Id", workspace.Id.Value);
        document.Set("Workspace", "Name", workspace.Name);
        document.Set("Workspace", "CurrentCustomerId", workspace.CurrentCustomerId?.Value ?? string.Empty);
        document.Set("Workspace", "TenantId", workspace.TenantId?.ToString() ?? string.Empty);
        document.Set("Workspace", "Environment", workspace.Environment);
        document.Set("Workspace", "DatabaseReference", workspace.DatabaseReference ?? string.Empty);
        document.Set("Workspace", "GoldenTenantReference", workspace.GoldenTenantReference ?? string.Empty);
        document.Set("Workspace", "CreatedAtUtc", workspace.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        document.Set("Workspace", "ModifiedAtUtc", workspace.ModifiedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        foreach (var (key, value) in workspace.IntegrationSettings.OrderBy(pair => pair.Key, StringComparer.Ordinal)) document.Set("Integrations", key, value);
        await AtomicFile.WriteAllTextAsync(path, document.ToText(), cancellationToken);
    }

    private string FilePath(WorkspaceId id) => Path.Combine(GetDirectory(id), "Workspace.ini");

    private static DateTimeOffset ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date.ToUniversalTime() : DateTimeOffset.UnixEpoch;
}

/// <summary>
/// Customer profiles as data/Customers/{id}/Customer.ini (also the Customer.ini configuration layer). Holds
/// non-secret identifiers and secret references only.
/// </summary>
public sealed class IniCustomerProfileStore(PathSettings paths, ILogger<IniCustomerProfileStore> logger) : ICustomerProfileStore
{
    public async Task<IReadOnlyList<CustomerProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(paths.CustomerDirectory)) return [];
        var profiles = new List<CustomerProfile>();
        foreach (var directory in Directory.EnumerateDirectories(paths.CustomerDirectory).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!CustomerId.TryCreate(Path.GetFileName(directory), out var id)) continue;
            try
            {
                if (await LoadAsync(id, cancellationToken) is { } profile) profiles.Add(profile);
            }
            catch (InvalidDataException exception)
            {
                logger.LogWarning("Customer profile {CustomerId} skipped: {Reason}", id.Value, exception.Message);
            }
        }

        return profiles.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<CustomerProfile?> LoadAsync(CustomerId id, CancellationToken cancellationToken = default)
    {
        var path = FilePath(id);
        if (!File.Exists(path)) return null;
        var document = IniDocument.Load(await File.ReadAllBytesAsync(path, cancellationToken));
        if (document.Errors.Count > 0) throw new InvalidDataException($"Customer.ini of '{id}' is invalid: {document.Errors[0].Message}");
        var sections = document.Entries.GroupBy(entry => entry.Section, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var customer = sections.GetValueOrDefault("Customer") ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? Get(IReadOnlyDictionary<string, string> section, string key) => section.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
        static IReadOnlyList<string> List(string? value) => value is null ? [] : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var tenants = sections.Where(pair => pair.Key.StartsWith("Tenant:", StringComparison.OrdinalIgnoreCase)).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair =>
        {
            var section = pair.Value;
            var key = pair.Key["Tenant:".Length..];
            var clientId = Guid.TryParse(Get(section, "ClientId"), out var client) ? client : (Guid?)null;
            var registration = clientId is null ? null : new ApplicationRegistration(
                clientId.Value,
                Guid.TryParse(Get(section, "ServicePrincipalId"), out var servicePrincipal) ? servicePrincipal : null,
                Enum.TryParse<TenantAuthenticationType>(Get(section, "AuthenticationType"), true, out var authentication) ? authentication : TenantAuthenticationType.ClientSecret,
                SecretReference.TryParse(Get(section, "ClientSecretReference"), out var reference) ? reference : null,
                Get(section, "CertificateReference"),
                DateTimeOffset.TryParse(Get(section, "CredentialExpiration"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expires) ? expires.ToUniversalTime() : null,
                List(Get(section, "RequiredCapabilities")).Select(value => Enum.TryParse<TenantCapability>(value, true, out var capability) ? capability : (TenantCapability?)null)
                    .OfType<TenantCapability>().ToArray());
            return new TenantConfiguration(
                key,
                Get(section, "DisplayName") ?? key,
                Get(section, "TenantName"),
                Guid.TryParse(Get(section, "TenantId"), out var tenantId) ? tenantId : null,
                Get(section, "PrimaryDomain"),
                Enum.TryParse<CloudEnvironment>(Get(section, "CloudEnvironment"), true, out var cloud) ? cloud : CloudEnvironment.Commercial,
                registration);
        }).ToArray();

        var contacts = sections.Where(pair => pair.Key.StartsWith("Contact:", StringComparison.OrdinalIgnoreCase)).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new CustomerContact(Get(pair.Value, "Name") ?? pair.Key, Get(pair.Value, "Email"), Get(pair.Value, "Title"), Get(pair.Value, "Role"),
                bool.TryParse(Get(pair.Value, "IsDecisionOwner"), out var owner) && owner)).ToArray();

        return new CustomerProfile(id, Get(customer, "Name") ?? id.Value, Get(customer, "Environment"), tenants, List(Get(customer, "AdditionalDomains")), contacts,
            List(Get(customer, "Tags")), Get(customer, "Notes"),
            sections.GetValueOrDefault("Integrations") ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            sections.GetValueOrDefault("WorkspaceSettings") ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    public async Task SaveAsync(CustomerProfile profile, CancellationToken cancellationToken = default)
    {
        var path = FilePath(profile.Id);
        var document = File.Exists(path) ? IniDocument.Load(await File.ReadAllBytesAsync(path, cancellationToken)) : IniDocument.Parse(
            "; Customer profile: non-secret identifiers and secret references only. Never put secrets in this file.\r\n" +
            "; Also a configuration layer: other sections here override application settings for this customer.\r\n");
        if (document.Errors.Count > 0) throw new InvalidDataException($"Customer.ini of '{profile.Id}' is invalid and was not modified: {document.Errors[0].Message}");
        document.Set("Customer", "Id", profile.Id.Value);
        document.Set("Customer", "Name", profile.Name);
        document.Set("Customer", "Environment", profile.Environment ?? string.Empty);
        document.Set("Customer", "AdditionalDomains", string.Join(';', profile.AdditionalDomains));
        document.Set("Customer", "Tags", string.Join(';', profile.Tags));
        document.Set("Customer", "Notes", (profile.Notes ?? string.Empty).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal));
        foreach (var tenant in profile.Tenants)
        {
            var section = $"Tenant:{tenant.TenantKey}";
            var registration = tenant.ApplicationRegistration;
            document.Set(section, "DisplayName", tenant.DisplayName);
            document.Set(section, "TenantName", tenant.TenantName ?? string.Empty);
            document.Set(section, "TenantId", tenant.TenantId?.ToString() ?? string.Empty);
            document.Set(section, "PrimaryDomain", tenant.PrimaryDomain ?? string.Empty);
            document.Set(section, "CloudEnvironment", tenant.CloudEnvironment.ToString());
            document.Set(section, "ClientId", registration?.ClientId.ToString() ?? string.Empty);
            document.Set(section, "ServicePrincipalId", registration?.ServicePrincipalId?.ToString() ?? string.Empty);
            document.Set(section, "AuthenticationType", registration?.AuthenticationType.ToString() ?? TenantAuthenticationType.ClientSecret.ToString());
            document.Set(section, "ClientSecretReference", registration?.SecretReference?.ToString() ?? string.Empty);
            document.Set(section, "CertificateReference", registration?.CertificateReference ?? string.Empty);
            document.Set(section, "CredentialExpiration", registration?.CredentialExpirationUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
            document.Set(section, "RequiredCapabilities", string.Join(';', registration?.RequiredCapabilities ?? [TenantCapability.TenantDiscovery]));
        }

        // Integration and workspace settings were read from these sections but never written, so edits made without a database were lost.
        WriteSection(document, "Integrations", profile.IntegrationSettings);
        WriteSection(document, "WorkspaceSettings", profile.WorkspaceSettings);
        await AtomicFile.WriteAllTextAsync(path, document.ToText(), cancellationToken);
    }

    private static void WriteSection(IniDocument document, string section, IReadOnlyDictionary<string, string> values)
    {
        foreach (var key in document.Entries.Where(entry => string.Equals(entry.Section, section, StringComparison.OrdinalIgnoreCase) && !values.ContainsKey(entry.Key))
                     .Select(entry => entry.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
            document.Set(section, key, null);
        foreach (var (key, value) in values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            document.Set(section, key, value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal));
    }

    private string FilePath(CustomerId id) => Path.Combine(paths.CustomerDirectory, id.Value, "Customer.ini");
}

/// <summary>Shell state as JSON in the per-user state directory.</summary>
public sealed class JsonApplicationStateStore(string stateDirectory, ILogger<JsonApplicationStateStore> logger) : IApplicationStateStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string FilePath => Path.Combine(stateDirectory, "application-state.json");

    public async Task<ApplicationState> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath)) return ApplicationState.Empty;
        try
        {
            await using var stream = File.OpenRead(FilePath);
            return await JsonSerializer.DeserializeAsync<ApplicationState>(stream, cancellationToken: cancellationToken) ?? ApplicationState.Empty;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Application state {Path} could not be read; starting with empty state", FilePath);
            return ApplicationState.Empty;
        }
    }

    public async Task SaveAsync(ApplicationState state, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await AtomicFile.WriteAllBytesAsync(FilePath, JsonSerializer.SerializeToUtf8Bytes(state, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Application state {Path} could not be saved", FilePath);
        }
        finally
        {
            _gate.Release();
        }
    }
}
