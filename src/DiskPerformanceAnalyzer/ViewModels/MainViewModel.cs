using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskPerformanceAnalyzer.Monitoring;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using SkiaSharp;

namespace DiskPerformanceAnalyzer.ViewModels;

/// <summary>
/// Owns the monitor and the ring buffer; projects the selected disk onto the chart and the
/// process table. Chart and table are two views of the same per-process ETW buckets, so the
/// filter (text + selected rows) applies to both. All mutations of bound collections happen
/// through <c>_marshal</c> (UI thread).
/// </summary>
public partial class MainViewModel : ObservableObject, IProcessRowHost, IDisposable
{
    public const int ChartSeconds = 60;
    public const int MaxProcessRows = 60;
    public static readonly int[] WindowChoices = [1, 5, 60, 300];

    // One hue per metric family, read always the lighter shade: throughput in green, requests in blue.
    private static readonly SKColor ReadBytesColor = new(0x8E, 0xE3, 0xB4);
    private static readonly SKColor WriteBytesColor = new(0x2E, 0x8F, 0x5E);
    private static readonly SKColor ReadOpsColor = new(0x9C, 0xC8, 0xFF);
    private static readonly SKColor WriteOpsColor = new(0x2F, 0x6F, 0xC7);
    private static readonly SKColor Blue = new(0x3B, 0x8E, 0xEA);

