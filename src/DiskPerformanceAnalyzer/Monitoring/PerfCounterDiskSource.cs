using System.Diagnostics;
using System.Runtime.Versioning;

namespace DiskPerformanceAnalyzer.Monitoring;

/// <summary>
/// Reads per-physical-disk metrics from the "PhysicalDisk" performance counter category.
/// ActivePercent is 100 - "% Idle Time", which is what Task Manager shows.
/// Instances are re-enumerated periodically so hot-plugged disks appear.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PerfCounterDiskSource : IDisposable
{
    private const string Category = "PhysicalDisk";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly Dictionary<string, DiskCounters> _byInstance = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private bool _disposed;

    /// <summary>Samples all physical disks. Refreshes the instance list every ~10 s.</summary>
    public IReadOnlyList<DiskSample> Sample()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var now = DateTimeOffset.UtcNow;
            if (now - _lastRefresh >= RefreshInterval)
            {
                RefreshInstances();
                _lastRefresh = now;
            }

            var result = new List<DiskSample>(_byInstance.Count);
            foreach (var counters in _byInstance.Values)
            {
                var sample = counters.TryRead();
                if (sample is not null)
                {
                    result.Add(sample);
                }
            }

            result.Sort((a, b) => a.DiskNumber.CompareTo(b.DiskNumber));
            return result;
        }
    }

    private void RefreshInstances()
    {
        string[] instances;
        try
        {
            instances = new PerformanceCounterCategory(Category).GetInstanceNames();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in instances)
        {
            var parsed = PerfCounterInstanceParser.Parse(instance);
            if (parsed is null)
            {
                continue;
            }

            seen.Add(instance);
            if (_byInstance.ContainsKey(instance))
            {
                continue;
            }

            var counters = DiskCounters.TryCreate(instance, parsed.Value.DiskNumber, parsed.Value.DriveLetters);
            if (counters is not null)
            {
                _byInstance[instance] = counters;
            }
        }

        foreach (var stale in _byInstance.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _byInstance[stale].Dispose();
            _byInstance.Remove(stale);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var counters in _byInstance.Values)
            {
                counters.Dispose();
            }

            _byInstance.Clear();
        }
    }

    private sealed class DiskCounters : IDisposable
    {
        private readonly PerformanceCounter _idle;
        private readonly PerformanceCounter _read;
        private readonly PerformanceCounter _write;
        private readonly PerformanceCounter _queue;

        private DiskCounters(string instance, int diskNumber, IReadOnlyList<string> driveLetters)
        {
            Instance = instance;
            DiskNumber = diskNumber;
            DriveLetters = driveLetters;
            _idle = new PerformanceCounter(Category, "% Idle Time", instance, readOnly: true);
            _read = new PerformanceCounter(Category, "Disk Read Bytes/sec", instance, readOnly: true);
            _write = new PerformanceCounter(Category, "Disk Write Bytes/sec", instance, readOnly: true);
            _queue = new PerformanceCounter(Category, "Current Disk Queue Length", instance, readOnly: true);
        }

        public string Instance { get; }
        public int DiskNumber { get; }
        public IReadOnlyList<string> DriveLetters { get; }

        public static DiskCounters? TryCreate(string instance, int diskNumber, IReadOnlyList<string> driveLetters)
        {
            DiskCounters? counters = null;
            try
            {
                counters = new DiskCounters(instance, diskNumber, driveLetters);
                // First NextValue() of a rate counter is always 0 — prime once.
                counters._idle.NextValue();
                counters._read.NextValue();
                counters._write.NextValue();
                counters._queue.NextValue();
                return counters;
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                counters?.Dispose();
                return null;
            }
        }

        public DiskSample? TryRead()
        {
            try
            {
                var active = Math.Clamp(100.0 - _idle.NextValue(), 0.0, 100.0);
                return new DiskSample(
                    DiskNumber,
                    Instance,
                    DriveLetters,
                    active,
                    _read.NextValue(),
                    _write.NextValue(),
                    _queue.NextValue());
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }

        public void Dispose()
        {
            _idle.Dispose();
            _read.Dispose();
            _write.Dispose();
            _queue.Dispose();
        }
    }
}
