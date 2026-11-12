using System.Data;
using System.Globalization;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Planning;
using EngineeringWorkspace.Infrastructure.Database;

namespace EngineeringWorkspace.Infrastructure.Persistence;

/// <summary>
/// Planning data in the application database: ATT&amp;CK coverage (threat.*, kb.Capability, svc.*), setting remediation
/// (cfg.Setting, cfg.SettingAlias, cfg.RecommendationProcedure), the WBS (dlv.WbsTask...), SOW templates (dlv.SowTemplate,
/// dlv.SowSection) and customer project plans (dlv.ProjectPlan, dlv.ProjectTask, dlv.ProjectTaskDependency).
/// </summary>
public sealed class SqlPlanningRepository(IDatabaseStatusService database, DatabaseConnectionFactory connections) : IPlanningRepository
{
    public bool IsAvailable => database.Settings.Provider != DatabaseProvider.None && database.LatestStatus is { State: DatabaseOverallState.Ready };

    private Task<SqlSession> OpenAsync(CancellationToken cancellationToken) =>
        SqlSession.OpenAsync(database, connections, message => new InvalidOperationException(message), cancellationToken);

    private static string? S(Dictionary<string, object?> row, string name) => row[name] is null or DBNull ? null : Convert.ToString(row[name], CultureInfo.InvariantCulture);
    private static int I(Dictionary<string, object?> row, string name) => row[name] is null or DBNull ? 0 : Convert.ToInt32(row[name], CultureInfo.InvariantCulture);
    private static double D(Dictionary<string, object?> row, string name) => row[name] is null or DBNull ? 0 : Convert.ToDouble(row[name], CultureInfo.InvariantCulture);
    private static bool B(Dictionary<string, object?> row, string name) => row[name] is not null and not DBNull && Convert.ToBoolean(row[name], CultureInfo.InvariantCulture);

