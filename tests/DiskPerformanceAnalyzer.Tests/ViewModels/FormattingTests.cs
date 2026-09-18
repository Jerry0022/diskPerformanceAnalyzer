using DiskPerformanceAnalyzer.ViewModels;
using Xunit;

namespace DiskPerformanceAnalyzer.Tests.ViewModels;

public class FormattingTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(999, "999 B")]
    [InlineData(1_000, "1,000 B")]
    [InlineData(5_120, "5 KB")]
    [InlineData(987 * 1024, "990 KB")]
    [InlineData(1_572_864, "1,500 KB")]      // 1.5 MB stays in KB so two significant digits survive
    [InlineData(4_718_592, "4,600 KB")]
    [InlineData(12_897_484_800, "12 GB")]
    [InlineData(-2048, "-2 KB")]
    public void Bytes_RoundsToTwoSignificantDigitsWithoutDecimals(long bytes, string expected)
    {
        Assert.Equal(expected, Formatting.Bytes(bytes));
    }

    [Theory]
    [InlineData(0, "0 B/s")]
    [InlineData(12_897_484.8, "12 MB/s")]
    [InlineData(419_430.4, "410 KB/s")]
    [InlineData(double.NaN, "0 B/s")]
    [InlineData(-5, "0 B/s")]
    public void Rate_AppendsPerSecond(double bytesPerSecond, string expected)
    {
        Assert.Equal(expected, Formatting.Rate(bytesPerSecond));
    }

    [Theory]
    [InlineData(7, 7)]
    [InlineData(87, 87)]
    [InlineData(876, 880)]
    [InlineData(123, 120)]
    [InlineData(1_234, 1_200)]
    [InlineData(9_950, 10_000)]
    [InlineData(0, 0)]
    [InlineData(double.NaN, 0)]
    public void RoundSignificant_KeepsTwoDigits(double value, long expected)
    {
        Assert.Equal(expected, Formatting.RoundSignificant(value));
    }

    [Theory]
    [InlineData(37.6, "38%")]
    [InlineData(140, "100%")]
    [InlineData(-3, "0%")]
    public void Percent_ClampsAndRounds(double value, string expected)
    {
        Assert.Equal(expected, Formatting.Percent(value));
    }

    [Theory]
    [InlineData(0.376, "38%")]
    [InlineData(0.004, "<1%")]
    [InlineData(0, "0%")]
    [InlineData(1.4, "100%")]
    public void Share_FormatsFraction(double fraction, string expected)
    {
        Assert.Equal(expected, Formatting.Share(fraction));
    }
}

public class CountFormattingTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(340, "340")]
    [InlineData(1_234, "1,200")]
    [InlineData(9_999, "10,000")]
    [InlineData(12_345, "12k")]
    [InlineData(123_456, "120k")]
    [InlineData(1_234_567, "1,200k")]
    [InlineData(12_345_678, "12M")]
    public void Count_UsesCompactSuffixesWithoutDecimals(long count, string expected)
    {
        Assert.Equal(expected, Formatting.Count(count));
    }

    [Fact]
    public void Iops_AppendsUnit()
    {
        Assert.Equal("340 req/s", Formatting.Iops(340.4));
        Assert.Equal("0 req/s", Formatting.Iops(double.NaN));
        Assert.Equal("12k", Formatting.CountPerSec(12_345));
    }
}
