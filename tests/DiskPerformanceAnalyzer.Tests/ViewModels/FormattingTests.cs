using DiskPerformanceAnalyzer.ViewModels;
using Xunit;

namespace DiskPerformanceAnalyzer.Tests.ViewModels;

public class FormattingTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(999, "999 B")]
    [InlineData(1_000, "1.0 KB")]
    [InlineData(4_718_592, "4.5 MB")]
    [InlineData(12_897_484_800, "12.0 GB")]
    [InlineData(-2048, "-2.0 KB")]
    public void Bytes_UsesBinaryUnitsWithOneDecimal(long bytes, string expected)
    {
        Assert.Equal(expected, Formatting.Bytes(bytes));
    }

    [Theory]
    [InlineData(0, "0 B/s")]
    [InlineData(12_897_484.8, "12.3 MB/s")]
    [InlineData(double.NaN, "0 B/s")]
    [InlineData(-5, "0 B/s")]
    public void Rate_AppendsPerSecond(double bytesPerSecond, string expected)
    {
        Assert.Equal(expected, Formatting.Rate(bytesPerSecond));
    }

    [Theory]
    [InlineData(37.6, "38%")]
    [InlineData(140, "100%")]
    [InlineData(-3, "0%")]
    public void Percent_ClampsAndRounds(double value, string expected)
    {
        Assert.Equal(expected, Formatting.Percent(value));
    }
}

public class CountFormattingTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(340, "340")]
    [InlineData(9_999, "9999")]
    [InlineData(12_345, "12.3k")]
    [InlineData(1_234_567, "1.2M")]
    public void Count_UsesCompactSuffixes(long count, string expected)
    {
        Assert.Equal(expected, Formatting.Count(count));
    }

    [Fact]
    public void Iops_AppendsUnit()
    {
        Assert.Equal("340 IOPS", Formatting.Iops(340.4));
        Assert.Equal("0 IOPS", Formatting.Iops(double.NaN));
    }
}
