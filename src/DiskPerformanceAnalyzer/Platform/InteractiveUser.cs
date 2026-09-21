using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace DiskPerformanceAnalyzer.Platform;

/// <summary>
/// Detects "over-the-shoulder" elevation: a standard user who passed the UAC prompt with an
/// administrator's credentials runs this process as that administrator, so the profile folders,
/// the Start menu and the logon task would all land in the wrong account.
/// </summary>
public static class InteractiveUser
{
    /// <summary>
    /// Returns null when the process runs as the user who owns the desktop session; otherwise a
    /// sentence naming both accounts, for the settings flyout.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string? ProfileMismatch()
    {
        var session = SessionUserName();
        if (session is null)
        {
            return null; // cannot tell; do not block
        }

        using var identity = WindowsIdentity.GetCurrent();
        var process = identity.Name;
        if (string.Equals(process, session, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"You are signed in as {session}, but this window runs as {process}. Install, Start menu and autostart would land in {process}'s profile — start the app from an administrator account to set them up.";
    }

    [SupportedOSPlatform("windows")]
    private static string? SessionUserName()
    {
        var user = QuerySessionString(WTSUserName);
        var domain = QuerySessionString(WTSDomainName);
        if (string.IsNullOrEmpty(user))
        {
            return null;
        }

        return string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
    }

    private const int WTSCurrentSession = -1;
    private const int WTSUserName = 5;
    private const int WTSDomainName = 7;

    [SupportedOSPlatform("windows")]
    private static string? QuerySessionString(int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, WTSCurrentSession, infoClass, out var buffer, out _))
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr hServer, int sessionId, int infoClass, out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
