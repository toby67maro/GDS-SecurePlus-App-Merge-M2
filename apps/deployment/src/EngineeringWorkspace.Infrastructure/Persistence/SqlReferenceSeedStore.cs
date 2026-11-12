using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Reference;
using EngineeringWorkspace.Infrastructure.Database;
using static EngineeringWorkspace.Infrastructure.Persistence.SqlBatch;

namespace EngineeringWorkspace.Infrastructure.Persistence;

/// <summary>
/// Versioned, idempotent seed of the secureplus reference tables. Each dataset is compared with secureplus.SeedVersion
/// (version + SHA-256 of its rows); unchanged datasets are skipped. Rows are upserted by stable key. System
/// specifications and baselines a user has changed (IsModified = 1) are kept; customer rows (assignments, tailored
/// specifications, user baselines, results, exceptions) are never touched.
/// </summary>
public sealed class SqlReferenceSeedStore(IDatabaseStatusService database, DatabaseConnectionFactory connections, TimeProvider timeProvider) : IReferenceSeedStore
{
    private const string SeedActor = "Secure Plus seed";

    public bool IsAvailable => database.Settings.Provider != DatabaseProvider.None && database.LatestStatus?.State == DatabaseOverallState.Ready;

    public async Task<bool> IsCurrentAsync(ControlCatalog catalog, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        var recorded = await ReadVersionsAsync(session, null, cancellationToken);
        return Datasets(catalog).All(dataset => recorded.TryGetValue(dataset.Name, out var row) && row.Version == dataset.Version && row.ContentHash == dataset.Hash);
    }

    public async Task<IReadOnlyList<SeedVersionInfo>> ListVersionsAsync(CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        return (await ReadVersionsAsync(session, null, cancellationToken)).Values.OrderBy(row => row.Dataset, StringComparer.Ordinal).ToArray();
    }

