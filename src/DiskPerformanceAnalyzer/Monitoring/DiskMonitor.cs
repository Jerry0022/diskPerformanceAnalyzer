using System.Diagnostics;
using System.Runtime.Versioning;

namespace DiskPerformanceAnalyzer.Monitoring;

/// <summary>
/// Combines <see cref="PerfCounterDiskSource"/> and <see cref="EtwProcessIoSource"/> into
/// one <see cref="DiskSnapshot"/> per second, raised on a background thread.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DiskMonitor : IDiskMonitor
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly PerfCounterDiskSource _disks;
    private readonly EtwProcessIoSource _processIo;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _disposed;

    public DiskMonitor()
        : this(new PerfCounterDiskSource(), new EtwProcessIoSource())
    {
    }

    internal DiskMonitor(PerfCounterDiskSource disks, EtwProcessIoSource processIo)
    {
        _disks = disks;
        _processIo = processIo;
    }

    public event Action<DiskSnapshot>? SnapshotReady;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loop is not null)
        {
            return;
        }

        _processIo.Start();
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
                    // A transient counter/ETW hiccup (disk hot-unplug, session recycle) must not
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
        var drained = _processIo.DrainSecond();
        // ETW-derived active time is authoritative: the PhysicalDisk "% Idle Time" counter
        // reports 0 on many NVMe drives, which would pin the chart at 100 %.
        var active = _processIo.DrainActivePercent();
        var disks = counters
            .Select(d => d with { ActivePercent = active.GetValueOrDefault(d.DiskNumber, 0.0) })
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

        _processIo.Dispose();
        _disks.Dispose();
        _cts.Dispose();
    }
}
