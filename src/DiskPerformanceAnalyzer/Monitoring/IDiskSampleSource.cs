namespace DiskPerformanceAnalyzer.Monitoring;

/// <summary>Enumerates physical disks and samples their counters once per tick.</summary>
public interface IDiskSampleSource : IDisposable
{
    /// <summary>All physical disks with their current per-second numbers, ordered by disk number.</summary>
    IReadOnlyList<DiskSample> Sample();
}

/// <summary>
/// Attributes disk I/O to processes: per (disk, PID) bytes, request counts and files, bucketed
/// per second. Windows: ETW kernel trace. Linux: block tracepoints via tracefs.
/// </summary>
public interface IProcessIoSource : IDisposable
{
    /// <summary>Starts the trace. Throws when the platform facility is unavailable (not elevated, tracefs missing).</summary>
    void Start();

    /// <summary>Returns and resets the per-disk process buckets accumulated since the last call.</summary>
    Dictionary<int, List<ProcessIo>> DrainSecond();

    /// <summary>
    /// Per-disk active time (0–100 %) since the previous call, for sources that see request
    /// completion. Sources that cannot tell return an empty dictionary and the disk source's own
    /// number is used.
    /// </summary>
    Dictionary<int, double> DrainActivePercent();
}
