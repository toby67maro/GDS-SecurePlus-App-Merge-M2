using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Assessments;
using EngineeringWorkspace.Application.Jobs;
using EngineeringWorkspace.Application.Navigation;
using Microsoft.Extensions.Logging;

namespace EngineeringWorkspace.Desktop.Shell;

/// <summary>One job in the activity panel.</summary>
public sealed partial class JobItemViewModel(JobSnapshot snapshot) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsFinished), nameof(StateText), nameof(Steps), nameof(Progress), nameof(Activity), nameof(Summary), nameof(Duration))]
    private JobSnapshot _snapshot = snapshot;

    public Guid Id => Snapshot.Id;
    public string Title => Snapshot.Title;
    public string? Subject => Snapshot.Subject;
    public bool IsRunning => Snapshot.State is JobState.Running or JobState.Queued;
    public bool IsFinished => Snapshot.IsFinished;
    public JobState State => Snapshot.State;
    public string StateText => Snapshot.State.ToString();
    public double Progress => Snapshot.Progress;
    public string? Activity => Snapshot.CurrentActivity;
    public IReadOnlyList<JobStepSnapshot> Steps => Snapshot.Steps;
    public string Summary => Snapshot.Result ?? Snapshot.Error ?? Snapshot.CurrentActivity ?? string.Empty;
    public string Duration => Snapshot.Duration is { } duration ? $"{duration.TotalSeconds:0}s" : string.Empty;
}

/// <summary>A message in the info bar.</summary>
public sealed record TickerMessage(string Text, string Kind, string? ActionLabel = null, Action? Action = null, bool Highlight = false);

/// <summary>
/// The activity panel and the info bar under the page. Jobs (assessments, registrations) appear with their steps and
/// progress; completions are announced with a highlight. Messages stay at least 6 seconds; when nothing else is
/// queued a message stays up to 30 seconds, then the next statistic or suggestion (insight) is shown.
/// </summary>
public sealed partial class ActivityViewModel : ObservableObject
{
    public static readonly TimeSpan MinimumDisplay = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan IdleDisplay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InsightRefresh = TimeSpan.FromMinutes(2);

    private readonly IJobService _jobs;
    private readonly IInsightService _insights;
    private readonly IAssessmentService _assessments;
    private readonly INavigationService _navigation;
    private readonly ILogger<ActivityViewModel> _logger;
    private readonly Queue<TickerMessage> _queue = new();
    private readonly DispatcherTimer _timer;
    private List<Insight> _insightCache = [];
    private int _insightIndex;
    private DateTimeOffset _shownAt = DateTimeOffset.MinValue;
    private DateTimeOffset _insightsLoadedAt = DateTimeOffset.MinValue;

    public ActivityViewModel(IJobService jobs, IInsightService insights, IAssessmentService assessments, INavigationService navigation, ILogger<ActivityViewModel> logger)
    {
        _jobs = jobs;
        _insights = insights;
        _assessments = assessments;
        _navigation = navigation;
        _logger = logger;
        _jobs.JobChanged += (_, snapshot) => ShellViewModel.OnUi(() => OnJobChanged(snapshot));
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
    }

    public ObservableCollection<JobItemViewModel> Jobs { get; } = [];

    [ObservableProperty] private TickerMessage? _currentMessage;
    [ObservableProperty] private bool _isHighlighted;
    [ObservableProperty] private bool _isPanelOpen;
    [ObservableProperty] private int _runningCount;
    [ObservableProperty] private double _overallProgress;
    [ObservableProperty] private string _activitySummary = "No tasks running";

    public void Start()
    {
        _timer.Start();
        Enqueue(new TickerMessage("Ready. Tasks you start appear here and in the activity panel.", "Information"));
    }

    public void Enqueue(TickerMessage message)
    {
        _queue.Enqueue(message);
        if (CurrentMessage is null || DateTimeOffset.Now - _shownAt >= MinimumDisplay) ShowNext();
    }

    [RelayCommand]
    private void TogglePanel() => IsPanelOpen = !IsPanelOpen;

    [RelayCommand]
    private void ClearFinished()
    {
        _jobs.ClearFinished();
        foreach (var job in Jobs.Where(job => job.IsFinished).ToArray()) Jobs.Remove(job);
    }

