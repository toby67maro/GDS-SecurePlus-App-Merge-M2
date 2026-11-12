using System.Text;

namespace EngineeringWorkspace.Infrastructure.FileSystem;

/// <summary>Atomic file writes: write a temporary file in the same directory, flush it to disk, then replace.</summary>
public static class AtomicFile
{
    public static async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("The path has no directory.", nameof(path));
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken) =>
        WriteAllBytesAsync(path, new UTF8Encoding(false).GetBytes(content), cancellationToken);
}
