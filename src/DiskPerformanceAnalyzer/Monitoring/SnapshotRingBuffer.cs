namespace DiskPerformanceAnalyzer.Monitoring;

/// <summary>
/// Keeps the last <see cref="Capacity"/> one-second snapshots and aggregates process I/O
/// over a window or cumulatively since start. Thread-safe.
/// </summary>
public sealed class SnapshotRingBuffer
{
    public const int DefaultCapacity = 60;
    public const int TopFilesPerProcess = 3;
    /// <summary>Upper bound of distinct file paths tracked per process before pruning.</summary>
    public const int MaxTrackedFilesPerProcess = 64;

    private readonly object _gate = new();
    private readonly DiskSnapshot[] _items;
    private readonly Dictionary<int, Dictionary<int, Accumulator>> _cumulative = new();
    private int _head; // index of the oldest item
    private int _count;

    public SnapshotRingBuffer(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new DiskSnapshot[capacity];
    }

    public int Capacity => _items.Length;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public void Add(DiskSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (_count == _items.Length)
            {
                _items[_head] = snapshot;
                _head = (_head + 1) % _items.Length;
            }
            else
            {
                _items[(_head + _count) % _items.Length] = snapshot;
                _count++;
            }

            foreach (var (disk, processes) in snapshot.ProcessIoByDisk)
            {
                if (!_cumulative.TryGetValue(disk, out var perPid))
                {
                    perPid = new Dictionary<int, Accumulator>();
                    _cumulative[disk] = perPid;
                }

                foreach (var io in processes)
                {
                    Accumulate(perPid, io);
                }
            }
        }
    }

    /// <summary>Most recent <paramref name="seconds"/> snapshots, oldest first.</summary>
    public IReadOnlyList<DiskSnapshot> Window(int seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seconds);
        lock (_gate)
        {
            var take = Math.Min(seconds, _count);
            var result = new DiskSnapshot[take];
            var start = _count - take;
            for (var i = 0; i < take; i++)
            {
                result[i] = _items[(_head + start + i) % _items.Length];
            }

            return result;
        }
    }

    /// <summary>All buffered snapshots, oldest first.</summary>
    public IReadOnlyList<DiskSnapshot> All() => Window(Capacity);

    /// <summary>Per-process totals for one disk over the last <paramref name="windowSeconds"/> seconds, sorted by Read+Write descending.</summary>
    public IReadOnlyList<ProcessIo> AggregateProcesses(int diskNumber, int windowSeconds)
    {
        var perPid = new Dictionary<int, Accumulator>();
        foreach (var snapshot in Window(windowSeconds))
        {
            if (!snapshot.ProcessIoByDisk.TryGetValue(diskNumber, out var processes))
            {
                continue;
            }

            foreach (var io in processes)
            {
                Accumulate(perPid, io);
            }
        }

        return ToSortedList(perPid);
    }

    /// <summary>Per-process totals for one disk since the buffer was created; not bounded by the capacity.</summary>
    public IReadOnlyList<ProcessIo> AggregateCumulative(int diskNumber)
    {
        lock (_gate)
        {
            return _cumulative.TryGetValue(diskNumber, out var perPid)
                ? ToSortedList(perPid)
                : Array.Empty<ProcessIo>();
        }
    }

    /// <summary>
    /// Snapshot covering <paramref name="timestamp"/>: the latest snapshot whose timestamp is
    /// not after it, provided the gap is under one second. Used for click-to-freeze.
    /// </summary>
    public DiskSnapshot? At(DateTimeOffset timestamp)
    {
        lock (_gate)
        {
            DiskSnapshot? best = null;
            for (var i = 0; i < _count; i++)
            {
                var item = _items[(_head + i) % _items.Length];
                if (item.Timestamp <= timestamp && (best is null || item.Timestamp > best.Timestamp))
                {
                    best = item;
                }
            }

            return best is not null && timestamp - best.Timestamp < TimeSpan.FromSeconds(1)
                ? best
                : null;
        }
    }

    private static void Accumulate(Dictionary<int, Accumulator> perPid, ProcessIo io)
    {
        if (!perPid.TryGetValue(io.Pid, out var acc))
        {
            acc = new Accumulator(io.ProcessName);
            perPid[io.Pid] = acc;
        }

        acc.Read += io.ReadBytes;
        acc.Write += io.WriteBytes;
        acc.ProcessName = io.ProcessName;

        // TopFiles carry no byte counts; rank by position (index 0 = hottest) weighted by the
        // process's bytes in that second so merged lists reflect where the bytes went.
        var weight = Math.Max(1, io.ReadBytes + io.WriteBytes);
        for (var rank = 0; rank < io.TopFiles.Count; rank++)
        {
            var file = io.TopFiles[rank];
            var score = weight / (rank + 1);
            acc.Files[file] = acc.Files.GetValueOrDefault(file) + score;
        }

        // Cumulative totals live for the whole run; a busy process (System, browsers) touches
        // thousands of distinct files. Keep only the hottest entries so memory stays flat.
        if (acc.Files.Count > MaxTrackedFilesPerProcess)
        {
            foreach (var stale in acc.Files.OrderByDescending(f => f.Value).Skip(TopFilesPerProcess * 4).Select(f => f.Key).ToList())
            {
                acc.Files.Remove(stale);
            }
        }
    }

    private static List<ProcessIo> ToSortedList(Dictionary<int, Accumulator> perPid)
    {
        var list = new List<ProcessIo>(perPid.Count);
        foreach (var (pid, acc) in perPid)
        {
            var topFiles = acc.Files
                .OrderByDescending(f => f.Value)
                .Take(TopFilesPerProcess)
                .Select(f => f.Key)
                .ToList();
            list.Add(new ProcessIo(pid, acc.ProcessName, acc.Read, acc.Write, topFiles));
        }

        list.Sort((a, b) => b.TotalBytes.CompareTo(a.TotalBytes));
        return list;
    }

    private sealed class Accumulator
    {
        public Accumulator(string processName) => ProcessName = processName;

        public string ProcessName { get; set; }
        public long Read;
        public long Write;
        public Dictionary<string, long> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
