using DiskPerformanceAnalyzer.Monitoring;
using DiskPerformanceAnalyzer.ViewModels;
using Xunit;

namespace DiskPerformanceAnalyzer.Tests.ViewModels;

public class MainViewModelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstSnapshot_SelectsSystemDiskAndSortsProcessesByTotalBytes()
    {
        var monitor = new FakeMonitor();
        using var vm = new MainViewModel(monitor, a => a());

        monitor.Emit(Snapshot(T0, chromeRead: 1_000, msmpengWrite: 5_000));
        monitor.Emit(Snapshot(T0.AddSeconds(1), chromeRead: 9_000, msmpengWrite: 1_000));

        Assert.Equal(2, vm.Disks.Count);
        Assert.NotNull(vm.SelectedDisk);
        Assert.Equal(0, vm.SelectedDisk!.DiskNumber);
        Assert.Equal("Disk 0 (C:)", vm.SelectedDisk.Name);
        Assert.Equal("Disk 1 (D: E:)", vm.Disks[1].Name);

        // 5 s window aggregates both seconds: chrome 10 000 > MsMpEng 6 000.
        Assert.Equal(["chrome", "MsMpEng"], vm.Processes.Select(p => p.ProcessName).ToArray());
        Assert.Equal(10_000, vm.Processes[0].TotalBytes);
        Assert.Equal(6_000, vm.Processes[1].TotalBytes);
        Assert.True(vm.HasProcesses);
        Assert.Equal(2, vm.ReadValues.Count);
        Assert.Equal(2, vm.SelectedDisk.Sparkline.Count);
    }

    [Fact]
    public void OneSecondWindow_OnlyCountsLatestSecond()
    {
        var monitor = new FakeMonitor();
        using var vm = new MainViewModel(monitor, a => a());
        monitor.Emit(Snapshot(T0, chromeRead: 1_000, msmpengWrite: 5_000));
        monitor.Emit(Snapshot(T0.AddSeconds(1), chromeRead: 100, msmpengWrite: 900));

        vm.SelectWindowCommand.Execute("1");

        Assert.Equal(1, vm.WindowSeconds);
        Assert.Equal(["MsMpEng", "chrome"], vm.Processes.Select(p => p.ProcessName).ToArray());
        Assert.Equal(900, vm.Processes[0].TotalBytes);
    }

    [Fact]
    public void Freeze_PinsTableToClickedSecond_AndGoLiveReleases()
    {
        var monitor = new FakeMonitor();
        using var vm = new MainViewModel(monitor, a => a());
        monitor.Emit(Snapshot(T0, chromeRead: 1_000, msmpengWrite: 5_000));
        monitor.Emit(Snapshot(T0.AddSeconds(1), chromeRead: 9_000, msmpengWrite: 0));
        vm.SelectWindowCommand.Execute("1");

        vm.FreezeAt(T0.AddMilliseconds(400));

        Assert.True(vm.IsFrozen);
        Assert.Equal(T0, vm.FrozenAt);
        Assert.Equal("MsMpEng", vm.Processes[0].ProcessName);
        Assert.Equal(2, vm.FrozenMarker.Count);

        monitor.Emit(Snapshot(T0.AddSeconds(2), chromeRead: 50_000, msmpengWrite: 0));
        Assert.Equal("MsMpEng", vm.Processes[0].ProcessName); // still frozen

        vm.GoLiveCommand.Execute(null);
        Assert.False(vm.IsFrozen);
        Assert.Equal("chrome", vm.Processes[0].ProcessName);
        Assert.Empty(vm.FrozenMarker);
    }

    [Fact]
    public void Range_PinsTableAndShowsAveragesInLegend_GoLiveReleases()
    {
        var monitor = new FakeMonitor();
        using var vm = new MainViewModel(monitor, a => a());
        monitor.Emit(Snapshot(T0, chromeRead: 1_000, msmpengWrite: 0));
        monitor.Emit(Snapshot(T0.AddSeconds(1), chromeRead: 3_000, msmpengWrite: 0));
        monitor.Emit(Snapshot(T0.AddSeconds(2), chromeRead: 0, msmpengWrite: 4_000));
        monitor.Emit(Snapshot(T0.AddSeconds(3), chromeRead: 0, msmpengWrite: 90_000));

        // Dragged right to left, between sample positions: snaps to the seconds 0..2.
        vm.SelectRange(T0.AddSeconds(2.4), T0.AddSeconds(-0.3));

        Assert.True(vm.HasRange);
        Assert.True(vm.IsFrozen);
        Assert.Equal((T0, T0.AddSeconds(2)), vm.Range);
        Assert.False(vm.IsWindow60);
        Assert.Equal(4_000, vm.Processes.Single(p => p.ProcessName == "chrome").TotalBytes);
        Assert.Equal(4_000, vm.Processes.Single(p => p.ProcessName == "MsMpEng").TotalBytes);
        Assert.Equal("Ø " + Formatting.Rate(4_000 / 3.0), vm.Legend[0].AverageText);
        Assert.Equal("Ø " + Formatting.Rate(4_000 / 3.0), vm.Legend[1].AverageText);
        Assert.True(vm.Sections[0].IsVisible);

        monitor.Emit(Snapshot(T0.AddSeconds(4), chromeRead: 1_000_000, msmpengWrite: 0));
        Assert.Equal(4_000, vm.Processes.Single(p => p.ProcessName == "chrome").TotalBytes); // still pinned

        vm.GoLiveCommand.Execute(null);
        Assert.False(vm.IsFrozen);
        Assert.Null(vm.Legend[0].AverageText);
        Assert.False(vm.Sections[0].IsVisible);
        Assert.True(vm.IsWindow60);
    }

    [Fact]
    public void Range_OverOneSample_FreezesThatSecond()
    {
        var monitor = new FakeMonitor();
        using var vm = new MainViewModel(monitor, a => a());
        monitor.Emit(Snapshot(T0, chromeRead: 1_000, msmpengWrite: 0));
        monitor.Emit(Snapshot(T0.AddSeconds(1), chromeRead: 3_000, msmpengWrite: 0));

        vm.SelectRange(T0.AddSeconds(0.8), T0.AddSeconds(1.3));

        Assert.False(vm.HasRange);
        Assert.Equal(T0.AddSeconds(1), vm.FrozenAt);
    }

    [Fact]
    public void LegendToggle_HidesItsSeries()
    {
        using var vm = new MainViewModel(new FakeMonitor(), a => a());

        vm.Legend[0].IsVisible = false;

        Assert.False(vm.Series[0].IsVisible);
        Assert.True(vm.Series[1].IsVisible);
        vm.Legend[0].IsVisible = true;
        Assert.True(vm.Series[0].IsVisible);
    }

    [Fact]
    public void Cumulative_IgnoresWindowAndSumsSinceStart()
    {
        var monitor = new FakeMonitor();
        using var vm = new MainViewModel(monitor, a => a());
        monitor.Emit(Snapshot(T0, chromeRead: 1_000, msmpengWrite: 5_000));
        monitor.Emit(Snapshot(T0.AddSeconds(1), chromeRead: 1_000, msmpengWrite: 0));

        vm.SelectWindowCommand.Execute("all");

        Assert.True(vm.IsCumulative);
        Assert.Equal("No disk activity since start", vm.EmptyStateText);
        Assert.Equal(5_000, vm.Processes.Single(p => p.ProcessName == "MsMpEng").TotalBytes);
        Assert.Equal(2_000, vm.Processes.Single(p => p.ProcessName == "chrome").TotalBytes);
    }

    [Fact]
    public void IdleDisk_ShowsEmptyState()
    {
        var monitor = new FakeMonitor();
        using var vm = new MainViewModel(monitor, a => a());
        monitor.Emit(Snapshot(T0, chromeRead: 0, msmpengWrite: 0));

        Assert.False(vm.HasProcesses);
        Assert.Equal("No disk activity in the last 1 min", vm.EmptyStateText);
    }

    private static DiskSnapshot Snapshot(DateTimeOffset ts, long chromeRead, long msmpengWrite) =>
        new(
            ts,
            [
                new DiskSample(0, "0 C:", ["C:"], 42, 1_000_000, 2_000_000, 0.5),
                new DiskSample(1, "1 D: E:", ["D:", "E:"], 3, 0, 0, 0),
            ],
            new Dictionary<int, IReadOnlyList<ProcessIo>>
            {
                [0] =
                [
                    new ProcessIo(100, "chrome", chromeRead, 0, [@"C:\cache\a"]),
                    new ProcessIo(200, "MsMpEng", 0, msmpengWrite, [@"C:\Windows\x"]),
                ],
            });

    private sealed class FakeMonitor : IDiskMonitor
    {
        public event Action<DiskSnapshot>? SnapshotReady;
        public bool Started { get; private set; }
        public string? Notice => null;
        public void Start() => Started = true;
        public void Emit(DiskSnapshot snapshot) => SnapshotReady?.Invoke(snapshot);
        public void Dispose() { }
    }
}
