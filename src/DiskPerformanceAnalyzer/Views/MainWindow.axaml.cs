using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
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
        ProcessGrid.LoadingRow += OnLoadingRow;
        ProcessGrid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);
        ProcessGrid.AddHandler(PointerReleasedEvent, OnGridPointerReleased, RoutingStrategies.Bubble);
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
            // the sort indicator by hand. A header is either one button or a panel of chips.
            foreach (var header in HeaderButtons(column.Header))
            {
                if (header.Tag is string tag && Enum.TryParse<ProcessSort>(tag, out var sortColumn))
                {
                    _headers[sortColumn] = header;
                    header.Click += (_, _) => vm.SortByColumn(sortColumn);
                }
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

    private static IEnumerable<Button> HeaderButtons(object? header) => header switch
    {
        Button button => [button],
        Panel panel => panel.Children.OfType<Button>(),
        _ => [],
    };

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

            var active = vm.SortBy == column;
            text.Text = active ? label + (vm.SortDescending ? " ▼" : " ▲") : label;
            button.Classes.Set("active", active);
        }
    }

    /// <summary>The breakdown chevron lives in a cell; find its row and toggle the details there.</summary>
    private void OnExpandClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ProcessRowViewModel row)
        {
            return;
        }

        row.IsExpanded = !row.IsExpanded;
        if (button.FindAncestorOfType<DataGridRow>() is { } gridRow)
        {
            gridRow.AreDetailsVisible = row.IsExpanded;
        }

        e.Handled = true;
    }

    /// <summary>Rows are recycled and re-inserted on reorder; keep the details state with the view model.</summary>
    private void OnLoadingRow(object? sender, DataGridRowEventArgs e) => SyncRowDetails(e.Row);

    private static void SyncRowDetails(DataGridRow gridRow)
    {
        var expanded = gridRow.DataContext is ProcessRowViewModel row && row.IsExpanded;
        if (gridRow.AreDetailsVisible != expanded)
        {
            gridRow.AreDetailsVisible = expanded;
        }
    }

    /// <summary>
    /// A selection change makes the DataGrid recompute every row's details visibility from its
    /// (collapsed) mode, dropping the per-row state; put the view model's state back.
    /// </summary>
    private void SyncAllRowDetails()
    {
        foreach (var gridRow in ProcessGrid.GetVisualDescendants().OfType<DataGridRow>())
        {
            SyncRowDetails(gridRow);
        }
    }

    private bool _restoringSelection;
    private bool _cellButtonPressed;
    private ProcessRowViewModel? _pressedSelectedRow;

    /// <summary>
    /// A plain click on the single selected row releases the selection again, so the full chart
    /// is one click away — the DataGrid itself would only keep the row selected.
    /// </summary>
    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressedSelectedRow = null;
        var point = e.GetCurrentPoint(ProcessGrid);
        if (!point.Properties.IsLeftButtonPressed || e.KeyModifiers != KeyModifiers.None || e.Source is not Visual source)
        {
            return;
        }

        if (source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return; // icon buttons have their own handling
        }

        var row = source.FindAncestorOfType<DataGridRow>(includeSelf: true);
        if (row?.DataContext is ProcessRowViewModel vm && ProcessGrid.SelectedItems.Count == 1 && ProcessGrid.SelectedItems.Contains(vm))
        {
            _pressedSelectedRow = vm;
        }
    }

    private void OnGridPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var pressed = _pressedSelectedRow;
        _pressedSelectedRow = null;
        if (pressed is null || e.Source is not Visual source)
        {
            return;
        }

        var row = source.FindAncestorOfType<DataGridRow>(includeSelf: true);
        if (row?.DataContext == pressed)
        {
            ClearGridSelection();
        }
    }

    private void ClearGridSelection()
    {
        ProcessGrid.SelectedItems.Clear();
        SyncAllRowDetails();
    }

    /// <summary>
    /// The DataGrid cell selects its row on every pointer press, even one a button already
    /// handled. Pressing an icon (expand, open folder, copy path) must not change the filter,
    /// so the selection change it triggers is undone right away.
    /// </summary>
    private void OnCellButtonPressed(object? sender, PointerPressedEventArgs e)
    {
        _cellButtonPressed = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _cellButtonPressed = false, Avalonia.Threading.DispatcherPriority.Input);
    }

    /// <summary>Selected rows narrow the chart to those processes; Escape or "Clear" releases.</summary>
    private void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || _vm.IsRefreshingProcesses || _restoringSelection)
        {
            return;
        }

        if (_cellButtonPressed)
        {
            // Undo after the click has been delivered: clearing the selection synchronously
            // would steal the pointer capture from the button and swallow its Click.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => RestoreGridSelection(force: true), Avalonia.Threading.DispatcherPriority.Background);
            return;
        }

        _vm.SetSelectedProcesses(ProcessGrid.SelectedItems.OfType<ProcessRowViewModel>());
        SyncAllRowDetails();
    }

    private void RestoreGridSelection() => RestoreGridSelection(force: false);

    /// <summary>Rows are removed and re-inserted when they change rank; put the selection back.</summary>
    /// <param name="force">Also clear a selection when the view model has none (undo a stray click).</param>
    private void RestoreGridSelection(bool force)
    {
        if (_vm is null || (_vm.SelectedPids.Count == 0 && !force))
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
            SyncAllRowDetails();
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
