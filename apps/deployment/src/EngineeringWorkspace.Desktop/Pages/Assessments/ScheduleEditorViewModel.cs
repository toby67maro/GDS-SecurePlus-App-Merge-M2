using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Scheduling;
using EngineeringWorkspace.Desktop.Controls;

namespace EngineeringWorkspace.Desktop.Pages.Assessments;

/// <summary>
/// The Recurring Assessment editor. Frequency comes first; the fields that follow depend on it (Hourly: Every N Hours,
/// Which Days, active window; Daily: Which Days, Run Time; Weekly: Which Days, Run Time, Every N Weeks; Monthly: Which
/// Months, day of month or Nth weekday, Run Time). Validation messages and the next-run preview update while editing.
/// </summary>
public sealed partial class ScheduleEditorViewModel : ObservableObject
{
    public const string AllCustomersKey = "*";

    public ScheduleEditorViewModel()
    {
        WhichDays = new MultiSelectFilter("Which Days", emptyMeansAll: false,
            ScheduleMasks.WeekOrder.Select(day => (((int)day).ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(day))));
        WhichMonths = new MultiSelectFilter("Which Months", emptyMeansAll: false,
            Enumerable.Range(1, 12).Select(month => (month.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(month))));
        WhichDays.SetAll(true);
        WhichMonths.SetAll(true);
        WhichDays.Changed += (_, _) => Recalculate();
        WhichMonths.Changed += (_, _) => Recalculate();
        Reset(null);
    }

    public ObservableCollection<Choice<string>> Customers { get; } = [];

    public IReadOnlyList<Choice<ScheduleFrequency>> Frequencies { get; } =
    [
        new(ScheduleFrequency.Hourly, "Hourly"), new(ScheduleFrequency.Daily, "Daily"), new(ScheduleFrequency.Weekly, "Weekly"), new(ScheduleFrequency.Monthly, "Monthly")
    ];

    public IReadOnlyList<Choice<int>> DaysOfMonth { get; } =
        [.. Enumerable.Range(1, 31).Select(day => new Choice<int>(day, day.ToString(CultureInfo.CurrentCulture))), new Choice<int>(ScheduleMasks.LastDay, "Last day")];

    public IReadOnlyList<Choice<int>> WeeksOfMonth { get; } = [.. Enumerable.Range(1, 5).Select(week => new Choice<int>(week, ScheduleMasks.Ordinal(week)))];

    public IReadOnlyList<Choice<DayOfWeek>> Weekdays { get; } =
        [.. ScheduleMasks.WeekOrder.Select(day => new Choice<DayOfWeek>(day, CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(day)))];

    public MultiSelectFilter WhichDays { get; }
    public MultiSelectFilter WhichMonths { get; }
    public ObservableCollection<string> Messages { get; } = [];
    public ObservableCollection<string> Preview { get; } = [];

    public string TimeZoneText => $"Times are in {TimeZoneInfo.Local.DisplayName}.";

    /// <summary>The schedule being edited (0 for a new one).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorTitle), nameof(IsEditing))]
    private int _scheduleId;

