namespace DiskPerformanceAnalyzer.Monitoring;

/// <summary>Everything observed during one second: per-disk metrics and per-disk process attribution.</summary>
public sealed record DiskSnapshot(
    DateTimeOffset Timestamp,
    IReadOnlyList<DiskSample> Disks,
    IReadOnlyDictionary<int, IReadOnlyList<ProcessIo>> ProcessIoByDisk);