    private static DateOnly Date(object? value) => value switch
    {
        DateTime dateTime => DateOnly.FromDateTime(dateTime),
        DateOnly date => date,
        string text when DateOnly.TryParse(text.Length >= 10 ? text[..10] : text, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => DateOnly.FromDateTime(DateTime.Today)
    };

    private static object DateValue(SqlSession session, DateOnly date) =>
        session.Sqlite ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : date.ToDateTime(TimeOnly.MinValue);

    // ---- Threat coverage ----

    public async Task<IReadOnlyList<ThreatCoverageCell>> GetThreatCoverageAsync(CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        var techniques = await SqlBatch.QueryAsync(session, null,
            $"SELECT ta.Name AS Tactic, ta.KillChainOrder AS TacticOrder, t.TechniqueId AS Id, t.TechniqueCode AS Code, t.Name AS Name " +
            $"FROM {session.Table("threat", "TechniqueTactic")} AS tt JOIN {session.Table("threat", "AttackTactic")} AS ta ON ta.TacticId = tt.TacticId " +
            $"JOIN {session.Table("threat", "AttackTechnique")} AS t ON t.TechniqueId = tt.TechniqueId " +
            $"WHERE (t.IsSubTechnique = 0 OR t.IsSubTechnique IS NULL) AND (t.IsDeprecated = 0 OR t.IsDeprecated IS NULL);", null, cancellationToken);
        var mapped = await SqlBatch.QueryAsync(session, null,
            $"SELECT DISTINCT COALESCE(t.ParentTechniqueId, t.TechniqueId) AS Id, c.ProductId AS Product FROM {session.Table("threat", "CapabilityTechnique")} AS ct " +
            $"JOIN {session.Table("threat", "AttackTechnique")} AS t ON t.TechniqueId = ct.TechniqueId JOIN {session.Table("kb", "Capability")} AS c ON c.CapabilityId = ct.CapabilityId;",
            null, cancellationToken);
        var tiers = await SqlBatch.QueryAsync(session, null,
            $"SELECT DISTINCT st.Name AS Tier, st.SortOrder AS SortOrder, c.ProductId AS Product FROM {session.Table("svc", "TierCapability")} AS tc " +
            $"JOIN {session.Table("svc", "ServiceTier")} AS st ON st.ServiceTierId = tc.ServiceTierId JOIN {session.Table("kb", "Capability")} AS c ON c.CapabilityId = tc.CapabilityId " +
            $"WHERE c.ProductId IS NOT NULL;", null, cancellationToken);

        var coveredBy = mapped.GroupBy(row => I(row, "Id")).ToDictionary(group => group.Key, group => group.Select(row => row["Product"] is null or DBNull ? -1 : I(row, "Product")).ToHashSet());
        var columns = tiers.GroupBy(row => (Name: S(row, "Tier") ?? "Tier", Order: I(row, "SortOrder")))
            .OrderBy(group => group.Key.Order).Select(group => (group.Key.Name, Products: group.Select(row => I(row, "Product")).ToHashSet())).ToList();
        var cells = new List<ThreatCoverageCell>();
        foreach (var tactic in techniques.GroupBy(row => (Name: S(row, "Tactic") ?? "Tactic", Order: I(row, "TacticOrder"))))
        {
            var list = tactic.DistinctBy(row => I(row, "Id")).ToArray();
            string Label(Dictionary<string, object?> row) => $"{S(row, "Code")} {S(row, "Name")}";
            foreach (var (name, products) in columns)
            {
                var covered = list.Where(row => coveredBy.TryGetValue(I(row, "Id"), out var by) && by.Overlaps(products)).ToArray();
                cells.Add(new ThreatCoverageCell(tactic.Key.Name, tactic.Key.Order, name, list.Length, covered.Length, list.Except(covered).Select(Label).Take(12).ToArray()));
            }

            var any = list.Where(row => coveredBy.ContainsKey(I(row, "Id"))).ToArray();
            cells.Add(new ThreatCoverageCell(tactic.Key.Name, tactic.Key.Order, "All Microsoft", list.Length, any.Length, list.Except(any).Select(Label).Take(12).ToArray()));
        }

        return cells.OrderBy(cell => cell.TacticOrder).ToArray();
    }

    // ---- Settings and procedures ----

    public async Task<IReadOnlyList<KnowledgeSetting>> FindSettingsAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
    {
        var wanted = keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => key.Trim().ToLowerInvariant()).Distinct().Take(150).ToArray();
        if (wanted.Length == 0) return [];
        await using var session = await OpenAsync(cancellationToken);
        var names = string.Join(", ", wanted.Select((_, index) => $"@k{index}"));
        void Keys(System.Data.Common.DbCommand command)
        {
            for (var index = 0; index < wanted.Length; index++) DatabaseConnectionFactory.Parameter(command, $"@k{index}", wanted[index]);
        }

        var ids = (await SqlBatch.QueryAsync(session, null,
                $"SELECT DISTINCT SettingId AS Id FROM {session.Table("cfg", "SettingAlias")} WHERE LOWER(AliasValue) IN ({names}) " +
                $"UNION SELECT SettingId AS Id FROM {session.Table("cfg", "Setting")} WHERE LOWER(SettingKey) IN ({names}) OR LOWER(IntuneSettingDefinitionId) IN ({names}) OR LOWER(OmaUri) IN ({names});",
                Keys, cancellationToken))
            .Select(row => I(row, "Id")).Distinct().Take(40).ToArray();
        if (ids.Length == 0) return [];
        var idList = string.Join(", ", ids);
        var settings = await SqlBatch.QueryAsync(session, null,
            $"SELECT SettingId, SettingKey, Name, FriendlyName, GpoPath, RegistryHive, RegistryKey, RegistryValueName, RegistryValueType, OmaUri, IntuneSettingDefinitionId, " +
            $"PowerShellCmdlet, PortalPath, DefaultValue FROM {session.Table("cfg", "Setting")} WHERE SettingId IN ({idList});", null, cancellationToken);
        var values = await SqlBatch.QueryAsync(session, null,
            $"SELECT rs.SettingId AS SettingId, rs.ExpectedValue AS Value, b.Code AS Baseline, r.RecNumber AS Rec FROM {session.Table("cfg", "RecommendationSetting")} AS rs " +
            $"JOIN {session.Table("cfg", "Recommendation")} AS r ON r.RecommendationId = rs.RecommendationId JOIN {session.Table("cfg", "Baseline")} AS b ON b.BaselineId = r.BaselineId " +
            $"WHERE rs.SettingId IN ({idList});", null, cancellationToken);
        var procedures = await SqlBatch.QueryAsync(session, null,
            $"SELECT rs.SettingId AS SettingId, b.Code AS Baseline, r.RecNumber AS Rec, r.Title AS Title, p.ProcedureTypeCode AS Type, p.MethodCode AS Method, p.StepText AS Step, " +
            $"p.ScriptText AS Script, p.Sequence AS Seq FROM {session.Table("cfg", "RecommendationSetting")} AS rs " +
            $"JOIN {session.Table("cfg", "Recommendation")} AS r ON r.RecommendationId = rs.RecommendationId JOIN {session.Table("cfg", "Baseline")} AS b ON b.BaselineId = r.BaselineId " +
            $"JOIN {session.Table("cfg", "RecommendationProcedure")} AS p ON p.RecommendationId = r.RecommendationId WHERE rs.SettingId IN ({idList}) ORDER BY b.Code, r.RecNumber, p.Sequence;",
            null, cancellationToken);
        return settings.Select(row =>
        {
            var id = I(row, "SettingId");
            return new KnowledgeSetting(S(row, "SettingKey") ?? string.Empty, S(row, "FriendlyName") ?? S(row, "Name") ?? S(row, "SettingKey") ?? "Setting",
                S(row, "GpoPath"), S(row, "RegistryHive"), S(row, "RegistryKey"), S(row, "RegistryValueName"), S(row, "RegistryValueType"), S(row, "OmaUri"),
                S(row, "IntuneSettingDefinitionId"), S(row, "PowerShellCmdlet"), S(row, "PortalPath"), S(row, "DefaultValue"),
                values.Where(value => I(value, "SettingId") == id && S(value, "Value") is not null)
                    .Select(value => $"{S(value, "Baseline")} {S(value, "Rec")}: {S(value, "Value")}").Distinct().Take(12).ToArray(),
                procedures.Where(procedure => I(procedure, "SettingId") == id)
                    .Select(procedure => new KnowledgeProcedure($"{S(procedure, "Baseline")} {S(procedure, "Rec")} {S(procedure, "Title")}".Trim(), S(procedure, "Type") ?? string.Empty,
                        S(procedure, "Method") ?? string.Empty, S(procedure, "Step") ?? string.Empty, S(procedure, "Script")))
                    .DistinctBy(procedure => (procedure.Type, procedure.Method, procedure.Text)).Take(16).ToArray());
        }).ToArray();
    }

