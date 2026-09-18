using Avalonia.Controls;
using Avalonia.Input;
using DiskPerformanceAnalyzer.ViewModels;
using LiveChartsCore.Drawing;

namespace DiskPerformanceAnalyzer.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        Chart.PointerPressed += OnChartPointerPressed;
        ProcessGrid.SelectionChanged += OnGridSelectionChanged;
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.ClearSelectionRequested -= ClearGridSelection;
                _vm.ProcessesRefreshed -= RestoreGridSelection;
            }

            _vm = DataContext as MainViewModel;
            if (_vm is not null)
            {
                _vm.ClipboardWriter = text => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
                _vm.ClearSelectionRequested += ClearGridSelection;
                _vm.ProcessesRefreshed += RestoreGridSelection;
                BindColumnVisibility(_vm);
            }
        };
    }

    /// <summary>
    /// DataGrid columns are not part of the visual tree, so they cannot bind to the window's
    /// DataContext from XAML; wire the column-manager toggles here instead.
    /// </summary>
    private void BindColumnVisibility(MainViewModel vm)
    {
        // Column order matches MainWindow.axaml: Process, PID, Read, Write, Requests, Share, Top file.
        string[] toggles = [string.Empty, nameof(vm.ShowPid), nameof(vm.ShowRead), nameof(vm.ShowWrite), nameof(vm.ShowRequests), nameof(vm.ShowShare), nameof(vm.ShowTopFile)];
        for (var i = 0; i < ProcessGrid.Columns.Count; i++)
        {
            var column = ProcessGrid.Columns[i];
            if (i > 0 && i < toggles.Length)
            {
                column.Bind(DataGridColumn.IsVisibleProperty, new Avalonia.Data.Binding(toggles[i]) { Source = vm });
            }

            // Header content is not in the window's DataContext chain either: wire sorting and
            // the sort indicator by hand.
            if (column.Header is Button { Tag: string tag } header && Enum.TryParse<ProcessSort>(tag, out var sortColumn))
            {
                _headers[sortColumn] = header;
                header.Click += (_, _) => vm.SortByColumn(sortColumn);
            }
        }

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.SortBy) or nameof(MainViewModel.SortDescending))
            {
                UpdateSortIndicators(vm);
            }
        };
        UpdateSortIndicators(vm);
    }

    private readonly Dictionary<ProcessSort, Button> _headers = new();
    private readonly Dictionary<ProcessSort, string> _headerLabels = new();

    private void UpdateSortIndicators(MainViewModel vm)
    {
        foreach (var (column, button) in _headers)
        {
            if (button.Content is not TextBlock text)
            {
                continue;
            }

            if (!_headerLabels.TryGetValue(column, out var label))
            {
                label = text.Text ?? string.Empty;
                _headerLabels[column] = label;
            }

            var baseLabel = label == "SHARE" ? vm.ShareHeader : label;
            text.Text = vm.SortBy == column ? baseLabel + (vm.SortDescending ? " ▼" : " ▲") : baseLabel;
        }
    }

    private bool _restoringSelection;

    private void ClearGridSelection() => ProcessGrid.SelectedItems.Clear();

    /// <summary>Selected rows narrow the chart to those processes; Escape or "Clear" releases.</summary>
    private void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || _vm.IsRefreshingProcesses || _restoringSelection)
        {
            return;
        }

        _vm.SetSelectedProcesses(ProcessGrid.SelectedItems.OfType<ProcessRowViewModel>());
    }

    /// <summary>Rows are removed and re-inserted when they change rank; put the selection back.</summary>
    private void RestoreGridSelection()
    {
        if (_vm is null || _vm.SelectedPids.Count == 0)
        {
            return;
        }

        _restoringSelection = true;
        try
        {
            var wanted = ProcessGrid.ItemsSource?.OfType<ProcessRowViewModel>().Where(r => _vm.SelectedPids.Contains(r.Pid)).ToList() ?? [];
            var current = ProcessGrid.SelectedItems.OfType<ProcessRowViewModel>().ToList();
            if (wanted.Count == current.Count && !wanted.Except(current).Any())
            {
                return;
            }

            ProcessGrid.SelectedItems.Clear();
            foreach (var row in wanted)
            {
                ProcessGrid.SelectedItems.Add(row);
            }
        }
        finally
        {
            _restoringSelection = false;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ProcessGrid.SelectedItems.Count > 0)
        {
            ClearGridSelection();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void OnChartPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is null || !e.GetCurrentPoint(Chart).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var p = e.GetPosition(Chart);
        var data = Chart.ScalePixelsToData(new LvcPointD(p.X, p.Y));
        if (double.IsNaN(data.X) || data.X <= 0 || data.X > DateTime.MaxValue.Ticks)
        {
            return;
        }

        var clicked = new DateTime((long)data.X, DateTimeKind.Local);
        _vm.FreezeAt(new DateTimeOffset(clicked));
    }
}
