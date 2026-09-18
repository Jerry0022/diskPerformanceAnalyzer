using System.Globalization;

namespace DiskPerformanceAnalyzer.Monitoring.Linux;

/// <summary>One line of /proc/diskstats (cumulative kernel counters).</summary>
public readonly record struct DiskStatsLine(
    int Major,
    int Minor,
    string Name,
    long ReadsCompleted,
    long SectorsRead,
    long WritesCompleted,
    long SectorsWritten,
    long InFlight,
    long IoTicksMs);

/// <summary>
/// Per-disk numbers from /proc/diskstats: requests/s and bytes/s from the completed counters,
/// busy time from io_ticks, queue length from in-flight — the same numbers iostat shows.
/// </summary>
public sealed class ProcDiskStatsSource : IDiskSampleSource
{
    private const int SectorBytes = 512;

    private readonly LinuxBlockDevices _devices;
    private readonly string _diskStats;
    private readonly Dictionary<int, (DiskStatsLine Line, DateTime AtUtc)> _previous = new();

    public ProcDiskStatsSource(LinuxBlockDevices devices, string diskStats = "/proc/diskstats")
    {
        _devices = devices;
        _diskStats = diskStats;
    }

    public IReadOnlyList<DiskSample> Sample()
    {
        var now = DateTime.UtcNow;
        var lines = Parse(File.ReadAllText(_diskStats)).ToDictionary(l => (l.Major, l.Minor));
        var result = new List<DiskSample>();
        foreach (var disk in _devices.Disks)
        {
            if (!lines.TryGetValue((disk.Major, disk.Minor), out var line))
            {
                continue;
            }

            var sample = new DiskSample(disk.Index, disk.Name, disk.MountPoints, 0, 0, 0, line.InFlight, DeviceName: disk.Name);
            if (_previous.TryGetValue(disk.Index, out var prev))
            {
                var seconds = Math.Max(0.001, (now - prev.AtUtc).TotalSeconds);
                var p = prev.Line;
                sample = sample with
                {
                    ActivePercent = Math.Clamp((line.IoTicksMs - p.IoTicksMs) / (seconds * 10.0), 0, 100),
                    ReadBytesPerSec = Math.Max(0, line.SectorsRead - p.SectorsRead) * SectorBytes / seconds,
                    WriteBytesPerSec = Math.Max(0, line.SectorsWritten - p.SectorsWritten) * SectorBytes / seconds,
                    ReadsPerSec = Math.Max(0, line.ReadsCompleted - p.ReadsCompleted) / seconds,
                    WritesPerSec = Math.Max(0, line.WritesCompleted - p.WritesCompleted) / seconds,
                };
            }

            _previous[disk.Index] = (line, now);
            result.Add(sample);
        }

        return result;
    }

    /// <summary>Parses the whole file; lines with fewer than 14 fields (very old kernels) are skipped.</summary>
    public static IEnumerable<DiskStatsLine> Parse(string text)
    {
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 14)
            {
                continue;
            }

            yield return new DiskStatsLine(
                Int(f[0]), Int(f[1]), f[2],
                ReadsCompleted: Long(f[3]),
                SectorsRead: Long(f[5]),
                WritesCompleted: Long(f[7]),
                SectorsWritten: Long(f[9]),
                InFlight: Long(f[11]),
                IoTicksMs: Long(f[12]));
        }
    }

    private static int Int(string s) => int.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);
    private static long Long(string s) => long.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);

    public void Dispose()
    {
    }
}
