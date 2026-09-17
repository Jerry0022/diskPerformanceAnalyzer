namespace DiskPerformanceAnalyzer.Monitoring;

/// <summary>
/// Produces one <see cref="DiskSnapshot"/> per second on a background thread.
/// Consumers marshal to the UI thread themselves.
/// </summary>
public interface IDiskMonitor : IDisposable
{
    /// <summary>Raised once per second on a background thread. Never on the UI thread.</summary>
    event Action<DiskSnapshot>? SnapshotReady;

    /// <summary>Starts the ETW session and the sampling loop. Idempotent.</summary>
    void Start();
}
