using System.Globalization;
using System.Text;
using System.Text.Json;
using ArchiTech.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ArchiTech.Api.Data;

public static class StandardsCatalogSeeder
{
    public const string CatalogVersion = "SecurePlus-WIP-1";
    public const string TargetModelVersion = "ComplianceIQ-DataDictionary-v2";
    public const string LibraryInventoryVersion = "CloudServicesStandards-Inventory-1";
    private const int DecisionCount = 474;
    private const int WorkItemCount = 193;
    private const int SettingCount = 532;
    private const int ModelTableCount = 149;
    private const int ModelColumnCount = 1206;
    private const int LibrarySourceCount = 36;

    public static async Task SeedAsync(
        AppDbContext db,
        string dataDirectory,
        CancellationToken cancellationToken = default)
    {
        var counts = new[]
        {
            await db.SecurePlusDecisions.CountAsync(cancellationToken),
            await db.SecurePlusWorkItems.CountAsync(cancellationToken),
            await db.SecurePlusBaselineSettings.CountAsync(cancellationToken),
            await db.TargetModelTables.CountAsync(cancellationToken),
            await db.TargetModelColumns.CountAsync(cancellationToken)
        };
        var expected = new[]
        {
            DecisionCount,
            WorkItemCount,
            SettingCount,
            ModelTableCount,
            ModelColumnCount
        };
        if (counts.SequenceEqual(expected))
        {
            await SeedLibrarySourcesAsync(db, dataDirectory, cancellationToken);
            return;
        }

        if (counts.Any(count => count != 0))
        {
            throw new InvalidDataException(
                "The standards catalog tables are partially populated. Resolve the catalog version before reseeding.");
        }

        var decisions = ReadJson(dataDirectory, "secure-plus-discovery-decisions-v1.json")
            .Select(row => new SecurePlusDecision
            {
                Id = Value(row, "Decision ID"),
                CatalogVersion = CatalogVersion,
                BaselineReferences = Value(row, "Related Baseline IDs"),
                Phase = Value(row, "Phase"),
                Category = Value(row, "Decision Category"),
                Topic = Value(row, "Decision or Topic"),
                Question = Value(row, "Customer Question"),
                Rationale = Value(row, "Why This Must Be Asked"),
                AnswerType = Value(row, "Expected Answer Type"),
                SuggestedAnswers = Value(row, "Suggested Answer Options or Examples"),
                ConfigurationImpact = Value(row, "Configuration Impact"),
                DecisionOwner = Value(row, "Customer Decision Owner"),
                RequiredParticipants = Value(row, "Required Participants"),
                RequiredBy = Value(row, "Required By"),
                EvidenceRequired = Value(row, "Evidence Required"),
                DefaultIfUndecided = Value(row, "Default If Undecided"),
                EngineeringNotes = Value(row, "Engineering Notes")
            })
            .ToArray();

        var workItems = ReadJson(dataDirectory, "secure-plus-wbs-v1.json")
            .Select(row => new SecurePlusWorkItem
            {
                Id = Value(row, "Baseline ID"),
                CatalogVersion = CatalogVersion,
                PhaseReference = Value(row, "Phase-Group-Number Reference"),
                Phase = Value(row, "Phase"),
                Workstream = Value(row, "Workstream"),
                TaskNumber = ParseInteger(Value(row, "Task Num"), "Task Num"),
                TaskName = Value(row, "Task Name"),
                SubTaskNumber = Value(row, "Sub-Task Number"),
                SubTaskName = Value(row, "Sub-Task Name"),
                GroupAbbreviation = Value(row, "Group Abbreviation"),
                GroupItemNumber = ParseInteger(Value(row, "Group Item Number"), "Group Item Number"),
                Control = Value(row, "Control"),
                ControlDescription = Value(row, "Control Description"),
                Deliverable = Value(row, "Deliverable"),
                Dependency = Value(row, "Dependency"),
                AppliesWhen = Value(row, "Applies When"),
                LicenseRequirement = Value(row, "License Requirement"),
                EstimatedHoursMin = ParseDecimal(Value(row, "Est Hours Min"), "Est Hours Min"),
                EstimatedHoursMax = ParseDecimal(Value(row, "Est Hours Max"), "Est Hours Max"),
                DurationDaysMin = ParseDecimal(Value(row, "Calendar Duration Min (Days)"), "Calendar Duration Min (Days)"),
                DurationDaysMax = ParseDecimal(Value(row, "Calendar Duration Max (Days)"), "Calendar Duration Max (Days)"),
                Parallelizable = ParseYesNo(Value(row, "Parallelizable?"), "Parallelizable?"),
                SupportHandoff = ParseYesNo(Value(row, "Support Handoff?"), "Support Handoff?"),
                ComplexityDriver = Value(row, "Complexity Driver"),
                LeadershipNotes = Value(row, "Leadership Notes")
            })
            .ToArray();

        var settings = ReadJson(dataDirectory, "secure-plus-baseline-settings-v1.json")
            .Select(row =>
            {
                var baselineId = Value(row, "Baseline ID");
                var control = Value(row, "Control");
                var category = Value(row, "Setting Category");
                var settingName = Value(row, "Setting Name");
                return new SecurePlusBaselineSetting
                {
                    Id = string.Join('|', baselineId, control, category, settingName),
                    CatalogVersion = CatalogVersion,
                    BaselineReference = baselineId,
                    Phase = Value(row, "Phase"),
                    Workstream = Value(row, "Workstream"),
                    TaskNumber = ParseInteger(Value(row, "Task Num"), "Task Num"),
                    TaskName = Value(row, "Task Name"),
                    GroupAbbreviation = Value(row, "Group Abbreviation"),
                    GroupItemNumber = ParseInteger(Value(row, "Group Item Number"), "Group Item Number"),
                    Control = control,
                    SettingCategory = category,
                    SettingName = settingName,
                    TargetAction = Value(row, "Target Configuration or Required Action"),
                    ReviewPath = Value(row, "Configuration or Review Path"),
                    DeploymentMethod = Value(row, "Deployment Method"),
                    ValidationMethod = Value(row, "Validation Method"),
                    EvidenceRequired = Value(row, "Evidence Required"),
                    AppliesWhen = Value(row, "Applies When"),
                    LicenseRequirement = Value(row, "License Requirement"),
                    ImplementationMode = Value(row, "Implementation Mode"),
                    PilotGroup = Value(row, "Pilot Group"),
                    Exclusions = Value(row, "Exclusions"),
                    ExceptionCriteria = Value(row, "Exception Criteria"),
                    RollbackMethod = Value(row, "Rollback Method"),
                    ReviewCadence = Value(row, "Review Cadence"),
                    SupportOwner = Value(row, "Support Owner"),
                    EngineeringNotes = Value(row, "Engineering Notes")
                };
            })
            .ToArray();

        var (modelTables, modelColumns) = ReadTargetModel(dataDirectory);
        ValidateCounts(decisions.Length, workItems.Length, settings.Length, modelTables.Count, modelColumns.Count);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.SecurePlusDecisions.AddRange(decisions);
        db.SecurePlusWorkItems.AddRange(workItems);
        db.SecurePlusBaselineSettings.AddRange(settings);
        db.TargetModelTables.AddRange(modelTables);
        db.TargetModelColumns.AddRange(modelColumns);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await SeedLibrarySourcesAsync(db, dataDirectory, cancellationToken);
    }