    // ---- WBS ----

    public async Task<WorkBreakdown> GetWorkBreakdownAsync(CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        var phases = (await SqlBatch.QueryAsync(session, null, $"SELECT Code, Name, SortOrder FROM {session.Table("dlv", "Phase")} ORDER BY SortOrder;", null, cancellationToken))
            .Select(row => new WbsPhaseInfo(S(row, "Code") ?? string.Empty, S(row, "Name") ?? string.Empty, I(row, "SortOrder"))).ToArray();
        var dependencies = (await SqlBatch.QueryAsync(session, null,
                $"SELECT t.TaskCode AS Task, p.TaskCode AS Predecessor FROM {session.Table("dlv", "WbsTaskDependency")} AS d " +
                $"JOIN {session.Table("dlv", "WbsTask")} AS t ON t.WbsTaskId = d.WbsTaskId JOIN {session.Table("dlv", "WbsTask")} AS p ON p.WbsTaskId = d.PredecessorWbsTaskId;",
                null, cancellationToken))
            .GroupBy(row => S(row, "Task") ?? string.Empty).ToDictionary(group => group.Key, group => group.Select(row => S(row, "Predecessor")!).ToArray());
        var tasks = (await SqlBatch.QueryAsync(session, null,
                $"SELECT t.TaskCode, t.TaskName, t.SubTaskName, p.Code AS PhaseCode, w.Name AS Workstream, t.Bucket, t.ControlCode, t.EstHoursMin, t.EstHoursLikely, t.EstHoursMax, " +
                $"t.IsParallelizable, t.PriorityCode, t.Deliverable, t.DependencyText, t.SortKey FROM {session.Table("dlv", "WbsTask")} AS t " +
                $"LEFT JOIN {session.Table("dlv", "Phase")} AS p ON p.PhaseId = t.PhaseId LEFT JOIN {session.Table("dlv", "Workstream")} AS w ON w.WorkstreamId = t.WorkstreamId " +
                $"ORDER BY t.SortKey, t.TaskCode;", null, cancellationToken))
            .Select(row => new WbsTaskInfo(S(row, "TaskCode") ?? string.Empty, S(row, "TaskName") ?? string.Empty, S(row, "SubTaskName"), S(row, "PhaseCode"), S(row, "Workstream"),
                S(row, "Bucket"), S(row, "ControlCode"), D(row, "EstHoursMin"), D(row, "EstHoursLikely"), D(row, "EstHoursMax"), B(row, "IsParallelizable"), S(row, "PriorityCode"),
                S(row, "Deliverable"), S(row, "DependencyText"), S(row, "SortKey"), dependencies.GetValueOrDefault(S(row, "TaskCode") ?? string.Empty) ?? []))
            .ToArray();
        return new WorkBreakdown(phases, tasks);
    }

    // ---- SOW templates ----