    private readonly IDiskMonitor _monitor;
    private readonly SnapshotRingBuffer _buffer = new();
    private readonly Action<Action> _marshal;
    private readonly HashSet<int> _selectedPids = new();
    private bool _started;
    private bool _disposed;
    private long _selfOpsSinceStart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedDiskTitle))]
    private DiskViewModel? _selectedDisk;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowLabel), nameof(EmptyStateText), nameof(IsWindow5), nameof(IsWindow60), nameof(IsWindow300))]
    private int _windowSeconds = 60;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowLabel), nameof(EmptyStateText), nameof(IsWindow5), nameof(IsWindow60), nameof(IsWindow300))]
    private bool _isCumulative;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFrozen), nameof(FrozenText), nameof(WindowLabel), nameof(EmptyStateText))]
    private DateTimeOffset? _frozenAt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseButtonText))]
    private bool _isPaused;

    [ObservableProperty]
    private bool _hasProcesses;

    [ObservableProperty]
    private string _statusText = "Waiting for the first sample...";

    [ObservableProperty]
    private string _selfLoadText = "This app: idle";

    /// <summary>Substring filter on process name or attributed file path; applies to chart and table.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterDescription), nameof(HasFilter))]
    private string _filterText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterDescription), nameof(HasFilter))]
    private int _selectedProcessCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortIndicatorOps), nameof(SortIndicatorBytes), nameof(SortIndicatorName), nameof(SortIndicatorPid),
        nameof(SortIndicatorRead), nameof(SortIndicatorWrite), nameof(SortIndicatorShare), nameof(SortIndicatorTopFile), nameof(ShareHeader))]
    private ProcessSort _sortBy = ProcessSort.Ops;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortIndicatorOps), nameof(SortIndicatorBytes), nameof(SortIndicatorName), nameof(SortIndicatorPid),
        nameof(SortIndicatorRead), nameof(SortIndicatorWrite), nameof(SortIndicatorShare), nameof(SortIndicatorTopFile))]
    private bool _sortDescending = true;

    // Column visibility (column manager). Numbers are behind the share bar by default; PID/read/write/requests are opt-in.
    [ObservableProperty] private bool _showPid;
    [ObservableProperty] private bool _showRead;
    [ObservableProperty] private bool _showWrite;
    [ObservableProperty] private bool _showRequests;
    [ObservableProperty] private bool _showShare = true;
    [ObservableProperty] private bool _showTopFile = true;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public MainViewModel()
        : this(Design.IsDesignMode ? new NullDiskMonitor() : new DiskMonitor(), null)
    {
        if (Design.IsDesignMode)
        {
            SeedDesignData();
        }
    }

    /// <param name="monitor">Snapshot source; disposed with this view model.</param>
    /// <param name="marshal">Runs an action on the UI thread. Defaults to the Avalonia dispatcher; tests pass <c>a => a()</c>.</param>
    public MainViewModel(IDiskMonitor monitor, Action<Action>? marshal)
    {
        _monitor = monitor;
        _marshal = marshal ?? DispatchToUi;
        _monitor.SnapshotReady += OnSnapshotReady;

        Series =
        [
            Area("Read", ReadValues, ReadBytesColor, p => "Read " + Formatting.Rate(p.Model?.Value ?? 0)),
            Area("Write", WriteValues, WriteBytesColor, p => "Write " + Formatting.Rate(p.Model?.Value ?? 0)),
            Line("Read req/s", ReadOpsValues, ReadOpsColor, p => "Read " + Formatting.Iops(p.Model?.Value ?? 0)),
            Line("Write req/s", WriteOpsValues, WriteOpsColor, p => "Write " + Formatting.Iops(p.Model?.Value ?? 0)),
            new LineSeries<DateTimePoint>
            {
                Name = "Frozen",
                Values = FrozenMarker,
                ScalesYAt = 1,
                Fill = null,
                Stroke = new SolidColorPaint(SKColors.White.WithAlpha(0xCC), 1.5f) { PathEffect = new DashEffect([6, 4]) },
                GeometryFill = null,
                GeometryStroke = null,
                GeometrySize = 0,
                LineSmoothness = 0,
                AnimationsSpeed = TimeSpan.Zero,
                IsHoverable = false,
                IsVisibleAtLegend = false,
            },
        ];

        XAxes =
        [
            new DateTimeAxis(TimeSpan.FromSeconds(10), d => d.ToString("HH:mm:ss"))
            {
                TextSize = 11,
                LabelsPaint = new SolidColorPaint(SKColors.Gray),
                SeparatorsPaint = new SolidColorPaint(SKColors.Gray.WithAlpha(0x30)),
            },
        ];

        YAxes =
        [
            new Axis
            {
                Name = "Throughput",
                MinLimit = 0,
                Labeler = v => Formatting.Rate(v),
                TextSize = 11,
                LabelsPaint = new SolidColorPaint(SKColors.Gray),
                SeparatorsPaint = new SolidColorPaint(SKColors.Gray.WithAlpha(0x30)),
            },
            new Axis
            {
                Name = "Requests/s",
                Position = AxisPosition.End,
                MinLimit = 0,
                Labeler = v => Formatting.Count((long)v),
                TextSize = 11,
                LabelsPaint = new SolidColorPaint(Blue),
                ShowSeparatorLines = false,
            },
        ];
    }

    private static LineSeries<DateTimePoint> Area(string name, ObservableCollection<DateTimePoint> values, SKColor color, Func<LiveChartsCore.Kernel.ChartPoint<DateTimePoint, LiveChartsCore.SkiaSharpView.Drawing.Geometries.CircleGeometry, LiveChartsCore.SkiaSharpView.Drawing.Geometries.LabelGeometry>, string> tooltip) =>
        new()
        {
            Name = name,
            Values = values,
            ScalesYAt = 0,
            Fill = new SolidColorPaint(color.WithAlpha(0x66)),
            Stroke = new SolidColorPaint(color, 1.5f),
            GeometryFill = null,
            GeometryStroke = null,
            GeometrySize = 0,
            LineSmoothness = 0,
            AnimationsSpeed = TimeSpan.Zero,
            YToolTipLabelFormatter = tooltip,
        };

    private static LineSeries<DateTimePoint> Line(string name, ObservableCollection<DateTimePoint> values, SKColor color, Func<LiveChartsCore.Kernel.ChartPoint<DateTimePoint, LiveChartsCore.SkiaSharpView.Drawing.Geometries.CircleGeometry, LiveChartsCore.SkiaSharpView.Drawing.Geometries.LabelGeometry>, string> tooltip) =>
        new()
        {
            Name = name,
            Values = values,
            ScalesYAt = 1,
            Fill = null,
            Stroke = new SolidColorPaint(color, 2f),
            GeometryFill = null,
            GeometryStroke = null,
            GeometrySize = 0,
            LineSmoothness = 0,
            AnimationsSpeed = TimeSpan.Zero,
            YToolTipLabelFormatter = tooltip,
        };

    public ObservableCollection<DiskViewModel> Disks { get; } = new();
    public ObservableCollection<ProcessRowViewModel> Processes { get; } = new();

    public ObservableCollection<DateTimePoint> ReadValues { get; } = new();
    public ObservableCollection<DateTimePoint> WriteValues { get; } = new();
    public ObservableCollection<DateTimePoint> ReadOpsValues { get; } = new();
    public ObservableCollection<DateTimePoint> WriteOpsValues { get; } = new();
    public ObservableCollection<DateTimePoint> FrozenMarker { get; } = new();

    public ISeries[] Series { get; }
    public Axis[] XAxes { get; }
    public Axis[] YAxes { get; }

    /// <summary>Set by the view; writes text to the system clipboard.</summary>
    public Func<string, Task>? ClipboardWriter { get; set; }

    public SnapshotRingBuffer Buffer => _buffer;
    public IReadOnlySet<int> SelectedPids => _selectedPids;

    public bool HasSelection => SelectedDisk is not null;
    public string SelectedDiskTitle => SelectedDisk?.Name ?? "No disk selected";
    public bool IsFrozen => FrozenAt is not null;
    public string FrozenText => FrozenAt is { } t ? $"Frozen at {t.LocalDateTime:HH:mm:ss}" : string.Empty;
    public string PauseButtonText => IsPaused ? "Resume" : "Pause";

    public bool IsWindow5 => !IsCumulative && WindowSeconds == 5;
    public bool IsWindow60 => !IsCumulative && WindowSeconds == 60;
    public bool IsWindow300 => !IsCumulative && WindowSeconds == 300;
    public string ShareHeader => SortBy == ProcessSort.Bytes ? "SHARE (BYTES)" : "SHARE (REQ)";

    public bool HasFilter => !string.IsNullOrWhiteSpace(FilterText) || SelectedProcessCount > 0;

    /// <summary>What chart and table currently show — "all processes" or the active filter.</summary>
    public string FilterDescription
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(FilterText))
            {
                parts.Add($"matching \"{FilterText.Trim()}\"");
            }

            if (SelectedProcessCount > 0)
            {
                parts.Add($"{SelectedProcessCount} selected process{(SelectedProcessCount == 1 ? string.Empty : "es")}");
            }

            return parts.Count == 0 ? "all processes" : string.Join(", ", parts);
        }
    }

    public string SortIndicatorOps => Indicator(ProcessSort.Ops);
    public string SortIndicatorBytes => Indicator(ProcessSort.Bytes);
    public string SortIndicatorName => Indicator(ProcessSort.Name);
    public string SortIndicatorPid => Indicator(ProcessSort.Pid);
    public string SortIndicatorRead => Indicator(ProcessSort.Read);
    public string SortIndicatorWrite => Indicator(ProcessSort.Write);
    public string SortIndicatorShare => Indicator(ProcessSort.Share);
    public string SortIndicatorTopFile => Indicator(ProcessSort.TopFile);

    private string Indicator(ProcessSort column) => SortBy == column ? (SortDescending ? " \u25BC" : " \u25B2") : string.Empty;

    private string WindowText => WindowSeconds >= 60 ? $"{WindowSeconds / 60} min" : $"{WindowSeconds} s";

    public string WindowLabel => IsCumulative
        ? "since start"
        : FrozenAt is { } t
            ? $"{WindowText} ending {t.LocalDateTime:HH:mm:ss}"
            : $"last {WindowText}";

    public string EmptyStateText => HasFilter
        ? "No process matches the filter"
        : IsCumulative
            ? "No disk activity since start"
            : $"No disk activity in the last {WindowText}";

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _monitor.Start();
    }

    /// <summary>Pins the table to the second covering <paramref name="timestamp"/> (chart click).</summary>
    public void FreezeAt(DateTimeOffset timestamp)
    {
        var snapshot = _buffer.At(timestamp) ?? Nearest(timestamp);
        if (snapshot is not null)
        {
            FrozenAt = snapshot.Timestamp;
        }
    }

    /// <summary>Called by the view when the table selection changes; selected rows narrow the chart.</summary>
    public void SetSelectedProcesses(IEnumerable<ProcessRowViewModel> rows)
    {
        var next = rows.Select(r => r.Pid).ToHashSet();
        if (next.SetEquals(_selectedPids))
        {
            return;
        }

        _selectedPids.Clear();
        _selectedPids.UnionWith(next);
        SelectedProcessCount = _selectedPids.Count;
        RebuildChart();
    }

    /// <summary>Header click: same column toggles direction, another column sorts descending.</summary>
    public void SortByColumn(ProcessSort column)
    {
        if (SortBy == column)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortBy = column;
            SortDescending = column is not (ProcessSort.Name or ProcessSort.TopFile);
        }
    }

    [RelayCommand]
    private void GoLive() => FrozenAt = null;

    [RelayCommand]
    private void ClearFilter()
    {
        FilterText = string.Empty;
        ClearSelectionRequested?.Invoke();
    }

    /// <summary>Raised when the view should clear the table selection (the view owns the DataGrid).</summary>
    public event Action? ClearSelectionRequested;

    /// <summary>Raised after every table refresh so the view can restore the row selection.</summary>
    public event Action? ProcessesRefreshed;

    /// <summary>True while <see cref="Processes"/> is being reordered; selection changes during this are not user intent.</summary>
    public bool IsRefreshingProcesses { get; private set; }

    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
        if (!IsPaused)
        {
            RebuildChart();
            RefreshProcesses();
        }
    }

    [RelayCommand]
    private void SelectWindow(string mode)
    {
        if (mode == "all")
        {
            IsCumulative = true;
            return;
        }

        IsCumulative = false;
        WindowSeconds = int.TryParse(mode, out var s) && WindowChoices.Contains(s) ? s : 60;
    }

    [RelayCommand]
    private void Sort(string column) => SortByColumn(Enum.Parse<ProcessSort>(column, ignoreCase: true));

    public Task CopyTextAsync(string text) => ClipboardWriter?.Invoke(text) ?? Task.CompletedTask;

    partial void OnSelectedDiskChanged(DiskViewModel? value)
    {
        RebuildChart();
        RefreshProcesses();
    }

    partial void OnWindowSecondsChanged(int value) => RefreshProcesses();

    partial void OnIsCumulativeChanged(bool value) => RefreshProcesses();

    partial void OnSortByChanged(ProcessSort value) => RefreshProcesses();

    partial void OnSortDescendingChanged(bool value) => RefreshProcesses();

    partial void OnFilterTextChanged(string value)
    {
        RebuildChart();
        RefreshProcesses();
    }

    partial void OnFrozenAtChanged(DateTimeOffset? value)
    {
        UpdateFrozenMarker();
        RefreshProcesses();
    }

    private void OnSnapshotReady(DiskSnapshot snapshot)
    {
        _buffer.Add(snapshot);
        _marshal(() => Apply(snapshot));
    }

    private void Apply(DiskSnapshot snapshot)
    {
        if (_disposed)
        {
            return;
        }

        foreach (var sample in snapshot.Disks)
        {
            var disk = Disks.FirstOrDefault(d => d.DiskNumber == sample.DiskNumber);
            if (disk is null)
            {
                disk = new DiskViewModel(sample.DiskNumber, sample.DriveLetters);
                var index = 0;
                while (index < Disks.Count && Disks[index].DiskNumber < sample.DiskNumber)
                {
                    index++;
                }

                Disks.Insert(index, disk);
            }

            disk.Update(sample);
        }

        StatusText = $"{snapshot.Timestamp.LocalDateTime:HH:mm:ss}  |  {Disks.Count} disk(s)";
        UpdateSelfLoad(snapshot);

        if (SelectedDisk is null && Disks.Count > 0)
        {
            SelectedDisk = Disks.FirstOrDefault(d => d.DriveLetters.Contains("C:", StringComparison.OrdinalIgnoreCase)) ?? Disks[0];
            return; // OnSelectedDiskChanged already rebuilt chart + table from the buffer
        }

        if (IsPaused || SelectedDisk is null)
        {
            return;
        }

        AppendPoint(snapshot);
        TrimChart();
        UpdateXLimits(snapshot.Timestamp);

        if (FrozenAt is null)
        {
            RefreshProcesses();
        }
    }

    /// <summary>
    /// The monitor's own footprint on the disks, so "minimally invasive" is a number the user can
    /// see rather than a promise: the ETW session is real-time (no trace file) and nothing is
    /// logged, so this should stay at zero apart from start-up page-ins.
    /// </summary>
    private void UpdateSelfLoad(DiskSnapshot snapshot)
    {
        var self = Environment.ProcessId;
        long ops = 0;
        foreach (var list in snapshot.ProcessIoByDisk.Values)
        {
            foreach (var io in list)
            {
                if (io.Pid == self)
                {
                    ops += io.TotalOps;
                }
            }
        }

        _selfOpsSinceStart += ops;
        SelfLoadText = $"This app: {Formatting.Iops(ops)} now, {Formatting.Count(_selfOpsSinceStart)} requests since start";
    }

    // ---- filter -------------------------------------------------------------------------

    private bool MatchesFilter(ProcessIo io)
    {
        if (_selectedPids.Count > 0 && !_selectedPids.Contains(io.Pid))
        {
            return false;
        }

        return MatchesText(io);
    }

    private bool MatchesText(ProcessIo io)
    {
        var text = FilterText.Trim();
        if (text.Length == 0)
        {
            return true;
        }

        return io.ProcessName.Contains(text, StringComparison.OrdinalIgnoreCase)
            || io.TopFiles.Any(f => f.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    // ---- chart --------------------------------------------------------------------------

    /// <summary>Sums the filtered processes of the selected disk for one second.</summary>
    private (double Read, double Write, double ReadOps, double WriteOps) Sum(DiskSnapshot snapshot)
    {
        if (SelectedDisk is null || !snapshot.ProcessIoByDisk.TryGetValue(SelectedDisk.DiskNumber, out var ios))
        {
            return default;
        }

        double read = 0, write = 0, readOps = 0, writeOps = 0;
        foreach (var io in ios)
        {
            if (!MatchesFilter(io))
            {
                continue;
            }

            read += io.ReadBytes;
            write += io.WriteBytes;
            readOps += io.ReadOps;
            writeOps += io.WriteOps;
        }

        return (read, write, readOps, writeOps);
    }

    private void AppendPoint(DiskSnapshot snapshot)
    {
        var t = snapshot.Timestamp.LocalDateTime;
        var (read, write, readOps, writeOps) = Sum(snapshot);
        ReadValues.Add(new DateTimePoint(t, read));
        WriteValues.Add(new DateTimePoint(t, write));
        ReadOpsValues.Add(new DateTimePoint(t, readOps));
        WriteOpsValues.Add(new DateTimePoint(t, writeOps));
    }

    private void TrimChart()
    {
        while (ReadValues.Count > ChartSeconds)
        {
            ReadValues.RemoveAt(0);
            WriteValues.RemoveAt(0);
            ReadOpsValues.RemoveAt(0);
            WriteOpsValues.RemoveAt(0);
        }
    }

    private void UpdateXLimits(DateTimeOffset latest)
    {
        var end = latest.LocalDateTime;
        XAxes[0].MinLimit = end.AddSeconds(-(ChartSeconds - 1)).Ticks;
        XAxes[0].MaxLimit = end.Ticks;
    }

    private void RebuildChart()
    {
        ReadValues.Clear();
        WriteValues.Clear();
        ReadOpsValues.Clear();
        WriteOpsValues.Clear();
        if (SelectedDisk is null)
        {
            return;
        }

        DiskSnapshot? last = null;
        foreach (var snapshot in _buffer.Window(ChartSeconds))
        {
            AppendPoint(snapshot);
            last = snapshot;
        }

        if (last is not null)
        {
            UpdateXLimits(last.Timestamp);
        }

        UpdateFrozenMarker();
    }

    private void UpdateFrozenMarker()
    {
        FrozenMarker.Clear();
        if (FrozenAt is { } t)
        {
            var top = Math.Max(1, ReadOpsValues.Concat(WriteOpsValues).Select(p => p.Value ?? 0).DefaultIfEmpty(0).Max());
            FrozenMarker.Add(new DateTimePoint(t.LocalDateTime, 0));
            FrozenMarker.Add(new DateTimePoint(t.LocalDateTime, top));
        }
    }

    // ---- table --------------------------------------------------------------------------

    private void RefreshProcesses()
    {
        if (SelectedDisk is null)
        {
            Processes.Clear();
            HasProcesses = false;
            return;
        }

        var disk = SelectedDisk.DiskNumber;
        IReadOnlyList<ProcessIo> rows = IsCumulative
            ? _buffer.AggregateCumulative(disk)
            : FrozenAt is { } frozen
                ? AggregateEndingAt(disk, frozen, WindowSeconds)
                : _buffer.AggregateProcesses(disk, WindowSeconds);

        var shareMetric = SortBy == ProcessSort.Bytes ? (Func<ProcessIo, long>)(r => r.TotalBytes) : r => r.TotalOps;
        var visible = rows.Where(r => (r.TotalBytes > 0 || r.TotalOps > 0) && MatchesText(r)).ToList();
        var total = visible.Sum(shareMetric);

        // Update in place: existing rows (keyed by PID) keep their visuals and selection and are
        // moved into sorted position; only genuinely new processes allocate a row.
        var byPid = new Dictionary<int, ProcessRowViewModel>(Processes.Count);
        foreach (var row in Processes)
        {
            byPid[row.Pid] = row;
        }

        var updated = new List<ProcessRowViewModel>(visible.Count);
        foreach (var io in visible)
        {
            if (byPid.TryGetValue(io.Pid, out var existing))
            {
                existing.Update(io, total, SortBy);
                updated.Add(existing);
            }
            else
            {
                updated.Add(new ProcessRowViewModel(io, total, SortBy, this));
            }
        }

        var ordered = (SortDescending
                ? updated.OrderByDescending(r => r.SortKey(SortBy)).ThenByDescending(r => r.TotalBytes).ThenByDescending(r => r.TotalOps)
                : updated.OrderBy(r => r.SortKey(SortBy)).ThenBy(r => r.TotalBytes).ThenBy(r => r.TotalOps))
            .Take(MaxProcessRows)
            .ToList();

        // The DataGrid's collection view does not honour Move, so reorder with Remove + Insert.
        // The view suppresses selection tracking while IsRefreshingProcesses is set and restores
        // the selection from SelectedPids afterwards.
        IsRefreshingProcesses = true;
        try
        {
            for (var index = 0; index < ordered.Count; index++)
            {
                var row = ordered[index];
                var current = Processes.IndexOf(row);
                if (current == index)
                {
                    continue;
                }

                if (current >= 0)
                {
                    Processes.RemoveAt(current);
                }

                Processes.Insert(index, row);
            }

            while (Processes.Count > ordered.Count)
            {
                Processes.RemoveAt(Processes.Count - 1);
            }
        }
        finally
        {
            IsRefreshingProcesses = false;
        }

        ProcessesRefreshed?.Invoke();
        HasProcesses = Processes.Count > 0;
    }

    /// <summary>Window aggregation for a frozen second: the last <paramref name="seconds"/> snapshots not after <paramref name="end"/>.</summary>
    private List<ProcessIo> AggregateEndingAt(int disk, DateTimeOffset end, int seconds)
    {
        var slice = _buffer.All().Where(s => s.Timestamp <= end).TakeLast(seconds);
        var byPid = new Dictionary<int, (string Name, long Read, long Write, long ReadOps, long WriteOps, List<string> Files)>();
        foreach (var snapshot in slice)
        {
            if (!snapshot.ProcessIoByDisk.TryGetValue(disk, out var ios))
            {
                continue;
            }

            foreach (var io in ios)
            {
                var acc = byPid.GetValueOrDefault(io.Pid, (io.ProcessName, 0, 0, 0, 0, new List<string>()));
                foreach (var f in io.TopFiles.Where(f => !acc.Files.Contains(f, StringComparer.OrdinalIgnoreCase)))
                {
                    acc.Files.Insert(0, f);
                }

                byPid[io.Pid] = (io.ProcessName, acc.Read + io.ReadBytes, acc.Write + io.WriteBytes, acc.ReadOps + io.ReadOps, acc.WriteOps + io.WriteOps, acc.Files);
            }
        }

        return byPid
            .Select(kv => new ProcessIo(kv.Key, kv.Value.Name, kv.Value.Read, kv.Value.Write, kv.Value.Files.Take(SnapshotRingBuffer.TopFilesPerProcess).ToList(), kv.Value.ReadOps, kv.Value.WriteOps))
            .ToList();
    }

    private DiskSnapshot? Nearest(DateTimeOffset timestamp) =>
        _buffer.All().MinBy(s => Math.Abs((s.Timestamp - timestamp).Ticks));

    private static void DispatchToUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    private void SeedDesignData()
    {
        var rng = new Random(7);
        var start = DateTimeOffset.Now.AddSeconds(-45);
        for (var i = 0; i < 45; i++)
        {
            var ts = start.AddSeconds(i);
            var snapshot = new DiskSnapshot(
                ts,
                [
                    new DiskSample(0, "0 C:", ["C:"], 20, 3e6 * rng.NextDouble(), 12e6 * rng.NextDouble(), 0.4, 300, 900),
                    new DiskSample(1, "1 D: E:", ["D:", "E:"], 5, 1e5, 2e5, 0, 20, 5),
                ],
                new Dictionary<int, IReadOnlyList<ProcessIo>>
                {
                    [0] =
                    [
                        new ProcessIo(4321, "chrome", 2_400_000, 900_000, [@"C:\Users\Me\AppData\Local\Google\Chrome\User Data\Default\Cache\f_00012a"], 700, 120),
                        new ProcessIo(1200, "MsMpEng", 8_100_000, 0, [@"C:\Windows\System32\drivers\etc\hosts"], 90, 0),
                        new ProcessIo(777, "devenv", 300_000, 5_200_000, [@"C:\src\app\bin\Debug\net8.0\app.dll"], 40, 800),
                    ],
                    [1] = [new ProcessIo(999, "OneDrive", 100_000, 50_000, [@"D:\Photos\IMG_0001.jpg"], 20, 5)],
                });
            _buffer.Add(snapshot);
            Apply(snapshot);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _monitor.SnapshotReady -= OnSnapshotReady;
        _monitor.Dispose();
    }

    private sealed class NullDiskMonitor : IDiskMonitor
    {
        public event Action<DiskSnapshot>? SnapshotReady { add { } remove { } }
        public void Start() { }
        public void Dispose() { }
    }
}
