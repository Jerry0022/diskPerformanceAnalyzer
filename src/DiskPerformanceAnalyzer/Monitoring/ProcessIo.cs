namespace DiskPerformanceAnalyzer.Monitoring;

/// <summary>Bytes a process moved on one disk, plus the hottest file paths (max 3, by bytes).</summary>
public sealed record ProcessIo(
    int Pid,
    string ProcessName,
    long ReadBytes,
    long WriteBytes,
    IReadOnlyList<string> TopFiles)
{
    public long TotalBytes => ReadBytes + WriteBytes;
}