    [RelayCommand]
    private void Cancel(JobItemViewModel? job)
    {
        if (job is not null) _jobs.Cancel(job.Id);
    }

    [RelayCommand]
    private void RunMessageAction()
    {
        try
        {
            CurrentMessage?.Action?.Invoke();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning(exception, "Info bar action failed");
        }

        ShowNext();
    }

    [RelayCommand]
    private void DismissMessage() => ShowNext();

    private void OnJobChanged(JobSnapshot snapshot)
    {
        var item = Jobs.FirstOrDefault(job => job.Id == snapshot.Id);
        var previous = item?.Snapshot.State;
        if (item is null)
        {
            item = new JobItemViewModel(snapshot);
            Jobs.Insert(0, item);
            Enqueue(new TickerMessage($"Started: {snapshot.Title}{(snapshot.Subject is { } subject ? $" ({subject})" : string.Empty)}.", "Information",
                "Show activity", () => IsPanelOpen = true));
        }
        else
        {
            item.Snapshot = snapshot;
        }

        if (snapshot.IsFinished && previous is not null && previous != snapshot.State)
        {
            var (kind, text) = snapshot.State switch
            {
                JobState.Succeeded => ("Success", $"Completed: {snapshot.Title}. {snapshot.Result}"),
                JobState.Cancelled => ("Warning", $"Cancelled: {snapshot.Title}."),
                _ => ("Error", $"Failed: {snapshot.Title}. {snapshot.Error}")
            };
            Enqueue(new TickerMessage(text.Trim(), kind, "Show details", () => IsPanelOpen = true, Highlight: true));
            _insightsLoadedAt = DateTimeOffset.MinValue;
            // A newly connected tenant runs the assessments that were waiting for it.
            if (snapshot.State == JobState.Succeeded && snapshot.Category == "Tenant registration") _ = _assessments.QueuePendingAsync();
        }

        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var running = Jobs.Where(job => job.IsRunning).ToArray();
        RunningCount = running.Length;
        OverallProgress = running.Length == 0 ? 0 : running.Average(job => job.Progress);
        ActivitySummary = running.Length switch
        {
            0 => "No tasks running",
            1 => $"{running[0].Title}: {running[0].Activity}",
            _ => $"{running.Length} tasks running"
        };
    }

    private void Tick()
    {
        var shown = DateTimeOffset.Now - _shownAt;
        if (IsHighlighted && shown > TimeSpan.FromSeconds(5)) IsHighlighted = false;
        if (_queue.Count > 0 && shown >= MinimumDisplay)
        {
            ShowNext();
            return;
        }

        if (_queue.Count == 0 && shown >= IdleDisplay && RunningCount == 0) _ = ShowInsightAsync();
    }

    private void ShowNext()
    {
        if (_queue.Count == 0)
        {
            _ = ShowInsightAsync();
            return;
        }

        var message = _queue.Dequeue();
        CurrentMessage = message;
        IsHighlighted = message.Highlight;
        _shownAt = DateTimeOffset.Now;
    }

    private async Task ShowInsightAsync()
    {
        _shownAt = DateTimeOffset.Now;
        try
        {
            if (DateTimeOffset.Now - _insightsLoadedAt > InsightRefresh || _insightCache.Count == 0)
            {
                _insightCache = (await Task.Run(() => _insights.GetInsightsAsync())).ToList();
                _insightsLoadedAt = DateTimeOffset.Now;
                _insightIndex = 0;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogDebug(exception, "Insights could not be loaded");
        }

        if (_insightCache.Count == 0 || _queue.Count > 0) return;
        var insight = _insightCache[_insightIndex++ % _insightCache.Count];
        Action? action = insight switch
        {
            { AssessCustomer: { } customer } => () => _assessments.Queue(customer, null, "Suggested in the info bar"),
            { Route: { } route } when insight.ActionLabel == "Run pending" => () => _ = _assessments.QueuePendingAsync(),
            { Route: { } route } => () => _ = _navigation.NavigateAsync(route),
            _ => null
        };
        CurrentMessage = new TickerMessage(insight.Text, insight.Kind.ToString(), action is null ? null : insight.ActionLabel, action);
        IsHighlighted = false;
    }
}
