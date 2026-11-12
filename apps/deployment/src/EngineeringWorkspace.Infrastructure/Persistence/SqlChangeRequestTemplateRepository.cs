using System.Globalization;
using System.Text.Json;
using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Governance;
using EngineeringWorkspace.Infrastructure.Database;

namespace EngineeringWorkspace.Infrastructure.Persistence;

/// <summary>Change request templates in secureplus.ChangeRequestTemplate (content as JSON).</summary>
public sealed class SqlChangeRequestTemplateRepository(IDatabaseStatusService database, DatabaseConnectionFactory connections, AuditContext audit, TimeProvider timeProvider)
    : IChangeRequestTemplateRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsAvailable => database.Settings.Provider != DatabaseProvider.None && database.LatestStatus?.State == DatabaseOverallState.Ready;

    public async Task<IReadOnlyList<ChangeRequestTemplate>> ListGlobalAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return [ChangeRequestTemplates.Default];
        await EnsureDefaultAsync(cancellationToken);
        return await QueryAsync("CustomerKey IS NULL", null, cancellationToken);
    }

    public Task<IReadOnlyList<ChangeRequestTemplate>> ListForCustomerAsync(string customerKey, CancellationToken cancellationToken = default) =>
        IsAvailable ? QueryAsync("CustomerKey = @customer", customerKey, cancellationToken) : Task.FromResult<IReadOnlyList<ChangeRequestTemplate>>([]);

    public async Task<ChangeRequestTemplate> SaveAsync(ChangeRequestTemplate template, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.Key == ChangeRequestTemplates.DefaultKey) throw new InvalidOperationException("The GDS default template cannot be changed. Assign it to the customer and edit that copy.");
        if (string.IsNullOrWhiteSpace(template.Name)) throw new ArgumentException("Enter a template name.", nameof(template));
        var saved = template with { Version = template.Version + 1, Name = template.Name.Trim().Length > 200 ? template.Name.Trim()[..200] : template.Name.Trim() };
        await using var session = await OpenAsync(cancellationToken);
        var table = session.Table("secureplus", "ChangeRequestTemplate");
        var exists = await SqlBatch.CountAsync(session, null, $"SELECT COUNT(*) FROM {table} WHERE TemplateKey = @key;", SqlBatch.Parameters(("@key", saved.Key)), cancellationToken) > 0;
        var now = session.Date(timeProvider.GetUtcNow());
        await SqlBatch.ExecuteAsync(session, null, exists
                ? $"UPDATE {table} SET Name = @name, Version = @version, ContentJson = @content, SourceTemplateKey = @source, SourceVersion = @sourceVersion, ModifiedBy = @by, ModifiedAtUtc = @now WHERE TemplateKey = @key;"
                : $"INSERT INTO {table} (TemplateKey, CustomerKey, Name, Version, SourceTemplateKey, SourceVersion, IsSystem, ContentJson, CreatedBy, CreatedAtUtc) VALUES (@key, @customer, @name, @version, @source, @sourceVersion, @system, @content, @by, @now);",
            SqlBatch.Parameters(("@key", saved.Key), ("@customer", saved.CustomerKey), ("@name", saved.Name), ("@version", saved.Version), ("@source", saved.SourceKey),
                ("@sourceVersion", saved.SourceVersion), ("@system", session.Bool(false)), ("@content", JsonSerializer.Serialize(saved.Content, Json)), ("@by", Actor),
                ("@now", now)), cancellationToken);
        return saved;
    }

    public async Task<ChangeRequestTemplate> AssignAsync(string customerKey, string globalKey, CancellationToken cancellationToken = default)
    {
        var source = (await ListGlobalAsync(cancellationToken)).FirstOrDefault(template => template.Key == globalKey)
                     ?? throw new InvalidOperationException("The global template no longer exists.");
        return await SaveAsync(new ChangeRequestTemplate(NewKey(), customerKey, source.Name, 0, source.Key, source.Version, false, source.Content), cancellationToken);
    }

    public async Task<ChangeRequestTemplate> SaveAsGlobalAsync(ChangeRequestTemplate customerTemplate, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customerTemplate);
        var global = await SaveAsync(new ChangeRequestTemplate(NewKey(), null, name, 0, null, null, false, customerTemplate.Content), cancellationToken);
        if (customerTemplate.CustomerKey is not null)
            await SaveAsync(customerTemplate with { SourceKey = global.Key, SourceVersion = global.Version }, cancellationToken);
        return global;
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        if (key == ChangeRequestTemplates.DefaultKey) throw new InvalidOperationException("The GDS default template cannot be deleted.");
        await using var session = await OpenAsync(cancellationToken);
        await SqlBatch.ExecuteAsync(session, null, $"DELETE FROM {session.Table("secureplus", "ChangeRequestTemplate")} WHERE TemplateKey = @key AND IsSystem = 0;",
            SqlBatch.Parameters(("@key", key)), cancellationToken);
    }

    private async Task EnsureDefaultAsync(CancellationToken cancellationToken)
    {
        await using var session = await OpenAsync(cancellationToken);
        var table = session.Table("secureplus", "ChangeRequestTemplate");
        if (await SqlBatch.CountAsync(session, null, $"SELECT COUNT(*) FROM {table} WHERE TemplateKey = @key;", SqlBatch.Parameters(("@key", ChangeRequestTemplates.DefaultKey)),
                cancellationToken) > 0) return;
        var template = ChangeRequestTemplates.Default;
        await SqlBatch.ExecuteAsync(session, null,
            $"INSERT INTO {table} (TemplateKey, CustomerKey, Name, Version, IsSystem, ContentJson, CreatedBy, CreatedAtUtc) VALUES (@key, NULL, @name, @version, @system, @content, @by, @now);",
            SqlBatch.Parameters(("@key", template.Key), ("@name", template.Name), ("@version", template.Version), ("@system", session.Bool(true)),
                ("@content", JsonSerializer.Serialize(template.Content, Json)), ("@by", "Secure Plus"), ("@now", session.Date(timeProvider.GetUtcNow()))), cancellationToken);
    }

    private async Task<IReadOnlyList<ChangeRequestTemplate>> QueryAsync(string where, string? customerKey, CancellationToken cancellationToken)
    {
        await using var session = await OpenAsync(cancellationToken);
        var rows = await SqlBatch.QueryAsync(session, null,
            $"SELECT TemplateKey, CustomerKey, Name, Version, SourceTemplateKey, SourceVersion, IsSystem, ContentJson FROM {session.Table("secureplus", "ChangeRequestTemplate")} " +
            $"WHERE {where} ORDER BY IsSystem DESC, Name;", customerKey is null ? null : SqlBatch.Parameters(("@customer", customerKey)), cancellationToken);
        return rows.Select(row => new ChangeRequestTemplate((string)row["TemplateKey"]!, row["CustomerKey"] as string, (string)row["Name"]!,
            Convert.ToInt32(row["Version"], CultureInfo.InvariantCulture), row["SourceTemplateKey"] as string,
            row["SourceVersion"] is null or DBNull ? null : Convert.ToInt32(row["SourceVersion"], CultureInfo.InvariantCulture),
            Convert.ToBoolean(row["IsSystem"], CultureInfo.InvariantCulture),
            JsonSerializer.Deserialize<ChangeRequestTemplateContent>((string)row["ContentJson"]!, Json) ?? ChangeRequestTemplates.Default.Content)).ToArray();
    }

    private string Actor => audit.Actor.Length > 256 ? audit.Actor[..256] : audit.Actor;
    private static string NewKey() => Guid.NewGuid().ToString("N");

    private Task<SqlSession> OpenAsync(CancellationToken cancellationToken) =>
        SqlSession.OpenAsync(database, connections, message => new InvalidOperationException(message), cancellationToken);
}
