namespace DiskPerformanceAnalyzer.Monitoring;

/// <summary>One-second sample of a single physical disk (Task-Manager-style numbers).</summary>
public sealed record DiskSample(
    int DiskNumber,
    string Name,
    IReadOnlyList<string> DriveLetters,
    double ActivePercent,
    double ReadBytesPerSec,
    double WriteBytesPerSec,
    double QueueLength,
    double ReadsPerSec = 0,
    double WritesPerSec = 0)
{
    public double OpsPerSec => ReadsPerSec + WritesPerSec;
}