    [ObservableProperty] private Choice<string>? _customer;
    [ObservableProperty] private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFrequency), nameof(IsHourly), nameof(IsDaily), nameof(IsWeekly), nameof(IsMonthly), nameof(ShowRunTime), nameof(ShowWhichDays))]
    private Choice<ScheduleFrequency>? _frequency;

    [ObservableProperty] private string _runTime = "06:00";
    [ObservableProperty] private string _intervalHours = "4";
    [ObservableProperty] private bool _useWindow;
    [ObservableProperty] private string _windowStart = "08:00";
    [ObservableProperty] private string _windowEnd = "18:00";
    [ObservableProperty] private string _intervalWeeks = "1";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDayOfMonthMode))]
    private bool _isNthWeekdayMode;

    [ObservableProperty] private Choice<int>? _dayOfMonth;
    [ObservableProperty] private Choice<int>? _weekOfMonth;
    [ObservableProperty] private Choice<DayOfWeek>? _weekday;
    [ObservableProperty] private bool _isValid;
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private string _scanType = ScanTypes.InDepth;

    public IReadOnlyList<ScanTypeOption> ScanTypeOptions => ScanTypeOption.All;

    private DateOnly? _anchorDate;
    private DateTimeOffset? _lastRunUtc;

    public string EditorTitle => ScheduleId > 0 ? "Edit Schedule" : "New Schedule";
    public bool IsEditing => ScheduleId > 0;
    public bool HasFrequency => Frequency is not null;
    public bool IsHourly => Frequency?.Value == ScheduleFrequency.Hourly;
    public bool IsDaily => Frequency?.Value == ScheduleFrequency.Daily;
    public bool IsWeekly => Frequency?.Value == ScheduleFrequency.Weekly;
    public bool IsMonthly => Frequency?.Value == ScheduleFrequency.Monthly;
    public bool ShowRunTime => HasFrequency && !IsHourly;
    public bool ShowWhichDays => IsHourly || IsDaily || IsWeekly;
    public bool IsDayOfMonthMode => !IsNthWeekdayMode;

    partial void OnCustomerChanged(Choice<string>? value) => Recalculate();
    partial void OnNameChanged(string value) => Recalculate();
    partial void OnRunTimeChanged(string value) => Recalculate();
    partial void OnIntervalHoursChanged(string value) => Recalculate();
    partial void OnUseWindowChanged(bool value) => Recalculate();
    partial void OnWindowStartChanged(string value) => Recalculate();
    partial void OnWindowEndChanged(string value) => Recalculate();
    partial void OnIntervalWeeksChanged(string value) => Recalculate();
    partial void OnIsNthWeekdayModeChanged(bool value) => Recalculate();
    partial void OnDayOfMonthChanged(Choice<int>? value) => Recalculate();
    partial void OnWeekOfMonthChanged(Choice<int>? value) => Recalculate();
    partial void OnWeekdayChanged(Choice<DayOfWeek>? value) => Recalculate();

    partial void OnFrequencyChanged(Choice<ScheduleFrequency>? value)
    {
        if (value is not null && string.IsNullOrWhiteSpace(Name)) Name = $"{value.Label} assessment";
        // Weekly defaults to one day (Monday) rather than every day.
        if (value?.Value == ScheduleFrequency.Weekly && WhichDays.SelectedValues.Count == 7 && ScheduleId == 0)
            WhichDays.Select([((int)DayOfWeek.Monday).ToString(CultureInfo.InvariantCulture)]);
        Recalculate();
    }

    public void SetCustomers(IEnumerable<(string Key, string Name)> customers, string? defaultKey)
    {
        var current = Customer?.Value ?? defaultKey;
        Customers.Clear();
        Customers.Add(new Choice<string>(AllCustomersKey, "All connected customers"));
        foreach (var (key, name) in customers) Customers.Add(new Choice<string>(key, name));
        Customer = Customers.FirstOrDefault(choice => choice.Value == current) ?? Customers.Skip(1).FirstOrDefault() ?? Customers[0];
    }

    /// <summary>Clears the editor for a new schedule; Frequency is chosen first.</summary>
    public void Reset(string? defaultCustomerKey)
    {
        ScheduleId = 0;
        _anchorDate = null;
        _lastRunUtc = null;
        Customer = Customers.FirstOrDefault(choice => choice.Value == defaultCustomerKey) ?? Customer;
        Name = string.Empty;
        Frequency = null;
        RunTime = "06:00";
        IntervalHours = "4";
        UseWindow = false;
        WindowStart = "08:00";
        WindowEnd = "18:00";
        IntervalWeeks = "1";
        IsNthWeekdayMode = false;
        DayOfMonth = DaysOfMonth[0];
        WeekOfMonth = WeeksOfMonth[1];
        Weekday = Weekdays.First(day => day.Value == DayOfWeek.Tuesday);
        IsEnabled = true;
        WhichDays.SetAll(true);
        WhichMonths.SetAll(true);
        Recalculate();
    }

    public void Load(AssessmentSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var recurrence = schedule.Recurrence;
        ScheduleId = schedule.ScheduleId;
        _anchorDate = recurrence.AnchorDate;
        _lastRunUtc = schedule.LastRunUtc;
        var key = schedule.AllCustomers ? AllCustomersKey : schedule.CustomerKeys.FirstOrDefault();
        Customer = Customers.FirstOrDefault(choice => choice.Value == key) ?? Customer;
        Name = schedule.Name;
        IsEnabled = schedule.IsEnabled;
        ScanType = ScanTypes.Normalize(schedule.ScanType);
        Frequency = Frequencies.First(choice => choice.Value == recurrence.Frequency);
        RunTime = recurrence.RunTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        IntervalHours = recurrence.IntervalHours.ToString(CultureInfo.InvariantCulture);
        UseWindow = recurrence.WindowStart is not null || recurrence.WindowEnd is not null;
        WindowStart = (recurrence.WindowStart ?? new TimeOnly(8, 0)).ToString("HH:mm", CultureInfo.InvariantCulture);
        WindowEnd = (recurrence.WindowEnd ?? new TimeOnly(18, 0)).ToString("HH:mm", CultureInfo.InvariantCulture);
        IntervalWeeks = recurrence.IntervalWeeks.ToString(CultureInfo.InvariantCulture);
        IsNthWeekdayMode = recurrence.MonthlyMode == MonthlyMode.NthWeekday;
        DayOfMonth = DaysOfMonth.FirstOrDefault(choice => choice.Value == recurrence.DayOfMonth) ?? DaysOfMonth[0];
        WeekOfMonth = WeeksOfMonth.FirstOrDefault(choice => choice.Value == recurrence.WeekOfMonth) ?? WeeksOfMonth[0];
        Weekday = Weekdays.First(choice => choice.Value == recurrence.Weekday);
        var days = recurrence.DaysOfWeek == 0 ? ScheduleMasks.AllDays : recurrence.DaysOfWeek;
        WhichDays.Select(ScheduleMasks.WeekOrder.Where(day => ScheduleMasks.HasDay(days, day)).Select(day => ((int)day).ToString(CultureInfo.InvariantCulture)));
        WhichMonths.Select(Enumerable.Range(1, 12).Where(month => ScheduleMasks.HasMonth(recurrence.Months, month)).Select(month => month.ToString(CultureInfo.InvariantCulture)));
        Recalculate();
    }

    /// <summary>The schedule described by the editor, or null with <see cref="Messages"/> explaining why.</summary>
    public AssessmentSchedule? Build()
    {
        Recalculate();
        if (!IsValid || Frequency is null || Customer is null) return null;
        return new AssessmentSchedule(ScheduleId, Name.Trim(), [Customer.Value], BuildRecurrence()!, IsEnabled, TimeZoneInfo.Local.Id, _lastRunUtc, null)
        {
            ScanType = ScanTypes.Normalize(ScanType)
        };
    }

    private ScheduleRecurrence? BuildRecurrence()
    {
        if (Frequency is null) return null;
        var days = ScheduleMasks.Days(WhichDays.SelectedValues.Select(value => (DayOfWeek)int.Parse(value, CultureInfo.InvariantCulture)));
        var months = ScheduleMasks.Months(WhichMonths.SelectedValues.Select(value => int.Parse(value, CultureInfo.InvariantCulture)));
        return new ScheduleRecurrence(
            Frequency.Value,
            ParseTime(RunTime) ?? new TimeOnly(6, 0),
            days,
            int.TryParse(IntervalHours, NumberStyles.Integer, CultureInfo.CurrentCulture, out var hours) ? hours : 0,
            IsHourly && UseWindow ? ParseTime(WindowStart) : null,
            IsHourly && UseWindow ? ParseTime(WindowEnd) : null,
            int.TryParse(IntervalWeeks, NumberStyles.Integer, CultureInfo.CurrentCulture, out var weeks) ? weeks : 0,
            _anchorDate,
            months,
            IsNthWeekdayMode ? MonthlyMode.NthWeekday : MonthlyMode.DayOfMonth,
            DayOfMonth?.Value ?? 1,
            WeekOfMonth?.Value ?? 1,
            Weekday?.Value ?? DayOfWeek.Monday);
    }

    private static TimeOnly? ParseTime(string text) =>
        TimeOnly.TryParseExact(text.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;

    private void Recalculate()
    {
        Messages.Clear();
        Preview.Clear();
        if (Customer is null) Messages.Add("Choose the customer to assess.");
        if (string.IsNullOrWhiteSpace(Name)) Messages.Add("Enter a name for the schedule.");
        if (Frequency is null)
        {
            Messages.Add("Choose a frequency to continue.");
            IsValid = false;
            return;
        }

        if (ShowRunTime && ParseTime(RunTime) is null) Messages.Add("Enter the run time as HH:mm (24-hour), for example 06:30.");
        if (IsHourly && UseWindow && (ParseTime(WindowStart) is null || ParseTime(WindowEnd) is null)) Messages.Add("Enter the active window as HH:mm to HH:mm.");
        if (IsHourly && !int.TryParse(IntervalHours, NumberStyles.Integer, CultureInfo.CurrentCulture, out _)) Messages.Add("Every N Hours must be a whole number from 1 to 23.");
        if (IsWeekly && !int.TryParse(IntervalWeeks, NumberStyles.Integer, CultureInfo.CurrentCulture, out _)) Messages.Add("Every N Weeks must be a whole number from 1 to 52.");
        var recurrence = BuildRecurrence()!;
        var validation = NextRunCalculator.Validate(recurrence);
        foreach (var message in validation) Messages.Add(message.IsWarning ? $"Note: {message.Message}" : message.Message);
        IsValid = !Messages.Any(message => !message.StartsWith("Note:", StringComparison.Ordinal));
        if (!IsValid) return;
        var runs = NextRunCalculator.NextOccurrences(recurrence, DateTimeOffset.UtcNow, TimeZoneInfo.Local, 3);
        if (runs.Count == 0) Preview.Add("This schedule never runs with these settings.");
        foreach (var run in runs) Preview.Add(run.ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture));
    }
}
