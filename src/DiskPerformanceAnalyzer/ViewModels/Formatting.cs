using System.Globalization;

namespace DiskPerformanceAnalyzer.ViewModels;

/// <summary>
/// Human-readable numbers, Task-Manager style but without decimals: every value is rounded to
/// two significant digits ("87" → "90", "1,234" → "1,200", "12.3 MB" → "12 MB") so that live
/// figures do not flicker in the last digit. Units switch (KB → MB, plain → k) only once the
/// scaled value would reach 10,000, which keeps two significant digits intact.
/// </summary>
public static class Formatting
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];
    private const double UnitSwitch = 10_000;

    /// <summary>Rounds to two significant digits; values below 10 stay whole numbers.</summary>
    public static long RoundSignificant(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            return 0;
        }

        if (value < 10)
        {
            return (long)Math.Round(value);
        }

        var step = Math.Pow(10, Math.Floor(Math.Log10(value)) - 1);
        return (long)(Math.Round(value / step) * step);
    }

    public static string Bytes(long bytes)
    {
        if (bytes < 0)
        {
            return "-" + Bytes(-bytes);
        }

        if (bytes < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes:N0} B");
        }

        double value = bytes / 1024.0;
        var unit = 1;
        while (value >= UnitSwitch && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{RoundSignificant(value):N0} {Units[unit]}");
    }

    /// <summary>Index into B/KB/MB/GB/TB that <see cref="Bytes"/> would pick for <paramref name="bytes"/>.</summary>
    public static int UnitFor(double bytes)
    {
        if (double.IsNaN(bytes) || bytes < 1024)
        {
            return 0;
        }

        var value = bytes / 1024.0;
        var unit = 1;
        while (value >= UnitSwitch && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit;
    }

    /// <summary>Formats in a fixed unit (see <see cref="UnitFor"/>) so a whole axis reads the same way.</summary>
    public static string BytesIn(double bytes, int unit)
    {
        unit = Math.Clamp(unit, 0, Units.Length - 1);
        var value = Math.Max(0, bytes) / Math.Pow(1024, unit);
        return string.Create(CultureInfo.InvariantCulture, $"{RoundSignificant(value):N0} {Units[unit]}");
    }

    public static string RateIn(double bytesPerSecond, int unit) =>
        double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond) ? "0 B/s" : BytesIn(bytesPerSecond, unit) + "/s";

    public static string Rate(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond))
        {
            return "0 B/s";
        }

        return Bytes((long)Math.Round(Math.Max(0, bytesPerSecond))) + "/s";
    }

    /// <summary>Request counts: "340", "1,200", "12k", "120k", "1,200k", "12M".</summary>
    public static string Count(long count)
    {
        if (count < 0)
        {
            return "-" + Count(-count);
        }

        if (count < UnitSwitch)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{RoundSignificant(count):N0}");
        }

        if (count < UnitSwitch * 1000)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{RoundSignificant(count / 1000.0):N0}k");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{RoundSignificant(count / 1_000_000.0):N0}M");
    }

    /// <summary>I/O requests per second: "340 req/s", "12k req/s".</summary>
    public static string Iops(double opsPerSecond) => CountPerSec(opsPerSecond) + " " + Labels.RequestsUnit;

    /// <summary>Request rate without unit: "340", "12k".</summary>
    public static string CountPerSec(double opsPerSecond)
    {
        if (double.IsNaN(opsPerSecond) || double.IsInfinity(opsPerSecond))
        {
            return "0";
        }

        return Count((long)Math.Round(Math.Max(0, opsPerSecond)));
    }

    /// <summary>Share of a total (0–1) as "34%"; below 1 % it reads "&lt;1%" rather than "0%".</summary>
    public static string Share(double fraction)
    {
        var percent = Math.Clamp(fraction, 0, 1) * 100;
        return percent is > 0 and < 1
            ? "<1%"
            : string.Create(CultureInfo.InvariantCulture, $"{percent:0}%");
    }

    /// <summary>
    /// Shortens a path for a table cell: the first two segments, an ellipsis, then the last
    /// folder and the file name. <c>C:\Users\Me\AppData\Local\Cache\f_0001</c> becomes
    /// <c>C:\Users\…\Cache\f_0001</c>. Paths with five segments or fewer are returned as-is.
    /// </summary>
    private static readonly char[] PathSeparators = ['\\', '/'];

    public static string ShortPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "-";
        }

        var parts = path.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 5)
        {
            return path;
        }

        var separator = path.Contains('/') && !path.Contains('\\') ? "/" : "\\";
        var head = path.StartsWith('/') ? "/" + parts[0] : parts[0];
        return string.Join(separator, [head, parts[1], "\u2026", parts[^2], parts[^1]]);
    }

    public static string Percent(double percent) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Clamp(percent, 0, 100):0}%");
}
