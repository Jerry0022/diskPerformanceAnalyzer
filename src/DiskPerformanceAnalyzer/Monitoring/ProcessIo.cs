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
}
