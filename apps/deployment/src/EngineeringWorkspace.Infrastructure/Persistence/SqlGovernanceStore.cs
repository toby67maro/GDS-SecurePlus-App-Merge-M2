using System.Data;
using System.Data.Common;
using System.Globalization;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Governance;
using EngineeringWorkspace.Infrastructure.Database;
using static EngineeringWorkspace.Infrastructure.Persistence.SqlBatch;

namespace EngineeringWorkspace.Infrastructure.Persistence;

/// <summary>
/// Customer exceptions, specifications (and assignments), baselines, change requests (and executions) and the edit
/// audit in the secureplus extension tables (V0006), for SQL Server, Azure SQL and SQLite. All SQL is parameterized.
/// </summary>
public sealed class SqlGovernanceStore(IDatabaseStatusService database, DatabaseConnectionFactory connections, TimeProvider timeProvider)
    : ICustomerExceptionRepository, ISpecificationRepository, IBaselineRepository, IChangeRequestRepository, IEntityAuditRepository
{
    public bool IsAvailable => database.Settings.Provider != DatabaseProvider.None && database.LatestStatus?.State == DatabaseOverallState.Ready;

    private static string Str(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    private static int Int(object? value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);
    private static bool Bool(object? value) => value is not null && Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    private static DateTimeOffset? Date(object? value) => value is null ? null : SqlSession.ReadDate(value);

    // ----- Exceptions -----

    public async Task<IReadOnlyList<CustomerExceptionRecord>> ListExceptionsAsync(string? customerKey, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        var rows = await QueryAsync(session, null,
            $"SELECT ExceptionId, CustomerKey, ControlId, Reason, ApprovedBy, ExpiresAtUtc, StatusCode, CreatedBy, CreatedAtUtc FROM {session.Table("secureplus", "CustomerException")}" +
            (customerKey is null ? string.Empty : " WHERE CustomerKey = @customer") + " ORDER BY CreatedAtUtc DESC;",
            customerKey is null ? null : Parameters(("@customer", customerKey)), cancellationToken);
        return rows.Select(row => new CustomerExceptionRecord(Int(row["ExceptionId"]), Str(row["CustomerKey"]), Str(row["ControlId"]), Str(row["Reason"]),
            row["ApprovedBy"] as string, Date(row["ExpiresAtUtc"]), Str(row["StatusCode"]), Str(row["CreatedBy"]), Date(row["CreatedAtUtc"])!.Value)).ToArray();
    }

    public async Task<CustomerExceptionRecord> SaveExceptionAsync(CustomerExceptionRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var session = await OpenAsync(cancellationToken);
        var table = session.Table("secureplus", "CustomerException");
        var now = timeProvider.GetUtcNow();
        var values = Parameters(("@customer", Text(record.CustomerKey, 64)), ("@control", Text(record.ControlId, 150)), ("@reason", Text(record.Reason, 2000) ?? "Not given"),
            ("@approved", Text(record.ApprovedBy, 256)), ("@expires", record.ExpiresAtUtc is { } expires ? session.Date(expires) : null), ("@status", record.StatusCode),
            ("@by", Text(record.CreatedBy, 256)), ("@created", session.Date(record.ExceptionId > 0 ? record.CreatedAtUtc : now)), ("@modified", session.Date(now)),
            ("@id", record.ExceptionId));
        var id = record.ExceptionId;
        if (id > 0)
        {
            await ExecuteAsync(session, null,
                $"UPDATE {table} SET CustomerKey = @customer, ControlId = @control, Reason = @reason, ApprovedBy = @approved, ExpiresAtUtc = @expires, StatusCode = @status, ModifiedAtUtc = @modified WHERE ExceptionId = @id;",
                values, cancellationToken);
        }
        else
        {
            id = await InsertIdAsync(session, null,
                $"INSERT INTO {table} (CustomerKey, ControlId, Reason, ApprovedBy, ExpiresAtUtc, StatusCode, CreatedBy, CreatedAtUtc, ModifiedAtUtc) VALUES (@customer, @control, @reason, @approved, @expires, @status, @by, @created, @modified);",
                values, cancellationToken);
        }

        var saved = (await ListExceptionsAsync(record.CustomerKey, cancellationToken)).Single(item => item.ExceptionId == id);
        await MirrorExceptionAsync(session, saved, now, cancellationToken);
        return saved;
    }

    /// <summary>Approved exceptions also appear in the EaaS table cust.Exception (linked to the control's pol.Artifact); others are removed from it.</summary>
    private static async Task MirrorExceptionAsync(SqlSession session, CustomerExceptionRecord record, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var mirror = session.Table("cust", "Exception");
        await ExecuteAsync(session, null, $"DELETE FROM {mirror} WHERE SecurePlusExceptionId = @id;", Parameters(("@id", record.ExceptionId)), cancellationToken);
        if (record.StatusCode != ExceptionStatusCodes.Approved) return;
        var engagement = (await QueryAsync(session, null, $"SELECT EngagementId FROM {session.Table("secureplus", "CustomerLink")} WHERE CustomerKey = @customer;",
            Parameters(("@customer", record.CustomerKey)), cancellationToken)).FirstOrDefault()?["EngagementId"];
        var artifact = (await QueryAsync(session, null, $"SELECT ArtifactId FROM {session.Table("secureplus", "CatalogControl")} WHERE ControlId = @control;",
            Parameters(("@control", record.ControlId)), cancellationToken)).FirstOrDefault()?["ArtifactId"];
        if (engagement is null) return;
        object? expires = record.ExpiresAtUtc is { } date ? session.Sqlite ? date.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : date.UtcDateTime.Date : null;
        await ExecuteAsync(session, null,
            $"INSERT INTO {mirror} (EngagementId, ArtifactId, ExceptionTypeCode, Reason, ApprovedAt, ExpiresOn, ReviewCadence, SecurePlusExceptionId) " +
            "VALUES (@engagement, @artifact, 'RiskAcceptance', @reason, @approved, @expires, @cadence, @id);",
            Parameters(("@engagement", engagement), ("@artifact", artifact), ("@reason", Text($"{record.ControlId}: {record.Reason} (approved by {record.ApprovedBy ?? "unknown"})", 2000)),
                ("@approved", session.Date(now)), ("@expires", expires), ("@cadence", record.ExpiresAtUtc is null ? "Annual review" : "Until expiry"), ("@id", record.ExceptionId)),
            cancellationToken);
    }

    public async Task DeleteExceptionAsync(int exceptionId, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        await ExecuteAsync(session, null, $"DELETE FROM {session.Table("secureplus", "CustomerException")} WHERE ExceptionId = @id;", Parameters(("@id", exceptionId)), cancellationToken);
        await ExecuteAsync(session, null, $"DELETE FROM {session.Table("cust", "Exception")} WHERE SecurePlusExceptionId = @id;", Parameters(("@id", exceptionId)), cancellationToken);
    }

    // ----- Specifications -----

    public async Task<IReadOnlyList<SpecificationRecord>> ListSpecificationsAsync(CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        var items = await ReadItemsAsync(session, "SpecificationItem", "SpecificationKey", cancellationToken);
        var assignments = (await QueryAsync(session, null, $"SELECT CustomerKey, SpecificationKey FROM {session.Table("secureplus", "SpecificationAssignment")};", null, cancellationToken))
            .GroupBy(row => Str(row["SpecificationKey"]), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(row => Str(row["CustomerKey"])).ToArray(), StringComparer.OrdinalIgnoreCase);
        var rows = await QueryAsync(session, null,
            $"SELECT SpecificationKey, Name, Description, IsStandard, IsSystem, IsModified, BasedOnKey, StatusCode, CreatedBy, CreatedAtUtc, ModifiedBy, ModifiedAtUtc FROM {session.Table("secureplus", "Specification")} ORDER BY Name;",
            null, cancellationToken);
        return rows.Select(row =>
        {
            var key = Str(row["SpecificationKey"]);
            return new SpecificationRecord(key, Str(row["Name"]), row["Description"] as string, Bool(row["IsStandard"]), Bool(row["IsSystem"]), Bool(row["IsModified"]),
                row["BasedOnKey"] as string, Str(row["StatusCode"]), items.GetValueOrDefault(key) ?? [], assignments.GetValueOrDefault(key) ?? [], Str(row["CreatedBy"]),
                Date(row["CreatedAtUtc"])!.Value, row["ModifiedBy"] as string, Date(row["ModifiedAtUtc"]));
        }).ToArray();
    }

    public async Task<SpecificationRecord> SaveSpecificationAsync(SpecificationRecord record, string actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var session = await OpenAsync(cancellationToken);
        await using var transaction = await session.Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var table = session.Table("secureplus", "Specification");
        var now = session.Date(timeProvider.GetUtcNow());
        var exists = await CountAsync(session, transaction, $"SELECT COUNT(*) FROM {table} WHERE SpecificationKey = @key;", Parameters(("@key", record.SpecificationKey)),
            cancellationToken) > 0;
        var values = Parameters(("@key", Text(record.SpecificationKey, 64)), ("@name", Text(record.Name, 200)), ("@description", Text(record.Description, 2000)),
            ("@standard", session.Bool(record.IsStandard)), ("@system", session.Bool(record.IsSystem)), ("@modified", session.Bool(record.IsSystem || record.IsModified)),
            ("@basedOn", Text(record.BasedOnKey, 64)), ("@status", record.StatusCode), ("@actor", Text(actor, 256)), ("@now", now));
        await ExecuteAsync(session, transaction, exists
            ? $"UPDATE {table} SET Name = @name, Description = @description, IsStandard = @standard, IsModified = @modified, BasedOnKey = @basedOn, StatusCode = @status, ModifiedBy = @actor, ModifiedAtUtc = @now WHERE SpecificationKey = @key;"
            : $"INSERT INTO {table} (SpecificationKey, Name, Description, IsStandard, IsSystem, IsModified, BasedOnKey, StatusCode, CreatedBy, CreatedAtUtc) VALUES (@key, @name, @description, @standard, @system, @modified, @basedOn, @status, @actor, @now);",
            values, cancellationToken);
        await ReplaceItemsAsync(session, transaction, "SpecificationItem", "SpecificationKey", record.SpecificationKey, record.Items, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ListSpecificationsAsync(cancellationToken)).Single(item => item.SpecificationKey == record.SpecificationKey);
    }

    public async Task DeleteSpecificationAsync(string specificationKey, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        await using var transaction = await session.Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        foreach (var table in new[] { "SpecificationAssignment", "SpecificationItem", "Specification" })
            await ExecuteAsync(session, transaction, $"DELETE FROM {session.Table("secureplus", table)} WHERE SpecificationKey = @key;", Parameters(("@key", specificationKey)),
                cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task AssignSpecificationAsync(string customerKey, string? specificationKey, string actor, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        await using var transaction = await session.Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var table = session.Table("secureplus", "SpecificationAssignment");
        await ExecuteAsync(session, transaction, $"DELETE FROM {table} WHERE CustomerKey = @customer;", Parameters(("@customer", customerKey)), cancellationToken);
        if (specificationKey is not null)
            await ExecuteAsync(session, transaction, $"INSERT INTO {table} (CustomerKey, SpecificationKey, AssignedBy, AssignedAtUtc) VALUES (@customer, @key, @actor, @now);",
                Parameters(("@customer", customerKey), ("@key", specificationKey), ("@actor", Text(actor, 256)), ("@now", session.Date(timeProvider.GetUtcNow()))), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // ----- Baselines -----

    public async Task<IReadOnlyList<BaselineRecord>> ListBaselinesAsync(CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        var items = await ReadItemsAsync(session, "BaselineItem", "BaselineKey", cancellationToken);
        var rows = await QueryAsync(session, null,
            $"SELECT BaselineKey, Name, Description, IsActive, IsSystem, IsModified, CreatedBy, CreatedAtUtc, ModifiedBy, ModifiedAtUtc FROM {session.Table("secureplus", "Baseline")} ORDER BY Name;",
            null, cancellationToken);
        return rows.Select(row =>
        {
            var key = Str(row["BaselineKey"]);
            return new BaselineRecord(key, Str(row["Name"]), row["Description"] as string, Bool(row["IsActive"]), Bool(row["IsSystem"]), Bool(row["IsModified"]),
                items.GetValueOrDefault(key) ?? [], Str(row["CreatedBy"]), Date(row["CreatedAtUtc"])!.Value, row["ModifiedBy"] as string, Date(row["ModifiedAtUtc"]));
        }).ToArray();
    }

    public async Task<BaselineRecord> SaveBaselineAsync(BaselineRecord record, string actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var session = await OpenAsync(cancellationToken);
        await using var transaction = await session.Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var table = session.Table("secureplus", "Baseline");
        var exists = await CountAsync(session, transaction, $"SELECT COUNT(*) FROM {table} WHERE BaselineKey = @key;", Parameters(("@key", record.BaselineKey)), cancellationToken) > 0;
        var values = Parameters(("@key", Text(record.BaselineKey, 64)), ("@name", Text(record.Name, 200)), ("@description", Text(record.Description, 2000)),
            ("@active", session.Bool(record.IsActive)), ("@system", session.Bool(record.IsSystem)), ("@modified", session.Bool(record.IsSystem || record.IsModified)),
            ("@actor", Text(actor, 256)), ("@now", session.Date(timeProvider.GetUtcNow())));
        await ExecuteAsync(session, transaction, exists
            ? $"UPDATE {table} SET Name = @name, Description = @description, IsActive = @active, IsModified = @modified, ModifiedBy = @actor, ModifiedAtUtc = @now WHERE BaselineKey = @key;"
            : $"INSERT INTO {table} (BaselineKey, Name, Description, IsActive, IsSystem, IsModified, CreatedBy, CreatedAtUtc) VALUES (@key, @name, @description, @active, @system, @modified, @actor, @now);",
            values, cancellationToken);
        await ReplaceItemsAsync(session, transaction, "BaselineItem", "BaselineKey", record.BaselineKey, record.Items, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ListBaselinesAsync(cancellationToken)).Single(item => item.BaselineKey == record.BaselineKey);
    }

    public async Task SetBaselineActiveAsync(string baselineKey, bool isActive, string actor, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        await ExecuteAsync(session, null,
            $"UPDATE {session.Table("secureplus", "Baseline")} SET IsActive = @active, ModifiedBy = @actor, ModifiedAtUtc = @now WHERE BaselineKey = @key;",
            Parameters(("@active", session.Bool(isActive)), ("@actor", Text(actor, 256)), ("@now", session.Date(timeProvider.GetUtcNow())), ("@key", baselineKey)), cancellationToken);
    }

    public async Task DeleteBaselineAsync(string baselineKey, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        await using var transaction = await session.Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        foreach (var table in new[] { "BaselineItem", "Baseline" })
            await ExecuteAsync(session, transaction, $"DELETE FROM {session.Table("secureplus", table)} WHERE BaselineKey = @key;", Parameters(("@key", baselineKey)), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // ----- Change requests -----

    public async Task<IReadOnlyList<ChangeRequestRecord>> ListChangeRequestsAsync(string? customerKey, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        var items = (await QueryAsync(session, null, $"SELECT ChangeRequestId, ItemType, ItemKey FROM {session.Table("secureplus", "ChangeRequestItem")};", null, cancellationToken))
            .GroupBy(row => Int(row["ChangeRequestId"]))
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ProfileItem>)group.Select(row => new ProfileItem(Str(row["ItemType"]), Str(row["ItemKey"]))).ToArray());
        var rows = await QueryAsync(session, null,
            $"SELECT ChangeRequestId, CustomerKey, Title, Description, Risk, Impact, RollbackPlan, RequestedBy, ApprovedBy, Owner, WindowStartUtc, WindowEndUtc, StatusCode, " +
            $"DecisionNote, CreatedAtUtc, ModifiedAtUtc, DecidedAtUtc, CompletedAtUtc FROM {session.Table("secureplus", "ChangeRequest")}" +
            (customerKey is null ? string.Empty : " WHERE CustomerKey = @customer") + " ORDER BY ModifiedAtUtc DESC;",
            customerKey is null ? null : Parameters(("@customer", customerKey)), cancellationToken);
        return rows.Select(row =>
        {
            var id = Int(row["ChangeRequestId"]);
            return new ChangeRequestRecord(id, Str(row["CustomerKey"]), Str(row["Title"]), row["Description"] as string, Str(row["Risk"]), row["Impact"] as string,
                row["RollbackPlan"] as string, Str(row["RequestedBy"]), row["ApprovedBy"] as string, row["Owner"] as string, Date(row["WindowStartUtc"]), Date(row["WindowEndUtc"]),
                Str(row["StatusCode"]), row["DecisionNote"] as string, Date(row["CreatedAtUtc"])!.Value, Date(row["ModifiedAtUtc"])!.Value, Date(row["DecidedAtUtc"]),
                Date(row["CompletedAtUtc"]), items.GetValueOrDefault(id) ?? []);
        }).ToArray();
    }

    public async Task<ChangeRequestRecord> SaveChangeRequestAsync(ChangeRequestRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var session = await OpenAsync(cancellationToken);
        await using var transaction = await session.Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var table = session.Table("secureplus", "ChangeRequest");
        var now = timeProvider.GetUtcNow();
        object? D(DateTimeOffset? value) => value is { } date ? session.Date(date) : null;
        var values = Parameters(("@customer", Text(record.CustomerKey, 64)), ("@title", Text(record.Title, 300)), ("@description", Text(record.Description)),
            ("@risk", record.Risk), ("@impact", Text(record.Impact, 2000)), ("@rollback", Text(record.RollbackPlan)), ("@requested", Text(record.RequestedBy, 256)),
            ("@approved", Text(record.ApprovedBy, 256)), ("@owner", Text(record.Owner, 256)), ("@start", D(record.WindowStartUtc)), ("@end", D(record.WindowEndUtc)),
            ("@status", record.StatusCode), ("@note", Text(record.DecisionNote, 2000)), ("@created", session.Date(record.ChangeRequestId > 0 ? record.CreatedAtUtc : now)),
            ("@modified", session.Date(now)), ("@decided", D(record.DecidedAtUtc)), ("@completed", D(record.CompletedAtUtc)), ("@id", record.ChangeRequestId));
        var id = record.ChangeRequestId;
        if (id > 0)
        {
            await ExecuteAsync(session, transaction,
                $"UPDATE {table} SET CustomerKey = @customer, Title = @title, Description = @description, Risk = @risk, Impact = @impact, RollbackPlan = @rollback, " +
                "RequestedBy = @requested, ApprovedBy = @approved, Owner = @owner, WindowStartUtc = @start, WindowEndUtc = @end, StatusCode = @status, DecisionNote = @note, " +
                "ModifiedAtUtc = @modified, DecidedAtUtc = @decided, CompletedAtUtc = @completed WHERE ChangeRequestId = @id;", values, cancellationToken);
        }
        else
        {
            id = await InsertIdAsync(session, transaction,
                $"INSERT INTO {table} (CustomerKey, Title, Description, Risk, Impact, RollbackPlan, RequestedBy, ApprovedBy, Owner, WindowStartUtc, WindowEndUtc, StatusCode, " +
                "DecisionNote, CreatedAtUtc, ModifiedAtUtc, DecidedAtUtc, CompletedAtUtc) VALUES (@customer, @title, @description, @risk, @impact, @rollback, @requested, " +
                "@approved, @owner, @start, @end, @status, @note, @created, @modified, @decided, @completed);", values, cancellationToken);
        }

        var items = session.Table("secureplus", "ChangeRequestItem");
        await ExecuteAsync(session, transaction, $"DELETE FROM {items} WHERE ChangeRequestId = @id;", Parameters(("@id", id)), cancellationToken);
        await BulkInsertAsync(session, transaction, items, ["ChangeRequestId", "ItemType", "ItemKey"],
            record.Items.DistinctBy(item => (item.ItemType, item.ItemKey)).Select(item => new object?[] { id, item.ItemType, Text(item.ItemKey, 200) }).ToList(), null, cancellationToken);
        await MirrorChangeRequestAsync(session, transaction, id, record, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ListChangeRequestsAsync(null, cancellationToken)).Single(item => item.ChangeRequestId == id);
    }

    /// <summary>The EaaS view of a change request (deploy.ChangeRequest): title, impact and rollback, decision and dates.</summary>
    private static async Task MirrorChangeRequestAsync(SqlSession session, DbTransaction transaction, int id, ChangeRequestRecord record, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var mirror = session.Table("deploy", "ChangeRequest");
        await ExecuteAsync(session, transaction, $"DELETE FROM {mirror} WHERE SecurePlusChangeRequestId = @id;", Parameters(("@id", id)), cancellationToken);
        var engagement = (await QueryAsync(session, transaction, $"SELECT EngagementId FROM {session.Table("secureplus", "CustomerLink")} WHERE CustomerKey = @customer;",
            Parameters(("@customer", record.CustomerKey)), cancellationToken)).FirstOrDefault()?["EngagementId"];
        if (engagement is null) return;
        var decision = record.StatusCode switch
        {
            ChangeStatusCodes.Approved or ChangeStatusCodes.Scheduled => "Approved",
            ChangeStatusCodes.Completed => "Implemented",
            ChangeStatusCodes.Rejected => "Rejected",
            _ => "Pending"
        };
        var impact = string.Join("\n\n", new[] { record.Description, record.Impact is null ? null : $"Impact: {record.Impact}", record.RollbackPlan is null ? null : $"Rollback: {record.RollbackPlan}",
            $"Risk: {record.Risk}. Status: {record.StatusCode}." }.Where(part => !string.IsNullOrWhiteSpace(part)));
        await ExecuteAsync(session, transaction,
            $"INSERT INTO {mirror} (EngagementId, SourceCode, Title, ImpactAnalysis, DecisionCode, CreatedAt, DecidedAt, SecurePlusChangeRequestId) " +
            "VALUES (@engagement, 'SecurePlus', @title, @impact, @decision, @created, @decided, @id);",
            Parameters(("@engagement", engagement), ("@title", Text(record.Title, 300)), ("@impact", impact), ("@decision", decision),
                ("@created", session.Date(record.ChangeRequestId > 0 ? record.CreatedAtUtc : now)), ("@decided", record.DecidedAtUtc is { } decided ? session.Date(decided) : null), ("@id", id)),
            cancellationToken);
    }

    public async Task DeleteChangeRequestAsync(int changeRequestId, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        await using var transaction = await session.Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await ExecuteAsync(session, transaction, $"DELETE FROM {session.Table("deploy", "ChangeRequest")} WHERE SecurePlusChangeRequestId = @id;", Parameters(("@id", changeRequestId)),
            cancellationToken);
        foreach (var table in new[] { "ChangeExecution", "ChangeRequestItem", "ChangeRequest" })
            await ExecuteAsync(session, transaction, $"DELETE FROM {session.Table("secureplus", table)} WHERE ChangeRequestId = @id;", Parameters(("@id", changeRequestId)),
                cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChangeExecutionRecord>> ListExecutionsAsync(int? changeRequestId, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        var rows = await QueryAsync(session, null,
            $"SELECT ChangeExecutionId, ChangeRequestId, ModeCode, StatusCode, StartedAtUtc, CompletedAtUtc, ExecutedBy, Log, ReassessmentRunId FROM {session.Table("secureplus", "ChangeExecution")}" +
            (changeRequestId is null ? string.Empty : " WHERE ChangeRequestId = @id") + " ORDER BY StartedAtUtc DESC;",
            changeRequestId is null ? null : Parameters(("@id", changeRequestId.Value)), cancellationToken);
        return rows.Select(row => new ChangeExecutionRecord(Int(row["ChangeExecutionId"]), Int(row["ChangeRequestId"]), Str(row["ModeCode"]), Str(row["StatusCode"]),
            Date(row["StartedAtUtc"])!.Value, Date(row["CompletedAtUtc"]), Str(row["ExecutedBy"]), row["Log"] as string,
            row["ReassessmentRunId"] is null ? null : Int(row["ReassessmentRunId"]))).ToArray();
    }

    public async Task<int> StartExecutionAsync(int changeRequestId, string mode, string actor, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        return await InsertIdAsync(session, null,
            $"INSERT INTO {session.Table("secureplus", "ChangeExecution")} (ChangeRequestId, ModeCode, StatusCode, StartedAtUtc, ExecutedBy) VALUES (@id, @mode, @status, @now, @actor);",
            Parameters(("@id", changeRequestId), ("@mode", mode), ("@status", ExecutionStatusCodes.Running), ("@now", session.Date(timeProvider.GetUtcNow())),
                ("@actor", Text(actor, 256))), cancellationToken);
    }

    public async Task CompleteExecutionAsync(int executionId, string status, string log, int? reassessmentRunId, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        await ExecuteAsync(session, null,
            $"UPDATE {session.Table("secureplus", "ChangeExecution")} SET StatusCode = @status, CompletedAtUtc = @now, Log = @log, ReassessmentRunId = @run WHERE ChangeExecutionId = @id;",
            Parameters(("@status", status), ("@now", session.Date(timeProvider.GetUtcNow())), ("@log", log), ("@run", reassessmentRunId), ("@id", executionId)), cancellationToken);
    }

    // ----- Audit -----

    public async Task RecordChangesAsync(IReadOnlyList<EntityAuditRecord> changes, CancellationToken cancellationToken = default)
    {
        if (changes.Count == 0) return;
        await using var session = await OpenAsync(cancellationToken);
        await BulkInsertAsync(session, null, session.Table("secureplus", "EntityAudit"), ["EntityType", "EntityKey", "Property", "OldValue", "NewValue", "ChangedBy", "ChangedAtUtc"],
            changes.Select(change => new object?[]
            {
                Text(change.EntityType, 50), Text(change.EntityKey, 200), Text(change.Property, 200), Text(change.OldValue, 2000), Text(change.NewValue, 2000),
                Text(change.ChangedBy, 256), session.Date(change.ChangedAtUtc)
            }).ToList(), null, cancellationToken);
    }

    public async Task<IReadOnlyList<EntityAuditRecord>> ListChangesAsync(string entityType, string? entityKey, int take = 200, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        var rows = await QueryAsync(session, null,
            $"SELECT EntityAuditId, EntityType, EntityKey, Property, OldValue, NewValue, ChangedBy, ChangedAtUtc FROM {session.Table("secureplus", "EntityAudit")} WHERE EntityType = @type" +
            (entityKey is null ? string.Empty : " AND EntityKey = @key") + " ORDER BY ChangedAtUtc DESC;",
            entityKey is null ? Parameters(("@type", entityType)) : Parameters(("@type", entityType), ("@key", entityKey)), cancellationToken);
        return rows.Take(Math.Max(1, take)).Select(row => new EntityAuditRecord(Convert.ToInt64(row["EntityAuditId"], CultureInfo.InvariantCulture), Str(row["EntityType"]),
            Str(row["EntityKey"]), Str(row["Property"]), row["OldValue"] as string, row["NewValue"] as string, Str(row["ChangedBy"]), Date(row["ChangedAtUtc"])!.Value)).ToArray();
    }

    // ----- Helpers -----

    private static async Task<Dictionary<string, IReadOnlyList<ProfileItem>>> ReadItemsAsync(SqlSession session, string table, string keyColumn, CancellationToken cancellationToken) =>
        (await QueryAsync(session, null, $"SELECT {keyColumn} AS K, ItemType, ItemKey FROM {session.Table("secureplus", table)};", null, cancellationToken))
        .GroupBy(row => Str(row["K"]), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => (IReadOnlyList<ProfileItem>)group.Select(row => new ProfileItem(Str(row["ItemType"]), Str(row["ItemKey"]))).ToArray(),
            StringComparer.OrdinalIgnoreCase);

    private static async Task ReplaceItemsAsync(SqlSession session, DbTransaction transaction, string table, string keyColumn, string key, IReadOnlyList<ProfileItem> items,
        CancellationToken cancellationToken)
    {
        var target = session.Table("secureplus", table);
        await ExecuteAsync(session, transaction, $"DELETE FROM {target} WHERE {keyColumn} = @key;", Parameters(("@key", key)), cancellationToken);
        await BulkInsertAsync(session, transaction, target, [keyColumn, "ItemType", "ItemKey"],
            items.Where(item => !string.IsNullOrWhiteSpace(item.ItemKey)).DistinctBy(item => (item.ItemType, item.ItemKey.ToUpperInvariant()))
                .Select(item => new object?[] { key, item.ItemType, Text(item.ItemKey, 200) }).ToList(), null, cancellationToken);
    }

    private Task<SqlSession> OpenAsync(CancellationToken cancellationToken) =>
        SqlSession.OpenAsync(database, connections, message => new InvalidOperationException(message), cancellationToken);
}
