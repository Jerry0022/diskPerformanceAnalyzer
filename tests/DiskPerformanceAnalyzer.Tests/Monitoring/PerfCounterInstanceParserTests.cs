using DiskPerformanceAnalyzer.Monitoring;
using Xunit;

namespace DiskPerformanceAnalyzer.Tests.Monitoring;

public class PerfCounterInstanceParserTests
{
    [Fact]
    public void Parses_single_drive_letter()
    {
        var result = PerfCounterInstanceParser.Parse("0 C:");

        Assert.NotNull(result);
        Assert.Equal(0, result.Value.DiskNumber);
        Assert.Equal(new[] { "C:" }, result.Value.DriveLetters);
    }

    [Fact]
    public void Parses_multiple_drive_letters()
    {
        var result = PerfCounterInstanceParser.Parse("1 D: E:");

        Assert.NotNull(result);
        Assert.Equal(1, result.Value.DiskNumber);
        Assert.Equal(new[] { "D:", "E:" }, result.Value.DriveLetters);
    }

    [Fact]
    public void Parses_disk_without_letters()
    {
        var result = PerfCounterInstanceParser.Parse("2");

        Assert.NotNull(result);
        Assert.Equal(2, result.Value.DiskNumber);
        Assert.Empty(result.Value.DriveLetters);
    }

    [Theory]
    [InlineData("_Total")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void Rejects_non_disk_instances(string? name)
    {
        Assert.Null(PerfCounterInstanceParser.Parse(name));
    }
}
