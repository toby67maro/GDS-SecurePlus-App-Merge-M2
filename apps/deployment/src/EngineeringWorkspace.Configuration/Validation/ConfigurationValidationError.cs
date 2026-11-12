using EngineeringWorkspace.Application.Configuration;
using EngineeringWorkspace.Application.Security;

namespace EngineeringWorkspace.Configuration.Validation;

/// <summary>Validation stage that produced an error.</summary>
public enum ConfigurationValidationStage
{
    FilesAndProviders = 1,
    Binding = 2,
    Options = 3,
    CrossSection = 4
}

/// <summary>
/// One configuration problem. <see cref="RawValue"/> may be secret; display code must use
/// <see cref="ToIssue"/>, which redacts it.
/// </summary>
public sealed record ConfigurationValidationError(
    ConfigurationValidationStage Stage,
    string? FilePath,
    string? SourceLayer,
    string? Section,
    string? Key,
    string? RawValue,
    string ErrorCode,
    string Message,
    string? ExpectedFormat,
    IReadOnlyList<string> AllowedValues,
    ConfigurationSeverity Severity)
{
    public ConfigurationIssue ToIssue(IRedactionService redaction) => new(
        Severity, ErrorCode, redaction.RedactText(Message), FilePath, SourceLayer, Section, Key,
        Key is null ? redaction.RedactText(RawValue) : redaction.RedactValue(Key, RawValue),
        ExpectedFormat, AllowedValues);

    /// <summary>Actionable single-line description without secrets.</summary>
    public string Describe(IRedactionService redaction)
    {
        var issue = ToIssue(redaction);
        var location = string.Join(" ", new[]
        {
            FilePath is null ? null : $"File: {FilePath}",
            Section is null ? null : $"Section: [{Section}]",
            Key is null ? null : $"Key: {Key}",
            issue.SafeValue is null ? null : $"Value: '{issue.SafeValue}'"
        }.Where(part => part is not null));
        var allowed = AllowedValues.Count == 0 ? null : $"Allowed values: {string.Join(", ", AllowedValues)}.";
        var expected = ExpectedFormat is null ? null : $"Expected: {ExpectedFormat}.";
        return string.Join(" ", new[] { issue.Message, location, expected, allowed }.Where(part => !string.IsNullOrEmpty(part)));
    }
}

/// <summary>Thrown when configuration has errors. Carries every error found in one pass.</summary>
public sealed class ConfigurationValidationException(IReadOnlyList<ConfigurationValidationError> errors)
    : Exception($"Configuration has {errors.Count(error => error.Severity == ConfigurationSeverity.Error)} error(s).")
{
    public IReadOnlyList<ConfigurationValidationError> Errors { get; } = errors;
}
