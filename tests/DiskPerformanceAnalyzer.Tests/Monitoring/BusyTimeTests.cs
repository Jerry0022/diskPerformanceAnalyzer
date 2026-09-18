using DiskPerformanceAnalyzer.Monitoring;
using Xunit;

namespace DiskPerformanceAnalyzer.Tests.Monitoring;

public class BusyTimeTests
{
    [Fact]
    public void EmptyIsZero()
    {
        Assert.Equal(0, BusyTime.ActivePercent(new(), 0, 1000));
    }

    [Fact]
    public void OverlappingIntervalsAreNotDoubleCounted()
    {
        var intervals = new List<(double, double)> { (0, 500), (250, 750) };
        Assert.Equal(750, BusyTime.UnionLength(intervals, 0, 1000));
        Assert.Equal(75, BusyTime.ActivePercent(intervals, 0, 1000));
    }

    [Fact]
    public void DisjointIntervalsAreSummed()
    {
        var intervals = new List<(double, double)> { (0, 100), (200, 300), (900, 1000) };
        Assert.Equal(300, BusyTime.UnionLength(intervals, 0, 1000));
    }

    [Fact]
    public void IntervalsAreClampedToWindow()
    {
        var intervals = new List<(double, double)> { (-500, 200), (900, 1500) };
        Assert.Equal(300, BusyTime.UnionLength(intervals, 0, 1000));
    }

    [Fact]
    public void FullyBusyIsCappedAtHundred()
    {
        var intervals = new List<(double, double)> { (-10, 2000), (5, 6) };
        Assert.Equal(100, BusyTime.ActivePercent(intervals, 0, 1000));
    }

    [Fact]
    public void UnsortedInputIsHandled()
    {
        var intervals = new List<(double, double)> { (600, 700), (0, 100), (650, 800) };
        Assert.Equal(300, BusyTime.UnionLength(intervals, 0, 1000));
    }
}
