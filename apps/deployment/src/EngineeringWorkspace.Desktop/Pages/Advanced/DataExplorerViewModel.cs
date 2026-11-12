using System.Collections.ObjectModel;
using System.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EngineeringWorkspace.Application.Database;
using EngineeringWorkspace.Application.Navigation;
using EngineeringWorkspace.Desktop.Navigation;

namespace EngineeringWorkspace.Desktop.Pages.Advanced;

/// <summary>Advanced > Data Explorer: every table of the application database with its row count, and its rows a page at a time (read-only).</summary>
public sealed partial class DataExplorerViewModel(IDatabaseHealthService database) : PageViewModel
{
    private const int PageSize = 200;
    private List<TableCoverage> _all = [];

    public ObservableCollection<TableCoverage> Tables { get; } = [];
    public ObservableCollection<string> Schemas { get; } = [];

    [ObservableProperty] private string _tableSearch = string.Empty;
    [ObservableProperty] private string? _selectedSchema;
    [ObservableProperty] private bool _onlyFilled;
    [ObservableProperty] private TableCoverage? _selectedTable;
    [ObservableProperty] private DataView? _rows;
    [ObservableProperty] private string _rowFilter = string.Empty;
    [ObservableProperty] private string _pageText = string.Empty;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private int _offset;
    [ObservableProperty] private long _totalRows;

    public override async Task OnNavigatedToAsync(RouteDescriptor route, CancellationToken cancellationToken)
    {
        await base.OnNavigatedToAsync(route, cancellationToken);
        await RefreshAsync();
    }

    partial void OnTableSearchChanged(string value) => ApplyFilter();
    partial void OnSelectedSchemaChanged(string? value) => ApplyFilter();
    partial void OnOnlyFilledChanged(bool value) => ApplyFilter();

    partial void OnSelectedTableChanged(TableCoverage? value)
    {
        Offset = 0;
        _ = LoadPageAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            _all = (await database.CountTablesAsync()).OrderBy(table => table.Schema).ThenBy(table => table.Table).ToList();
            Schemas.Clear();
            Schemas.Add("All schemas");
            foreach (var schema in _all.Select(table => table.Schema).Distinct(StringComparer.OrdinalIgnoreCase)) Schemas.Add(schema);
            SelectedSchema ??= "All schemas";
            Summary = $"{_all.Count} tables, {_all.Count(table => table.Rows > 0)} with rows, {_all.Sum(table => table.Rows ?? 0):N0} rows in all.";
            ApplyFilter();
            StatusMessage = null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
        {
            StatusMessage = $"The database could not be read: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task SearchRowsAsync()
    {
        Offset = 0;
        return LoadPageAsync();
    }

    [RelayCommand]
    private Task NextPageAsync()
    {
        if (Offset + PageSize >= TotalRows) return Task.CompletedTask;
        Offset += PageSize;
        return LoadPageAsync();
    }

    [RelayCommand]
    private Task PreviousPageAsync()
    {
        if (Offset == 0) return Task.CompletedTask;
        Offset = Math.Max(0, Offset - PageSize);
        return LoadPageAsync();
    }

    private void ApplyFilter()
    {
        var search = TableSearch.Trim();
        Tables.Clear();
        foreach (var table in _all.Where(table =>
                     (SelectedSchema is null or "All schemas" || string.Equals(table.Schema, SelectedSchema, StringComparison.OrdinalIgnoreCase)) &&
                     (!OnlyFilled || table.Rows > 0) &&
                     (search.Length == 0 || table.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || table.Purpose.Contains(search, StringComparison.OrdinalIgnoreCase))))
            Tables.Add(table);
    }

    private async Task LoadPageAsync()
    {
        if (SelectedTable is not { } table)
        {
            Rows = null;
            PageText = string.Empty;
            return;
        }

        IsBusy = true;
        try
        {
            var page = await database.ReadTableAsync(table.Schema, table.Table, Offset, PageSize, RowFilter);
            var data = new DataTable(table.Name);
            foreach (var column in page.Columns) data.Columns.Add(column.Replace(".", "_", StringComparison.Ordinal), typeof(string));
            foreach (var row in page.Rows) data.Rows.Add([.. row.Cast<object?>().Select(value => value ?? DBNull.Value)]);
            Rows = data.DefaultView;
            TotalRows = page.TotalRows;
            PageText = page.TotalRows == 0 ? "No rows." : $"Rows {Offset + 1:N0}–{Offset + page.Rows.Count:N0} of {page.TotalRows:N0}";
            StatusMessage = null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Data.Common.DbException)
        {
            StatusMessage = $"{table.Name} could not be read: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
