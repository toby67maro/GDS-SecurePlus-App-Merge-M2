using System.Globalization;
using System.Text.Json;
using EngineeringWorkspace.Application.Workspaces;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Workspaces;
using EngineeringWorkspace.Infrastructure.Database;

namespace EngineeringWorkspace.Infrastructure.Persistence;

/// <summary>Workspaces as the system of record in secureplus.Workspace (shared by every computer using the database).</summary>
public sealed partial class SqlEngagementRepository : IWorkspaceRepository
{
    private static readonly JsonSerializerOptions WorkspaceJson = new(JsonSerializerDefaults.Web);

    private sealed record WorkspaceSettingsDocument(Guid? TenantId, string? DatabaseReference, string? GoldenTenantReference, Dictionary<string, string>? Integrations);

    public async Task<IReadOnlyList<Workspace>> ListWorkspacesAsync(string localWorkspaceRoot, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        return await ReadWorkspacesAsync(session, localWorkspaceRoot, null, cancellationToken);
    }

    public async Task<Workspace?> LoadWorkspaceAsync(WorkspaceId id, string localWorkspaceRoot, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        return (await ReadWorkspacesAsync(session, localWorkspaceRoot, id.Value, cancellationToken)).FirstOrDefault();
    }

    public async Task SaveWorkspaceAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        await SaveWorkspaceRowAsync(session, workspace, cancellationToken);
    }

    public async Task ArchiveWorkspaceAsync(WorkspaceId id, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        await using var command = session.Command(null, $"UPDATE {session.Table("secureplus", "Workspace")} SET IsArchived = @archived, ModifiedBy = @by, ModifiedAtUtc = @now WHERE WorkspaceId = @id;");
        DatabaseConnectionFactory.Parameter(command, "@archived", session.Bool(true));
        DatabaseConnectionFactory.Parameter(command, "@by", Truncate(auditContext.Actor, 256));
        DatabaseConnectionFactory.Parameter(command, "@now", session.Date(timeProvider.GetUtcNow()));
        DatabaseConnectionFactory.Parameter(command, "@id", id.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<List<Workspace>> ReadWorkspacesAsync(SqlSession session, string localWorkspaceRoot, string? id, CancellationToken cancellationToken)
    {
        await using var command = session.Command(null,
            $"SELECT WorkspaceId, Name, CustomerKey, Environment, ThemeKey, Description, SettingsJson, CreatedAtUtc, ModifiedAtUtc FROM {session.Table("secureplus", "Workspace")} " +
            $"WHERE (IsArchived IS NULL OR IsArchived = 0){(id is null ? string.Empty : " AND WorkspaceId = @id")} ORDER BY Name;");
        if (id is not null) DatabaseConnectionFactory.Parameter(command, "@id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var workspaces = new List<Workspace>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!WorkspaceId.TryCreate(reader.GetString(0), out var workspaceId)) continue;
            WorkspaceSettingsDocument? settings = null;
            if (!reader.IsDBNull(6))
            {
                try
                {
                    settings = JsonSerializer.Deserialize<WorkspaceSettingsDocument>(reader.GetString(6), WorkspaceJson);
                }
                catch (JsonException)
                {
                    // Keep the workspace usable without its optional settings.
                }
            }

            string? Text(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
            var created = reader.IsDBNull(7) ? DateTimeOffset.UnixEpoch : SqlSession.ReadDate(reader.GetValue(7));
            workspaces.Add(new Workspace(workspaceId, reader.GetString(1), Path.Combine(localWorkspaceRoot, workspaceId.Value),
                CustomerId.TryCreate(Text(2), out var customer) ? customer : null, settings?.TenantId, Text(3) ?? "Production", settings?.DatabaseReference,
                settings?.GoldenTenantReference, new Dictionary<string, string>(settings?.Integrations ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
                created, reader.IsDBNull(8) ? created : SqlSession.ReadDate(reader.GetValue(8)), Text(4), Text(5)));
        }

        return workspaces;
    }

    private async Task SaveWorkspaceRowAsync(SqlSession session, Workspace workspace, CancellationToken cancellationToken)
    {
        int? customerId = null;
        if (workspace.CurrentCustomerId is { } key && await FindLinkAsync(session, null, key.Value, cancellationToken) is { } link) customerId = link.CustomerId;
        var table = session.Table("secureplus", "Workspace");
        var exists = await ScalarIntAsync(session, null!, $"SELECT COUNT(*) FROM {table} WHERE WorkspaceId = @key;", workspace.Id.Value, cancellationToken) > 0;
        var settings = JsonSerializer.Serialize(new WorkspaceSettingsDocument(workspace.TenantId, workspace.DatabaseReference, workspace.GoldenTenantReference,
            new Dictionary<string, string>(workspace.IntegrationSettings)), WorkspaceJson);
        var sql = exists
            ? $"UPDATE {table} SET Name = @name, Directory = @directory, CustomerId = @customer, CustomerKey = @customerKey, Environment = @environment, ThemeKey = @theme, " +
              "Description = @description, SettingsJson = @settings, IsArchived = @archived, ModifiedBy = @by, ModifiedAtUtc = @modified WHERE WorkspaceId = @id;"
            : $"INSERT INTO {table} (WorkspaceId, Name, Directory, CustomerId, CustomerKey, Environment, ThemeKey, Description, SettingsJson, IsArchived, CreatedBy, ModifiedBy, " +
              "CreatedAtUtc, ModifiedAtUtc) VALUES (@id, @name, @directory, @customer, @customerKey, @environment, @theme, @description, @settings, @archived, @by, @by, @created, @modified);";
        await using var command = session.Command(null, sql);
        Parameters(command, ("@id", workspace.Id.Value), ("@name", Truncate(workspace.Name, 200)), ("@directory", Truncate(workspace.Directory, 1000)), ("@customer", customerId),
            ("@customerKey", workspace.CurrentCustomerId?.Value), ("@environment", Truncate(workspace.Environment, 20)), ("@theme", workspace.ThemeKey),
            ("@description", workspace.Description is null ? null : Truncate(workspace.Description, 1000)), ("@settings", settings), ("@archived", session.Bool(false)),
            ("@by", Truncate(auditContext.Actor, 256)), ("@created", session.Date(workspace.CreatedAtUtc)), ("@modified", session.Date(workspace.ModifiedAtUtc)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
