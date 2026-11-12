using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Security;
using EngineeringWorkspace.Infrastructure.Database;

namespace EngineeringWorkspace.Infrastructure.Persistence;

/// <summary>
/// Lead edits of the control catalogue (secureplus.CatalogControlEdit, CatalogPolicySetEdit) and their append-only history
/// (secureplus.CatalogEditHistory). These tables are never touched by a catalogue reload. Only Leads may change them once users are managed.
/// </summary>
public sealed class SqlCatalogEditRepository(IDatabaseStatusService database, DatabaseConnectionFactory connections, AuditContext audit, TimeProvider timeProvider,
    IUserDirectory? users = null) : ICatalogEditRepository
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public bool IsAvailable => database.Settings.Provider != DatabaseProvider.None && database.LatestStatus?.State == DatabaseOverallState.Ready;

    public async Task<CatalogEdits> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return CatalogEdits.None;
        await using var session = await OpenAsync(cancellationToken);
        var controls = (await SqlBatch.QueryAsync(session, null, $"SELECT * FROM {session.Table("secureplus", "CatalogControlEdit")} ORDER BY ControlId;", null, cancellationToken))
            .Select(row => new CatalogControlEdit((string)row["ControlId"]!, row["BasedOnControlId"] as string, Bool(row["IsHidden"]),
                row["ContentJson"] is string content ? JsonSerializer.Deserialize<CatalogControlContent>(content, Json) : null, row["BaseHash"] as string,
                Convert.ToInt32(row["Version"], CultureInfo.InvariantCulture), (string)row["ModifiedBy"]!, SqlSession.ReadDate(row["ModifiedAtUtc"]!)))
            .ToArray();
        var sets = (await SqlBatch.QueryAsync(session, null, $"SELECT * FROM {session.Table("secureplus", "CatalogPolicySetEdit")} ORDER BY DeploymentOrder, SetId;", null, cancellationToken))
            .Select(row => new CatalogPolicySetEdit((string)row["SetId"]!, row["BasedOnSetId"] as string, Bool(row["IsHidden"]), (string)row["DisplayName"]!,
                row["Description"] as string ?? string.Empty, Convert.ToInt32(row["DeploymentOrder"], CultureInfo.InvariantCulture),
                JsonSerializer.Deserialize<string[]>((string)row["ControlIdsJson"]!, Json) ?? [],
                row["IncludesJson"] is string includes ? JsonSerializer.Deserialize<string[]>(includes, Json) ?? [] : [], row["BaseHash"] as string,
                Convert.ToInt32(row["Version"], CultureInfo.InvariantCulture), (string)row["ModifiedBy"]!, SqlSession.ReadDate(row["ModifiedAtUtc"]!)))
            .ToArray();
        return new CatalogEdits(controls, sets);
    }

    public async Task SaveControlAsync(CatalogControlEdit edit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        Validate(edit);
        await EnsureLeadAsync(cancellationToken);
        await using var session = await OpenAsync(cancellationToken);
        await using var transaction = await session.Connection.BeginTransactionAsync(cancellationToken);
        var table = session.Table("secureplus", "CatalogControlEdit");
        var before = await SingleAsync(session, transaction, table, "ControlId", edit.ControlId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var version = before is null ? 1 : Convert.ToInt32(before["Version"], CultureInfo.InvariantCulture) + 1;
        var content = edit.Content is null ? null : JsonSerializer.Serialize(edit.Content, Json);
        await SqlBatch.ExecuteAsync(session, transaction, before is null
                ? $"INSERT INTO {table} (ControlId, BasedOnControlId, IsHidden, ContentJson, BaseHash, Version, CreatedBy, CreatedAtUtc, ModifiedBy, ModifiedAtUtc) " +
                  "VALUES (@id, @based, @hidden, @content, @hash, @version, @by, @at, @by, @at);"
                : $"UPDATE {table} SET BasedOnControlId = @based, IsHidden = @hidden, ContentJson = @content, BaseHash = @hash, Version = @version, ModifiedBy = @by, ModifiedAtUtc = @at WHERE ControlId = @id;",
            SqlBatch.Parameters(("@id", edit.ControlId), ("@based", edit.BasedOnControlId), ("@hidden", session.Bool(edit.IsHidden)), ("@content", content), ("@hash", edit.BaseHash),
                ("@version", version), ("@by", audit.Actor), ("@at", session.Date(now))), cancellationToken);
        var action = before is null ? edit.IsHidden ? CatalogEditActions.Hidden : CatalogEditActions.Created
            : edit.IsHidden && !Bool(before["IsHidden"]) ? CatalogEditActions.Hidden
            : !edit.IsHidden && Bool(before["IsHidden"]) ? CatalogEditActions.Restored
            : CatalogEditActions.Edited;
        await HistoryAsync(session, transaction, "Control", edit.ControlId, action, before is null ? null : JsonSerializer.Serialize(before, Json),
            JsonSerializer.Serialize(new { edit.BasedOnControlId, edit.IsHidden, edit.Content, version }, Json), now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteControlAsync(string controlId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlId);
        await EnsureLeadAsync(cancellationToken);
        await using var session = await OpenAsync(cancellationToken);
        await using var transaction = await session.Connection.BeginTransactionAsync(cancellationToken);
        var table = session.Table("secureplus", "CatalogControlEdit");
        var before = await SingleAsync(session, transaction, table, "ControlId", controlId, cancellationToken);
        if (before is null) return;
        await SqlBatch.ExecuteAsync(session, transaction, $"DELETE FROM {table} WHERE ControlId = @id;", SqlBatch.Parameters(("@id", controlId)), cancellationToken);
        await HistoryAsync(session, transaction, "Control", controlId, CatalogEditActions.Deleted, JsonSerializer.Serialize(before, Json), null, timeProvider.GetUtcNow(),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SavePolicySetAsync(CatalogPolicySetEdit edit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (string.IsNullOrWhiteSpace(edit.SetId) || edit.SetId.Length > 100) throw new ArgumentException("A policy set needs an ID of at most 100 characters.", nameof(edit));
        if (string.IsNullOrWhiteSpace(edit.DisplayName) || edit.DisplayName.Trim().Length > 200) throw new ArgumentException("Enter a name of at most 200 characters.", nameof(edit));
        await EnsureLeadAsync(cancellationToken);
        await using var session = await OpenAsync(cancellationToken);
        await using var transaction = await session.Connection.BeginTransactionAsync(cancellationToken);
        var table = session.Table("secureplus", "CatalogPolicySetEdit");
        var before = await SingleAsync(session, transaction, table, "SetId", edit.SetId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var version = before is null ? 1 : Convert.ToInt32(before["Version"], CultureInfo.InvariantCulture) + 1;
        var members = JsonSerializer.Serialize(edit.ControlIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), Json);
        var includes = JsonSerializer.Serialize(edit.Includes, Json);
        await SqlBatch.ExecuteAsync(session, transaction, before is null
                ? $"INSERT INTO {table} (SetId, BasedOnSetId, IsHidden, DisplayName, Description, DeploymentOrder, ControlIdsJson, IncludesJson, BaseHash, Version, CreatedBy, CreatedAtUtc, ModifiedBy, ModifiedAtUtc) " +
                  "VALUES (@id, @based, @hidden, @name, @description, @order, @members, @includes, @hash, @version, @by, @at, @by, @at);"
                : $"UPDATE {table} SET BasedOnSetId = @based, IsHidden = @hidden, DisplayName = @name, Description = @description, DeploymentOrder = @order, ControlIdsJson = @members, " +
                  "IncludesJson = @includes, BaseHash = @hash, Version = @version, ModifiedBy = @by, ModifiedAtUtc = @at WHERE SetId = @id;",
            SqlBatch.Parameters(("@id", edit.SetId), ("@based", edit.BasedOnSetId), ("@hidden", session.Bool(edit.IsHidden)), ("@name", edit.DisplayName.Trim()),
                ("@description", SqlBatch.Text(edit.Description, 2000)), ("@order", edit.DeploymentOrder), ("@members", members), ("@includes", includes), ("@hash", edit.BaseHash),
                ("@version", version), ("@by", audit.Actor), ("@at", session.Date(now))), cancellationToken);
        await HistoryAsync(session, transaction, "PolicySet", edit.SetId, before is null ? CatalogEditActions.Created : edit.IsHidden ? CatalogEditActions.Hidden : CatalogEditActions.Edited,
            before is null ? null : JsonSerializer.Serialize(before, Json), JsonSerializer.Serialize(edit with { Version = version }, Json), now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeletePolicySetAsync(string setId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setId);
        await EnsureLeadAsync(cancellationToken);
        await using var session = await OpenAsync(cancellationToken);
        await using var transaction = await session.Connection.BeginTransactionAsync(cancellationToken);
        var table = session.Table("secureplus", "CatalogPolicySetEdit");
        var before = await SingleAsync(session, transaction, table, "SetId", setId, cancellationToken);
        if (before is null) return;
        await SqlBatch.ExecuteAsync(session, transaction, $"DELETE FROM {table} WHERE SetId = @id;", SqlBatch.Parameters(("@id", setId)), cancellationToken);
        await HistoryAsync(session, transaction, "PolicySet", setId, CatalogEditActions.Deleted, JsonSerializer.Serialize(before, Json), null, timeProvider.GetUtcNow(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CatalogEditHistoryEntry>> HistoryAsync(string itemId, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return [];
        await using var session = await OpenAsync(cancellationToken);
        var rows = await SqlBatch.QueryAsync(session, null,
            $"SELECT * FROM {session.Table("secureplus", "CatalogEditHistory")} WHERE ItemId = @id ORDER BY ChangedAtUtc DESC, CatalogEditHistoryId DESC;",
            SqlBatch.Parameters(("@id", itemId)), cancellationToken);
        return rows.Select(row => new CatalogEditHistoryEntry(Convert.ToInt64(row["CatalogEditHistoryId"], CultureInfo.InvariantCulture), (string)row["ItemType"]!,
            (string)row["ItemId"]!, (string)row["Action"]!, row["BeforeJson"] as string, row["AfterJson"] as string, (string)row["ChangedBy"]!,
            SqlSession.ReadDate(row["ChangedAtUtc"]!))).ToArray();
    }

    private static void Validate(CatalogControlEdit edit)
    {
        if (string.IsNullOrWhiteSpace(edit.ControlId) || edit.ControlId.Length > 150) throw new ArgumentException("A control needs an ID of at most 150 characters.", nameof(edit));
        if (!edit.IsHidden && edit.Content is null) throw new ArgumentException("A visible control needs its content.", nameof(edit));
        if (edit.Content is { } content && (string.IsNullOrWhiteSpace(content.Name) || content.Name.Length > 400))
            throw new ArgumentException("Enter a control name of at most 400 characters.", nameof(edit));
        if (edit.IsLeadControl && edit.IsHidden) throw new ArgumentException("Delete a Lead control instead of hiding it.", nameof(edit));
    }

    private async Task EnsureLeadAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable) throw new InvalidOperationException("Catalogue edits are stored in the application database, which is not ready.");
        if (users is not null && await users.IsEnforcedAsync(cancellationToken) && !AppRoles.CanManageUsers((await users.CurrentAsync(cancellationToken))?.RoleCode))
            throw new UnauthorizedAccessException("Only a Lead can change controls and policy sets.");
    }

    private async Task HistoryAsync(SqlSession session, DbTransaction transaction, string type, string id, string action, string? before, string? after, DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await SqlBatch.ExecuteAsync(session, transaction,
            $"INSERT INTO {session.Table("secureplus", "CatalogEditHistory")} (ItemType, ItemId, Action, BeforeJson, AfterJson, ChangedBy, ChangedAtUtc) VALUES (@type, @id, @action, @before, @after, @by, @at);",
            SqlBatch.Parameters(("@type", type), ("@id", id), ("@action", action), ("@before", before), ("@after", after), ("@by", audit.Actor), ("@at", session.Date(now))),
            cancellationToken);

    private static async Task<Dictionary<string, object?>?> SingleAsync(SqlSession session, DbTransaction transaction, string table, string key, string id,
        CancellationToken cancellationToken) =>
        (await SqlBatch.QueryAsync(session, transaction, $"SELECT * FROM {table} WHERE {key} = @id;", SqlBatch.Parameters(("@id", id)), cancellationToken)).FirstOrDefault();

    private static bool Bool(object? value) => value is not null && Convert.ToBoolean(value, CultureInfo.InvariantCulture);

    private Task<SqlSession> OpenAsync(CancellationToken cancellationToken) =>
        SqlSession.OpenAsync(database, connections, message => new InvalidOperationException(message), cancellationToken);
}
