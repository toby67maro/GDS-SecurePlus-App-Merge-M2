using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EngineeringWorkspace.Application.Repositories;
using EngineeringWorkspace.Domain.Policies;

namespace EngineeringWorkspace.Infrastructure.Repositories;

/// <summary>Scans a policy repository for JSON artifacts and their companion PowerShell files. Never executes scripts.</summary>
public sealed class FilePolicyRepositoryScanner : IPolicyRepositoryScanner
{
    public async Task<RepositoryScanResult> ScanAsync(string repositoryPath, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var artifacts = new List<PolicyArtifact>();
        var diagnostics = new List<RepositoryDiagnostic>();
        if (!Directory.Exists(repositoryPath))
        {
            diagnostics.Add(new(RepositoryDiagnosticSeverity.Error, "RepositoryMissing", $"Repository directory does not exist: {repositoryPath}"));
            return new(artifacts, diagnostics, 0);
        }

        var files = Directory.EnumerateFiles(repositoryPath, "*.json", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = files[index];
            try
            {
                var raw = await File.ReadAllTextAsync(path, cancellationToken);
                using var document = JsonDocument.Parse(raw);
                var root = document.RootElement;
                var id = ReadString(root, "id") ?? ReadString(root, "name") ?? Path.GetFileNameWithoutExtension(path);
                var displayName = ReadString(root, "displayName") ?? ReadString(root, "name") ?? id;
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
                artifacts.Add(new(id, displayName, path, repositoryPath, hash, File.GetLastWriteTimeUtc(path), raw, FindCompanion(path)));
            }
            catch (JsonException exception)
            {
                diagnostics.Add(new(RepositoryDiagnosticSeverity.Error, "MalformedJson", exception.Message, path));
            }
            catch (IOException exception)
            {
                diagnostics.Add(new(RepositoryDiagnosticSeverity.Error, "FileReadFailed", exception.Message, path));
            }

            progress?.Report(index + 1);
        }

        foreach (var duplicate in artifacts.GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            diagnostics.Add(new(RepositoryDiagnosticSeverity.Error, "DuplicateArtifactId", $"Duplicate artifact identifier: {duplicate.Key}"));
        return new(artifacts, diagnostics, files.Length);
    }

    private static string? FindCompanion(string jsonPath)
    {
        var directory = Path.GetDirectoryName(jsonPath);
        if (directory is null) return null;
        var exact = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(jsonPath)}.ps1");
        return File.Exists(exact) ? exact : Directory.EnumerateFiles(directory, "*.ps1").Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    private static string? ReadString(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
