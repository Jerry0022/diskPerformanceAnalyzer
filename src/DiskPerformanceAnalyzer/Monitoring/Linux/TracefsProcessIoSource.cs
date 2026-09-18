using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DiskPerformanceAnalyzer.Monitoring.Linux;

/// <summary>
/// Attributes block I/O to processes on Linux with the <c>block:block_rq_issue</c> tracepoint
/// read through tracefs. Every request the block layer hands to a driver appears as one line
/// in <c>trace_pipe</c> with device, direction, size and the issuing PID — the same
/// information the ETW DiskIO events carry on Windows. Needs root (tracefs is root-only).
/// Files cannot be attributed to requests without eBPF; a process's open files on the disk
/// are listed instead, without per-file counts.
/// </summary>
public sealed class TracefsProcessIoSource : IProcessIoSource
{
    public const string InstanceName = "diskPerformanceAnalyzer";
    /// <summary>Kernel comm of the reader thread (max 15 chars); used to detect a PID namespace.</summary>
    private const string ReaderThreadName = "dpa-trace-read";
    public const int TopFilesPerProcess = SnapshotRingBuffer.TopFilesPerProcess;
    private const int MaxFilesPerProcess = SnapshotRingBuffer.KeptFilesPerProcess;

    private static readonly string[] TracefsRoots = ["/sys/kernel/tracing", "/sys/kernel/debug/tracing"];
    private static readonly string[] IgnoredPathPrefixes = ["/dev/", "/proc/", "/sys/", "/run/"];

    private readonly LinuxBlockDevices _devices;
    private readonly string _instance;
    private readonly bool _ownsInstance;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<int, string> _processNames = new();
    private readonly ConcurrentDictionary<int, int> _threadGroups = new();
    private Dictionary<(int Disk, int Pid), Bucket> _buckets = new();
    private DateTime _lastPidSweepUtc = DateTime.UtcNow;
    private Thread? _reader;
    private volatile bool _disposed;
    private volatile bool _foreignPids;

    private TracefsProcessIoSource(LinuxBlockDevices devices, string instance, bool ownsInstance)
    {
        _devices = devices;
        _instance = instance;
        _ownsInstance = ownsInstance;
    }

    /// <summary>
    /// Locates tracefs (mounting it when the kernel has it but nothing mounted it yet) and
    /// creates a private tracing instance so the app never interferes with other tracers.
    /// Returns null with a user-facing <paramref name="reason"/> when this machine cannot do it.
    /// </summary>
    public static TracefsProcessIoSource? TryCreate(LinuxBlockDevices devices, out string? reason)
    {
        if (!Environment.IsPrivilegedProcess)
        {
            reason = "Per-process attribution needs root (tracefs). Disks are still monitored; restart with sudo or pkexec for the process table.";
            return null;
        }

        var root = FindTracefs() ?? MountTracefs();
        if (root is null)
        {
            reason = "tracefs is not available (kernel without CONFIG_TRACING?). Disks are still monitored without process attribution.";
            return null;
        }

        if (!Directory.Exists(Path.Combine(root, "events", "block", "block_rq_issue")))
        {
            reason = "The kernel does not expose the block:block_rq_issue tracepoint. Disks are still monitored without process attribution.";
            return null;
        }

        var instance = Path.Combine(root, "instances", InstanceName);
        var owns = false;
        try
        {
            if (!Directory.Exists(instance))
            {
                Directory.CreateDirectory(instance); // mkdir in tracefs/instances creates an isolated trace buffer
                owns = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"tracefs instance unavailable ({ex.Message}); using the global trace buffer.");
            instance = root;
        }

        reason = null;
        return new TracefsProcessIoSource(devices, instance, owns);
    }

    private static string? FindTracefs() =>
        TracefsRoots.FirstOrDefault(r => File.Exists(Path.Combine(r, "trace_pipe")));

    private static string? MountTracefs()
    {
        try
        {
            using var mount = Process.Start(new ProcessStartInfo("mount", ["-t", "tracefs", "tracefs", TracefsRoots[0]])
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });
            mount?.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }

