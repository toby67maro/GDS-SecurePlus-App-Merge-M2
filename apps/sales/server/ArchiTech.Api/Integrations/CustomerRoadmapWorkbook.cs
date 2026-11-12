using System.Text.Json;
using ArchiTech.Api.Contracts;
using ArchiTech.Api.Domain;
using ClosedXML.Excel;
using EngineeringWorkspace.Application.Experience;
using EngineeringWorkspace.Domain.Common;
using EngineeringWorkspace.Domain.Customers;

namespace ArchiTech.Api.Integrations;

public sealed class CustomerRoadmapWorkbook
{
    private static readonly string[] RoleNames =
    [
        "Customer leadership",
        "IT / admin",
        "Managers",
        "Employees",
        "Sales",
        "Project management",
        "Engineering"
    ];

    public byte[] Build(Engagement engagement, IReadOnlyList<DeploymentControl> controls)
    {
        using var workbook = new XLWorkbook();
        var groups = EvaluateGroups(engagement);
        AddSummarySheet(workbook, engagement, groups);
        AddRoadmapSheet(workbook, engagement);
        AddExperienceMapSheet(workbook, engagement, groups);
        AddControlImpactSheet(workbook, engagement, controls, groups);
        AddHeatmapSheet(workbook, engagement);
        AddPhaseSummarySheet(workbook, engagement);
        AddHotspotsSheet(workbook, engagement);
        AddPackageComparisonSheet(workbook, engagement);
        AddGuideSheet(workbook, engagement);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void AddPackageComparisonSheet(XLWorkbook workbook, Engagement engagement)
    {
        var comparison = SecurePlusPackages.Compare(engagement.Answers);
        var sheet = workbook.Worksheets.Add("Package Comparison");
        AddTitle(sheet, engagement, "Package components for discussion");
        sheet.Cell(4, 1).Value = comparison.Disclaimer;
        var rows = comparison.Packages.SelectMany(package => package.Components.Select(component =>
            new object[] { package.Sheet, package.Name, component })).ToArray();
        AddTable(sheet, 6, ["Source sheet", "Package", "Service component"], rows, "PackageComponents");
        sheet.Column(1).Width = 26;
        sheet.Column(2).Width = 52;
        sheet.Column(3).Width = 65;
        var row = rows.Length + 9;
        sheet.Cell(row, 1).Value = "Intake discussion signals";
        sheet.Cell(row, 1).Style.Font.Bold = true;
        foreach (var signal in comparison.Signals)
        {
            row++;
            sheet.Cell(row, 1).Value = signal.Questionnaire;
            sheet.Cell(row, 2).Value = signal.Value;
            sheet.Cell(row, 3).Value = signal.Review;
        }
        sheet.Column(3).Style.Alignment.WrapText = true;
        sheet.SheetView.FreezeRows(6);
    }

    private const string Navy = "#002060";
    private const string Teal = "#156082";
    private const string Cyan = "#72E1F5";
    private const string Mist = "#E4F2F5";
    private const string Good = "#C6E7D0";
    private const string Warn = "#FFF2CC";
    private const string Bad = "#F8CECC";

    private static IReadOnlyList<GroupExperience> EvaluateGroups(Engagement engagement)
    {
        if (string.IsNullOrWhiteSpace(engagement.ExperienceJson)) return [];
        try
        {
            var selection = JsonSerializer.Deserialize<ExperienceSelection>(
                engagement.ExperienceJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (selection is null) return [];
            var userGroups = (selection.Groups ?? [])
                .Where(group => !string.IsNullOrWhiteSpace(group.Name))
                .Take(CustomerProfile.MaxUserGroups)
                .Select((group, index) => new UserGroup(
                    $"group-{index + 1}",
                    group.Name!.Trim(),
                    ExperienceCatalog.Classification(group.Classification).Code,
                    UserCount: group.UserCount,
                    ExperienceProfile: group.ExperienceProfile,
                    Answers: group.Answers))
                .ToArray();
            if (userGroups.Length == 0) return [];
            var profile = CustomerProfile.Create(new CustomerId("sales-workbook"), "Sales workbook") with
            {
                UserGroups = userGroups,
                ExperienceAnswers = selection.Answers ?? new Dictionary<string, string>()
            };
            var frameworks = (selection.Frameworks ?? [])
                .Where(code => ExperienceCatalog.Framework(code) is not null).ToArray();
            return ExperienceEngine.EvaluateAll(profile, frameworks);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void StyleHeader(IXLRange range)
    {
        range.Style.Fill.BackgroundColor = XLColor.FromHtml(Navy);
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Font.Bold = true;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        range.Style.Alignment.WrapText = true;
    }

    private static void AddScale(IXLRange range, bool highIsGood)
    {
        var low = XLColor.FromHtml(highIsGood ? "#F4B6B0" : "#9ED8B0");
        var high = XLColor.FromHtml(highIsGood ? "#9ED8B0" : "#F4B6B0");
        range.AddConditionalFormat().ColorScale()
            .LowestValue(low).Midpoint(XLCFContentType.Percent, 50, XLColor.FromHtml("#FFF2CC")).HighestValue(high);
    }

    private static void AddSummarySheet(
        XLWorkbook workbook, Engagement engagement, IReadOnlyList<GroupExperience> groups)
    {
        var sheet = workbook.Worksheets.Add("Summary");
        AddTitle(sheet, engagement, "Your Secure Plus journey at a glance");
        var phases = engagement.Tasks.GroupBy(task => task.WbsTask.Phase)
            .OrderBy(group => group.Min(task => task.WbsTask.Sequence)).ToArray();
        var kpis = new (string Label, string Value)[]
        {
            ("One-time effort", $"{engagement.OneTimeHoursMin:0.#}–{engagement.OneTimeHoursMax:0.#} hrs"),
            ("Monthly operations", $"{engagement.MonthlyHoursMin:0.#}–{engagement.MonthlyHoursMax:0.#} hrs/mo"),
            ("Tasks in scope", engagement.Tasks.Count.ToString()),
            ("Journey phases", phases.Length.ToString()),
            ("User groups", groups.Count == 0 ? "Not captured" : groups.Count.ToString())
        };
        for (var index = 0; index < kpis.Length; index++)
        {
            var column = 1 + index * 2;
            var label = sheet.Range(5, column, 5, column + 1).Merge();
            label.Value = kpis[index].Label.ToUpperInvariant();
            label.Style.Font.FontSize = 9;
            label.Style.Font.FontColor = XLColor.FromHtml(Teal);
            label.Style.Font.Bold = true;
            var value = sheet.Range(6, column, 6, column + 1).Merge();
            value.Value = kpis[index].Value;
            value.Style.Font.FontSize = 16;
            value.Style.Font.Bold = true;
            value.Style.Font.FontColor = XLColor.FromHtml(Navy);
            sheet.Range(5, column, 6, column + 1).Style.Fill.BackgroundColor = XLColor.FromHtml(Mist);
            sheet.Range(5, column, 6, column + 1).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            sheet.Range(5, column, 6, column + 1).Style.Border.OutsideBorderColor = XLColor.White;
        }

        sheet.Cell(8, 1).Value = "Journey phases";
        sheet.Cell(8, 1).Style.Font.Bold = true;
        sheet.Cell(8, 1).Style.Font.FontColor = XLColor.FromHtml(Navy);
        var headers = new[] { "Phase", "What happens", "Tasks", "Max hours", "Share of effort" };
        for (var column = 0; column < headers.Length; column++) sheet.Cell(9, column + 1).Value = headers[column];
        StyleHeader(sheet.Range(9, 1, 9, headers.Length));
        var total = Math.Max(1m, phases.Sum(group => group.Sum(task => task.MaxHours)));
        for (var index = 0; index < phases.Length; index++)
        {
            var row = 10 + index;
            var hours = phases[index].Sum(task => task.MaxHours);
            sheet.Cell(row, 1).Value = phases[index].Key;
            sheet.Cell(row, 2).Value = JourneyNarrative(phases[index].Key);
            sheet.Cell(row, 3).Value = phases[index].Count();
            sheet.Cell(row, 4).Value = hours;
            sheet.Cell(row, 5).Value = (double)(hours / total);
            sheet.Cell(row, 5).Style.NumberFormat.Format = "0%";
        }
        if (phases.Length > 0)
        {
            sheet.Range(10, 5, 9 + phases.Length, 5).AddConditionalFormat().DataBar(XLColor.FromHtml(Teal));
            sheet.Range(10, 1, 9 + phases.Length, 5).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            sheet.Range(10, 2, 9 + phases.Length, 2).Style.Alignment.WrapText = true;
            sheet.Rows(10, 9 + phases.Length).Height = 34;
        }
        sheet.Column(1).Width = 34;
        sheet.Column(2).Width = 70;
        sheet.Columns(3, 10).Width = 13;
        sheet.Column(5).Width = 22;

        var next = 12 + phases.Length;
        sheet.Cell(next, 1).Value = "Where to look next";
        sheet.Cell(next, 1).Style.Font.Bold = true;
        sheet.Cell(next, 1).Style.Font.FontColor = XLColor.FromHtml(Navy);
        var guide = new[]
        {
            ("Journey Roadmap", "Every task by phase, with who is involved at each step."),
            ("Experience Map", "How each user group will feel the change: security gained versus day-to-day friction."),
            ("Control Impact", "Which controls touch which users, starting from catalog impact ratings you can adjust."),
            ("Heatmap", "Where effort concentrates across phases and workstreams.")
        };
        for (var index = 0; index < guide.Length; index++)
        {
            sheet.Cell(next + 1 + index, 1).Value = guide[index].Item1;
            sheet.Cell(next + 1 + index, 2).Value = guide[index].Item2;
        }
    }

    private static void AddExperienceMapSheet(
        XLWorkbook workbook, Engagement engagement, IReadOnlyList<GroupExperience> groups)
    {
        var sheet = workbook.Worksheets.Add("Experience Map");
        AddTitle(sheet, engagement, "Experience by user group");
        sheet.Cell(4, 1).Value =
            "Scores are 0–100 from the Secure Plus experience model. Security: higher is stronger. Friction: lower is smoother. Planning aid, not a tenant finding.";
        if (groups.Count == 0)
        {
            sheet.Cell(6, 1).Value =
                "No user groups were captured in the intake. Add groups in step 07 (Experience & security) and export again.";
            sheet.Column(1).Width = 110;
            return;
        }

        var solutions = ExperienceCatalog.Solutions;
        var headers = new List<string>
        {
            "User group", "Type", "Users", "Experience profile", "Security", "Friction",
            "Security (with frameworks)", "Friction (with frameworks)", "Framework conflicts"
        };
        headers.AddRange(solutions.Select(solution => $"{solution} friction"));
        for (var column = 0; column < headers.Count; column++) sheet.Cell(6, column + 1).Value = headers[column];
        StyleHeader(sheet.Range(6, 1, 6, headers.Count));
        sheet.Row(6).Height = 44;

        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            var row = 7 + index;
            sheet.Cell(row, 1).Value = group.Name;
            sheet.Cell(row, 2).Value = group.Classification;
            if (group.UserCount is int users) sheet.Cell(row, 3).Value = users;
            sheet.Cell(row, 4).Value = group.Profile.Name;
            sheet.Cell(row, 5).Value = Math.Round(group.Security);
            sheet.Cell(row, 6).Value = Math.Round(group.Friction);
            sheet.Cell(row, 7).Value = Math.Round(group.SecurityWithFrameworks);
            sheet.Cell(row, 8).Value = Math.Round(group.FrictionWithFrameworks);
            sheet.Cell(row, 9).Value = group.Conflicts;
            for (var column = 0; column < solutions.Count; column++)
            {
                var score = group.Solutions.FirstOrDefault(item => item.Solution == solutions[column]);
                if (score is not null) sheet.Cell(row, 10 + column).Value = Math.Round(score.Friction);
            }
        }

        var last = 6 + groups.Count;
        sheet.Range(7, 1, last, 1).Style.Font.Bold = true;
        AddScale(sheet.Range(7, 5, last, 5), highIsGood: true);
        AddScale(sheet.Range(7, 7, last, 7), highIsGood: true);
        AddScale(sheet.Range(7, 6, last, 6), highIsGood: false);
        AddScale(sheet.Range(7, 8, last, 8), highIsGood: false);
        if (solutions.Count > 0) AddScale(sheet.Range(7, 10, last, 9 + solutions.Count), highIsGood: false);
        sheet.Range(7, 3, last, headers.Count).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        sheet.Range(6, 1, last, headers.Count).Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        sheet.Range(6, 1, last, headers.Count).Style.Border.InsideBorderColor = XLColor.White;
        sheet.Column(1).Width = 28;
        sheet.Columns(2, headers.Count).Width = 16;
        sheet.Column(4).Width = 24;

        var detailRow = last + 3;
        sheet.Cell(detailRow - 1, 1).Value = "What each group will experience";
        sheet.Cell(detailRow - 1, 1).Style.Font.Bold = true;
        sheet.Cell(detailRow - 1, 1).Style.Font.FontSize = 13;
        sheet.Cell(detailRow - 1, 1).Style.Font.FontColor = XLColor.FromHtml(Navy);
        var detailHeaders = new[] { "User group", "Solution area", "Topic", "Selected option", "Experience", "Friction", "Security" };
        for (var column = 0; column < detailHeaders.Length; column++)
            sheet.Cell(detailRow, column + 1).Value = detailHeaders[column];
        StyleHeader(sheet.Range(detailRow, 1, detailRow, detailHeaders.Length));
        var row2 = detailRow;
        foreach (var group in groups)
        {
            foreach (var answer in group.Answers)
            {
                row2++;
                sheet.Cell(row2, 1).Value = group.Name;
                sheet.Cell(row2, 2).Value = answer.Question.Solution;
                sheet.Cell(row2, 3).Value = answer.Question.Topic;
                sheet.Cell(row2, 4).Value = answer.Option.Label;
                sheet.Cell(row2, 5).Value = answer.Option.UserExperience;
                sheet.Cell(row2, 6).Value = answer.Option.Friction;
                sheet.Cell(row2, 7).Value = answer.Option.Security;
            }
        }
        if (row2 > detailRow)
        {
            AddScale(sheet.Range(detailRow + 1, 6, row2, 6), highIsGood: false);
            AddScale(sheet.Range(detailRow + 1, 7, row2, 7), highIsGood: true);
            sheet.Range(detailRow + 1, 5, row2, 5).Style.Alignment.WrapText = true;
            sheet.Range(detailRow + 1, 1, row2, 7).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        }
        sheet.Column(5).Width = 60;
        sheet.SheetView.FreezeRows(6);
        sheet.SheetView.FreezeColumns(1);
    }

    private static void AddControlImpactSheet(
        XLWorkbook workbook,
        Engagement engagement,
        IReadOnlyList<DeploymentControl> controls,
        IReadOnlyList<GroupExperience> groups)
    {
        var sheet = workbook.Worksheets.Add("Control Impact");
        AddTitle(sheet, engagement, "Control impact by user group");
        sheet.Cell(4, 1).Value =
            "Impact starts from the Secure Plus catalog rating for each control. Adjust any cell for this customer: High = red, Medium = yellow, Low = green. Impact is not a security finding; edits are not saved back to the app.";
        var audiences = groups.Count > 0
            ? groups.Select(group => group.Name).ToArray()
            : engagement.Answers.Where(answer => answer.FieldKey == "orgDepartments")
                .Select(answer => answer.Value).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
        if (audiences.Length == 0) audiences = ["All users"];

        var headers = new List<string> { "Control ID", "Control", "Category", "Solution", "Phase", "Applicability" };
        headers.AddRange(audiences);
        for (var column = 0; column < headers.Count; column++) sheet.Cell(6, column + 1).Value = headers[column];
        StyleHeader(sheet.Range(6, 1, 6, headers.Count));
        sheet.Row(6).Height = 32;

        string Rating(string impact) => impact?.Trim().ToLowerInvariant() switch
        {
            "high" => "High",
            "medium" => "Medium",
            "low" => "Low",
            _ => "Unknown"
        };
        var catalog = controls.OrderBy(control => control.ControlId, StringComparer.Ordinal).ToArray();
        for (var index = 0; index < catalog.Length; index++)
        {
            var control = catalog[index];
            var row = 7 + index;
            sheet.Cell(row, 1).Value = control.ControlId;
            sheet.Cell(row, 2).Value = control.Name;
            sheet.Cell(row, 3).Value = control.Category;
            sheet.Cell(row, 4).Value = control.MicrosoftSolution;
            sheet.Cell(row, 5).Value = control.DeploymentPhase;
            sheet.Cell(row, 6).Value = "Review";
            for (var column = 0; column < audiences.Length; column++)
                sheet.Cell(row, 7 + column).Value = Rating(control.UserImpact);
        }

        if (catalog.Length > 0)
        {
            var last = catalog.Length + 6;
            sheet.Range(7, 6, last, 6).CreateDataValidation().List("\"Review,Applicable,Not applicable\"");
            var ratings = sheet.Range(7, 7, last, audiences.Length + 6);
            ratings.CreateDataValidation().List("\"High,Medium,Low,Unknown\"");
            ratings.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ratings.AddConditionalFormat().WhenEquals("High").Fill.SetBackgroundColor(XLColor.FromHtml("#F4B6B0"));
            ratings.AddConditionalFormat().WhenEquals("Medium").Fill.SetBackgroundColor(XLColor.FromHtml("#FFE699"));
            ratings.AddConditionalFormat().WhenEquals("Low").Fill.SetBackgroundColor(XLColor.FromHtml("#B9E3C6"));
            ratings.AddConditionalFormat().WhenEquals("Unknown").Fill.SetBackgroundColor(XLColor.FromHtml("#E7E6E6"));
            sheet.Range(6, 1, last, 6 + audiences.Length).SetAutoFilter();
        }
        sheet.Column(1).Width = 12;
        sheet.Column(2).Width = 54;
        sheet.Column(3).Width = 20;
        sheet.Column(4).Width = 24;
        sheet.Column(5).Width = 22;
        sheet.Column(6).Width = 16;
        sheet.Columns(7, audiences.Length + 6).Width = 16;
        sheet.SheetView.FreezeRows(6);
        sheet.SheetView.FreezeColumns(2);
    }

    private static void AddHeatmapSheet(XLWorkbook workbook, Engagement engagement)
    {
        var sheet = workbook.Worksheets.Add("Heatmap");
        AddTitle(sheet, engagement, "Effort heatmap");
        sheet.Cell(4, 1).Value =
            "Planning hours (midpoint of each estimate range) by journey phase and workstream. Darker cells mean more effort.";
        var tasks = engagement.Tasks.OrderBy(task => task.WbsTask.Sequence).ToArray();
        var phases = tasks.Select(task => task.WbsTask.Phase).Distinct().ToArray();
        var streams = tasks.GroupBy(task => task.WbsTask.Workstream)
            .OrderByDescending(group => group.Sum(task => (task.MinHours + task.MaxHours) / 2))
            .Select(group => group.Key).Take(14).ToArray();
        if (phases.Length == 0 || streams.Length == 0)
        {
            sheet.Cell(6, 1).Value = "No tasks are in scope for this engagement.";
            return;
        }

        sheet.Cell(6, 1).Value = "Phase";
        for (var column = 0; column < streams.Length; column++) sheet.Cell(6, column + 2).Value = streams[column];
        sheet.Cell(6, streams.Length + 2).Value = "Total";
        StyleHeader(sheet.Range(6, 1, 6, streams.Length + 2));
        sheet.Row(6).Height = 48;
        for (var row = 0; row < phases.Length; row++)
        {
            sheet.Cell(7 + row, 1).Value = phases[row];
            sheet.Cell(7 + row, 1).Style.Font.Bold = true;
            decimal total = 0;
            for (var column = 0; column < streams.Length; column++)
            {
                var hours = tasks.Where(task => task.WbsTask.Phase == phases[row] && task.WbsTask.Workstream == streams[column])
                    .Sum(task => (task.MinHours + task.MaxHours) / 2);
                total += hours;
                if (hours > 0) sheet.Cell(7 + row, column + 2).Value = Math.Round(hours, 1);
            }
            sheet.Cell(7 + row, streams.Length + 2).Value = Math.Round(total, 1);
        }
        var last = 6 + phases.Length;
        var body = sheet.Range(7, 2, last, streams.Length + 1);
        body.AddConditionalFormat().ColorScale()
            .LowestValue(XLColor.FromHtml(Mist))
            .Midpoint(XLCFContentType.Percent, 50, XLColor.FromHtml(Cyan))
            .HighestValue(XLColor.FromHtml(Teal));
        sheet.Range(7, 2, last, streams.Length + 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        sheet.Range(7, streams.Length + 2, last, streams.Length + 2).Style.Font.Bold = true;
        sheet.Range(6, 1, last, streams.Length + 2).Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        sheet.Range(6, 1, last, streams.Length + 2).Style.Border.InsideBorderColor = XLColor.White;
        sheet.Column(1).Width = 36;
        sheet.Columns(2, streams.Length + 2).Width = 15;
        sheet.SheetView.FreezeRows(6);
        sheet.SheetView.FreezeColumns(1);
    }

    private static void AddRoadmapSheet(XLWorkbook workbook, Engagement engagement)
    {
        var sheet = workbook.Worksheets.Add("Journey Roadmap");
        AddTitle(sheet, engagement, "Customer onboarding journey");
        sheet.Cell(4, 1).Value =
            "Filter the table to explore phases, controls, stakeholder touchpoints, and explicit intake hotspot signals.";

        var headers = new List<string>
        {
            "Journey phase", "Customer journey", "Control ID", "Action / deliverable",
            "Workstream", "Business impact"
        };
        headers.AddRange(RoleNames);
        headers.AddRange(["Hotspot signals", "Min hours", "Max hours", "Duration range"]);
        var rows = engagement.Tasks
            .OrderBy(task => task.WbsTask.Sequence)
            .Select(task =>
            {
                var wbs = task.WbsTask;
                var hotspot = GetHotspotSignals(engagement, wbs);
                return new object[]
                {
                    wbs.Phase,
                    JourneyNarrative(wbs.Phase),
                    wbs.Id,
                    wbs.Name,
                    wbs.Workstream,
                    wbs.Deliverable,
                    RoleValue("Customer leadership", wbs),
                    RoleValue("IT / admin", wbs),
                    RoleValue("Managers", wbs),
                    RoleValue("Employees", wbs),
                    RoleValue("Sales", wbs),
                    RoleValue("Project management", wbs),
                    RoleValue("Engineering", wbs),
                    hotspot,
                    task.MinHours,
                    task.MaxHours,
                    $"{wbs.DurationMin}–{wbs.DurationMax}"
                };
            })
            .ToArray();

        AddTable(sheet, 6, headers, rows, "CustomerJourney");
        sheet.SheetView.FreezeRows(6);
        sheet.Columns(1, headers.Count).AdjustToContents(10, 48);
        sheet.Column(2).Width = 48;
        sheet.Column(4).Width = 38;
        sheet.Column(6).Width = 55;
        for (var column = 7; column <= 13; column++)
        {
            sheet.Column(column).Width = 21;
        }
        sheet.Column(14).Width = 48;
        sheet.Column(17).Width = 22;
        sheet.Column(6).Style.Alignment.WrapText = true;
        sheet.Column(14).Style.Alignment.WrapText = true;
        sheet.Range(7, 1, Math.Max(7, rows.Length + 6), headers.Count)
            .Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        sheet.Column(15).Style.NumberFormat.Format = "0.##";
        sheet.Column(16).Style.NumberFormat.Format = "0.##";
    }

    private static void AddPhaseSummarySheet(XLWorkbook workbook, Engagement engagement)
    {
        var sheet = workbook.Worksheets.Add("Phase Summary");
        AddTitle(sheet, engagement, "Effort and phase overview");
        sheet.Cell(4, 1).Value = "Hours are planning ranges from the sales estimate, not committed delivery dates.";
        var headers = new[] { "Journey phase", "Tasks", "Min hours", "Max hours" };
        var rows = engagement.Tasks
            .GroupBy(task => task.WbsTask.Phase)
            .Select(group => new object[]
            {
                group.Key,
                group.Count(),
                group.Sum(task => task.MinHours),
                group.Sum(task => task.MaxHours)
            })
            .ToArray();
        AddTable(sheet, 6, headers, rows, "PhaseEffort");

        var summaryRow = rows.Length + 8;
        sheet.Cell(summaryRow, 1).Value = "Total one-time hours";
        sheet.Cell(summaryRow, 3).Value = engagement.OneTimeHoursMin;
        sheet.Cell(summaryRow, 4).Value = engagement.OneTimeHoursMax;
        sheet.Cell(summaryRow + 1, 1).Value = "Monthly operations hours";
        sheet.Cell(summaryRow + 1, 3).Value = engagement.MonthlyHoursMin;
        sheet.Cell(summaryRow + 1, 4).Value = engagement.MonthlyHoursMax;
        sheet.Range(summaryRow, 1, summaryRow + 1, 4).Style.Font.Bold = true;
        sheet.Columns(1, 4).AdjustToContents(12, 52);

        if (rows.Length > 0)
        {
            sheet.Range(7, 3, rows.Length + 6, 4)
                .AddConditionalFormat()
                .DataBar(XLColor.FromHtml("#3172d8"));
        }
        sheet.SheetView.FreezeRows(6);
    }

    private static void AddHotspotsSheet(XLWorkbook workbook, Engagement engagement)
    {
        var sheet = workbook.Worksheets.Add("Hotspots");
        AddTitle(sheet, engagement, "Customer impact and hotspot signals");
        sheet.Cell(4, 1).Value =
            "Signals below are directly mapped from selected intake answers; validate impact and priority with the customer.";
        var headers = new[] { "Control ID", "Journey phase", "Workstream", "Action", "Selected intake signal" };
        var rows = engagement.Tasks
            .OrderBy(task => task.WbsTask.Sequence)
            .Select(task => (task, signal: GetHotspotSignals(engagement, task.WbsTask)))
            .Where(item => !string.IsNullOrWhiteSpace(item.signal))
            .Select(item => new object[]
            {
                item.task.WbsTask.Id,
                item.task.WbsTask.Phase,
                item.task.WbsTask.Workstream,
                item.task.WbsTask.Name,
                item.signal
            })
            .ToArray();
        AddTable(sheet, 6, headers, rows, "HotspotSignals");
        if (rows.Length == 0)
        {
            sheet.Cell(7, 1).Value = "No explicit hotspot signals were selected in the sales intake.";
        }
        sheet.Columns(1, headers.Length).AdjustToContents(12, 52);
        sheet.Column(4).Width = 40;
        sheet.Column(5).Width = 64;
        sheet.Column(5).Style.Alignment.WrapText = true;
        sheet.SheetView.FreezeRows(6);
    }

    private static void AddGuideSheet(XLWorkbook workbook, Engagement engagement)
    {
        var sheet = workbook.Worksheets.Add("Read Me");
        AddTitle(sheet, engagement, "How to use this roadmap");
        var guidance = new[]
        {
            ("Customer", engagement.Customer.Name),
            ("Engagement ID", engagement.Id.ToString("D")),
            ("Sales owner", engagement.Salesperson),
            ("Created", engagement.CreatedUtc.ToString("u")),
            ("Journey", "Phases and controls are ordered from the Secure Plus WBS. The narrative describes the intended journey, not a guaranteed schedule."),
            ("Role columns", "Starter stakeholder cues only. Confirm responsibilities, participation, and user impact with the customer and delivery team."),
            ("Hotspots", "Only explicit selections from the sales intake are mapped. They are discussion prompts, not findings from a tenant assessment."),
            ("Experience Map", "All 371 assessment catalog controls are provided for review, not presumed applicable to this customer or industry. Department impact starts Unknown and is manually rated in this workbook; edits are not saved back to the app."),
            ("Effort", "Hours are estimate ranges. Duration ranges are copied from the WBS and do not establish calendar dates."),
            ("Safety", "This workbook does not assess or modify a customer tenant. Any future tenant work requires explicit review and approval.")
        };
        for (var index = 0; index < guidance.Length; index++)
        {
            sheet.Cell(index + 4, 1).Value = guidance[index].Item1;
            sheet.Cell(index + 4, 2).Value = guidance[index].Item2;
        }
        sheet.Column(1).Width = 22;
        sheet.Column(1).Style.Font.Bold = true;
        sheet.Column(2).Width = 110;
        sheet.Column(2).Style.Alignment.WrapText = true;
        sheet.Rows(4, guidance.Length + 3).Height = 34;
    }

    private static void AddTitle(IXLWorksheet sheet, Engagement engagement, string title)
    {
        sheet.ShowGridLines = false;
        var banner = sheet.Range(1, 1, 3, 14);
        banner.Style.Fill.BackgroundColor = XLColor.FromHtml(Navy);
        banner.Style.Font.FontColor = XLColor.White;
        sheet.Cell(1, 1).Value = title;
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 1).Style.Font.FontSize = 18;
        sheet.Cell(2, 1).Value = engagement.Customer.Name;
        sheet.Cell(2, 1).Style.Font.Bold = true;
        sheet.Cell(2, 1).Style.Font.FontColor = XLColor.FromHtml(Cyan);
        sheet.Cell(3, 1).Value =
            $"Global Data Systems · Secure Plus onboarding · {engagement.Salesperson} · {engagement.CreatedUtc:yyyy-MM-dd}";
        sheet.Row(1).Height = 30;
        sheet.Cell(4, 1).Style.Font.Italic = true;
        sheet.Cell(4, 1).Style.Font.FontColor = XLColor.FromHtml(Teal);
        sheet.TabColor = XLColor.FromHtml(Teal);
    }

    private static void AddTable(
        IXLWorksheet sheet,
        int headerRow,
        IReadOnlyList<string> headers,
        IReadOnlyList<object[]> rows,
        string tableName)
    {
        for (var column = 0; column < headers.Count; column++)
        {
            sheet.Cell(headerRow, column + 1).Value = headers[column];
        }

        for (var row = 0; row < rows.Count; row++)
        {
            for (var column = 0; column < headers.Count; column++)
            {
                SetCellValue(sheet.Cell(headerRow + row + 1, column + 1), rows[row][column]);
            }
        }

        var lastDataRow = headerRow + Math.Max(1, rows.Count);
        var table = sheet.Range(headerRow, 1, lastDataRow, headers.Count).CreateTable(tableName);
        table.Theme = XLTableTheme.TableStyleLight9;
        table.ShowAutoFilter = true;
        sheet.Row(headerRow).Style.Font.Bold = true;
        StyleHeader(sheet.Range(headerRow, 1, headerRow, headers.Count));
    }

    private static void SetCellValue(IXLCell cell, object value)
    {
        switch (value)
        {
            case string text:
                cell.Value = text;
                break;
            case int integer:
                cell.Value = integer;
                break;
            case decimal number:
                cell.Value = number;
                break;
            default:
                throw new InvalidOperationException($"Unsupported roadmap cell type: {value.GetType().Name}.");
        }
    }

    private static string JourneyNarrative(string phase) => phase switch
    {
        var value when value.StartsWith("Phase 0", StringComparison.OrdinalIgnoreCase) =>
            "We learn the business context, validate dependencies, and align stakeholders on a sequenced path.",
        var value when value.StartsWith("Phase 1", StringComparison.OrdinalIgnoreCase) =>
            "We establish foundational security and prepare the people and systems for controlled change.",
        var value when value.StartsWith("Phase 2", StringComparison.OrdinalIgnoreCase) =>
            "We address selected technical debt and plan or execute the in-scope modernization work.",
        var value when value.StartsWith("Phase 3", StringComparison.OrdinalIgnoreCase) =>
            "We transition agreed services and users in reviewed stages, then validate the outcomes.",
        var value when value.StartsWith("Ongoing", StringComparison.OrdinalIgnoreCase) =>
            "We sustain operations through recurring review, guidance, and support.",
        _ => "The delivery team confirms the customer outcome and sequence during kickoff."
    };

    private static string RoleValue(string role, WbsTask task)
    {
        var work = $"{task.Phase} {task.Workstream} {task.Name} {task.Deliverable}";
        var isChange = ContainsAny(work, "migration", "rollout", "cutover", "deploy", "enforcement", "training", "decommission");
        return role switch
        {
            "Customer leadership" when ContainsAny(work, "kickoff", "roadmap", "licensing", "policy", "cutover", "decommission") => "Review / decide",
            "Customer leadership" => "Informed",
            "IT / admin" when ContainsAny(work, "tenant", "identity", "endpoint", "infrastructure", "exchange", "email", "data", "storage", "security", "intune", "device") => "Participate",
            "Managers" when ContainsAny(work, "department", "data", "training", "migration", "collaboration", "roadmap") => "Consult",
            "Employees" when isChange => "Potentially impacted",
            "Sales" when task.Sequence <= 9 => "Context / handoff",
            "Sales" => "Informed",
            "Project management" => "Coordinate",
            "Engineering" when task.Workstream.StartsWith("Ongoing", StringComparison.OrdinalIgnoreCase) => "Support",
            "Engineering" => "Deliver / assess",
            _ => ""
        };
    }

    private static string GetHotspotSignals(Engagement engagement, WbsTask task)
    {
        var work = $"{task.Phase} {task.Workstream} {task.Name} {task.Deliverable}";
        var signals = new List<string>();
        var answers = engagement.Answers
            .GroupBy(answer => answer.FieldKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(answer => answer.Value).ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.Ordinal);

        bool IsYes(string key) => HasAnswer(key, "yes");
        bool HasAnswer(string key, params string[] values) =>
            answers.TryGetValue(key, out var selected) && values.Any(selected.Contains);

        if (IsYes("onPremAD") && ContainsAny(work, "infrastructure", "identity", "domain", "active directory", "gpo"))
            signals.Add("On-premises identity selected; confirm dependencies and change windows.");
        if (IsYes("exchangeOnPrem") && ContainsAny(work, "exchange", "messaging", "mail"))
            signals.Add("On-premises Exchange selected; confirm mail-flow and migration dependencies.");
        if (HasAnswer("mfa", "none") && ContainsAny(work, "identity", "mfa", "conditional access"))
            signals.Add("No current MFA provider selected; confirm sign-in and support impact.");
        if (HasAnswer("mfa", "duo", "okta", "other") && IsYes("moveToMsMfa") &&
            ContainsAny(work, "mfa", "identity", "conditional access"))
            signals.Add("MFA transition selected; confirm pilot groups and user support.");
        if (HasAnswer("emailSecurity", "proofpoint", "mimecast", "egress", "other") &&
            IsYes("moveEmailSecurity") && ContainsAny(work, "email security", "mail security", "dns", "mail"))
            signals.Add("Email-security transition selected; confirm routing and cutover impact.");
        if ((IsYes("poorSharePoint") || HasAnswer("storage", "fileserver", "egnyte", "box", "dropbox", "other-cloud")) &&
            ContainsAny(work, "data", "storage", "sharepoint", "file"))
            signals.Add("Storage or SharePoint concern selected; confirm ownership, access, and migration readiness.");
        if (IsYes("byod") && ContainsAny(work, "endpoint", "device", "intune", "compliance"))
            signals.Add("BYOD selected; confirm privacy expectations and device support boundaries.");
        if (IsYes("aiReadiness") && ContainsAny(work, "ai", "data", "sharepoint", "permission"))
            signals.Add("AI-readiness selected; confirm governance and data-access expectations.");
        if (IsYes("redundantTools") && ContainsAny(work, "application", "tool", "dependency"))
            signals.Add("Tool consolidation selected; validate business dependencies before retirement.");

        return string.Join(" ", signals.Distinct(StringComparer.Ordinal));
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
}
