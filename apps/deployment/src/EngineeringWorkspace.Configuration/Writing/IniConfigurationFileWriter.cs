using System.Text;
using EngineeringWorkspace.Application.Common;
using EngineeringWorkspace.Application.Configuration;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Configuration.Ini;
using EngineeringWorkspace.Configuration.Layering;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Configuration.Writing;

/// <summary>
/// Writes non-secret settings into an application-level INI file of the writable configuration directory.
/// Comments and unrelated keys are kept; the write is atomic (temporary file, then replace). Secret-bearing
/// keys are refused.
/// </summary>
public sealed class IniConfigurationFileWriter(string configurationDirectory, IRedactionService redaction, ILogger<IniConfigurationFileWriter> logger)
    : IConfigurationFileWriter
{
    private static readonly HashSet<string> WritableFiles = ConfigurationPrecedence.Layers
        .Where(layer => layer.Root == ConfigurationFileRoot.ConfigurationDirectory && layer.PathTemplate is not null && !layer.PathTemplate.Contains('{', StringComparison.Ordinal))
        .Select(layer => layer.PathTemplate!)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task<OperationResult<string>> WriteSectionAsync(string fileName, string section, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default)
    {
        if (!WritableFiles.Contains(fileName))
            return OperationResult<string>.Failure("ConfigurationFileNotWritable", $"'{fileName}' is not an application configuration file.");
        var secretKey = values.Keys.FirstOrDefault(key => redaction.IsSecretKey(key));
        if (secretKey is not null)
            return OperationResult<string>.Failure("SecretNotAllowed", $"'{secretKey}' is secret-bearing and cannot be written to configuration.", null, "Store the secret in Credential Management and save its reference.");
        if (values.Values.Any(value => value is not null && (value.Contains('\n') || value.Contains('\r'))))
            return OperationResult<string>.Failure("InvalidValue", "Configuration values cannot contain line breaks.");

        var path = Path.Combine(configurationDirectory, fileName);
        try
        {
            Directory.CreateDirectory(configurationDirectory);
            var document = File.Exists(path) ? IniDocument.Load(await File.ReadAllBytesAsync(path, cancellationToken)) : IniDocument.Empty();
            if (document.Errors.Count > 0)
                return OperationResult<string>.Failure("ConfigurationFileInvalid", $"{fileName} has errors and was not modified.", document.Errors[0].Message, "Fix the file first.");
            foreach (var (key, value) in values) document.Set(section, key, value ?? string.Empty);

            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, document.ToText(), new UTF8Encoding(false), cancellationToken);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }

            return OperationResult<string>.Success(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Configuration file {Path} could not be written", path);
            return OperationResult<string>.Failure("ConfigurationWriteFailed", $"{fileName} could not be written.", exception.GetType().Name, "Check that the configuration directory is writable.");
        }
    }
}