    public async Task<IReadOnlyList<SowTemplate>> GetSowTemplatesAsync(CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        var templates = await SqlBatch.QueryAsync(session, null,
            $"SELECT t.SowTemplateId AS Id, t.Code, t.Name, t.Version, t.EngagementTypeCode, st.Code AS Tier FROM {session.Table("dlv", "SowTemplate")} AS t " +
            $"LEFT JOIN {session.Table("svc", "ServiceTier")} AS st ON st.ServiceTierId = t.ServiceTierId ORDER BY t.Name;", null, cancellationToken);
        var sections = await SqlBatch.QueryAsync(session, null,
            $"SELECT SowTemplateId AS Id, SectionCode, Title, SortOrder, ContentTemplate, IsRequired FROM {session.Table("dlv", "SowSection")} ORDER BY SowTemplateId, SortOrder;",
            null, cancellationToken);
        return templates.Select(row => new SowTemplate(S(row, "Code") ?? string.Empty, S(row, "Name") ?? string.Empty, S(row, "Version") ?? "1.0", S(row, "EngagementTypeCode"), S(row, "Tier"),
            sections.Where(section => I(section, "Id") == I(row, "Id"))
                .Select(section => new SowSection(S(section, "SectionCode") ?? string.Empty, S(section, "Title") ?? string.Empty, I(section, "SortOrder"),
                    S(section, "ContentTemplate") ?? string.Empty, B(section, "IsRequired"))).ToArray())).ToArray();
    }

