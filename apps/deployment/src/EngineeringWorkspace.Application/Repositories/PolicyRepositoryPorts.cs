using EngineeringWorkspace.Domain.Policies;

namespace EngineeringWorkspace.Application.Repositories;

public enum RepositoryDiagnosticSeverity
{
    Information,
    Warning,
    Error
}

public sealed record RepositoryDiagnostic(RepositoryDiagnosticSeverity Severity, string Code, string Message, string? FilePath = null);

public sealed record RepositoryScanResult(IReadOnlyList<PolicyArtifact> Artifacts, IReadOnlyList<RepositoryDiagnostic> Diagnostics, int FilesScanned);

/// <summary>Scans a policy repository. Never executes scripts.</summary>
public interface IPolicyRepositoryScanner
{
    Task<RepositoryScanResult> ScanAsync(string repositoryPath, IProgress<int>? progress, CancellationToken cancellationToken);
}
