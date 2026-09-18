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
/// Owns the monitor and the 60 s ring buffer; projects the selected disk onto the chart and the
/// process table. All mutations of bound collections happen through <c>_marshal</c> (UI thread).
/// </summary>
public partial class MainViewModel : ObservableObject, IProcessRowHost, IDisposable
{
    public const int ChartSeconds = 60;
    public static readonly int[] WindowChoices = [1, 5, 60, 300];

    private static readonly SKColor Blue = new(0x3B, 0x8E, 0xEA);
    private static readonly SKColor Green = new(0x4C, 0xC3, 0x8A);
    private static readonly SKColor Orange = new(0xF2, 0x9E, 0x4C);

    private readonly IDiskMonitor _monitor;
    private readonly SnapshotRingBuffer _buffer = new();
    private readonly Action<Action> _marshal;
    private bool _started;
    private bool _disposed;

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
    [NotifyPropertyChangedFor(nameof(IsSortBytes), nameof(IsSortOps), nameof(ShareHeader))]
    private ProcessSort _sortBy = ProcessSort.Ops;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChartThroughput), nameof(IsChartIops))]
    private bool _chartShowsIops;

    [ObservableProperty]
    private string _selfLoadText = "This app: idle";

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
            new LineSeries<DateTimePoint>
            {
                Name = "Active",
                Values = ActiveValues,
                ScalesYAt = 0,
                Fill = new SolidColorPaint(Blue.WithAlpha(0x55)),
                Stroke = new SolidColorPaint(Blue, 2),
                GeometryFill = null,
                GeometryStroke = null,
                GeometrySize = 0,
                LineSmoothness = 0,
                AnimationsSpeed = TimeSpan.Zero,
                YToolTipLabelFormatter = p => $"{p.Model?.Value ?? 0:0}% active",
            },
            new LineSeries<DateTimePoint>
            {
                Name = "Read",
                Values = ReadValues,
                ScalesYAt = 1,
                Fill = null,
                Stroke = new SolidColorPaint(Green, 1.2f),
                GeometryFill = null,
                GeometryStroke = null,
                GeometrySize = 0,
                LineSmoothness = 0,
                AnimationsSpeed = TimeSpan.Zero,
                YToolTipLabelFormatter = p => "Read " + Formatting.Rate(p.Model?.Value ?? 0),
            },
            new LineSeries<DateTimePoint>
            {
                Name = "Write",
                Values = WriteValues,
                ScalesYAt = 1,
                Fill = null,
                Stroke = new SolidColorPaint(Orange, 1.2f),
                GeometryFill = null,
                GeometryStroke = null,
                GeometrySize = 0,
                LineSmoothness = 0,
                AnimationsSpeed = TimeSpan.Zero,
                YToolTipLabelFormatter = p => "Write " + Formatting.Rate(p.Model?.Value ?? 0),
            },
            new LineSeries<DateTimePoint>
            {
                Name = "Read IOPS",
                Values = ReadOpsValues,
                ScalesYAt = 1,
                Fill = null,
                Stroke = new SolidColorPaint(Green, 1.2f),
                GeometryFill = null,
                GeometryStroke = null,
                GeometrySize = 0,
                LineSmoothness = 0,
                AnimationsSpeed = TimeSpan.Zero,
                IsVisible = false,
                IsVisibleAtLegend = false,
                YToolTipLabelFormatter = p => "Read " + Formatting.Iops(p.Model?.Value ?? 0),
            },
            new LineSeries<DateTimePoint>
            {
                Name = "Write IOPS",
                Values = WriteOpsValues,
                ScalesYAt = 1,
                Fill = null,
                Stroke = new SolidColorPaint(Orange, 1.2f),
                GeometryFill = null,
                GeometryStroke = null,
                GeometrySize = 0,
                LineSmoothness = 0,
                AnimationsSpeed = TimeSpan.Zero,
                IsVisible = false,
                IsVisibleAtLegend = false,
                YToolTipLabelFormatter = p => "Write " + Formatting.Iops(p.Model?.Value ?? 0),
            },
            new LineSeries<DateTimePoint>
            {
                Name = "Frozen",
                Values = FrozenMarker,
                ScalesYAt = 0,
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
                Name = "Active",
                MinLimit = 0,
                MaxLimit = 100,
                MinStep = 25,
                Labeler = v => $"{v:0}%",
                TextSize = 11,
                LabelsPaint = new SolidColorPaint(Blue),
                SeparatorsPaint = new SolidColorPaint(SKColors.Gray.WithAlpha(0x30)),
            },
            new Axis
            {
                Name = "Throughput",
                Position = AxisPosition.End,
                MinLimit = 0,
                Labeler = v => Formatting.Rate(v),
                TextSize = 11,
                LabelsPaint = new SolidColorPaint(SKColors.Gray),
                ShowSeparatorLines = false,
            },
        ];
    }

    public ObservableCollection<DiskViewModel> Disks { get; } = new();
    public ObservableCollection<ProcessRowViewModel> Processes { get; } = new();

    public ObservableCollection<DateTimePoint> ActiveValues { get; } = new();
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

    public bool HasSelection => SelectedDisk is not null;
    public string SelectedDiskTitle => SelectedDisk?.Name ?? "No disk selected";
    public bool IsFrozen => FrozenAt is not null;
    public string FrozenText => FrozenAt is { } t ? $"Frozen at {t.LocalDateTime:HH:mm:ss}" : string.Empty;
    public string PauseButtonText => IsPaused ? "Resume" : "Pause";

    public bool IsWindow5 => !IsCumulative && WindowSeconds == 5;
    public bool IsWindow60 => !IsCumulative && WindowSeconds == 60;
    public bool IsWindow300 => !IsCumulative && WindowSeconds == 300;
    public bool IsSortBytes => SortBy == ProcessSort.Bytes;
    public bool IsSortOps => SortBy == ProcessSort.Ops;
    public bool IsChartThroughput => !ChartShowsIops;
    public bool IsChartIops => ChartShowsIops;
    public string ShareHeader => SortBy == ProcessSort.Ops ? "SHARE OF REQUESTS" : "SHARE OF BYTES";

    private string WindowText => WindowSeconds >= 60 ? $"{WindowSeconds / 60} min" : $"{WindowSeconds} s";

    public string WindowLabel => IsCumulative
        ? "since start"
        : FrozenAt is { } t
            ? $"{WindowText} ending {t.LocalDateTime:HH:mm:ss}"
            : $"last {WindowText}";

    public string EmptyStateText => IsCumulative
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

    [RelayCommand]
    private void GoLive() => FrozenAt = null;

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
    private void SelectSort(string mode) => SortBy = mode == "ops" ? ProcessSort.Ops : ProcessSort.Bytes;

    [RelayCommand]
    private void SelectChartMetric(string mode) => ChartShowsIops = mode == "iops";

    partial void OnSortByChanged(ProcessSort value) => RefreshProcesses();

    partial void OnChartShowsIopsChanged(bool value)
    {
        for (var i = 1; i <= 4; i++)
        {
            var show = i <= 2 ? !value : value;
            Series[i].IsVisible = show;
            Series[i].IsVisibleAtLegend = show;
        }

        YAxes[1].Name = value ? "Requests/s" : "Throughput";
        YAxes[1].Labeler = value ? v => Formatting.Iops(v) : v => Formatting.Rate(v);
    }

    public Task CopyTextAsync(string text) => ClipboardWriter?.Invoke(text) ?? Task.CompletedTask;

    partial void OnSelectedDiskChanged(DiskViewModel? value)
    {
        RebuildChart();
        RefreshProcesses();
    }

    partial void OnWindowSecondsChanged(int value) => RefreshProcesses();

    partial void OnIsCumulativeChanged(bool value) => RefreshProcesses();

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

        var selected = snapshot.Disks.FirstOrDefault(d => d.DiskNumber == SelectedDisk.DiskNumber);
        if (selected is not null)
        {
            AppendPoint(snapshot.Timestamp, selected);
            TrimChart();
            UpdateXLimits(snapshot.Timestamp);
        }

        if (FrozenAt is null)
        {
            RefreshProcesses();
        }
    }

    /// <summary>
    /// The monitor's own footprint on the disks, so "minimally invasive" is a number the user can
    /// see rather than a promise: the ETW session is real-time (no trace file) and nothing is
    /// logged, so this should read 0 apart from occasional .NET/JIT page-ins.
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

    private long _selfOpsSinceStart;

    private void AppendPoint(DateTimeOffset timestamp, DiskSample sample)
    {
        var t = timestamp.LocalDateTime;
        ActiveValues.Add(new DateTimePoint(t, Math.Clamp(sample.ActivePercent, 0, 100)));
        ReadValues.Add(new DateTimePoint(t, sample.ReadBytesPerSec));
        WriteValues.Add(new DateTimePoint(t, sample.WriteBytesPerSec));
        ReadOpsValues.Add(new DateTimePoint(t, sample.ReadsPerSec));
        WriteOpsValues.Add(new DateTimePoint(t, sample.WritesPerSec));
    }

    private void TrimChart()
    {
        while (ActiveValues.Count > ChartSeconds)
        {
            ActiveValues.RemoveAt(0);
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
        ActiveValues.Clear();
        ReadValues.Clear();
        WriteValues.Clear();
        ReadOpsValues.Clear();
        WriteOpsValues.Clear();
        if (SelectedDisk is null)
        {
            return;
        }

        DiskSnapshot? last = null;
        foreach (var snapshot in _buffer.All())
        {
            var sample = snapshot.Disks.FirstOrDefault(d => d.DiskNumber == SelectedDisk.DiskNumber);
            if (sample is null)
            {
                continue;
            }

            AppendPoint(snapshot.Timestamp, sample);
            last = snapshot;
        }

        TrimChart();
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
            FrozenMarker.Add(new DateTimePoint(t.LocalDateTime, 0));
            FrozenMarker.Add(new DateTimePoint(t.LocalDateTime, 100));
        }
    }

    /// <summary>Rows shown in the process table; anything beyond this is noise and costs layout time every second.</summary>
    public const int MaxProcessRows = 40;

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

        Func<ProcessIo, long> metric = SortBy == ProcessSort.Ops ? r => r.TotalOps : r => r.TotalBytes;
        var total = rows.Sum(metric);
        var ordered = rows
            .Where(r => r.TotalBytes > 0 || r.TotalOps > 0)
            .OrderByDescending(metric)
            .ThenByDescending(r => r.TotalBytes)
            .Take(MaxProcessRows)
            .ToList();

        // Update in place: existing rows (keyed by PID) keep their visuals and are moved into
        // position; only genuinely new processes allocate a row. Rebuilding the collection
        // every second re-templated every row (context menu, buttons, tooltips) and was the
        // main source of UI-thread CPU and garbage.
        var byPid = new Dictionary<int, ProcessRowViewModel>(Processes.Count);
        foreach (var row in Processes)
        {
            byPid[row.Pid] = row;
        }

        for (var index = 0; index < ordered.Count; index++)
        {
            var io = ordered[index];
            if (byPid.TryGetValue(io.Pid, out var existing))
            {
                existing.Update(io, total, SortBy);
                var current = Processes.IndexOf(existing);
                if (current != index)
                {
                    Processes.Move(current, index);
                }
            }
            else
            {
                Processes.Insert(index, new ProcessRowViewModel(io, total, SortBy, this));
            }
        }

        while (Processes.Count > ordered.Count)
        {
            Processes.RemoveAt(Processes.Count - 1);
        }

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
            .OrderByDescending(p => p.TotalBytes)
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
                    new DiskSample(0, "0 C:", ["C:"], 20 + 60 * Math.Abs(Math.Sin(i / 6.0)), 3e6 * rng.NextDouble(), 12e6 * rng.NextDouble(), 0.4),
                    new DiskSample(1, "1 D: E:", ["D:", "E:"], 5 * rng.NextDouble(), 1e5, 2e5, 0),
                ],
                new Dictionary<int, IReadOnlyList<ProcessIo>>
                {
                    [0] =
                    [
                        new ProcessIo(4321, "chrome", 2_400_000, 900_000, [@"C:\Users\Me\AppData\Local\Google\Chrome\User Data\Default\Cache\f_00012a"]),
                        new ProcessIo(1200, "MsMpEng", 8_100_000, 0, [@"C:\Windows\System32\drivers\etc\hosts"]),
                        new ProcessIo(777, "devenv", 300_000, 5_200_000, [@"C:\src\app\bin\Debug\net8.0\app.dll"]),
                    ],
                    [1] = [new ProcessIo(999, "OneDrive", 100_000, 50_000, [@"D:\Photos\IMG_0001.jpg"])],
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
