using System.Runtime.Versioning;
using System.Security.Principal;

namespace DiskPerformanceAnalyzer.Platform;

/// <summary>
/// Reports whether the current process is running with administrator privileges.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Elevation
{
    /// <summary>
    /// Returns true if the current process token is a member of the built-in Administrators role.
    /// </summary>
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
