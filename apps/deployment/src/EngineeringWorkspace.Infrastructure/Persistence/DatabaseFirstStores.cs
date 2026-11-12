using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Settings;
using EngineeringWorkspace.Application.Workspaces;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Customers;
using EngineeringWorkspace.Domain.Workspaces;
using EngineeringWorkspace.Infrastructure.FileSystem;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Infrastructure.Persistence;

/// <summary>
/// Moves this computer's local files into the database once and keeps them as a backup. Files are copied into a dated
/// folder under data\Backup and removed from their original place, so they are never read again. A matching database
/// record is replaced only when the local file is newer (the newer edit wins).
/// </summary>
public sealed class LocalFileImporter(PathSettings paths, ILogger<LocalFileImporter> logger)
{
    private readonly string _stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

    public string BackupRoot => Path.Combine(paths.DataDirectory, "Backup", _stamp);

    /// <summary>Moves <paramref name="file"/> (and its emptied folder) into the backup tree, keeping its path relative to <paramref name="root"/>.</summary>
    public void Backup(string file, string root, string area)
    {
        try
        {
            if (!File.Exists(file)) return;
            var relative = Path.GetRelativePath(root, file);
            var target = Path.Combine(BackupRoot, area, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            File.Delete(file);
            var folder = Path.GetDirectoryName(file)!;
            if (!string.Equals(Path.GetFullPath(folder), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "{File} was imported into the database but could not be moved to the backup folder", file);
        }
    }
}

/// <summary>
/// Customer profiles with the database as the system of record. Before a database is configured (first-run setup) the
/// local files are used; once it is ready, local files are imported once and the database is used from then on.
/// </summary>
public sealed class DatabaseFirstCustomerProfileStore(
    IniCustomerProfileStore files,
    ICustomerProfileRepository repository,
    IDatabaseStatusService database,
    PathSettings paths,
    LocalFileImporter importer,
    ILogger<DatabaseFirstCustomerProfileStore> logger) : ICustomerProfileStore
{
    private readonly SemaphoreSlim _import = new(1, 1);
    private bool _imported;

    public async Task<IReadOnlyList<CustomerProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!await UseDatabaseAsync(cancellationToken)) return await files.ListAsync(cancellationToken);
        return (await repository.ListCustomerProfilesAsync(cancellationToken)).Select(stored => stored.Profile).ToArray();
    }

    public async Task<CustomerProfile?> LoadAsync(CustomerId id, CancellationToken cancellationToken = default)
    {
        if (!await UseDatabaseAsync(cancellationToken)) return await files.LoadAsync(id, cancellationToken);
        return (await repository.LoadCustomerProfileAsync(id.Value, cancellationToken))?.Profile;
    }

    public async Task SaveAsync(CustomerProfile profile, CancellationToken cancellationToken = default)
    {
        if (!await UseDatabaseAsync(cancellationToken))
        {
            await files.SaveAsync(profile, cancellationToken);
            return;
        }

        var existing = await repository.LoadCustomerProfileAsync(profile.Id.Value, cancellationToken);
        await repository.SaveCustomerProfileAsync(profile, existing?.IsSample ?? IsSample(profile.Id), null, cancellationToken);
    }

    private static bool IsSample(CustomerId id) => id.Value == Application.Assessments.EngagementService.SampleCustomerId.Value;

    private async Task<bool> UseDatabaseAsync(CancellationToken cancellationToken)
    {
        if (database.Settings.Provider == DatabaseProvider.None) return false;
        if (database.LatestStatus is null) await database.RefreshAsync(cancellationToken);
        if (!repository.IsAvailable) return false;
        if (!_imported) await ImportAsync(cancellationToken);
        return true;
    }

    private async Task ImportAsync(CancellationToken cancellationToken)
    {
        await _import.WaitAsync(cancellationToken);
        try
        {
            if (_imported) return;
            if (Directory.Exists(paths.CustomerDirectory))
            {
                foreach (var folder in Directory.EnumerateDirectories(paths.CustomerDirectory))
                {
                    var file = Path.Combine(folder, "Customer.ini");
                    if (!File.Exists(file) || !CustomerId.TryCreate(Path.GetFileName(folder), out var id)) continue;
                    try
                    {
                        var local = await files.LoadAsync(id, cancellationToken);
                        if (local is null) continue;
                        var fileTime = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                        var stored = await repository.LoadCustomerProfileAsync(id.Value, cancellationToken);
                        if (stored is null || stored.ModifiedAtUtc < fileTime)
                            await repository.SaveCustomerProfileAsync(stored is null ? local : Merge(local, stored.Profile), stored?.IsSample ?? IsSample(id), fileTime, cancellationToken);
                        importer.Backup(file, paths.CustomerDirectory, "Customers");
                        importer.Backup(Path.Combine(folder, "Overrides.ini"), paths.CustomerDirectory, "Customers");
                        logger.LogInformation("Customer {CustomerId} imported from {File} into the database", id.Value, file);
                    }
                    catch (Exception exception) when (exception is InvalidDataException or IOException)
                    {
                        logger.LogWarning(exception, "Customer file {File} could not be imported; it was left in place", file);
                    }
                }
            }

            _imported = true;
        }
        finally
        {
            _import.Release();
        }
    }

