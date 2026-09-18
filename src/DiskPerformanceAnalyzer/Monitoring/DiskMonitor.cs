using System.Diagnostics;
using DiskPerformanceAnalyzer.Monitoring.Linux;

namespace DiskPerformanceAnalyzer.Monitoring;

/// <summary>
/// Combines a disk sample source and a process I/O source into one <see cref="DiskSnapshot"/>
/// per second, raised on a background thread. <see cref="Create"/> picks the platform sources.
/// </summary>
public sealed class DiskMonitor : IDiskMonitor
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly IDiskSampleSource _disks;
    private readonly IProcessIoSource? _processIo;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _disposed;

    /// <param name="processIo">Null when per-process attribution is unavailable on this machine; disks still get sampled.</param>
    public DiskMonitor(IDiskSampleSource disks, IProcessIoSource? processIo, string? notice = null)
    {
        _disks = disks;
        _processIo = processIo;
        Notice = notice;
    }

    /// <summary>
    /// Platform sources: Windows uses PhysicalDisk counters + an ETW kernel session; Linux uses
    /// /proc/diskstats + block tracepoints (tracefs, root). When the process source cannot start,
    /// the monitor still runs disk-only and <see cref="Notice"/> says why.
    /// </summary>
    public static DiskMonitor Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new DiskMonitor(new PerfCounterDiskSource(), new EtwProcessIoSource());
        }

        if (OperatingSystem.IsLinux())
        {
            var devices = new LinuxBlockDevices();
            var disks = new ProcDiskStatsSource(devices);
            var tracing = TracefsProcessIoSource.TryCreate(devices, out var reason);
            return new DiskMonitor(disks, tracing, tracing is null ? reason : null);
        }

        throw new PlatformNotSupportedException("Disk Performance Analyzer supports Windows and Linux.");
    }

    public event Action<DiskSnapshot>? SnapshotReady;

    /// <summary>Why per-process attribution is missing (shown in the UI), or null when it works.</summary>
    public string? Notice { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loop is not null)
        {
            return;
        }

        if (_processIo is not null)
        {
            try
            {
                _processIo.Start();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Notice = $"Per-process attribution unavailable: {ex.Message}";
                Trace.TraceWarning(Notice);
            }
        }

        _disks.Sample(); // enumerate + prime counters before the first tick
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                DiskSnapshot snapshot;
                try
                {
                    snapshot = Capture();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A transient counter/trace hiccup (disk hot-unplug, session recycle) must not
                    // end the monitoring loop; skip this second and keep going.
                    Trace.TraceWarning($"DiskMonitor tick failed: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                SnapshotReady?.Invoke(snapshot);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private DiskSnapshot Capture()
    {
        var counters = _disks.Sample();
        if (_processIo is null || Notice is not null)
        {
            return new DiskSnapshot(DateTimeOffset.Now, counters, new Dictionary<int, IReadOnlyList<ProcessIo>>());
        }

        var drained = _processIo.DrainSecond();
        // Trace-derived active time is authoritative where available: the Windows PhysicalDisk
        // "% Idle Time" counter reports 0 on many NVMe drives, which would pin the chart at 100 %.
        var active = _processIo.DrainActivePercent();
        // Bytes and request counts come from the same trace events the process table is built
        // from, so disk totals, chart and table always agree. The disk source only contributes
        // the disk list, the queue length and (on Linux) the busy time.
        var disks = counters
            .Select(d =>
            {
                var ops = drained.TryGetValue(d.DiskNumber, out var list) ? list : null;
                return d with
                {
                    ActivePercent = active.TryGetValue(d.DiskNumber, out var pct) ? pct : d.ActivePercent,
                    ReadBytesPerSec = ops?.Sum(p => p.ReadBytes) ?? 0,
                    WriteBytesPerSec = ops?.Sum(p => p.WriteBytes) ?? 0,
                    ReadsPerSec = ops?.Sum(p => p.ReadOps) ?? 0,
                    WritesPerSec = ops?.Sum(p => p.WriteOps) ?? 0,
                };
            })
            .ToList();

        var byDisk = new Dictionary<int, IReadOnlyList<ProcessIo>>(drained.Count);
        foreach (var (disk, list) in drained)
        {
            byDisk[disk] = list;
        }

        return new DiskSnapshot(DateTimeOffset.Now, disks, byDisk);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }

        _processIo?.Dispose();
        _disks.Dispose();
        _cts.Dispose();
    }
}
