using System.Globalization;
using System.Text;

namespace DiskPerformanceAnalyzer.Monitoring.Linux;

/// <summary>One whole block device (sda, nvme0n1, mmcblk0) as the Linux equivalent of a "physical disk".</summary>
public sealed record LinuxDisk(int Index, string Name, int Major, int Minor, IReadOnlyList<string> MountPoints);

/// <summary>
/// Enumerates whole block devices from sysfs and maps everything the kernel may report I/O
/// against — the disk itself, its partitions, device-mapper/md volumes stacked on it — back to
/// the disk. Also maps mount points to disks so a file path can be attributed to a disk.
/// Refreshes every 10 s so hot-plugged devices appear.
/// </summary>
public sealed class LinuxBlockDevices
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);
    private static readonly string[] IgnoredPrefixes = ["loop", "ram", "zram", "fd", "nbd", "dm-", "md"];

    private readonly string _sysBlock;
    private readonly string _mountInfo;
    private readonly object _gate = new();
    private readonly Dictionary<(int, int), int> _deviceToDisk = new();
    private readonly List<(string MountPoint, int Disk)> _mounts = new();
    private List<LinuxDisk> _disks = new();
    private DateTime _lastRefreshUtc = DateTime.MinValue;

    public LinuxBlockDevices(string sysBlock = "/sys/block", string mountInfo = "/proc/self/mountinfo")
    {
        _sysBlock = sysBlock;
        _mountInfo = mountInfo;
    }

    /// <summary>Whole disks, index-stable for the lifetime of this instance.</summary>
    public IReadOnlyList<LinuxDisk> Disks
    {
        get
        {
            RefreshIfStale();
            lock (_gate)
            {
                return _disks;
            }
        }
    }

    /// <summary>Disk index for a device number as printed by /proc/diskstats or a block tracepoint.</summary>
    public bool TryGetDisk(int major, int minor, out int disk)
    {
        RefreshIfStale();
        lock (_gate)
        {
            return _deviceToDisk.TryGetValue((major, minor), out disk);
        }
    }

    /// <summary>Disk holding <paramref name="path"/>, by longest matching mount point.</summary>
    public bool TryGetDiskForPath(string path, out int disk)
    {
        RefreshIfStale();
        lock (_gate)
        {
            foreach (var (mountPoint, index) in _mounts) // sorted longest first
            {
                if (path.StartsWith(mountPoint, StringComparison.Ordinal)
                    && (mountPoint.Length == 1 || path.Length == mountPoint.Length || path[mountPoint.Length] == '/'))
                {
                    disk = index;
                    return true;
                }
            }

            disk = -1;
            return false;
        }
    }

    private void RefreshIfStale()
    {
        var now = DateTime.UtcNow;
        if (now - _lastRefreshUtc < RefreshInterval)
        {
            return;
        }

        lock (_gate)
        {
            if (now - _lastRefreshUtc < RefreshInterval)
            {
                return;
            }

            _lastRefreshUtc = now;
            try
            {
                Refresh();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // sysfs went away mid-read (device removal); keep the previous map.
            }
        }
    }

    private void Refresh()
    {
        var known = _disks.ToDictionary(d => d.Name, StringComparer.Ordinal);
        var disks = new List<LinuxDisk>(_disks);
        var deviceToDisk = new Dictionary<(int, int), int>();
        var stacked = new List<(string Name, int Major, int Minor, string[] Slaves)>();

        foreach (var dir in Directory.EnumerateDirectories(_sysBlock).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            if (!TryReadDev(Path.Combine(dir, "dev"), out var major, out var minor))
            {
                continue;
            }

            var slavesDir = Path.Combine(dir, "slaves");
            if (IgnoredPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
            {
                if (name.StartsWith("dm-", StringComparison.Ordinal) || name.StartsWith("md", StringComparison.Ordinal))
                {
                    var slaves = Directory.Exists(slavesDir) ? Directory.EnumerateDirectories(slavesDir).Select(Path.GetFileName).OfType<string>().ToArray() : [];
                    stacked.Add((name, major, minor, slaves));
                }

                continue;
            }

            if (!known.TryGetValue(name, out var disk))
            {
                disk = new LinuxDisk(disks.Count, name, major, minor, Array.Empty<string>());
                disks.Add(disk);
                known[name] = disk;
            }

            deviceToDisk[(major, minor)] = disk.Index;
            foreach (var part in Directory.EnumerateDirectories(dir))
            {
                if (File.Exists(Path.Combine(part, "partition")) && TryReadDev(Path.Combine(part, "dev"), out var pMajor, out var pMinor))
                {
                    deviceToDisk[(pMajor, pMinor)] = disk.Index;
                }
            }
        }

        // Stacked devices (LVM, LUKS, RAID) resolve to the disk of their first slave; a slave may
        // itself be stacked, so iterate until nothing new resolves.
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var disk in disks)
        {
            byName[disk.Name] = disk.Index;
        }

        foreach (var dir in Directory.EnumerateDirectories(_sysBlock))
        {
            foreach (var part in Directory.EnumerateDirectories(dir))
            {
                if (File.Exists(Path.Combine(part, "partition")) && byName.TryGetValue(Path.GetFileName(dir), out var idx))
                {
                    byName[Path.GetFileName(part)] = idx;
                }
            }
        }

        bool progress;
        do
        {
            progress = false;
            foreach (var (name, major, minor, slaves) in stacked)
            {
                if (byName.ContainsKey(name))
                {
                    continue;
                }

                var slave = slaves.FirstOrDefault(s => byName.ContainsKey(s));
                if (slave is not null)
                {
                    byName[name] = byName[slave];
                    deviceToDisk[(major, minor)] = byName[slave];
                    progress = true;
                }
            }
        }
        while (progress);

        // Mount points per disk, longest first so path lookup picks the most specific mount.
        var mounts = new List<(string MountPoint, int Disk)>();
        var mountsPerDisk = disks.ToDictionary(d => d.Index, _ => new List<string>());
        if (File.Exists(_mountInfo))
        {
            foreach (var (major, minor, mountPoint) in ParseMountInfo(File.ReadAllText(_mountInfo)))
            {
                if (deviceToDisk.TryGetValue((major, minor), out var idx))
                {
                    mounts.Add((mountPoint, idx));
                    mountsPerDisk[idx].Add(mountPoint);
                }
            }
        }

        mounts.Sort((a, b) => b.MountPoint.Length.CompareTo(a.MountPoint.Length));

        _disks = disks
            .Select(d => d with { MountPoints = mountsPerDisk[d.Index].Distinct().OrderBy(m => m.Length).ThenBy(m => m, StringComparer.Ordinal).ToList() })
            .ToList();
        _deviceToDisk.Clear();
        foreach (var (key, value) in deviceToDisk)
        {
            _deviceToDisk[key] = value;
        }

        _mounts.Clear();
        _mounts.AddRange(mounts);
    }

    private static bool TryReadDev(string path, out int major, out int minor)
    {
        major = minor = 0;
        if (!File.Exists(path))
        {
            return false;
        }

        return TryParseDev(File.ReadAllText(path).Trim(), out major, out minor);
    }

    /// <summary>Parses "8:16" (sysfs) or "8,16" (tracepoint) device numbers.</summary>
    public static bool TryParseDev(ReadOnlySpan<char> text, out int major, out int minor)
    {
        major = minor = 0;
        var sep = text.IndexOfAny(':', ',');
        return sep > 0
            && int.TryParse(text[..sep], NumberStyles.None, CultureInfo.InvariantCulture, out major)
            && int.TryParse(text[(sep + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out minor);
    }

    /// <summary>
    /// /proc/self/mountinfo lines: "36 35 98:0 /mnt1 /mnt2 rw,noatime master:1 - ext3 /dev/root rw"
    /// → (major, minor, mount point). Octal escapes in the mount point ("\040") are decoded.
    /// </summary>
    public static IEnumerable<(int Major, int Minor, string MountPoint)> ParseMountInfo(string text)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ');
            if (fields.Length < 5 || !TryParseDev(fields[2], out var major, out var minor))
            {
                continue;
            }

            yield return (major, minor, Unescape(fields[4]));
        }
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\'))
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 3 < s.Length && char.IsDigit(s[i + 1]) && char.IsDigit(s[i + 2]) && char.IsDigit(s[i + 3]))
            {
                sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                sb.Append(s[i]);
            }
        }

        return sb.ToString();
    }
}
