using System.Data;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ArchiTech.Api.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace ArchiTech.Api.Integrations;

public sealed class BridgeOptions
{
    public const string SectionName = "Bridge";

    /// <summary>Master switch. When false the form works standalone and engagements stay Pending/NotConfigured.</summary>
    public bool Enabled { get; set; }

    /// <summary>Connection string of the Secure Plus (EaaS v3) database. Supply via secrets or environment, never source control.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Run database\bridge\*.sql (idempotent) at startup. Needs rights to create objects in the secureplus schema.</summary>
    public bool ApplySchema { get; set; }

    public int ReconcileIntervalSeconds { get; set; } = 120;
    public int ReconcileBatchSize { get; set; } = 25;
}

public sealed record BridgeSyncResult(bool Succeeded, string Status, string Message, string? CustomerKey = null);

public sealed record SalesOverviewRow(
    Guid SalesEngagementId,
    string CustomerKey,
    string? CustomerName,
    string? Salesperson,
    string? OwnerEmail,
    DateTime SubmittedAtUtc,
    DateTime SyncedAtUtc,
    string? AssessmentStatus,
    int? AssessmentScore);

public sealed record AssessmentSummary(
    string CustomerKey,
    string? AssessmentName,
    string? StatusCode,
    int? Score,
    int? ControlsTotal,
    int? ControlsPassed,
    int ControlsFailed,
    int ControlsPartial,
    int CriticalGaps,
    int HighGaps,
    DateTime? CompletedAtUtc);

/// <summary>
/// Connects the sales form to the Secure Plus deployment database. The form never owns the customer
/// profile outright: it merges its fields into the existing ProfileJson so engineer-owned data
/// (tenants, integrations, assessments) is preserved.
/// </summary>
public sealed partial class DeploymentBridge(IOptions<BridgeOptions> options, ILogger<DeploymentBridge> logger)
{
    private const int VersionConflict = 51001;
    private readonly BridgeOptions _options = options.Value;

    public bool IsConfigured => _options.Enabled && !string.IsNullOrWhiteSpace(_options.ConnectionString);

