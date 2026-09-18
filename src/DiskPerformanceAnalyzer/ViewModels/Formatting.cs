using System.Globalization;

namespace DiskPerformanceAnalyzer.ViewModels;

/// <summary>Human-readable byte and throughput strings (Task-Manager style: "4.5 MB", "12.3 MB/s").</summary>
public static class Formatting
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long bytes)
    {
        if (bytes < 0)
        {
            return "-" + Bytes(-bytes);
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {Units[unit]}");
    }

    public static string Rate(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond))
        {
            return "0 B/s";
        }

        var rounded = (long)Math.Round(Math.Max(0, bytesPerSecond));
        return Bytes(rounded) + "/s";
    }

    /// <summary>Request counts: "340", "12.3k", "1.2M".</summary>
    public static string Count(long count)
    {
        if (count < 0)
        {
            return "-" + Count(-count);
        }

        return count switch
        {
            < 10_000 => string.Create(CultureInfo.InvariantCulture, $"{count}"),
            < 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{count / 1000.0:0.0}k"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{count / 1_000_000.0:0.0}M"),
        };
    }

    /// <summary>I/O requests per second: "340 IOPS", "12.3k IOPS".</summary>
    public static string Iops(double opsPerSecond)
    {
        if (double.IsNaN(opsPerSecond) || double.IsInfinity(opsPerSecond))
        {
            return "0 IOPS";
        }

        return Count((long)Math.Round(Math.Max(0, opsPerSecond))) + " IOPS";
    }

    /// <summary>
    /// Shortens a path for a table cell: the first two segments, an ellipsis, then the last
    /// folder and the file name. <c>C:\Users\Me\AppData\Local\Cache\f_0001</c> becomes
    /// <c>C:\Users\…\Cache\f_0001</c>. Paths with five segments or fewer are returned as-is.
    /// </summary>
    public static string ShortPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "-";
        }

        var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 5)
        {
            return path;
        }

        return string.Join('\\', [parts[0], parts[1], "\u2026", parts[^2], parts[^1]]);
    }

    public static string Percent(double percent) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Clamp(percent, 0, 100):0}%");
}
