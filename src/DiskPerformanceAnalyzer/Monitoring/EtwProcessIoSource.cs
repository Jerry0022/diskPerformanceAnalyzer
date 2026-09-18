using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace DiskPerformanceAnalyzer.Monitoring;

/// <summary>
/// Attributes physical disk I/O to processes and files using an ETW kernel session.
/// Requires an elevated process. Bytes are bucketed per (DiskNumber, PID) and per file
/// for the current second; <see cref="DrainSecond"/> swaps the buckets atomically.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EtwProcessIoSource : IDisposable
{
    public const string SessionName = "DiskPerformanceAnalyzer-Kernel";
    public const int TopFilesPerProcess = SnapshotRingBuffer.TopFilesPerProcess;

    // DiskIO gives the per-request events. DiskFileIO provides the FileKey -> name rundown for
    // files already open at session start; FileIO adds names for files opened afterwards (the
    // interesting ones). FileIOInit (every read/write call) is deliberately left out.
    private const KernelTraceEventParser.Keywords Keywords =
        KernelTraceEventParser.Keywords.DiskIO
        | KernelTraceEventParser.Keywords.DiskFileIO
        | KernelTraceEventParser.Keywords.FileIO;

    /// <summary>
    /// TraceEvent keeps every FileKey -> name mapping it has ever seen in a history dictionary that
    /// is never trimmed, so a long-running session grows without bound. Recycling the session
    /// drops that state; the cost is at most one second without process attribution.
    /// </summary>
    public static readonly TimeSpan SessionRecycleInterval = TimeSpan.FromMinutes(10);
    private const int MaxFilesPerBucket = 32;

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<int, string> _processNames = new();
    private Dictionary<(int Disk, int Pid), Bucket> _buckets = new();
    private Dictionary<int, List<(double Start, double End)>> _busy = new();
    private double _lastDrainMs = NowMs();
    private DateTime _sessionStartedUtc;
    private DateTime _lastPidSweepUtc = DateTime.UtcNow;
    private TraceEventSession? _session;
    private Thread? _pump;
    private bool _disposed;

    /// <summary>Starts the kernel session, stopping any stale session with the same name. Idempotent.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null)
            {
                return;
            }

            StartSessionLocked();
        }
    }

    private void StartSessionLocked()
    {
        StopStaleSession();

        // Real-time session (no FileName): events are consumed in memory and never written to
        // disk, so the monitor does not add to the load it measures. 16 MB of kernel buffers is
        // plenty for DiskIO + DiskFileIO; the default 64 MB only costs non-paged pool.
        var session = new TraceEventSession(SessionName)
        {
            StopOnDispose = true,
            BufferSizeMB = 16,
        };
        if (!session.IsRealTime)
        {
            throw new InvalidOperationException("ETW session must be real-time; a file-backed session would write to the disk under observation.");
        }

        session.EnableKernelProvider(Keywords);
        session.Source.Kernel.DiskIORead += OnRead;
        session.Source.Kernel.DiskIOWrite += OnWrite;
        _session = session;
        _sessionStartedUtc = DateTime.UtcNow;

        _pump = new Thread(() => Pump(session))
        {
            Name = "EtwProcessIoSource",
            IsBackground = true,
        };
        _pump.Start();
    }

    private void StopSessionLocked()
    {
        var session = _session;
        var pump = _pump;
        _session = null;
        _pump = null;
        if (session is null)
        {
            return;
        }

        session.Source.Kernel.DiskIORead -= OnRead;
        session.Source.Kernel.DiskIOWrite -= OnWrite;
        session.Dispose();
        pump?.Join(TimeSpan.FromSeconds(5));
    }

    /// <summary>Periodic housekeeping: recycle the ETW session and drop names of exited processes.</summary>
    private void MaintainLocked()
    {
        var now = DateTime.UtcNow;
        if (_session is not null && now - _sessionStartedUtc >= SessionRecycleInterval)
        {
            StopSessionLocked();
            StartSessionLocked();
        }

        if (now - _lastPidSweepUtc >= TimeSpan.FromMinutes(2))
        {
            _lastPidSweepUtc = now;
            var alive = new HashSet<int>();
            foreach (var process in Process.GetProcesses())
            {
                alive.Add(process.Id);
                process.Dispose();
            }

            foreach (var pid in _processNames.Keys.Where(pid => !alive.Contains(pid)).ToList())
            {
                _processNames.TryRemove(pid, out _);
            }
        }
    }

    /// <summary>
    /// Returns and resets the per-disk process buckets accumulated since the last call.
    /// Each list is sorted by Read+Write descending.
    /// </summary>
    public Dictionary<int, List<ProcessIo>> DrainSecond()
    {
        var result = new Dictionary<int, List<ProcessIo>>();
        lock (_gate)
        {
            var drained = _buckets;
            _buckets = new Dictionary<(int, int), Bucket>(drained.Count);

            // Resolve FileKey -> path only now, for the few hottest keys per bucket, using the
            // parser that recorded them (before a possible session recycle below discards it).
            var parser = _session?.Source.Kernel;
            foreach (var ((disk, pid), bucket) in drained)
            {
                if (!result.TryGetValue(disk, out var list))
                {
                    list = new List<ProcessIo>();
                    result[disk] = list;
                }

                // Every named file keeps its own counts (the folder breakdown needs them); keys the
                // kernel never named (paging, $Mft, cache flushes) collapse into one unnamed entry.
                var files = new Dictionary<string, FileIo>(bucket.Files.Count, StringComparer.OrdinalIgnoreCase);
                long unnamedBytes = 0, unnamedOps = 0;
                foreach (var (key, counts) in bucket.Files)
                {
                    var name = parser?.FileIDToFileName(key);
                    if (string.IsNullOrEmpty(name))
                    {
                        unnamedBytes += counts.Bytes;
                        unnamedOps += counts.Ops;
                        continue;
                    }

                    files[name] = files.TryGetValue(name, out var existing)
                        ? existing with { Bytes = existing.Bytes + counts.Bytes, Ops = existing.Ops + counts.Ops }
                        : new FileIo(name, counts.Bytes, counts.Ops);
                }

                var ranked = files.Values.OrderByDescending(f => f.Ops).ThenByDescending(f => f.Bytes).ToList();
                if (unnamedOps > 0 || unnamedBytes > 0)
                {
                    ranked.Add(new FileIo(string.Empty, unnamedBytes, unnamedOps));
                }

                var topFiles = ranked.Where(f => !f.IsUnnamed).Take(TopFilesPerProcess).Select(f => f.Path).ToList();
                list.Add(new ProcessIo(pid, ResolveProcessName(pid), bucket.Read, bucket.Write, topFiles, bucket.ReadOps, bucket.WriteOps) { Files = ranked });
            }

            if (!_disposed)
            {
                MaintainLocked();
            }
        }

        foreach (var list in result.Values)
        {
            list.Sort((a, b) => b.TotalBytes.CompareTo(a.TotalBytes));
        }

        return result;
    }

    /// <summary>
    /// Returns per-disk active time (0–100 %) for the window since the previous call, computed
    /// as the union of all I/O in-flight intervals (see <see cref="BusyTime"/>). Disks without
    /// any completed I/O in the window are absent.
    /// </summary>
    public Dictionary<int, double> DrainActivePercent()
    {
        Dictionary<int, List<(double Start, double End)>> drained;
        double windowStart;
        var windowEnd = NowMs();
        lock (_gate)
        {
            drained = _busy;
            _busy = new Dictionary<int, List<(double, double)>>(drained.Count);
            windowStart = _lastDrainMs;
            _lastDrainMs = windowEnd;
        }

        var result = new Dictionary<int, double>(drained.Count);
        foreach (var (disk, intervals) in drained)
        {
            result[disk] = BusyTime.ActivePercent(intervals, windowStart, windowEnd);
        }

        return result;
    }

    private static double NowMs() => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerMillisecond;

    private void OnRead(DiskIOTraceData data) => Record(data, isWrite: false);

    private void OnWrite(DiskIOTraceData data) => Record(data, isWrite: true);

    private void Record(DiskIOTraceData data, bool isWrite)
    {
        var key = (data.DiskNumber, data.ProcessID);
        long size = data.TransferSize;
        // Only the raw FileKey is stored here: resolving it to a path allocates a string per event,
        // and at thousands of I/Os per second that dominated the pump thread. DrainSecond resolves
        // the few hottest keys once per second instead.
        var fileKey = data.FileKey;
        // Completion timestamp + elapsed time gives the interval this request was in flight.
        var endMs = data.TimeStamp.ToUniversalTime().Ticks / (double)TimeSpan.TicksPerMillisecond;
        var startMs = endMs - Math.Max(data.ElapsedTimeMSec, 0);

        lock (_gate)
        {
            if (!_busy.TryGetValue(data.DiskNumber, out var intervals))
            {
                intervals = new List<(double, double)>();
                _busy[data.DiskNumber] = intervals;
            }

            intervals.Add((startMs, endMs));

            if (!_buckets.TryGetValue(key, out var bucket))
            {
                bucket = new Bucket();
                _buckets[key] = bucket;
            }

            if (isWrite)
            {
                bucket.Write += size;
                bucket.WriteOps++;
            }
            else
            {
                bucket.Read += size;
                bucket.ReadOps++;
            }

            if (fileKey != 0
                && (bucket.Files.Count < MaxFilesPerBucket || bucket.Files.ContainsKey(fileKey)))
            {
                var counts = bucket.Files.GetValueOrDefault(fileKey);
                bucket.Files[fileKey] = (counts.Bytes + size, counts.Ops + 1);
            }
        }
    }

    private string ResolveProcessName(int pid)
    {
        if (pid == 4)
        {
            return "System";
        }

        if (pid <= 0)
        {
            return $"PID {pid}";
        }

        return _processNames.GetOrAdd(pid, static id =>
        {
            try
            {
                using var process = Process.GetProcessById(id);
                return process.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return $"PID {id}";
            }
        });
    }

    private static void Pump(TraceEventSession session)
    {
        try
        {
            session.Source.Process();
        }
        catch (Exception)
        {
            // Session was stopped or disposed from another thread; nothing to recover.
        }
    }

    private static void StopStaleSession()
    {
        try
        {
            if (TraceEventSession.GetActiveSessionNames().Contains(SessionName, StringComparer.OrdinalIgnoreCase))
            {
                using var stale = new TraceEventSession(SessionName, TraceEventSessionOptions.Attach);
                stale.Stop();
            }
        }
        catch (Exception)
        {
            // Best effort; creating the new session surfaces real failures.
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
            StopSessionLocked();
        }
    }

    private sealed class Bucket
    {
        public long Read;
        public long Write;
        public long ReadOps;
        public long WriteOps;
        public Dictionary<ulong, (long Bytes, long Ops)> Files { get; } = new();
    }
}
