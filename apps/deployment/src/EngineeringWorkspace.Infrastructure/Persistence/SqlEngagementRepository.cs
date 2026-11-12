using System.Data;
using System.Data.Common;
using System.Globalization;
using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Scheduling;
using EngineeringWorkspace.Application.Auditing;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Customers;
using EngineeringWorkspace.Domain.Workspaces;
using EngineeringWorkspace.Infrastructure.Database;

namespace EngineeringWorkspace.Infrastructure.Persistence;

/// <summary>
/// Customer, workspace and assessment history for SQL Server, Azure SQL and SQLite. Customers become EaaS v3
/// cust.Customer rows with one cust.Engagement each; assessments are cust.AssessmentRun rows with their status and
/// findings in the Secure Plus extension tables (V0003). All SQL is parameterized.
/// </summary>
public sealed partial class SqlEngagementRepository(
    IDatabaseStatusService database,
    DatabaseConnectionFactory connections,
    AuditContext auditContext,
    TimeProvider timeProvider) : IEngagementRepository
{
    public bool IsAvailable =>
        database.Settings.Provider != DatabaseProvider.None && database.LatestStatus?.State == DatabaseOverallState.Ready;

    public async Task UpsertCustomerAsync(CustomerProfile customer, bool isSample, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customer);
        await using var session = await OpenAsync(cancellationToken);
        await using var transaction = await session.Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await ProjectCustomerAsync(session, transaction, customer, isSample, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpsertWorkspaceAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        await using var session = await OpenAsync(cancellationToken);
        await SaveWorkspaceRowAsync(session, workspace, cancellationToken);
    }

    public async Task<CustomerEngagementState> GetCustomerStateAsync(CustomerId customerId, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        var link = await FindLinkAsync(session, null, customerId.Value, cancellationToken);
        if (link is null) return new CustomerEngagementState(false, 0, 0, null);
        var assessments = await ListAssessmentsAsync(session, customerId.Value, cancellationToken);
        await using var command = session.Command(null, $"SELECT COUNT(*) FROM {session.Table("secureplus", "Workspace")} WHERE CustomerId = @customer;");
        DatabaseConnectionFactory.Parameter(command, "@customer", link.CustomerId);
        var workspaces = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return new CustomerEngagementState(true, assessments.Count, workspaces, assessments.FirstOrDefault());
    }

    public async Task<IReadOnlyList<AssessmentSummary>> ListAssessmentsAsync(CustomerId? customerId, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        return await ListAssessmentsAsync(session, customerId?.Value, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredAssessmentFinding>> ListFindingsAsync(int assessmentRunId, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        var findings = new List<StoredAssessmentFinding>();
        await using (var command = session.Command(null,
                         $"SELECT Area, Control, Result, Severity, Recommendation FROM {session.Table("secureplus", "AssessmentFinding")} WHERE AssessmentRunId = @run ORDER BY AssessmentFindingId;"))
        {
            DatabaseConnectionFactory.Parameter(command, "@run", assessmentRunId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                findings.Add(new StoredAssessmentFinding(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        foreach (var result in await ReadResultsAsync(session, assessmentRunId, cancellationToken))
            findings.Add(new StoredAssessmentFinding(result.Area, result.Control, result.Result, result.Severity, result.Recommendation, result.Evidence, result.ControlId));
        return findings;
    }

    public async Task<IReadOnlyList<AssessmentResultItem>> ListResultsAsync(int runId, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        return await ReadResultsAsync(session, runId, cancellationToken);
    }

    public Task<int> StartAssessmentAsync(CustomerId customerId, WorkspaceId? workspaceId, string name, int? pendingRunId, CancellationToken cancellationToken = default) =>
        StartAssessmentAsync(customerId, workspaceId, name, pendingRunId, AssessmentRunSources.Manual, null, cancellationToken);

    public async Task<int> StartAssessmentAsync(CustomerId customerId, WorkspaceId? workspaceId, string name, int? pendingRunId, string source, int? scheduleId,
        CancellationToken cancellationToken = default)
    {
        var runId = pendingRunId;
        if (pendingRunId is { } existing)
        {
            await using var session = await OpenAsync(cancellationToken);
            await using var command = session.Command(null, $"UPDATE {session.Table("secureplus", "AssessmentStatus")} SET StatusCode = @status WHERE AssessmentRunId = @run;");
            DatabaseConnectionFactory.Parameter(command, "@status", AssessmentStatusCodes.Running);
            DatabaseConnectionFactory.Parameter(command, "@run", existing);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) runId = null;
        }

        runId ??= (await RecordAssessmentAsync(new RecordAssessmentRequest(customerId, workspaceId, name, AssessmentStatusCodes.Running, "SecurePlus", null, [], false,
            $"Assessment engine run ({source})."), cancellationToken)).AssessmentRunId;
        await using (var session = await OpenAsync(cancellationToken))
        {
            await SqlBatch.ExecuteAsync(session, null,
                $"UPDATE {session.Table("secureplus", "AssessmentStatus")} SET Source = @source, ScheduleId = @schedule, StartedAtUtc = @started, ResultsSaved = 0, ErrorMessage = NULL WHERE AssessmentRunId = @run;",
                SqlBatch.Parameters(("@source", Truncate(source, 20)), ("@schedule", scheduleId), ("@started", session.Date(timeProvider.GetUtcNow())), ("@run", runId.Value)),
                cancellationToken);
            // A resumed pending run starts over: results of an earlier attempt are replaced.
            await SqlBatch.ExecuteAsync(session, null, $"DELETE FROM {session.Table("secureplus", "AssessmentResult")} WHERE AssessmentRunId = @run;",
                SqlBatch.Parameters(("@run", runId.Value)), cancellationToken);
        }

        return runId.Value;
    }

    public async Task SetScanTypeAsync(int runId, string scanType, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        await SqlBatch.ExecuteAsync(session, null, $"UPDATE {session.Table("secureplus", "AssessmentStatus")} SET ScanType = @type WHERE AssessmentRunId = @run;",
            SqlBatch.Parameters(("@type", ScanTypes.Normalize(scanType)), ("@run", runId)), cancellationToken);
    }

    public async Task AppendResultsAsync(int runId, IReadOnlyList<AssessmentResultItem> results, CancellationToken cancellationToken = default)
    {
        if (results.Count == 0) return;
        await using var session = await OpenAsync(cancellationToken);
        await using var transaction = await session.Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var now = session.Date(timeProvider.GetUtcNow());
        await SqlBatch.BulkInsertAsync(session, transaction, session.Table("secureplus", "AssessmentResult"),
            ["AssessmentRunId", "ControlId", "Area", "Control", "Result", "Severity", "Evidence", "Recommendation", "RecordedAtUtc"],
            results.Select(item => new object?[]
            {
                runId, Truncate(item.ControlId, 100), Truncate(item.Area, 100), Truncate(item.Control, 400), Truncate(item.Result, 20), Truncate(item.Severity, 20),
                item.Evidence is { } evidence ? Truncate(evidence, 2000) : null, item.Recommendation is { } recommendation ? Truncate(recommendation, 1000) : null, now
            }).ToList(), null, cancellationToken);
        await SqlBatch.ExecuteAsync(session, transaction,
            $"UPDATE {session.Table("secureplus", "AssessmentStatus")} SET ResultsSaved = COALESCE(ResultsSaved, 0) + @count WHERE AssessmentRunId = @run;",
            SqlBatch.Parameters(("@count", results.Count), ("@run", runId)), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task FinishAssessmentAsync(int runId, AssessmentRunTotals totals, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(totals);
        await using var session = await OpenAsync(cancellationToken);
        await SqlBatch.ExecuteAsync(session, null,
            $"UPDATE {session.Table("secureplus", "AssessmentStatus")} SET StatusCode = @status, Score = @score, ControlsTotal = @total, ControlsPassed = @passed, " +
            "ResultsSaved = @saved, ErrorMessage = @error, CompletedAtUtc = @completed WHERE AssessmentRunId = @run;",
            SqlBatch.Parameters(("@status", totals.StatusCode), ("@score", totals.Score), ("@total", totals.ControlsTotal), ("@passed", totals.ControlsPassed),
                ("@saved", totals.ResultsSaved), ("@error", totals.ErrorMessage is { } error ? Truncate(error, 2000) : null), ("@completed", session.Date(timeProvider.GetUtcNow())),
                ("@run", runId)), cancellationToken);
        if (totals.StatusCode == AssessmentStatusCodes.Completed) await TryPublishToEaasAsync(runId, cancellationToken);
    }

    public async Task CompleteAssessmentAsync(int runId, string statusCode, decimal? score, IReadOnlyList<AssessmentResultItem> results, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        await using var transaction = await session.Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using (var clear = session.Command(transaction, $"DELETE FROM {session.Table("secureplus", "AssessmentResult")} WHERE AssessmentRunId = @run;"))
        {
            DatabaseConnectionFactory.Parameter(clear, "@run", runId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        // Batched multi-row inserts keep a full catalog run (about 2,000 rows) fast on remote SQL servers.
        const int batchSize = 100;
        for (var offset = 0; offset < results.Count; offset += batchSize)
        {
            var batch = results.Skip(offset).Take(batchSize).ToArray();
            var values = new List<string>();
            await using var insert = session.Command(transaction, string.Empty);
            for (var index = 0; index < batch.Length; index++)
            {
                values.Add($"(@run, @c{index}, @a{index}, @n{index}, @r{index}, @s{index}, @e{index}, @m{index})");
                DatabaseConnectionFactory.Parameter(insert, $"@c{index}", Truncate(batch[index].ControlId, 100));
                DatabaseConnectionFactory.Parameter(insert, $"@a{index}", Truncate(batch[index].Area, 100));
                DatabaseConnectionFactory.Parameter(insert, $"@n{index}", Truncate(batch[index].Control, 400));
                DatabaseConnectionFactory.Parameter(insert, $"@r{index}", Truncate(batch[index].Result, 20));
                DatabaseConnectionFactory.Parameter(insert, $"@s{index}", Truncate(batch[index].Severity, 20));
                DatabaseConnectionFactory.Parameter(insert, $"@e{index}", batch[index].Evidence is { } evidence ? Truncate(evidence, 2000) : null);
                DatabaseConnectionFactory.Parameter(insert, $"@m{index}", batch[index].Recommendation is { } recommendation ? Truncate(recommendation, 1000) : null);
            }

            DatabaseConnectionFactory.Parameter(insert, "@run", runId);
            insert.CommandText = $"INSERT INTO {session.Table("secureplus", "AssessmentResult")} (AssessmentRunId, ControlId, Area, Control, Result, Severity, Evidence, Recommendation) VALUES {string.Join(", ", values)};";
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        var scored = results.Where(result => AssessmentResultValues.IsScored(result.Result)).ToArray();
        await using (var update = session.Command(transaction,
                         $"UPDATE {session.Table("secureplus", "AssessmentStatus")} SET StatusCode = @status, Score = @score, ControlsTotal = @total, ControlsPassed = @passed, CompletedAtUtc = @completed WHERE AssessmentRunId = @run;"))
        {
            DatabaseConnectionFactory.Parameter(update, "@status", statusCode);
            DatabaseConnectionFactory.Parameter(update, "@score", score);
            DatabaseConnectionFactory.Parameter(update, "@total", scored.Length);
            DatabaseConnectionFactory.Parameter(update, "@passed", scored.Count(result => result.Result == AssessmentResultValues.Pass));
            DatabaseConnectionFactory.Parameter(update, "@completed", session.Date(timeProvider.GetUtcNow()));
            DatabaseConnectionFactory.Parameter(update, "@run", runId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        if (statusCode == AssessmentStatusCodes.Completed) await TryPublishToEaasAsync(runId, cancellationToken);
    }

    private const string ScheduleColumns =
        "ScheduleId, Name, CustomerKeys, Frequency, DayOfWeek, DayOfMonth, TimeOfDay, IsEnabled, LastRunUtc, NextRunUtc, CreatedBy, CustomerKey, DaysOfWeek, Months, " +
        "IntervalHours, IntervalWeeks, WindowStart, WindowEnd, MonthlyMode, WeekOfMonth, TimeZoneId, AnchorDate, LastStatus, LastMessage, ModifiedAtUtc, ScanType";

    public async Task<IReadOnlyList<AssessmentSchedule>> ListSchedulesAsync(CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        var rows = await SqlBatch.QueryAsync(session, null, $"SELECT {ScheduleColumns} FROM {session.Table("secureplus", "AssessmentSchedule")} ORDER BY Name;", null, cancellationToken);
        return rows.Select(ReadSchedule).ToArray();
    }

    /// <summary>Reads a schedule; rows written by earlier versions (one weekday, one day of month) are converted.</summary>
    private static AssessmentSchedule ReadSchedule(Dictionary<string, object?> row)
    {
        static int? Int(object? value) => value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        static TimeOnly? Time(object? value) => value is string text && TimeOnly.TryParse(text, CultureInfo.InvariantCulture, out var time) ? time : null;
        var frequency = Enum.TryParse<ScheduleFrequency>(row["Frequency"] as string, true, out var parsed) ? parsed : ScheduleFrequency.Weekly;
        var legacyDay = Int(row["DayOfWeek"]);
        var days = Int(row["DaysOfWeek"]) ?? frequency switch
        {
            ScheduleFrequency.Weekly => ScheduleMasks.Day((DayOfWeek)(legacyDay ?? (int)DayOfWeek.Monday)),
            _ => ScheduleMasks.AllDays
        };
        var recurrence = new ScheduleRecurrence(
            frequency,
            Time(row["TimeOfDay"]) ?? new TimeOnly(6, 0),
            days,
            Int(row["IntervalHours"]) ?? 1,
            Time(row["WindowStart"]),
            Time(row["WindowEnd"]),
            Int(row["IntervalWeeks"]) ?? 1,
            row["AnchorDate"] is string anchor && DateOnly.TryParse(anchor, CultureInfo.InvariantCulture, out var date) ? date : null,
            Int(row["Months"]) ?? ScheduleMasks.AllMonths,
            Enum.TryParse<MonthlyMode>(row["MonthlyMode"] as string, true, out var mode) ? mode : MonthlyMode.DayOfMonth,
            Int(row["DayOfMonth"]) ?? 1,
            Int(row["WeekOfMonth"]) ?? 1,
            legacyDay is { } weekday && Int(row["DaysOfWeek"]) is not null ? (DayOfWeek)weekday : DayOfWeek.Monday);
        var keys = row["CustomerKey"] is string single && single.Length > 0
            ? [single]
            : ((string?)row["CustomerKeys"] ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new AssessmentSchedule(
            Convert.ToInt32(row["ScheduleId"], CultureInfo.InvariantCulture),
            (string)row["Name"]!,
            keys,
            recurrence,
            Convert.ToBoolean(row["IsEnabled"], CultureInfo.InvariantCulture),
            row["TimeZoneId"] as string ?? TimeZoneInfo.Local.Id,
            row["LastRunUtc"] is { } last ? SqlSession.ReadDate(last) : null,
            row["NextRunUtc"] is { } next ? SqlSession.ReadDate(next) : null)
        {
            LastStatus = row["LastStatus"] as string ?? ScheduleStatusCodes.Idle,
            LastMessage = row["LastMessage"] as string,
            CreatedBy = row["CreatedBy"] as string,
            ScanType = ScanTypes.Normalize(row["ScanType"] as string),
            ModifiedAtUtc = row["ModifiedAtUtc"] is { } modified ? SqlSession.ReadDate(modified) : null
        };
    }

    public async Task<AssessmentSchedule> SaveScheduleAsync(AssessmentSchedule schedule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        await using var session = await OpenAsync(cancellationToken);
        var table = session.Table("secureplus", "AssessmentSchedule");
        var insert = schedule.ScheduleId <= 0;
        var recurrence = schedule.Recurrence;
        var now = session.Date(timeProvider.GetUtcNow());
        string? Time(TimeOnly? value) => value?.ToString("HH:mm", CultureInfo.InvariantCulture);
        var values = new (string Column, object? Value)[]
        {
            ("Name", Truncate(schedule.Name, 200)),
            ("CustomerKeys", Truncate(string.Join(",", schedule.CustomerKeys), 4000)),
            ("CustomerKey", schedule.CustomerKeys.Count == 1 ? Truncate(schedule.CustomerKeys[0], 64) : null),
            ("Frequency", recurrence.Frequency.ToString()),
            ("DayOfWeek", (int)recurrence.Weekday),
            ("DayOfMonth", recurrence.DayOfMonth),
            ("TimeOfDay", Time(recurrence.RunTime)),
            ("IsEnabled", session.Bool(schedule.IsEnabled)),
            ("NextRunUtc", schedule.NextRunUtc is { } next ? session.Date(next) : null),
            ("DaysOfWeek", recurrence.DaysOfWeek),
            ("Months", recurrence.Months),
            ("IntervalHours", recurrence.IntervalHours),
            ("IntervalWeeks", recurrence.IntervalWeeks),
            ("WindowStart", Time(recurrence.WindowStart)),
            ("WindowEnd", Time(recurrence.WindowEnd)),
            ("MonthlyMode", recurrence.MonthlyMode.ToString()),
            ("WeekOfMonth", recurrence.WeekOfMonth),
            ("TimeZoneId", Truncate(schedule.TimeZoneId, 100)),
            ("AnchorDate", recurrence.AnchorDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("ScanType", ScanTypes.Normalize(schedule.ScanType)),
            ("ModifiedAtUtc", now)
        };
        int id;
        if (insert)
        {
            var columns = values.Select(value => value.Column).Concat(["LastRunUtc", "LastStatus", "CreatedBy", "CreatedAtUtc"]).ToArray();
            id = await SqlBatch.InsertIdAsync(session, null,
                $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select(column => "@" + column))});",
                command =>
                {
                    foreach (var (column, value) in values) DatabaseConnectionFactory.Parameter(command, "@" + column, value);
                    DatabaseConnectionFactory.Parameter(command, "@LastRunUtc", null);
                    DatabaseConnectionFactory.Parameter(command, "@LastStatus", ScheduleStatusCodes.Idle);
                    DatabaseConnectionFactory.Parameter(command, "@CreatedBy", Truncate(auditContext.Actor, 256));
                    DatabaseConnectionFactory.Parameter(command, "@CreatedAtUtc", now);
                }, cancellationToken);
        }
        else
        {
            id = schedule.ScheduleId;
            await SqlBatch.ExecuteAsync(session, null,
                $"UPDATE {table} SET {string.Join(", ", values.Select(value => $"{value.Column} = @{value.Column}"))} WHERE ScheduleId = @id;",
                command =>
                {
                    foreach (var (column, value) in values) DatabaseConnectionFactory.Parameter(command, "@" + column, value);
                    DatabaseConnectionFactory.Parameter(command, "@id", id);
                }, cancellationToken);
        }

        return (await ListSchedulesAsync(cancellationToken)).Single(item => item.ScheduleId == id);
    }

    public async Task DeleteScheduleAsync(int scheduleId, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        await using var transaction = await session.Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await SqlBatch.ExecuteAsync(session, transaction, $"DELETE FROM {session.Table("secureplus", "ScheduleRun")} WHERE ScheduleId = @id;", SqlBatch.Parameters(("@id", scheduleId)),
            cancellationToken);
        await SqlBatch.ExecuteAsync(session, transaction, $"DELETE FROM {session.Table("secureplus", "AssessmentSchedule")} WHERE ScheduleId = @id;",
            SqlBatch.Parameters(("@id", scheduleId)), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> TryClaimScheduleAsync(int scheduleId, DateTimeOffset? expectedNextRunUtc, DateTimeOffset? nextRunUtc, TimeSpan staleAfter,
        CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var sql = $"UPDATE {session.Table("secureplus", "AssessmentSchedule")} SET LastStatus = @running, LastMessage = NULL, ModifiedAtUtc = @now" +
                  (expectedNextRunUtc is null ? string.Empty : ", NextRunUtc = @next") +
                  " WHERE ScheduleId = @id AND (LastStatus IS NULL OR LastStatus <> @running OR ModifiedAtUtc IS NULL OR ModifiedAtUtc < @stale)" +
                  (expectedNextRunUtc is null ? string.Empty : " AND NextRunUtc = @expected") + ";";
        await using var command = session.Command(null, sql);
        DatabaseConnectionFactory.Parameter(command, "@running", ScheduleStatusCodes.Running);
        DatabaseConnectionFactory.Parameter(command, "@now", session.Date(now));
        DatabaseConnectionFactory.Parameter(command, "@stale", session.Date(now - staleAfter));
        DatabaseConnectionFactory.Parameter(command, "@id", scheduleId);
        if (expectedNextRunUtc is { } expected)
        {
            DatabaseConnectionFactory.Parameter(command, "@expected", session.Date(expected));
            DatabaseConnectionFactory.Parameter(command, "@next", nextRunUtc is { } next ? session.Date(next) : null);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task UpdateScheduleStatusAsync(int scheduleId, string status, string? message, DateTimeOffset? lastRunUtc, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        await SqlBatch.ExecuteAsync(session, null,
            $"UPDATE {session.Table("secureplus", "AssessmentSchedule")} SET LastStatus = @status, LastMessage = @message, ModifiedAtUtc = @now" +
            (lastRunUtc is null ? string.Empty : ", LastRunUtc = @last") + " WHERE ScheduleId = @id;",
            SqlBatch.Parameters(("@status", status), ("@message", message is null ? null : Truncate(message, 1000)), ("@now", session.Date(timeProvider.GetUtcNow())),
                ("@last", lastRunUtc is { } last ? session.Date(last) : null), ("@id", scheduleId)), cancellationToken);
    }

    public async Task SetScheduleEnabledAsync(int scheduleId, bool enabled, DateTimeOffset? nextRunUtc, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        await SqlBatch.ExecuteAsync(session, null,
            $"UPDATE {session.Table("secureplus", "AssessmentSchedule")} SET IsEnabled = @enabled, NextRunUtc = @next, ModifiedAtUtc = @now WHERE ScheduleId = @id;",
            SqlBatch.Parameters(("@enabled", session.Bool(enabled)), ("@next", nextRunUtc is { } next ? session.Date(next) : null), ("@now", session.Date(timeProvider.GetUtcNow())),
                ("@id", scheduleId)), cancellationToken);
    }

    public async Task<int> StartScheduleRunAsync(int scheduleId, string host, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        return await SqlBatch.InsertIdAsync(session, null,
            $"INSERT INTO {session.Table("secureplus", "ScheduleRun")} (ScheduleId, StartedAtUtc, StatusCode, Host) VALUES (@schedule, @started, @status, @host);",
            SqlBatch.Parameters(("@schedule", scheduleId), ("@started", session.Date(timeProvider.GetUtcNow())), ("@status", ScheduleStatusCodes.Running), ("@host", Truncate(host, 50))),
            cancellationToken);
    }

    public async Task CompleteScheduleRunAsync(int scheduleRunId, string status, string? message, IReadOnlyList<int> assessmentRunIds, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        await SqlBatch.ExecuteAsync(session, null,
            $"UPDATE {session.Table("secureplus", "ScheduleRun")} SET CompletedAtUtc = @completed, StatusCode = @status, Message = @message, AssessmentRunIds = @runs WHERE ScheduleRunId = @id;",
            SqlBatch.Parameters(("@completed", session.Date(timeProvider.GetUtcNow())), ("@status", status), ("@message", message is null ? null : Truncate(message, 2000)),
                ("@runs", Truncate(string.Join(",", assessmentRunIds), 1000)), ("@id", scheduleRunId)), cancellationToken);
    }

    public async Task<IReadOnlyList<ScheduleRunInfo>> ListScheduleRunsAsync(int scheduleId, int take, CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(cancellationToken);
        var rows = await SqlBatch.QueryAsync(session, null,
            $"SELECT ScheduleRunId, ScheduleId, StartedAtUtc, CompletedAtUtc, StatusCode, Message, Host, AssessmentRunIds FROM {session.Table("secureplus", "ScheduleRun")} " +
            "WHERE ScheduleId = @id ORDER BY StartedAtUtc DESC;", SqlBatch.Parameters(("@id", scheduleId)), cancellationToken);
        return rows.Take(Math.Max(1, take)).Select(row => new ScheduleRunInfo(Convert.ToInt32(row["ScheduleRunId"], CultureInfo.InvariantCulture),
            Convert.ToInt32(row["ScheduleId"], CultureInfo.InvariantCulture), SqlSession.ReadDate(row["StartedAtUtc"]!),
            row["CompletedAtUtc"] is { } completed ? SqlSession.ReadDate(completed) : null, (string)row["StatusCode"]!, row["Message"] as string, row["Host"] as string,
            row["AssessmentRunIds"] as string)).ToArray();
    }

    private static async Task<IReadOnlyList<AssessmentResultItem>> ReadResultsAsync(SqlSession session, int runId, CancellationToken cancellationToken)
    {
        await using var command = session.Command(null,
            $"SELECT ControlId, Area, Control, Result, Severity, Evidence, Recommendation FROM {session.Table("secureplus", "AssessmentResult")} WHERE AssessmentRunId = @run ORDER BY AssessmentResultId;");
        DatabaseConnectionFactory.Parameter(command, "@run", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<AssessmentResultItem>();
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new AssessmentResultItem(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        return results;
    }

    public async Task<AssessmentSummary> RecordAssessmentAsync(RecordAssessmentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var session = await OpenAsync(cancellationToken);
        await using var transaction = await session.Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var link = await FindLinkAsync(session, transaction, request.CustomerId.Value, cancellationToken)
                   ?? throw new EngagementStoreUnavailableException($"Customer '{request.CustomerId}' is not recorded in the application database.");
        var engagementId = link.EngagementId ?? await CreateEngagementAsync(session, transaction, link.CustomerId, link.DisplayName, request.CustomerId.Value, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var completed = request.StatusCode == AssessmentStatusCodes.Completed ? now : (DateTimeOffset?)null;
        var passed = request.Findings.Count(finding => string.Equals(finding.Result, "Pass", StringComparison.OrdinalIgnoreCase));

        int runId;
        await using (var run = session.Command(transaction,
                         $"INSERT INTO {session.Table("cust", "AssessmentRun")} (EngagementId, ToolCode, RunAt, RunBy, Notes) VALUES (@engagement, @tool, @at, @by, @notes); {session.IdentitySql}"))
        {
            DatabaseConnectionFactory.Parameter(run, "@engagement", engagementId);
            DatabaseConnectionFactory.Parameter(run, "@tool", Truncate(request.ToolCode, 30));
            DatabaseConnectionFactory.Parameter(run, "@at", session.Date(now));
            DatabaseConnectionFactory.Parameter(run, "@by", Truncate(auditContext.Actor, 200));
            DatabaseConnectionFactory.Parameter(run, "@notes", request.Notes is null ? null : Truncate(request.Notes, 1000));
            runId = Convert.ToInt32(await run.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        await using (var status = session.Command(transaction,
                         $"""
                          INSERT INTO {session.Table("secureplus", "AssessmentStatus")}
                              (AssessmentRunId, CustomerKey, WorkspaceId, Name, StatusCode, Score, ControlsTotal, ControlsPassed, RequestedAtUtc, CompletedAtUtc, IsSample)
                          VALUES (@run, @customer, @workspace, @name, @status, @score, @total, @passed, @requested, @completed, @sample);
                          """))
        {
            DatabaseConnectionFactory.Parameter(status, "@run", runId);
            DatabaseConnectionFactory.Parameter(status, "@customer", request.CustomerId.Value);
            DatabaseConnectionFactory.Parameter(status, "@workspace", request.WorkspaceId?.Value);
            DatabaseConnectionFactory.Parameter(status, "@name", Truncate(request.Name, 200));
            DatabaseConnectionFactory.Parameter(status, "@status", request.StatusCode);
            DatabaseConnectionFactory.Parameter(status, "@score", request.Score);
            DatabaseConnectionFactory.Parameter(status, "@total", request.Findings.Count == 0 ? null : request.Findings.Count);
            DatabaseConnectionFactory.Parameter(status, "@passed", request.Findings.Count == 0 ? null : passed);
            DatabaseConnectionFactory.Parameter(status, "@requested", session.Date(now));
            DatabaseConnectionFactory.Parameter(status, "@completed", completed is null ? null : session.Date(completed.Value));
            DatabaseConnectionFactory.Parameter(status, "@sample", session.Bool(request.IsSample));
            await status.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var finding in request.Findings)
        {
            await using var command = session.Command(transaction,
                $"INSERT INTO {session.Table("secureplus", "AssessmentFinding")} (AssessmentRunId, Area, Control, Result, Severity, Recommendation) VALUES (@run, @area, @control, @result, @severity, @recommendation);");
            DatabaseConnectionFactory.Parameter(command, "@run", runId);
            DatabaseConnectionFactory.Parameter(command, "@area", Truncate(finding.Area, 100));
            DatabaseConnectionFactory.Parameter(command, "@control", Truncate(finding.Control, 300));
            DatabaseConnectionFactory.Parameter(command, "@result", Truncate(finding.Result, 20));
            DatabaseConnectionFactory.Parameter(command, "@severity", Truncate(finding.Severity, 20));
            DatabaseConnectionFactory.Parameter(command, "@recommendation", finding.Recommendation is null ? null : Truncate(finding.Recommendation, 1000));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new AssessmentSummary(runId, request.CustomerId.Value, request.WorkspaceId?.Value, request.Name, request.StatusCode, request.Score,
            request.Findings.Count == 0 ? null : request.Findings.Count, request.Findings.Count == 0 ? null : passed, now, completed, auditContext.Actor, request.IsSample);
    }

    private async Task<IReadOnlyList<AssessmentSummary>> ListAssessmentsAsync(SqlSession session, string? customerKey, CancellationToken cancellationToken)
    {
        var sql = $"""
                   SELECT s.AssessmentRunId, s.CustomerKey, s.WorkspaceId, s.Name, s.StatusCode, s.Score, s.ControlsTotal, s.ControlsPassed,
                          s.RequestedAtUtc, s.CompletedAtUtc, r.RunBy, s.IsSample, s.Source, s.ScheduleId, s.ResultsSaved, s.ErrorMessage, s.ScanType
                   FROM {session.Table("secureplus", "AssessmentStatus")} AS s
                   LEFT JOIN {session.Table("cust", "AssessmentRun")} AS r ON r.AssessmentRunId = s.AssessmentRunId
                   {(customerKey is null ? string.Empty : "WHERE s.CustomerKey = @customer")}
                   ORDER BY s.RequestedAtUtc DESC, s.AssessmentRunId DESC;
                   """;
        await using var command = session.Command(null, sql);
        if (customerKey is not null) DatabaseConnectionFactory.Parameter(command, "@customer", customerKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<AssessmentSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AssessmentSummary(
                Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture),
                reader.IsDBNull(7) ? null : Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture),
                SqlSession.ReadDate(reader.GetValue(8)),
                reader.IsDBNull(9) ? null : SqlSession.ReadDate(reader.GetValue(9)),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                Convert.ToBoolean(reader.GetValue(11), CultureInfo.InvariantCulture))
            {
                Source = reader.IsDBNull(12) ? AssessmentRunSources.Manual : reader.GetString(12),
                ScheduleId = reader.IsDBNull(13) ? null : Convert.ToInt32(reader.GetValue(13), CultureInfo.InvariantCulture),
                ResultsSaved = reader.IsDBNull(14) ? null : Convert.ToInt32(reader.GetValue(14), CultureInfo.InvariantCulture),
                ErrorMessage = reader.IsDBNull(15) ? null : reader.GetString(15),
                ScanType = reader.IsDBNull(16) ? ScanTypes.InDepth : ScanTypes.Normalize(reader.GetString(16))
            });
        }

        return results;
    }

    private async Task<CustomerLinkRow> EnsureCustomerAsync(SqlSession session, DbTransaction transaction, string key, string name, bool isSample, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (await FindLinkAsync(session, transaction, key, cancellationToken) is { } existing)
        {
            await using var update = session.Command(transaction, $"UPDATE {session.Table("cust", "Customer")} SET Name = @name, IsActive = @active WHERE CustomerId = @id;");
            DatabaseConnectionFactory.Parameter(update, "@name", Truncate(name, 200));
            DatabaseConnectionFactory.Parameter(update, "@active", session.Bool(true));
            DatabaseConnectionFactory.Parameter(update, "@id", existing.CustomerId);
            await update.ExecuteNonQueryAsync(cancellationToken);
            await using var touch = session.Command(transaction, $"UPDATE {session.Table("secureplus", "CustomerLink")} SET DisplayName = @name, ModifiedAtUtc = @now WHERE CustomerKey = @key;");
            DatabaseConnectionFactory.Parameter(touch, "@name", Truncate(name, 200));
            DatabaseConnectionFactory.Parameter(touch, "@now", session.Date(now));
            DatabaseConnectionFactory.Parameter(touch, "@key", key);
            await touch.ExecuteNonQueryAsync(cancellationToken);
            var engagement = existing.EngagementId ?? await CreateEngagementAsync(session, transaction, existing.CustomerId, name, key, cancellationToken);
            return existing with { DisplayName = name, EngagementId = engagement };
        }

        int customerId;
        await using (var insert = session.Command(transaction,
                         $"INSERT INTO {session.Table("cust", "Customer")} (Name, Notes, IsActive) VALUES (@name, @notes, @active); {session.IdentitySql}"))
        {
            DatabaseConnectionFactory.Parameter(insert, "@name", Truncate(name, 200));
            DatabaseConnectionFactory.Parameter(insert, "@notes", isSample ? "Sample customer created by Secure Plus Agent setup." : $"Secure Plus customer '{key}'.");
            DatabaseConnectionFactory.Parameter(insert, "@active", session.Bool(true));
            customerId = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        await using (var link = session.Command(transaction,
                         $"INSERT INTO {session.Table("secureplus", "CustomerLink")} (CustomerKey, CustomerId, EngagementId, DisplayName, IsSample, CreatedAtUtc, ModifiedAtUtc) VALUES (@key, @id, NULL, @name, @sample, @now, @now);"))
        {
            DatabaseConnectionFactory.Parameter(link, "@key", key);
            DatabaseConnectionFactory.Parameter(link, "@id", customerId);
            DatabaseConnectionFactory.Parameter(link, "@name", Truncate(name, 200));
            DatabaseConnectionFactory.Parameter(link, "@sample", session.Bool(isSample));
            DatabaseConnectionFactory.Parameter(link, "@now", session.Date(now));
            await link.ExecuteNonQueryAsync(cancellationToken);
        }

        var engagementId = await CreateEngagementAsync(session, transaction, customerId, name, key, cancellationToken);
        return new CustomerLinkRow(key, customerId, engagementId, name);
    }

    private async Task<int> CreateEngagementAsync(SqlSession session, DbTransaction? transaction, int customerId, string name, string key, CancellationToken cancellationToken)
    {
        int engagementId;
        await using (var insert = session.Command(transaction,
                         $"INSERT INTO {session.Table("cust", "Engagement")} (CustomerId, Name, EngagementTypeCode, StatusCode, StartDate) VALUES (@customer, @name, @type, @status, @start); {session.IdentitySql}"))
        {
            DatabaseConnectionFactory.Parameter(insert, "@customer", customerId);
            DatabaseConnectionFactory.Parameter(insert, "@name", Truncate($"{name} - Secure Plus", 200));
            DatabaseConnectionFactory.Parameter(insert, "@type", "SecurePlus");
            DatabaseConnectionFactory.Parameter(insert, "@status", "Active");
            var today = timeProvider.GetUtcNow().UtcDateTime.Date;
            DatabaseConnectionFactory.Parameter(insert, "@start", session.Sqlite ? today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : today);
            engagementId = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        await using var link = session.Command(transaction, $"UPDATE {session.Table("secureplus", "CustomerLink")} SET EngagementId = @engagement WHERE CustomerKey = @key;");
        DatabaseConnectionFactory.Parameter(link, "@engagement", engagementId);
        DatabaseConnectionFactory.Parameter(link, "@key", key);
        await link.ExecuteNonQueryAsync(cancellationToken);
        return engagementId;
    }

    private static async Task<CustomerLinkRow?> FindLinkAsync(SqlSession session, DbTransaction? transaction, string key, CancellationToken cancellationToken)
    {
        await using var command = session.Command(transaction, $"SELECT CustomerId, EngagementId, DisplayName FROM {session.Table("secureplus", "CustomerLink")} WHERE CustomerKey = @key;");
        DatabaseConnectionFactory.Parameter(command, "@key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new CustomerLinkRow(key, Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
            reader.IsDBNull(1) ? null : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture), reader.GetString(2));
    }

    private Task<SqlSession> OpenAsync(CancellationToken cancellationToken) =>
        SqlSession.OpenAsync(database, connections, message => new EngagementStoreUnavailableException(message), cancellationToken);

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    private sealed record CustomerLinkRow(string Key, int CustomerId, int? EngagementId, string DisplayName);
}
