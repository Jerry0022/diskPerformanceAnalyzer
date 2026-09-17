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

    private const KernelTraceEventParser.Keywords Keywords =
        KernelTraceEventParser.Keywords.DiskIO
        | KernelTraceEventParser.Keywords.DiskFileIO
        | KernelTraceEventParser.Keywords.FileIO
        | KernelTraceEventParser.Keywords.FileIOInit;

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<int, string> _processNames = new();
    private Dictionary<(int Disk, int Pid), Bucket> _buckets = new();
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

            StopStaleSession();

            var session = new TraceEventSession(SessionName) { StopOnDispose = true };
            session.EnableKernelProvider(Keywords);
            session.Source.Kernel.DiskIORead += OnRead;
            session.Source.Kernel.DiskIOWrite += OnWrite;
            _session = session;

            _pump = new Thread(() => Pump(session))
            {
                Name = "EtwProcessIoSource",
                IsBackground = true,
            };
            _pump.Start();
        }
    }

    /// <summary>
    /// Returns and resets the per-disk process buckets accumulated since the last call.
    /// Each list is sorted by Read+Write descending.
    /// </summary>
    public Dictionary<int, List<ProcessIo>> DrainSecond()
    {
        Dictionary<(int Disk, int Pid), Bucket> drained;
        lock (_gate)
        {
            drained = _buckets;
            _buckets = new Dictionary<(int, int), Bucket>(drained.Count);
        }

        var result = new Dictionary<int, List<ProcessIo>>();
        foreach (var ((disk, pid), bucket) in drained)
        {
            if (!result.TryGetValue(disk, out var list))
            {
                list = new List<ProcessIo>();
                result[disk] = list;
            }

            var topFiles = bucket.Files
                .OrderByDescending(f => f.Value)
                .Take(TopFilesPerProcess)
                .Select(f => f.Key)
                .ToList();

            list.Add(new ProcessIo(pid, ResolveProcessName(pid), bucket.Read, bucket.Write, topFiles));
        }

        foreach (var list in result.Values)
        {
            list.Sort((a, b) => b.TotalBytes.CompareTo(a.TotalBytes));
        }

        return result;
    }

    private void OnRead(DiskIOTraceData data) => Record(data, isWrite: false);

    private void OnWrite(DiskIOTraceData data) => Record(data, isWrite: true);

    private void Record(DiskIOTraceData data, bool isWrite)
    {
        var key = (data.DiskNumber, data.ProcessID);
        long size = data.TransferSize;
        // FileName is resolved by TraceEvent from FileKey via the FileIO/DiskFileIO rundown events.
        var fileName = data.FileName;

        lock (_gate)
        {
            if (!_buckets.TryGetValue(key, out var bucket))
            {
                bucket = new Bucket();
                _buckets[key] = bucket;
            }

            if (isWrite)
            {
                bucket.Write += size;
            }
            else
            {
                bucket.Read += size;
            }

            if (!string.IsNullOrEmpty(fileName))
            {
                bucket.Files[fileName] = bucket.Files.GetValueOrDefault(fileName) + size;
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
        TraceEventSession? session;
        Thread? pump;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            session = _session;
            pump = _pump;
            _session = null;
            _pump = null;
        }

        if (session is null)
        {
            return;
        }

        session.Source.Kernel.DiskIORead -= OnRead;
        session.Source.Kernel.DiskIOWrite -= OnWrite;
        session.Dispose();
        pump?.Join(TimeSpan.FromSeconds(5));
    }

    private sealed class Bucket
    {
        public long Read;
        public long Write;
        public Dictionary<string, long> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
