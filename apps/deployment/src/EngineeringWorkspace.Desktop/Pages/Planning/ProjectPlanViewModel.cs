using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Export;
using EngineeringWorkspace.Application.Planning;
using EngineeringWorkspace.Application.Reporting;
using EngineeringWorkspace.Desktop.Controls;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Desktop.Pages.Reporting;
using EngineeringWorkspace.Desktop.Services;
using EngineeringWorkspace.Application.Governance;
using EngineeringWorkspace.Domain.Customers;

namespace EngineeringWorkspace.Desktop.Pages.Planning;

/// <summary>An editable task of a project plan.</summary>
public sealed partial class ProjectTaskRow : ObservableObject
{
    private bool _settingDates;

    [ObservableProperty] private string _key = Guid.NewGuid().ToString("N");
    [ObservableProperty] private string _outline = string.Empty;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _bucket = string.Empty;
    [ObservableProperty] private string _role = "Engineer";
    [ObservableProperty] private string _assignedTo = string.Empty;
    [ObservableProperty] private double _hours = 4;
    [ObservableProperty] private double _percentComplete;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompleted), nameof(IsStaged), nameof(CompletionToolTip))]
    private string _status = "NotStarted";
    [ObservableProperty] private string _priority = string.Empty;
    [ObservableProperty] private string _predecessors = string.Empty;
    [ObservableProperty] private bool _isMilestone;
    [ObservableProperty] private string _start = string.Empty;
    [ObservableProperty] private string _finish = string.Empty;
    [ObservableProperty] private double _durationDays;
    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private string _changeRequestsText = string.Empty;
    [ObservableProperty] private string _controlsText = string.Empty;
    [ObservableProperty] private DateTime? _startPick;
    [ObservableProperty] private DateTime? _finishPick;
    public string? WbsTaskCode { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly FinishDate { get; set; }
    public double StartHour { get; set; }
    public double FinishHour { get; set; }

    /// <summary>Scheduling order among ready tasks (kept when the task is moved up or down, so moving never changes dates).</summary>
    public double ScheduleRank { get; set; }

    /// <summary>A start date set by hand: the task starts no earlier than this.</summary>
    public DateOnly? PinnedStart { get; set; }

    public DateOnly? ActualFinish { get; set; }

    public bool IsCompleted => Status == ProjectTaskStatuses.Completed;
    public bool IsStaged => Status == ProjectTaskStatuses.Staged;
    public string CompletionToolTip => IsCompleted ? "Completed: select to reopen" : IsStaged ? "Staged (report-only / unassigned): select to complete once enforced" : "Mark complete";

    /// <summary>Raised when Start or Finish is edited in place (true = start).</summary>
    public event Action<ProjectTaskRow, bool, DateTime>? DateEdited;

    public IReadOnlyList<int> ChangeRequestIds => ChangeRequestsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(value => int.TryParse(value.TrimStart('#'), CultureInfo.InvariantCulture, out var id) ? id : 0).Where(id => id > 0).Distinct().ToArray();

    public IReadOnlyList<string> ControlIds => ControlsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Sets the computed dates without raising <see cref="DateEdited"/>.</summary>
    public void SetSchedule(DateOnly start, DateOnly finish)
    {
        _settingDates = true;
        StartDate = start;
        FinishDate = finish;
        StartPick = start.ToDateTime(TimeOnly.MinValue);
        FinishPick = finish.ToDateTime(TimeOnly.MinValue);
        _settingDates = false;
    }

    partial void OnStartPickChanged(DateTime? value)
    {
        if (!_settingDates && value is { } date) DateEdited?.Invoke(this, true, date);
    }

    partial void OnFinishPickChanged(DateTime? value)
    {
        if (!_settingDates && value is { } date) DateEdited?.Invoke(this, false, date);
    }

    public ProjectTask ToTask(IReadOnlyList<string> predecessorKeys) => new(Key, Outline, Name, Bucket, Role, AssignedTo, StartDate, FinishDate, DurationDays, Hours,
        PercentComplete, IsMilestone, Status, Priority, WbsTaskCode, predecessorKeys, string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(), ActualFinish, ChangeRequestIds,
        ControlIds, ScheduleRank, PinnedStart);
}

