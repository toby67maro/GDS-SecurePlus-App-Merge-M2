using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Catalog;
using EngineeringWorkspace.Application.Customers;
using EngineeringWorkspace.Application.Export;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Application.Reporting;
using EngineeringWorkspace.Desktop.Controls;
using EngineeringWorkspace.Desktop.Navigation;
using EngineeringWorkspace.Desktop.Pages.Reporting;
using EngineeringWorkspace.Desktop.Services;
using EngineeringWorkspace.Domain.Customers;

namespace EngineeringWorkspace.Desktop.Pages.Insights;

/// <summary>A filter on one dimension, added or removed by the user.</summary>
public sealed record DimensionFilter(string Name, MultiSelectFilter Filter);

/// <summary>One heat-map cell.</summary>
public sealed record HeatCellView(string Row, string Column, string Text, Brush Fill, Brush Foreground, string ToolTip, HeatMapCell? Cell);

public sealed record HeatRowView(string Label, IReadOnlyList<HeatCellView> Cells);

public sealed record LegendEntry(string Label, Brush Fill);

/// <summary>
/// Dashboard → Heat Map: compliance of the selected customer by any two dimensions (rows × columns), coloured by
/// compliance % or by the number of failing controls, with filters on any dimension (added and removed by the user), an
/// assessment date range, a legend, tooltips with the numbers and drill-down to the controls behind a cell.
/// </summary>
public sealed partial class HeatMapViewModel(ICustomerContextService context, ICustomerProfileStore store, IReportingService reporting, ICatalogService catalog,
    ExportCoordinator export, EngineeringWorkspace.Application.Planning.IPlanningRepository planning) : CustomerScopedPageViewModel(context, store)
{
    public const string MetricCompliance = "Compliance %";
    public const string MetricFailing = "Failing Controls";

    private CustomerResults? _results;
    private HeatMap? _map;

    public IReadOnlyList<string> Dimensions { get; } = ReportDimensions.Names;
    public IReadOnlyList<string> Metrics { get; } = [MetricCompliance, MetricFailing];
    public IReadOnlyList<Choice<ReportScope>> Scopes { get; } =
        [new(ReportScope.ActiveBaselines, "Active Baselines"), new(ReportScope.AssignedSpecification, "Assigned Specification"), new(ReportScope.AllEvaluated, "All Evaluated Controls")];

    public ObservableCollection<DimensionFilter> ActiveFilters { get; } = [];
    public ObservableCollection<string> AvailableFilters { get; } = [];
    public ObservableCollection<string> ColumnHeaders { get; } = [];
    public ObservableCollection<HeatRowView> Rows { get; } = [];
    public ObservableCollection<LegendEntry> Legend { get; } = [];
    public ObservableCollection<FactRow> DrillDown { get; } = [];

    [ObservableProperty] private string _rowDimension = ReportDimensions.Framework;
    [ObservableProperty] private string _columnDimension = ReportDimensions.SolutionArea;
    [ObservableProperty] private string _metric = MetricCompliance;
    [ObservableProperty] private Choice<ReportScope>? _scope;
    [ObservableProperty] private string? _filterToAdd;
    [ObservableProperty] private DateTime? _fromDate;
    [ObservableProperty] private DateTime? _toDate;
    [ObservableProperty] private string? _scopeText;
    [ObservableProperty] private string _drillDownCaption = "Select a cell to see the controls behind it.";
    [ObservableProperty] private bool _hasMap;

    partial void OnRowDimensionChanged(string value) => Render();
    partial void OnColumnDimensionChanged(string value) => Render();
    partial void OnMetricChanged(string value) => Render();
    partial void OnScopeChanged(Choice<ReportScope>? value) => _ = LoadAsync();
    partial void OnFromDateChanged(DateTime? value) => _ = LoadAsync();
    partial void OnToDateChanged(DateTime? value) => _ = LoadAsync();

    public override async Task OnNavigatedToAsync(RouteDescriptor route, CancellationToken cancellationToken)
    {
        Scope = Scopes[0];
        SelectedKind ??= MapKinds[0];
        foreach (var name in new[] { ReportDimensions.Framework, ReportDimensions.Severity, ReportDimensions.Status }) AddFilterCore(name);
        RefreshAvailable();
        await base.OnNavigatedToAsync(route, cancellationToken);
    }

    protected override async Task OnCustomerChangedAsync(CustomerProfile? customer)
    {
        await LoadAsync();
        await LoadOtherMapAsync();
    }

    [RelayCommand]
    private void AddFilter()
    {
        if (FilterToAdd is not { } name) return;
        AddFilterCore(name);
        RefreshAvailable();
        UpdateFilterOptions();
        Render();
    }

    [RelayCommand]
    private void RemoveFilter(DimensionFilter? filter)
    {
        if (filter is null) return;
        ActiveFilters.Remove(filter);
        RefreshAvailable();
        Render();
    }

    [RelayCommand]
    private void SelectCell(HeatCellView? cell)
    {
        DrillDown.Clear();
        if (cell?.Cell is not { } data)
        {
            DrillDownCaption = "This cell has no controls.";
            return;
        }

        var current = catalog.Catalog;
        foreach (var fact in data.Facts.OrderBy(fact => fact.Credit).ThenBy(fact => ReportDimensions.SeverityRank(fact.Control.Severity)))
            DrillDown.Add(new FactRow(fact.ControlId, fact.Control.Name, fact.Status, fact.Control.Severity, fact.Control.KindDisplayName, current.SolutionAreaName(fact.Control.SolutionArea),
                current.ExperienceAreaName(fact.Control.ExperienceArea), fact.Control.Implementation.Effort, fact.Evidence));
        DrillDownCaption = $"{cell.Row} × {cell.Column}: {data.Completion.Controls} control(s), {data.Completion.PercentText} complete, {data.Completion.Gaps} gap(s).";
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadAsync();
        await LoadOtherMapAsync();
    }

    private void AddFilterCore(string name)
    {
        if (ActiveFilters.Any(filter => filter.Name == name)) return;
        var filter = new MultiSelectFilter(name);
        filter.Changed += (_, _) => Render();
        ActiveFilters.Add(new DimensionFilter(name, filter));
    }

    private void RefreshAvailable()
    {
        AvailableFilters.Clear();
        foreach (var name in Dimensions.Where(name => ActiveFilters.All(filter => filter.Name != name))) AvailableFilters.Add(name);
        FilterToAdd = AvailableFilters.FirstOrDefault();
    }

    private void UpdateFilterOptions()
    {
        if (_results is null) return;
        foreach (var active in ActiveFilters)
        {
            var dimension = ReportDimensions.Get(active.Name, catalog.Catalog);
            active.Filter.SetOptions(_results.Facts.SelectMany(dimension.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)
                .Select(key => (key, key)));
        }
    }

    private async Task LoadAsync()
    {
        _results = null;
        Render();
        if (SelectedCustomer is not { } customer)
        {
            StatusMessage = "Choose a customer.";
            return;
        }

        if (!reporting.IsAvailable)
        {
            StatusMessage = "The application database is not ready, so stored assessment results are unavailable.";
            return;
        }

        IsBusy = true;
        try
        {
            _results = await reporting.GetCustomerResultsAsync(customer.Id.Value, Scope?.Value ?? ReportScope.ActiveBaselines,
                FromDate is { } from ? new DateTimeOffset(from.Date, TimeZoneInfo.Local.GetUtcOffset(from.Date)) : null,
                ToDate is { } to ? new DateTimeOffset(to.Date.AddDays(1), TimeZoneInfo.Local.GetUtcOffset(to.Date)) : null);
            StatusMessage = _results.HasResults ? null : $"{customer.Name} has no stored assessment with results{(FromDate is not null || ToDate is not null ? " in this date range" : string.Empty)}.";
            ScopeText = _results.HasResults
                ? $"{_results.ScopeDescription}. Assessment '{_results.Latest!.Name}' of {(_results.Latest.CompletedAtUtc ?? _results.Latest.RequestedAtUtc).ToLocalTime():yyyy-MM-dd HH:mm}."
                : _results.ScopeDescription;
            UpdateFilterOptions();
            Render();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private IReadOnlyList<ResultFact> FilteredFacts()
    {
        if (_results is null) return [];
        var current = catalog.Catalog;
        var filters = ActiveFilters.Where(filter => filter.Filter.IsActive).Select(filter => (Dimension: ReportDimensions.Get(filter.Name, current), filter.Filter)).ToArray();
        return _results.Facts.Where(fact => filters.All(pair => pair.Filter.MatchesAny(pair.Dimension.Keys(fact)))).ToArray();
    }

    private void Render()
    {
        Rows.Clear();
        ColumnHeaders.Clear();
        DrillDown.Clear();
        Legend.Clear();
        HasMap = false;
        if (_results is not { HasResults: true }) return;
        var current = catalog.Catalog;
        _map = HeatMap.Build(FilteredFacts(), ReportDimensions.Get(RowDimension, current), ReportDimensions.Get(ColumnDimension, current));
        foreach (var column in _map.Columns) ColumnHeaders.Add(column);
        var palette = Palette();
        foreach (var row in _map.Rows)
        {
            var cells = _map.Columns.Select(column =>
            {
                var cell = _map.Cell(row, column);
                if (cell is null) return new HeatCellView(row, column, string.Empty, palette.Empty, palette.Text, $"{row} × {column}: no controls", null);
                var completion = cell.Completion;
                var failing = completion.NotSatisfied;
                var (text, fill) = Metric == MetricFailing
                    ? (failing.ToString(CultureInfo.CurrentCulture), failing == 0 ? palette.High : failing <= 4 ? palette.Middle : palette.Low)
                    : (completion.PercentText, completion.Percent is { } percent ? Blend(palette, percent) : palette.Empty);
                var tip = $"{row} × {column}\n{completion.Controls} control(s): {completion.Satisfied} satisfied, {completion.Partial} partial, {completion.NotSatisfied} not satisfied, " +
                          $"{completion.Excepted} exception(s), {completion.NotEvaluated} not evaluated\nCompletion {completion.PercentText}";
                return new HeatCellView(row, column, text, fill, palette.OnFill, tip, cell);
            }).ToArray();
            Rows.Add(new HeatRowView(row, cells));
        }

        if (Metric == MetricFailing)
        {
            Legend.Add(new LegendEntry("0 failing", palette.High));
            Legend.Add(new LegendEntry("1–4 failing", palette.Middle));
            Legend.Add(new LegendEntry("5 or more", palette.Low));
        }
        else
        {
            foreach (var (label, value) in new[] { ("0%", 0.0), ("50%", 50.0), ("80%", 80.0), ("100%", 100.0) }) Legend.Add(new LegendEntry(label, Blend(palette, value)));
        }

        Legend.Add(new LegendEntry("No controls", palette.Empty));
        HasMap = _map.Rows.Count > 0;
        if (!HasMap) StatusMessage = "No controls match the filters.";
    }

    private sealed record HeatPalette(Color LowColor, Color MiddleColor, Color HighColor, Brush Low, Brush Middle, Brush High, Brush Empty, Brush Text, Brush OnFill);

    private static HeatPalette Palette()
    {
        static Color ColorOf(string key, Color fallback) => System.Windows.Application.Current?.TryFindResource(key) is Color color ? color : fallback;
        static Brush BrushOf(string key, Brush fallback) => System.Windows.Application.Current?.TryFindResource(key) as Brush ?? fallback;
        var low = ColorOf("AppColor.Error", Color.FromRgb(0xC4, 0x2B, 0x1C));
        var middle = ColorOf("AppColor.Warning", Color.FromRgb(0x9D, 0x5D, 0x00));
        var high = ColorOf("AppColor.Success", Color.FromRgb(0x10, 0x7C, 0x10));
        return new HeatPalette(low, middle, high, Frozen(low), Frozen(middle), Frozen(high), BrushOf("AppBrush.SurfaceSecondary", Brushes.WhiteSmoke),
            BrushOf("AppBrush.TextSecondary", Brushes.Gray), Brushes.White);
    }

    private static Brush Blend(HeatPalette palette, double percent)
    {
        // Red below 50 %, through amber at 50-80 %, to green at 100 %.
        var (from, to, t) = percent < 50
            ? (palette.LowColor, palette.MiddleColor, percent / 50)
            : (palette.MiddleColor, palette.HighColor, Math.Min(1, (percent - 50) / 50));
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * t);
        return Frozen(Color.FromRgb(Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B)));
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    [RelayCommand]
    private async Task ExportAsync(object? format)
    {
        if (_map is null || _results is not { HasResults: true } results)
        {
            StatusMessage = "There is nothing to export.";
            return;
        }

        var columns = new[] { $"{RowDimension} \\ {ColumnDimension}" }.Concat(_map.Columns).ToArray();
        var table = _map.Rows.Select(row => (IReadOnlyList<string?>)new[] { row }.Concat(_map.Columns.Select(column =>
        {
            var cell = _map.Cell(row, column);
            return cell is null ? string.Empty : Metric == MetricFailing ? cell.Completion.NotSatisfied.ToString(CultureInfo.InvariantCulture) : cell.Completion.PercentText;
        })).ToArray()).ToArray();
        var document = new ExportDocument("Compliance Heat Map", results.CustomerName, DateTimeOffset.UtcNow,
            [new("Rows", RowDimension), new("Columns", ColumnDimension), new("Metric", Metric), new("Scope", ScopeText ?? string.Empty),
             new("Filters", string.Join("; ", ActiveFilters.Where(filter => filter.Filter.IsActive).Select(filter => $"{filter.Name}: {filter.Filter.Summary}")))],
            [new ExportTable($"{Metric} by {RowDimension} and {ColumnDimension}", columns, table)]);
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
