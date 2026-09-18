namespace DiskPerformanceAnalyzer.Monitoring;

/// <summary>
/// What a process did on one disk: bytes moved, number of I/O requests issued, and the hottest
/// file paths (max 3). Request counts matter as much as bytes — a process issuing thousands of
/// tiny reads saturates a disk long before it moves many megabytes.
/// </summary>
public sealed record ProcessIo(
    int Pid,
    string ProcessName,
    long ReadBytes,
    long WriteBytes,
    IReadOnlyList<string> TopFiles,
    long ReadOps = 0,
    long WriteOps = 0)
{
    public long TotalBytes => ReadBytes + WriteBytes;
    public long TotalOps => ReadOps + WriteOps;

    /// <summary>
    /// Per-file attribution behind <see cref="TopFiles"/>: every path the source could name,
    /// with its bytes and requests, plus at most one entry with an empty path for I/O whose
    /// file the kernel did not name (paging, NTFS metadata, cache flushes). Bounded by the
    /// source; the remainder up to the process totals is unattributed.
    /// </summary>
    public IReadOnlyList<FileIo> Files { get; init; } = Array.Empty<FileIo>();
}

/// <summary>One file's share of a process's I/O. An empty <see cref="Path"/> means "unnamed".</summary>
public readonly record struct FileIo(string Path, long Bytes, long Ops)
{
    public bool IsUnnamed => Path.Length == 0;
}