/// <summary>
/// Deployment > Project Plan: project plans for a customer, built from the GDS work breakdown structure (dlv.WbsTask)
/// or the customer's remediation roadmap, scheduled on a Monday-Friday calendar of 8-hour days with dependencies, editable,
/// shown as a flow timeline, saved to dlv.ProjectPlan / dlv.ProjectTask and exported.
/// </summary>
public sealed partial class ProjectPlanViewModel(ICustomerContextService context, ICustomerProfileStore store, IPlanningRepository planning, IReportingService reporting,
    ICatalogService catalog, ExportCoordinator export, IDialogService dialogs, TaskDetailServices? detailServices = null) : CustomerScopedPageViewModel(context, store)
{
    private readonly Dictionary<string, TaskDetailViewModel> _details = new(StringComparer.Ordinal);

    /// <summary>The selected task's details (Task Detail pane; the same object is shown in its pop-out window, so edits sync both ways).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    private TaskDetailViewModel? _detail;

    public bool HasDetail => Detail is not null;

    public IPlanningRepository Planning => planning;
    public IReportingService Reporting => reporting;
    public ICatalogService Catalog => catalog;
    public IDialogService Dialogs => dialogs;
    public TaskDetailServices? DetailServices => detailServices;
    public int? PlanId => _planId;

    partial void OnSelectedTaskChanged(ProjectTaskRow? value)
    {
        if (value is null) return;
        if (!_details.TryGetValue(value.Key, out var detail)) _details[value.Key] = detail = new TaskDetailViewModel(this, value);
        Detail = detail;
        _ = detail.LoadAsync();
    }

    private ProjectTaskRow Track(ProjectTaskRow row)
    {
        row.DateEdited -= OnDateEdited;
        row.DateEdited += OnDateEdited;
        return row;
    }

    /// <summary>Start edited: the task starts no earlier than that date. Finish edited: the duration (hours) follows. Finish before Start is refused.</summary>
    private void OnDateEdited(ProjectTaskRow row, bool isStart, DateTime value)
    {
        var date = DateOnly.FromDateTime(value);
        if (isStart)
        {
            row.PinnedStart = date;
        }
        else
        {
            if (date < row.StartDate)
            {
                StatusMessage = $"Finish ({date:d MMM}) is before Start ({row.StartDate:d MMM}) for '{row.Name}'; not changed.";
                Recalculate();
                return;
            }

            var days = 0;
            for (var day = row.StartDate; day <= date; day = day.AddDays(1)) if (WorkCalendar.IsWorkDay(day)) days++;
            row.Hours = Math.Max(1, days) * Math.Clamp(HoursPerDay, 1, 24);
        }

        Recalculate();
        IsDirty = true;
        StatusMessage = $"'{row.Name}' now runs {row.Start} to {row.Finish}; dependent tasks moved with it.";
    }

    /// <summary>Renumbers outlines in list order and rewrites predecessor references, so numbering stays correct after a move or insert.</summary>
    private void Renumber()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < Tasks.Count; index++)
        {
            var number = (index + 1).ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(Tasks[index].Outline)) map.TryAdd(Tasks[index].Outline, number);
            Tasks[index].Outline = number;
        }

        foreach (var task in Tasks)
            task.Predecessors = string.Join(", ", task.Predecessors.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(outline => map.GetValueOrDefault(outline)).OfType<string>());
    }

    /// <summary>Up / Down change the display order only, within the task's group (bucket); dates and dependencies are not changed.</summary>
    [RelayCommand]
    private void MoveUp(ProjectTaskRow? row) => Move(row, -1);

    [RelayCommand]
    private void MoveDown(ProjectTaskRow? row) => Move(row, +1);

    private void Move(ProjectTaskRow? row, int direction)
    {
        if (row is null) return;
        var index = Tasks.IndexOf(row);
        var target = index + direction;
        while (target >= 0 && target < Tasks.Count && Tasks[target].Bucket != row.Bucket) target += direction;
        if (target < 0 || target >= Tasks.Count) return;
        Tasks.Move(index, target);
        Renumber();
        Recalculate();
        SelectedTask = row;
        IsDirty = true;
    }

    /// <summary>Insert Above / Below: the neighbour's start date, one day, the same group, no dependencies.</summary>
    [RelayCommand]
    private void InsertAbove(ProjectTaskRow? row) => Insert(row, above: true);

    [RelayCommand]
    private void InsertBelow(ProjectTaskRow? row) => Insert(row, above: false);

    private void Insert(ProjectTaskRow? row, bool above)
    {
        if (row is null) return;
        var task = Track(new ProjectTaskRow
        {
            Name = "New task", Bucket = row.Bucket, Role = row.Role, Hours = Math.Clamp(HoursPerDay, 1, 24), PinnedStart = row.StartDate,
            ScheduleRank = row.ScheduleRank + (above ? -0.5 : 0.5)
        });
        Tasks.Insert(Tasks.IndexOf(row) + (above ? 0 : 1), task);
        Renumber();
        Recalculate();
        SelectedTask = task;
        IsDirty = true;
    }

    /// <summary>The completion check: open → complete (100%, actual finish today); complete → reopen (confirmed); Staged → complete only once enforced.</summary>
    [RelayCommand]
    private async Task ToggleCompleteAsync(ProjectTaskRow? row)
    {
        if (row is null) return;
        string eventType;
        string summary;
        if (row.IsCompleted)
        {
            if (!dialogs.Confirm(new ConfirmationRequest("Reopen Task", $"'{row.Name}' will be reopened and its completion cleared. The history keeps the earlier completion.",
                    [new("Task", $"{row.Outline}. {row.Name}")], "Reopen Task")))
                return;
            row.Status = ProjectTaskStatuses.InProgress;
            row.PercentComplete = 0;
            row.ActualFinish = null;
            (eventType, summary) = (ProjectTaskEventTypes.Reopened, "Reopened.");
        }
        else
        {
            if (row.IsStaged)
            {
                var latest = await LatestResultsAsync();
                var task = row.ToTask([]);
                DateTimeOffset? stagedAt = null;
                if (PlanId is { } planId)
                {
                    try
                    {
                        stagedAt = (await planning.GetTaskEventsAsync(planId, task.Key)).Where(item => item.EventType == ProjectTaskEventTypes.RemediationSucceeded)
                            .Select(item => (DateTimeOffset?)item.CreatedAtUtc).Min();
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
                    {
                        stagedAt = null;
                    }
                }

                var includesCa = task.Controls.Any(id => catalog.Catalog.Find(id)?.Kind == EngineeringWorkspace.Application.Catalog.CatalogKinds.ConditionalAccess);
                var (canComplete, reason) = TaskRemediationService.CanComplete(task, latest, stagedAt, DateTimeOffset.UtcNow, includesCa);
                if (!canComplete)
                {
                    StatusMessage = reason;
                    return;
                }
            }

            row.Status = ProjectTaskStatuses.Completed;
            row.PercentComplete = 100;
            row.ActualFinish = DateOnly.FromDateTime(DateTime.Today);
            (eventType, summary) = (ProjectTaskEventTypes.Completed, $"Completed on {DateTime.Today:yyyy-MM-dd}.");
        }

        Recalculate();
        IsDirty = true;
        await RecordEventAsync(row, eventType, summary);
    }

    /// <summary>Latest result per control ("Pass", "Fail"...) for the selected customer.</summary>
    public async Task<IReadOnlyDictionary<string, string>> LatestResultsAsync()
    {
        if (SelectedCustomer is not { } customer || !reporting.IsAvailable) return new Dictionary<string, string>();
        var results = await reporting.GetCustomerResultsAsync(customer.Id.Value, ReportScope.ActiveBaselines);
        return results.Facts.GroupBy(fact => fact.ControlId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Result, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Saves the plan (so the task exists) and appends a history entry.</summary>
    public async Task RecordEventAsync(ProjectTaskRow row, string eventType, string summary)
    {
        if (!await EnsureSavedAsync()) return;
        try
        {
            await planning.AddTaskEventAsync(new ProjectTaskEvent(0, _planId!.Value, row.Key, eventType, summary, null, null, CurrentUser, DateTimeOffset.UtcNow));
            if (_details.TryGetValue(row.Key, out var detail)) await detail.LoadAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
        {
            StatusMessage = $"The task history could not be written: {exception.Message}";
        }
    }

    public string CurrentUser => Environment.UserDomainName + "\\" + Environment.UserName;

    /// <summary>Saves the plan if it has never been saved or has unsaved changes; false when it could not be saved.</summary>
    public async Task<bool> EnsureSavedAsync()
    {
        if (_planId is not null && !IsDirty) return true;
        await SaveAsync();
        return _planId is not null && !IsDirty;
    }
    public static IReadOnlyList<string> Sources { get; } = ["GDS Secure Plus work breakdown structure", "The customer's remediation roadmap", "Both (WBS then roadmap)", "Empty plan"];
    public static IReadOnlyList<string> Statuses { get; } = ["NotStarted", "InProgress", "Blocked", ProjectTaskStatuses.Staged, "Completed"];
    public static IReadOnlyList<string> PlanStatuses { get; } = ["Draft", "Proposed", "Approved", "Active", "Completed"];

    public ObservableCollection<ProjectPlan> Plans { get; } = [];
    public ObservableCollection<ProjectTaskRow> Tasks { get; } = [];
    public ObservableCollection<TimelineNode> Timeline { get; } = [];
    public ObservableCollection<ChartSlice> HoursByBucket { get; } = [];
    public ObservableCollection<ChartSlice> HoursByRole { get; } = [];
    public ObservableCollection<KpiTile> Kpis { get; } = [];

    [ObservableProperty] private ProjectPlan? _selectedPlan;
    [ObservableProperty] private ProjectTaskRow? _selectedTask;
    [ObservableProperty] private string _planName = string.Empty;
    [ObservableProperty] private string _planStatus = "Draft";
    [ObservableProperty] private DateTime _startDate = NextMonday(DateTime.Today);
    [ObservableProperty] private double _hoursPerDay = 8;
    [ObservableProperty] private int _engineers = 2;
    [ObservableProperty] private string _source = Sources[0];
    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private double _zoom = 40;
    [ObservableProperty] private string _detailText = string.Empty;
    [ObservableProperty] private bool _isDirty;

    private int? _planId;

    private static DateTime NextMonday(DateTime date)
    {
        while (date.DayOfWeek != DayOfWeek.Monday) date = date.AddDays(1);
        return date;
    }

    partial void OnStartDateChanged(DateTime value) => Recalculate();
    partial void OnHoursPerDayChanged(double value) => Recalculate();
    partial void OnEngineersChanged(int value) => Recalculate();

    partial void OnSelectedPlanChanged(ProjectPlan? value)
    {
        if (value is not null) Open(value);
    }

    protected override async Task OnCustomerChangedAsync(CustomerProfile? customer)
    {
        Plans.Clear();
        Tasks.Clear();
        _planId = null;
        Recalculate();
        if (customer is null)
        {
            StatusMessage = "Choose a customer.";
            return;
        }

        if (!planning.IsAvailable)
        {
            StatusMessage = "The application database is not ready; project plans are stored in it.";
            return;
        }

        foreach (var plan in await planning.GetProjectPlansAsync(customer.Id.Value)) Plans.Add(plan);
        PlanName = $"{customer.Name} – Secure Plus deployment";
        StatusMessage = Plans.Count == 0 ? $"{customer.Name} has no project plan yet. Choose a source and select Build Plan." : null;
    }

    private void Open(ProjectPlan plan)
    {
        _planId = plan.Id;
        PlanName = plan.Name;
        PlanStatus = plan.Status;
        Tasks.Clear();
        _details.Clear();
        Detail = null;
        var rank = 0;
        foreach (var task in plan.Tasks)
            Tasks.Add(Track(new ProjectTaskRow
            {
                Key = task.Key, Outline = task.Outline, Name = task.Name, Bucket = task.Bucket ?? string.Empty, Role = task.Role ?? "Engineer", AssignedTo = task.AssignedTo ?? string.Empty,
                Hours = task.EffortHours, PercentComplete = task.PercentComplete, Status = task.Status, Priority = task.Priority ?? string.Empty, IsMilestone = task.IsMilestone,
                WbsTaskCode = task.WbsTaskCode, Notes = task.Notes ?? string.Empty, ActualFinish = task.ActualFinish, PinnedStart = task.PinnedStart,
                ScheduleRank = task.ScheduleRank ?? rank++, ChangeRequestsText = string.Join(", ", task.ChangeRequests), ControlsText = string.Join(", ", task.Controls),
                Predecessors = string.Join(", ", task.Predecessors.Select(key => plan.Tasks.FirstOrDefault(other => other.Key == key)?.Outline).OfType<string>())
            }));
        StartDate = plan.Start.ToDateTime(TimeOnly.MinValue);
        Recalculate();
        IsDirty = false;
        StatusMessage = $"Opened '{plan.Name}' ({plan.Tasks.Count} task(s)).";
    }

    [RelayCommand]
    private async Task BuildAsync()
    {
        if (SelectedCustomer is not { } customer) return;
        IsBusy = true;
        StatusMessage = null;
        try
        {
            Tasks.Clear();
            _details.Clear();
            Detail = null;
            _planId = null;
            var outline = 0;
            if (Source is var source && (source == Sources[0] || source == Sources[2]))
            {
                var wbs = await planning.GetWorkBreakdownAsync();
                if (wbs.Tasks.Count == 0) StatusMessage = "The WBS is not loaded yet (knowledge base phase 2); building from the roadmap only.";
                var phaseOrder = wbs.Phases.ToDictionary(phase => phase.Code, phase => phase, StringComparer.OrdinalIgnoreCase);
                var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var task in wbs.Tasks.OrderBy(task => task.PhaseCode is { } code && phaseOrder.TryGetValue(code, out var phase) ? phase.Order : 99)
                             .ThenBy(task => task.SortKey, StringComparer.Ordinal))
                {
                    var row = new ProjectTaskRow
                    {
                        Outline = (++outline).ToString(CultureInfo.InvariantCulture), Name = task.DisplayName,
                        Bucket = task.PhaseCode is { } code && phaseOrder.TryGetValue(code, out var phase) ? phase.Name : task.Workstream ?? task.Bucket ?? "Delivery",
                        Role = task.Workstream ?? "Engineer", Hours = Math.Max(0.5, task.HoursLikely > 0 ? task.HoursLikely : task.HoursMax > 0 ? task.HoursMax : 1),
                        Priority = task.Priority ?? string.Empty, WbsTaskCode = task.Code, IsMilestone = task.HoursLikely == 0 && task.HoursMax == 0,
                        ScheduleRank = Tasks.Count
                    };
                    Track(row);
                    keys[task.Code] = row.Outline;
                    Tasks.Add(row);
                }

                foreach (var (task, row) in wbs.Tasks.Select(task => (task, Tasks.First(row => row.WbsTaskCode == task.Code))))
                    row.Predecessors = string.Join(", ", task.Predecessors.Where(keys.ContainsKey).Select(code => keys[code]));
            }

            if (Source == Sources[1] || Source == Sources[2])
            {
                if (!reporting.IsAvailable) StatusMessage = "The database is not ready; the roadmap could not be read.";
                else
                {
                    var results = await reporting.GetCustomerResultsAsync(customer.Id.Value, ReportScope.ActiveBaselines);
                    var plan = RoadmapPlanner.Plan(RoadmapBuilder.Build(results.Facts, catalog.Catalog), catalog.Catalog,
                        new RoadmapPlanOptions(DateOnly.FromDateTime(StartDate), HoursPerDay, Engineers));
                    var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var anchor = Tasks.LastOrDefault()?.Outline;
                    foreach (var package in plan.Packages)
                    {
                        var row = new ProjectTaskRow
                        {
                            Outline = (++outline).ToString(CultureInfo.InvariantCulture), Name = $"Remediate: {package.Name} ({package.Items.Count} control(s))",
                            Bucket = $"Remediation – Phase {package.Phase}", Role = "Engineer", Hours = Math.Max(0.5, Math.Round(package.Hours, 1)),
                            Priority = package.Phase switch { 1 => "Critical", 2 => "High", 3 => "Medium", _ => "Low" },
                            ScheduleRank = Tasks.Count, ControlsText = string.Join(", ", package.Items.Select(item => item.Item.Fact.ControlId).Distinct(StringComparer.OrdinalIgnoreCase))
                        };
                        Track(row);
                        keys[package.Key] = row.Outline;
                        var predecessors = package.DependsOn.Where(keys.ContainsKey).Select(key => keys[key]).ToList();
                        if (predecessors.Count == 0 && anchor is not null && Source == Sources[2]) predecessors.Add(anchor);
                        row.Predecessors = string.Join(", ", predecessors);
                        Tasks.Add(row);
                    }

                    if (!results.HasResults) StatusMessage = $"{customer.Name} has no stored assessment, so the roadmap is empty.";
                }
            }

            Recalculate();
            IsDirty = true;
            StatusMessage ??= $"Built {Tasks.Count} task(s). Edit them, then Save Plan.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void AddTask()
    {
        var outline = (Tasks.Select(task => int.TryParse(task.Outline, out var number) ? number : 0).DefaultIfEmpty(0).Max() + 1).ToString(CultureInfo.InvariantCulture);
        Tasks.Add(Track(new ProjectTaskRow
        {
            Outline = outline, Name = "New task", Bucket = "Delivery", Predecessors = SelectedTask?.Outline ?? string.Empty,
            ScheduleRank = Tasks.Select(task => task.ScheduleRank).DefaultIfEmpty(0).Max() + 1
        }));
        Recalculate();
        IsDirty = true;
    }

    [RelayCommand]
    private void RemoveTask(ProjectTaskRow? row)
    {
        if ((row ?? SelectedTask) is not { } task) return;
        Tasks.Remove(task);
        foreach (var other in Tasks)
            other.Predecessors = string.Join(", ", other.Predecessors.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(item => item != task.Outline));
        Recalculate();
        IsDirty = true;
    }

    [RelayCommand]
    public void Recalculate()
    {
        Timeline.Clear();
        HoursByBucket.Clear();
        HoursByRole.Clear();
        Kpis.Clear();
        if (Tasks.Count == 0) return;
        var calendar = new WorkCalendar(DateOnly.FromDateTime(StartDate), Math.Clamp(HoursPerDay, 1, 24));
        var byOutline = Tasks.GroupBy(task => task.Outline).ToDictionary(group => group.Key, group => group.First());
        // Priority comes from the saved schedule rank, not the display order, so moving a task up or down never changes dates.
        var items = Tasks.Select(task => new SchedulableItem(task.Key, task.IsMilestone ? 0 : Math.Max(0, task.Hours), -task.ScheduleRank,
            task.Predecessors.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(byOutline.ContainsKey).Select(outline => byOutline[outline].Key).ToArray(),
            StartNotBeforeHour: task.PinnedStart is { } pinned ? calendar.HourOf(pinned) : null)).ToArray();
        var schedule = WorkScheduler.Schedule(items, calendar, Math.Clamp(Engineers, 1, 50)).ToDictionary(item => item.Item.Key);
        foreach (var task in Tasks)
        {
            var slot = schedule[task.Key];
            task.StartHour = slot.StartHour;
            task.FinishHour = slot.FinishHour;
            task.SetSchedule(slot.StartDate, slot.FinishDate);
            task.Start = slot.StartDate.ToString("ddd d MMM", CultureInfo.CurrentCulture);
            task.Finish = slot.FinishDate.ToString("ddd d MMM", CultureInfo.CurrentCulture);
            task.DurationDays = Math.Round(Math.Max(0, task.Hours) / Math.Max(1, HoursPerDay), 1);
        }

        var finish = Tasks.Max(task => task.FinishDate);
        var hours = Tasks.Sum(task => task.Hours);
        Kpis.Add(new KpiTile("Tasks", Tasks.Count.ToString("N0", CultureInfo.CurrentCulture), $"{Tasks.Count(task => task.Status == "Completed")} completed"));
        Kpis.Add(new KpiTile("Effort", $"{hours:N0} h", $"{hours / Math.Max(1, HoursPerDay):N0} person-days"));
        Kpis.Add(new KpiTile("Start", calendar.Start.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture), $"{HoursPerDay:0.#}-hour days, Monday to Friday"));
        Kpis.Add(new KpiTile("Finish", finish.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture), $"{calendar.WorkDaysUntil(finish)} work day(s) with {Engineers} engineer(s)"));
        Kpis.Add(new KpiTile("Complete", $"{(hours <= 0 ? 0 : Tasks.Sum(task => task.Hours * task.PercentComplete / 100) / hours):P0}", "weighted by effort"));
        foreach (var bucket in Tasks.GroupBy(task => string.IsNullOrWhiteSpace(task.Bucket) ? "Unassigned" : task.Bucket).OrderByDescending(group => group.Sum(task => task.Hours)))
            HoursByBucket.Add(new ChartSlice(bucket.Key, Math.Round(bucket.Sum(task => task.Hours), 1), null, $"{bucket.Count()} task(s)", null,
                bucket.Select(task => new ChartSlice(task.Name, task.Hours, null, $"{task.Start} – {task.Finish}")).ToArray()));
        foreach (var role in Tasks.GroupBy(task => string.IsNullOrWhiteSpace(task.Role) ? "Unassigned" : task.Role).OrderByDescending(group => group.Sum(task => task.Hours)))
            HoursByRole.Add(new ChartSlice(role.Key, Math.Round(role.Sum(task => task.Hours), 1), null, $"{role.Count()} task(s)"));
        var buckets = Tasks.Select(task => task.Bucket).Distinct().ToList();
        string[] palette = ["#2B88D8", "#E06C00", "#107C10", "#8764B8", "#C42B1C", "#00B7C3", "#CA5010", "#5B6B7F"];
        foreach (var task in Tasks.Where(task => string.IsNullOrWhiteSpace(Search) || task.Name.Contains(Search, StringComparison.CurrentCultureIgnoreCase)).Take(150))
            Timeline.Add(new TimelineNode(task.Key, $"{task.Outline}. {task.Name}", $"{task.Hours:0.#} h · {task.Role}", task.StartHour, Math.Max(task.FinishHour, task.StartHour + 0.5),
                task.Predecessors.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(byOutline.ContainsKey).Select(outline => byOutline[outline].Key).ToArray(),
                task.IsCompleted ? "#107C10" : task.IsStaged ? "#C19C00" : palette[Math.Max(0, buckets.IndexOf(task.Bucket)) % palette.Length],
                $"{task.Outline}. {task.Name}\n{task.Bucket}\n{task.Start} – {task.Finish}, {task.Hours:0.#} h, {task.Role}{(task.IsCompleted ? "\nCompleted" : task.IsStaged ? "\nStaged: created, not yet enforced" : string.Empty)}", task));
    }

    [RelayCommand]
    private void ShowDetail(object? item)
    {
        if (item is TimelineNode { Tag: ProjectTaskRow row })
        {
            SelectedTask = row;
            DetailText = $"{row.Outline}. {row.Name} — {row.Start} to {row.Finish}, {row.Hours:0.#} h, {row.Role}{(row.Predecessors.Length > 0 ? $", after {row.Predecessors}" : string.Empty)}";
        }
        else if (item is ChartSlice slice) DetailText = $"{slice.Label}: {slice.Value:0.#} h. {slice.Detail}";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (SelectedCustomer is not { } customer || Tasks.Count == 0)
        {
            StatusMessage = "Build or add tasks first.";
            return;
        }

        Recalculate();
        var plan = new ProjectPlan(_planId, string.IsNullOrWhiteSpace(PlanName) ? $"{customer.Name} project plan" : PlanName.Trim(), DateOnly.FromDateTime(StartDate),
            Tasks.Max(task => task.FinishDate), PlanStatus,
            Tasks.Select(task => task.ToTask(task.Predecessors.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(outline => Tasks.FirstOrDefault(other => other.Outline == outline)?.Key).OfType<string>().ToArray())).ToArray());
        try
        {
            var saved = await planning.SaveProjectPlanAsync(customer.Id.Value, plan);
            _planId = saved.Id;
            Plans.Clear();
            foreach (var item in await planning.GetProjectPlansAsync(customer.Id.Value)) Plans.Add(item);
            IsDirty = false;
            StatusMessage = $"'{saved.Name}' saved ({saved.Tasks.Count} task(s), finishing {saved.Finish:ddd d MMM yyyy}).";
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
        {
            StatusMessage = $"The plan could not be saved: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedCustomer is not { } customer || _planId is not { } id) return;
        if (!dialogs.Confirm(new ConfirmationRequest("Delete Project Plan", "The plan and all its tasks are deleted from the application database.",
                [new("Plan", PlanName), new("Customer", customer.Name)], "Delete Plan")))
            return;
        await planning.DeletePlanSafeAsync(customer.Id.Value, id);
        Plans.Clear();
        foreach (var item in await planning.GetProjectPlansAsync(customer.Id.Value)) Plans.Add(item);
        Tasks.Clear();
        _planId = null;
        Recalculate();
        StatusMessage = "The plan was deleted.";
    }

    [RelayCommand]
    private void NewPlan()
    {
        Tasks.Clear();
        _planId = null;
        SelectedPlan = null;
        PlanStatus = "Draft";
        Recalculate();
        StatusMessage = "New plan: choose a source and select Build Plan, or add tasks.";
    }

    [RelayCommand]
    private async Task ExportAsync(object? format)
    {
        if (Tasks.Count == 0)
        {
            StatusMessage = "There is nothing to export.";
            return;
        }

        var document = new ExportDocument(string.IsNullOrWhiteSpace(PlanName) ? "Project Plan" : PlanName, SelectedCustomer?.Name, DateTimeOffset.UtcNow,
            Kpis.Select(kpi => new KeyValuePair<string, string>(kpi.Title, $"{kpi.Value} – {kpi.Detail}")).ToArray(),
            [
                new ExportTable("Tasks", ["Outline", "Task", "Bucket", "Role", "Assigned To", "Hours", "Start", "Finish", "Duration (days)", "Predecessors", "Status", "% Complete", "Priority", "WBS"],
                    Tasks.Select(task => (IReadOnlyList<string?>)
                    [
                        task.Outline, task.Name, task.Bucket, task.Role, task.AssignedTo, task.Hours.ToString("0.#", CultureInfo.InvariantCulture),
                        task.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), task.FinishDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        task.DurationDays.ToString("0.#", CultureInfo.InvariantCulture), task.Predecessors, task.Status, task.PercentComplete.ToString("0", CultureInfo.InvariantCulture),
                        task.Priority, task.WbsTaskCode
                    ]).ToArray())
            ], "Schedule: Monday to Friday working days; predecessors finish before a task starts.");
        try
        {
            StatusMessage = await export.ExportAsync(document, ExportCoordinator.Parse(format)) ?? StatusMessage;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"The export could not be written: {exception.Message}";
        }
    }
}

internal static class PlanningExtensions
{
    public static async Task DeletePlanSafeAsync(this IPlanningRepository planning, string customerKey, int id)
    {
        try
        {
            await planning.DeleteProjectPlanAsync(customerKey, id);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
        {
            // Reported by the caller's refresh.
        }
    }
}