    public async Task SaveSowTemplateAsync(SowTemplate template, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        await using var transaction = await session.Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var tier = template.ServiceTierCode is null ? null : await SqlBatch.ScalarAsync(session, transaction,
            $"SELECT ServiceTierId FROM {session.Table("svc", "ServiceTier")} WHERE Code = @code;", SqlBatch.Parameters(("@code", template.ServiceTierCode)), cancellationToken);
        var existing = await SqlBatch.ScalarAsync(session, transaction, $"SELECT SowTemplateId FROM {session.Table("dlv", "SowTemplate")} WHERE Code = @code;",
            SqlBatch.Parameters(("@code", template.Code)), cancellationToken);
        int id;
        if (existing is null or DBNull)
        {
            id = await SqlBatch.InsertIdAsync(session, transaction,
                $"INSERT INTO {session.Table("dlv", "SowTemplate")} (Code, Name, Version, EngagementTypeCode, ServiceTierId) VALUES (@code, @name, @version, @type, @tier);",
                SqlBatch.Parameters(("@code", Fit(template.Code, 40)), ("@name", Fit(template.Name, 200)), ("@version", Fit(template.Version, 20)), ("@type", Fit(template.EngagementType, 30)),
                    ("@tier", tier is null or DBNull ? null : tier)), cancellationToken);
        }
        else
        {
            id = Convert.ToInt32(existing, CultureInfo.InvariantCulture);
            await SqlBatch.ExecuteAsync(session, transaction,
                $"UPDATE {session.Table("dlv", "SowTemplate")} SET Name = @name, Version = @version, EngagementTypeCode = @type, ServiceTierId = @tier WHERE SowTemplateId = @id;",
                SqlBatch.Parameters(("@name", Fit(template.Name, 200)), ("@version", Fit(template.Version, 20)), ("@type", Fit(template.EngagementType, 30)),
                    ("@tier", tier is null or DBNull ? null : tier), ("@id", id)), cancellationToken);
            await SqlBatch.ExecuteAsync(session, transaction, $"DELETE FROM {session.Table("dlv", "SowSection")} WHERE SowTemplateId = @id;", SqlBatch.Parameters(("@id", id)), cancellationToken);
        }

        await SqlBatch.BulkInsertAsync(session, transaction, session.Table("dlv", "SowSection"), ["SowTemplateId", "SectionCode", "Title", "SortOrder", "ContentTemplate", "IsRequired"],
            template.Sections.Select((section, index) => new object?[]
                { id, Fit(section.Code, 40), Fit(section.Title, 200), (short)(index + 1), section.Content, session.Bool(section.IsRequired) }).ToList(), null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteSowTemplateAsync(string code, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        await using var transaction = await session.Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await SqlBatch.ExecuteAsync(session, transaction,
            $"DELETE FROM {session.Table("dlv", "SowSection")} WHERE SowTemplateId IN (SELECT SowTemplateId FROM {session.Table("dlv", "SowTemplate")} WHERE Code = @code);",
            SqlBatch.Parameters(("@code", code)), cancellationToken);
        await SqlBatch.ExecuteAsync(session, transaction, $"DELETE FROM {session.Table("dlv", "SowTemplate")} WHERE Code = @code;", SqlBatch.Parameters(("@code", code)), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // ---- Project plans ----

    private static async Task<int?> EngagementAsync(SqlSession session, System.Data.Common.DbTransaction? transaction, string customerKey, CancellationToken cancellationToken) =>
        await SqlBatch.ScalarAsync(session, transaction, $"SELECT EngagementId FROM {session.Table("secureplus", "CustomerLink")} WHERE CustomerKey = @key;",
            SqlBatch.Parameters(("@key", customerKey)), cancellationToken) is { } value and not DBNull ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : null;

    public async Task<IReadOnlyList<ProjectPlan>> GetProjectPlansAsync(string customerKey, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        if (await EngagementAsync(session, null, customerKey, cancellationToken) is not { } engagement) return [];
        var plans = await SqlBatch.QueryAsync(session, null,
            $"SELECT ProjectPlanId, Name, StartDate, FinishDate, StatusCode FROM {session.Table("dlv", "ProjectPlan")} WHERE EngagementId = @e ORDER BY ProjectPlanId DESC;",
            SqlBatch.Parameters(("@e", engagement)), cancellationToken);
        var result = new List<ProjectPlan>();
        foreach (var plan in plans)
        {
            var id = I(plan, "ProjectPlanId");
            var tasks = await SqlBatch.QueryAsync(session, null,
                $"SELECT pt.ProjectTaskId, pt.OutlineNumber, pt.Name, pt.Bucket, r.Name AS Role, pt.AssignedTo, pt.StartDate, pt.FinishDate, pt.DurationDays, pt.EffortHours, pt.PctComplete, " +
                $"pt.IsMilestone, pt.StatusCode, pt.PriorityCode, w.TaskCode, pt.TaskKey, pt.SortOrder, pt.Notes, pt.ActualFinishDate, pt.ChangeRequestIds, pt.ControlIds, pt.ScheduleRank, pt.PinnedStartDate " +
                $"FROM {session.Table("dlv", "ProjectTask")} AS pt " +
                $"LEFT JOIN {session.Table("dlv", "Role")} AS r ON r.RoleId = pt.AssignedRoleId LEFT JOIN {session.Table("dlv", "WbsTask")} AS w ON w.WbsTaskId = pt.WbsTaskId " +
                $"WHERE pt.ProjectPlanId = @p ORDER BY COALESCE(pt.SortOrder, pt.ProjectTaskId), pt.ProjectTaskId;", SqlBatch.Parameters(("@p", id)), cancellationToken);
            // Tasks are re-inserted on every save, so the stable key is TaskKey (older rows fall back to their row ID).
            var keyOf = tasks.ToDictionary(task => Convert.ToInt64(task["ProjectTaskId"], CultureInfo.InvariantCulture),
                task => S(task, "TaskKey") ?? Convert.ToInt64(task["ProjectTaskId"], CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
            var dependencies = (await SqlBatch.QueryAsync(session, null,
                    $"SELECT d.ProjectTaskId AS Task, d.PredecessorProjectTaskId AS Predecessor FROM {session.Table("dlv", "ProjectTaskDependency")} AS d " +
                    $"JOIN {session.Table("dlv", "ProjectTask")} AS pt ON pt.ProjectTaskId = d.ProjectTaskId WHERE pt.ProjectPlanId = @p;", SqlBatch.Parameters(("@p", id)), cancellationToken))
                .GroupBy(row => Convert.ToInt64(row["Task"], CultureInfo.InvariantCulture))
                .ToDictionary(group => group.Key, group => group.Select(row => Convert.ToInt64(row["Predecessor"], CultureInfo.InvariantCulture))
                    .Where(keyOf.ContainsKey).Select(predecessor => keyOf[predecessor]).ToArray());
            result.Add(new ProjectPlan(id, S(plan, "Name") ?? "Project plan", Date(plan["StartDate"]), Date(plan["FinishDate"]), S(plan, "StatusCode") ?? "Draft",
                tasks.Select(task =>
                {
                    var key = Convert.ToInt64(task["ProjectTaskId"], CultureInfo.InvariantCulture);
                    return new ProjectTask(keyOf[key], S(task, "OutlineNumber") ?? string.Empty, S(task, "Name") ?? string.Empty, S(task, "Bucket"),
                        S(task, "Role"), S(task, "AssignedTo"), Date(task["StartDate"]), Date(task["FinishDate"]), D(task, "DurationDays"), D(task, "EffortHours"), D(task, "PctComplete"),
                        B(task, "IsMilestone"), S(task, "StatusCode") ?? "NotStarted", S(task, "PriorityCode"), S(task, "TaskCode"), dependencies.GetValueOrDefault(key) ?? [],
                        S(task, "Notes"), task["ActualFinishDate"] is null or DBNull ? null : Date(task["ActualFinishDate"]),
                        (S(task, "ChangeRequestIds") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(value => int.TryParse(value, CultureInfo.InvariantCulture, out var number) ? number : 0).Where(number => number > 0).ToArray(),
                        (S(task, "ControlIds") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                        task["ScheduleRank"] is null or DBNull ? null : Convert.ToDouble(task["ScheduleRank"], CultureInfo.InvariantCulture),
                        task["PinnedStartDate"] is null or DBNull ? null : Date(task["PinnedStartDate"]));
                }).ToArray()));
        }

        return result;
    }

    public async Task<ProjectPlan> SaveProjectPlanAsync(string customerKey, ProjectPlan plan, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        await using var transaction = await session.Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var engagement = await EngagementAsync(session, transaction, customerKey, cancellationToken)
                         ?? throw new InvalidOperationException("The customer has no engagement yet; save the customer profile first.");
        int id;
        if (plan.Id is { } existing)
        {
            id = existing;
            await SqlBatch.ExecuteAsync(session, transaction,
                $"UPDATE {session.Table("dlv", "ProjectPlan")} SET Name = @name, StartDate = @start, FinishDate = @finish, StatusCode = @status WHERE ProjectPlanId = @id AND EngagementId = @e;",
                SqlBatch.Parameters(("@name", Fit(plan.Name, 200)), ("@start", DateValue(session, plan.Start)), ("@finish", DateValue(session, plan.Finish)), ("@status", Fit(plan.Status, 20)),
                    ("@id", id), ("@e", engagement)), cancellationToken);
            await SqlBatch.ExecuteAsync(session, transaction,
                $"DELETE FROM {session.Table("dlv", "ProjectTaskDependency")} WHERE ProjectTaskId IN (SELECT ProjectTaskId FROM {session.Table("dlv", "ProjectTask")} WHERE ProjectPlanId = @id);",
                SqlBatch.Parameters(("@id", id)), cancellationToken);
            await SqlBatch.ExecuteAsync(session, transaction, $"DELETE FROM {session.Table("dlv", "ProjectTask")} WHERE ProjectPlanId = @id;", SqlBatch.Parameters(("@id", id)), cancellationToken);
        }
        else
        {
            id = await SqlBatch.InsertIdAsync(session, transaction,
                $"INSERT INTO {session.Table("dlv", "ProjectPlan")} (EngagementId, Name, StartDate, FinishDate, StatusCode) VALUES (@e, @name, @start, @finish, @status);",
                SqlBatch.Parameters(("@e", engagement), ("@name", Fit(plan.Name, 200)), ("@start", DateValue(session, plan.Start)), ("@finish", DateValue(session, plan.Finish)),
                    ("@status", Fit(plan.Status, 20))), cancellationToken);
        }

        // Roles by name (dlv.Role is created on demand), WBS tasks by code.
        var roles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in await SqlBatch.QueryAsync(session, transaction, $"SELECT RoleId, Code, Name FROM {session.Table("dlv", "Role")};", null, cancellationToken))
        {
            if (S(row, "Name") is { } name) roles[name] = I(row, "RoleId");
            if (S(row, "Code") is { } code) roles.TryAdd(code, I(row, "RoleId"));
        }

        foreach (var role in plan.Tasks.Select(task => task.Role).OfType<string>().Where(role => role.Trim().Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(role => !roles.ContainsKey(role)).ToArray())
            roles[role] = await SqlBatch.InsertIdAsync(session, transaction,
                $"INSERT INTO {session.Table("dlv", "Role")} (Code, Name, Description) VALUES (@code, @name, @description);",
                SqlBatch.Parameters(("@code", Fit(new string(role.Where(char.IsLetterOrDigit).ToArray()), 30)), ("@name", Fit(role, 100)), ("@description", "Created by a Secure Plus project plan.")),
                cancellationToken);
        var wbs = (await SqlBatch.QueryAsync(session, transaction, $"SELECT WbsTaskId, TaskCode FROM {session.Table("dlv", "WbsTask")};", null, cancellationToken))
            .Where(row => S(row, "TaskCode") is not null).GroupBy(row => S(row, "TaskCode")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => I(group.First(), "WbsTaskId"), StringComparer.OrdinalIgnoreCase);

        var keys = new Dictionary<string, long>(StringComparer.Ordinal);
        var saved = new List<ProjectTask>();
        foreach (var task in plan.Tasks)
        {
            await using var insert = session.Command(transaction,
                $"INSERT INTO {session.Table("dlv", "ProjectTask")} (ProjectPlanId, WbsTaskId, OutlineNumber, Name, Bucket, AssignedRoleId, AssignedTo, StartDate, FinishDate, DurationDays, " +
                $"EffortHours, PctComplete, IsMilestone, StatusCode, PriorityCode, TaskKey, SortOrder, Notes, ActualFinishDate, ChangeRequestIds, ControlIds, ScheduleRank, PinnedStartDate) VALUES (@p, @wbs, @outline, @name, " +
                $"@bucket, @role, @to, @start, @finish, @days, @hours, @pct, @milestone, @status, @priority, @key, @sort, @notes, @actual, @crs, @controls, @rank, @pinned); {(session.Sqlite ? session.IdentitySql : "SELECT CAST(SCOPE_IDENTITY() AS BIGINT);")}");
            DatabaseConnectionFactory.Parameter(insert, "@p", id);
            DatabaseConnectionFactory.Parameter(insert, "@wbs", task.WbsTaskCode is { } code && wbs.TryGetValue(code, out var wbsId) ? wbsId : null);
            DatabaseConnectionFactory.Parameter(insert, "@outline", Fit(task.Outline, 20));
            DatabaseConnectionFactory.Parameter(insert, "@name", Fit(task.Name, 300));
            DatabaseConnectionFactory.Parameter(insert, "@bucket", Fit(task.Bucket, 200));
            DatabaseConnectionFactory.Parameter(insert, "@role", task.Role is { } role && roles.TryGetValue(role, out var roleId) ? roleId : null);
            DatabaseConnectionFactory.Parameter(insert, "@to", Fit(task.AssignedTo, 200));
            DatabaseConnectionFactory.Parameter(insert, "@start", DateValue(session, task.Start));
            DatabaseConnectionFactory.Parameter(insert, "@finish", DateValue(session, task.Finish));
            DatabaseConnectionFactory.Parameter(insert, "@days", Math.Round((decimal)Math.Min(task.DurationDays, 9999), 1));
            DatabaseConnectionFactory.Parameter(insert, "@hours", Math.Round((decimal)Math.Min(task.EffortHours, 999999), 1));
            DatabaseConnectionFactory.Parameter(insert, "@pct", Math.Round((decimal)Math.Clamp(task.PercentComplete, 0, 100), 2));
            DatabaseConnectionFactory.Parameter(insert, "@milestone", session.Bool(task.IsMilestone));
            DatabaseConnectionFactory.Parameter(insert, "@status", Fit(task.Status, 20));
            DatabaseConnectionFactory.Parameter(insert, "@priority", Fit(task.Priority, 10));
            var stable = StableKey(task.Key);
            DatabaseConnectionFactory.Parameter(insert, "@key", stable);
            DatabaseConnectionFactory.Parameter(insert, "@sort", saved.Count);
            DatabaseConnectionFactory.Parameter(insert, "@notes", Fit(task.Notes, 4000));
            DatabaseConnectionFactory.Parameter(insert, "@actual", task.ActualFinish is { } actual ? DateValue(session, actual) : null);
            DatabaseConnectionFactory.Parameter(insert, "@crs", task.ChangeRequests.Count == 0 ? null : Fit(string.Join(",", task.ChangeRequests.Distinct()), 400));
            DatabaseConnectionFactory.Parameter(insert, "@controls", task.Controls.Count == 0 ? null : string.Join(",", task.Controls.Distinct(StringComparer.OrdinalIgnoreCase)));
            DatabaseConnectionFactory.Parameter(insert, "@rank", task.ScheduleRank);
            DatabaseConnectionFactory.Parameter(insert, "@pinned", task.PinnedStart is { } pinned ? DateValue(session, pinned) : null);
            var newId = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            keys[task.Key] = newId;
            saved.Add(task with { Key = stable });
        }

        var dependencyRows = plan.Tasks.SelectMany(task => task.Predecessors.Where(keys.ContainsKey).Distinct()
                .Select(predecessor => new object?[] { keys[task.Key], keys[predecessor], "FS", 0m }))
            .Where(row => !Equals(row[0], row[1])).ToList();
        await SqlBatch.BulkInsertAsync(session, transaction, session.Table("dlv", "ProjectTaskDependency"), ["ProjectTaskId", "PredecessorProjectTaskId", "DependencyTypeCode", "LagDays"],
            dependencyRows, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return plan with
        {
            Id = id,
            Tasks = saved.Select((task, index) => task with
            {
                Predecessors = plan.Tasks[index].Predecessors.Where(keys.ContainsKey).Select(StableKey).ToArray()
            }).ToArray()
        };
    }

    /// <summary>A task's stable key: kept as given, except temporary or legacy numeric keys, which get a deterministic replacement.</summary>
    private static string StableKey(string key) => string.IsNullOrWhiteSpace(key) ? Guid.NewGuid().ToString("N") : key.Length > 64 ? key[..64] : key;

    public async Task AddTaskEventAsync(ProjectTaskEvent entry, byte[]? attachment = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (attachment is { Length: > 10 * 1024 * 1024 }) throw new ArgumentException("The attachment is larger than 10 MB.", nameof(attachment));
        await using var session = await OpenAsync(cancellationToken);
        await using var command = session.Command(null,
            $"INSERT INTO {session.Table("secureplus", "ProjectTaskEvent")} (ProjectPlanId, TaskKey, EventType, Summary, DetailJson, AttachmentName, Attachment, CreatedBy, CreatedAtUtc) " +
            "VALUES (@plan, @key, @type, @summary, @detail, @name, @file, @by, @at);");
        DatabaseConnectionFactory.Parameter(command, "@plan", entry.PlanId);
        DatabaseConnectionFactory.Parameter(command, "@key", Fit(entry.TaskKey, 64));
        DatabaseConnectionFactory.Parameter(command, "@type", Fit(entry.EventType, 40));
        DatabaseConnectionFactory.Parameter(command, "@summary", Fit(entry.Summary, 1000));
        DatabaseConnectionFactory.Parameter(command, "@detail", entry.DetailJson);
        DatabaseConnectionFactory.Parameter(command, "@name", attachment is null ? null : Fit(entry.AttachmentName ?? "attachment", 260));
        var file = command.CreateParameter();
        file.ParameterName = "@file";
        file.DbType = DbType.Binary;
        file.Value = (object?)attachment ?? DBNull.Value;
        command.Parameters.Add(file);
        DatabaseConnectionFactory.Parameter(command, "@by", Fit(entry.CreatedBy, 256));
        DatabaseConnectionFactory.Parameter(command, "@at", session.Date(entry.CreatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectTaskEvent>> GetTaskEventsAsync(int planId, string taskKey, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        var rows = await SqlBatch.QueryAsync(session, null,
            $"SELECT ProjectTaskEventId, EventType, Summary, DetailJson, AttachmentName, CreatedBy, CreatedAtUtc FROM {session.Table("secureplus", "ProjectTaskEvent")} " +
            "WHERE ProjectPlanId = @plan AND TaskKey = @key ORDER BY ProjectTaskEventId DESC;", SqlBatch.Parameters(("@plan", planId), ("@key", taskKey)), cancellationToken);
        return rows.Select(row => new ProjectTaskEvent(Convert.ToInt64(row["ProjectTaskEventId"], CultureInfo.InvariantCulture), planId, taskKey, S(row, "EventType") ?? string.Empty,
            S(row, "Summary") ?? string.Empty, S(row, "DetailJson"), S(row, "AttachmentName"), S(row, "CreatedBy") ?? string.Empty,
            SqlSession.ReadDate(row["CreatedAtUtc"]!))).ToArray();
    }

    public async Task DeleteProjectPlanAsync(string customerKey, int planId, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        await using var transaction = await session.Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var engagement = await EngagementAsync(session, transaction, customerKey, cancellationToken);
        var owned = await SqlBatch.ScalarAsync(session, transaction, $"SELECT COUNT(*) FROM {session.Table("dlv", "ProjectPlan")} WHERE ProjectPlanId = @id AND EngagementId = @e;",
            SqlBatch.Parameters(("@id", planId), ("@e", engagement)), cancellationToken);
        if (Convert.ToInt32(owned, CultureInfo.InvariantCulture) == 0) return;
        await SqlBatch.ExecuteAsync(session, transaction,
            $"DELETE FROM {session.Table("dlv", "ProjectTaskDependency")} WHERE ProjectTaskId IN (SELECT ProjectTaskId FROM {session.Table("dlv", "ProjectTask")} WHERE ProjectPlanId = @id);",
            SqlBatch.Parameters(("@id", planId)), cancellationToken);
        await SqlBatch.ExecuteAsync(session, transaction, $"DELETE FROM {session.Table("dlv", "ProjectTask")} WHERE ProjectPlanId = @id;", SqlBatch.Parameters(("@id", planId)), cancellationToken);
        await SqlBatch.ExecuteAsync(session, transaction, $"DELETE FROM {session.Table("dlv", "ProjectPlan")} WHERE ProjectPlanId = @id;", SqlBatch.Parameters(("@id", planId)), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static string? Fit(string? value, int length) => value is null ? null : value.Length <= length ? value : value[..length];
}