    private static async Task SeedLibrarySourcesAsync(
        AppDbContext db,
        string dataDirectory,
        CancellationToken cancellationToken)
    {
        var existingCount = await db.StandardsReferenceSources.CountAsync(
            item => item.CatalogVersion == LibraryInventoryVersion,
            cancellationToken);
        if (existingCount == LibrarySourceCount)
        {
            return;
        }
        if (existingCount != 0)
        {
            throw new InvalidDataException(
                "The standards source inventory is partially populated. Resolve the inventory version before reseeding.");
        }

        var path = Path.Combine(dataDirectory, "library-source-inventory-v1.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The standards source inventory file is missing.", path);
        }

        var rows = JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("The standards source inventory is empty or invalid.");
        var sources = rows.Select(row => new StandardsReferenceSource
        {
            Id = RequiredString(row, "Id"),
            CatalogVersion = LibraryInventoryVersion,
            Area = RequiredString(row, "Area"),
            Name = RequiredString(row, "Name"),
            FileName = RequiredString(row, "FileName"),
            Format = RequiredString(row, "Format"),
            SourceVersion = RequiredString(row, "SourceVersion"),
            Status = RequiredString(row, "Status"),
            UsageBoundary = RequiredString(row, "UsageBoundary"),
            Description = RequiredString(row, "Description"),
            ImportedRecordCount = row.GetProperty("ImportedRecordCount").ValueKind == JsonValueKind.Null
                ? null
                : row.GetProperty("ImportedRecordCount").GetInt32(),
            RecordUnit = row.GetProperty("RecordUnit").ValueKind == JsonValueKind.Null
                ? null
                : row.GetProperty("RecordUnit").GetString()
        }).ToArray();

        if (sources.Length != LibrarySourceCount ||
            sources.Select(source => source.Id).Distinct(StringComparer.Ordinal).Count() != LibrarySourceCount)
        {
            throw new InvalidDataException(
                $"The standards source inventory must contain {LibrarySourceCount} uniquely identified sources.");
        }

        db.StandardsReferenceSources.AddRange(sources);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string RequiredString(JsonElement row, string propertyName) =>
        row.GetProperty(propertyName).GetString() is { Length: > 0 } value
            ? value
            : throw new InvalidDataException(
                $"The standards source inventory contains a blank '{propertyName}'.");

    private static List<Dictionary<string, string>> ReadJson(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("A versioned standards catalog source file is missing.", path);
        }

