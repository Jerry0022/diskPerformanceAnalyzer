using System.Diagnostics;
using System.Runtime.Versioning;

namespace DiskPerformanceAnalyzer.Platform;

/// <summary>
/// Copies the running program into the per-user programs folder
/// (<c>%LocalAppData%\Programs\Disk Performance Analyzer</c>) so that a Start menu shortcut and a
/// logon task have a stable path to point at. A single-file release exe is copied alone; a
/// framework-dependent build output (exe next to its .dll) is copied as a whole directory.
/// </summary>
public static class AppInstall
{
    public const string ProductName = "Disk Performance Analyzer";
    public const string ExeName = "DiskPerformanceAnalyzer.exe";
    private const string MainAssembly = "DiskPerformanceAnalyzer.dll";

    public static string InstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", ProductName);

    public static string InstalledExePath => Path.Combine(InstallDirectory, ExeName);

    public static string? CurrentExePath => Environment.ProcessPath;

    public static bool IsInstalled => File.Exists(InstalledExePath);

    /// <summary>True when this very process was started from the install folder.</summary>
    public static bool IsRunningInstalledCopy => PathsEqual(CurrentExePath, InstalledExePath);

    /// <summary>
    /// Copies the running program to <see cref="InstallDirectory"/>, replacing a previous install.
    /// Throws with a user-readable message when the copy fails.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void Install()
    {
        var source = CurrentExePath;
        if (string.IsNullOrEmpty(source) || !File.Exists(source))
        {
            throw new InvalidOperationException("The running executable could not be located.");
        }

        if (IsRunningInstalledCopy)
        {
            throw new InvalidOperationException("This copy is already the installed one.");
        }

        if (!IsAppExecutable(source))
        {
            // Started as "dotnet DiskPerformanceAnalyzer.dll" or through a wrapper: ProcessPath is the host, not us.
            throw new InvalidOperationException($"Install needs the app's own executable; this process is {Path.GetFileName(source)}.");
        }

        var sourceDir = Path.GetDirectoryName(source)!;
        if (PathsEqual(sourceDir, InstallDirectory) || IsInside(InstallDirectory, sourceDir))
        {
            throw new InvalidOperationException("The running copy sits inside the install folder.");
        }

        // Start from a clean folder so a differently shaped previous install (build output vs.
        // single file, older version) leaves nothing behind.
        if (Directory.Exists(InstallDirectory))
        {
            Directory.Delete(InstallDirectory, recursive: true);
        }

        Directory.CreateDirectory(InstallDirectory);

        if (IsSingleFileLayout(sourceDir))
        {
            // Release download: one self-contained exe (possibly renamed to include the version).
            File.Copy(source, InstalledExePath, overwrite: true);
            return;
        }

        // Build output: the apphost needs every file next to it.
        CopyDirectory(sourceDir, InstallDirectory);
        var copiedExe = Path.Combine(InstallDirectory, Path.GetFileName(source));
        if (!PathsEqual(copiedExe, InstalledExePath))
        {
            File.Copy(copiedExe, InstalledExePath, overwrite: true);
        }
    }

    /// <summary>
    /// Deletes the install folder. When this process runs from it the folder is removed after
    /// the process exits (Windows cannot delete a running exe); returns true in that case.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool Uninstall()
    {
        if (!Directory.Exists(InstallDirectory))
        {
            return false;
        }

        if (!IsRunningInstalledCopy)
        {
            Directory.Delete(InstallDirectory, recursive: true);
            return false;
        }

        var script = $"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue; " +
                     $"Remove-Item -LiteralPath '{InstallDirectory.Replace("'", "''")}' -Recurse -Force -ErrorAction SilentlyContinue";
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-WindowStyle");
        start.ArgumentList.Add("Hidden");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(script);
        using var process = Process.Start(start);
        if (process is null)
        {
            throw new InvalidOperationException($"Could not schedule the removal of {InstallDirectory}; delete the folder after closing the app.");
        }

        return true;
    }

    /// <summary>The release exe may carry a version suffix (DiskPerformanceAnalyzer-1.2.3-win-x64.exe); a host like dotnet.exe never does.</summary>
    internal static bool IsAppExecutable(string path) =>
        Path.GetFileName(path).StartsWith("DiskPerformanceAnalyzer", StringComparison.OrdinalIgnoreCase);

    internal static bool IsInside(string path, string ancestor)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var root = Path.GetFullPath(ancestor).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.Length > root.Length && full.StartsWith(root, comparison);
    }

    /// <summary>
    /// A published single-file bundle has no managed assembly next to the exe; a normal build
    /// output does. Decides whether the exe alone or the whole folder must be copied.
    /// </summary>
    internal static bool IsSingleFileLayout(string exeDirectory) =>
        !File.Exists(Path.Combine(exeDirectory, MainAssembly));

    internal static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), comparison);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
    }
}
