using System.Text.RegularExpressions;
using EngineeringWorkspace.Application.Configuration;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Configuration.Binding;
using EngineeringWorkspace.Configuration.Ini;
using EngineeringWorkspace.Configuration.Layering;
using EngineeringWorkspace.Configuration.Snapshot;
using EngineeringWorkspace.Configuration.Validation;
using EngineeringWorkspace.Domain.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Ini;

namespace EngineeringWorkspace.Configuration.Loading;

/// <summary>Inputs resolved by the composition root before configuration is loaded (startup steps 1-2).</summary>
public sealed record ConfigurationLoadRequest(
    string ApplicationDirectory,
    string DataRoot,
    string ConfigurationDirectory,
    IReadOnlyList<string> CommandLineArguments,
    IReadOnlyDictionary<string, string?> EnvironmentVariables,
    string? WorkspaceId,
    string? CustomerId,
    string ApplicationVersion,
    string? DefaultsDirectory = null,
    IReadOnlyDictionary<string, string?>? SharedSettings = null,
    IReadOnlyDictionary<string, string?>? ComputerSettings = null)
{
    /// <summary>True when settings come from the database (workspace and customer INI layers are then not read).</summary>
    public bool UsesDatabase => SharedSettings is not null;

    /// <summary>The user's file when it exists, otherwise the shipped default (Database.ini is always the user's).</summary>
    public string Resolve(string relativePath)
    {
        var user = Path.Combine(ConfigurationDirectory, relativePath);
        if (DefaultsDirectory is null || File.Exists(user) || string.Equals(relativePath, "Database.ini", StringComparison.OrdinalIgnoreCase)) return user;
        return Path.Combine(DefaultsDirectory, relativePath);
    }
}

/// <summary>Result of loading configuration. <see cref="Snapshot"/> is always produced; check its errors.</summary>
public sealed record ConfigurationLoadResult(ConfigurationSnapshot Snapshot)
{
    public bool HasErrors => Snapshot.HasErrors;

    public ConfigurationSnapshot GetValidSnapshotOrThrow() =>
        HasErrors ? throw new ConfigurationValidationException(Snapshot.ValidationErrors) : Snapshot;
}

/// <summary>
/// INI → providers → layers → typed settings → binding validation → cross-section validation → immutable
/// snapshot. All errors from all four stages are collected in one pass.
/// </summary>
public static partial class ConfigurationLoader
{
    /// <summary>Command-line switches that map to bootstrap keys.</summary>
    public static IReadOnlyDictionary<string, string> SwitchMappings { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["--environment"] = "Application:Environment",
        ["--workspace"] = "Bootstrap:Workspace",
        ["--customer"] = "Bootstrap:Customer",
        ["--config-dir"] = "Bootstrap:ConfigurationDirectory",
        ["--data-dir"] = "Bootstrap:DataDirectory",
    };

