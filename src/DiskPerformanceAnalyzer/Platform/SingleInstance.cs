using System.Runtime.Versioning;

namespace DiskPerformanceAnalyzer.Platform;

/// <summary>
/// One running copy per Windows session. A second start (Start menu click while the logon task
/// already started the app) would otherwise stop the first copy's ETW kernel session by name and
/// leave two windows, one of them dead; instead it asks the first copy to come to the front and
/// exits. Windows only: named events are not available on Unix, and Linux runs through pkexec.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SingleInstance : IDisposable
{
    private const string Name = "DiskPerformanceAnalyzer-3E1C7F7A";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly RegisteredWaitHandle _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle activate)
    {
        _mutex = mutex;
        _activate = activate;
        _wait = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => ActivateRequested?.Invoke(), null, -1, executeOnlyOnce: false);
    }

    /// <summary>Raised on a pool thread when another copy was started; bring the window to the front.</summary>
    public event Action? ActivateRequested;

    /// <summary>
    /// Claims the instance. Returns null when another copy already runs — that copy has been
    /// signalled to activate itself and the caller should exit.
    /// </summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, Name + "-mutex", out var createdNew);
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, Name + "-activate");
        if (createdNew)
        {
            return new SingleInstance(mutex, activate);
        }

        activate.Set();
        activate.Dispose();
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        _wait.Unregister(null);
        _activate.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Released from a thread that does not own it (shutdown path); the handle close frees it anyway.
        }

        _mutex.Dispose();
    }
}
