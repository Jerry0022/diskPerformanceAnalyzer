using DiskPerformanceAnalyzer.Monitoring;
using Xunit;

namespace DiskPerformanceAnalyzer.Tests.Monitoring;

public class OpsAggregationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private static DiskSnapshot Snap(int second, long bytes, long readOps, long writeOps) =>
        new(
            T0.AddSeconds(second),
            [new DiskSample(0, "0 C:", ["C:"], 10, 0, 0, 0, readOps, writeOps)],
            new Dictionary<int, IReadOnlyList<ProcessIo>>
            {
                [0] = [new ProcessIo(7, "svchost", bytes, 0, [], readOps, writeOps)],
            });

    [Fact]
    public void WindowAggregation_SumsRequestCounts()
    {
        var buffer = new SnapshotRingBuffer();
        buffer.Add(Snap(0, 100, 500, 20));
        buffer.Add(Snap(1, 100, 300, 5));

        var row = Assert.Single(buffer.AggregateProcesses(0, 60));
        Assert.Equal(800, row.ReadOps);
        Assert.Equal(25, row.WriteOps);
        Assert.Equal(825, row.TotalOps);
    }

    [Fact]
    public void CumulativeAggregation_KeepsRequestCountsAcrossEviction()
    {
        var buffer = new SnapshotRingBuffer(capacity: 2);
        buffer.Add(Snap(0, 1, 10, 0));
        buffer.Add(Snap(1, 1, 10, 0));
        buffer.Add(Snap(2, 1, 10, 0));

        Assert.Equal(20, Assert.Single(buffer.AggregateProcesses(0, 60)).ReadOps);
        Assert.Equal(30, Assert.Single(buffer.AggregateCumulative(0)).ReadOps);
    }

    [Fact]
    public void ManyTinyRequests_OutrankFewLargeOnes_WhenSortedByOps()
    {
        var rows = new List<ProcessIo>
        {
            new(1, "copy", 500_000_000, 0, [], 400, 0),
            new(2, "indexer", 2_000_000, 0, [], 9_000, 0),
        };

        var byOps = rows.OrderByDescending(r => r.TotalOps).First();
        var byBytes = rows.OrderByDescending(r => r.TotalBytes).First();
        Assert.Equal("indexer", byOps.ProcessName);
        Assert.Equal("copy", byBytes.ProcessName);
    }
}