    public static ConfigurationLoadResult Load(ConfigurationLoadRequest request, IRedactionService redaction)
    {
        var errors = new List<ConfigurationValidationError>();
        var sources = new List<ConfigurationSourceDiagnostic>();
        var layers = new List<LayerData> { Defaults() };

        var commandLine = CommandLine(request.CommandLineArguments, errors);
        var environmentVariables = EnvironmentVariables(request.EnvironmentVariables);
        foreach (var layer in ConfigurationPrecedence.Layers.Where(layer => layer.Kind == ConfigurationSourceKind.IniFile && layer.Root == ConfigurationFileRoot.ConfigurationDirectory && !layer.PathTemplate!.Contains('{', StringComparison.Ordinal)))
            layers.AddRange(ReadFile(new ConfigurationFileDescriptor(layer, request.Resolve(layer.PathTemplate!)), errors, sources));

        // Environment: command line, then environment variables, then the application-level files.
        var preliminary = ConfigurationMerger.Merge(layers.Append(environmentVariables).Append(commandLine));
        var environment = preliminary.TryGetValue("Application:Environment", out var environmentValue) ? environmentValue.Value ?? "Development" : "Development";
        var environmentLayer = ConfigurationPrecedence.Layers.Single(layer => layer.Order == 100);
        if (EnvironmentNamePattern().IsMatch(environment))
            layers.AddRange(ReadFile(new ConfigurationFileDescriptor(environmentLayer, request.Resolve(environmentLayer.PathTemplate!.Replace("{Environment}", environment, StringComparison.Ordinal))), errors, sources));
        else
            errors.Add(new(ConfigurationValidationStage.FilesAndProviders, null, "Application", "Application", "Environment", environment, "InvalidEnvironmentName",
                "The environment name must be letters only; its file cannot be loaded.", "letters, for example Development", [], ConfigurationSeverity.Error));

        foreach (var (layer, values) in DatabaseLayers(request))
        {
            layers.Add(new LayerData(layer, layer.Name, values.Select(pair => Value(pair.Key, pair.Value)).ToArray()));
            sources.Add(new(layer.Order, layer.Name, "SecurePlus database (secureplus.Setting)", true, values.Count));
        }

        // Workspace and customer layers need the resolved data paths.
        preliminary = ConfigurationMerger.Merge(layers.Append(environmentVariables).Append(commandLine));
        var pathBinder = new SettingsBinder(preliminary, new List<ConfigurationValidationError>());
        var workspaceRoot = pathBinder.Path("Paths:WorkspaceDirectory", request.DataRoot);
        var customerRoot = pathBinder.Path("Paths:CustomerDirectory", request.DataRoot);
        var workspace = request.WorkspaceId ?? Value(preliminary, "Bootstrap:Workspace") ?? Value(preliminary, "Workspace:DefaultWorkspace");
        var customer = request.CustomerId ?? Value(preliminary, "Bootstrap:Customer");
        foreach (var layer in ConfigurationPrecedence.Layers.Where(layer => !request.UsesDatabase && layer.Root is ConfigurationFileRoot.WorkspaceDirectory or ConfigurationFileRoot.CustomerDirectory))
        {
            var (root, token, id) = layer.Root == ConfigurationFileRoot.WorkspaceDirectory ? (workspaceRoot, "{Workspace}", workspace) : (customerRoot, "{Customer}", customer);
            if (!StableIdentifier.IsValid(id)) continue;
            layers.AddRange(ReadFile(new ConfigurationFileDescriptor(layer, Path.Combine(root, layer.PathTemplate!.Replace(token, id, StringComparison.Ordinal))), errors, sources));
        }

        layers.Add(environmentVariables);
        layers.Add(commandLine);
        sources.Add(new(environmentVariables.Layer.Order, environmentVariables.Layer.Name, $"{ConfigurationPrecedence.EnvironmentVariablePrefix}*", environmentVariables.Values.Count > 0, environmentVariables.Values.Count));
        sources.Add(new(commandLine.Layer.Order, commandLine.Layer.Name, "command line", commandLine.Values.Count > 0, commandLine.Values.Count));
        sources.Insert(0, new(10, "Compiled defaults", "(compiled)", true, CompiledDefaults.Values.Count));

        var effective = ConfigurationMerger.Merge(layers);
        var binder = new SettingsBinder(effective, errors);
        var settings = SettingsBinding.Bind(binder, request.ApplicationDirectory, request.DataRoot, request.ConfigurationDirectory, request.ApplicationVersion);
        SettingsValidators.ValidateOptions(settings, binder);
        SettingsValidators.ValidateCrossSection(settings, binder, redaction);

        var issues = errors.Select(error => error.ToIssue(redaction)).ToArray();
        var snapshot = new ConfigurationSnapshot
        {
            Application = settings.Application,
            Paths = settings.Paths,
            Workspace = settings.Workspace,
            ImportExport = settings.ImportExport,
            Credentials = settings.Credentials,
            Branding = settings.Branding,
            Features = settings.Features,
            Navigation = settings.Navigation,
            Integrations = settings.Integrations,
            Database = settings.Database,
            Logging = settings.Logging,
            GoldenTenant = settings.GoldenTenant,
            Repositories = settings.Repositories,
            ValidationErrors = errors,
            LoadedAtUtc = DateTimeOffset.UtcNow,
            Sources = sources.OrderBy(source => source.Order).ToArray(),
            Entries = Entries(effective, errors, redaction),
            Issues = issues
        };
        return new ConfigurationLoadResult(snapshot);
    }

    private static IEnumerable<(ConfigurationLayer Layer, IReadOnlyDictionary<string, string?> Values)> DatabaseLayers(ConfigurationLoadRequest request)
    {
        var database = ConfigurationPrecedence.Layers.Where(layer => layer.Kind == ConfigurationSourceKind.Database).OrderBy(layer => layer.Order).ToArray();
        if (request.SharedSettings is { } shared) yield return (database[0], shared);
        if (request.ComputerSettings is { } computer) yield return (database[1], computer);
    }

    private static LayerData Defaults()
    {
        var layer = ConfigurationPrecedence.Layers.Single(layer => layer.Kind == ConfigurationSourceKind.CompiledDefaults);
        return new LayerData(layer, "(compiled defaults)", CompiledDefaults.Values.Select(pair => Value(pair.Key, pair.Value)).ToArray());
    }

