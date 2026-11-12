using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Infrastructure.Database;

namespace EngineeringWorkspace.Infrastructure.Persistence;

/// <summary>
/// Loads the embedded control catalog into the application database (SQL Server, Azure SQL or SQLite), in one
/// transaction:
/// <list type="bullet">
/// <item>pol.Repository "SECUREPLUS-CATALOG" with one pol.RepositoryVersion per catalog version (StatusCode Current);</item>
/// <item>pol.PolicyType per control kind, pol.Category per kind and category, pol.Artifact per control (Graph body in
/// ContentJson), pol.ArtifactSetting per expected setting;</item>
/// <item>pol.PolicySet, pol.PolicySetMember, pol.PolicySetInclude and pol.PolicySetPrerequisite per baseline;</item>
/// <item>fw.Framework, fw.FrameworkVersion and fw.Requirement per framework reference, with pol.ArtifactMapping rows
/// (ref.MappingSource "GDS-CATALOG") from each control to its requirements;</item>
/// <item>gds.Standard and gds.StandardItem for the Secure Plus standards, the Microsoft security baselines and the
/// assessment checks;</item>
/// <item>secureplus.CatalogControl: the flat, filterable catalog with the full control ID and its pol.Artifact.</item>
/// </list>
/// The previous catalog version is removed (or kept as Superseded when other data references it). Reference rows
/// (policy types, categories, frameworks, requirements, standards) are reused by their natural keys, so a sync is
/// idempotent and never duplicates. All SQL is parameterized.
/// </summary>
public sealed class SqlCatalogStore(IDatabaseStatusService database, DatabaseConnectionFactory connections, TimeProvider timeProvider) : ICatalogDatabaseStore
{
    public const string RepositoryCode = "SECUREPLUS-CATALOG";
    public const string MappingSourceCode = "GDS-CATALOG";
    public const string CurrentStatus = "Current";
    public const string SupersededStatus = "Superseded";

    // SQL Server allows 2,100 parameters per command; SQLite 32,766. Stay well below both.
    private const int MaxParameters = 900;

    // A NULL for a VARBINARY column: SQL Server cannot convert an untyped (NVARCHAR) NULL to VARBINARY.
    private static readonly object NullBinary = new();

    private static readonly (string Kind, string Code, string Name, string Notes)[] Standards =
    [
        (CatalogKinds.Standard, "SP-STANDARDS", "Secure Plus Standards Baseline", "GDS Secure Plus standards (SecurePlusStandards.xlsx, Standards_Baseline_WIP)."),
        (CatalogKinds.MicrosoftBaseline, "MS-SECURITY-BASELINES", "Microsoft Security Baselines", "Microsoft security baseline settings (Security Compliance Toolkit)."),
        (CatalogKinds.AssessmentCheck, "SP-ASSESSMENT-CHECKS", "Secure Plus Assessment Checks", "Cloud Capsule assessment checks (GDS Baseline Settings Map)."),
        (CatalogKinds.TenantSetting, "SP-TENANT-SETTINGS", "Secure Plus Tenant Settings", "Microsoft 365 and Azure tenant settings (Documents TenantSettings).")
    ];

    public bool IsAvailable =>
        database.Settings.Provider != DatabaseProvider.None && database.LatestStatus?.State == DatabaseOverallState.Ready;