        return FindTracefs();
    }

    /// <summary>Enables the tracepoint and starts the trace_pipe reader. Idempotent.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_reader is not null)
            {
                return;
            }

            WriteControl("tracing_on", "0");
            WriteControl("trace", string.Empty); // clear whatever an earlier run left behind
            WriteControl("events/block/block_rq_issue/enable", "1");
            WriteControl("tracing_on", "1");

            _reader = new Thread(ReadLoop)
            {
                Name = ReaderThreadName,
                IsBackground = true,
            };
            _reader.Start();
        }
    }

    private void ReadLoop()
    {
        try
        {
            // trace_pipe blocks until events arrive and consumes them, so the kernel ring buffer
            // never fills. Reads return as soon as any data is there, not when the buffer is full.
            using var stream = new FileStream(Path.Combine(_instance, "trace_pipe"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.None);
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096);
            while (!_disposed)
            {
                var line = reader.ReadLine();
                if (line is null)
                {
                    break;
                }

                if (TryParseLine(line, out var major, out var minor, out var tid, out var isWrite, out var bytes, out var comm)
                    && _devices.TryGetDisk(major, minor, out var disk))
                {
                    // The tracepoint reports PIDs of the root PID namespace. Inside a container or
                    // WSL distro those do not match /proc, so a lookup would name the wrong process.
                    // Our own reader thread is the tell: its comm is unique and must be one of our tasks.
                    if (!_foreignPids && comm == ReaderThreadName && !Directory.Exists($"/proc/self/task/{tid}"))
                    {
                        _foreignPids = true;
                        Trace.TraceWarning("Trace PIDs belong to another PID namespace; process names come from the trace only.");
                    }

                    // Fold the issuing thread into its process right away: short-lived worker
                    // threads are often gone by the time the second is drained.
                    Record(disk, ThreadGroupOf(tid), isWrite, bytes, comm);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Trace.TraceWarning($"trace_pipe reader stopped: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses one trace_pipe line, e.g.
    /// <c> kworker/u8:3-123 [002] d..1. 5.123: block_rq_issue: 8,48 WS 4096 () 2048 + 8 [kworker/u8:3]</c>
    /// → device 8:48, task 123, write, 4096 bytes. The task id is the issuing *thread*; the
    /// caller folds it into its process. Discards and non-R/W requests return false; a pure
    /// flush counts as a zero-byte write (fsync storms are real disk load).
    /// </summary>
    public static bool TryParseLine(string line, out int major, out int minor, out int pid, out bool isWrite, out long bytes, out string comm)
    {
        major = minor = pid = 0;
        isWrite = false;
        bytes = 0;
        comm = string.Empty;

        const string marker = "block_rq_issue: ";
        var at = line.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return false;
        }

        // Prefix: "<comm>-<pid> [<cpu>]"; comm may itself contain '-' and spaces, so cut at the
        // CPU bracket and take the digits after the last dash.
        var bracket = line.IndexOf(" [", StringComparison.Ordinal);
        if (bracket < 0 || bracket > at)
        {
            return false;
        }

        var head = line.AsSpan(0, bracket).Trim();
        var dash = head.LastIndexOf('-');
        if (dash < 0 || !int.TryParse(head[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out pid))
        {
            return false;
        }

        // Tail: "<major>,<minor> <rwbs> <bytes> () <sector> + <nr_sectors> [<comm>]"
        var f = line[(at + marker.Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length < 3 || !LinuxBlockDevices.TryParseDev(f[0], out major, out minor))
        {
            return false;
        }

        var rwbs = f[1];
        if (rwbs.Contains('R'))
        {
            isWrite = false;
        }
        else if (rwbs.Contains('W') || (rwbs.Contains('F') && !rwbs.Contains('D') && !rwbs.Contains('N')))
        {
            isWrite = true;
        }
        else
        {
            return false;
        }

        long.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out bytes);
        if (bytes == 0)
        {
            // Older kernels print 0 bytes for some requests; fall back to the sector count.
            var plus = Array.IndexOf(f, "+");
            if (plus > 0 && plus + 1 < f.Length && long.TryParse(f[plus + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var sectors))
            {
                bytes = sectors * 512;
            }
        }

        var open = line.LastIndexOf('[');
        var close = line.LastIndexOf(']');
        if (open > at && close > open)
        {
            comm = line.Substring(open + 1, close - open - 1);
        }

        return true;
    }

    private void Record(int disk, int pid, bool isWrite, long bytes, string comm)
    {
        lock (_gate)
        {
            if (!_buckets.TryGetValue((disk, pid), out var bucket))
            {
                bucket = new Bucket { Comm = comm };
                _buckets[(disk, pid)] = bucket;
            }

            if (isWrite)
            {
                bucket.Write += bytes;
                bucket.WriteOps++;
            }
            else
            {
                bucket.Read += bytes;
                bucket.ReadOps++;
            }
        }
    }

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

            var files = _foreignPids ? [] : OpenFiles(pid, path => _devices.TryGetDiskForPath(path, out var fileDisk) && fileDisk == disk);
            var topFiles = files.Take(TopFilesPerProcess).Select(f => f.Path).ToList();
            list.Add(new ProcessIo(pid, ResolveProcessName(pid, bucket.Comm), bucket.Read, bucket.Write, topFiles, bucket.ReadOps, bucket.WriteOps) { Files = files });
        }

        foreach (var list in result.Values)
        {
            list.Sort((a, b) => b.TotalBytes.CompareTo(a.TotalBytes));
        }

        SweepExitedProcesses();
        return result;
    }

    /// <summary>Block tracepoints carry no completion time; the disk source's io_ticks is used instead.</summary>
    public Dictionary<int, double> DrainActivePercent() => new();

    /// <summary>
    /// Regular files the process currently holds open (from /proc/pid/fd) for which
    /// <paramref name="onDisk"/> is true. Counts stay 0: the block layer does not know which
    /// file a request belongs to.
    /// </summary>
    internal static List<FileIo> OpenFiles(int pid, Func<string, bool> onDisk, int max = MaxFilesPerProcess)
    {
        var files = new List<FileIo>();
        if (pid <= 0)
        {
            return files;
        }

        try
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            // EnumerateFileSystemEntries: the fd entries are symlinks, which EnumerateFiles skips.
            foreach (var fd in Directory.EnumerateFileSystemEntries($"/proc/{pid}/fd"))
            {
                string? target;
                try
                {
                    target = new FileInfo(fd).LinkTarget;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue; // fd closed while we looked
                }

                if (target is null || !target.StartsWith('/') || target.EndsWith(" (deleted)", StringComparison.Ordinal)
                    || IgnoredPathPrefixes.Any(p => target.StartsWith(p, StringComparison.Ordinal))
                    || !seen.Add(target)
                    || !onDisk(target))
                {
                    continue;
                }

                files.Add(new FileIo(target, 0, 0));
                if (files.Count >= max)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Process exited between the trace event and now.
        }

        return files;
    }

    /// <summary>
    /// PID (thread group id) of a task id: the tracepoint names the issuing thread, the table is
    /// per process. From /proc/tid/status, cached; the tid itself when the task is already gone.
    /// </summary>
    private int ThreadGroupOf(int tid)
    {
        if (tid <= 0 || _foreignPids)
        {
            return tid;
        }

        return _threadGroups.GetOrAdd(tid, static id =>
        {
            try
            {
                foreach (var line in File.ReadLines($"/proc/{id}/status"))
                {
                    if (line.StartsWith("Tgid:", StringComparison.Ordinal)
                        && int.TryParse(line.AsSpan(5).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var tgid))
                    {
                        return tgid;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            return id;
        });
    }

    private string ResolveProcessName(int pid, string comm)
    {
        if (pid == 0)
        {
            return "kernel";
        }

        if (_foreignPids)
        {
            return comm.Length > 0 ? comm : $"PID {pid}";
        }

        return _processNames.GetOrAdd(pid, id =>
        {
            try
            {
                var name = File.ReadAllText($"/proc/{id}/comm").Trim();
                return name.Length > 0 ? name : comm;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return comm.Length > 0 ? comm : $"PID {id}";
            }
        });
    }

    private void SweepExitedProcesses()
    {
        var now = DateTime.UtcNow;
        if (now - _lastPidSweepUtc < TimeSpan.FromMinutes(2))
        {
            return;
        }

        _lastPidSweepUtc = now;
        foreach (var pid in _processNames.Keys.Where(p => !Directory.Exists($"/proc/{p}")).ToList())
        {
            _processNames.TryRemove(pid, out _);
        }

        foreach (var tid in _threadGroups.Keys.Where(t => !Directory.Exists($"/proc/{t}")).ToList())
        {
            _threadGroups.TryRemove(tid, out _);
        }
    }

    private void WriteControl(string relativePath, string value)
    {
        File.WriteAllText(Path.Combine(_instance, relativePath), value);
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
        }

        try
        {
            WriteControl("events/block/block_rq_issue/enable", "0");
            // A marker write shows up in trace_pipe and wakes the blocked reader so it can exit.
            WriteControl("trace_marker", "DiskPerformanceAnalyzer stop");
            _reader?.Join(TimeSpan.FromSeconds(2));
            WriteControl("tracing_on", "0");
            if (_ownsInstance)
            {
                Directory.Delete(_instance);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"tracefs cleanup incomplete: {ex.Message}");
        }
    }

    private sealed class Bucket
    {
        public long Read;
        public long Write;
        public long ReadOps;
        public long WriteOps;
        public string Comm = string.Empty;
    }
}
