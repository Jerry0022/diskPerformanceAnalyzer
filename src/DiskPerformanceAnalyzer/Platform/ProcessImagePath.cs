using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace DiskPerformanceAnalyzer.Platform;

/// <summary>
/// Resolves the full executable image path for a process id, including elevated processes
/// that <see cref="System.Diagnostics.Process.MainModule"/> cannot read from a non-elevated
/// caller.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProcessImagePath
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private static readonly ConcurrentDictionary<int, string?> Cache = new();

    /// <summary>
    /// Attempts to resolve the full image path of the process identified by <paramref name="pid"/>.
    /// Returns false for pid 0 (System Idle Process) and pid 4 (System), and for any failure.
    /// </summary>
    public static bool TryGet(int pid, out string? path)
    {
        if (pid == 0 || pid == 4)
        {
            path = null;
            return false;
        }

        if (Cache.TryGetValue(pid, out var cached))
        {
            path = cached;
            return cached != null;
        }

        var resolved = Resolve(pid);
        Cache[pid] = resolved;
        path = resolved;
        return resolved != null;
    }

    private static string? Resolve(int pid)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            if (!QueryFullProcessImageNameW(handle, 0, buffer, ref size))
            {
                return null;
            }

            return buffer.ToString(0, size);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