    public async Task<SeedSummary> SeedAsync(ControlCatalog catalog, bool force, IProgress<(double Progress, string Activity)>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var started = timeProvider.GetTimestamp();
        await using var session = await OpenAsync(cancellationToken);
        session.CommandTimeoutSeconds = Math.Max(session.CommandTimeoutSeconds, 300);
        await using var transaction = await session.Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var recorded = await ReadVersionsAsync(session, transaction, cancellationToken);
        var results = new List<SeedDatasetResult>();
        var datasets = Datasets(catalog);
        for (var index = 0; index < datasets.Count; index++)
        {
            var dataset = datasets[index];
            progress?.Report((100.0 * index / datasets.Count, $"Seeding {dataset.Name}..."));
            if (!force && recorded.TryGetValue(dataset.Name, out var row) && row.Version == dataset.Version && row.ContentHash == dataset.Hash)
            {
                results.Add(new SeedDatasetResult(dataset.Name, dataset.Version, true, 0, 0, 0, 0, row.Rows));
                continue;
            }

            var result = await dataset.Apply(session, transaction, cancellationToken);
            results.Add(result);
            await RecordVersionAsync(session, transaction, dataset.Name, dataset.Version, dataset.Hash, result, recorded.ContainsKey(dataset.Name), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        progress?.Report((100, "Reference data is current."));
        return new SeedSummary(results, timeProvider.GetElapsedTime(started));
    }

    /// <summary>Records the catalog dataset (loaded by <see cref="SqlCatalogStore"/>) in secureplus.SeedVersion.</summary>
    internal static Task RecordCatalogAsync(SqlSession session, DbTransaction transaction, ControlCatalog catalog, int rows, DateTimeOffset now, CancellationToken cancellationToken) =>
        UpsertVersionAsync(session, transaction, SeedDatasets.Catalog, catalog.SeedVersion, catalog.ContentHash, rows, now, $"{rows:N0} controls, catalog {catalog.Version}.",
            cancellationToken);

    private sealed record Dataset(string Name, string Hash, Func<SqlSession, DbTransaction, CancellationToken, Task<SeedDatasetResult>> Apply, string? DatasetVersion = null)
    {
        public string Version => DatasetVersion ?? CurrentCatalogVersion;
        public string CurrentCatalogVersion { get; init; } = string.Empty;
    }

    private List<Dataset> Datasets(ControlCatalog catalog)
    {
        var version = catalog.SeedVersion;
        var now = timeProvider.GetUtcNow();
        var referenceRows = catalog.SolutionAreas.Select((area, index) => new object?[] { "SolutionArea", area.Code, Text(area.Name, 200), Text(area.Description, 1000), index * 10 })
            .Concat(catalog.ExperienceAreas.Select((area, index) => new object?[] { "ExperienceArea", area.Code, Text(area.Name, 200), Text(area.Description, 1000), index * 10 }))
            .ToList();
        var detailRows = catalog.Controls.Select(control => DetailRow(control, version)).ToList();
        var aliasRows = catalog.Aliases.Select(alias => new object?[] { Text(alias.Alias, 150), Text(alias.ControlId, 150), Text(alias.Reason, 1000) }).ToList();
        var specifications = catalog.Specifications.Select(spec => (Key: spec.Code, spec.Name, spec.Description, Items: SpecificationItems(catalog, spec))).ToList();
        var baselines = catalog.Baselines.Select(baseline => (Key: baseline.Code, baseline.Name, baseline.Description, baseline.IsActive, Items: BaselineItems(baseline))).ToList();

        var eaas = EaasRows(catalog);
        List<Dataset> list =
        [
            new Dataset(SeedDatasets.EaasReference, Hash(eaas.SelectMany(table => table.Rows.Select(row => row.Prepend(table.Table).ToArray()))),
                (session, transaction, token) => SeedEaasReferenceAsync(session, transaction, eaas, token), EaasReferenceData.Version),
            new Dataset(SeedDatasets.ReferenceValues, Hash(referenceRows), (session, transaction, token) =>
                UpsertAsync(session, transaction, SeedDatasets.ReferenceValues, version, "ReferenceValue", ["Category", "Code"], ["Name", "Description", "SortOrder"], referenceRows,
                    deleteMissing: true, token)),
            new Dataset(SeedDatasets.ControlDetails, Hash(detailRows), (session, transaction, token) =>
                UpsertAsync(session, transaction, SeedDatasets.ControlDetails, version, "ControlDetail", ["ControlId"], DetailColumns.Skip(1).ToArray(),
                    detailRows.Select(row => row.Append(session.Date(now)).ToArray()).ToList(), deleteMissing: true, token, extraColumn: "UpdatedAtUtc")),
            new Dataset(SeedDatasets.ControlAliases, Hash(aliasRows), (session, transaction, token) =>
                UpsertAsync(session, transaction, SeedDatasets.ControlAliases, version, "ControlAlias", ["Alias"], ["ControlId", "Reason"], aliasRows, deleteMissing: true, token)),
            new Dataset(SeedDatasets.Specifications, Hash(specifications.Select(spec => new object?[] { spec.Key, spec.Name, spec.Description, string.Join("|", spec.Items) }).ToList()),
                (session, transaction, token) => SeedProfilesAsync(session, transaction, SeedDatasets.Specifications, version, "Specification", "SpecificationKey", "SpecificationItem",
                    specifications.Select(spec => new SystemProfile(spec.Key, spec.Name, spec.Description, null, spec.Items)).ToList(), token)),
            new Dataset(SeedDatasets.Baselines, Hash(baselines.Select(baseline => new object?[] { baseline.Key, baseline.Name, baseline.Description, baseline.IsActive, string.Join("|", baseline.Items) }).ToList()),
                (session, transaction, token) => SeedProfilesAsync(session, transaction, SeedDatasets.Baselines, version, "Baseline", "BaselineKey", "BaselineItem",
                    baselines.Select(baseline => new SystemProfile(baseline.Key, baseline.Name, baseline.Description, baseline.IsActive, baseline.Items)).ToList(), token)),
        ];
        return list.Select(dataset => dataset with { CurrentCatalogVersion = version }).ToList();
    }

    /// <summary>One EaaS v3 reference table: natural key columns first, then value columns.</summary>
    private sealed record EaasTable(string Schema, string Table, string[] Keys, string[] Values, IReadOnlyList<object?[]> Rows);

    private static List<EaasTable> EaasRows(ControlCatalog catalog) =>
    [
        new("ref", "CodeSet", ["Code"], ["Name", "Description"],
            EaasReferenceData.CodeSetList.Select(set => new object?[] { set.Code, set.Name, set.Description }).ToList()),
        new("ref", "ImplementationStatus", ["Code"], ["Label", "CreditFactor", "CountsAsApplicable", "SortOrder"],
            EaasReferenceData.ImplementationStatuses.Select((status, index) => new object?[] { status.Code, status.Label, status.CreditFactor, status.CountsAsApplicable, index + 1 }).ToList()),
        new("ref", "MaturityLevel", ["Code"], ["Name", "Description"], EaasReferenceData.MaturityLevels.Select(level => new object?[] { level.Code, level.Name, level.Description }).ToList()),
        new("ref", "RelationshipType", ["Code"], ["Name", "CountsAsSatisfies", "Description"],
            EaasReferenceData.RelationshipTypes.Select(type => new object?[] { type.Code, type.Name, type.CountsAsSatisfies, type.Description }).ToList()),
        new("ref", "EntityType", ["Code"], ["Name", "TableName"], EaasReferenceData.EntityTypes.Select(type => new object?[] { type.Code, type.Name, type.Description }).ToList()),
        new("svc", "ServiceTier", ["Code"], ["Name", "Tagline", "Description", "SortOrder"],
            EaasReferenceData.ServiceTiers.Select((tier, index) => new object?[] { tier.Code, tier.Name, tier.Tagline, tier.Description, index + 1 }).ToList()),
        new("sec", "AppRole", ["Code"], ["Name"], EaasReferenceData.AppRoles.Select(role => new object?[] { role.Code, role.Name }).ToList()),
        new("eux", "ExperienceArea", ["Code"], ["Name", "Description", "SortOrder"],
            catalog.ExperienceAreas.Select((area, index) => new object?[] { Text(area.Code, 30), Text(area.Name, 100), Text(area.Description, 1000), index + 1 }).ToList()),
        // Code values last: they need the CodeSetId of their set (resolved in SeedEaasReferenceAsync).
        new("ref", "CodeValue", ["CodeSet", "Code"], ["Label", "Description", "SortOrder", "IsActive"],
            EaasReferenceData.CodeSetList.SelectMany(set => set.Values.Select((value, index) => new object?[] { set.Code, value.Code, value.Label, value.Description, index + 1, true })).ToList()),
    ];

    /// <summary>Upserts the EaaS v3 reference tables by natural code. Rows users added are kept (never deleted).</summary>
    private static async Task<SeedDatasetResult> SeedEaasReferenceAsync(SqlSession session, DbTransaction transaction, IReadOnlyList<EaasTable> tables, CancellationToken cancellationToken)
    {
        int inserted = 0, updated = 0, total = 0;
        foreach (var table in tables)
        {
            var rows = table.Rows;
            var keys = table.Keys;
            if (table.Table == "CodeValue")
            {
                var sets = (await QueryAsync(session, transaction, $"SELECT CodeSetId, Code FROM {session.Table("ref", "CodeSet")};", null, cancellationToken))
                    .Where(row => row["Code"] is string).ToDictionary(row => (string)row["Code"]!, row => Convert.ToInt32(row["CodeSetId"], CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase);
                rows = rows.Where(row => sets.ContainsKey((string)row[0]!)).Select(row => row.Skip(1).Prepend(sets[(string)row[0]!]).ToArray()).ToList();
                keys = ["CodeSetId", "Code"];
            }

            var result = await UpsertAsync(session, transaction, string.Empty, string.Empty, table.Table, keys, table.Values, rows, deleteMissing: false, cancellationToken, schema: table.Schema);
            inserted += result.Inserted;
            updated += result.Updated;
            total += result.Total;
        }

        return new SeedDatasetResult(SeedDatasets.EaasReference, EaasReferenceData.Version, false, inserted, updated, 0, 0, total);
    }

    private static readonly string[] DetailColumns =
    [
        "ControlId", "SeedVersion", "SolutionArea", "ExperienceArea", "VariantOf", "VariantLabel", "ComplianceStandards", "EstimatedMinutes", "Difficulty", "Effort", "RiskLevel",
        "Benefits", "ExperienceImpact", "GraphRequest", "GraphCommand", "PowerShell", "JsonBody", "SettingPath", "GpoPath", "Registry", "Generated"
    ];

    private static object?[] DetailRow(CatalogControl control, string version)
    {
        var standards = control.FrameworkReferences.Where(reference => reference.IsComplianceStandard).Select(reference => reference.Label).Distinct(StringComparer.OrdinalIgnoreCase);
        var implementation = control.Implementation;
        var remediation = control.Remediation;
        return
        [
            Text(control.Id, 150), Text(version, 50), Text(control.SolutionArea, 64), Text(control.ExperienceArea, 64), Text(control.VariantOf, 150), Text(control.VariantLabel, 400),
            Text(string.Join("; ", standards)), implementation.EstimatedMinutes, Text(implementation.Difficulty, 20), Text(implementation.Effort, 20),
            Text(implementation.RiskLevel, 20), Text(implementation.Benefits, 1000), Text(implementation.ExperienceImpact, 2000), Text(remediation.GraphRequest),
            Text(remediation.GraphCommand), Text(remediation.PowerShell), Text(remediation.JsonBody), Text(remediation.SettingPath, 2000), Text(remediation.GpoPath, 2000),
            Text(remediation.Registry, 2000), Text(string.Join(", ", control.Generated), 1000)
        ];
    }

    private static List<string> SpecificationItems(ControlCatalog catalog, CatalogSpecification spec)
    {
        var items = new List<string>();
        items.AddRange(spec.Experiences.Select(code => $"{ProfileItemTypes.Experience}:{code}"));
        var references = spec.Frameworks.Select(FrameworkReference.Parse).ToArray();
        items.AddRange(references.Where(reference => !reference.IsComplianceStandard).Select(reference => $"{ProfileItemTypes.Framework}:{reference.FrameworkCode}").Distinct());
        items.AddRange(references.Where(reference => reference.IsComplianceStandard).Select(reference => $"{ProfileItemTypes.ComplianceStandard}:{reference.FrameworkCode}").Distinct());
        items.AddRange(references.Select(reference => $"{ProfileItemTypes.Requirement}:{reference.Label}").Distinct());
        items.AddRange(spec.PolicySets.Select(set => $"{ProfileItemTypes.PolicySet}:{set}"));
        items.AddRange(spec.ControlIds.Where(id => catalog.Find(id) is not null).Select(id => $"{ProfileItemTypes.Control}:{id}"));
        return items.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> BaselineItems(CatalogBaseline baseline) =>
        baseline.PolicySets.Select(set => $"{ProfileItemTypes.PolicySet}:{set}").Concat(baseline.ControlIds.Select(id => $"{ProfileItemTypes.Control}:{id}"))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private sealed record SystemProfile(string Key, string Name, string Description, bool? IsActive, IReadOnlyList<string> Items);

    /// <summary>Upserts system specifications or baselines. Rows changed by users (IsModified = 1) and their items are left alone; IsActive is only set on insert.</summary>
    private async Task<SeedDatasetResult> SeedProfilesAsync(SqlSession session, DbTransaction transaction, string dataset, string version, string table, string keyColumn,
        string itemTable, IReadOnlyList<SystemProfile> profiles, CancellationToken cancellationToken)
    {
        var target = session.Table("secureplus", table);
        var items = session.Table("secureplus", itemTable);
        var baseline = table == "Baseline";
        var existing = (await QueryAsync(session, transaction, $"SELECT {keyColumn} AS K, IsModified AS M, IsSystem AS S FROM {target};", null, cancellationToken))
            .ToDictionary(row => (string)row["K"]!, row => (Modified: Convert.ToBoolean(row["M"], CultureInfo.InvariantCulture), System: Convert.ToBoolean(row["S"], CultureInfo.InvariantCulture)),
                StringComparer.OrdinalIgnoreCase);
        var now = session.Date(timeProvider.GetUtcNow());
        int inserted = 0, updated = 0, protectedRows = 0;
        foreach (var profile in profiles)
        {
            if (existing.TryGetValue(profile.Key, out var state))
            {
                if (state.Modified || !state.System)
                {
                    protectedRows++;
                    continue;
                }

                await ExecuteAsync(session, transaction,
                    $"UPDATE {target} SET Name = @name, Description = @description, SeedVersion = @version, ModifiedBy = @by, ModifiedAtUtc = @at WHERE {keyColumn} = @key;",
                    Parameters(("@name", Text(profile.Name, 200)), ("@description", Text(profile.Description, 2000)), ("@version", version), ("@by", SeedActor), ("@at", now),
                        ("@key", profile.Key)), cancellationToken);
                await ExecuteAsync(session, transaction, $"DELETE FROM {items} WHERE {keyColumn} = @key;", Parameters(("@key", profile.Key)), cancellationToken);
                updated++;
            }
            else
            {
                var sql = baseline
                    ? $"INSERT INTO {target} (BaselineKey, Name, Description, IsActive, IsSystem, IsModified, SeedVersion, CreatedBy, CreatedAtUtc) VALUES (@key, @name, @description, @active, @true, @false, @version, @by, @at);"
                    : $"INSERT INTO {target} (SpecificationKey, Name, Description, IsStandard, IsSystem, IsModified, StatusCode, SeedVersion, CreatedBy, CreatedAtUtc) VALUES (@key, @name, @description, @true, @true, @false, 'Approved', @version, @by, @at);";
                await ExecuteAsync(session, transaction, sql,
                    Parameters(("@key", profile.Key), ("@name", Text(profile.Name, 200)), ("@description", Text(profile.Description, 2000)), ("@active", session.Bool(profile.IsActive == true)),
                        ("@true", session.Bool(true)), ("@false", session.Bool(false)), ("@version", version), ("@by", SeedActor), ("@at", now)), cancellationToken);
                inserted++;
            }

            await BulkInsertAsync(session, transaction, items, [keyColumn, "ItemType", "ItemKey"],
                profile.Items.Select(item => item.Split(':', 2)).Select(parts => new object?[] { profile.Key, parts[0], Text(parts[1], 200) }).ToList(), null, cancellationToken);
        }

        var total = await CountAsync(session, transaction, $"SELECT COUNT(*) FROM {target};", null, cancellationToken);
        return new SeedDatasetResult(dataset, version, false, inserted, updated, 0, protectedRows, total);
    }

    /// <summary>Upserts reference rows by key; rows no longer shipped are removed when <paramref name="deleteMissing"/>.</summary>
    private static async Task<SeedDatasetResult> UpsertAsync(SqlSession session, DbTransaction transaction, string dataset, string version, string table, string[] keyColumns,
        string[] valueColumns, IReadOnlyList<object?[]> rows, bool deleteMissing, CancellationToken cancellationToken, string? extraColumn = null, string schema = "secureplus")
    {
        var target = session.Table(schema, table);
        var values = extraColumn is null ? valueColumns : [.. valueColumns, extraColumn];
        var existing = (await QueryAsync(session, transaction, $"SELECT {string.Join(", ", keyColumns)} FROM {target};", null, cancellationToken))
            .Select(row => Key(keyColumns.Select(column => row[column]))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inserts = new List<object?[]>();
        var updates = new List<object?[]>();
        foreach (var row in rows)
        {
            var key = Key(row.Take(keyColumns.Length));
            if (!shipped.Add(key)) continue;
            if (existing.Contains(key)) updates.Add([.. row.Skip(keyColumns.Length), .. row.Take(keyColumns.Length)]);
            else inserts.Add(row);
        }

        await BulkInsertAsync(session, transaction, target, [.. keyColumns, .. values], inserts, null, cancellationToken);
        await BulkUpdateAsync(session, transaction, target, keyColumns, values, updates, cancellationToken);
        var deleted = 0;
        if (deleteMissing && keyColumns.Length == 1)
        {
            var missing = existing.Where(key => !shipped.Contains(key)).Cast<object>().ToList();
            await DeleteKeysAsync(session, transaction, target, keyColumns[0], missing, cancellationToken);
            deleted = missing.Count;
        }
        else if (deleteMissing)
        {
            foreach (var key in existing.Where(key => !shipped.Contains(key)))
            {
                var parts = key.Split('\u001f');
                await ExecuteAsync(session, transaction, $"DELETE FROM {target} WHERE {string.Join(" AND ", keyColumns.Select((column, index) => $"{column} = @k{index}"))};",
                    Parameters(parts.Select((part, index) => ($"@k{index}", (object?)part)).ToArray()), cancellationToken);
                deleted++;
            }
        }

        return new SeedDatasetResult(dataset, version, false, inserts.Count, updates.Count, deleted, 0, shipped.Count);
    }

    private static string Key(IEnumerable<object?> parts) => string.Join('\u001f', parts.Select(part => Convert.ToString(part, CultureInfo.InvariantCulture) ?? string.Empty));

    private static string Hash(IEnumerable<object?[]> rows)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var row in rows)
            hash.AppendData(Encoding.UTF8.GetBytes(string.Join('\u001f', row.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "␀")) + "\u001e"));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private async Task RecordVersionAsync(SqlSession session, DbTransaction transaction, string dataset, string version, string hash, SeedDatasetResult result, bool exists,
        CancellationToken cancellationToken) =>
        await UpsertVersionAsync(session, transaction, dataset, version, hash, result.Total, timeProvider.GetUtcNow(), result.Describe(), cancellationToken, exists);

    internal static async Task UpsertVersionAsync(SqlSession session, DbTransaction transaction, string dataset, string version, string hash, int rows, DateTimeOffset now,
        string summary, CancellationToken cancellationToken, bool? exists = null)
    {
        var table = session.Table("secureplus", "SeedVersion");
        exists ??= await CountAsync(session, transaction, $"SELECT COUNT(*) FROM {table} WHERE Dataset = @dataset;", Parameters(("@dataset", dataset)), cancellationToken) > 0;
        var sql = exists.Value
            ? $"UPDATE {table} SET Version = @version, ContentHash = @hash, RowsApplied = @rows, AppliedAtUtc = @at, AppliedBy = @by, Summary = @summary WHERE Dataset = @dataset;"
            : $"INSERT INTO {table} (Dataset, Version, ContentHash, RowsApplied, AppliedAtUtc, AppliedBy, Summary) VALUES (@dataset, @version, @hash, @rows, @at, @by, @summary);";
        await ExecuteAsync(session, transaction, sql,
            Parameters(("@dataset", dataset), ("@version", version), ("@hash", hash), ("@rows", rows), ("@at", session.Date(now)), ("@by", Environment.UserName),
                ("@summary", Text(summary, 2000))), cancellationToken);
    }

    private static async Task<Dictionary<string, SeedVersionInfo>> ReadVersionsAsync(SqlSession session, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(session, transaction,
            $"SELECT Dataset, Version, ContentHash, RowsApplied, AppliedAtUtc, AppliedBy, Summary FROM {session.Table("secureplus", "SeedVersion")};", null, cancellationToken);
        return rows.Select(row => new SeedVersionInfo((string)row["Dataset"]!, (string)row["Version"]!, (string)row["ContentHash"]!,
                Convert.ToInt32(row["RowsApplied"], CultureInfo.InvariantCulture), SqlSession.ReadDate(row["AppliedAtUtc"]!), (string)row["AppliedBy"]!, row["Summary"] as string))
            .ToDictionary(row => row.Dataset, StringComparer.OrdinalIgnoreCase);
    }

    private Task<SqlSession> OpenAsync(CancellationToken cancellationToken) =>
        SqlSession.OpenAsync(database, connections, message => new InvalidOperationException(message), cancellationToken);
}