    /// <summary>The local file never held contacts or organization details; keep those from the database.</summary>
    private static CustomerProfile Merge(CustomerProfile local, CustomerProfile stored) => local with
    {
        Contacts = local.Contacts.Count > 0 ? local.Contacts : stored.Contacts,
        Organization = local.Organization ?? stored.Organization
    };
}

/// <summary>Workspaces with the database as the system of record (same rules as customer profiles).</summary>
public sealed class DatabaseFirstWorkspaceStore(
    IniWorkspaceStore files,
    IWorkspaceRepository repository,
    IDatabaseStatusService database,
    PathSettings paths,
    LocalFileImporter importer,
    ILogger<DatabaseFirstWorkspaceStore> logger) : IWorkspaceStore
{
    private readonly SemaphoreSlim _import = new(1, 1);
    private bool _imported;

    public string GetDirectory(WorkspaceId id) => files.GetDirectory(id);

    public async Task<bool> ExistsAsync(WorkspaceId id, CancellationToken cancellationToken = default) =>
        await UseDatabaseAsync(cancellationToken)
            ? await repository.LoadWorkspaceAsync(id, paths.WorkspaceDirectory, cancellationToken) is not null
            : await files.ExistsAsync(id, cancellationToken);

    public async Task<IReadOnlyList<Workspace>> ListAsync(CancellationToken cancellationToken = default) =>
        await UseDatabaseAsync(cancellationToken) ? await repository.ListWorkspacesAsync(paths.WorkspaceDirectory, cancellationToken) : await files.ListAsync(cancellationToken);

    public async Task<Workspace?> LoadAsync(WorkspaceId id, CancellationToken cancellationToken = default)
    {
        if (!await UseDatabaseAsync(cancellationToken)) return await files.LoadAsync(id, cancellationToken);
        var workspace = await repository.LoadWorkspaceAsync(id, paths.WorkspaceDirectory, cancellationToken);
        if (workspace is not null) Directory.CreateDirectory(workspace.Directory);
        return workspace;
    }

    public async Task SaveAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        if (!await UseDatabaseAsync(cancellationToken))
        {
            await files.SaveAsync(workspace, cancellationToken);
            return;
        }

        Directory.CreateDirectory(workspace.Directory);
        await repository.SaveWorkspaceAsync(workspace, cancellationToken);
    }

    private async Task<bool> UseDatabaseAsync(CancellationToken cancellationToken)
    {
        if (database.Settings.Provider == DatabaseProvider.None) return false;
        if (database.LatestStatus is null) await database.RefreshAsync(cancellationToken);
        if (!repository.IsAvailable) return false;
        if (!_imported) await ImportAsync(cancellationToken);
        return true;
    }

    private async Task ImportAsync(CancellationToken cancellationToken)
    {
        await _import.WaitAsync(cancellationToken);
        try
        {
            if (_imported) return;
            foreach (var workspace in await files.ListAsync(cancellationToken))
            {
                var file = Path.Combine(files.GetDirectory(workspace.Id), "Workspace.ini");
                var stored = await repository.LoadWorkspaceAsync(workspace.Id, paths.WorkspaceDirectory, cancellationToken);
                if (stored is null || stored.ModifiedAtUtc < workspace.ModifiedAtUtc)
                    await repository.SaveWorkspaceAsync(stored is null ? workspace : workspace with { ThemeKey = stored.ThemeKey, Description = stored.Description }, cancellationToken);
                importer.Backup(file, paths.WorkspaceDirectory, "Workspaces");
                logger.LogInformation("Workspace {WorkspaceId} imported from {File} into the database", workspace.Id.Value, file);
            }

            _imported = true;
        }
        finally
        {
            _import.Release();
        }
    }
}