    private static IEnumerable<LayerData> ReadFile(ConfigurationFileDescriptor file, ICollection<ConfigurationValidationError> errors, ICollection<ConfigurationSourceDiagnostic> sources)
    {
        if (!File.Exists(file.Path))
        {
            sources.Add(new(file.Layer.Order, file.Layer.Name, file.Path, false, 0));
            if (file.Layer.Required)
                errors.Add(new(ConfigurationValidationStage.FilesAndProviders, file.Path, file.Layer.Name, null, null, null, "RequiredFileMissing",
                    $"The required configuration file {file.Layer.Name} was not found.", "an INI file in the configuration directory", [], ConfigurationSeverity.Error));
            yield break;
        }

        byte[] content;
        try
        {
            content = File.ReadAllBytes(file.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            sources.Add(new(file.Layer.Order, file.Layer.Name, file.Path, true, 0));
            errors.Add(new(ConfigurationValidationStage.FilesAndProviders, file.Path, file.Layer.Name, null, null, null, "FileUnreadable",
                $"The file could not be read ({exception.GetType().Name}).", "a readable file", [], ConfigurationSeverity.Error));
            yield break;
        }

        var document = IniDocument.Load(content);
        foreach (var error in document.Errors)
            errors.Add(new(ConfigurationValidationStage.FilesAndProviders, file.Path, file.Layer.Name, error.Section, error.Key, null, error.ErrorCode, error.Message,
                "[Section] headers and Key=Value lines", [], ConfigurationSeverity.Error));
        if (document.Errors.Count > 0)
        {
            sources.Add(new(file.Layer.Order, file.Layer.Name, file.Path, true, 0));
            yield break;
        }

        // The Microsoft INI provider is the parser of record for values; IniDocument provides line-level diagnostics.
        IDictionary<string, string?> data;
        using (var stream = new MemoryStream(content))
            data = IniStreamConfigurationProvider.Read(stream);
        var values = document.Entries
            .Select(entry => new LayerValue($"{entry.Section}:{entry.Key}", entry.Section, entry.Key,
                data.TryGetValue($"{entry.Section}:{entry.Key}", out var value) ? value : entry.Value))
            .ToArray();
        sources.Add(new(file.Layer.Order, file.Layer.Name, file.Path, true, values.Length));
        yield return new LayerData(file.Layer, file.Path, values);
    }

    private static LayerData EnvironmentVariables(IReadOnlyDictionary<string, string?> variables)
    {
        var layer = ConfigurationPrecedence.Layers.Single(layer => layer.Kind == ConfigurationSourceKind.EnvironmentVariables);
        var root = new ConfigurationBuilder()
            .Add(new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource
            {
                InitialData = variables
                    .Where(pair => pair.Key.StartsWith(ConfigurationPrecedence.EnvironmentVariablePrefix, StringComparison.OrdinalIgnoreCase))
                    .Select(pair => new KeyValuePair<string, string?>(pair.Key[ConfigurationPrecedence.EnvironmentVariablePrefix.Length..].Replace("__", ":", StringComparison.Ordinal), pair.Value))
            })
            .Build();
        return new LayerData(layer, "environment variables", root.AsEnumerable().Where(pair => pair.Value is not null).Select(pair => Value(pair.Key, pair.Value)).ToArray());
    }

    private static LayerData CommandLine(IReadOnlyList<string> arguments, ICollection<ConfigurationValidationError> errors)
    {
        var layer = ConfigurationPrecedence.Layers.Single(layer => layer.Kind == ConfigurationSourceKind.CommandLine);
        try
        {
            var root = new ConfigurationBuilder().AddCommandLine(arguments.ToArray(), SwitchMappings.ToDictionary()).Build();
            return new LayerData(layer, "command line", root.AsEnumerable().Where(pair => pair.Value is not null).Select(pair => Value(pair.Key, pair.Value)).ToArray());
        }
        catch (FormatException exception)
        {
            errors.Add(new(ConfigurationValidationStage.FilesAndProviders, null, layer.Name, null, null, null, "InvalidCommandLine", exception.Message,
                "--Section:Key=value or --environment Test", SwitchMappings.Keys.ToArray(), ConfigurationSeverity.Error));
            return new LayerData(layer, "command line", []);
        }
    }

    private static LayerValue Value(string key, string? value)
    {
        var separator = key.LastIndexOf(':');
        return new LayerValue(key, separator < 0 ? string.Empty : key[..separator], separator < 0 ? key : key[(separator + 1)..], value);
    }

    private static string? Value(IReadOnlyDictionary<string, EffectiveValue> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value.Value) ? value.Value!.Trim() : null;

    private static IReadOnlyList<ConfigurationEntryDiagnostic> Entries(
        IReadOnlyDictionary<string, EffectiveValue> effective, IReadOnlyList<ConfigurationValidationError> errors, IRedactionService redaction)
    {
        var byKey = errors.Where(error => error.Section is not null && error.Key is not null)
            .GroupBy(error => $"{error.Section}:{error.Key}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(error => error.Severity).First(), StringComparer.OrdinalIgnoreCase);
        return effective.Values.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase).Select(value =>
        {
            var isSecret = redaction.IsSecretKey(value.Key);
            var redacted = redaction.RedactValue(value.Key, value.Value);
            byKey.TryGetValue(value.Key, out var error);
            var state = error?.Severity switch
            {
                ConfigurationSeverity.Error => ConfigurationEntryState.Error,
                ConfigurationSeverity.Warning => ConfigurationEntryState.Warning,
                _ => ConfigurationEntryState.Valid
            };
            // Effective values are shown through the redaction service: secret keys are masked and embedded secrets removed.
            return new ConfigurationEntryDiagnostic(value.Key, redacted, redacted, isSecret, value.Source, value.Layer.Name, value.Layer.Order,
                value.Section, value.OriginalKey, value.IsOverridden, value.PreviousSource, state, error is null ? null : redaction.RedactText(error.Message));
        }).ToArray();
    }

    [GeneratedRegex("^[A-Za-z]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentNamePattern();
}
