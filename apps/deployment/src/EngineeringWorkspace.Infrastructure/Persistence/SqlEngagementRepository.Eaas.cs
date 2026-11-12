using System.Data;
using System.Data.Common;
using System.Globalization;
using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Infrastructure.Database;

namespace EngineeringWorkspace.Infrastructure.Persistence;

/// <summary>
/// Publishes a finished assessment run into the EaaS v3 engagement tables, so reporting tools that read the EaaS model
/// see Secure Plus results: open findings (cust.Finding), a derived status per framework requirement
/// (cust.RequirementResult, through the knowledge base's configuration-change and standard-item requirement links), a
/// score snapshot (cust.ScoreSnapshot) and an observation snapshot (audit.Snapshot / audit.SnapshotItem). Best effort:
/// the run itself is already saved in the Secure Plus tables, so a failure here never fails the assessment.
/// </summary>
public sealed partial class SqlEngagementRepository
{
    private static readonly Dictionary<string, decimal> SeverityRisk = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Critical"] = 90m, ["High"] = 75m, ["Medium"] = 50m, ["Low"] = 25m, ["Informational"] = 10m, ["Info"] = 10m
    };

    /// <summary>Last error of <see cref="PublishToEaasAsync"/> (diagnostics; null when the last publish succeeded).</summary>
    public string? LastEaasPublishError { get; private set; }

    private async Task TryPublishToEaasAsync(int runId, CancellationToken cancellationToken)
    {
        try
        {
            await PublishToEaasAsync(runId, cancellationToken);
            LastEaasPublishError = null;
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException or FormatException)
        {
            LastEaasPublishError = exception.Message;
        }
    }

    /// <summary>Writes the EaaS view of one completed run (replacing the engagement's open findings and derived requirement results).</summary>
    public async Task PublishToEaasAsync(int runId, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        var run = (await SqlBatch.QueryAsync(session, null,
            $"SELECT r.EngagementId AS E, r.RunAt AS At, e.TenantId AS T, s.Score AS Score FROM {session.Table("cust", "AssessmentRun")} AS r " +
            $"JOIN {session.Table("cust", "Engagement")} AS e ON e.EngagementId = r.EngagementId " +
            $"LEFT JOIN {session.Table("secureplus", "AssessmentStatus")} AS s ON s.AssessmentRunId = r.AssessmentRunId WHERE r.AssessmentRunId = @run;",
            SqlBatch.Parameters(("@run", runId)), cancellationToken)).FirstOrDefault();
        if (run is null) return;
        var results = await ReadResultsAsync(session, runId, cancellationToken);
        if (results.Count == 0) return;
        var engagement = Convert.ToInt32(run["E"], CultureInfo.InvariantCulture);
        var collectedAt = run["At"] is { } at ? SqlSession.ReadDate(at) : timeProvider.GetUtcNow();

        var artifacts = await CodeIdsAsync(session, "pol", "Artifact", "ArtifactId", "Code", cancellationToken);
        var items = await CodeIdsAsync(session, "gds", "StandardItem", "StandardItemId", "ItemCode", cancellationToken);
        var changes = await CodeIdsAsync(session, "kb", "ConfigurationChange", "ConfigurationChangeId", "Code", cancellationToken);
        var statuses = await CodeIdsAsync(session, "ref", "ImplementationStatus", "ImplementationStatusId", "Code", cancellationToken);
        var requirementLinks = await ControlRequirementsAsync(session, cancellationToken);
        int? Id(Dictionary<string, int> map, string code) => map.TryGetValue(code, out var id) ? id : null;

        await using var transaction = await session.Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // Open findings: the failing checks of this run replace the engagement's previous open findings.
        await SqlBatch.ExecuteAsync(session, transaction, $"DELETE FROM {session.Table("cust", "Finding")} WHERE EngagementId = @e AND StatusCode = 'Open';",
            SqlBatch.Parameters(("@e", engagement)), cancellationToken);
        var failing = results.Where(result => result.Result is AssessmentResultValues.Fail or AssessmentResultValues.Partial or AssessmentResultValues.Missing).ToArray();
        await SqlBatch.BulkInsertAsync(session, transaction, session.Table("cust", "Finding"),
            ["EngagementId", "ArtifactId", "StandardItemId", "ConfigurationChangeId", "Title", "Description", "RiskScore", "StatusCode", "IsPoam"],
            failing.Select(result => new object?[]
            {
                engagement, Id(artifacts, result.ControlId), Id(items, result.ControlId), Id(changes, result.ControlId), Truncate(result.Control, 300),
                string.Join("\n", new[] { $"{result.Result}: {result.Evidence}".TrimEnd(' ', ':'), result.Recommendation }.Where(text => !string.IsNullOrWhiteSpace(text))),
                SeverityRisk.GetValueOrDefault(result.Severity, 50m), "Open", session.Bool(false)
            }).ToList(), null, cancellationToken);

        // Requirement results derived from the checks mapped to each requirement (only rows Secure Plus derived are replaced).
        var manual = (await SqlBatch.QueryAsync(session, transaction,
                $"SELECT RequirementId AS R FROM {session.Table("cust", "RequirementResult")} WHERE EngagementId = @e AND (IsDerived = 0 OR IsDerived IS NULL);",
                SqlBatch.Parameters(("@e", engagement)), cancellationToken))
            .Select(row => Convert.ToInt32(row["R"], CultureInfo.InvariantCulture)).ToHashSet();
        await SqlBatch.ExecuteAsync(session, transaction, $"DELETE FROM {session.Table("cust", "RequirementResult")} WHERE EngagementId = @e AND IsDerived = 1;",
            SqlBatch.Parameters(("@e", engagement)), cancellationToken);
        var byRequirement = new Dictionary<int, List<AssessmentResultItem>>();
        foreach (var result in results.Where(result => AssessmentResultValues.IsScored(result.Result)))
        foreach (var requirement in requirementLinks.TryGetValue(result.ControlId, out var linked) ? linked : [])
        {
            if (!byRequirement.TryGetValue(requirement, out var list)) byRequirement[requirement] = list = [];
            list.Add(result);
        }

        var assessedAt = session.Date(collectedAt);
        await SqlBatch.BulkInsertAsync(session, transaction, session.Table("cust", "RequirementResult"),
            ["EngagementId", "RequirementId", "ImplementationStatusId", "IsDerived", "IsInherited", "AssessedBy", "AssessedAt", "AssessmentRunId", "Notes"],
            byRequirement.Where(pair => !manual.Contains(pair.Key)).Select(pair =>
            {
                var passed = pair.Value.Count(result => result.Result == AssessmentResultValues.Pass);
                var status = passed == pair.Value.Count ? "Implemented" : passed > 0 || pair.Value.Any(result => result.Result == AssessmentResultValues.Partial)
                    ? "PartiallyImplemented" : "NotImplemented";
                return new object?[]
                {
                    engagement, pair.Key, Id(statuses, status), session.Bool(true), session.Bool(false), Truncate(auditContext.Actor, 200), assessedAt, runId,
                    Truncate($"Derived from {pair.Value.Count} Secure Plus check(s): {passed} passed. " +
                             string.Join(", ", pair.Value.Select(result => result.ControlId).Distinct(StringComparer.Ordinal).Take(20)), 2000)
                };
            }).ToList(), null, cancellationToken);

        // Score snapshot for trend charts.
        await SqlBatch.ExecuteAsync(session, transaction,
            $"INSERT INTO {session.Table("cust", "ScoreSnapshot")} (EngagementId, AsOf, PctComplete, AvgRiskScore, OpenFindings) VALUES (@e, @at, @pct, @risk, @open);",
            SqlBatch.Parameters(("@e", engagement), ("@at", assessedAt), ("@pct", run["Score"] is { } score ? Math.Min(100m, Convert.ToDecimal(score, CultureInfo.InvariantCulture)) : null),
                ("@risk", failing.Length == 0 ? 0m : Math.Round(failing.Average(result => SeverityRisk.GetValueOrDefault(result.Severity, 50m)), 2)), ("@open", failing.Length)),
            cancellationToken);

        // Observation snapshot (the baseline for drift comparison).
        int snapshotId;
        await using (var insert = session.Command(transaction,
                         $"INSERT INTO {session.Table("audit", "Snapshot")} (TenantId, WorkloadCode, CollectedAt, CollectorVersion, StatusCode) VALUES (@t, @w, @at, @v, @s); " +
                         (session.Sqlite ? session.IdentitySql : "SELECT CAST(SCOPE_IDENTITY() AS BIGINT);")))
        {
            DatabaseConnectionFactory.Parameter(insert, "@t", run["T"]);
            DatabaseConnectionFactory.Parameter(insert, "@w", "M365");
            DatabaseConnectionFactory.Parameter(insert, "@at", assessedAt);
            DatabaseConnectionFactory.Parameter(insert, "@v", Truncate($"Secure Plus run {runId}", 30));
            DatabaseConnectionFactory.Parameter(insert, "@s", "Collected");
            snapshotId = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        await SqlBatch.BulkInsertAsync(session, transaction, session.Table("audit", "SnapshotItem"), ["SnapshotId", "ArtifactId", "ObjectId", "ObservedValue"],
            results.Select(result => new object?[]
            {
                snapshotId, Id(artifacts, result.ControlId), Truncate(result.ControlId, 100),
                result.Evidence is { Length: > 0 } evidence ? $"{result.Result}: {Truncate(evidence, 3900)}" : result.Result
            }).ToList(), null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<Dictionary<string, int>> CodeIdsAsync(SqlSession session, string schema, string table, string idColumn, string codeColumn,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in await SqlBatch.QueryAsync(session, null,
                     $"SELECT {idColumn} AS Id, {codeColumn} AS Code FROM {session.Table(schema, table)} WHERE {codeColumn} IS NOT NULL ORDER BY {idColumn} DESC;", null, cancellationToken))
            map[(string)row["Code"]!] = Convert.ToInt32(row["Id"], CultureInfo.InvariantCulture);
        return map;
    }

    /// <summary>Framework requirements of each catalog control: through its configuration change, or its GDS standard item.</summary>
    private static async Task<Dictionary<string, List<int>>> ControlRequirementsAsync(SqlSession session, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var rows = await SqlBatch.QueryAsync(session, null,
            $"SELECT c.Code AS Code, r.RequirementId AS R FROM {session.Table("kb", "ConfigurationChangeRequirement")} AS r " +
            $"JOIN {session.Table("kb", "ConfigurationChange")} AS c ON c.ConfigurationChangeId = r.ConfigurationChangeId " +
            $"UNION SELECT i.ItemCode AS Code, r.RequirementId AS R FROM {session.Table("gds", "StandardItemRequirement")} AS r " +
            $"JOIN {session.Table("gds", "StandardItem")} AS i ON i.StandardItemId = r.StandardItemId;", null, cancellationToken);
        foreach (var row in rows)
        {
            if (row["Code"] is not string code) continue;
            if (!map.TryGetValue(code, out var list)) map[code] = list = [];
            list.Add(Convert.ToInt32(row["R"], CultureInfo.InvariantCulture));
        }

        return map;
    }
}
