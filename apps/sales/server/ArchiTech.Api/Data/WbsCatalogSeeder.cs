using System.Globalization;
using System.Text;
using ArchiTech.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ArchiTech.Api.Data;

public static class WbsCatalogSeeder
{
    public static async Task SeedAsync(AppDbContext db, string csvPath, CancellationToken token = default)
    {
        if (await db.WbsTasks.AnyAsync(token))
        {
            return;
        }

        if (!File.Exists(csvPath))
        {
            throw new FileNotFoundException("The WBS task catalog CSV was not found.", csvPath);
        }

        var rows = ParseCsv(await File.ReadAllTextAsync(csvPath, token));
        if (rows.Count < 2)
        {
            throw new InvalidDataException("The WBS task catalog CSV contains no task rows.");
        }

        var header = rows[0].Select(value => value.Trim()).ToArray();
        var index = header.Select((name, position) => (name, position))
            .ToDictionary(item => item.name, item => item.position, StringComparer.OrdinalIgnoreCase);
        var tasks = new List<WbsTask>();

        foreach (var row in rows.Skip(1).Where(row => row.Any(value => !string.IsNullOrWhiteSpace(value))))
        {
            string Value(string key) =>
                index.TryGetValue(key, out var position) && position < row.Length
                    ? row[position].Trim()
                    : throw new InvalidDataException($"WBS CSV is missing the '{key}' column.");

            tasks.Add(new WbsTask
            {
                Id = Value("WBS ID"),
                Sequence = int.Parse(Value("Seq"), CultureInfo.InvariantCulture),
                Phase = Value("Phase"),
                Workstream = Value("Workstream"),
                Name = Value("Task"),
                Deliverable = Value("Deliverable"),
                RuleKey = Value("Rule key"),
                MinHours = decimal.Parse(Value("Min Hours"), CultureInfo.InvariantCulture),
                MaxHours = decimal.Parse(Value("Max Hours"), CultureInfo.InvariantCulture),
                DurationMin = Value("Duration Min"),
                DurationMax = Value("Duration Max"),
                SupportHandoff = Value("Support Handoff").Equals("Yes", StringComparison.OrdinalIgnoreCase)
            });
        }

        if (tasks.Count != 95 || tasks.Select(task => task.Id).Distinct(StringComparer.Ordinal).Count() != 95)
        {
            throw new InvalidDataException(
                $"Expected 95 unique WBS tasks, found {tasks.Count}.");
        }

        db.WbsTasks.AddRange(tasks);
        await db.SaveChangesAsync(token);
    }

    private static List<string[]> ParseCsv(string csv)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < csv.Length; i++)
        {
            var character = csv[i];
            if (inQuotes)
            {
                if (character == '"' && i + 1 < csv.Length && csv[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (character == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    field.Append(character);
                }

                continue;
            }

            if (character == '"')
            {
                inQuotes = true;
            }
            else if (character == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n')
                {
                    i++;
                }

                fields.Add(field.ToString());
                field.Clear();
                records.Add(fields.ToArray());
                fields.Clear();
            }
            else
            {
                field.Append(character);
            }
        }

        if (inQuotes)
        {
            throw new InvalidDataException("The WBS task catalog CSV contains an unclosed quoted field.");
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add(fields.ToArray());
        }

        return records;
    }
}
