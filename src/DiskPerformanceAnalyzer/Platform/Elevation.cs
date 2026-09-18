using System.Runtime.Versioning;
using System.Security.Principal;

namespace DiskPerformanceAnalyzer.Platform;

/// <summary>
/// Reports whether the current process has the privileges the kernel trace needs:
/// Administrators on Windows (ETW kernel session), root on Linux (tracefs).
/// </summary>
public static class Elevation
{
    public static bool IsElevated() =>
        OperatingSystem.IsWindows() ? IsWindowsAdministrator() : Environment.IsPrivilegedProcess;

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