        return JsonSerializer.Deserialize<List<Dictionary<string, string>>>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = false })
               ?? throw new InvalidDataException($"The standards catalog file '{fileName}' is empty or invalid.");
    }

    private static (List<TargetModelTable> Tables, List<TargetModelColumn> Columns) ReadTargetModel(
        string directory)
    {
        var path = Path.Combine(directory, "target-model-v2-reference.csv");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The target data-model reference file is missing.", path);
        }

        using var reader = new StringReader(File.ReadAllText(path));
        var rows = ParseDelimited(reader, '|');
        if (rows.Count < 2)
        {
            throw new InvalidDataException("The target data-model reference contains no columns.");
        }

        var header = rows[0].Select(value => value.Trim()).ToArray();
        var index = header.Select((name, position) => (name, position))
            .ToDictionary(item => item.name, item => item.position, StringComparer.Ordinal);
        string Value(string[] row, string key) =>
            index.TryGetValue(key, out var position) && position < row.Length
                ? row[position].Trim()
                : throw new InvalidDataException($"The target data-model reference is missing the '{key}' column.");

        var tablesById = new Dictionary<string, TargetModelTable>(StringComparer.Ordinal);
        var columns = new List<TargetModelColumn>();
        foreach (var row in rows.Skip(1).Where(row => row.Any(value => !string.IsNullOrWhiteSpace(value))))
        {
            var schema = Value(row, "Schema");
            var tableName = Value(row, "Table");
            var tableId = $"{schema}.{tableName}";
            var table = new TargetModelTable
            {
                Id = tableId,
                CatalogVersion = TargetModelVersion,
                Schema = schema,
                Name = tableName,
                Description = Value(row, "TableDescription"),
                EstimatedRows = Value(row, "EstimatedRows"),
                PrimarySources = Value(row, "PrimarySources"),
                TemporalHistory = Value(row, "TemporalHistory")
            };
            if (!tablesById.TryAdd(tableId, table) &&
                (tablesById[tableId].Description != table.Description ||
                 tablesById[tableId].EstimatedRows != table.EstimatedRows ||
                 tablesById[tableId].PrimarySources != table.PrimarySources ||
                 tablesById[tableId].TemporalHistory != table.TemporalHistory))
            {
                throw new InvalidDataException($"Target model metadata differs between rows for '{tableId}'.");
            }

            columns.Add(new TargetModelColumn
            {
                TableId = tableId,
                CatalogVersion = TargetModelVersion,
                ColumnOrdinal = ParseInteger(Value(row, "ColumnOrdinal"), "ColumnOrdinal"),
                Name = Value(row, "ColumnName"),
                DataType = Value(row, "DataType"),
                IsPrimaryKey = ParseYesNo(Value(row, "IsPrimaryKey"), "IsPrimaryKey"),
                IsForeignKey = ParseYesNo(Value(row, "IsForeignKey"), "IsForeignKey"),
                ForeignKeyTarget = NullIfEmpty(Value(row, "ForeignKeyTarget")),
                IsUniqueKeyPart = ParseYesNo(Value(row, "IsUniqueKeyPart"), "IsUniqueKeyPart"),
                IsIndexed = ParseYesNo(Value(row, "IsIndexed"), "IsIndexed")
            });
        }

        return (tablesById.Values.ToList(), columns);
    }

    private static void ValidateCounts(
        int decisions,
        int workItems,
        int settings,
        int modelTables,
        int modelColumns)
    {
        if (decisions != DecisionCount || workItems != WorkItemCount ||
            settings != SettingCount || modelTables != ModelTableCount ||
            modelColumns != ModelColumnCount)
        {
            throw new InvalidDataException(
                $"Unexpected catalog size: {decisions} decisions, {workItems} WBS tasks, " +
                $"{settings} settings, {modelTables} target tables, {modelColumns} target columns.");
        }
    }

    private static string Value(IReadOnlyDictionary<string, string> row, string key) =>
        row.TryGetValue(key, out var value)
            ? value.Trim()
            : throw new InvalidDataException($"The standards workbook is missing the '{key}' column.");

    private static int ParseInteger(string value, string field) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) &&
              decimal.Truncate(number) == number
                ? decimal.ToInt32(number)
                : throw new InvalidDataException($"'{value}' is not a valid integer in '{field}'.");

    private static decimal ParseDecimal(string value, string field) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new InvalidDataException($"'{value}' is not a valid number in '{field}'.");

    private static bool ParseYesNo(string value, string field) =>
        value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("y", StringComparison.OrdinalIgnoreCase)
            ? true
            : value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
              value.Equals("n", StringComparison.OrdinalIgnoreCase)
                ? false
                : throw new InvalidDataException($"'{value}' is not a valid Yes/No value in '{field}'.");

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static List<string[]> ParseDelimited(TextReader reader, char delimiter)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var character = reader.Read();
        while (character >= 0)
        {
            var current = (char)character;
            if (inQuotes)
            {
                if (current == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(current);
                }
            }
            else if (current == '"')
            {
                inQuotes = true;
            }
            else if (current == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (current is '\r' or '\n')
            {
                if (current == '\r' && reader.Peek() == '\n')
                {
                    reader.Read();
                }
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row.ToArray());
                row.Clear();
            }
            else
            {
                field.Append(current);
            }

            character = reader.Read();
        }

        if (inQuotes)
        {
            throw new InvalidDataException("The target data-model reference contains an unclosed quoted field.");
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }

        return rows;
    }
}