    public async Task<CatalogDatabaseState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        return await ReadStateAsync(session, null, cancellationToken);
    }

    public async Task<CatalogSyncSummary> SyncAsync(ControlCatalog catalog, bool force, IProgress<(double Progress, string Activity)>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var started = timeProvider.GetTimestamp();
        await using var session = await OpenAsync(cancellationToken);
        session.CommandTimeoutSeconds = Math.Max(session.CommandTimeoutSeconds, 300);
        if (!force && (await ReadStateAsync(session, null, cancellationToken)).IsCurrent(catalog))
        {
            progress?.Report((100, "The database already holds this catalog."));
            return new CatalogSyncSummary(true, catalog.Version, catalog.Controls.Count, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, timeProvider.GetElapsedTime(started));
        }

        var now = timeProvider.GetUtcNow();
        var controls = catalog.Controls.GroupBy(control => control.Id, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
        await using var transaction = await session.Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // 1. Repository and version; the previous version is superseded and, where nothing else references it, removed.
        progress?.Report((2, "Preparing the catalog repository..."));
        var repositoryId = await EnsureRepositoryAsync(session, transaction, catalog, cancellationToken);
        await SupersedePreviousVersionsAsync(session, transaction, repositoryId, cancellationToken);
        var versionId = await InsertVersionAsync(session, transaction, repositoryId, catalog, controls.Length, now, cancellationToken);

        // 2. Policy types, categories, artifacts and their expected settings.
        progress?.Report((5, "Writing policy types and categories..."));
        var policyTypes = await EnsurePolicyTypesAsync(session, transaction, controls, cancellationToken);
        var categories = await EnsureCategoriesAsync(session, transaction, controls, policyTypes, cancellationToken);
        var artifactCodes = ArtifactCodes(controls);
        var artifactRows = controls.Select(control => ArtifactRow(session, versionId, categories[CategoryKey(control)], artifactCodes[control.Id], control)).ToList();
        await BulkInsertAsync(session, transaction, session.Table("pol", "Artifact"),
            ["RepositoryVersionId", "CategoryId", "Code", "PolicyNumber", "FileName", "RelativePath", "DisplayNameTemplate", "ConfiguredValueLabel", "DefaultStateCode",
             "PlatformCode", "Technologies", "IsTemplateOnly", "ContentJson", "ContentHash", "LeafSettingCount", "StatusCode", "LicenseText", "Notes"],
            artifactRows, (done, total) => progress?.Report((5 + 40.0 * done / total, $"Writing policies: {done:N0} of {total:N0}...")), cancellationToken);
        var artifactIdsByCode = await ReadMapAsync(session, transaction,
            $"SELECT Code, ArtifactId FROM {session.Table("pol", "Artifact")} WHERE RepositoryVersionId = @version;", versionId, cancellationToken);
        var artifacts = controls.ToDictionary(control => control.Id, control => artifactIdsByCode[artifactCodes[control.Id]], StringComparer.OrdinalIgnoreCase);

        var settingRows = new List<object?[]>();
        foreach (var control in controls)
        {
            for (var index = 0; index < control.Expected.Count; index++)
            {
                var expected = control.Expected[index];
                settingRows.Add([artifacts[control.Id], Text(expected.Path, 800), Text(expected.Path, 400), expected.Value.GetRawText(), Text(Display(expected.Value), 400),
                    session.Bool(index == 0), session.Bool(false)]);
            }
        }

        await BulkInsertAsync(session, transaction, session.Table("pol", "ArtifactSetting"),
            ["ArtifactId", "JsonPath", "SettingDefinitionId", "ValueRaw", "ValueDisplay", "IsPrimary", "IsMergeParent"],
            settingRows, (done, total) => progress?.Report((45 + 10.0 * done / total, $"Writing expected settings: {done:N0} of {total:N0}...")), cancellationToken);

        // 3. Policy sets (baselines), their members, included sets and prerequisites.
        progress?.Report((55, "Writing policy sets..."));
        var sets = catalog.PolicySets.GroupBy(set => set.Id, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
        await BulkInsertAsync(session, transaction, session.Table("pol", "PolicySet"),
            ["RepositoryVersionId", "Code", "DisplayName", "Description", "DeploymentOrder", "SetKindCode", "IsApprovedBaseline"],
            sets.Select(set => new object?[]
            {
                versionId, Text(set.Id, 80), Text(set.DisplayName, 200), Text(set.Description, 1000), set.DeploymentOrder, set.Includes.Count > 0 ? "Composite" : "Baseline",
                session.Bool(string.Equals(set.Id, "Golden-Tenant", StringComparison.OrdinalIgnoreCase))
            }).ToList(), null, cancellationToken);
        var setIds = await ReadMapAsync(session, transaction,
            $"SELECT Code, PolicySetId FROM {session.Table("pol", "PolicySet")} WHERE RepositoryVersionId = @version;", versionId, cancellationToken);
        var memberRows = new List<object?[]>();
        var includeRows = new List<object?[]>();
        var prerequisiteRows = new List<object?[]>();
        foreach (var set in sets)
        {
            var setId = setIds[Text(set.Id, 80)!];
            var sequence = 0;
            foreach (var controlId in set.ControlIds.Distinct(StringComparer.OrdinalIgnoreCase))
                if (artifacts.TryGetValue(controlId, out var artifactId)) memberRows.Add([setId, artifactId, ++sequence]);
            sequence = 0;
            foreach (var include in set.Includes.Distinct(StringComparer.OrdinalIgnoreCase))
                if (setIds.TryGetValue(Text(include, 80)!, out var childId) && childId != setId) includeRows.Add([setId, childId, ++sequence]);
            sequence = 0;
            foreach (var requirement in set.Requires.Where(text => !string.IsNullOrWhiteSpace(text)))
                prerequisiteRows.Add([setId, ++sequence, Text(requirement, 500), "Checklist"]);
        }

        await BulkInsertAsync(session, transaction, session.Table("pol", "PolicySetMember"), ["PolicySetId", "ArtifactId", "Sequence"], memberRows, null, cancellationToken);
        await BulkInsertAsync(session, transaction, session.Table("pol", "PolicySetInclude"), ["ParentPolicySetId", "ChildPolicySetId", "Sequence"], includeRows, null, cancellationToken);
        await BulkInsertAsync(session, transaction, session.Table("pol", "PolicySetPrerequisite"), ["PolicySetId", "Sequence", "Text", "PrerequisiteTypeCode"],
            prerequisiteRows, null, cancellationToken);

        // 4. Frameworks, versions, requirements and the control-to-requirement mappings.
        progress?.Report((70, "Writing frameworks and requirements..."));
        var references = controls.SelectMany(control => control.Frameworks).Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(FrameworkReference.Parse).ToArray();
        var requirementIds = await EnsureRequirementsAsync(session, transaction, references, cancellationToken);
        var mappingSourceId = await EnsureMappingSourceAsync(session, transaction, cancellationToken);
        var mappingRows = new List<object?[]>();
        foreach (var control in controls)
        {
            foreach (var label in control.Frameworks.Where(label => !string.IsNullOrWhiteSpace(label)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var reference = FrameworkReference.Parse(label);
                if (!requirementIds.TryGetValue(RequirementKey(reference), out var requirementId)) continue;
                var authoritative = reference.FrameworkCode == "MS-SECURITY-BASELINE";
                mappingRows.Add([artifacts[control.Id], requirementId, mappingSourceId, authoritative ? "Source" : "Keyword", authoritative ? "High" : "Indicative",
                    authoritative ? "From the Microsoft security baseline workbook." : null]);
            }
        }

        await BulkInsertAsync(session, transaction, session.Table("pol", "ArtifactMapping"),
            ["ArtifactId", "RequirementId", "MappingSourceId", "MethodCode", "ConfidenceCode", "Notes"],
            mappingRows, (done, total) => progress?.Report((72 + 16.0 * done / total, $"Writing framework mappings: {done:N0} of {total:N0}...")), cancellationToken);

        // 5. Standards and standard items, then the flat catalog table.
        progress?.Report((90, "Writing standards..."));
        var standardItems = await WriteStandardsAsync(session, transaction, catalog, controls, cancellationToken);

        progress?.Report((93, "Writing the catalog table..."));
        await ExecuteAsync(session, transaction, $"DELETE FROM {session.Table("secureplus", "CatalogControl")};", null, cancellationToken);
        await BulkInsertAsync(session, transaction, session.Table("secureplus", "CatalogControl"),
            ["ControlId", "ArtifactId", "CatalogVersion", "Number", "Kind", "KindName", "Category", "Platform", "Name", "Setting", "Value", "Description", "Source", "Frameworks",
             "Severity", "UserImpact", "Experience", "PainPoints", "Evaluation", "IsAutomatic", "PolicySets", "Path", "GraphResource", "OdataType", "SyncedAtUtc"],
            controls.Select(control => new object?[]
            {
                Text(control.Id, 150), artifacts[control.Id], Text(catalog.Version, 50), Text(control.Number, 20), Text(control.Kind, 30), Text(control.KindDisplayName, 60),
                Text(control.Category, 200), Text(control.Platform, 100), Text(control.Name, 400) ?? control.Id, Text(control.Setting, 400), Text(control.Value),
                Text(control.Description), Text(control.Source, 200), Text(string.Join("; ", control.Frameworks)), Text(control.Severity, 20), Text(control.UserImpact, 50),
                Text(control.Experience, 400), Text(control.PainPoints, 1000), Text(control.Evaluation, 30), session.Bool(control.IsAutomatic),
                Text(string.Join("; ", control.PolicySets), 1000), Text(control.Path, 800), Text(control.GraphResource, 200), Text(control.OdataType, 200), session.Date(now)
            }).ToList(), (done, total) => progress?.Report((93 + 6.0 * done / total, $"Writing the catalog table: {done:N0} of {total:N0}...")), cancellationToken);

        await ExecuteAsync(session, transaction, $"UPDATE {session.Table("pol", "RepositoryVersion")} SET StatusCode = @status WHERE RepositoryVersionId = @version;",
            command =>
            {
                DatabaseConnectionFactory.Parameter(command, "@status", CurrentStatus);
                DatabaseConnectionFactory.Parameter(command, "@version", versionId);
            }, cancellationToken);
        await SqlReferenceSeedStore.RecordCatalogAsync(session, transaction, catalog, controls.Length, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        progress?.Report((100, "Catalog loaded."));

        return new CatalogSyncSummary(false, catalog.Version, controls.Length, policyTypes.Count, categories.Count, settingRows.Count, sets.Length, memberRows.Count,
            references.Select(reference => reference.FrameworkCode).Distinct(StringComparer.OrdinalIgnoreCase).Count(), requirementIds.Count, mappingRows.Count,
            Standards.Count(standard => controls.Any(control => control.Kind == standard.Kind)), standardItems, timeProvider.GetElapsedTime(started));
    }

    private static async Task<CatalogDatabaseState> ReadStateAsync(SqlSession session, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        var version = session.Table("pol", "RepositoryVersion");
        var repository = session.Table("pol", "Repository");
        var sql = $"""
                   SELECT {(session.Sqlite ? string.Empty : "TOP 1")} v.RepositoryVersionId, v.VersionLabel, v.ManifestHash, v.SnapshotAt
                   FROM {version} AS v INNER JOIN {repository} AS r ON r.RepositoryId = v.RepositoryId
                   WHERE r.Code = @code AND v.StatusCode = @status
                   ORDER BY v.RepositoryVersionId DESC{(session.Sqlite ? " LIMIT 1" : string.Empty)};
                   """;
        int versionId;
        string? label, hash;
        DateTimeOffset? syncedAt;
        await using (var command = session.Command(transaction, sql))
        {
            DatabaseConnectionFactory.Parameter(command, "@code", RepositoryCode);
            DatabaseConnectionFactory.Parameter(command, "@status", CurrentStatus);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return new CatalogDatabaseState(true, null, null, null, 0, 0, 0, 0);
            versionId = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            label = reader.IsDBNull(1) ? null : reader.GetString(1);
            hash = reader.IsDBNull(2) ? null : Convert.ToHexString((byte[])reader.GetValue(2)).ToLowerInvariant();
            syncedAt = reader.IsDBNull(3) ? null : SqlSession.ReadDate(reader.GetValue(3));
        }

        var controls = await CountAsync(session, transaction, $"SELECT COUNT(*) FROM {session.Table("secureplus", "CatalogControl")};", null, cancellationToken);
        var sets = await CountAsync(session, transaction, $"SELECT COUNT(*) FROM {session.Table("pol", "PolicySet")} WHERE RepositoryVersionId = @version;", versionId, cancellationToken);
        var frameworks = await CountAsync(session, transaction, $"SELECT COUNT(*) FROM {session.Table("fw", "Framework")};", null, cancellationToken);
        var requirements = await CountAsync(session, transaction, $"SELECT COUNT(*) FROM {session.Table("fw", "Requirement")};", null, cancellationToken);
        return new CatalogDatabaseState(true, label, hash, syncedAt, controls, sets, frameworks, requirements);
    }

    private static async Task<int> EnsureRepositoryAsync(SqlSession session, DbTransaction transaction, ControlCatalog catalog, CancellationToken cancellationToken)
    {
        var table = session.Table("pol", "Repository");
        if (await ScalarIdAsync(session, transaction, $"SELECT RepositoryId FROM {table} WHERE Code = @code;", RepositoryCode, cancellationToken) is { } existing)
            return existing;
        return await InsertIdAsync(session, transaction,
            $"INSERT INTO {table} (Code, Name, StorageTypeCode, RootUrl, DefaultBranch, OwnerRole, Description, IsActive) VALUES (@code, @name, @storage, @url, @branch, @owner, @description, @active);",
            command =>
            {
                DatabaseConnectionFactory.Parameter(command, "@code", RepositoryCode);
                DatabaseConnectionFactory.Parameter(command, "@name", "Secure Plus control catalog");
                DatabaseConnectionFactory.Parameter(command, "@storage", "Embedded");
                DatabaseConnectionFactory.Parameter(command, "@url", Text(SourceUrl(catalog.Source), 1000));
                DatabaseConnectionFactory.Parameter(command, "@branch", "main");
                DatabaseConnectionFactory.Parameter(command, "@owner", "GDS Modern Work");
                DatabaseConnectionFactory.Parameter(command, "@description",
                    "Policies, settings, policy sets, frameworks and standards shipped with Secure Plus Agent (generated from the GDS Documents repository).");
                DatabaseConnectionFactory.Parameter(command, "@active", session.Bool(true));
            }, cancellationToken);
    }

    private static async Task SupersedePreviousVersionsAsync(SqlSession session, DbTransaction transaction, int repositoryId, CancellationToken cancellationToken)
    {
        var versions = new List<(int Id, string? Label)>();
        await using (var command = session.Command(transaction,
                         $"SELECT RepositoryVersionId, VersionLabel FROM {session.Table("pol", "RepositoryVersion")} WHERE RepositoryId = @repository;"))
        {
            DatabaseConnectionFactory.Parameter(command, "@repository", repositoryId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                versions.Add((Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        if (versions.Count == 0) return;
        // The flat table points at the previous artifacts; it is rewritten for the new version.
        await ExecuteAsync(session, transaction, $"DELETE FROM {session.Table("secureplus", "CatalogControl")};", null, cancellationToken);
        foreach (var (id, label) in versions)
        {
            // Labels are unique per repository: a superseded version keeps its label with its ID appended.
            var superseded = label is null || label.Contains('~', StringComparison.Ordinal) ? label : Text($"{label}~{id}", 50);
            await ExecuteAsync(session, transaction,
                $"UPDATE {session.Table("pol", "RepositoryVersion")} SET StatusCode = @status, VersionLabel = @label WHERE RepositoryVersionId = @version;",
                command =>
                {
                    DatabaseConnectionFactory.Parameter(command, "@status", SupersededStatus);
                    DatabaseConnectionFactory.Parameter(command, "@label", superseded);
                    DatabaseConnectionFactory.Parameter(command, "@version", id);
                }, cancellationToken);

            // Remove the superseded version unless other data (for example scores) references its artifacts.
            var savepoint = $"catalog_v{id}";
            await ExecuteAsync(session, transaction, session.Sqlite ? $"SAVEPOINT {savepoint};" : $"SAVE TRANSACTION {savepoint};", null, cancellationToken);
            try
            {
                var artifacts = $"SELECT ArtifactId FROM {session.Table("pol", "Artifact")} WHERE RepositoryVersionId = @version";
                var sets = $"SELECT PolicySetId FROM {session.Table("pol", "PolicySet")} WHERE RepositoryVersionId = @version";
                string[] statements =
                [
                    $"DELETE FROM {session.Table("pol", "ArtifactMapping")} WHERE ArtifactId IN ({artifacts});",
                    $"DELETE FROM {session.Table("pol", "ArtifactSetting")} WHERE ArtifactId IN ({artifacts});",
                    $"DELETE FROM {session.Table("pol", "PolicySetMember")} WHERE PolicySetId IN ({sets});",
                    $"DELETE FROM {session.Table("pol", "PolicySetInclude")} WHERE ParentPolicySetId IN ({sets}) OR ChildPolicySetId IN ({sets});",
                    $"DELETE FROM {session.Table("pol", "PolicySetPrerequisite")} WHERE PolicySetId IN ({sets});",
                    $"DELETE FROM {session.Table("pol", "PolicySet")} WHERE RepositoryVersionId = @version;",
                    $"DELETE FROM {session.Table("pol", "Artifact")} WHERE RepositoryVersionId = @version;",
                    $"DELETE FROM {session.Table("pol", "RepositoryVersion")} WHERE RepositoryVersionId = @version;"
                ];
                foreach (var statement in statements)
                    await ExecuteAsync(session, transaction, statement, command => DatabaseConnectionFactory.Parameter(command, "@version", id), cancellationToken);
                if (session.Sqlite) await ExecuteAsync(session, transaction, $"RELEASE {savepoint};", null, cancellationToken);
            }
            catch (DbException)
            {
                await ExecuteAsync(session, transaction, session.Sqlite ? $"ROLLBACK TO {savepoint};" : $"ROLLBACK TRANSACTION {savepoint};", null, cancellationToken);
            }
        }
    }

    private static Task<int> InsertVersionAsync(SqlSession session, DbTransaction transaction, int repositoryId, ControlCatalog catalog, int fileCount, DateTimeOffset now,
        CancellationToken cancellationToken) =>
        InsertIdAsync(session, transaction,
            $"INSERT INTO {session.Table("pol", "RepositoryVersion")} (RepositoryId, VersionLabel, SourceCommitOrETag, SnapshotAt, FileCount, ManifestHash, StatusCode, Notes) VALUES (@repository, @label, @source, @at, @files, @hash, @status, @notes);",
            command =>
            {
                DatabaseConnectionFactory.Parameter(command, "@repository", repositoryId);
                DatabaseConnectionFactory.Parameter(command, "@label", Text(catalog.Version, 50));
                DatabaseConnectionFactory.Parameter(command, "@source", Text(catalog.Source, 200));
                DatabaseConnectionFactory.Parameter(command, "@at", session.Date(now));
                DatabaseConnectionFactory.Parameter(command, "@files", fileCount);
                DatabaseConnectionFactory.Parameter(command, "@hash", catalog.ContentHash.Length == 64 ? Convert.FromHexString(catalog.ContentHash) : null).DbType = DbType.Binary;
                DatabaseConnectionFactory.Parameter(command, "@status", "Loading");
                DatabaseConnectionFactory.Parameter(command, "@notes", Text(catalog.DerivedFieldsNote, 1000));
            }, cancellationToken);

    private static async Task<Dictionary<string, int>> EnsurePolicyTypesAsync(SqlSession session, DbTransaction transaction, IReadOnlyList<CatalogControl> controls,
        CancellationToken cancellationToken)
    {
        var table = session.Table("pol", "PolicyType");
        var existing = await ReadMapAsync(session, transaction, $"SELECT Code, PolicyTypeId FROM {table} WHERE Code IS NOT NULL;", null, cancellationToken);
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in controls.GroupBy(control => control.Kind, StringComparer.OrdinalIgnoreCase))
        {
            var code = Text(kind.Key, 30)!;
            if (!existing.TryGetValue(code, out var id))
            {
                var resource = kind.Select(control => control.GraphResource).Where(value => value.Length > 0).GroupBy(value => value)
                    .OrderByDescending(group => group.Count()).Select(group => group.Key).FirstOrDefault();
                id = await InsertIdAsync(session, transaction, $"INSERT INTO {table} (Code, Name, GraphResource, GraphApiVersion) VALUES (@code, @name, @resource, @api);", command =>
                {
                    DatabaseConnectionFactory.Parameter(command, "@code", code);
                    DatabaseConnectionFactory.Parameter(command, "@name", Text(CatalogKinds.DisplayName(kind.Key), 200));
                    DatabaseConnectionFactory.Parameter(command, "@resource", Text(resource, 300));
                    DatabaseConnectionFactory.Parameter(command, "@api", resource is null ? null : "beta");
                }, cancellationToken);
            }

            result[kind.Key] = id;
        }

        return result;
    }

    private static async Task<Dictionary<string, int>> EnsureCategoriesAsync(SqlSession session, DbTransaction transaction, IReadOnlyList<CatalogControl> controls,
        IReadOnlyDictionary<string, int> policyTypes, CancellationToken cancellationToken)
    {
        var table = session.Table("pol", "Category");
        var existing = await ReadMapAsync(session, transaction, $"SELECT Code, CategoryId FROM {table} WHERE Code IS NOT NULL;", null, cancellationToken);
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var order = 0;
        foreach (var group in controls.GroupBy(CategoryKey, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            var name = first.Category.Length == 0 ? "General" : first.Category;
            var code = CategoryCode(first.Kind, name);
            order++;
            if (!existing.TryGetValue(code, out var id))
            {
                var sortOrder = order;
                id = await InsertIdAsync(session, transaction,
                    $"INSERT INTO {table} (PolicyTypeId, Code, Name, FolderPath, IntuneTemplateFamily, SortOrder) VALUES (@type, @code, @name, @folder, @family, @order);", command =>
                    {
                        DatabaseConnectionFactory.Parameter(command, "@type", policyTypes[first.Kind]);
                        DatabaseConnectionFactory.Parameter(command, "@code", code);
                        DatabaseConnectionFactory.Parameter(command, "@name", Text(name, 200));
                        DatabaseConnectionFactory.Parameter(command, "@folder", Text($"{first.Kind}/{name}", 400));
                        DatabaseConnectionFactory.Parameter(command, "@family", Text(group.Select(control => control.TemplateFamily).FirstOrDefault(value => value.Length > 0), 100));
                        DatabaseConnectionFactory.Parameter(command, "@order", Math.Min(sortOrder, short.MaxValue));
                    }, cancellationToken);
            }

            result[group.Key] = id;
        }

        return result;
    }

    private static async Task<Dictionary<string, int>> EnsureRequirementsAsync(SqlSession session, DbTransaction transaction, IReadOnlyList<FrameworkReference> references,
        CancellationToken cancellationToken)
    {
        var frameworkTable = session.Table("fw", "Framework");
        var versionTable = session.Table("fw", "FrameworkVersion");
        var requirementTable = session.Table("fw", "Requirement");
        var frameworks = await ReadMapAsync(session, transaction, $"SELECT Code, MIN(FrameworkId) FROM {frameworkTable} WHERE Code IS NOT NULL GROUP BY Code;", null, cancellationToken);
        foreach (var framework in references.GroupBy(reference => reference.FrameworkCode, StringComparer.OrdinalIgnoreCase))
        {
            if (frameworks.ContainsKey(framework.Key)) continue;
            var first = framework.First();
            frameworks[framework.Key] = await InsertIdAsync(session, transaction,
                $"INSERT INTO {frameworkTable} (Code, Name, ShortName, Publisher, FrameworkTypeCode, IsHub, Description) VALUES (@code, @name, @short, @publisher, @type, @hub, @description);",
                command =>
                {
                    DatabaseConnectionFactory.Parameter(command, "@code", Text(first.FrameworkCode, 80));
                    DatabaseConnectionFactory.Parameter(command, "@name", Text(first.FrameworkName, 400));
                    DatabaseConnectionFactory.Parameter(command, "@short", Text(first.FrameworkCode, 100));
                    DatabaseConnectionFactory.Parameter(command, "@publisher", Text(first.Publisher, 200));
                    DatabaseConnectionFactory.Parameter(command, "@type", first.FrameworkCode == "MS-SECURITY-BASELINE" ? "Baseline" : "Standard");
                    DatabaseConnectionFactory.Parameter(command, "@hub", session.Bool(false));
                    DatabaseConnectionFactory.Parameter(command, "@description", "Referenced by the Secure Plus control catalog.");
                }, cancellationToken);
        }

        var versions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using (var command = session.Command(transaction, $"SELECT FrameworkId, VersionLabel, FrameworkVersionId FROM {versionTable} WHERE VersionLabel IS NOT NULL;"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                versions.TryAdd($"{Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)}|{reader.GetString(1)}", Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture));
        }

        foreach (var version in references.GroupBy(reference => $"{frameworks[reference.FrameworkCode]}|{Text(reference.Version, 50)}", StringComparer.OrdinalIgnoreCase))
        {
            if (versions.ContainsKey(version.Key)) continue;
            var first = version.First();
            versions[version.Key] = await InsertIdAsync(session, transaction,
                $"INSERT INTO {versionTable} (FrameworkId, VersionLabel, LifecycleStatusCode, HasFullText, Notes) VALUES (@framework, @label, @status, @full, @notes);", command =>
                {
                    DatabaseConnectionFactory.Parameter(command, "@framework", frameworks[first.FrameworkCode]);
                    DatabaseConnectionFactory.Parameter(command, "@label", Text(first.Version, 50));
                    DatabaseConnectionFactory.Parameter(command, "@status", "Active");
                    DatabaseConnectionFactory.Parameter(command, "@full", session.Bool(false));
                    DatabaseConnectionFactory.Parameter(command, "@notes", "Requirements referenced by the Secure Plus control catalog (codes and titles only).");
                }, cancellationToken);
        }

        var requirements = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using (var command = session.Command(transaction, $"SELECT FrameworkVersionId, RequirementCode, RequirementId FROM {requirementTable} WHERE RequirementCode IS NOT NULL;"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                requirements.TryAdd($"{Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)}|{reader.GetString(1)}", Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture));
        }

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<(string Key, FrameworkReference Reference, int VersionId)>();
        foreach (var reference in references)
        {
            var versionId = versions[$"{frameworks[reference.FrameworkCode]}|{Text(reference.Version, 50)}"];
            var stored = $"{versionId}|{Text(reference.RequirementCode, 100)}";
            if (requirements.TryGetValue(stored, out var id)) result[RequirementKey(reference)] = id;
            else if (missing.All(item => !string.Equals(item.Key, stored, StringComparison.OrdinalIgnoreCase))) missing.Add((stored, reference, versionId));
        }

        await BulkInsertAsync(session, transaction, requirementTable,
            ["FrameworkVersionId", "RequirementCode", "MatchKey", "SortKey", "HierarchyLevelCode", "Title", "IsAssessable", "IsStub", "IsNewInVersion", "IsWithdrawn", "SourceRowRef"],
            missing.Select(item => new object?[]
            {
                item.VersionId, Text(item.Reference.RequirementCode, 100), Text(item.Reference.RequirementCode.ToUpperInvariant(), 100), Text(item.Reference.RequirementCode, 100),
                "Control", Text(item.Reference.Label, 500), session.Bool(true), session.Bool(true), session.Bool(false), session.Bool(false), "Secure Plus catalog"
            }).ToList(), null, cancellationToken);
        if (missing.Count > 0)
        {
            await using var command = session.Command(transaction, $"SELECT FrameworkVersionId, RequirementCode, RequirementId FROM {requirementTable} WHERE RequirementCode IS NOT NULL;");
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                requirements.TryAdd($"{Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)}|{reader.GetString(1)}", Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture));
            foreach (var item in missing) result[RequirementKey(item.Reference)] = requirements[item.Key];
        }

        return result;
    }

    private static async Task<int> EnsureMappingSourceAsync(SqlSession session, DbTransaction transaction, CancellationToken cancellationToken)
    {
        var table = session.Table("ref", "MappingSource");
        if (await ScalarIdAsync(session, transaction, $"SELECT MIN(MappingSourceId) FROM {table} WHERE Code = @code;", MappingSourceCode, cancellationToken) is { } existing)
            return existing;
        return await InsertIdAsync(session, transaction,
            $"INSERT INTO {table} (Code, Name, Publisher, AuthorityLevelCode, DefaultConfidence) VALUES (@code, @name, @publisher, @authority, @confidence);", command =>
            {
                DatabaseConnectionFactory.Parameter(command, "@code", MappingSourceCode);
                DatabaseConnectionFactory.Parameter(command, "@name", "GDS Secure Plus catalog crosswalk");
                DatabaseConnectionFactory.Parameter(command, "@publisher", "GDS");
                DatabaseConnectionFactory.Parameter(command, "@authority", "Internal");
                DatabaseConnectionFactory.Parameter(command, "@confidence", 50);
            }, cancellationToken);
    }

    private static async Task<int> WriteStandardsAsync(SqlSession session, DbTransaction transaction, ControlCatalog catalog, IReadOnlyList<CatalogControl> controls,
        CancellationToken cancellationToken)
    {
        var standardTable = session.Table("gds", "Standard");
        var itemTable = session.Table("gds", "StandardItem");
        var written = 0;
        foreach (var standard in Standards)
        {
            var items = controls.Where(control => control.Kind == standard.Kind).ToArray();
            if (items.Length == 0) continue;
            int standardId;
            if (await ScalarIdAsync(session, transaction, $"SELECT StandardId FROM {standardTable} WHERE Code = @code;", standard.Code, cancellationToken) is { } existing)
            {
                standardId = existing;
                await ExecuteAsync(session, transaction, $"UPDATE {standardTable} SET Name = @name, Version = @version, StatusCode = @status, Notes = @notes WHERE StandardId = @id;",
                    command => StandardParameters(command, standard, catalog, standardId), cancellationToken);
            }
            else
            {
                standardId = await InsertIdAsync(session, transaction,
                    $"INSERT INTO {standardTable} (Code, Name, Version, StatusCode, OwnerName, Notes) VALUES (@code, @name, @version, @status, @owner, @notes);", command =>
                    {
                        StandardParameters(command, standard, catalog, null);
                        DatabaseConnectionFactory.Parameter(command, "@code", standard.Code);
                        DatabaseConnectionFactory.Parameter(command, "@owner", "GDS Modern Work");
                    }, cancellationToken);
            }

            // Items are replaced; when other data references them they are updated in place instead.
            var current = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var savepoint = $"standard_{standardId}";
            await ExecuteAsync(session, transaction, session.Sqlite ? $"SAVEPOINT {savepoint};" : $"SAVE TRANSACTION {savepoint};", null, cancellationToken);
            try
            {
                await ExecuteAsync(session, transaction, $"DELETE FROM {itemTable} WHERE StandardId = @id;", command => DatabaseConnectionFactory.Parameter(command, "@id", standardId),
                    cancellationToken);
                if (session.Sqlite) await ExecuteAsync(session, transaction, $"RELEASE {savepoint};", null, cancellationToken);
            }
            catch (DbException)
            {
                await ExecuteAsync(session, transaction, session.Sqlite ? $"ROLLBACK TO {savepoint};" : $"ROLLBACK TRANSACTION {savepoint};", null, cancellationToken);
                current = await ReadMapAsync(session, transaction, $"SELECT ItemCode, StandardItemId FROM {itemTable} WHERE StandardId = @version AND ItemCode IS NOT NULL;",
                    standardId, cancellationToken);
            }

            var rows = new List<object?[]>();
            var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < items.Length; index++)
            {
                var row = StandardItemRow(session, standardId, index + 1, items[index], codes);
                if (current.TryGetValue((string)row[1]!, out var itemId))
                {
                    await ExecuteAsync(session, transaction,
                        $"UPDATE {itemTable} SET {string.Join(", ", StandardItemColumns.Skip(2).Select((column, position) => $"{column} = @v{position}"))} WHERE StandardItemId = @id;",
                        command =>
                        {
                            for (var position = 2; position < row.Length; position++) DatabaseConnectionFactory.Parameter(command, $"@v{position - 2}", row[position]);
                            DatabaseConnectionFactory.Parameter(command, "@id", itemId);
                        }, cancellationToken);
                }
                else
                {
                    rows.Add(row);
                }
            }

            await BulkInsertAsync(session, transaction, itemTable, StandardItemColumns, rows, null, cancellationToken);
            written += items.Length;
        }

        return written;
    }

    private static readonly string[] StandardItemColumns =
    [
        "StandardId", "ItemCode", "Sequence", "PhaseGroupRef", "TaskNum", "TaskName", "GroupAbbrev", "GroupItemNumber", "ControlCode", "SettingCategory", "SettingName",
        "TargetConfiguration", "ConfigurationPath", "DeploymentMethodCode", "ValidationMethod", "EvidenceRequired", "AppliesWhen", "LicenseRequirementText",
        "ImplementationModeCode", "PilotGroup", "Exclusions", "ExceptionCriteria", "RollbackMethod", "ReviewCadence", "SupportOwnerRole", "IsOptional", "EngineeringNotes"
    ];

    private static object?[] StandardItemRow(SqlSession session, int standardId, int sequence, CatalogControl control, HashSet<string> codes)
    {
        var code = UniqueCode(control.Id, 60, codes);
        string? Attribute(string name, int length) => control.Attributes.TryGetValue(name, out var value) ? Text(value, length) : null;
        return
        [
            standardId, code, Math.Min(sequence, short.MaxValue), Attribute("Phase-Group-Number Reference", 40), Attribute("Task Num", 10),
            Attribute("Task Name", 200) ?? Text(control.Name, 200), Attribute("Group Abbreviation", 10), Attribute("Group Item Number", 10),
            Attribute("Control", 20) ?? Text(control.Number, 20), Text(control.Category, 100), Attribute("Setting Name", 300) ?? Text(control.Setting.Length > 0 ? control.Setting : control.Name, 300),
            Attribute("Target Configuration or Required Action", int.MaxValue) ?? Text(control.Value),
            Attribute("Configuration or Review Path", 1000) ?? Attribute("Policy Path", 1000) ?? Text(control.Path, 1000), Attribute("Deployment Method", 40),
            Attribute("Validation Method", 1000), Attribute("Evidence Required", 1000), Attribute("Applies When", 300),
            Attribute("License Requirement", 300) ?? Text(control.Licensing, 300), Attribute("Implementation Mode", 30), Attribute("Pilot Group", 200),
            Attribute("Exclusions", 1000), Attribute("Exception Criteria", 1000), Attribute("Rollback Method", 1000), Attribute("Review Cadence", 200),
            Attribute("Support Owner", 100), session.Bool(false), Attribute("Engineering Notes", int.MaxValue) ?? Text(control.Description)
        ];
    }

    private static void StandardParameters(DbCommand command, (string Kind, string Code, string Name, string Notes) standard, ControlCatalog catalog, int? id)
    {
        DatabaseConnectionFactory.Parameter(command, "@name", standard.Name);
        DatabaseConnectionFactory.Parameter(command, "@version", Text(catalog.Version, 20));
        DatabaseConnectionFactory.Parameter(command, "@status", "Active");
        DatabaseConnectionFactory.Parameter(command, "@notes", Text(standard.Notes, 1000));
        if (id is { } value) DatabaseConnectionFactory.Parameter(command, "@id", value);
    }

    private static object?[] ArtifactRow(SqlSession session, int versionId, int categoryId, string code, CatalogControl control)
    {
        var body = control.Body.Length == 0 ? null : control.Body;
        var fileName = control.Path.Length == 0 ? null : control.Path.Replace('\\', '/').Split('/')[^1];
        return
        [
            versionId, categoryId, code, int.TryParse(control.Number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null,
            Text(fileName, 400), Text(control.Path, 800), Text(control.Name, 400), Text(control.Value, 200), Text(control.Evaluation, 40), Text(control.Platform, 30),
            Text(control.Kind, 100), session.Bool(false), body, body is null ? NullBinary : SHA256.HashData(Encoding.UTF8.GetBytes(body)),
            Math.Min(control.Expected.Count, short.MaxValue), "Active", Text(control.Licensing, 500), Text(control.Description, 2000)
        ];
    }

    /// <summary>pol.Artifact.Code is 30 characters: longer control IDs are shortened with a hash suffix.</summary>
    private static Dictionary<string, string> ArtifactCodes(IEnumerable<CatalogControl> controls)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return controls.ToDictionary(control => control.Id, control => UniqueCode(control.Id, 30, used), StringComparer.OrdinalIgnoreCase);
    }

    private static string UniqueCode(string id, int length, HashSet<string> used)
    {
        var code = id.Length <= length ? id : $"{id[..(length - 9)]}~{Hash(id)[..8]}";
        for (var attempt = 1; !used.Add(code); attempt++) code = $"{id[..Math.Min(id.Length, length - 9)]}~{Hash($"{id}#{attempt}")[..8]}";
        return code;
    }

    private static string CategoryKey(CatalogControl control) => $"{control.Kind}|{(control.Category.Length == 0 ? "General" : control.Category)}";

    /// <summary>pol.Category.Code is unique across the database (60 characters).</summary>
    private static string CategoryCode(string kind, string category)
    {
        var prefix = kind switch
        {
            CatalogKinds.ConditionalAccess => "CA",
            CatalogKinds.EndpointSecurity => "ES",
            CatalogKinds.Intune => "INT",
            CatalogKinds.AssessmentCheck => "CHK",
            CatalogKinds.Standard => "STD",
            CatalogKinds.MicrosoftBaseline => "MSB",
            CatalogKinds.TenantSetting => "TS",
            _ => kind.ToUpperInvariant()
        };
        var slug = new string(category.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        var code = $"SP-{prefix}-{slug.Trim('-')}";
        return code.Length <= 60 ? code : $"{code[..51]}~{Hash($"{kind}|{category}")[..8]}";
    }

    private static string RequirementKey(FrameworkReference reference) => $"{reference.FrameworkCode}|{reference.Version}|{reference.RequirementCode}";

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string? SourceUrl(string source)
    {
        var repository = source.Split('@')[0];
        return repository.Count(ch => ch == '/') == 1 ? $"https://github.com/{repository}" : null;
    }

    private static string Display(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        _ => value.GetRawText()
    };

    private static string? Text(string? value, int length = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= length ? trimmed : trimmed[..length];
    }

    private static async Task BulkInsertAsync(SqlSession session, DbTransaction transaction, string table, IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows,
        Action<int, int>? onBatch, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return;
        var perBatch = Math.Max(1, MaxParameters / columns.Count);
        var prefix = $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ";
        for (var offset = 0; offset < rows.Count; offset += perBatch)
        {
            var count = Math.Min(perBatch, rows.Count - offset);
            await using var command = session.Command(transaction, string.Empty);
            var text = new StringBuilder(prefix);
            for (var row = 0; row < count; row++)
            {
                text.Append(row == 0 ? "(" : ", (");
                var values = rows[offset + row];
                for (var column = 0; column < columns.Count; column++)
                {
                    var name = $"@p{row}_{column}";
                    text.Append(column == 0 ? name : $", {name}");
                    if (ReferenceEquals(values[column], NullBinary)) DatabaseConnectionFactory.Parameter(command, name, null).DbType = DbType.Binary;
                    else DatabaseConnectionFactory.Parameter(command, name, values[column]);
                }

                text.Append(')');
            }

            command.CommandText = text.Append(';').ToString();
            await command.ExecuteNonQueryAsync(cancellationToken);
            onBatch?.Invoke(offset + count, rows.Count);
        }
    }

    private static async Task<Dictionary<string, int>> ReadMapAsync(SqlSession session, DbTransaction? transaction, string sql, int? version, CancellationToken cancellationToken)
    {
        await using var command = session.Command(transaction, sql);
        if (version is { } value) DatabaseConnectionFactory.Parameter(command, "@version", value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
            if (!reader.IsDBNull(0) && !reader.IsDBNull(1)) map.TryAdd(reader.GetString(0), Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture));
        return map;
    }

    private static async Task<int?> ScalarIdAsync(SqlSession session, DbTransaction transaction, string sql, string code, CancellationToken cancellationToken)
    {
        await using var command = session.Command(transaction, sql);
        DatabaseConnectionFactory.Parameter(command, "@code", code);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<int> InsertIdAsync(SqlSession session, DbTransaction transaction, string sql, Action<DbCommand> parameters, CancellationToken cancellationToken)
    {
        await using var command = session.Command(transaction, $"{sql} {session.IdentitySql}");
        parameters(command);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountAsync(SqlSession session, DbTransaction? transaction, string sql, int? version, CancellationToken cancellationToken)
    {
        await using var command = session.Command(transaction, sql);
        if (version is { } value) DatabaseConnectionFactory.Parameter(command, "@version", value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(SqlSession session, DbTransaction transaction, string sql, Action<DbCommand>? parameters, CancellationToken cancellationToken)
    {
        await using var command = session.Command(transaction, sql);
        parameters?.Invoke(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private Task<SqlSession> OpenAsync(CancellationToken cancellationToken) =>
        SqlSession.OpenAsync(database, connections, message => new InvalidOperationException(message), cancellationToken);
}
