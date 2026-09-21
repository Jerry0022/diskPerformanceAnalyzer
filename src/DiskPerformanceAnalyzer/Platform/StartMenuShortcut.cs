using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace DiskPerformanceAnalyzer.Platform;

/// <summary>
/// The per-user Start menu entry (<c>%AppData%\Microsoft\Windows\Start Menu\Programs\*.lnk</c>),
/// written through the shell's IShellLink so it gets the exe's icon and a working directory.
/// </summary>
public static class StartMenuShortcut
{
    public static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppInstall.ProductName + ".lnk");

    /// <summary>False off Windows, where SpecialFolder.Programs is empty and the path would be relative.</summary>
    public static bool Exists() => OperatingSystem.IsWindows() && File.Exists(ShortcutPath);

    /// <summary>Writes the shortcut; safe to call from a worker thread (ShellLink is apartment-threaded, so it runs on an STA thread).</summary>
    [SupportedOSPlatform("windows")]
    public static void Create(string exePath)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            CreateOnCurrentThread(exePath);
            return;
        }

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                CreateOnCurrentThread(exePath);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException($"The Start menu shortcut could not be written: {failure.Message}", failure);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CreateOnCurrentThread(string exePath)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(exePath);
            link.SetWorkingDirectory(Path.GetDirectoryName(exePath) ?? string.Empty);
            link.SetDescription("Task-Manager-style disk activity monitor");
            link.SetIconLocation(exePath, 0);
            ((IPersistFile)link).Save(ShortcutPath, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    [SupportedOSPlatform("windows")]
    public static void Remove()
    {
        if (Exists())
        {
            File.Delete(ShortcutPath);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    [SupportedOSPlatform("windows")]
    private class ShellLink
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [SupportedOSPlatform("windows")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [SupportedOSPlatform("windows")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
