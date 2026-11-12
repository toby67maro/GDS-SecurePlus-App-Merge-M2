using EngineeringWorkspace.Domain.Common;

namespace EngineeringWorkspace.Application.Scripts;

// Interfaces only; script generation is implemented in M6. Templates stay out of the UI.

/// <summary>A generated script with the provenance every generated script must carry.</summary>
public sealed record GeneratedScript(
    string FileName,
    string Content,
    CustomerId CustomerId,
    DateTimeOffset GeneratedAtUtc,
    string SourceControlId,
    string Description,
    IReadOnlyList<string> Prerequisites,
    IReadOnlyList<string> RequiredPermissions,
    string ApplicationVersion);

public interface IScriptTemplate
{
    string TemplateId { get; }
    string Description { get; }
    IReadOnlyList<string> RequiredParameters { get; }
}

public interface IScriptTemplateProvider
{
    Task<IReadOnlyList<IScriptTemplate>> ListAsync(CancellationToken cancellationToken);
}

public interface IScriptGenerator
{
    Task<GeneratedScript> GenerateAsync(IScriptTemplate template, CustomerId customerId, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken);
}
