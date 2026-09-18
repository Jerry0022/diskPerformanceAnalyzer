using DiskPerformanceAnalyzer.Monitoring;
using DiskPerformanceAnalyzer.ViewModels;
using Xunit;

namespace DiskPerformanceAnalyzer.Tests.ViewModels;

public class FilterSortTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FilterText_NarrowsTableAndChart()
    {
        var monitor = new FakeMonitor();
        using var vm = new MainViewModel(monitor, a => a());
        monitor.Emit(Snapshot(T0, chromeRead: 1_000, msmpengWrite: 5_000));
        monitor.Emit(Snapshot(T0.AddSeconds(1), chromeRead: 3_000, msmpengWrite: 1_000));

        vm.FilterText = "chr";

        var row = Assert.Single(vm.Processes);
        Assert.Equal("chrome", row.ProcessName);
        // Chart sums only the matching process: reads 1 000 then 3 000, no writes.
        Assert.Equal([1_000d, 3_000d], vm.ReadValues.Select(p => p.Value).ToArray());
        Assert.Equal([0d, 0d], vm.WriteValues.Select(p => p.Value).ToArray());
        Assert.Equal("matching \"chr\"", vm.FilterDescription);

        vm.FilterText = "windows"; // matches MsMpEng via its top file path
        Assert.Equal("MsMpEng", Assert.Single(vm.Processes).ProcessName);

        vm.ClearFilterCommand.Execute(null);
        Assert.Equal(2, vm.Processes.Count);
        Assert.Equal("all processes", vm.FilterDescription);
    }

    [Fact]
    public void SelectedRows_IsolateChartButKeepTable()
    {
        var monitor = new FakeMonitor();
        using var vm = new MainViewModel(monitor, a => a());
        monitor.Emit(Snapshot(T0, chromeRead: 1_000, msmpengWrite: 5_000));

        vm.SetSelectedProcesses(vm.Processes.Where(p => p.ProcessName == "MsMpEng"));

        Assert.Equal(2, vm.Processes.Count);
        Assert.Equal([0d], vm.ReadValues.Select(p => p.Value).ToArray());
        Assert.Equal([5_000d], vm.WriteValues.Select(p => p.Value).ToArray());
        Assert.Equal("1 selected process", vm.FilterDescription);
    }

    [Fact]
    public void HeaderClick_SortsAndTogglesDirection()
    {
        var monitor = new FakeMonitor();
        using var vm = new MainViewModel(monitor, a => a());
        monitor.Emit(Snapshot(T0, chromeRead: 1_000, msmpengWrite: 5_000));

        vm.SortCommand.Execute("Name");
        Assert.Equal(ProcessSort.Name, vm.SortBy);
        Assert.False(vm.SortDescending); // text columns start ascending
        Assert.Equal(["chrome", "MsMpEng"], vm.Processes.Select(p => p.ProcessName).ToArray());
        Assert.Equal(ProcessSort.Ops, vm.ShareMetric); // name sort keeps the request bar primary

        vm.SortCommand.Execute("Name");
        Assert.True(vm.SortDescending);
        Assert.Equal(["MsMpEng", "chrome"], vm.Processes.Select(p => p.ProcessName).ToArray());

        vm.SortCommand.Execute("Write");
        Assert.True(vm.SortDescending); // numeric columns start descending
        Assert.Equal("MsMpEng", vm.Processes[0].ProcessName);
        Assert.Equal(ProcessSort.Bytes, vm.ShareMetric); // byte columns make the data bar primary
        Assert.True(vm.Processes[0].IsBytesPrimary);
        Assert.Equal(5_000 / 6_000.0, vm.Processes[0].ShareBytes, 3);
        Assert.Equal(1 / 3.0, vm.Processes[0].ShareOps, 3);
    }

    [Fact]
    public void ShortPath_KeepsFirstTwoAndLastTwoSegments()
    {
        Assert.Equal(@"C:\Users\" + "\u2026" + @"\Cache\f_0001", Formatting.ShortPath(@"C:\Users\Me\AppData\Local\Cache\f_0001"));
        Assert.Equal(@"C:\Windows\System32\x.dll", Formatting.ShortPath(@"C:\Windows\System32\x.dll"));
        Assert.Equal("-", Formatting.ShortPath(null));
    }

    private static DiskSnapshot Snapshot(DateTimeOffset ts, long chromeRead, long msmpengWrite) =>
        new(
            ts,
            [new DiskSample(0, "0 C:", ["C:"], 42, chromeRead, msmpengWrite, 0.5, 2, 1)],
            new Dictionary<int, IReadOnlyList<ProcessIo>>
            {
                [0] =
                [
                    new ProcessIo(100, "chrome", chromeRead, 0, [@"C:\cache\a"], 2, 0),
                    new ProcessIo(200, "MsMpEng", 0, msmpengWrite, [@"C:\Windows\x"], 0, 1),
                ],
            });

    private sealed class FakeMonitor : IDiskMonitor
    {
        public event Action<DiskSnapshot>? SnapshotReady;
        public void Start() { }
        public void Emit(DiskSnapshot snapshot) => SnapshotReady?.Invoke(snapshot);
        public void Dispose() { }
    }
}