    public async Task ApplySchemaAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured || !_options.ApplySchema) return;
        var assembly = Assembly.GetExecutingAssembly();
        var scripts = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("bridge.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var name in scripts)
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            var text = await reader.ReadToEndAsync(cancellationToken);
            foreach (var batch in SplitBatches(text))
            {
                await using var command = new SqlCommand(batch, connection) { CommandTimeout = 120 };
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            logger.LogInformation("Applied bridge script {Script}", name);
        }
    }

    public static IEnumerable<string> SplitBatches(string script) =>
        GoSeparator().Split(script).Select(batch => batch.Trim()).Where(batch => batch.Length > 0);

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();

    public static string Slug(string name)
    {
        var chars = name.Trim().ToLowerInvariant()
            .Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-').ToArray();
        var slug = Regex.Replace(new string(chars), "-{2,}", "-").Trim('-');
        if (slug.Length == 0) slug = "customer";
        return slug.Length <= 64 ? slug : slug[..64].TrimEnd('-');
    }

    public async Task<BridgeSyncResult> SyncAsync(Engagement engagement, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return new BridgeSyncResult(false, "NotConfigured", "Deployment bridge is not configured.");
        try
        {
            await using var connection = new SqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            var customerKey = await ResolveCustomerKeyAsync(connection, engagement, cancellationToken);
            for (var attempt = 1; ; attempt++)
            {
                var (json, version) = await ReadProfileAsync(connection, customerKey, cancellationToken);
                var merged = MergeProfile(json, engagement);
                try
                {
                    await UpsertAsync(connection, engagement, customerKey, merged, version, cancellationToken);
                    await PublishPlanAsync(connection, engagement, customerKey, cancellationToken);
                    return new BridgeSyncResult(true, "Synced", "Synced to the deployment database.", customerKey);
                }
                catch (SqlException exception) when (exception.Number == VersionConflict && attempt < 4)
                {
                    logger.LogInformation("Profile version conflict for {CustomerKey}; retrying ({Attempt})", customerKey, attempt);
                }
            }
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException or JsonException)
        {
            logger.LogWarning(exception, "Deployment bridge sync failed for engagement {EngagementId}", engagement.Id);
            var message = exception is SqlException sql ? $"SQL error {sql.Number}: {sql.Message}" : exception.Message;
            return new BridgeSyncResult(false, "Failed", Truncate(message, 290));
        }
    }

    private static async Task<string> ResolveCustomerKeyAsync(
        SqlConnection connection, Engagement engagement, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT CustomerKey FROM [secureplus].[SalesCustomerMap] WHERE SalesCustomerId = @id", connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = engagement.CustomerId;
        return await command.ExecuteScalarAsync(cancellationToken) as string ?? Slug(engagement.Customer.Name);
    }

    private static async Task<(string? Json, int? Version)> ReadProfileAsync(
        SqlConnection connection, string customerKey, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT ProfileJson, [Version] FROM [secureplus].[CustomerProfile] WHERE CustomerKey = @key", connection);
        command.Parameters.Add("@key", SqlDbType.NVarChar, 64).Value = customerKey;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetString(0), reader.GetInt32(1))
            : (null, null);
    }

    /// <summary>Merges sales-owned fields into the existing profile without discarding engineer-owned data.</summary>
    public static string MergeProfile(string? existingJson, Engagement engagement)
    {
        var root = string.IsNullOrWhiteSpace(existingJson)
            ? new JsonObject()
            : JsonNode.Parse(existingJson) as JsonObject ?? new JsonObject();
        root["schema"] ??= 1;
        if (string.IsNullOrWhiteSpace(root["name"]?.GetValue<string>())) root["name"] = engagement.Customer.Name;

        var contacts = root["contacts"] as JsonArray ?? new JsonArray();
        root["contacts"] = contacts;
        var email = engagement.Customer.ContactEmail;
        if (!string.IsNullOrWhiteSpace(email) && !contacts.Any(c =>
                string.Equals(c?["email"]?.GetValue<string>(), email, StringComparison.OrdinalIgnoreCase)))
        {
            contacts.Add(new JsonObject
            {
                ["name"] = engagement.Customer.ContactName ?? email,
                ["email"] = email,
                ["role"] = "Sales contact",
                ["isDecisionOwner"] = false,
                ["isPrimary"] = !contacts.Any(c => c?["isPrimary"]?.GetValue<bool>() == true),
                ["isChangeApprover"] = false
            });
        }

        var tags = root["tags"] as JsonArray ?? new JsonArray();
        root["tags"] = tags;
        if (!tags.Any(t => t?.GetValue<string>() == "sales-intake")) tags.Add("sales-intake");

        var organization = root["organization"] as JsonObject ?? new JsonObject();
        root["organization"] = organization;
        organization["salesLead"] = engagement.Salesperson;

        if (!string.IsNullOrWhiteSpace(engagement.ExperienceJson) &&
            JsonNode.Parse(engagement.ExperienceJson) is JsonObject experience)
        {
            ApplyExperience(root, organization, experience);
        }
        return root.ToJsonString();
    }

    private static void ApplyExperience(JsonObject root, JsonObject organization, JsonObject experience)
    {
        if (experience["industryKey"]?.GetValue<string>() is { Length: > 0 } industry) organization["industryKey"] = industry;
        if (experience["industryOther"]?.GetValue<string>() is { Length: > 0 } other) organization["industryOther"] = other;

        if (experience["answers"] is JsonObject answers && answers.Count > 0)
        {
            var target = root["experienceAnswers"] as JsonObject ?? new JsonObject();
            root["experienceAnswers"] = target;
            foreach (var (key, value) in answers) target[key] = value?.DeepClone();
        }

        if (experience["groups"] is not JsonArray groups) return;
        var userGroups = root["userGroups"] as JsonArray ?? new JsonArray();
        root["userGroups"] = userGroups;
        foreach (var node in groups.OfType<JsonObject>())
        {
            var name = node["name"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            var existing = userGroups.OfType<JsonObject>().FirstOrDefault(g =>
                string.Equals(g["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                if (userGroups.Count >= 25) continue;
                existing = new JsonObject { ["key"] = Slug(name), ["name"] = name };
                userGroups.Add(existing);
            }
            foreach (var field in new[] { "classification", "userCount", "experienceProfile" })
            {
                if (node[field] is { } value) existing[field] = value.DeepClone();
            }
            if (node["answers"] is JsonObject groupAnswers)
            {
                var target = existing["answers"] as JsonObject ?? new JsonObject();
                existing["answers"] = target;
                foreach (var (key, value) in groupAnswers) target[key] = value?.DeepClone();
            }
        }
    }

    private async Task UpsertAsync(
        SqlConnection connection, Engagement e, string customerKey, string profileJson, int? version,
        CancellationToken cancellationToken)
    {
        var answers = e.Answers.GroupBy(a => a.FieldKey)
            .ToDictionary(g => g.Key, g => g.Select(a => a.Value).ToArray());
        await using var command = new SqlCommand("[secureplus].[usp_SalesUpsertCustomer]", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 60
        };
        var p = command.Parameters;
        p.Add("@SalesCustomerId", SqlDbType.UniqueIdentifier).Value = e.CustomerId;
        p.Add("@CustomerKey", SqlDbType.NVarChar, 64).Value = customerKey;
        p.Add("@Name", SqlDbType.NVarChar, 200).Value = e.Customer.Name;
        p.Add("@ProfileJson", SqlDbType.NVarChar, -1).Value = profileJson;
        p.Add("@ExpectedVersion", SqlDbType.Int).Value = (object?)version ?? DBNull.Value;
        p.Add("@Actor", SqlDbType.NVarChar, 256).Value = Truncate($"sales:{e.OwnerEmail ?? e.Salesperson}", 256);
        p.Add("@ContactName", SqlDbType.NVarChar, 200).Value = (object?)e.Customer.ContactName ?? DBNull.Value;
        p.Add("@ContactEmail", SqlDbType.NVarChar, 320).Value = (object?)e.Customer.ContactEmail ?? DBNull.Value;
        p.Add("@SalesOwner", SqlDbType.NVarChar, 200).Value = e.Salesperson;
        p.Add("@SalesEngagementId", SqlDbType.UniqueIdentifier).Value = e.Id;
        p.Add("@Salesperson", SqlDbType.NVarChar, 160).Value = e.Salesperson;
        p.Add("@OwnerId", SqlDbType.NVarChar, 200).Value = (object?)e.OwnerId ?? DBNull.Value;
        p.Add("@OwnerEmail", SqlDbType.NVarChar, 320).Value = (object?)e.OwnerEmail ?? DBNull.Value;
        p.Add("@MeetingDate", SqlDbType.Date).Value = e.MeetingDate is { } d ? d.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
        p.Add("@UserCount", SqlDbType.Int).Value = (object?)e.Users ?? DBNull.Value;
        p.Add("@EndpointCount", SqlDbType.Int).Value = (object?)e.Endpoints ?? DBNull.Value;
        p.Add("@Notes", SqlDbType.NVarChar, 4000).Value = (object?)e.Notes ?? DBNull.Value;
        p.Add("@OneTimeHoursMin", SqlDbType.Decimal).Value = e.OneTimeHoursMin;
        p.Add("@OneTimeHoursMax", SqlDbType.Decimal).Value = e.OneTimeHoursMax;
        p.Add("@MonthlyHoursMin", SqlDbType.Decimal).Value = e.MonthlyHoursMin;
        p.Add("@MonthlyHoursMax", SqlDbType.Decimal).Value = e.MonthlyHoursMax;
        p.Add("@TaskCount", SqlDbType.Int).Value = e.Tasks.Count;
        p.Add("@AnswersJson", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(answers);
        p.Add("@PlannerStatus", SqlDbType.NVarChar, 24).Value = e.PlannerHandoffStatus;
        p.Add("@SubmittedAtUtc", SqlDbType.DateTime2).Value = e.CreatedUtc;
        foreach (SqlParameter parameter in p.OfType<SqlParameter>().Where(x => x.SqlDbType == SqlDbType.Decimal))
        {
            parameter.Precision = 9;
            parameter.Scale = 2;
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task PublishPlanAsync(
        SqlConnection connection, Engagement e, string customerKey, CancellationToken cancellationToken)
    {
        if (e.Tasks.Count == 0) return;
        var tasks = e.Tasks.Select((t, index) => new
        {
            id = t.WbsTaskId,
            name = t.WbsTask?.Name ?? t.WbsTaskId,
            bucket = t.WbsTask?.Phase,
            sequence = t.WbsTask?.Sequence ?? index,
            minHours = t.MinHours,
            maxHours = t.MaxHours
        });
        await using var command = new SqlCommand("[secureplus].[usp_SalesPublishProjectPlan]", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 60
        };
        command.Parameters.Add("@CustomerKey", SqlDbType.NVarChar, 64).Value = customerKey;
        command.Parameters.Add("@TasksJson", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(tasks);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SalesOverviewRow>> GetOverviewAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured) return [];
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT SalesEngagementId, CustomerKey, CustomerName, Salesperson, OwnerEmail, SubmittedAtUtc, SyncedAtUtc,
                   AssessmentStatus, AssessmentScore
            FROM [secureplus].[vw_SalesEngagementOverview]
            """, connection);
        var rows = new List<SalesOverviewRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SalesOverviewRow(
                reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetDateTime(5), reader.GetDateTime(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture)));
        }
        return rows;
    }

    /// <summary>Latest non-sample assessment for a sales customer, written by the engineers' deployment app.</summary>
    public async Task<AssessmentSummary?> GetAssessmentAsync(Guid salesCustomerId, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return null;
        try
        {
            await using var connection = new SqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(
                """
                SELECT a.CustomerKey, a.AssessmentName, a.StatusCode, a.Score, a.ControlsTotal, a.ControlsPassed,
                       a.ControlsFailed, a.ControlsPartial, a.CriticalGaps, a.HighGaps, a.CompletedAtUtc
                FROM [secureplus].[vw_CustomerAssessmentSummary] a
                JOIN [secureplus].[SalesCustomerMap] m ON m.CustomerKey = a.CustomerKey
                WHERE m.SalesCustomerId = @id
                """, connection);
            command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = salesCustomerId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            int? Int(int i) => reader.IsDBNull(i) ? null : Convert.ToInt32(reader.GetValue(i), CultureInfo.InvariantCulture);
            return new AssessmentSummary(
                reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), Int(3), Int(4), Int(5),
                Int(6) ?? 0, Int(7) ?? 0, Int(8) ?? 0, Int(9) ?? 0,
                reader.IsDBNull(10) ? null : reader.GetDateTime(10));
        }
        catch (SqlException exception)
        {
            logger.LogWarning(exception, "Assessment summary lookup failed");
            return null;
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
