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
        public void Start() => Started = true;
        public void Emit(DiskSnapshot snapshot) => SnapshotReady?.Invoke(snapshot);
        public void Dispose() { }
    }
}
