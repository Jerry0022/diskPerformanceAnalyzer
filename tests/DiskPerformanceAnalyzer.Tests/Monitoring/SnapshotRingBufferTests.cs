using DiskPerformanceAnalyzer.Monitoring;
using Xunit;

namespace DiskPerformanceAnalyzer.Tests.Monitoring;

public class SnapshotRingBufferTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    private static DiskSnapshot Snapshot(int second, params (int Disk, ProcessIo Io)[] entries)
    {
        var byDisk = entries
            .GroupBy(e => e.Disk)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ProcessIo>)g.Select(e => e.Io).ToList());
        return new DiskSnapshot(T0.AddSeconds(second), Array.Empty<DiskSample>(), byDisk);
    }

    private static ProcessIo Io(int pid, long read, long write, params string[] files)
        => new(pid, $"proc{pid}", read, write, files);

    [Fact]
    public void Evicts_oldest_beyond_capacity()
    {
        var buffer = new SnapshotRingBuffer(60);
        for (var i = 0; i < 65; i++)
        {
            buffer.Add(Snapshot(i));
        }

        var all = buffer.Window(60);

        Assert.Equal(60, buffer.Count);
        Assert.Equal(60, all.Count);
        Assert.Equal(T0.AddSeconds(5), all[0].Timestamp);
        Assert.Equal(T0.AddSeconds(64), all[^1].Timestamp);
    }

    [Fact]
    public void Window_returns_most_recent_seconds_oldest_first()
    {
        var buffer = new SnapshotRingBuffer();
        for (var i = 0; i < 10; i++)
        {
            buffer.Add(Snapshot(i));
        }

        var window = buffer.Window(3);

        Assert.Equal(new[] { 7, 8, 9 }, window.Select(s => (int)(s.Timestamp - T0).TotalSeconds));
    }

    [Fact]
    public void AggregateProcesses_sums_per_pid_and_sorts_by_total_desc()
    {
        var buffer = new SnapshotRingBuffer();
        buffer.Add(Snapshot(0, (0, Io(10, 100, 0, "a.txt")), (0, Io(20, 50, 50, "b.txt"))));
        buffer.Add(Snapshot(1, (0, Io(10, 100, 200, "a.txt")), (0, Io(30, 10, 0)), (1, Io(10, 9999, 9999))));
        buffer.Add(Snapshot(2, (0, Io(20, 0, 30, "c.txt"))));

        var result = buffer.AggregateProcesses(0, 3);

        Assert.Equal(new[] { 10, 20, 30 }, result.Select(p => p.Pid));
        Assert.Equal(200, result[0].ReadBytes);
        Assert.Equal(200, result[0].WriteBytes);
        Assert.Equal(50, result[1].ReadBytes);
        Assert.Equal(80, result[1].WriteBytes);
        Assert.Equal("a.txt", result[0].TopFiles.Single());
        Assert.Equal("b.txt", result[1].TopFiles[0]);
        Assert.Equal("c.txt", result[1].TopFiles[1]);
    }

    [Fact]
    public void AggregateProcesses_respects_window()
    {
        var buffer = new SnapshotRingBuffer();
        buffer.Add(Snapshot(0, (0, Io(10, 100, 0))));
        buffer.Add(Snapshot(1, (0, Io(10, 1, 0))));

        var result = buffer.AggregateProcesses(0, 1);

        Assert.Equal(1, result.Single().ReadBytes);
    }

    [Fact]
    public void TopFiles_capped_at_three()
    {
        var buffer = new SnapshotRingBuffer();
        buffer.Add(Snapshot(0, (0, Io(10, 100, 0, "a", "b", "c"))));
        buffer.Add(Snapshot(1, (0, Io(10, 100, 0, "d", "e", "f"))));

        var result = buffer.AggregateProcesses(0, 2);

        Assert.Equal(3, result.Single().TopFiles.Count);
    }

    [Fact]
    public void AggregateCumulative_survives_eviction()
    {
        var buffer = new SnapshotRingBuffer(2);
        buffer.Add(Snapshot(0, (0, Io(10, 100, 0))));
        buffer.Add(Snapshot(1, (0, Io(10, 100, 0))));
        buffer.Add(Snapshot(2, (0, Io(10, 100, 0))));
        buffer.Add(Snapshot(3, (0, Io(20, 0, 5))));

        var windowed = buffer.AggregateProcesses(0, 60);
        var cumulative = buffer.AggregateCumulative(0);

        Assert.Equal(100, windowed.Single(p => p.Pid == 10).ReadBytes);
        Assert.Equal(300, cumulative.Single(p => p.Pid == 10).ReadBytes);
        Assert.Equal(5, cumulative.Single(p => p.Pid == 20).WriteBytes);
        Assert.Empty(buffer.AggregateCumulative(7));
    }

    [Fact]
    public void Files_accumulate_bytes_and_ops_and_rank_top_files_by_requests()
    {
        var buffer = new SnapshotRingBuffer();
        buffer.Add(Snapshot(0, (0, new ProcessIo(10, "p", 0, 0, ["a"], 0, 0)
        {
            Files = [new FileIo("a", 100, 1), new FileIo("b", 10, 5), new FileIo(string.Empty, 1, 9)],
        })));
        buffer.Add(Snapshot(1, (0, new ProcessIo(10, "p", 0, 0, ["a"], 0, 0)
        {
            Files = [new FileIo("a", 100, 1), new FileIo(string.Empty, 1, 1)],
        })));

        var io = buffer.AggregateProcesses(0, 2).Single();

        Assert.Equal(["b", "a"], io.TopFiles);
        Assert.Equal(new FileIo(string.Empty, 2, 10), io.Files[0]);
        Assert.Equal(new FileIo("b", 10, 5), io.Files[1]);
        Assert.Equal(new FileIo("a", 200, 2), io.Files[2]);
    }

    [Fact]
    public void AggregateEndingAt_takes_the_window_before_a_timestamp()
    {
        var buffer = new SnapshotRingBuffer();
        buffer.Add(Snapshot(0, (0, Io(10, 1, 0))));
        buffer.Add(Snapshot(1, (0, Io(10, 10, 0))));
        buffer.Add(Snapshot(2, (0, Io(10, 100, 0))));

        Assert.Equal(11, buffer.AggregateEndingAt(0, T0.AddSeconds(1), 5).Single().ReadBytes);
        Assert.Equal(10, buffer.AggregateEndingAt(0, T0.AddSeconds(1), 1).Single().ReadBytes);
    }

    [Fact]
    public void At_returns_snapshot_covering_timestamp()
    {
        var buffer = new SnapshotRingBuffer();
        for (var i = 0; i < 5; i++)
        {
            buffer.Add(Snapshot(i));
        }

        Assert.Equal(T0.AddSeconds(2), buffer.At(T0.AddSeconds(2))!.Timestamp);
        Assert.Equal(T0.AddSeconds(2), buffer.At(T0.AddSeconds(2.7))!.Timestamp);
        Assert.Equal(T0.AddSeconds(4), buffer.At(T0.AddSeconds(4.5))!.Timestamp);
        Assert.Null(buffer.At(T0.AddSeconds(-1)));
        Assert.Null(buffer.At(T0.AddSeconds(10)));
    }
}
